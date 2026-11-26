using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Rendering;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // An absence proof, not a shader compiler or an active-variant approximation. All source
    // branches, includes, passes, UsePass and fallback dependencies contribute possible names.
    // Imported programs do not require editable source. Fixed-function state and
    // program availability are separate proofs; a declaration counts as usage.
    internal sealed partial class ShaderPropertyUsage
    {
        internal sealed class Programs
        {
            internal readonly List<(Shader Shader, ShaderData.Pass Pass, UnityEngine.Rendering.PassIdentifier Identifier)> Passes = new();
            internal readonly HashSet<Shader> ShaderWideKeywordOwners = new();
            internal readonly HashSet<string> IncludeDirectories = new(StringComparer.Ordinal);
            internal readonly HashSet<string> ShaderLabIdentifiers = new(StringComparer.Ordinal);
            internal readonly HashSet<string> ShaderLabStateIdentifiers = new(StringComparer.Ordinal);
            internal bool ShaderLabStatesComplete = true;
            internal string ShaderLabStateIssue;
            internal readonly HashSet<string> NativeDependencyPaths = new(StringComparer.Ordinal);
            internal readonly HashSet<Shader> NativeShaderDependencies = new();
            internal bool NativeDependenciesComplete = true;
            internal string DegenerateRasterizationIssue;
            internal string Uncertain;
        }

        internal sealed class Proof
        {
            // Predefined numeric macros such as __LINE__ and __COUNTER__ may expand to
            // digits absent from the literal source before a token-pasting operation.
            internal readonly HashSet<string> Tokens = new("0123456789".Select(c => c.ToString()), StringComparer.Ordinal);
            internal readonly Dictionary<string, Regex> PastePatterns = new(StringComparer.Ordinal);
            internal bool TokenPasting;
            internal string Uncertain;
            internal string FixedFunctionUncertain;

            internal bool MayUse(string property) => FixedFunctionUncertain != null || MayUseProgramIdentifier(property);
            internal bool MayUseProgramIdentifier(string identifier) => Uncertain != null || MayExpandName(identifier);

            internal bool MayExpandName(string property)
            {
                if (string.IsNullOrEmpty(property) || property.Length > 128 ||
                    !Identifier.IsMatch(property) || Tokens.Contains(property)) return true;
                if (!TokenPasting) return false;
                // A generated identifier must be the result of a paste expression. Literal
                // prefixes/suffixes constrain the last paste even across nested macro calls.
                if (!PastePatterns.Values.Any(pattern => pattern.IsMatch(property))) return false;
                // Macro parameters can be pasted, expanded and pasted again. Over-approximate
                // this by allowing arbitrary concatenations of any source token, not just a
                // single macro expansion. This also covers generated texture _ST names.
                var possible = new bool[property.Length + 1]; possible[0] = true;
                for (int end = 1; end <= property.Length; end++)
                    for (int start = 0; start < end; start++)
                        if (possible[start] && Tokens.Contains(property.Substring(start, end - start)))
                        { possible[end] = true; break; }
                return possible[property.Length];
            }
        }

        private static readonly Regex Identifier = new(@"^[_A-Za-z][_A-Za-z0-9]*$", RegexOptions.CultureInvariant);
        private static readonly Regex Tokens = new(@"""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[_A-Za-z][_A-Za-z0-9]*|[0-9]+", RegexOptions.CultureInvariant);
        private static readonly Regex Include = new(@"^\s*#\s*include(?:_with_pragmas)?\b([^\r\n]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex IncludePath = new(@"^\s*[""<]([^"">]+)["">]\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex UntrackedInclude = new(@"^\s*#\s*(include_next|import|using|embed)\b", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex ShaderDependency = new(@"\b(Fallback|UsePass)\s+""([^""]+)""|\bDependency\s+""[^""]+""\s*=\s*""([^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Define = new(@"^\s*#\s*define\s+[_A-Za-z][_A-Za-z0-9]*(?:\(([^)]*)\))?\s+(.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex Paste = new(@"[_A-Za-z0-9]+(?:\s*##\s*[_A-Za-z0-9]+)+", RegexOptions.CultureInvariant);
        private readonly Dictionary<Shader, Proof> cache = new();
        private readonly Dictionary<Shader, Programs> programs = new();
        private readonly Dictionary<(Shader, string, BuildTarget), bool> propertyReads = new();
        private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);
        // A ShaderLab snapshot belongs to the shader, not its asset container.
        // Multiple generated shaders may share one .asset file or include origin.
        private readonly Dictionary<Shader, string> shaderSources = new();
        private readonly Dictionary<(string, string), PpDocument> parsedPreprocessorDocuments = new();
        private readonly Dictionary<(Shader, Shader, string, string), ShaderPreprocessorGraph.Source[]> resolvedIncludes = new();
        private readonly Dictionary<(Shader Owner, ShaderTextKey Source, string Query, string Context, string IncludeSearch), ShaderPreprocessorGraph> preprocessorGraphs = new();
        private readonly Dictionary<(Shader, int, int, bool), ShaderData.Pass> passSnapshots = new();
        private readonly Dictionary<ShaderData.Pass, string> passSources = new();
        private readonly Dictionary<Shader, ShaderImportedMetadata> importedMetadata = new();
        private readonly Dictionary<(BuildTarget, ShaderCompilerPlatform, ShaderType, UnityEngine.Rendering.GraphicsTier?), Dictionary<string, string>> preprocessorContexts = new();
        internal int FilesRead => files.Count;
        internal double PreprocessorGraphMilliseconds { get; private set; }
        internal double PreprocessorDiscoveryMilliseconds { get; private set; }
        internal double PreprocessorEvaluationMilliseconds { get; private set; }
        internal int PreprocessorGraphs => preprocessorGraphs.Count;
        internal readonly ShaderAnalysisArtifacts Artifacts;
        internal ShaderPropertyUsage() : this(new ShaderAnalysisArtifacts()) { }
        internal ShaderPropertyUsage(ShaderAnalysisArtifacts artifacts) { Artifacts = artifacts; }
        private ShaderMeshInputs meshInputs;
        internal ShaderMeshInputs MeshInputs => meshInputs ??= new ShaderMeshInputs(this);

        internal string PassSource(ShaderData.Pass pass)
        {
            if (!passSources.TryGetValue(pass, out string source)) passSources.Add(pass, source = pass.SourceCode);
            return source;
        }

        internal bool MayRead(Shader shader, string property, BuildTarget? buildTarget = null)
        {
            if (shader == null || string.IsNullOrEmpty(property) || !Identifier.IsMatch(property)) return true;
            var target = buildTarget ?? EditorUserBuildSettings.activeBuildTarget;
            if (propertyReads.TryGetValue((shader, property, target), out bool cached)) return cached;
            bool Inspect()
            {
                var program = GetPrograms(shader);
                if (!program.ShaderLabStatesComplete) return true;
                if (!Get(shader).MayUse(property)) return false;
                // ShaderLab render states and declarations are part of the native
                // material contract, independently of HLSL program references.
                if (program.Uncertain != null || program.ShaderLabIdentifiers.Contains(property) || program.Passes.Count == 0) return true;
                return ProgramsMayRead(shader, ShaderInputQuery.Identifier(property), target);
            }
            return propertyReads[(shader, property, target)] = Inspect();
        }

        internal bool MayRead(Material material, Renderer renderer, AvatarAnimationIndex animations, string property,
            ISet<string> changingUniforms, BuildTarget? buildTarget = null)
        {
            if (material == null || material.shader == null || renderer == null || animations == null ||
                string.IsNullOrEmpty(property) || !Identifier.IsMatch(property)) return true;
            var shader = material.shader;
            if (!MayRead(shader, property, buildTarget)) return false;
            var program = GetPrograms(shader);
            // A Properties declaration is authoring metadata, but render states,
            // tags and other ShaderLab uses are outside compiled HLSL reflection.
            if (program.Uncertain != null || !program.ShaderLabStatesComplete || program.ShaderLabStateIdentifiers.Contains(property)) return true;
            return MeshInputs.Issue(material, renderer, animations, ShaderInputQuery.Uniform(property),
                buildTarget ?? EditorUserBuildSettings.activeBuildTarget, changingUniforms) != null;
        }

        internal void PrimePropertyReads(Shader shader, IEnumerable<string> properties, BuildTarget? buildTarget = null)
        {
            if (shader == null) return;
            var target = buildTarget ?? EditorUserBuildSettings.activeBuildTarget;
            var proof = Get(shader); var program = GetPrograms(shader); var pending = new List<string>();
            foreach (string property in properties.Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(property) || !Identifier.IsMatch(property) || propertyReads.ContainsKey((shader, property, target))) continue;
                if (!program.ShaderLabStatesComplete) propertyReads[(shader, property, target)] = true;
                else if (!proof.MayUse(property)) propertyReads[(shader, property, target)] = false;
                else if (program.Uncertain != null || program.ShaderLabIdentifiers.Contains(property) || program.Passes.Count == 0)
                    propertyReads[(shader, property, target)] = true;
                else pending.Add(property);
            }
            // Most disjoint material interfaces have a whole set of absent
            // uniforms. Prove them together, sharing one preprocessor traversal.
            // A possible match never marks its unrelated peers as readers.
            if (pending.Count != 0 && !ProgramsMayRead(shader, ShaderInputQuery.Identifiers(pending), target))
                foreach (string property in pending) propertyReads[(shader, property, target)] = false;
        }

        private bool ProgramsMayRead(Shader shader, ShaderInputQuery query, BuildTarget target)
        {
            var demand = new ShaderInputDemand(query);
            var backends = ShaderMeshInputs.Backends(PlayerSettings.GetGraphicsAPIs(target));
            if (backends == null || backends.Length == 0) return true;
            foreach (var pass in GetPrograms(shader).Passes)
            {
                var stages = ShaderMeshInputs.Stages.Where(pass.Pass.HasShaderStage).ToArray();
                if (stages.Length == 0) return true;
                foreach (var stage in stages)
                    foreach (var backend in backends)
                        foreach (var tier in ShaderMeshInputs.Tiers)
                        {
                            var graph = InputDemand(shader, pass.Shader, pass.Pass, demand, target, backend, stage, tier);
                            if (graph.Issue != null || !graph.InputAbsent) return true;
                        }
            }
            return false;
        }

        private static void ReadShaderLabIdentifiers(IReadOnlyList<PpToken> tokens, ISet<string> names, bool skipProperties = false)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind != PpTokenKind.Identifier) continue;
                int next = i + 1;
                while (next < tokens.Count && tokens[next].Kind == PpTokenKind.Newline) next++;
                if (skipProperties && token.Text == "Properties" && next < tokens.Count && tokens[next].Text == "{")
                {
                    int depth = 1; i = next + 1;
                    for (; i < tokens.Count && depth != 0; i++)
                    { if (tokens[i].Text == "{") depth++; else if (tokens[i].Text == "}") depth--; }
                    if (depth != 0) throw new InvalidOperationException("unterminated ShaderLab properties");
                    i--; continue;
                }
                if (token.Text == "CGPROGRAM" || token.Text == "CGINCLUDE" || token.Text == "HLSLPROGRAM" || token.Text == "HLSLINCLUDE")
                {
                    string end = token.Text.StartsWith("CG", StringComparison.Ordinal) ? "ENDCG" : "ENDHLSL";
                    do { i++; } while (i < tokens.Count && (tokens[i].Kind != PpTokenKind.Identifier || tokens[i].Text != end));
                    if (i == tokens.Count) throw new InvalidOperationException("unterminated ShaderLab program");
                }
                else names.Add(token.Text);
            }
        }

        internal Programs GetPrograms(Shader shader)
        {
            if (shader == null) return new Programs { Uncertain = "missing shader" };
            Get(shader);
            return programs[shader];
        }

        internal ShaderPreprocessorGraph PreprocessorGraph(Shader root, Shader owner, ShaderData.Pass pass, bool addSkinning,
            BuildTarget? target = null, ShaderCompilerPlatform? backend = null, ShaderType? stage = null, UnityEngine.Rendering.GraphicsTier? tier = null)
            => PreprocessorGraph(root, owner, pass, ShaderInputQuery.ForMeshChange(addSkinning), target, backend, stage, tier);

        internal ShaderPreprocessorGraph PreprocessorGraph(Shader root, Shader owner, ShaderData.Pass pass, ShaderInputQuery query,
            BuildTarget? target = null, ShaderCompilerPlatform? backend = null, ShaderType? stage = null, UnityEngine.Rendering.GraphicsTier? tier = null)
            => PreprocessorGraphCore(root, owner, pass, query, target, backend, stage, tier, null);

        internal ShaderPreprocessorGraph InputDemand(Shader root, Shader owner, ShaderData.Pass pass, ShaderInputDemand demand,
            BuildTarget target, ShaderCompilerPlatform backend, ShaderType stage, UnityEngine.Rendering.GraphicsTier tier)
            => PreprocessorGraphCore(root, owner, pass, demand.Query, target, backend, stage, tier, demand);

        internal static string ProgramSourceId(Shader owner)
        {
            string physical = ShaderBuildSource.PhysicalPath(owner);
            return Path.Combine(physical != null ? Path.GetDirectoryName(physical) :
                Path.Combine(EditorApplication.applicationContentsPath, "CGIncludes"), "__ALTO_PASS_SOURCE__");
        }

        private ShaderPreprocessorGraph PreprocessorGraphCore(Shader root, Shader owner, ShaderData.Pass pass, ShaderInputQuery query,
            BuildTarget? target, ShaderCompilerPlatform? backend, ShaderType? stage, UnityEngine.Rendering.GraphicsTier? tier, ShaderInputDemand demand)
        {
            string context = target.HasValue && backend.HasValue && stage.HasValue ? target + ":" + backend + ":" + stage + ":" + tier : "";
            Dictionary<string, string> definitions = null;
            if (context != "")
            {
                var contextKey = (target.Value, backend.Value, stage.Value, tier);
                if (!preprocessorContexts.TryGetValue(contextKey, out var common))
                    preprocessorContexts.Add(contextKey, common = ShaderPreprocessorContext.Definitions(target.Value, backend.Value, stage.Value, tier));
                var keywords = owner.keywordSpace.keywords.Select(k => k.name).ToHashSet(StringComparer.Ordinal);
                definitions = common.Where(d => !keywords.Contains(d.Key)).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                // A source graph depends on macro facts, not the tier's label.
                // Native probes still cover every tier independently.
                context = target + ":" + backend + ":" + stage + ":" + string.Join(";", definitions.OrderBy(d => d.Key, StringComparer.Ordinal)
                    .Select(d => d.Key + "=" + (d.Value ?? "<undefined>")));
            }
            var program = GetPrograms(root);
            // Imported passes have fresh Unity wrappers in different root shaders.
            // The managed proof depends on source, compiler inputs and the complete
            // include search sequence, not on that wrapper or the root shader identity.
            // Retain candidate order as well: bounded abstract joins must see the
            // same sequence even when several include paths resolve successfully.
            string includeSearch = string.Join("\0", program.IncludeDirectories);
            string passSource = PassSource(pass);
            var key = (owner, passSource, query.Signature + (demand == null ? "" : "|demand:" + demand.Signature), context, includeSearch);
            if (preprocessorGraphs.TryGetValue(key, out var previous)) return previous;
            string physical = ShaderBuildSource.PhysicalPath(owner);
            string directory = physical != null ? Path.GetDirectoryName(physical) :
                Path.Combine(EditorApplication.applicationContentsPath, "CGIncludes");
            var source = new ShaderPreprocessorGraph.Source(ProgramSourceId(owner), passSource);
            ShaderPreprocessorGraph.Source[] Includes(string origin, string include)
            {
                var includeKey = (root, owner, origin, include);
                if (resolvedIncludes.TryGetValue(includeKey, out var previousSources)) return previousSources;
                var roots = program.IncludeDirectories.Concat(new[] { Path.GetDirectoryName(origin), directory });
                var paths = roots.Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.GetFullPath(Path.Combine(p, include)))
                    .Concat(new[] { IncludePhysicalPath(include) }).Where(p => p != null && SourceExists(p)).Distinct(StringComparer.Ordinal).ToArray();
                var result = paths.Select(path =>
                {
                    if (!files.TryGetValue(path, out string text))
                    {
                        if (files.Count >= 512 || new FileInfo(path).Length > 4 * 1024 * 1024)
                            throw new InvalidOperationException("shader source analysis limit reached");
                        files.Add(path, text = File.ReadAllText(path));
                    }
                    return new ShaderPreprocessorGraph.Source(path, text);
                }).ToArray();
                resolvedIncludes.Add(includeKey, result);
                return result;
            }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var graph = ShaderPreprocessorGraph.AnalyzeCached(source, Includes, query,
                owner.keywordSpace.keywords.Select(k => k.name), definitions, parsedPreprocessorDocuments, demand);
            PreprocessorGraphMilliseconds += timer.Elapsed.TotalMilliseconds;
            PreprocessorDiscoveryMilliseconds += graph.DiscoveryMilliseconds;
            PreprocessorEvaluationMilliseconds += graph.EvaluationMilliseconds;
            preprocessorGraphs.Add(key, graph); return graph;
        }

        internal Proof Get(Shader shader)
        {
            if (shader == null) return new Proof { Uncertain = "missing shader" };
            if (cache.TryGetValue(shader, out var cached)) return cached;
            using var discoveryCost = AnalysisCost.Measure("Shader source and pass discovery");
            var proof = new Proof(); cache.Add(shader, proof);
            var sourcePrograms = new Programs(); programs.Add(shader, sourcePrograms);
            var seenShaders = new HashSet<Shader>();
            var requiredPasses = new Dictionary<Shader, HashSet<string>>();
            var rasterStates = new Dictionary<Shader, ShaderRasterizationState>();
            var importedStates = new Dictionary<Shader, ShaderImportedMetadata>();
            var seenFiles = new HashSet<string>(StringComparer.Ordinal);
            var directories = new HashSet<string>(StringComparer.Ordinal);
            var passSources = new List<(string Source, string Directory)>();
            var includeDirectory = Path.Combine(EditorApplication.applicationContentsPath, "CGIncludes");
            void Uncertain(string reason) { proof.Uncertain ??= reason; }
            string Read(string path)
            {
                sourcePrograms.NativeDependencyPaths.Add(path);
                if (files.TryGetValue(path, out var source)) return Artifacts.WithoutComments(source);
                if (files.Count >= 512 || new FileInfo(path).Length > 4 * 1024 * 1024)
                    throw new InvalidOperationException("shader source analysis limit reached");
                source = File.ReadAllText(path); files.Add(path, source); return Artifacts.WithoutComments(source);
            }
            void Scan(string text, string directory)
            {
                using var cost = AnalysisCost.Measure("Shader discovery/include closure");
                var facts = Artifacts.SourceFacts(text);
                facts.Apply(proof);
                sourcePrograms.NativeDependenciesComplete &= facts.TrackedIncludes;
                foreach (string path in facts.Includes)
                {
                    if (path == null)
                    { sourcePrograms.NativeDependenciesComplete = false; Uncertain("macro or unsupported include path"); continue; }
                    var roots = directories.Concat(new[] { directory, includeDirectory }).Where(d => !string.IsNullOrEmpty(d));
                    var possiblePaths = roots.Select(d => Path.GetFullPath(Path.Combine(d, path)))
                        .Concat(new[] { IncludePhysicalPath(path) }).Where(p => p != null).Distinct(StringComparer.Ordinal).ToArray();
                    sourcePrograms.NativeDependencyPaths.UnionWith(possiblePaths);
                    var candidates = possiblePaths.Where(SourceExists).ToArray();
                    if (candidates.Length == 0) Uncertain("unresolved include " + path);
                    foreach (string candidate in candidates)
                        if (seenFiles.Add(candidate)) Scan(Read(candidate), Path.GetDirectoryName(candidate));
                }
            }
            void Visit(Shader current, string requiredPass = null)
            {
                if (current == null) { Uncertain("unresolved shader dependency"); return; }
                if (!requiredPasses.TryGetValue(current, out var required))
                    requiredPasses.Add(current, requiredPass == null ? null : new HashSet<string>(new[] { requiredPass }, StringComparer.OrdinalIgnoreCase));
                else if (requiredPass == null) requiredPasses[current] = null;
                else required?.Add(requiredPass);
                if (!seenShaders.Add(current)) return;
                sourcePrograms.NativeShaderDependencies.Add(current);
                if (seenShaders.Count > 64)
                { Uncertain("invalid or excessive shader dependencies"); return; }
                var discovered = Discover(current);
                if (discovered.HasError) { Uncertain("invalid or excessive shader dependencies"); return; }
                proof.Tokens.UnionWith(discovered.Properties); sourcePrograms.ShaderLabIdentifiers.UnionWith(discovered.Properties);
                string physical = discovered.PhysicalPath;
                bool generatedSource = discovered.Generated;
                string generatedText = discovered.GeneratedText;
                string directory = physical != null ? Path.GetDirectoryName(physical) : includeDirectory;
                var data = discovered.Data;
                bool editable = discovered.Editable;
                directories.Add(directory);
                if (editable)
                {
                    string source = generatedSource ? generatedText : Read(physical);
                    shaderSources[current] = source;
                    var facts = Artifacts.LabFacts(source);
                    if (facts.Issue != null) throw new InvalidOperationException(facts.Issue);
                    rasterStates.Add(current, facts.Rasterization);
                    sourcePrograms.ShaderLabIdentifiers.UnionWith(facts.Identifiers);
                    sourcePrograms.ShaderLabStateIdentifiers.UnionWith(facts.StateIdentifiers);
                    passSources.Add((source, directory));
                    foreach (Match dependency in ShaderDependency.Matches(source))
                    {
                        string name = dependency.Groups[3].Success ? dependency.Groups[3].Value : dependency.Groups[2].Value;
                        string importedPass = null;
                        if (dependency.Groups[1].Value.Equals("UsePass", StringComparison.OrdinalIgnoreCase))
                        {
                            sourcePrograms.ShaderWideKeywordOwners.Add(current);
                            int split = name.LastIndexOf('/');
                            if (split < 0) { Uncertain("invalid UsePass reference"); continue; }
                            importedPass = name.Substring(split + 1);
                            name = name.Substring(0, split);
                        }
                        Visit(Shader.Find(name), importedPass);
                    }
                }
                else
                {
                    // An importer can expose complete native programs without a
                    // ShaderLab file. Do not confuse source rewriting capability
                    // with input analysis or trust an unrelated TextAsset subasset.
                    if (!importedMetadata.TryGetValue(current, out var metadata))
                    {
                        using var cost = AnalysisCost.Measure("Shader discovery/imported metadata");
                        importedMetadata.Add(current, metadata = ShaderImportedMetadata.Read(current, data));
                    }
                    importedStates.Add(current, metadata);
                    sourcePrograms.ShaderLabIdentifiers.UnionWith(metadata.StateIdentifiers);
                    sourcePrograms.ShaderLabStateIdentifiers.UnionWith(metadata.StateIdentifiers);
                    proof.Tokens.UnionWith(metadata.StateIdentifiers);
                    if (metadata.StateIssue != null)
                    {
                        sourcePrograms.ShaderLabStatesComplete = false;
                        sourcePrograms.ShaderLabStateIssue ??= metadata.StateIssue;
                        proof.FixedFunctionUncertain ??= metadata.StateIssue;
                    }
                    // The snippet API does not share the imported pass index
                    // space. Its complete keyword universe is the safe fallback.
                    sourcePrograms.ShaderWideKeywordOwners.Add(current);
                    var nativeDependencies = CollectNativeShaderDependencies(current);
                    if (metadata.DependencyIssue == null)
                    {
                        foreach (var dependency in metadata.Dependencies)
                        {
                            Shader resolved = null;
                            foreach (var candidate in nativeDependencies)
                            {
                                if (candidate.name != dependency.Shader) continue;
                                if (resolved != null && resolved != candidate)
                                { Uncertain("ambiguous imported shader dependency: " + dependency.Shader); resolved = null; break; }
                                resolved = candidate;
                            }
                            // Resolve against actual imported objects, not a name
                            // lookup which could select a different shader asset.
                            if (resolved == null) { Uncertain("unresolved imported shader dependency: " + dependency.Shader); continue; }
                            Visit(resolved, dependency.Pass);
                        }
                    }
                    else
                    {
                        // Unknown metadata cannot narrow a dependency to one
                        // named pass. Inspect every native dependency instead.
                        foreach (var dependency in nativeDependencies)
                            if (dependency != current) Visit(dependency);
                    }
                }
                // Imported sources include generated surface-shader code and Unity's implicit
                // includes. For built-in shaders this also includes their fallback subshaders;
                // their declared properties supply the ShaderLab render-state inputs.
                if (discovered.PassIssue != null) Uncertain(discovered.PassIssue);
                foreach (var pass in discovered.Passes)
                {
                    passSources.Add((pass.Source, directory));
                    if (!pass.Pass.IsGrabPass) sourcePrograms.Passes.Add((current, pass.Pass, pass.Identifier));
                }
            }
            try
            {
                Visit(shader);
                // UsePass imports named programs, not every sibling pass in the
                // referenced shader. Source/property discovery remains unchanged.
                sourcePrograms.Passes.RemoveAll(p => requiredPasses.TryGetValue(p.Shader, out var names) && names != null && !names.Contains(p.Pass.Name));
                RegisterImportedNativePasses(sourcePrograms);
                foreach (var state in rasterStates)
                    sourcePrograms.DegenerateRasterizationIssue ??= state.Value.Issue(requiredPasses[state.Key]);
                foreach (var state in importedStates)
                    sourcePrograms.DegenerateRasterizationIssue ??= state.Value.RasterizationIssue(requiredPasses[state.Key]);
                // Discovery errors are distinct from a missing include in an inactive
                // branch. The latter may be resolved by Unity's variant preprocessor.
                sourcePrograms.Uncertain = proof.Uncertain;
                sourcePrograms.IncludeDirectories.UnionWith(directories);
                sourcePrograms.IncludeDirectories.Add(includeDirectory);
                foreach (var source in passSources) Scan(source.Source, source.Directory);
            }
            catch (Exception exception)
            {
                sourcePrograms.NativeDependenciesComplete = false;
                sourcePrograms.Uncertain ??= "cannot inspect shader source: " + exception.Message;
                Uncertain(sourcePrograms.Uncertain);
            }
            return proof;
        }

        internal static string StripComments(string text)
        {
            text = text.Replace("\\\r\n", "").Replace("\\\n", "");
            char[] result = null;
            for (int cursor = 0; cursor < text.Length;)
            {
                char current = text[cursor];
                if (current == '"' || current == '\'')
                {
                    int end = cursor + 1;
                    while (end < text.Length)
                    {
                        if (text[end] == current) { cursor = end + 1; break; }
                        if (text[end] == '\\')
                        {
                            if (end + 1 >= text.Length || text[end + 1] == '\n') break;
                            end += 2;
                        }
                        else end++;
                    }
                    if (cursor == end + 1) continue;
                }
                if (current != '/' || cursor + 1 >= text.Length) { cursor++; continue; }
                int stop;
                if (text[cursor + 1] == '/')
                {
                    stop = cursor + 2;
                    while (stop < text.Length && text[stop] != '\r' && text[stop] != '\n') stop++;
                }
                else if (text[cursor + 1] == '*')
                {
                    stop = text.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                    if (stop < 0) { cursor++; continue; }
                    stop += 2;
                }
                else { cursor++; continue; }
                result ??= text.ToCharArray();
                for (; cursor < stop; cursor++)
                    if (text[cursor] != '\r' && text[cursor] != '\n') result[cursor] = ' ';
            }
            return result == null ? text : new string(result);
        }

        internal static void ScanPasting(string source, Proof proof)
        {
            if (source.Contains("%:") || source.Contains("??="))
                proof.Uncertain ??= "unsupported preprocessor spelling";
            foreach (string line in source.Split('\n').Where(l => l.Contains("##")))
            {
                proof.TokenPasting = true;
                var define = Define.Match(line.TrimEnd('\r'));
                if (!define.Success) { proof.Uncertain ??= "unrecognized token paste"; continue; }
                if (define.Groups[1].Value.Contains("..."))
                { proof.Uncertain ??= "variadic token paste"; continue; }
                var parameters = new HashSet<string>(define.Groups[1].Value.Split(',').Select(p => p.Trim()));
                string body = define.Groups[2].Value;
                string remainder = Paste.Replace(body, match =>
                {
                    string pattern = "^" + string.Concat(Regex.Split(match.Value, @"\s*##\s*")
                        .Select(part => parameters.Contains(part) ? "[_A-Za-z0-9]*" : Regex.Escape(part))) + "$";
                    if (!proof.PastePatterns.ContainsKey(pattern)) proof.PastePatterns.Add(pattern, new Regex(pattern, RegexOptions.CultureInvariant));
                    return "";
                });
                if (remainder.Contains("##")) proof.Uncertain ??= "unsupported token paste expression";
            }
        }

        internal static string PhysicalPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            if (File.Exists(assetPath)) return Path.GetFullPath(assetPath);
            if (!assetPath.StartsWith("Packages/", StringComparison.Ordinal)) return null;
            var package = PackageInfo.FindForAssetPath(assetPath);
            if (package == null || !assetPath.StartsWith(package.assetPath + "/", StringComparison.Ordinal)) return null;
            return Path.GetFullPath(Path.Combine(package.resolvedPath, assetPath.Substring(package.assetPath.Length + 1)));
        }
    }
}
