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
    internal sealed class ShaderMeshInputs
    {
        internal const int MaximumVariantsPerUnit = 256;
        internal const int MaximumCallsPerShader = 1024;
        internal const int MaximumCallsPerSnapshot = 2048;
        internal const int MaximumCompilesPerSnapshot = 128;
        private const string IndexIssue = "shader may depend on the queried inputs";
        internal static readonly ShaderType[] Stages = { ShaderType.Vertex, ShaderType.Fragment,
            ShaderType.Geometry, ShaderType.Hull, ShaderType.Domain };
        internal static readonly GraphicsTier[] Tiers = { GraphicsTier.Tier1, GraphicsTier.Tier2, GraphicsTier.Tier3 };
        private readonly ShaderPropertyUsage sources;
        private readonly Dictionary<(ShaderPropertyUsage.Proof, string), string> sourceIssues = new();
        private readonly Dictionary<(Shader, BuildTarget, string), string> cache = new();
        private readonly Dictionary<(Shader, BuildTarget, string, string), string> materialCache = new();
        private readonly Dictionary<(Shader, BuildTarget, string, string), List<Dictionary<string, int>>> inputCertificates = new();
        private readonly Dictionary<(Shader, ShaderData.Pass, ShaderType, ShaderCompilerPlatform, GraphicsTier, BuildTarget, string, string),
            List<Dictionary<string, int>>> unitCertificates = new();
        private readonly Dictionary<(string Program, string Entry, ShaderType Stage, bool Native), (D3DShaderInputs.Input[] Inputs, string Error)> compiled = new();
        private readonly Dictionary<(ShaderData.Pass, ShaderType, string, ShaderCompilerPlatform, BuildTarget, GraphicsTier), (bool Success, string Code)> preprocessed = new();
        private readonly Dictionary<(bool, string, string), string> inspectedPrograms = new();
        private readonly Dictionary<(ShaderData.Pass, ShaderType), string> entries = new();
        private readonly Dictionary<ShaderData.Pass, string> passCompilerIdentities = new();
        private static readonly Lazy<string> UnityCompilerIdentity = new(() =>
        {
            try
            {
                string compiler = Path.Combine(EditorApplication.applicationContentsPath, "Tools", "UnityShaderCompiler.exe");
                return "Unity/" + Application.unityVersion + "/" + typeof(ShaderUtil).Module.ModuleVersionId + "/" +
                    ShaderAnalysisCache.Digest(File.ReadAllBytes(compiler));
            }
            catch (Exception) { return null; }
        });
        private readonly Dictionary<(ShaderData.Pass, ShaderType, ShaderCompilerPlatform, bool), ShaderKeywordPlan.DeclarationSet> keywordDeclarations = new();
        private readonly Dictionary<(ShaderData.Pass, ShaderType, ShaderCompilerPlatform, bool), LocalKeyword[]> passKeywords = new();
        private CompilePrefetch prefetch;
        private CompilePrefetch ahead;
        internal bool PrepareUnitsAhead = true;
        private static int AheadCompileLimit => Math.Min(6, Math.Max(1, (Environment.ProcessorCount - 1) / 2));
        internal int PreprocessCalls { get; private set; }
        internal int CompileCalls { get; private set; }
        internal readonly List<string> UnitDiagnostics = new();
        private readonly BoundedAnalysisCache<(string Source, string Query, string Original, string Specialized, string Entry), ShaderInputUseSlice> useSlices = new(MaximumVariantsPerUnit, 32 * 1024 * 1024);
        private readonly BoundedAnalysisCache<(string, string), (string Code, int Count)> specializedPrograms = new(MaximumVariantsPerUnit, 16 * 1024 * 1024);
        private readonly BoundedAnalysisCache<string, ShaderConstantInputs.Plan> specializationPlans = new(MaximumVariantsPerUnit, 16 * 1024 * 1024, StringComparer.Ordinal);
        internal int UseSliceAnalyses { get; private set; }
        internal int SpecializationCalls { get; private set; }
        internal double SpecializationMilliseconds { get; private set; }
        internal double NativePreprocessMilliseconds { get; private set; }
        internal double NativeCompileMilliseconds { get; private set; }
        internal double UseSliceMilliseconds { get; private set; }
        internal double KeywordPlanMilliseconds { get; private set; }
        internal double PreprocessedInspectionMilliseconds { get; private set; }
        internal double NativeCompileWaitMilliseconds { get; private set; }
        internal int PrefetchedCompiles { get; private set; }
        internal int UnitCertificateHits { get; private set; }

        internal ShaderMeshInputs(ShaderPropertyUsage sources) { this.sources = sources; }

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
            try { result = Inspect(shader, target.Value, query, null, null, out _); }
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
                result = Inspect(shader, target.Value, query, constants, stableKeywords ? material : null, out var observed);
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
            if (proof.Tokens.Any(query.MatchesToken) || query.MayUseIdentifiers(proof) ||
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
        {
            // Unity can report success with empty code for an unavailable program.
            // This is not evidence that a runtime input is unused.
            if (!success || string.IsNullOrWhiteSpace(code)) return Unknown("a shader variant has no verified preprocessed program");
            for (int i = 0; i < code.Length; i++)
                if (char.IsControl(code[i]) && code[i] != '\r' && code[i] != '\n' && code[i] != '\t')
                    return Unknown("shader preprocessing returned malformed source text");
            if (code.Length > 4 * 1024 * 1024) return Unknown("preprocessed shader size limit reached");
            code = ShaderPropertyUsage.StripComments(code);
            // Line/pragma directives are metadata, not identifiers used by HLSL.
            // Any other remaining directive means preprocessing is incomplete.
            for (int lineStart = 0; lineStart < code.Length;)
            {
                int lineEnd = code.IndexOf('\n', lineStart);
                if (lineEnd < 0) lineEnd = code.Length;
                int cursor = lineStart;
                while (cursor < lineEnd && char.IsWhiteSpace(code[cursor])) cursor++;
                lineStart = lineEnd + 1;
                if (cursor < lineEnd && code[cursor] == '#')
                {
                    string text = code.Substring(cursor, lineEnd - cursor);
                    if (Regex.IsMatch(text, @"^#\s*(line\b|pragma\b|[0-9])", RegexOptions.CultureInvariant)) continue;
                    return Unknown("an unsupported directive remains after shader preprocessing");
                }
                for (int i = cursor; i < lineEnd;)
                {
                    if (!PpLexer.IdentifierStart(code[i])) { i++; continue; }
                    int start = i++;
                    while (i < lineEnd && PpLexer.IdentifierPart(code[i])) i++;
                    // Match the former Unicode-aware regex word boundaries,
                    // including identifiers adjacent to non-ASCII source text.
                    if ((start == cursor || !RegexWord(code[start - 1])) &&
                        (i == lineEnd || !RegexWord(code[i])) && query.MatchesToken(code, start, i - start)) return IndexIssue;
                }
            }
            return null;
        }

        private static bool RegexWord(char c)
        {
            if (c < 128) return PpLexer.IdentifierPart(c);
            // .NET regex boundaries additionally include the two join controls.
            if (c == '\u200c' || c == '\u200d') return true;
            switch (char.GetUnicodeCategory(c))
            {
                case System.Globalization.UnicodeCategory.UppercaseLetter:
                case System.Globalization.UnicodeCategory.LowercaseLetter:
                case System.Globalization.UnicodeCategory.TitlecaseLetter:
                case System.Globalization.UnicodeCategory.ModifierLetter:
                case System.Globalization.UnicodeCategory.OtherLetter:
                case System.Globalization.UnicodeCategory.NonSpacingMark:
                case System.Globalization.UnicodeCategory.DecimalDigitNumber:
                case System.Globalization.UnicodeCategory.ConnectorPunctuation: return true;
                default: return false;
            }
        }

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
            int shaderStart = PreprocessCalls;
            var units = programs.Passes.Where(p => ShaderPassSchedule.MayExecute(p.Pass)).SelectMany(p => Stages.Where(p.Pass.HasShaderStage)
                .SelectMany(stage => backends.SelectMany(backend => Tiers.Select(tier => {
                    var id = p.Identifier;
                    if (!passCompilerIdentities.ContainsKey(p.Pass))
                    {
                        string asset = AssetDatabase.GetAssetPath(p.Shader);
                        passCompilerIdentities[p.Pass] = UnityCompilerIdentity.Value == null || string.IsNullOrEmpty(asset) ? null :
                            UnityCompilerIdentity.Value + "/" + AssetDatabase.GetAssetDependencyHash(asset) + "/" + PassSource(p.Pass);
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
                if (!PrepareUnitsAhead || pending.Count != 0 || exhausted || pendingFailure != null ||
                    CompileCalls >= MaximumCompilesPerSnapshot || Environment.ProcessorCount < 4) return;
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
                    try { PrepareAhead(batch, shader, next, material, target, query, constants, shaderStart); }
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
                    if (unitCertificates.TryGetValue(certificateKey, out var certificates))
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
                        var proof = ShaderInputVariantProof.Analyze(query, entry, enabled =>
                        {
                            if (PreprocessCalls - shaderStart >= MaximumCallsPerShader)
                                return new ShaderInputVariantProof.Sample { Issue = "Shader preprocessing budget exhausted" };
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
                                string issue = Preprocess(unit.Pass, unit.Stage, enabled, unit.Backend, target, unit.Tier, query, constants, out var raw, preparation);
                                if (issue == null) PrepareNext();
                                return new ShaderInputVariantProof.Sample { Original = raw, SourceId = sourceId, Preparation = preparation,
                                    Specialized = raw == null ? null : Specialize(raw, constants).Code,
                                    Issue = issue, InputLive = issue == IndexIssue };
                            }
                            catch { preparation.Dispose(); throw; }
                        }, demand => sources.InputDemand(shader, unit.Shader, unit.Pass, demand, target, unit.Backend, unit.Stage, unit.Tier),
                            UnitPlan, CompileProgram, value => UseSlice(value, query, entry), enabled =>
                                BeginCompileBatch(unit.Pass, unit.Stage, enabled, unit.Backend, target, unit.Tier,
                                    query, constants, entry, shaderStart, ShaderPropertyUsage.ProgramSourceId(unit.Shader)));
                        UnitDiagnostics.Add(unit.Shader.name + "/" + unit.Pass.Name + "/" + unit.Stage + "/" + unit.Tier +
                            ": samples=" + proof.Samples + ", preprocess=" + (PreprocessCalls - beforePreprocess) +
                            ", compile=" + (CompileCalls - beforeCompile) + ", complete=" + proof.Complete + ", issue=" + proof.Issue);
                        if (proof.InputLive) return IndexIssue;
                        if (proof.Complete)
                        {
                            observedConstants?.UnionWith(proof.Names);
                            if (certificates == null) unitCertificates.Add(certificateKey, certificates = new());
                            var certificate = new Dictionary<string, int>(StringComparer.Ordinal);
                            if (constants != null)
                                foreach (var pair in constants)
                                    if (proof.Names.Contains(pair.Key)) certificate.Add(pair.Key, FloatBits(pair.Value));
                            certificates.Add(certificate);
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
                    if (PreprocessCalls - shaderStart >= MaximumCallsPerShader || plan.Variants.Length > MaximumCallsPerSnapshot - PreprocessCalls)
                        return Unknown("shader variant coverage exceeds the bounded analysis limit");
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
            var slice = ShaderInputVariantProof.Slice(value, query, entry, CompileProgram);
            UseSliceMilliseconds += timer.Elapsed.TotalMilliseconds;
            useSlices.Add(key, slice, (value.Original?.Length ?? 0) + (value.Specialized?.Length ?? 0));
            return slice;
        }

        private (string Code, int Count) Specialize(string source, IReadOnlyDictionary<string, float> constants)
        {
            var timer = Stopwatch.StartNew();
            if (!specializationPlans.TryGetValue(source, out var plan))
            {
                plan = ShaderConstantInputs.Prepare(source);
                specializationPlans.Add(source, plan, source.Length + plan.Source.Length);
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
            if (specializedPrograms.TryGetValue(key, out var previous))
            { SpecializationMilliseconds += timer.Elapsed.TotalMilliseconds; return previous; }
            SpecializationCalls++;
            string code = plan.Apply(constants, out int count);
            SpecializationMilliseconds += timer.Elapsed.TotalMilliseconds;
            // This cache is build-local and bounded by text size as well as count.
            // Keep recently used programs even when earlier shaders fill the cache.
            specializedPrograms.Add(key, (code, count), source.Length + signature.Length + code.Length);
            return (code, count);
        }

        private string Preprocess(ShaderData.Pass pass, ShaderType stage, string[] keywords,
            ShaderCompilerPlatform backend, BuildTarget target, GraphicsTier tier, ShaderInputQuery query, IReadOnlyDictionary<string, float> constants, out string raw,
            ShaderInputPreparation preparation = null)
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
                if (PreprocessCalls >= MaximumCallsPerSnapshot) return Unknown("shader preprocessing budget exhausted");
                PreprocessCalls++;
                if ((prefetch == null || !prefetch.Programs.TryGetValue(variantKey, out variant)) &&
                    (ahead == null || !ahead.Programs.TryGetValue(variantKey, out variant)))
                {
                    var timer = Stopwatch.StartNew();
                    var value = pass.PreprocessVariant(stage, keywords, backend, target, tier, false);
                    NativePreprocessMilliseconds += timer.Elapsed.TotalMilliseconds;
                    variant = (value.Success, value.PreprocessedCode);
                }
                preprocessed.Add(variantKey, variant);
            }
            raw = variant.Code;
            string issue = InspectProgram(variant.Success, raw, query);
            if (issue != IndexIssue) return issue;
            if (backend != ShaderCompilerPlatform.D3D || Application.platform != RuntimePlatform.WindowsEditor) return issue;
            var specialized = Specialize(raw, constants);
            string program = specialized.Code; int count = specialized.Count;
            string entry = Entry(pass, stage);
            if (entry == null) return Unknown("shader entry point is not uniquely inspectable");
            // Unity strips uniform reflection from some compiled variants. Use
            // the unstripped analysis compiler for named-uniform queries even
            // when no constants could be substituted.
            bool native = count != 0 || query.UniformQuery;
            var key = (program, entry, stage, native);
            string programKey = native ? D3DShaderInputs.CacheKey(program, entry, stage) :
                passCompilerIdentities.TryGetValue(pass, out string identity) && identity != null
                    ? ShaderAnalysisCache.Shared.Key(program, entry, stage, identity + "/" + backend + "/" + target + "/" + tier + "/" + string.Join(";", keywords))
                    : null;
            if (!compiled.TryGetValue(key, out var proof))
            {
                if (native)
                {
                    // Parsing does not consume compiler/proof budgets. Overlap it
                    // with the native call (or an already queued compile), then
                    // consume the same native result before inspecting the slice.
                    if (CompileCalls < MaximumCompilesPerSnapshot && preparation != null && !preparation.Started)
                        preparation.Start(raw, program);
                    proof = CompileProgram(program, entry, stage);
                }
                else
                {
                    if (CompileCalls >= MaximumCompilesPerSnapshot) return Unknown("shader compiler analysis budget exhausted");
                    CompileCalls++;
                    var timer = Stopwatch.StartNew();
                    using var cost = AnalysisCost.Measure("Unity shader compiler");
                    proof = ShaderAnalysisCache.Shared.Compile(programKey, () =>
                    {
                        var result = pass.CompileVariant(stage, keywords, backend, target, tier, true);
                        if (!result.Success) return (null, "shader variant compilation failed");
                        D3DShaderInputs.Reflect(result.ShaderData, stage, out var inputs, out string error);
                        return (inputs, error);
                    });
                    NativeCompileMilliseconds += timer.Elapsed.TotalMilliseconds;
                    NativeCompileWaitMilliseconds += timer.Elapsed.TotalMilliseconds;
                    compiled.Add(key, proof);
                }
            }
            if (proof.Inputs == null) return Unknown(proof.Error ?? "shader input reflection was incomplete");
            return ShaderAnalysisCache.Shared.Usage(programKey, query, proof.Inputs, native) ? IndexIssue : null;
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
                if (CompileCalls >= MaximumCompilesPerSnapshot) return (null, "shader compiler analysis budget exhausted");
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
                issue = InspectPreprocessed(success, source, query);
                inspectedPrograms.Add(key, issue);
            }
            PreprocessedInspectionMilliseconds += timer.Elapsed.TotalMilliseconds;
            return issue;
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
            ShaderInputQuery query, IReadOnlyDictionary<string, float> constants, int shaderStart)
        {
            if (unit.Backend != ShaderCompilerPlatform.D3D || Application.platform != RuntimePlatform.WindowsEditor ||
                CompileCalls >= MaximumCompilesPerSnapshot || Environment.ProcessorCount < 3) return;
            string entry = Entry(unit.Pass, unit.Stage);
            if (entry == null) return;
            var plan = Plan(shader, unit, material, null, _ => true);
            if (plan.Issue != null || plan.Variants.Length == 0) return;
            var keywords = plan.Variants[0];
            var key = (unit.Pass, unit.Stage, string.Join(";", keywords), unit.Backend, target, unit.Tier);
            int pending = (prefetch?.Programs.Count ?? 0) + batch.Programs.Count;
            if (PreprocessCalls + pending >= MaximumCallsPerSnapshot ||
                PreprocessCalls - shaderStart + pending >= MaximumCallsPerShader) return;
            if (!preprocessed.TryGetValue(key, out var value) && !batch.Programs.TryGetValue(key, out value))
            {
                var timer = Stopwatch.StartNew();
                var native = unit.Pass.PreprocessVariant(unit.Stage, keywords, unit.Backend, target, unit.Tier, false);
                NativePreprocessMilliseconds += timer.Elapsed.TotalMilliseconds;
                batch.Programs.Add(key, value = (native.Success, native.PreprocessedCode));
            }
            if (InspectProgram(value.Success, value.Code, query) != IndexIssue) return;
            var specialized = Specialize(value.Code, constants);
            if (specialized.Count == 0 && !query.UniformQuery || compiled.ContainsKey((specialized.Code, entry, unit.Stage, true)) ||
                prefetch?.Native.Contains(specialized.Code, entry, unit.Stage) == true ||
                batch.Native.Contains(specialized.Code, entry, unit.Stage)) return;
            batch.Native.Add(specialized.Code, entry, unit.Stage);
            var preparation = new ShaderInputPreparation(ShaderPropertyUsage.ProgramSourceId(unit.Shader));
            batch.Preparations.Add(key, preparation);
            preparation.Start(value.Code, specialized.Code);
        }

        private IDisposable BeginCompileBatch(ShaderData.Pass pass, ShaderType stage, IReadOnlyList<string[]> variants,
            ShaderCompilerPlatform backend, BuildTarget target, GraphicsTier tier, ShaderInputQuery query,
            IReadOnlyDictionary<string, float> constants, string entry, int shaderStart, string sourceId)
        {
            if (prefetch != null || backend != ShaderCompilerPlatform.D3D || Application.platform != RuntimePlatform.WindowsEditor) return null;
            // Worker counts grow with available cores; logical proof budgets and
            // publication order remain independent of scheduling and CPU count.
            int reserve = PrepareUnitsAhead ? AheadCompileLimit + 1 : 1;
            int limit = Math.Min(Math.Min(16 - reserve, Math.Max(1, Environment.ProcessorCount - reserve)), MaximumCompilesPerSnapshot - CompileCalls);
            if (limit < 2) return null;
            var batch = prefetch = new CompilePrefetch(this, limit);
            try
            {
                // Native source preparation stays on the main thread. Speculation
                // never publishes proof results or spends the logical analysis
                // budget: the ordinary consumer does both in its original order.
                // Unconsumed entries are discarded when this round ends.
                for (int i = 0; i < variants.Count && i < limit; i++)
                {
                    var keywords = variants[i];
                    var key = (pass, stage, string.Join(";", keywords), backend, target, tier);
                    if (!preprocessed.TryGetValue(key, out var value) &&
                        (ahead == null || !ahead.Programs.TryGetValue(key, out value)))
                    {
                        if (PreprocessCalls + batch.Programs.Count >= MaximumCallsPerSnapshot ||
                            PreprocessCalls - shaderStart + batch.Programs.Count >= MaximumCallsPerShader) break;
                        var timer = Stopwatch.StartNew();
                        var native = pass.PreprocessVariant(stage, keywords, backend, target, tier, false);
                        NativePreprocessMilliseconds += timer.Elapsed.TotalMilliseconds;
                        batch.Programs.Add(key, value = (native.Success, native.PreprocessedCode));
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

        private static string Unknown(string reason) => "unverified shader inputs: " + reason;
    }
}
