using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Materials
{
    // Source specialization is the normal compiler front-end transformation.
    // The optional strict path below additionally proves native bit-level output
    // correspondence when compiler floating-point folding is not permitted.
    internal static partial class SharedShaderConstantSource
    {
        internal const int MaximumVariants = 16384, MaximumPrograms = 262144;
        private const int MaximumRefinements = 8, MaximumNativeCompiles = MaximumPrograms * 2;
        private const string CacheKind = "runtime shared shader constants";
        private const string RetainedCacheKind = "runtime shared shader constants retained";
        private sealed class StableNativeLimitation : Exception
        { internal StableNativeLimitation(string reason) : base(reason) { } }
        // VRChat checks these exact names before its case-sensitive name-token
        // fallback. A unique generated name cannot retain that exact-name
        // contract, even when editable source happens to use a built-in name.
        // https://creators.vrchat.com/avatars/shader-fallback-system/ (2026-11-25)
        private static readonly HashSet<string> ExactSafetyFallbackNames = new(StringComparer.Ordinal)
        {
            "Standard", "Standard (Specular setup)", "Effects/Rim", "Effects/GlowAdditiveSimple",
            "Legacy Shaders/Bumped Diffuse", "Legacy Shaders/Bumped Specular", "Legacy Shaders/Decal",
            "Legacy Shaders/Diffuse", "Legacy Shaders/Diffuse Detail", "Legacy Shaders/Diffuse Fast",
            "Legacy Shaders/Lightmapped/Diffuse", "Legacy Shaders/Lightmapped/Specular", "Legacy Shaders/Lightmapped/VertexLit",
            "Legacy Shaders/Parallax Diffuse", "Legacy Shaders/Parallax Specular",
            "Legacy Shaders/Reflective/Bumped Diffuse", "Legacy Shaders/Reflective/Bumped Specular",
            "Legacy Shaders/Reflective/Bumped Unlit", "Legacy Shaders/Reflective/Bumped VertexLit",
            "Legacy Shaders/Reflective/Diffuse", "Legacy Shaders/Reflective/Parallax Diffuse",
            "Legacy Shaders/Reflective/Parallax Specular", "Legacy Shaders/Reflective/Specular", "Legacy Shaders/Reflective/VertexLit",
            "Legacy Shaders/Self-Illum/Bumped Diffuse", "Legacy Shaders/Self-Illum/Bumped Specular",
            "Legacy Shaders/Self-Illum/Diffuse", "Legacy Shaders/Self-Illum/Parallax Diffuse",
            "Legacy Shaders/Self-Illum/Parallax Specular", "Legacy Shaders/Self-Illum/Specular", "Legacy Shaders/Self-Illum/VertexLit",
            "Legacy Shaders/Specular", "Legacy Shaders/Transparent/Bumped Diffuse", "Legacy Shaders/Transparent/Bumped Specular",
            "Legacy Shaders/Transparent/Cutout/Bumped Diffuse", "Legacy Shaders/Transparent/Cutout/Bumped Specular",
            "Legacy Shaders/Transparent/Cutout/Diffuse", "Legacy Shaders/Transparent/Cutout/Soft Edge Unlit",
            "Legacy Shaders/Transparent/Cutout/Specular", "Legacy Shaders/Transparent/Cutout/VertexLit",
            "Legacy Shaders/Transparent/Diffuse", "Legacy Shaders/Transparent/Parallax Diffuse",
            "Legacy Shaders/Transparent/Parallax Specular", "Legacy Shaders/Transparent/Specular", "Legacy Shaders/Transparent/VertexLit",
            "Legacy Shaders/VertexLit", "MatCap/Vertex/Textured Lit", "Mobile/Bumped Diffuse", "Mobile/Bumped Specular",
            "Mobile/Bumped Specular (1 Directional Light)", "Mobile/Diffuse", "Mobile/Unlit (Supports Lightmap)",
            "Mobile/Particles/Additive", "Mobile/Particles/Alpha Blended", "Mobile/Particles/Multiply", "Mobile/Particles/VertexLit Blended",
            "Particles/~Additive-Multiply", "Particles/Additive", "Particles/Additive (Soft)", "Particles/Alpha Blended",
            "Particles/Alpha Blended Premultiply", "Particles/Anim Alpha Blended", "Particles/Multiply", "Particles/Multiply (Double)",
            "Particles/VertexLit Blended", "Sprites/Default", "Sprites/Diffuse", "Toon/Lit", "Toon/Lit (Double)",
            "Toon/Lit Cutout", "Toon/Lit Cutout (Double)", "Toon/Lit Outline", "UI/Default", "Unlit/FailShader", "VRChat/UI/Default"
        };
        internal sealed class Statistics
        {
            internal string EntryUseDetail;
            internal int CacheHits, RetainedCacheHits, PlannedPrograms, ValidatedPrograms, RetainedInputs, NativeCompiles, RuntimeArtifactCompiles, IdenticalRuntimePrograms;
            internal double LayoutMilliseconds, KeyMilliseconds, LookupMilliseconds, PreflightMilliseconds, SourceMilliseconds,
                ImportMilliseconds, ValidationMilliseconds, PreprocessMilliseconds, CompileMilliseconds, TotalMilliseconds;
            internal string Detail => $"cache hits {CacheHits}, retained cache hits {RetainedCacheHits}, programs planned {PlannedPrograms}, validated {ValidatedPrograms}; " +
                $"layout {LayoutMilliseconds:F2} ms, key {KeyMilliseconds:F2} ms, lookup {LookupMilliseconds:F2} ms, preflight {PreflightMilliseconds:F2} ms, " +
                $"source {SourceMilliseconds:F2} ms, import {ImportMilliseconds:F2} ms, validation {ValidationMilliseconds:F2} ms " +
                $"(preprocess {PreprocessMilliseconds:F2} ms, compile {CompileMilliseconds:F2} ms), native compiles {NativeCompiles} " +
                $"(runtime artifacts {RuntimeArtifactCompiles}, identical programs {IdenticalRuntimePrograms}), retained inputs {RetainedInputs}, total {TotalMilliseconds:F2} ms; entry use: {EntryUseDetail}";
        }

        private sealed class ValidationPlan
        {
            internal int[] PassCounts;
            internal string Keywords;
            internal readonly List<(int Subshader, int Pass, ShaderData.Pass Original, ShaderType Stage, string[][] Keywords)> Stages = new();
            internal int NativeCompiles;
            internal readonly BoundedAnalysisCache<(int Subshader, int Pass, ShaderType Stage, GraphicsTier Tier, string Keywords),
                (string Source, ShaderData.VariantCompileInfo Program)> Originals = new(256, 64 * 1024 * 1024);
            internal readonly BoundedAnalysisCache<(int Subshader, int Pass, ShaderType Stage, GraphicsTier Tier, string Keywords),
                ShaderData.VariantCompileInfo> OriginalRuntimeArtifacts = new(256, 16 * 1024 * 1024);
        }

        internal static bool SupportedTarget(BuildTarget target) =>
            Application.platform == RuntimePlatform.WindowsEditor &&
            (target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64) &&
            PlayerSettings.GetGraphicsAPIs(target).Length != 0 &&
            PlayerSettings.GetGraphicsAPIs(target).All(a => a == GraphicsDeviceType.Direct3D11 || a == GraphicsDeviceType.Direct3D12);

        internal static bool TryCreate(Shader original, IReadOnlyDictionary<string, float> constants,
            BuildTarget target, ShaderPropertyUsage sources, out Shader output, out string source, out int declarations, out string reason,
            Statistics statistics = null, IReadOnlyDictionary<string, float[]> uniformDomains = null,
            IReadOnlyDictionary<Shader, Shader> dependencyReplacements = null, bool allowCompilerConstantFolding = false)
        {
            if (allowCompilerConstantFolding) return TryCreateHighLevel(original, constants, target, sources,
                out output, out source, out declarations, out reason, statistics, dependencyReplacements);
            output = null; source = null; declarations = 0; reason = null;
            string retainedKey = null;
            var total = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!SupportedTarget(target)) throw new InvalidOperationException("runtime shader generation requires a verified desktop D3D target");
                if (ExactSafetyFallbackNames.Contains(original.name))
                    throw new InvalidOperationException("the shader has an exact-name VRChat Safety Fallback contract");
                if (string.IsNullOrEmpty(original.name) || original.name.IndexOfAny(new[] { '"', '\\', '\r', '\n' }) >= 0)
                    throw new InvalidOperationException("the shader name has no verified generated-name encoding");
                if (!ShaderBuildSource.TryRead(original, out string originalText, out string path))
                    throw new InvalidOperationException("editable ShaderLab source is unavailable");
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var sourceLayout = ShaderVertexIdRewriter.Layout.Parse(originalText,
                    allowSharedPrograms: true, allowDependencies: true);
                var capturedDependencies = CaptureDependencies(sourceLayout, dependencyReplacements);
                if (statistics != null) statistics.LayoutMilliseconds += timer.Elapsed.TotalMilliseconds;
                // A pure UsePass/Fallback wrapper has neither a declaration to
                // specialize nor a dependency name to relocate in this case.
                // Its eventual edit set is empty regardless of keyword variants;
                // avoid generating their identities and validation plan. Shared
                // program blocks and actual dependency rewrites take the full path.
                if (sourceLayout.Programs.Count == 0 && dependencyReplacements?.Count is not > 0)
                    throw new InvalidOperationException("no supported standalone uniform declaration");
                timer.Restart();
                string signature = string.Join(";", constants.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => p.Key + ":float:" + BitConverter.SingleToInt32Bits(p.Value))) + "|domains:" +
                    string.Join(";", (uniformDomains ?? new Dictionary<string, float[]>()).OrderBy(p => p.Key, StringComparer.Ordinal)
                        .Select(p => p.Key + ":[" + string.Join(",", p.Value.Select(BitConverter.SingleToInt32Bits).Distinct().OrderBy(v => v)) + "]"));
                string identity = sources.NativeBuildIdentity(original, target);
                var cache = ContentFactCache.Shared;
                string cacheKey = identity == null ? null : cache.Key(CacheKind, identity, signature,
                    "native-expression-correspondence-v5; exact-value-domains-v1; source-edit-refinement-v3; complete-runtime-artifact-identity-v1; dependency-relocation-v1; native-variant-union-v1; fallback-name-preservation-v1; all passes/stages/tiers/keywords; limits=" +
                    string.Join(";", (dependencyReplacements ?? new Dictionary<Shader, Shader>()).OrderBy(p => p.Key.name, StringComparer.Ordinal)
                        .Select(p => p.Key.name + "=" + p.Value.name)) + "|" +
                    MaximumVariants + "/" + MaximumPrograms + "/" + MaximumNativeCompiles + "/" + MaximumRefinements);
                if (statistics != null) statistics.KeyMilliseconds += timer.Elapsed.TotalMilliseconds;
                retainedKey = cacheKey == null ? null : cache.Key(RetainedCacheKind, cacheKey);
                timer.Restart();
                if (cache.TryRead(RetainedCacheKind, retainedKey, reader => reader.ReadString(), out string retainedReason))
                {
                    if (statistics != null) { statistics.RetainedCacheHits++; statistics.LookupMilliseconds += timer.Elapsed.TotalMilliseconds; }
                    reason = retainedReason; return false;
                }
                bool cached = cache.TryRead(CacheKind, cacheKey,
                    reader => (Source: reader.ReadString(), Declarations: reader.ReadInt32(), Programs: reader.ReadInt32()), out var previous);
                if (statistics != null) statistics.LookupMilliseconds += timer.Elapsed.TotalMilliseconds;
                if (cached && (previous.Declarations > 0 || dependencyReplacements?.Count > 0))
                {
                    source = previous.Source; declarations = previous.Declarations;
                    output = Import(original, source, statistics);
                    if (output == null || ShaderUtil.ShaderHasError(output)) throw new InvalidOperationException("cached generated shader import failed");
                    ShaderUtil.RegisterShader(output);
                    ShaderBuildSource.Register(output, source, path, dependencies: capturedDependencies);
                    if (statistics != null) { statistics.CacheHits++; statistics.PlannedPrograms = previous.Programs; }
                    return true;
                }
                // Count the complete original variant universe before generating
                // or importing a candidate. Reaching a budget must not first spend
                // that budget on native compilations which can never be accepted.
                timer.Restart(); ValidationPlan validation;
                try { validation = Preflight(original, sources, statistics); }
                finally { if (statistics != null) statistics.PreflightMilliseconds += timer.Elapsed.TotalMilliseconds; }
                var pending = new Queue<Dictionary<string, float>>();
                pending.Enqueue(constants.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
                var visited = new HashSet<string>(StringComparer.Ordinal);
                static string Selection(IEnumerable<string> names) => string.Join(";", names.OrderBy(n => n, StringComparer.Ordinal));
                visited.Add(Selection(constants.Keys));
                int supportedInputs = -1;
                string refinementIssue = null;
                for (int attempt = 0; pending.Count != 0 && attempt <= MaximumRefinements; attempt++)
                {
                    var retained = pending.Dequeue();
                    timer.Restart(); declarations = 0;
                    var selected = new HashSet<string>(StringComparer.Ordinal);
                    try
                    {
                        string text = originalText;
                        var layout = ShaderVertexIdRewriter.Layout.Parse(text, allowSharedPrograms: true, allowDependencies: true);
                        foreach (string dependency in layout.Dependencies)
                        {
                            var shader = Shader.Find(dependency);
                            if (shader == null) throw new InvalidOperationException("unresolved shader dependency: " + dependency);
                            var dependencies = ShaderPropertyUsage.CollectSourceAndNativeShaderDependencies(shader);
                            if (shader == original || shader.name == original.name || dependencies.Any(s => s == original || s.name == original.name))
                                throw new InvalidOperationException("a shader dependency would retain the original alongside its replacement");
                        }
                        foreach (var block in layout.Programs.OrderByDescending(p => p.Offset))
                        {
                            string expanded = ShaderSourceExpansion.Expand(block.Body, Path.GetDirectoryName(path),
                                new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, retainUnresolvedIncludes: true);
                            var plan = DeclarationPlan(sources.Artifacts, expanded);
                            string specialized = plan.Apply(retained, out int count, floatingPointOnly: true, specializedNames: selected); declarations += count;
                            text = text.Remove(block.Offset, block.Length).Insert(block.Offset,
                                block.Prefix + "\n" + specialized + "\n" + block.Suffix);
                        }
                        int relocated = 0;
                        if (dependencyReplacements?.Count > 0)
                        {
                            var expandedLayout = ShaderVertexIdRewriter.Layout.Parse(text, allowSharedPrograms: true, allowDependencies: true);
                            foreach (var reference in expandedLayout.DependencyReferences.OrderByDescending(r => r.Token.Offset))
                            {
                                var dependency = Shader.Find(reference.Shader);
                                if (dependency == null || !dependencyReplacements.TryGetValue(dependency, out var replacement)) continue;
                                text = text.Remove(reference.Token.Offset, reference.Token.Length).Insert(reference.Token.Offset,
                                    "\"" + replacement.name + reference.Suffix + "\"");
                                relocated++;
                            }
                        }
                        if (declarations == 0 && relocated == 0)
                            throw new InvalidOperationException(refinementIssue ?? "no supported standalone uniform declaration");
                        string key = ContentHash.Text(text + "\n" + target + "\n" + Application.unityVersion + "\n" + signature);
                        // With no VRCFallback tag the client matches case-sensitive
                        // substrings in the shader name. Preserve the entire original
                        // name; this prefix and hexadecimal digest add no such tokens.
                        source = text.Remove(layout.Name.Offset, layout.Name.Length).Insert(layout.Name.Offset,
                            "\"Hidden/ALTO/SharedConstants/" + key.Substring(0, 24) + "/" + original.name + "\"");
                    }
                    finally { if (statistics != null) statistics.SourceMilliseconds += timer.Elapsed.TotalMilliseconds; }
                    if (supportedInputs < 0) supportedInputs = selected.Count;
                    if (statistics != null) statistics.RetainedInputs = supportedInputs - selected.Count;
                    if (attempt == 0 && dependencyReplacements?.Count is not > 0 && selected.Count > 0 &&
                        EntryInputsAbsent(original, validation, sources, target, selected, statistics))
                        throw new StableNativeLimitation("shared scalar inputs are unreachable from every shader entry; no runtime uniform can be removed");
                    output = Import(original, source, statistics);
                    if (output == null || ShaderUtil.ShaderHasError(output)) throw new InvalidOperationException("generated shader import failed");
                    timer.Restart(); int programsValidated; HashSet<string> unproved;
                    try { programsValidated = Validate(validation, output, retained, constants, uniformDomains,
                        target, sources, statistics, Selection(selected), out unproved, out refinementIssue); }
                    finally { if (statistics != null) statistics.ValidationMilliseconds += timer.Elapsed.TotalMilliseconds; }
                    if (unproved.Count == 0)
                    {
                        // CreateShaderAsset does not register transient shaders
                        // for UsePass/Fallback name resolution. Only publish a
                        // proved replacement to the native shader registry.
                        ShaderUtil.RegisterShader(output);
                        ShaderBuildSource.Register(output, source, path, dependencies: capturedDependencies);
                        string cachedSource = source; int cachedDeclarations = declarations;
                        cache.Store(CacheKind, cacheKey, writer => { writer.Write(cachedSource); writer.Write(cachedDeclarations); writer.Write(programsValidated); });
                        return true;
                    }
                    // Try retaining one implicated edit, re-proving the entire
                    // shader for each candidate. Neither safety nor compiler
                    // vectorization is monotone as source edits are removed.
                    var removals = unproved.Where(selected.Contains).OrderByDescending(n => n, StringComparer.Ordinal).ToArray();
                    // A different edit can change vectorization of an input which
                    // already remains a uniform. Native provenance identifies the
                    // changed value, not which source edit caused that compiler
                    // decision. Try sibling subsets before removing more edits
                    // from one subset, so a failed guess cannot discard a useful
                    // combination. Unsupported seeds cannot consume attempts.
                    if (removals.Length == 0) removals = selected.OrderBy(n => n, StringComparer.Ordinal).ToArray();
                    foreach (string remove in removals)
                    {
                        if (selected.Count <= 1 || pending.Count >= MaximumRefinements - attempt) break;
                        var next = retained.Where(p => p.Key != remove).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                        if (visited.Add(Selection(next.Keys))) pending.Enqueue(next);
                    }
                    UnityEngine.Object.DestroyImmediate(output); output = null;
                }
                throw new InvalidOperationException(refinementIssue ?? "native constant refinement limit exceeded");
            }
            catch (Exception exception)
            {
                if (output != null) UnityEngine.Object.DestroyImmediate(output);
                // Only explicit, deterministic proof limitations may be cached.
                // Import/compilation/IO failures and incomplete reflection still
                // retry. This record retains the original; it never certifies an
                // absent input or a valid shader. The key includes source and
                // dependency bytes, exact values/domains, compiler environment,
                // analyzer version and all proof limits.
                if (exception is StableNativeLimitation)
                    ContentFactCache.Shared.Store(RetainedCacheKind, retainedKey, writer => writer.Write(exception.Message));
                output = null; reason = exception.Message; return false;
            }
            finally { if (statistics != null) statistics.TotalMilliseconds += total.Elapsed.TotalMilliseconds; }
        }

        private static Shader[] CaptureDependencies(ShaderVertexIdRewriter.Layout layout,
            IReadOnlyDictionary<Shader, Shader> replacements)
        {
            var result = new HashSet<Shader>();
            foreach (string name in layout.Dependencies)
            {
                var shader = Shader.Find(name);
                if (shader == null) throw new InvalidOperationException("unresolved shader dependency: " + name);
                if (replacements != null && replacements.TryGetValue(shader, out var replacement)) shader = replacement;
                result.Add(shader);
            }
            return result.ToArray();
        }

        private static Shader Import(Shader original, string source, Statistics statistics)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var shader = ShaderUtil.CreateShaderAsset(source, true);
                if (shader != null) shader.maximumLOD = original.maximumLOD;
                return shader;
            }
            finally { if (statistics != null) statistics.ImportMilliseconds += timer.Elapsed.TotalMilliseconds; }
        }

        private static ShaderConstantInputs.Plan DeclarationPlan(ShaderAnalysisArtifacts artifacts, string source)
        {
            if (!artifacts.Declarations.TryGetValue(source, out var plan))
            {
                plan = ShaderConstantInputs.PrepareCached(source);
                artifacts.Declarations.Add(source, plan, (source.Length + plan.Source.Length) * 2);
            }
            return plan;
        }

        private static string Tokens(string source) => string.Join(" ",
            PpLexer.Lex(ShaderConstantInputs.WithoutDiagnosticPragmas(ShaderConstantInputs.Normalize(source)))
                .Where(t => t.Kind != PpTokenKind.Newline).Select(t => t.Text));

        private static string KeywordContract(Shader shader) => string.Join(";", shader.keywordSpace.keywords
            .OrderBy(k => k.name, StringComparer.Ordinal).Select(k => k.name + ":" + k.type + ":" + k.isOverridable + ":" + k.isDynamic));

        private static ValidationPlan Preflight(Shader original, ShaderPropertyUsage sources, Statistics statistics)
        {
            var before = ShaderUtil.GetShaderData(original);
            if (before.SerializedSubshaderCount > before.SubshaderCount)
                throw new InvalidOperationException("original subshader layout is incomplete");
            var result = new ValidationPlan { PassCounts = new int[before.SerializedSubshaderCount], Keywords = KeywordContract(original) };
            NativeShaderVariants importedVariants = null;
            // GetPassKeywords indexes snippets owned by the shader, not every
            // imported UsePass slot. Querying an imported slot can log a native
            // "Snippet not found" error. Keep the complete dependency keyword
            // universe for such shaders rather than guessing snippet indices.
            int programs = 0;
            for (int s = 0; s < before.SerializedSubshaderCount; s++)
            {
                var a = before.GetSerializedSubshader(s); result.PassCounts[s] = a.PassCount;
                for (int p = 0; p < a.PassCount; p++)
                {
                    var first = a.GetPass(p);
                    foreach (var stage in ShaderMeshInputs.Stages)
                    {
                        if (!first.HasShaderStage(stage)) { result.Stages.Add((s, p, first, stage, null)); continue; }
                        // No per-material keyword assumptions: the replacement is
                        // shared by every renderer and every runtime keyword state.
                        var keywords = sources.SharedConstantPassKeywords(original, s, p, stage);
                        var variants = ShaderKeywordPlan.Create(first.SourceCode, stage, keywords,
                            null, false, MaximumVariants, backend: ShaderCompilerPlatform.D3D);
                        string[][] alternatives = variants.Issue == null ? variants.Variants :
                            (importedVariants ??= NativeShaderVariants.Read(original, MaximumVariants)).Project(keywords, MaximumVariants);
                        programs += alternatives.Length * ShaderMeshInputs.Tiers.Length;
                        if (statistics != null) statistics.PlannedPrograms = programs;
                        if (programs > MaximumPrograms) throw new InvalidOperationException("shared shader variant validation budget exceeded before native compilation");
                        result.Stages.Add((s, p, first, stage, alternatives));
                    }
                }
            }
            if (programs == 0) throw new InvalidOperationException("no inspectable native programs");
            return result;
        }

        private static int Validate(ValidationPlan plan, Shader output, IReadOnlyDictionary<string, float> sourceConstants,
            IReadOnlyDictionary<string, float> knownConstants, IReadOnlyDictionary<string, float[]> uniformDomains,
            BuildTarget target, ShaderPropertyUsage sources, Statistics statistics, string selectedConstants,
            out HashSet<string> unprovedConstants, out string issue)
        {
            unprovedConstants = new HashSet<string>(StringComparer.Ordinal); issue = null;
            var after = ShaderUtil.GetShaderData(output);
            if (after.SerializedSubshaderCount > after.SubshaderCount || plan.PassCounts.Length != after.SerializedSubshaderCount)
                throw new InvalidOperationException("generated subshader layout is incomplete or changed");
            if (plan.Keywords != KeywordContract(output)) throw new InvalidOperationException("generated keyword contract changed: original [" +
                plan.Keywords + "], generated [" + KeywordContract(output) + "]");
            for (int s = 0; s < plan.PassCounts.Length; s++)
                if (plan.PassCounts[s] != after.GetSerializedSubshader(s).PassCount) throw new InvalidOperationException("generated pass layout changed");
            int programs = 0, specialized = 0;
            foreach (var unit in plan.Stages)
                if ((unit.Keywords != null) != after.GetSerializedSubshader(unit.Subshader).GetPass(unit.Pass).HasShaderStage(unit.Stage))
                    throw new InvalidOperationException("generated shader stages changed");
            // Visit one variant of every stage before expanding a large stage's
            // keyword universe. An unsupported fragment program should reject a
            // candidate before thousands of unchanged vertex programs are proved.
            // Both sweeps together visit exactly the original Cartesian product;
            // no successful candidate skips a pass, stage, tier or keyword choice.
            for (int sweep = 0; sweep < 2; sweep++)
            foreach (var unit in plan.Stages)
            {
                var second = after.GetSerializedSubshader(unit.Subshader).GetPass(unit.Pass);
                if (unit.Keywords == null) continue;
                for (int tierIndex = 0; tierIndex < ShaderMeshInputs.Tiers.Length; tierIndex++)
                for (int keywordIndex = 0; keywordIndex < unit.Keywords.Length; keywordIndex++)
                {
                    if ((tierIndex == 0 && keywordIndex == 0) != (sweep == 0)) continue;
                    var tier = ShaderMeshInputs.Tiers[tierIndex]; var keywords = unit.Keywords[keywordIndex];
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var originalKey = (unit.Subshader, unit.Pass, unit.Stage, tier, string.Join(";", keywords));
                    if (!plan.Originals.TryGetValue(originalKey, out var original))
                    {
                        var originalProgram = unit.Original.PreprocessVariant(unit.Stage, keywords, ShaderCompilerPlatform.D3D, target, tier, false);
                        if (statistics != null) statistics.PreprocessMilliseconds += timer.Elapsed.TotalMilliseconds;
                        if (!originalProgram.Success || string.IsNullOrWhiteSpace(originalProgram.PreprocessedCode))
                            throw new InvalidOperationException("an original shader variant has no verified native preprocessing output");
                        var compilation = Compile(plan, unit.Original, unit.Stage, keywords, target, tier, true, statistics);
                        original = (originalProgram.PreprocessedCode, compilation);
                        plan.Originals.Add(originalKey, original, original.Source.Length * 2 + (compilation.ShaderData?.Length ?? 0));
                    }
                    timer.Restart();
                    var generatedProgram = second.PreprocessVariant(unit.Stage, keywords, ShaderCompilerPlatform.D3D, target, tier, false);
                    if (statistics != null) statistics.PreprocessMilliseconds += timer.Elapsed.TotalMilliseconds;
                    if (!generatedProgram.Success || string.IsNullOrWhiteSpace(generatedProgram.PreprocessedCode))
                        throw new InvalidOperationException("a shader variant has no verified native preprocessing output");
                    var declarations = DeclarationPlan(sources.Artifacts, original.Source);
                    var scalarNames = declarations.Declarations.Where(d => d.Type == "float" || d.Type == "half")
                        .Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
                    // Refinement changes only the source edits. A retained
                    // runtime uniform still has its originally proved value.
                    var scalarConstants = knownConstants.Where(p => scalarNames.Contains(p.Key))
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                    var scalarDomains = uniformDomains?.Where(p => scalarNames.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                    string actualTokens = Tokens(generatedProgram.PreprocessedCode);
                    bool sameSource = actualTokens == Tokens(original.Source);
                    // Imported UsePass programs and unchanged fallback assets keep
                    // their runtime inputs. Every changed native program must still
                    // be exactly the restricted declaration rewrite of its original.
                    if (!sameSource)
                    {
                        if (!declarations.MatchesSpecialization(generatedProgram.PreprocessedCode, knownConstants, out int count))
                            throw new InvalidOperationException("generated source is neither unchanged nor the verified standalone-declaration rewrite");
                        specialized += count;
                    }
                    var compiled = Compile(plan, second, unit.Stage, keywords, target, tier, true, statistics);
                    if (!D3DConstantEquivalence.TryEquivalent(original.Program, compiled, scalarConstants, unit.Stage,
                        out var unproved, out string nativeIssue, sameSource, scalarDomains))
                    {
                        string witness = $"subshader {unit.Subshader}, pass {unit.Pass}, stage {unit.Stage}, tier {tier}, " +
                            $"keywords [{string.Join(";", keywords)}], selected [{selectedConstants}]";
                        if (unproved == null || unproved.Count == 0)
                        {
                            if (!plan.OriginalRuntimeArtifacts.TryGetValue(originalKey, out var originalRuntime))
                            {
                                originalRuntime = Compile(plan, unit.Original, unit.Stage, keywords, target, tier, false, statistics);
                                plan.OriginalRuntimeArtifacts.Add(originalKey, originalRuntime, originalRuntime.ShaderData?.Length ?? 0);
                            }
                            var candidateRuntime = Compile(plan, second, unit.Stage, keywords, target, tier, false, statistics);
                            if (!IdenticalRuntimeArtifacts(originalRuntime, candidateRuntime))
                            {
                                string message = "native constant correspondence is unavailable and complete runtime artifacts differ: " +
                                    nativeIssue + " (" + witness + ")";
                                // These checks operate only on successfully
                                // compiled bytecode and metadata. Their answer
                                // cannot change without one of the keyed inputs.
                                if (nativeIssue == "native buffer slots have no unique metadata mapping" ||
                                    nativeIssue == "native integer or mixed-type source modifier is unsupported")
                                    throw new StableNativeLimitation(message);
                                throw new InvalidOperationException(message);
                            }
                            if (statistics != null) statistics.IdenticalRuntimePrograms++;
                        }
                        else
                        {
                            unprovedConstants.UnionWith(unproved); issue ??= "native constant outputs differ or remain unproved: " + nativeIssue +
                                " (" + witness + "; implicated [" + string.Join(";", unproved.OrderBy(n => n, StringComparer.Ordinal)) + "])";
                        }
                    }
                    programs++; if (statistics != null) statistics.ValidatedPrograms++;
                    // This candidate will be discarded after retaining an
                    // implicated input. Do not spend the remaining compile budget
                    // proving its other programs. Refinement restarts this entire
                    // traversal; only a complete successful traversal is cached.
                    if (unprovedConstants.Count != 0) return programs;
                }
            }
            if (programs == 0 || specialized == 0) throw new InvalidOperationException("no native program contains a verified shared constant");
            return programs;
        }

        private static ShaderData.VariantCompileInfo Compile(ValidationPlan plan, ShaderData.Pass pass, ShaderType stage,
            string[] keywords, BuildTarget target, GraphicsTier tier, bool forExternalTool, Statistics statistics)
        {
            if (plan.NativeCompiles >= MaximumNativeCompiles)
                throw new InvalidOperationException("native constant correspondence compilation budget exceeded");
            plan.NativeCompiles++;
            if (statistics != null)
            {
                statistics.NativeCompiles++;
                if (!forExternalTool) statistics.RuntimeArtifactCompiles++;
            }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = pass.CompileVariant(stage, keywords, ShaderCompilerPlatform.D3D, target, tier, forExternalTool);
                if (!result.Success) throw new InvalidOperationException("a shader variant failed native compilation");
                return result;
            }
            finally { if (statistics != null) statistics.CompileMilliseconds += timer.Elapsed.TotalMilliseconds; }
        }

        // Both values must come from forExternalTool:false at the same stage,
        // backend, target, tier and keywords. Unity's complete runtime artifact
        // retains its opaque prefix omitted from external-tool DXBC. Compare all
        // bytes, including its opaque prefix, and every public layout descriptor;
        // no inferred constant-buffer slot mapping participates in this proof.
        internal static bool IdenticalRuntimeArtifacts(ShaderData.VariantCompileInfo original, ShaderData.VariantCompileInfo candidate) =>
            D3DConstantEquivalence.IdenticalCompileArtifacts(original, candidate);
    }
}
