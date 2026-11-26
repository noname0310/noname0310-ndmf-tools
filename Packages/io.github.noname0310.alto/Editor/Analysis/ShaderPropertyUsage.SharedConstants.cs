using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Analysis
{
    internal sealed partial class ShaderPropertyUsage
    {
        private readonly Dictionary<Shader, string> constantSourceIdentities = new();

        internal string ConstantSourceIdentity(Shader shader)
        {
            if (!ContentFactCache.Enabled) return null;
            if (constantSourceIdentities.TryGetValue(shader, out string identity)) return identity;
            var programs = GetPrograms(shader);
            if (!programs.NativeDependenciesComplete || !ShaderBuildSource.TryRead(shader, out string source, out string sourcePath)) return null;
            var parts = new List<string> { sourcePath, ContentFactCache.TextDigest(source), Application.unityVersion };
            foreach (var dependency in programs.NativeShaderDependencies.OrderBy(s => s.name, StringComparer.Ordinal))
            {
                parts.Add(dependency.name);
                if (ShaderBuildSource.TryGenerated(dependency, out string generated, out _)) parts.Add(ContentFactCache.TextDigest(generated));
                else
                {
                    string asset = AssetDatabase.GetAssetPath(dependency);
                    parts.Add(asset); parts.Add(string.IsNullOrEmpty(asset) ? null : AssetDatabase.GetAssetDependencyHash(asset).ToString());
                }
            }
            foreach (string path in programs.NativeDependencyPaths.OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!nativeFileDigests.TryGetValue(path, out string digest))
                {
                    digest = File.Exists(path) ? ShaderAnalysisCache.Digest(File.ReadAllBytes(path)) : null;
                    nativeFileDigests.Add(path, digest);
                }
                parts.Add(path); parts.Add(digest);
            }
            identity = ContentFactCache.Shared.Key("shader source rewrite dependencies", parts.ToArray());
            constantSourceIdentities.Add(shader, identity); return identity;
        }

        // Inspect include code even when generated programs keep the original
        // #include, notably Unity headers. This snapshot has already resolved
        // every available include candidate, including conditional branches.
        internal IEnumerable<string> ConstantSourceIncludes(Shader shader)
        {
            var programs = GetPrograms(shader);
            if (!programs.NativeDependenciesComplete)
                throw new InvalidOperationException("shader include ownership is incomplete");
            foreach (string path in programs.NativeDependencyPaths)
                if (files.TryGetValue(path, out string source)) yield return source;
        }

        // Native asset ownership does not require editable shader source. In
        // particular, ScriptedImporters and CreateShaderAsset keep real UsePass
        // and fallback references even when no .shader file exists on disk.
        // Query the live build graph; never persist Unity object identities.
        internal static HashSet<Shader> CollectNativeShaderDependencies(Shader shader)
        {
            using var cost = AnalysisCost.Measure("Shader discovery/native dependencies");
            if (shader == null) throw new ArgumentNullException(nameof(shader));
            var dependencies = EditorUtility.CollectDependencies(new UnityEngine.Object[] { shader });
            if (dependencies == null || !dependencies.Contains(shader))
                throw new InvalidOperationException("native shader dependency collection is incomplete: " + shader.name);
            return dependencies.OfType<Shader>().ToHashSet();
        }

        internal static HashSet<Shader> CollectSourceAndNativeShaderDependencies(Shader shader)
        {
            var visited = new HashSet<Shader>(); var pending = new Stack<Shader>(); pending.Push(shader);
            while (pending.Count != 0)
            {
                var current = pending.Pop(); if (!visited.Add(current)) continue;
                if (visited.Count > 4096) throw new InvalidOperationException("shader dependency graph proof budget exceeded");
                foreach (var dependency in CollectNativeShaderDependencies(current))
                    if (!visited.Contains(dependency)) pending.Push(dependency);
                if (!ShaderBuildSource.TryRead(current, out string source, out _)) continue;
                // A file may have changed before Unity imports it. Candidate
                // generation reads that file, so native references alone cannot
                // rule out a newly introduced source back edge.
                var layout = ShaderVertexIdRewriter.Layout.Parse(source, allowSharedPrograms: true, allowDependencies: true);
                foreach (string name in layout.Dependencies)
                {
                    var dependency = Shader.Find(name);
                    if (dependency == null) throw new InvalidOperationException("unresolved shader dependency: " + name);
                    if (!visited.Contains(dependency)) pending.Push(dependency);
                }
            }
            return visited;
        }

        internal LocalKeyword[] SharedConstantPassKeywords(Shader shader, int subshader, int index, ShaderType stage)
        {
            GetPrograms(shader);
            if (passSnapshots.TryGetValue((shader, subshader, index, false), out var imported))
            {
                var owner = ImportedNativePass(imported);
                if (owner != imported)
                    foreach (var pair in passSnapshots)
                        if (pair.Value == owner)
                            return ShaderUtil.GetPassKeywords(pair.Key.Item1,
                                new PassIdentifier((uint)pair.Key.Item2, (uint)pair.Key.Item3), stage, ShaderCompilerPlatform.D3D);
            }
            if (ShaderBuildSource.TryRead(shader, out string source, out _))
            {
                var layout = ShaderVertexIdRewriter.Layout.Parse(source, allowSharedPrograms: true, allowDependencies: true);
                if (layout.DependencyReferences.All(r => r.Suffix.Length == 0))
                    return ShaderUtil.GetPassKeywords(shader, new PassIdentifier((uint)subshader, (uint)index), stage, ShaderCompilerPlatform.D3D);
            }
            // An unaliased import or generated pass has no verified snippet
            // index. A shader-wide dependency union is safe, though less precise.
            return CollectNativeShaderDependencies(shader).SelectMany(s => s.keywordSpace.keywords)
                .GroupBy(k => k.name, StringComparer.Ordinal).Select(g => g.First()).ToArray();
        }

        // This snapshot revalidates exact bytes, missing include candidates,
        // imported dependencies and live Graphics/Quality/Player settings through
        // the same identity used by native analysis. No avatar objects are cached.
        private readonly Dictionary<(Shader, BuildTarget), string> nativeBuildIdentities = new();

        internal string NativeBuildIdentity(Shader shader, BuildTarget target)
        {
            if (!ContentFactCache.Enabled) return null;
            if (nativeBuildIdentities.TryGetValue((shader, target), out string previous)) return previous;
            string dependencies = NativeDependencyIdentity(shader), compiler = UnityShaderCompilerIdentity.D3D;
            string decoder = D3DShaderInputs.CompilerFingerprint;
            if (dependencies == null || compiler == null || decoder == null) return null;
            var programs = GetPrograms(shader);
            string imported = string.Join(";", programs.Passes.Select(p =>
                p.Identifier.SubshaderIndex + ":" + p.Identifier.PassIndex + ":" + ContentFactCache.TextDigest(PassSource(p.Pass))));
            string keywords = string.Join(";", shader.keywordSpace.keywords.OrderBy(k => k.name, StringComparer.Ordinal)
                .Select(k => k.name + ":" + k.type + ":" + k.isOverridable + ":" + k.isDynamic));
            string identity = ContentFactCache.Shared.Key("native shader build inputs", dependencies, compiler, decoder, imported,
                target.ToString(), "D3D", keywords, string.Join(";", PlayerSettings.GetGraphicsAPIs(target)));
            nativeBuildIdentities.Add((shader, target), identity);
            return identity;
        }
    }
}
