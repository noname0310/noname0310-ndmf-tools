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
    // A declaration counts as usage. Missing/generated-only source keeps the binding alive.
    internal sealed class ShaderPropertyUsage
    {
        internal sealed class Programs
        {
            internal readonly List<(Shader Shader, ShaderData.Pass Pass, UnityEngine.Rendering.PassIdentifier Identifier)> Passes = new();
            internal readonly HashSet<Shader> ShaderWideKeywordOwners = new();
            internal readonly HashSet<string> IncludeDirectories = new(StringComparer.Ordinal);
            internal readonly HashSet<string> ShaderLabIdentifiers = new(StringComparer.Ordinal);
            internal readonly HashSet<string> ShaderLabStateIdentifiers = new(StringComparer.Ordinal);
            internal bool ShaderLabStatesComplete = true;
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

            internal bool MayUse(string property)
            {
                if (Uncertain != null || string.IsNullOrEmpty(property) || property.Length > 128 ||
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
        private static readonly Regex ShaderDependency = new(@"\b(Fallback|UsePass)\s+""([^""]+)""|\bDependency\s+""[^""]+""\s*=\s*""([^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Define = new(@"^\s*#\s*define\s+[_A-Za-z][_A-Za-z0-9]*(?:\(([^)]*)\))?\s+(.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex Paste = new(@"[_A-Za-z0-9]+(?:\s*##\s*[_A-Za-z0-9]+)+", RegexOptions.CultureInvariant);
        private readonly Dictionary<Shader, Proof> cache = new();
        private readonly Dictionary<Shader, Programs> programs = new();
        private readonly Dictionary<(Shader, string, BuildTarget), bool> propertyReads = new();
        private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);
        private readonly Dictionary<(string, string), PpDocument> parsedPreprocessorDocuments = new();
        private readonly Dictionary<(Shader, Shader, string, string), ShaderPreprocessorGraph.Source[]> resolvedIncludes = new();
        private readonly Dictionary<(Shader Owner, string Source, string Query, string Context, string IncludeSearch), ShaderPreprocessorGraph> preprocessorGraphs = new();
        private readonly Dictionary<(Shader, int, int, bool), ShaderData.Pass> passSnapshots = new();
        private readonly Dictionary<ShaderData.Pass, string> passSources = new();
        private readonly Dictionary<(BuildTarget, ShaderCompilerPlatform, ShaderType, UnityEngine.Rendering.GraphicsTier?), Dictionary<string, string>> preprocessorContexts = new();
        internal int FilesRead => files.Count;
        internal double PreprocessorGraphMilliseconds { get; private set; }
        internal double PreprocessorDiscoveryMilliseconds { get; private set; }
        internal double PreprocessorEvaluationMilliseconds { get; private set; }
        internal int PreprocessorGraphs => preprocessorGraphs.Count;
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
                if (!Get(shader).MayUse(property)) return false;
                var program = GetPrograms(shader);
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
                if (!proof.MayUse(property)) propertyReads[(shader, property, target)] = false;
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

        private static void ReadShaderLabIdentifiers(string source, ISet<string> names, bool skipProperties = false)
        {
            var tokens = PpLexer.Lex(source);
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
            string physical = PhysicalPath(AssetDatabase.GetAssetPath(owner));
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
            string physical = PhysicalPath(AssetDatabase.GetAssetPath(owner));
            string directory = physical != null ? Path.GetDirectoryName(physical) :
                Path.Combine(EditorApplication.applicationContentsPath, "CGIncludes");
            var source = new ShaderPreprocessorGraph.Source(ProgramSourceId(owner), passSource);
            ShaderPreprocessorGraph.Source[] Includes(string origin, string include)
            {
                var includeKey = (root, owner, origin, include);
                if (resolvedIncludes.TryGetValue(includeKey, out var previousSources)) return previousSources;
                var roots = program.IncludeDirectories.Concat(new[] { Path.GetDirectoryName(origin), directory });
                var paths = roots.Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.GetFullPath(Path.Combine(p, include)))
                    .Concat(new[] { PhysicalPath(include) }).Where(p => p != null && File.Exists(p)).Distinct(StringComparer.Ordinal).ToArray();
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
            var graph = demand == null ? ShaderPreprocessorGraph.Analyze(source, Includes, query, owner.keywordSpace.keywords.Select(k => k.name), definitions, parsedPreprocessorDocuments) :
                ShaderPreprocessorGraph.AnalyzeDemand(source, Includes, demand, owner.keywordSpace.keywords.Select(k => k.name), definitions, parsedPreprocessorDocuments);
            PreprocessorGraphMilliseconds += timer.Elapsed.TotalMilliseconds;
            PreprocessorDiscoveryMilliseconds += graph.DiscoveryMilliseconds;
            PreprocessorEvaluationMilliseconds += graph.EvaluationMilliseconds;
            preprocessorGraphs.Add(key, graph); return graph;
        }

        internal Proof Get(Shader shader)
        {
            if (shader == null) return new Proof { Uncertain = "missing shader" };
            if (cache.TryGetValue(shader, out var cached)) return cached;
            var proof = new Proof(); cache.Add(shader, proof);
            var sourcePrograms = new Programs(); programs.Add(shader, sourcePrograms);
            var seenShaders = new HashSet<Shader>();
            var requiredPasses = new Dictionary<Shader, HashSet<string>>();
            var seenFiles = new HashSet<string>(StringComparer.Ordinal);
            var directories = new HashSet<string>(StringComparer.Ordinal);
            var passSources = new List<(string Source, string Directory)>();
            var includeDirectory = Path.Combine(EditorApplication.applicationContentsPath, "CGIncludes");
            void Uncertain(string reason) { proof.Uncertain ??= reason; }
            string Read(string path)
            {
                if (files.TryGetValue(path, out var source)) return StripComments(source);
                if (files.Count >= 512 || new FileInfo(path).Length > 4 * 1024 * 1024)
                    throw new InvalidOperationException("shader source analysis limit reached");
                source = File.ReadAllText(path); files.Add(path, source); return StripComments(source);
            }
            void Scan(string text, string directory)
            {
                foreach (Match token in Tokens.Matches(text))
                    if (token.Value[0] != '"' && token.Value[0] != '\'') proof.Tokens.Add(token.Value);
                proof.TokenPasting |= text.Contains("##");
                ScanPasting(text, proof);
                foreach (Match include in Include.Matches(text))
                {
                    var match = IncludePath.Match(include.Groups[1].Value);
                    if (!match.Success) { Uncertain("macro or unsupported include path"); continue; }
                    string path = match.Groups[1].Value.Replace('\\', '/');
                    var roots = directories.Concat(new[] { directory, includeDirectory }).Where(d => !string.IsNullOrEmpty(d));
                    var candidates = roots.Select(d => Path.GetFullPath(Path.Combine(d, path)))
                        .Concat(new[] { PhysicalPath(path) }).Where(p => p != null && File.Exists(p)).Distinct(StringComparer.Ordinal).ToArray();
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
                if (seenShaders.Count > 64 || ShaderUtil.ShaderHasError(current))
                { Uncertain("invalid or excessive shader dependencies"); return; }
                for (int i = 0; i < current.GetPropertyCount(); i++)
                { proof.Tokens.Add(current.GetPropertyName(i)); sourcePrograms.ShaderLabIdentifiers.Add(current.GetPropertyName(i)); }
                string assetPath = AssetDatabase.GetAssetPath(current), physical = PhysicalPath(assetPath);
                string directory = physical != null ? Path.GetDirectoryName(physical) : includeDirectory;
                bool builtin = assetPath == "Resources/unity_builtin_extra" || assetPath == "Library/unity default resources";
                if (builtin) sourcePrograms.ShaderLabStatesComplete = false;
                if (builtin) sourcePrograms.ShaderWideKeywordOwners.Add(current);
                if (!builtin && (physical == null || !File.Exists(physical) || Path.GetExtension(physical) != ".shader"))
                { Uncertain("shader source is unavailable: " + current.name); return; }
                if (!builtin)
                {
                    directories.Add(directory);
                    string source = Read(physical);
                    ReadShaderLabIdentifiers(source, sourcePrograms.ShaderLabIdentifiers);
                    ReadShaderLabIdentifiers(source, sourcePrograms.ShaderLabStateIdentifiers, true);
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
                // Imported sources include generated surface-shader code and Unity's implicit
                // includes. For built-in shaders this also includes their fallback subshaders;
                // their declared properties supply the ShaderLab render-state inputs.
                var data = ShaderUtil.GetShaderData(current);
                int count = builtin ? data.SubshaderCount : data.SerializedSubshaderCount;
                // Unity 2022 can report more serialized subshaders than its inspection API
                // exposes (for example platform-stripped subshaders). Calling GetPass on
                // those entries logs an Editor error instead of throwing. Keep the output.
                if (!builtin && count > data.SubshaderCount)
                { Uncertain("not all serialized subshaders are inspectable"); return; }
                if (count == 0) Uncertain("shader contains no inspectable subshaders");
                for (int i = 0; i < count; i++)
                {
                    var subshader = builtin ? data.GetSubshader(i) : data.GetSerializedSubshader(i);
                    for (int j = 0; j < subshader.PassCount; j++)
                    {
                        var passKey = (current, i, j, builtin);
                        if (!passSnapshots.TryGetValue(passKey, out var pass)) passSnapshots.Add(passKey, pass = subshader.GetPass(j));
                        string source = PassSource(pass);
                        if (string.IsNullOrWhiteSpace(source))
                        { if (!pass.IsGrabPass) Uncertain("a shader pass has no inspectable source"); continue; }
                        passSources.Add((StripComments(source), directory));
                        if (!pass.IsGrabPass) sourcePrograms.Passes.Add((current, pass, new UnityEngine.Rendering.PassIdentifier((uint)i, (uint)j)));
                    }
                }
            }
            try
            {
                Visit(shader);
                // UsePass imports named programs, not every sibling pass in the
                // referenced shader. Source/property discovery remains unchanged.
                sourcePrograms.Passes.RemoveAll(p => requiredPasses.TryGetValue(p.Shader, out var names) && names != null && !names.Contains(p.Pass.Name));
                // Discovery errors are distinct from a missing include in an inactive
                // branch. The latter may be resolved by Unity's variant preprocessor.
                sourcePrograms.Uncertain = proof.Uncertain;
                sourcePrograms.IncludeDirectories.UnionWith(directories);
                sourcePrograms.IncludeDirectories.Add(includeDirectory);
                foreach (var source in passSources) Scan(source.Source, source.Directory);
            }
            catch (Exception exception)
            {
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
