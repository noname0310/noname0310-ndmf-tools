using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // An absence proof, not a shader compiler or an active-variant approximation. All source
    // branches, includes, passes, UsePass and fallback dependencies contribute possible names.
    // A declaration counts as usage. Missing/generated-only source keeps the binding alive.
    internal sealed class ShaderPropertyUsage
    {
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
        private static readonly Regex Comments = new(@"""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|//[^\r\n]*|/\*[\s\S]*?\*/", RegexOptions.CultureInvariant);
        private static readonly Regex Include = new(@"^\s*#\s*include(?:_with_pragmas)?\b([^\r\n]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex IncludePath = new(@"^\s*[""<]([^"">]+)["">]\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex ShaderDependency = new(@"\b(Fallback|UsePass)\s+""([^""]+)""|\bDependency\s+""[^""]+""\s*=\s*""([^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Define = new(@"^\s*#\s*define\s+[_A-Za-z][_A-Za-z0-9]*(?:\(([^)]*)\))?\s+(.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex Paste = new(@"[_A-Za-z0-9]+(?:\s*##\s*[_A-Za-z0-9]+)+", RegexOptions.CultureInvariant);
        private readonly Dictionary<Shader, Proof> cache = new();
        private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);
        internal int FilesRead => files.Count;

        internal Proof Get(Shader shader)
        {
            if (shader == null) return new Proof { Uncertain = "missing shader" };
            if (cache.TryGetValue(shader, out var cached)) return cached;
            var proof = new Proof(); cache.Add(shader, proof);
            var seenShaders = new HashSet<Shader>();
            var seenFiles = new HashSet<string>(StringComparer.Ordinal);
            var directories = new HashSet<string>(StringComparer.Ordinal);
            var passSources = new List<(string Source, string Directory)>();
            var includeDirectory = Path.Combine(EditorApplication.applicationContentsPath, "CGIncludes");
            void Uncertain(string reason) { proof.Uncertain ??= reason; }
            string Read(string path)
            {
                if (files.TryGetValue(path, out var source)) return source;
                if (files.Count >= 512 || new FileInfo(path).Length > 4 * 1024 * 1024)
                    throw new InvalidOperationException("shader source analysis limit reached");
                source = StripComments(File.ReadAllText(path)); files.Add(path, source); return source;
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
            void Visit(Shader current)
            {
                if (current == null) { Uncertain("unresolved shader dependency"); return; }
                if (!seenShaders.Add(current)) return;
                if (seenShaders.Count > 64 || ShaderUtil.ShaderHasError(current))
                { Uncertain("invalid or excessive shader dependencies"); return; }
                for (int i = 0; i < current.GetPropertyCount(); i++) proof.Tokens.Add(current.GetPropertyName(i));
                string assetPath = AssetDatabase.GetAssetPath(current), physical = PhysicalPath(assetPath);
                string directory = physical != null ? Path.GetDirectoryName(physical) : includeDirectory;
                bool builtin = assetPath == "Resources/unity_builtin_extra" || assetPath == "Library/unity default resources";
                if (!builtin && (physical == null || !File.Exists(physical) || Path.GetExtension(physical) != ".shader"))
                { Uncertain("shader source is unavailable: " + current.name); return; }
                if (!builtin)
                {
                    directories.Add(directory);
                    string source = Read(physical);
                    passSources.Add((source, directory));
                    foreach (Match dependency in ShaderDependency.Matches(source))
                    {
                        string name = dependency.Groups[3].Success ? dependency.Groups[3].Value : dependency.Groups[2].Value;
                        if (dependency.Groups[1].Value.Equals("UsePass", StringComparison.OrdinalIgnoreCase))
                        {
                            int split = name.LastIndexOf('/');
                            if (split < 0) { Uncertain("invalid UsePass reference"); continue; }
                            name = name.Substring(0, split);
                        }
                        Visit(Shader.Find(name));
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
                        var pass = subshader.GetPass(j);
                        string source = pass.SourceCode;
                        if (string.IsNullOrWhiteSpace(source))
                        { if (!pass.IsGrabPass) Uncertain("a shader pass has no inspectable source"); continue; }
                        passSources.Add((StripComments(source), directory));
                    }
                }
            }
            try
            {
                Visit(shader);
                foreach (var source in passSources) Scan(source.Source, source.Directory);
            }
            catch (Exception exception) { Uncertain("cannot inspect shader source: " + exception.Message); }
            return proof;
        }

        internal static string StripComments(string text)
        {
            text = text.Replace("\\\r\n", "").Replace("\\\n", "");
            return Comments.Replace(text, m => m.Value.StartsWith("//", StringComparison.Ordinal) || m.Value.StartsWith("/*", StringComparison.Ordinal)
                ? new string(m.Value.Select(c => c == '\r' || c == '\n' ? c : ' ').ToArray()) : m.Value);
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
