using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Materials
{
    internal static partial class SharedShaderConstantSource
    {
        // Material invariance is proved by the caller over every renderer and
        // animation use. This path edits declarations before Unity compiles them;
        // native register allocation and DCE are compiler outputs, not premises.
        private static bool TryCreateHighLevel(Shader original, IReadOnlyDictionary<string, float> constants,
            BuildTarget target, ShaderPropertyUsage sources, out Shader output, out string source,
            out int declarations, out string reason, Statistics statistics,
            IReadOnlyDictionary<Shader, Shader> dependencyReplacements)
        {
            output = null; source = null; declarations = 0; reason = null;
            var total = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (dependencyReplacements?.Count is not > 0 && !ShaderBuildSource.CanSpecialize(original, constants.Keys))
                    throw new InvalidOperationException("the generated source has no remaining candidate declaration");
                if (!SupportedTarget(target)) throw new InvalidOperationException("runtime shader generation requires a supported desktop target");
                if (ExactSafetyFallbackNames.Contains(original.name))
                    throw new InvalidOperationException("the shader has an exact-name VRChat Safety Fallback contract");
                if (string.IsNullOrEmpty(original.name) || original.name.IndexOfAny(new[] { '"', '\\', '\r', '\n' }) >= 0)
                    throw new InvalidOperationException("the shader name has no verified generated-name encoding");
                if (!ShaderBuildSource.TryRead(original, out string text, out string path))
                    throw new InvalidOperationException("editable ShaderLab source is unavailable");
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var layout = ShaderBuildSource.Layout(original, text);
                var capturedDependencies = CaptureDependencies(layout, dependencyReplacements);
                if (statistics != null) statistics.LayoutMilliseconds += timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                const string cacheKind = "source shader constants";
                var cache = ContentFactCache.Shared;
                string identity = sources.ConstantSourceIdentity(original);
                string key = identity == null ? null : cache.Key(cacheKind, identity,
                    string.Join(";", constants.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + BitConverter.SingleToInt32Bits(p.Value))),
                    string.Join(";", (dependencyReplacements ?? new Dictionary<Shader, Shader>()).OrderBy(p => p.Key.name, StringComparer.Ordinal)
                        .Select(p => p.Key.name + "=" + p.Value.name)), target.ToString(), "source-initializers-v1");
                if (statistics != null) statistics.KeyMilliseconds += timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                if (cache.TryRead(cacheKind, key, reader =>
                {
                    string body = reader.ReadString(); int count = reader.ReadInt32();
                    var names = new string[ContentFactCache.Count(reader)];
                    for (int i = 0; i < names.Length; i++) names[i] = reader.ReadString();
                    return (Source: body, Declarations: count, Remaining: names);
                }, out var cached))
                {
                    source = cached.Source; declarations = cached.Declarations;
                    if (statistics != null) { statistics.CacheHits++; statistics.LookupMilliseconds += timer.Elapsed.TotalMilliseconds; }
                    output = Import(original, source, statistics);
                    if (output == null || ShaderUtil.ShaderHasError(output) || KeywordContract(original) != KeywordContract(output))
                        throw new InvalidOperationException("cached shader source failed import validation");
                    ShaderBuildSource.Register(output, source, path, cached.Remaining, capturedDependencies);
                    ShaderUtil.RegisterShader(output);
                    if (statistics != null) statistics.EntryUseDetail = "cached source-level constant folding; native DCE remains a downstream analysis";
                    return true;
                }
                if (statistics != null) statistics.LookupMilliseconds += timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                foreach (string dependency in layout.Dependencies)
                {
                    var shader = Shader.Find(dependency);
                    if (shader == null) throw new InvalidOperationException("unresolved shader dependency: " + dependency);
                    var closure = ShaderPropertyUsage.CollectSourceAndNativeShaderDependencies(shader);
                    if (shader == original || shader.name == original.name || closure.Any(s => s == original || s.name == original.name))
                        throw new InvalidOperationException("a shader dependency would retain the original alongside its replacement");
                }
                var expanded = new List<(int Offset, int Length, string Prefix, string Body, string Suffix)>();
                var readOnly = new HashSet<string>(constants.Keys, StringComparer.Ordinal);
                foreach (var block in layout.Programs)
                {
                    string body = ShaderSourceExpansion.Expand(block.Body, Path.GetDirectoryName(path),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, retainUnresolvedIncludes: true);
                    expanded.Add((block.Offset, block.Length, block.Prefix, body, block.Suffix));
                }
                // A declaration and its writes may be in separate CGINCLUDE /
                // program blocks. Use the intersection for the complete shader.
                // Includes are already captured once by source discovery. Do not
                // scan the same inlined library again for every shader pass.
                string inspected = string.Join("\n", sources.ConstantSourceIncludes(original).Append(text));
                var declared = new HashSet<string>(constants.Keys, StringComparer.Ordinal);
                ShaderSourceConstants.RestrictDeclarationNames(inspected, declared);
                ShaderSourceConstants.RestrictToReadOnly(inspected, readOnly);
                var selected = constants.Where(p => declared.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                var remaining = new HashSet<string>(StringComparer.Ordinal);
                foreach (var block in expanded.OrderByDescending(b => b.Offset))
                {
                    // A helper or token-pasting macro may write a uniform inside
                    // one shader invocation. An initialized static preserves
                    // that possibility; const is added only with a read-only
                    // certificate. Both expose the known initial value to DCE.
                    var plan = DeclarationPlan(sources.Artifacts, block.Body);
                    string body = plan.Apply(selected, out int count, floatingPointOnly: true,
                        readOnlyNames: readOnly, remainingNames: remaining);
                    declarations += count;
                    text = text.Remove(block.Offset, block.Length).Insert(block.Offset, block.Prefix + "\n" + body + "\n" + block.Suffix);
                }
                ShaderSourceConstants.RestrictDeclarationNames(inspected, remaining);
                int relocated = 0;
                if (dependencyReplacements?.Count > 0)
                {
                    var rewritten = ShaderVertexIdRewriter.Layout.Parse(text, allowSharedPrograms: true, allowDependencies: true);
                    foreach (var reference in rewritten.DependencyReferences.OrderByDescending(r => r.Token.Offset))
                    {
                        var dependency = Shader.Find(reference.Shader);
                        if (dependency == null || !dependencyReplacements.TryGetValue(dependency, out var replacement)) continue;
                        text = text.Remove(reference.Token.Offset, reference.Token.Length).Insert(reference.Token.Offset,
                            "\"" + replacement.name + reference.Suffix + "\"");
                        relocated++;
                    }
                }
                if (declarations == 0 && relocated == 0) throw new InvalidOperationException("no supported read-only standalone uniform declaration");
                string nameHash = ContentHash.Text(text + "\n" + target + "\n" + Application.unityVersion);
                source = text.Remove(layout.Name.Offset, layout.Name.Length).Insert(layout.Name.Offset,
                    "\"Hidden/ALTO/SharedConstants/" + nameHash.Substring(0, 24) + "/" + original.name + "\"");
                if (statistics != null) statistics.SourceMilliseconds += timer.Elapsed.TotalMilliseconds;
                output = Import(original, source, statistics);
                if (output == null || ShaderUtil.ShaderHasError(output))
                    throw new InvalidOperationException("generated shader import failed");
                if (KeywordContract(original) != KeywordContract(output))
                    throw new InvalidOperationException("generated keyword contract changed");
                ShaderBuildSource.Register(output, source, path, remaining, capturedDependencies);
                ShaderUtil.RegisterShader(output);
                string savedSource = source; int savedCount = declarations;
                cache.Store(cacheKind, key, writer =>
                {
                    writer.Write(savedSource); writer.Write(savedCount); writer.Write(remaining.Count);
                    foreach (string name in remaining.OrderBy(n => n, StringComparer.Ordinal)) writer.Write(name);
                });
                if (statistics != null) statistics.EntryUseDetail = "source-level constant folding; native DCE remains a downstream analysis";
                return true;
            }
            catch (Exception exception)
            {
                if (output != null) UnityEngine.Object.DestroyImmediate(output);
                output = null; reason = exception.Message; return false;
            }
            finally { if (statistics != null) statistics.TotalMilliseconds += total.Elapsed.TotalMilliseconds; }
        }
    }
}
