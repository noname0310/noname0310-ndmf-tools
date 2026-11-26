using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Noname.AvatarTools.Analysis
{
    // A bounded absence proof. Unity owns HLSL preprocessing. A native compiler
    // can refine declarations into actual reads, using proven constant scalar
    // material inputs and a complete set of reachable keyword combinations.
    // Snapshot proofs stay local; exact compiler programs can reuse durable facts.
    internal sealed partial class ShaderMeshInputs
    {
        internal const int MaximumVariantsPerUnit = 256;
        private const string IndexIssue = ShaderProgramInspection.InputIssue;
        internal static readonly ShaderType[] Stages = { ShaderType.Vertex, ShaderType.Fragment,
            ShaderType.Geometry, ShaderType.Hull, ShaderType.Domain };
        internal static readonly GraphicsTier[] Tiers = { GraphicsTier.Tier1, GraphicsTier.Tier2, GraphicsTier.Tier3 };
        private readonly ShaderPropertyUsage sources;
        private readonly ShaderAnalysisArtifacts artifacts;
        private readonly Dictionary<(ShaderPropertyUsage.Proof, string), string> sourceIssues = new();
        private readonly Dictionary<(Shader, BuildTarget, string), string> cache = new();
        private readonly Dictionary<(Shader, BuildTarget, string, string), string> materialCache = new();
        private readonly Dictionary<(Shader, BuildTarget, string, string), List<Dictionary<string, int>>> inputCertificates = new();
        private readonly Dictionary<(Shader, ShaderData.Pass, ShaderType, ShaderCompilerPlatform, GraphicsTier, BuildTarget, string, string),
            List<Dictionary<string, int>>> unitCertificates = new();
        private readonly Dictionary<(ShaderTextKey Program, string Entry, ShaderType Stage, bool Native), (D3DShaderInputs.Input[] Inputs, string Error)> compiled = new();
        private readonly Dictionary<(ShaderData.Pass, ShaderType, string, ShaderCompilerPlatform, BuildTarget, GraphicsTier), (bool Success, string Code)> preprocessed = new();
        private readonly Dictionary<(bool, ShaderTextKey, string), string> inspectedPrograms = new();
        private readonly BoundedAnalysisCache<(ShaderTextKey, string, string), bool> entryInputFacts = new(256, 32 * 1024 * 1024);
        private readonly Dictionary<(ShaderData.Pass, ShaderType), string> entries = new();
        private readonly Dictionary<(ShaderData.Pass, ShaderType, string, ShaderCompilerPlatform, BuildTarget, GraphicsTier),
            (ShaderNativeProgram Program, string Error)> nativePrograms = new();
        private readonly Dictionary<(ShaderData.Pass, ShaderType, string, ShaderCompilerPlatform, BuildTarget, GraphicsTier),
            (D3DShaderInputs.Input[] Inputs, string Error)> nativeSemanticFacts = new();
        private readonly HashSet<(ShaderData.Pass, ShaderType, string, ShaderCompilerPlatform, BuildTarget, GraphicsTier)> nativeProgramRequests = new();
        private readonly Dictionary<(ShaderData.Pass, ShaderType, string, ShaderCompilerPlatform, BuildTarget, GraphicsTier), ShaderData.Pass> nativeProgramOwners = new();
        private readonly Dictionary<(ShaderData.Pass, ShaderType, string, ShaderCompilerPlatform, BuildTarget, GraphicsTier, string, string, bool),
            (D3DShaderInputs.Input[] Inputs, string Error, string[] Constants)> nativeConstantFacts = new();
        private readonly Dictionary<ShaderData.Pass, string> passCompilerIdentities = new();
        private readonly Dictionary<ShaderData.Pass, string> preprocessingIdentities = new();
        private static readonly Lazy<string> UnityCompilerIdentity = new(() => UnityShaderCompilerIdentity.D3D);
        private readonly Dictionary<(ShaderData.Pass, ShaderType, ShaderCompilerPlatform, bool), ShaderKeywordPlan.DeclarationSet> keywordDeclarations = new();
        private readonly Dictionary<(ShaderData.Pass, ShaderType, ShaderCompilerPlatform, bool), LocalKeyword[]> passKeywords = new();
        private CompilePrefetch prefetch;
        private CompilePrefetch ahead;
        internal bool PrepareUnitsAhead = true;
        private static int AheadCompileLimit => Math.Min(6, Math.Max(1, (Environment.ProcessorCount - 1) / 2));
        // Logical requests include cached facts. They are diagnostics, never a
        // correctness limit shared by unrelated materials or earlier queries.
        // Complete each bounded keyword plan, then reuse its durable facts.
        internal int PreprocessCalls { get; private set; }
        internal int CompileCalls { get; private set; }
        internal int NativeProgramCompileCalls { get; private set; }
        internal static bool CacheNativePrograms = true;
        internal int NativeProgramCacheHits { get; private set; }
        internal int NativeImportedProgramHits { get; private set; }
        internal int EntrySignatureProofHits { get; private set; }
        internal readonly List<string> UnitDiagnostics = new();
        private readonly BoundedAnalysisCache<(string Source, string Query, ShaderTextKey Original, ShaderTextKey Specialized, string Entry), ShaderInputUseSlice> useSlices = new(MaximumVariantsPerUnit, 32 * 1024 * 1024);
        internal int UseSliceAnalyses { get; private set; }
        internal int SpecializationCalls { get; private set; }
        internal double SpecializationMilliseconds { get; private set; }
        internal double NativePreprocessMilliseconds { get; private set; }
        internal int NativePreprocessCacheHits { get; private set; }
        internal double NativeCompileMilliseconds { get; private set; }
        internal double UseSliceMilliseconds { get; private set; }
        internal double KeywordPlanMilliseconds { get; private set; }
        internal double PreprocessedInspectionMilliseconds { get; private set; }
        internal double NativeCompileWaitMilliseconds { get; private set; }
        internal int PrefetchedCompiles { get; private set; }
        internal int UnitCertificateHits { get; private set; }

        internal ShaderMeshInputs(ShaderPropertyUsage sources) { this.sources = sources; artifacts = sources.Artifacts; }

        internal string Issue(Shader shader, bool addSkinning, BuildTarget? target)
            => Issue(shader, ShaderInputQuery.ForMeshChange(addSkinning), target);

        internal string Issue(Shader shader, ShaderInputQuery query, BuildTarget? target)
        {
            var source = sources.Get(shader);
            string original = CachedSourceIssue(source, query);
            // Keep the cheap, platform-independent proof whenever it already suffices.
            // A caller without an explicit build target cannot narrow that proof.
            if (original == null || shader == null) return original;
            if (!target.HasValue)
            {
                try
                {
                    var programs = sources.GetPrograms(shader);
                    if (programs.Uncertain == null && programs.Passes.Count != 0 && programs.Passes.All(p =>
                        sources.PreprocessorGraph(shader, p.Shader, p.Pass, query).InputAbsent)) return null;
                }
                catch (Exception) { /* The original source proof remains conservative. */ }
                return original;
            }
            var key = (shader, target.Value, query.Signature);
            if (cache.TryGetValue(key, out string cached)) return cached;
            string result;
            try
            {
                string coverage = CoverageKey(shader, target.Value, query, null);
                if (HasCoverage(coverage)) result = null;
                else
                {
                    result = Inspect(shader, target.Value, query, null, null, out _);
                    if (result == null) StoreCoverage(coverage);
                }
            }
            catch (Exception exception) { result = Unknown("variant inspection failed: " + exception.Message); }
            cache.Add(key, result);
            return result;
        }

        internal string Issue(Material material, Renderer renderer, AvatarAnimationIndex animations, bool addSkinning, BuildTarget? target)
            => Issue(material, renderer, animations, ShaderInputQuery.ForMeshChange(addSkinning), target);

        internal string Issue(Material material, Renderer renderer, AvatarAnimationIndex animations, ShaderInputQuery query, BuildTarget? target)
            => Issue(material, renderer, animations, query, target, null);

        internal string Issue(Material material, Renderer renderer, AvatarAnimationIndex animations, ShaderInputQuery query,
            BuildTarget? target, ISet<string> changingUniforms)
        {
            if (material == null || renderer == null || animations == null || !target.HasValue)
                return Issue(material != null ? material.shader : null, query, target);
            var shader = material.shader;
            string original = CachedSourceIssue(sources.Get(shader), query);
            if (original == null || shader == null) return original;
            var constants = animations.MaterialInputs.ConstantUniforms(renderer, material);
            if (query.UniformQuery || changingUniforms?.Count > 0)
                constants = constants.Where(p => !query.QueriesIdentifier(p.Key) && changingUniforms?.Contains(p.Key) != true)
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            bool stableKeywords = animations.UnsafeReason == null && animations.ForObject(renderer).All(u =>
                u.Complete && !u.Binding.isPPtrCurve);
            string keywordSignature = stableKeywords ? "stable:" + ShaderKeywordPlan.MaterialSignature(material) : "dynamic";
            var certificateKey = (shader, target.Value, query.Signature, keywordSignature);
            if (inputCertificates.TryGetValue(certificateKey, out var certificates) && certificates.Any(c =>
                c.All(p => constants.TryGetValue(p.Key, out float value) && FloatBits(value) == p.Value))) return null;
            string signature = string.Join(";", constants.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + ":" + FloatBits(p.Value))) +
                "|keywords:" + keywordSignature;
            var key = (shader, target.Value, query.Signature, signature);
            if (materialCache.TryGetValue(key, out string cached)) return cached;
            string result;
            try
            {
                string coverage = CoverageKey(shader, target.Value, query, signature);
                if (HasCoverage(coverage)) { materialCache.Add(key, null); return null; }
                result = Inspect(shader, target.Value, query, constants, stableKeywords ? material : null, out var observed);
                if (result == null) StoreCoverage(coverage);
                if (result == null && observed != null)
                {
                    if (certificates == null) inputCertificates.Add(certificateKey, certificates = new List<Dictionary<string, int>>());
                    certificates.Add(constants.Where(p => observed.Contains(p.Key)).ToDictionary(p => p.Key, p => FloatBits(p.Value), StringComparer.Ordinal));
                }
            }
            catch (Exception exception) { result = Unknown("material input inspection failed: " + exception.Message); }
            materialCache.Add(key, result); return result;
        }

        internal static string SourceIssue(ShaderPropertyUsage.Proof proof, bool addSkinning)
            => SourceIssue(proof, ShaderInputQuery.ForMeshChange(addSkinning));

        internal static string SourceIssue(ShaderPropertyUsage.Proof proof, ShaderInputQuery query)
        {
            if (proof.Uncertain != null) return Unknown(proof.Uncertain);
            if (proof.Tokens.Any(query.MayDeclareToken) || query.MayUseIdentifiers(proof) ||
                query.Semantics.Any(s => MayPasteInput(proof, s.Name))) return IndexIssue;
            return null;
        }

        private string CachedSourceIssue(ShaderPropertyUsage.Proof proof, ShaderInputQuery query)
        {
            var key = (proof, query.Signature);
            if (!sourceIssues.TryGetValue(key, out string issue)) sourceIssues.Add(key, issue = SourceIssue(proof, query));
            return issue;
        }

        private static bool MayPasteInput(ShaderPropertyUsage.Proof proof, string name)
        {
            if (!proof.TokenPasting) return false;
            // Over-approximate nested macro expansion by concatenating source
            // tokens. Recognize the entire semantic family, including arbitrary
            // numeric suffixes and case variants, rather than sampling indices.
            var possible = new bool[name.Length + 1]; possible[0] = true;
            for (int start = 0; start < name.Length; start++)
            {
                if (!possible[start]) continue;
                foreach (string token in proof.Tokens)
                {
                    int length = Math.Min(token.Length, name.Length - start);
                    if (length == 0 || string.Compare(name, start, token, 0, length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                    if (start + length == name.Length && token.Skip(length).All(c => c >= '0' && c <= '9')) return true;
                    if (length == token.Length) possible[start + length] = true;
                }
            }
            return false;
        }

        internal static bool IsInput(string identifier, bool addSkinning) =>
            ShaderInputQuery.ForMeshChange(addSkinning).MatchesToken(identifier);

        internal static string InspectPreprocessed(bool success, string code, bool addSkinning)
            => InspectPreprocessed(success, code, ShaderInputQuery.ForMeshChange(addSkinning));

        internal static string InspectPreprocessed(bool success, string code, ShaderInputQuery query)
            => success ? new ShaderProgramInspection(code).Inspect(true, query) : Unknown("a shader variant has no verified preprocessed program");

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatRepresentation
        {
            [FieldOffset(0)] internal float Value;
            [FieldOffset(0)] internal int Bits;
        }
        private static int FloatBits(float value) => new FloatRepresentation { Value = value }.Bits;

        private readonly struct InspectionUnit
        {
            internal readonly Shader Shader;
            internal readonly ShaderData.Pass Pass;
            internal readonly ShaderType Stage;
            internal readonly ShaderCompilerPlatform Backend;
            internal readonly GraphicsTier Tier;
            internal readonly LocalKeyword[] Keywords;
            internal InspectionUnit(Shader shader, ShaderData.Pass pass, ShaderType stage,
                ShaderCompilerPlatform backend, GraphicsTier tier, LocalKeyword[] keywords)
            { Shader = shader; Pass = pass; Stage = stage; Backend = backend; Tier = tier; Keywords = keywords; }
        }

        private ShaderKeywordPlan Plan(Shader shader, InspectionUnit unit, Material material,
            ShaderPreprocessorGraph graph, Func<string, bool> representative = null)
        {
            var timer = Stopwatch.StartNew();
            var key = (unit.Pass, unit.Stage, unit.Backend, graph?.IncludesWithoutKeywordPragmas == true);
            if (!keywordDeclarations.TryGetValue(key, out var declarations))
            {
                declarations = new ShaderKeywordPlan.DeclarationSet(PassSource(unit.Pass), unit.Stage, unit.Backend, key.Item4);
                keywordDeclarations.Add(key, declarations);
            }
            var result = ShaderKeywordPlan.Create(declarations, unit.Keywords, material,
                unit.Shader == shader, MaximumVariantsPerUnit, graph, representative);
            KeywordPlanMilliseconds += timer.Elapsed.TotalMilliseconds;
            return result;
        }

        private string Inspect(Shader shader, BuildTarget target, ShaderInputQuery query, IReadOnlyDictionary<string, float> constants, Material material,
            out HashSet<string> observedConstants)
        {
            observedConstants = new HashSet<string>(StringComparer.Ordinal);
            var programs = sources.GetPrograms(shader);
            if (programs.Uncertain != null) return Unknown(programs.Uncertain);
            if (programs.Passes.Count == 0) return Unknown("no inspectable shader programs");
            var backends = Backends(PlayerSettings.GetGraphicsAPIs(target));
            if (backends == null || backends.Length == 0) return Unknown("unverified graphics API coverage");
            var units = programs.Passes.Where(p => ShaderPassSchedule.MayExecute(p.Pass)).SelectMany(p => Stages.Where(p.Pass.HasShaderStage)
                .SelectMany(stage => backends.SelectMany(backend => Tiers.Select(tier => {
                    var id = p.Identifier;
                    if (!passCompilerIdentities.ContainsKey(p.Pass))
                    {
                        string asset = AssetDatabase.GetAssetPath(p.Shader);
                        string sourceIdentity = ShaderBuildSource.TryGenerated(p.Shader, out string generatedSource, out _) ?
                            ContentFactCache.TextDigest(generatedSource) : string.IsNullOrEmpty(asset) ? null : AssetDatabase.GetAssetDependencyHash(asset).ToString();
                        passCompilerIdentities[p.Pass] = UnityCompilerIdentity.Value == null || sourceIdentity == null ? null :
                            UnityCompilerIdentity.Value + "/" + sourceIdentity + "/" + ContentFactCache.TextDigest(PassSource(p.Pass));
                        preprocessingIdentities[p.Pass] = sources.NativePreprocessIdentity(shader, p.Shader, p.Pass, id, UnityCompilerIdentity.Value);
                    }
                    // Unity 2022's pass-keyword API cannot address imported
                    // UsePass entries or built-in fallback pass indices reliably.
                    // Use their complete shader-wide universe without issuing
                    // invalid native queries that log Editor errors.
                    bool shaderWide = programs.ShaderWideKeywordOwners.Contains(p.Shader);
                    var keywordKey = (p.Pass, stage, backend, shaderWide);
                    if (!passKeywords.TryGetValue(keywordKey, out var keywords))
                    {
                        keywords = shaderWide ||
                            backend == ShaderCompilerPlatform.Vulkan || backend == ShaderCompilerPlatform.GLES3x || backend == ShaderCompilerPlatform.OpenGLCore ? p.Shader.keywordSpace.keywords :
                            ShaderUtil.GetPassKeywords(p.Shader, id, stage, backend);
                        passKeywords.Add(keywordKey, keywords);
                    }
                    return new InspectionUnit(p.Shader, p.Pass, stage, backend, tier, keywords);
                }))));
            bool inspected = false;
            string keywordSignature = material == null ? "dynamic" : "stable:" + ShaderKeywordPlan.MaterialSignature(material);
            using var iterator = units.GetEnumerator();
            var pending = new Queue<InspectionUnit>();
            bool exhausted = false;
            Exception pendingFailure = null;
            void PrepareNext()
            {
                if (SemanticOnly(query) || !PrepareUnitsAhead || pending.Count != 0 || exhausted || pendingFailure != null ||
                    Environment.ProcessorCount < 4) return;
                // Metadata and Unity preprocessing stay on the editor thread.
                // Repeated tiers often share a program. Bound both the metadata
                // window and distinct native jobs, leaving capacity for current
                // query refinements and a dependent synchronous witness.
                ahead?.Dispose();
                var batch = ahead = new CompilePrefetch(this, AheadCompileLimit);
                while (pending.Count < AheadCompileLimit * 6 && batch.Native.Count < AheadCompileLimit)
                {
                    InspectionUnit next;
                    try
                    {
                        if (!iterator.MoveNext()) { exhausted = true; break; }
                        next = iterator.Current;
                    }
                    catch (Exception exception)
                    {
                        // An iterator failure belongs after the queued units.
                        // An earlier counterexample must still win.
                        pendingFailure = exception; break;
                    }
                    pending.Enqueue(next);
                    try { PrepareAhead(batch, shader, next, material, target, query, constants); }
                    catch { /* The canonical consumer diagnoses this unit. */ }
                }
            }
            try
            {
                while (true)
                {
                    InspectionUnit unit;
                    if (pending.Count != 0) unit = pending.Dequeue();
                    else if (pendingFailure != null) throw pendingFailure;
                    else if (!exhausted && iterator.MoveNext()) unit = iterator.Current;
                    else break;
                    inspected = true;
                    var certificateKey = (shader, unit.Pass, unit.Stage, unit.Backend, unit.Tier, target, query.Signature, keywordSignature);
                    string storedCertificateKey = UnitCoverageKey(unit, target, query, keywordSignature);
                    if (!unitCertificates.TryGetValue(certificateKey, out var certificates))
                    {
                        if (!ContentFactCache.Shared.TryRead(UnitCoverageKind, storedCertificateKey, ReadUnitCoverage, out certificates))
                            certificates = new();
                        unitCertificates.Add(certificateKey, certificates);
                    }
                    if (certificates.Count != 0)
                    {
                        Dictionary<string, int> matching = null;
                        foreach (var certificate in certificates)
                        {
                            bool matches = true;
                            foreach (var pair in certificate)
                                if (constants == null || !constants.TryGetValue(pair.Key, out float value) || FloatBits(value) != pair.Value)
                                { matches = false; break; }
                            if (matches) { matching = certificate; break; }
                        }
                        if (matching != null)
                        {
                            // A later live/unsupported pass must not discard completed
                            // proofs for earlier units. Reuse the same input-use
                            // certificate as the whole-shader cache, with stage,
                            // backend, tier and keyword coverage kept separate.
                            observedConstants?.UnionWith(matching.Keys);
                            UnitCertificateHits++;
                            continue;
                        }
                    }
                    var declarations = sources.InputDemand(shader, unit.Shader, unit.Pass, new ShaderInputDemand(query), target, unit.Backend, unit.Stage, unit.Tier);
                    if (declarations.InputAbsent) continue;
                    ShaderKeywordPlan UnitPlan(ShaderPreprocessorGraph graph, Func<string, bool> representative = null)
                        => Plan(shader, unit, material, graph, representative);
                    string sliceIssue = null;
                    if (unit.Backend == ShaderCompilerPlatform.D3D && Application.platform == RuntimePlatform.WindowsEditor &&
                        Entry(unit.Pass, unit.Stage) is string entry)
                    {
                        int beforeCompile = CompileCalls, beforePreprocess = PreprocessCalls;
                        var constantDependencies = new HashSet<string>(StringComparer.Ordinal);
                        var proof = ShaderInputVariantProof.Analyze(query, entry, enabled =>
                        {
                            // The first program can share a native job with a
                            // later tier. Start that window before waiting for
                            // the first result; later windows retain their owner
                            // until the current sample has consumed it.
                            if (ahead == null) PrepareNext();
                            string sourceId = ShaderPropertyUsage.ProgramSourceId(unit.Shader);
                            var preparation = prefetch?.TakePreparation((unit.Pass, unit.Stage, string.Join(";", enabled), unit.Backend, target, unit.Tier))
                                ?? ahead?.TakePreparation((unit.Pass, unit.Stage, string.Join(";", enabled), unit.Backend, target, unit.Tier))
                                ?? new ShaderInputPreparation(sourceId);
                            try
                            {
                                string issue = PreprocessWithEvidence(unit.Pass, unit.Stage, enabled, unit.Backend, target, unit.Tier, query, constants, out var raw, preparation, constantDependencies);
                                if (issue == null) PrepareNext();
                                string specialized = raw == null ? null : Specialize(raw, constants).Code;
                                return new ShaderInputVariantProof.Sample { Original = raw, SourceId = sourceId, Preparation = preparation,
                                    Specialized = specialized,
                                    Issue = issue, InputLive = issue == IndexIssue };
                            }
                            catch { preparation.Dispose(); throw; }
                        }, demand => sources.InputDemand(shader, unit.Shader, unit.Pass, demand, target, unit.Backend, unit.Stage, unit.Tier),
                            UnitPlan, CompileProgram, value => UseSlice(value, query, entry), enabled =>
                                BeginCompileBatch(unit.Pass, unit.Stage, enabled, unit.Backend, target, unit.Tier,
                                    query, constants, entry, ShaderPropertyUsage.ProgramSourceId(unit.Shader)));
                        UnitDiagnostics.Add(unit.Shader.name + "/" + unit.Pass.Name + "/" + unit.Stage + "/" + unit.Tier +
                            ": samples=" + proof.Samples + ", preprocess=" + (PreprocessCalls - beforePreprocess) +
                            ", compile=" + (CompileCalls - beforeCompile) + ", complete=" + proof.Complete + ", issue=" + proof.Issue);
                        if (proof.InputLive) return IndexIssue;
                        if (proof.Complete)
                        {
                            // Slicing records source assumptions; native evaluation
                            // records every constant it reads, including branch
                            // conditions eliminated from its final expressions.
                            proof.Names.UnionWith(constantDependencies);
                            observedConstants?.UnionWith(proof.Names);
                            var certificate = new Dictionary<string, int>(StringComparer.Ordinal);
                            if (constants != null)
                                foreach (var pair in constants)
                                    if (proof.Names.Contains(pair.Key)) certificate.Add(pair.Key, FloatBits(pair.Value));
                            certificates.Add(certificate);
                            StoreUnitCoverage(storedCertificateKey, certificates);
                            continue;
                        }
                        sliceIssue = proof.Issue;
                    }
                    var dependencies = sources.PreprocessorGraph(shader, unit.Shader, unit.Pass, query, target, unit.Backend, unit.Stage, unit.Tier);
                    if (dependencies.InputAbsent) continue;
                    // Full-program DCE may depend on uniforms outside the supported
                    // use slice. Such results keep the complete material cache key.
                    observedConstants = null;
                    var plan = UnitPlan(dependencies);
                    if (plan.Issue != null)
                        return Unknown(plan.Issue + " in " + unit.Pass.Name + ", " + unit.Stage + ", " + unit.Backend +
                            (sliceIssue == null ? "" : "; input-use query: " + sliceIssue) +
                            (dependencies.Issue == null ? "" : "; " + dependencies.Issue));
                    foreach (var enabled in plan.Variants)
                    {
                        string issue = Preprocess(unit.Pass, unit.Stage, enabled, unit.Backend, target, unit.Tier, query, constants, out _);
                        if (issue != null) return issue;
                    }
                }
                return inspected ? null : Unknown("no inspectable shader stages");
            }
            finally { ahead?.Dispose(); }
        }

        private ShaderInputUseSlice UseSlice(ShaderInputVariantProof.Sample value, ShaderInputQuery query, string entry)
        {
            // Identical native programs occur across tiers and imported passes.
            // The proof includes original locations, the query and specialized
            // constants; only this immutable slice is shared, not variant coverage.
            var key = (value.SourceId, query.Signature, value.Original, value.Specialized, entry);
            if (useSlices.TryGetValue(key, out var previous)) return previous;
            UseSliceAnalyses++;
            var timer = Stopwatch.StartNew();
            var slice = ShaderSliceFacts.Get(value, query, entry, CompileProgram);
            UseSliceMilliseconds += timer.Elapsed.TotalMilliseconds;
            useSlices.Add(key, slice, (value.Original?.Length ?? 0) + (value.Specialized?.Length ?? 0));
            return slice;
        }

        private (string Code, int Count) Specialize(string source, IReadOnlyDictionary<string, float> constants)
        {
            var timer = Stopwatch.StartNew();
            if (!artifacts.Declarations.TryGetValue(source, out var plan))
            {
                plan = ShaderConstantInputs.PrepareCached(source);
                artifacts.Declarations.Add(source, plan, (source.Length + plan.Source.Length) * 2);
            }
            var signatureBuilder = new StringBuilder();
            if (constants != null)
                foreach (string name in plan.UniformNames)
                    if (constants.TryGetValue(name, out float value))
                    {
                        if (signatureBuilder.Length != 0) signatureBuilder.Append(';');
                        signatureBuilder.Append(name).Append(':').Append(FloatBits(value));
                    }
            string signature = signatureBuilder.ToString();
            var key = (source, signature);
            if (artifacts.Specializations.TryGetValue(key, out var previous))
            { SpecializationMilliseconds += timer.Elapsed.TotalMilliseconds; return previous; }
            SpecializationCalls++;
            var shared = ContentFactCache.Shared;
            string sharedKey = ContentFactCache.Enabled ? shared.Key("shader specialization", ContentFactCache.TextDigest(source), signature) : null;
            string code; int count;
            if (shared.TryRead("shader specialization", sharedKey, reader => (Code: reader.ReadString(), Count: reader.ReadInt32()), out var cached))
            { code = cached.Code; count = cached.Count; }
            else
            {
                code = plan.Apply(constants, out count);
                shared.Store("shader specialization", sharedKey, writer => { writer.Write(code); writer.Write(count); });
            }
            code = new ShaderTextKey(code).Text;
            // This cache is build-local and bounded by text size as well as count.
            // Keep recently used programs even when earlier shaders fill the cache.
            artifacts.Specializations.Add(key, (code, count), (source.Length + signature.Length + code.Length) * 2);
            SpecializationMilliseconds += timer.Elapsed.TotalMilliseconds;
            return (code, count);
        }

        private string Preprocess(ShaderData.Pass pass, ShaderType stage, string[] keywords,
            ShaderCompilerPlatform backend, BuildTarget target, GraphicsTier tier, ShaderInputQuery query, IReadOnlyDictionary<string, float> constants, out string raw,
            ShaderInputPreparation preparation = null)
            => PreprocessWithEvidence(pass, stage, keywords, backend, target, tier, query, constants, out raw, preparation, null);

        private string PreprocessWithEvidence(ShaderData.Pass pass, ShaderType stage, string[] keywords,
            ShaderCompilerPlatform backend, BuildTarget target, GraphicsTier tier, ShaderInputQuery query, IReadOnlyDictionary<string, float> constants, out string raw,
            ShaderInputPreparation preparation, ISet<string> observedConstants)
        {
            raw = null;
            // Unity 2022's combined-stage backends can return empty, concatenated
            // or malformed source with Success=true. Only the all-stage source
            // proof may grant safety there; a single returned snippet cannot.
            if (backend != ShaderCompilerPlatform.D3D && backend != ShaderCompilerPlatform.Metal)
                return Unknown("preprocessed stage coverage is not independently verifiable for this graphics backend");
            var variantKey = (pass, stage, string.Join(";", keywords), backend, target, tier);
            if (!preprocessed.TryGetValue(variantKey, out var variant))
            {
                PreprocessCalls++;
                if ((prefetch == null || !prefetch.Programs.TryGetValue(variantKey, out variant)) &&
                    (ahead == null || !ahead.Programs.TryGetValue(variantKey, out variant)))
                {
                    variant = NativePreprocess(pass, stage, keywords, backend, target, tier);
                }
                preprocessed.Add(variantKey, variant);
            }
            raw = variant.Code;
            string issue = InspectProgram(variant.Success, raw, query);
            if (issue != IndexIssue) return issue;
            if (backend != ShaderCompilerPlatform.D3D || Application.platform != RuntimePlatform.WindowsEditor) return issue;
            string entry = Entry(pass, stage);
            if (entry == null) return Unknown("shader entry point is not uniquely inspectable");
            if (SemanticOnly(query) && (stage == ShaderType.Vertex || stage == ShaderType.Fragment))
            {
                var signatureKey = ((ShaderTextKey)raw, entry, query.Signature);
                if (!entryInputFacts.TryGetValue(signatureKey, out bool absent))
                {
                    absent = ShaderEntryInputs.Absent(raw, entry, query);
                    entryInputFacts.Add(signatureKey, absent, raw.Length * 2);
                }
                if (absent) { EntrySignatureProofHits++; return null; }
            }
            var specialized = Specialize(raw, constants);
            string program = specialized.Code; int count = specialized.Count;
            if (SemanticOnly(query))
            {
                // The actual player program already supplies a complete semantic
                // dependency proof. A second compilation of the specialized
                // whole source cannot strengthen that fact and adds another
                // compiler request. Source slicing and its pure-call probes still run
                // in ShaderInputVariantProof to cover the same keyword choices.
                if (preparation != null && !preparation.Started &&
                    !ContentFactCache.Shared.Contains(ShaderSliceFacts.Key(preparation.SourceId, raw, program, query, entry)))
                    preparation.Start(raw, program);
                // Absence from the original native input signature is stronger
                // than conditional absence under material constants. Query that
                // independent, durable fact first: an unsupported arithmetic
                // proof must not force the same native compile on every build.
                var originalInputs = OriginalSemanticInputs(pass, stage, keywords, backend, target, tier);
                if (originalInputs.Inputs != null && !query.MayReadCompiled(originalInputs.Inputs, true)) return null;
                var actual = NativeConstantInputs(pass, stage, keywords, backend, target, tier, constants, semanticInputsOnly: true,
                    observedConstants: observedConstants);
                if (actual.Inputs == null) actual = originalInputs;
                if (actual.Inputs == null) return Unknown(actual.Error ?? "native semantic dependency proof is unavailable");
                return query.MayReadCompiled(actual.Inputs, true) ? IndexIssue : null;
            }
            // Unity strips uniform reflection from some compiled variants. Use
            // the unstripped analysis compiler for named-uniform queries even
            // when no constants could be substituted.
            bool native = count != 0 || query.UniformQuery;
            var key = (program, entry, stage, native);
            if (!compiled.TryGetValue(key, out var proof))
            {
                if (native)
                {
                    // Parsing does not require a native compiler call. Overlap it
                    // with the native call (or an already queued compile), then
                    // consume the same native result before inspecting the slice.
                    if (preparation != null && !preparation.Started &&
                        !ContentFactCache.Shared.Contains(ShaderSliceFacts.Key(preparation.SourceId, raw, program, query, entry)))
                        preparation.Start(raw, program);
                    proof = CompileProgram(program, entry, stage);
                }
                else
                {
                    CountNativeProgramRequest(pass, stage, keywords, backend, target, tier);
                    string programKey = passCompilerIdentities.TryGetValue(pass, out string identity) && identity != null
                        ? ShaderAnalysisCache.Shared.Key(program, entry, stage, identity + "/" + backend + "/" + target + "/" + tier + "/" + string.Join(";", keywords))
                        : null;
                    proof = ShaderAnalysisCache.Shared.Compile(programKey, () =>
                    {
                        if (!TryNativeProgram(pass, stage, keywords, backend, target, tier, out var result, out string compileIssue))
                            return (null, compileIssue);
                        D3DShaderInputs.Reflect(result.ShaderData, stage, out var inputs, out string error);
                        return (inputs, error);
                    });
                    compiled.Add(key, proof);
                }
            }
            if (proof.Inputs == null) return Unknown(proof.Error ?? "shader input reflection was incomplete");
            if (query.MayReadCompiled(proof.Inputs, native)) return IndexIssue;
            if (native)
            {
                // Strict source compilation is a discovery probe. Even without
                // injected material constants it can fold a threshold differently
                // from Unity's player compiler. Only the actual native program
                // may certify that the queried input cannot affect its outputs.
                // Anonymous native samplers can remain opaque for semantic
                // dependencies; named-uniform absence still needs ownership.
                var actual = NativeConstantInputs(pass, stage, keywords, backend, target, tier, constants,
                    semanticInputsOnly: false, observedConstants: observedConstants);
                if (actual.Inputs == null) return Unknown(actual.Error ?? "native constant dependency proof is unavailable");
                if (query.MayReadCompiled(actual.Inputs, true)) return IndexIssue;
            }
            return null;
        }

        // Native bytes and public binding metadata describe one actual variant,
        // independently of which proof consumes it. Position dependencies,
        // nonfinite rasterization and whole-program reflection must share this
        // snapshot-owned artifact and count its logical request once. Cache hits
        // must not exclude later variants from an otherwise complete proof.
        private void CountNativeProgramRequest(ShaderData.Pass pass, ShaderType stage, string[] keywords,
            ShaderCompilerPlatform backend, BuildTarget target, GraphicsTier tier)
        {
            pass = NativeProgramOwner(pass, stage, keywords, backend, target, tier);
            var key = (pass, stage, string.Join(";", keywords), backend, target, tier);
            if (nativeProgramRequests.Add(key)) CompileCalls++;
        }

        private bool TryNativeProgram(ShaderData.Pass pass, ShaderType stage, string[] keywords,
            ShaderCompilerPlatform backend, BuildTarget target, GraphicsTier tier,
            out ShaderNativeProgram program, out string issue)
        {
            preprocessingIdentities.TryGetValue(pass, out string consumerIdentity);
            pass = NativeProgramOwner(pass, stage, keywords, backend, target, tier);
            var key = (pass, stage, string.Join(";", keywords), backend, target, tier);
            if (nativePrograms.TryGetValue(key, out var cached))
            { program = cached.Program; issue = cached.Error; return issue == null; }
            program = default;
            issue = null;
            CountNativeProgramRequest(pass, stage, keywords, backend, target, tier);
            // Capture the complete compiler artifact before applying any query or
            // material constants. A new value domain needs evaluation, not another
            // Unity compiler invocation. Imported ownership remains freshly proved.
            const string artifactKind = "native shader program";
            string artifactKey = null;
            string requestedIdentity = CacheNativePrograms && ContentFactCache.Enabled
                ? sources.NativeArtifactIdentity(pass, UnityCompilerIdentity.Value) ?? consumerIdentity : null;
            if (CacheNativePrograms && ContentFactCache.Enabled && requestedIdentity != null)
            {
                artifactKey = ContentFactCache.Shared.Key(artifactKind, requestedIdentity, ContentFactCache.TextDigest(PassSource(pass)),
                    stage.ToString(), key.Item3, backend.ToString(), target.ToString(), tier.ToString(), "public-metadata:true");
                if (ContentFactCache.Shared.TryRead(artifactKind, artifactKey, ShaderNativeProgram.Read, out program))
                {
                    NativeProgramCacheHits++;
                    nativePrograms.Add(key, (program, null)); return true;
                }
                // Earlier records included the importing consumer's dependency
                // identity. It is still a valid (stronger) premise after today's
                // ownership proof; promote it without another native compilation.
                if (consumerIdentity != null && consumerIdentity != requestedIdentity)
                {
                    string previousKey = ContentFactCache.Shared.Key(artifactKind, consumerIdentity, ContentFactCache.TextDigest(PassSource(pass)),
                        stage.ToString(), key.Item3, backend.ToString(), target.ToString(), tier.ToString(), "public-metadata:true");
                    if (ContentFactCache.Shared.Contains(previousKey) &&
                        ContentFactCache.Shared.TryRead(artifactKind, previousKey, ShaderNativeProgram.Read, out program))
                    {
                        NativeProgramCacheHits++;
                        ContentFactCache.Shared.Store(artifactKind, artifactKey, program.Write);
                        nativePrograms.Add(key, (program, null)); return true;
                    }
                }
            }
            var timer = Stopwatch.StartNew(); NativeProgramCompileCalls++;
            try
            {
                using var cost = AnalysisCost.Measure("Unity shader compiler");
                program = pass.CompileVariant(stage, keywords, backend, target, tier, true);
                if (!program.Success) issue = "native shader variant compilation failed";
            }
            catch (Exception exception) { issue = "native shader variant compilation failed: " + exception.GetType().Name; }
            finally
            {
                NativeCompileMilliseconds += timer.Elapsed.TotalMilliseconds;
                NativeCompileWaitMilliseconds += timer.Elapsed.TotalMilliseconds;
            }
            if (issue == null && artifactKey != null) ContentFactCache.Shared.Store(artifactKind, artifactKey, program.Write);
            nativePrograms.Add(key, (program, issue)); return issue == null;
        }

        private ShaderData.Pass NativeProgramOwner(ShaderData.Pass pass, ShaderType stage, string[] keywords,
            ShaderCompilerPlatform backend, BuildTarget target, GraphicsTier tier)
        {
            var key = (pass, stage, string.Join(";", keywords), backend, target, tier);
            if (nativeProgramOwners.TryGetValue(key, out var previous)) return previous;
            var candidate = pass;
            var visited = new HashSet<ShaderData.Pass>();
            while (visited.Add(candidate) && visited.Count <= 64)
            {
                var next = sources.ImportedNativePass(candidate);
                if (next == candidate) break;
                if (Entry(candidate, stage) != Entry(next, stage) ||
                    !Preprocessed(candidate, out var imported) || !Preprocessed(next, out var original) ||
                    !imported.Success || !original.Success || string.IsNullOrEmpty(imported.Code) || imported.Code != original.Code) break;
                candidate = next;
                if (visited.Contains(candidate) || visited.Count == 64) { candidate = pass; break; }
            }
            // Resolve the complete chain before either constructing the artifact
            // key or counting it. A -> B -> C must not compile B against C's
            // identity and then compile C a second time as a separate program.
            if (candidate != pass) NativeImportedProgramHits++;
            nativeProgramOwners.Add(key, candidate); return candidate;

            bool Preprocessed(ShaderData.Pass current, out (bool Success, string Code) value)
            {
                var currentKey = (current, stage, string.Join(";", keywords), backend, target, tier);
                if (preprocessed.TryGetValue(currentKey, out value)) return true;
                PreprocessCalls++;
                value = NativePreprocess(current, stage, keywords, backend, target, tier);
                preprocessed.Add(currentKey, value); return true;
            }
        }

        // A native expression failure cannot invalidate the stronger fact that
        // the original program never reads a semantic at all. This inventory has
        // no material assumptions and is used only for whole-program semantic
        // queries, never named uniforms or a projected output such as SV_POSITION.
        private (D3DShaderInputs.Input[] Inputs, string Error) OriginalSemanticInputs(ShaderData.Pass pass, ShaderType stage,
            string[] keywords, ShaderCompilerPlatform backend, BuildTarget target, GraphicsTier tier)
        {
            var key = (pass, stage, string.Join(";", keywords), backend, target, tier);
            if (nativeSemanticFacts.TryGetValue(key, out var cached)) return cached;
            CountNativeProgramRequest(pass, stage, keywords, backend, target, tier);
            string identity = preprocessingIdentities.TryGetValue(pass, out var preprocessing) ? preprocessing : null;
            string decoder = D3DShaderInputs.CompilerFingerprint;
            // Never infer an environment identity from bytecode size or a shader
            // name. The preprocessing identity covers the complete include graph
            // and live compiler settings; source, stage, tier and keyword choice
            // distinguish this exact program. Material values do not participate.
            string contentKey = string.IsNullOrEmpty(identity) || decoder == null ||
                !preprocessed.TryGetValue(key, out var source) || !source.Success || string.IsNullOrEmpty(source.Code)
                ? null : ShaderAnalysisCache.Shared.Key("native-semantic-inventory-v1/" + ContentFactCache.TextDigest(source.Code),
                    "native program", stage, identity + "/" + decoder + "/" + backend + "/" + target + "/" + tier + "/" + key.Item3);
            var result = ShaderAnalysisCache.Shared.Compile(contentKey, () =>
            {
                if (!TryNativeProgram(pass, stage, keywords, backend, target, tier, out var actual, out string compileIssue))
                    return (null, compileIssue);
                D3DShaderInputs.Reflect(actual.ShaderData, stage, out var inputs, out string issue);
                return (inputs?.Where(i => i.Kind == D3DShaderInputs.InputKind.Semantic).ToArray(), issue);
            });
            nativeSemanticFacts.Add(key, result); return result;
        }

        // Shared by whole-program queries and the vertex-position projection.
        // Cache only native program facts; scene/material eligibility remains in
        // the caller's snapshot, and every constant bit participates in this key.
        // Semantic-only facts cannot satisfy the stricter named-input contract.
        private (D3DShaderInputs.Input[] Inputs, string Error) NativeConstantInputs(ShaderData.Pass pass, ShaderType stage,
            string[] keywords, ShaderCompilerPlatform backend, BuildTarget target, GraphicsTier tier,
            IReadOnlyDictionary<string, float> constants, string outputSemantic = null, bool semanticInputsOnly = false,
            ISet<string> observedConstants = null)
        {
            if (backend != ShaderCompilerPlatform.D3D || Application.platform != RuntimePlatform.WindowsEditor)
                return (null, "native constant dependency proof requires D3D bytecode");
            string enabled = string.Join(";", keywords);
            var programKey = (pass, stage, enabled, backend, target, tier);
            // Unity's public scalar metadata can overlap singleton-array metadata.
            // Only exact native source declarations establish the scalar premise;
            // an absent source premise means no material constant is seeded.
            var nativeConstants = new Dictionary<string, float>(StringComparer.Ordinal);
            var zeroIntegerUniforms = new HashSet<string>(StringComparer.Ordinal);
            if (constants != null && preprocessed.TryGetValue(programKey, out var original) && original.Success)
            {
                if (!artifacts.Declarations.TryGetValue(original.Code, out var declarations))
                {
                    declarations = ShaderConstantInputs.PrepareCached(original.Code);
                    artifacts.Declarations.Add(original.Code, declarations, (original.Code.Length + declarations.Source.Length) * 2);
                }
                foreach (var declaration in declarations.Declarations)
                    if (constants.TryGetValue(declaration.Name, out float value))
                    {
                        if (declaration.Type == "float" || declaration.Type == "half") nativeConstants[declaration.Name] = value;
                        else if (semanticInputsOnly && FloatBits(value) == 0 &&
                            (declaration.Type == "int" || declaration.Type == "uint" || declaration.Type == "bool"))
                        {
                            // MaterialAnimationInputs uses the declared property's
                            // typed getter and excludes non-scalar/unknown writers.
                            // Positive zero has the same 32 bits after integer
                            // conversion or raw upload; negative/nonzero values do
                            // not carry this cross-type premise.
                            nativeConstants[declaration.Name] = 0; zeroIntegerUniforms.Add(declaration.Name);
                        }
                    }
            }
            string signature = string.Join(";", nativeConstants.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + ":" + FloatBits(p.Value)));
            var key = (pass, stage, enabled, backend, target, tier, signature, outputSemantic ?? "", semanticInputsOnly);
            if (nativeConstantFacts.TryGetValue(key, out var cached))
            {
                if (cached.Constants != null) observedConstants?.UnionWith(cached.Constants);
                return (cached.Inputs, cached.Error);
            }
            CountNativeProgramRequest(pass, stage, keywords, backend, target, tier);
            // The preprocessing identity covers live graphics/player/quality
            // settings and the complete source/include dependency closure. The
            // older compiler-only identity is insufficient for this native proof.
            string identity = preprocessingIdentities.TryGetValue(pass, out var preprocessing) ? preprocessing : null;
            string decoder = D3DShaderInputs.CompilerFingerprint;
            string contentKey = string.IsNullOrEmpty(identity) || decoder == null ? null : ShaderAnalysisCache.Shared.Key(
                (semanticInputsOnly ? "native-expression-dependencies-v4/semantics/" : "native-expression-dependencies-v2/named-inputs/") +
                signature + "/" + (outputSemantic ?? "*") + "/" +
                (preprocessed.TryGetValue(programKey, out var source) ? ContentFactCache.TextDigest(source.Code ?? "") : "<unavailable>"), "native program", stage,
                identity + "/" + decoder + "/" + backend + "/" + target + "/" + tier + "/" + enabled);
            var result = ShaderNativeInputFacts.Get(contentKey, () =>
            {
                if (!TryNativeProgram(pass, stage, keywords, backend, target, tier, out var actual, out string compileIssue))
                    return (null, compileIssue, null);
                using var evaluation = AnalysisCost.Measure("Native shader constant dependency evaluation");
                var observed = new HashSet<string>(StringComparer.Ordinal);
                D3DConstantEquivalence.TryInputs(actual, nativeConstants, stage, out var inputs, out string issue,
                    outputSemantic, semanticInputsOnly, zeroIntegerUniforms, observed);
                return (inputs, issue, observed.ToArray());
            });
            nativeConstantFacts.Add(key, result);
            if (result.Constants != null) observedConstants?.UnionWith(result.Constants);
            return (result.Inputs, result.Error);
        }

        private string PassSource(ShaderData.Pass pass)
            => sources.PassSource(pass);

        private string Entry(ShaderData.Pass pass, ShaderType stage)
        {
            if (entries.TryGetValue((pass, stage), out string previous)) return previous;
            string stageName = stage switch { ShaderType.Vertex => "vertex", ShaderType.Fragment => "fragment", ShaderType.Geometry => "geometry",
                ShaderType.Hull => "hull", ShaderType.Domain => "domain", _ => null };
            var names = Regex.Matches(ShaderPropertyUsage.StripComments(PassSource(pass)),
                @"(?m)^\s*#\s*pragma\s+" + stageName + @"\s+([_A-Za-z][_A-Za-z0-9]*)\s*(?://.*)?$")
                .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToArray();
            string result = names.Length == 1 ? names[0] : null;
            entries.Add((pass, stage), result);
            return result;
        }

        private (D3DShaderInputs.Input[] Inputs, string Error) CompileProgram(string program, string entry, ShaderType stage)
        {
            var key = (program, entry, stage, true);
            if (!compiled.TryGetValue(key, out var proof))
            {
                CompileCalls++;
                if ((prefetch == null || !prefetch.Native.TryGet(program, entry, stage, out proof)) &&
                    (ahead == null || !ahead.Native.TryGet(program, entry, stage, out proof)))
                {
                    using var cost = AnalysisCost.Measure("Synchronous native shader compiler");
                    var timer = Stopwatch.StartNew();
                    D3DShaderInputs.Compile(program, entry, stage, out var inputs, out string error);
                    NativeCompileMilliseconds += timer.Elapsed.TotalMilliseconds;
                    NativeCompileWaitMilliseconds += timer.Elapsed.TotalMilliseconds;
                    proof = (inputs, error);
                }
                compiled.Add(key, proof);
            }
            return proof;
        }

        private string InspectProgram(bool success, string source, ShaderInputQuery query)
        {
            var timer = Stopwatch.StartNew();
            var key = (success, source, query.Signature);
            if (!inspectedPrograms.TryGetValue(key, out string issue))
            {
                issue = success ? artifacts.Inspection(source).Inspect(true, query) : Unknown("a shader variant has no verified preprocessed program");
                inspectedPrograms.Add(key, issue);
            }
            PreprocessedInspectionMilliseconds += timer.Elapsed.TotalMilliseconds;
            return issue;
        }

        private (bool Success, string Code) NativePreprocess(ShaderData.Pass pass, ShaderType stage, string[] keywords,
            ShaderCompilerPlatform backend, BuildTarget target, GraphicsTier tier)
        {
            preprocessingIdentities.TryGetValue(pass, out string identity);
            string signature = string.Join(";", keywords);
            if (artifacts.TryPreprocessed(identity, stage, signature, backend, target, tier, out string previous))
            { NativePreprocessCacheHits++; return (true, previous); }
            var timer = Stopwatch.StartNew();
            var value = pass.PreprocessVariant(stage, keywords, backend, target, tier, false);
            NativePreprocessMilliseconds += timer.Elapsed.TotalMilliseconds;
            string code = value.PreprocessedCode;
            artifacts.StorePreprocessed(identity, stage, signature, backend, target, tier, value.Success, code);
            return (value.Success, code);
        }

        private sealed class CompilePrefetch : IDisposable
        {
            internal readonly ShaderCompileBatch Native;
            internal readonly Dictionary<(ShaderData.Pass, ShaderType, string, ShaderCompilerPlatform, BuildTarget, GraphicsTier), (bool Success, string Code)> Programs = new();
            internal readonly Dictionary<(ShaderData.Pass, ShaderType, string, ShaderCompilerPlatform, BuildTarget, GraphicsTier), ShaderInputPreparation> Preparations = new();
            private readonly ShaderMeshInputs owner;
            private bool disposed;
            internal CompilePrefetch(ShaderMeshInputs owner, int limit)
            { this.owner = owner; Native = new ShaderCompileBatch(limit); }
            internal ShaderInputPreparation TakePreparation((ShaderData.Pass, ShaderType, string, ShaderCompilerPlatform, BuildTarget, GraphicsTier) key)
            {
                if (!Preparations.TryGetValue(key, out var preparation)) return null;
                Preparations.Remove(key); return preparation;
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                try { Native.Dispose(); }
                finally
                {
                    // Consumed preparations belong to their sample. Join every
                    // abandoned parse here, inside the measured analysis lifetime.
                    foreach (var preparation in Preparations.Values) preparation.Dispose();
                    Preparations.Clear();
                    owner.NativeCompileMilliseconds += Native.WorkMilliseconds;
                    owner.NativeCompileWaitMilliseconds += Native.WaitMilliseconds;
                    owner.PrefetchedCompiles += Native.Count;
                    if (ReferenceEquals(owner.prefetch, this)) owner.prefetch = null;
                    if (ReferenceEquals(owner.ahead, this)) owner.ahead = null;
                }
            }
        }

        private void PrepareAhead(CompilePrefetch batch, Shader shader, InspectionUnit unit, Material material, BuildTarget target,
            ShaderInputQuery query, IReadOnlyDictionary<string, float> constants)
        {
            if (SemanticOnly(query) || unit.Backend != ShaderCompilerPlatform.D3D || Application.platform != RuntimePlatform.WindowsEditor ||
                Environment.ProcessorCount < 3) return;
            string entry = Entry(unit.Pass, unit.Stage);
            if (entry == null) return;
            var plan = Plan(shader, unit, material, null, _ => true);
            if (plan.Issue != null || plan.Variants.Length == 0) return;
            var keywords = plan.Variants[0];
            var key = (unit.Pass, unit.Stage, string.Join(";", keywords), unit.Backend, target, unit.Tier);
            if (!preprocessed.TryGetValue(key, out var value) && !batch.Programs.TryGetValue(key, out value))
            {
                batch.Programs.Add(key, value = NativePreprocess(unit.Pass, unit.Stage, keywords, unit.Backend, target, unit.Tier));
            }
            if (InspectProgram(value.Success, value.Code, query) != IndexIssue) return;
            var specialized = Specialize(value.Code, constants);
            if (specialized.Count == 0 && !query.UniformQuery || compiled.ContainsKey((specialized.Code, entry, unit.Stage, true)) ||
                prefetch?.Native.Contains(specialized.Code, entry, unit.Stage) == true ||
                batch.Native.Contains(specialized.Code, entry, unit.Stage)) return;
            batch.Native.Add(specialized.Code, entry, unit.Stage);
            var preparation = new ShaderInputPreparation(ShaderPropertyUsage.ProgramSourceId(unit.Shader));
            batch.Preparations.Add(key, preparation);
            if (!ContentFactCache.Shared.Contains(ShaderSliceFacts.Key(preparation.SourceId, value.Code, specialized.Code, query, entry)))
                preparation.Start(value.Code, specialized.Code);
        }

        private IDisposable BeginCompileBatch(ShaderData.Pass pass, ShaderType stage, IReadOnlyList<string[]> variants,
            ShaderCompilerPlatform backend, BuildTarget target, GraphicsTier tier, ShaderInputQuery query,
            IReadOnlyDictionary<string, float> constants, string entry, string sourceId)
        {
            if (SemanticOnly(query) || prefetch != null || backend != ShaderCompilerPlatform.D3D || Application.platform != RuntimePlatform.WindowsEditor) return null;
            // Worker counts grow with available cores; logical proof coverage and
            // publication order remain independent of scheduling and CPU count.
            int reserve = PrepareUnitsAhead ? AheadCompileLimit + 1 : 1;
            int limit = Math.Min(16 - reserve, Math.Max(1, Environment.ProcessorCount - reserve));
            if (limit < 2) return null;
            var batch = prefetch = new CompilePrefetch(this, limit);
            try
            {
                // Native source preparation stays on the main thread. Speculation
                // never publishes proof results or counts logical requests:
                // the ordinary consumer does both in its original order.
                // Unconsumed entries are discarded when this round ends.
                for (int i = 0; i < variants.Count && i < limit; i++)
                {
                    var keywords = variants[i];
                    var key = (pass, stage, string.Join(";", keywords), backend, target, tier);
                    if (!preprocessed.TryGetValue(key, out var value) &&
                        (ahead == null || !ahead.Programs.TryGetValue(key, out value)))
                    {
                        batch.Programs.Add(key, value = NativePreprocess(pass, stage, keywords, backend, target, tier));
                    }
                    if (InspectProgram(value.Success, value.Code, query) != IndexIssue) continue;
                    var specialized = Specialize(value.Code, constants);
                    // Unspecialized semantic queries retain Unity's compiler;
                    // uniforms require the unstripped analysis compiler.
                    if (specialized.Count == 0 && !query.UniformQuery || compiled.ContainsKey((specialized.Code, entry, stage, true)) ||
                        ahead?.Native.Contains(specialized.Code, entry, stage) == true) continue;
                    batch.Native.Add(specialized.Code, entry, stage);
                    // The first samples otherwise start parsing only after their
                    // prefetched compile has already completed. Keep at most two
                    // tokenized pairs ahead, independently of compiler batch size.
                    if (batch.Preparations.Count < 2)
                    {
                        var preparation = new ShaderInputPreparation(sourceId);
                        batch.Preparations.Add(key, preparation);
                        if (!ContentFactCache.Shared.Contains(ShaderSliceFacts.Key(preparation.SourceId, value.Code, specialized.Code, query, entry)))
                            preparation.Start(value.Code, specialized.Code);
                    }
                }
                return batch;
            }
            catch (Exception)
            {
                batch.Dispose();
                // Preparatory failures cannot replace the serial proof's failure
                // location, diagnostics or conservative result.
                return null;
            }
        }

        internal static ShaderCompilerPlatform[] Backends(IEnumerable<GraphicsDeviceType> apis)
        {
            var result = new HashSet<ShaderCompilerPlatform>();
            foreach (var api in apis)
            {
                switch (api)
                {
                    case GraphicsDeviceType.Direct3D11:
                    case GraphicsDeviceType.Direct3D12: result.Add(ShaderCompilerPlatform.D3D); break;
                    case GraphicsDeviceType.OpenGLCore: result.Add(ShaderCompilerPlatform.OpenGLCore); break;
                    case GraphicsDeviceType.OpenGLES3: result.Add(ShaderCompilerPlatform.GLES3x); break;
                    case GraphicsDeviceType.Vulkan: result.Add(ShaderCompilerPlatform.Vulkan); break;
                    case GraphicsDeviceType.Metal: result.Add(ShaderCompilerPlatform.Metal); break;
                    default: return null;
                }
            }
            return result.OrderBy(p => p).ToArray();
        }

        private static bool SemanticOnly(ShaderInputQuery query) => !query.UniformQuery && query.Semantics.Length != 0;
        private static string Unknown(string reason) => "unverified shader inputs: " + reason;
    }
}
