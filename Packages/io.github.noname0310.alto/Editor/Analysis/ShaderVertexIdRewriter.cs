using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // A source adapter for explicit vertex entry points. Unsupported source forms
    // produce no adapter; they can still use an unchanged, identity-numbered mesh.
    // Source spans, parameter scopes and semantic declarations determine edits.
    // Identifier replacement alone is not a safe way to rewrite system inputs.
    internal sealed class ShaderVertexIdRewriter
    {
        internal const string OffsetProperty = "__ALTO_VertexIDOffset_28F97C";
        private const string OriginalAttribute = "__altoOriginalVertexID_28F97C";
        private readonly string source;
        private readonly Layout layout;
        private readonly List<(int Offset, int Length, string Prefix, string Body, string Suffix)> programs;
        internal readonly Shader Original;

        private ShaderVertexIdRewriter(Shader shader, string source, Layout layout,
            List<(int, int, string, string, string)> programs)
        { Original = shader; this.source = source; this.layout = layout; this.programs = programs; }

        // ShaderLab words inside comments and strings are not declarations. Keep
        // original source spans so edits never rewrite names in properties/tags.
        private sealed class Layout
        {
            internal PpToken Name;
            internal int Body, Properties = -1;
            internal readonly List<(int Offset, int Length, string Prefix, string Body, string Suffix)> Programs = new();
            internal static Layout Parse(string source)
            {
                var tokens = PpLexer.Lex(source, trackOffsets: true).Where(t => t.Kind != PpTokenKind.Newline).ToArray();
                if (tokens.Length < 4 || tokens[0].Text != "Shader" || tokens[1].Kind != PpTokenKind.String || tokens[2].Text != "{")
                    throw new InvalidOperationException("unsupported ShaderLab header");
                var result = new Layout { Name = tokens[1], Body = tokens[2].Offset + tokens[2].Length };
                for (int i = 3; i < tokens.Length; i++)
                {
                    var token = tokens[i];
                    if (token.Kind != PpTokenKind.Identifier) continue;
                    if (token.Text == "CGINCLUDE" || token.Text == "HLSLINCLUDE")
                        throw new InvalidOperationException("shared ShaderLab program blocks need a separate input adapter");
                    if (token.Text == "CGPROGRAM" || token.Text == "HLSLPROGRAM")
                    {
                        string suffix = token.Text == "CGPROGRAM" ? "ENDCG" : "ENDHLSL";
                        int end = i + 1;
                        while (end < tokens.Length && (tokens[end].Kind != PpTokenKind.Identifier || tokens[end].Text != suffix)) end++;
                        if (end == tokens.Length) throw new InvalidOperationException("unterminated ShaderLab program");
                        int begin = token.Offset + token.Length;
                        result.Programs.Add((token.Offset, tokens[end].Offset + tokens[end].Length - token.Offset,
                            token.Text, source.Substring(begin, tokens[end].Offset - begin), suffix));
                        i = end; continue;
                    }
                    if (string.Equals(token.Text, "UsePass", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(token.Text, "Dependency", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(token.Text, "Fallback", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Length && tokens[i + 1].Kind == PpTokenKind.String)
                        throw new InvalidOperationException("shader dependencies need a separate input adapter");
                    if (token.Text != "Properties" || i + 1 == tokens.Length || tokens[i + 1].Text != "{") continue;
                    if (result.Properties >= 0) throw new InvalidOperationException("ambiguous ShaderLab properties block");
                    result.Properties = tokens[i + 1].Offset + tokens[i + 1].Length;
                }
                if (result.Programs.Count == 0) throw new InvalidOperationException("no explicit ShaderLab programs");
                return result;
            }
        }

        internal static bool TryCreate(Shader shader, out ShaderVertexIdRewriter result, out string reason)
        {
            result = null; reason = null;
            try
            {
                string path = ShaderPropertyUsage.PhysicalPath(AssetDatabase.GetAssetPath(shader));
                if (path == null || !File.Exists(path) || Path.GetExtension(path) != ".shader")
                    throw new InvalidOperationException("editable ShaderLab source is unavailable");
                string text = File.ReadAllText(path);
                var layout = Layout.Parse(text);
                if (PpLexer.Lex(text).Any(t => t.Kind == PpTokenKind.Identifier && (t.Text == OffsetProperty || t.Text == OriginalAttribute)))
                    throw new InvalidOperationException("an existing vertex-ID adapter must retain its mesh layout");
                var blocks = new List<(int, int, string, string, string)>();
                foreach (var block in layout.Programs)
                {
                    var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string body = Expand(block.Body, Path.GetDirectoryName(path), stack, 0);
                    if (!RewriteProgram(body, 0, false, -1, out _, out string issue, shader.keywordSpace.keywords.Select(k => k.name)))
                        throw new InvalidOperationException(issue);
                    blocks.Add((block.Offset, block.Length, block.Prefix, body, block.Suffix));
                }
                result = new ShaderVertexIdRewriter(shader, text, layout, blocks); return true;
            }
            catch (Exception exception) { reason = "vertex-ID adapter: " + exception.Message; return false; }
        }

        private static string Expand(string body, string directory, HashSet<string> stack, int depth)
        {
            if (depth > 32 || body.Length > 4 * 1024 * 1024) throw new InvalidOperationException("source expansion budget exceeded");
            string engine = Path.GetFullPath(Path.Combine(EditorApplication.applicationContentsPath, "CGIncludes"));
            var tokens = PpLexer.Lex(body, trackOffsets: true);
            var edits = new List<(int Begin, int End, string Text)>();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Text != "#" || i + 1 == tokens.Count) continue;
                int end = i + 1;
                while (end < tokens.Count && tokens[end].Kind != PpTokenKind.Newline) end++;
                if (tokens[i + 1].Text == "include_with_pragmas")
                    throw new InvalidOperationException("pragma includes need a separate source adapter");
                if (tokens[i + 1].Text != "include") { i = end; continue; }
                var arguments = tokens.Skip(i + 2).Take(end - i - 2).ToArray();
                string include;
                if (arguments.Length == 1 && arguments[0].Kind == PpTokenKind.String && arguments[0].Text.StartsWith("\"", StringComparison.Ordinal))
                    include = arguments[0].Text.Substring(1, arguments[0].Text.Length - 2);
                else if (arguments.Length >= 3 && arguments[0].Text == "<" && arguments[arguments.Length - 1].Text == ">")
                    include = string.Concat(arguments.Skip(1).Take(arguments.Length - 2).Select(t => t.Text));
                else throw new InvalidOperationException("macro-generated include needs a separate source adapter");
                var paths = new[] { Path.Combine(directory, include), ShaderPropertyUsage.PhysicalPath(include), Path.Combine(engine, include) };
                string path = paths.Where(p => p != null).Select(Path.GetFullPath).FirstOrDefault(File.Exists);
                if (path == null) throw new InvalidOperationException("unresolved include: " + include);
                // Engine includes do not define user entry points. Keep them in their
                // original compiler environment; reject hidden system-ID declarations.
                string content = File.ReadAllText(path);
                string replacement;
                if (path.StartsWith(engine + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    if (PpLexer.Lex(content).Any(t => t.Kind == PpTokenKind.Identifier && ShaderInputQuery.VertexId.MatchesToken(t.Text)))
                        throw new InvalidOperationException("engine include declares a vertex-ID interface");
                    replacement = "\n#include \"" + path.Replace('\\', '/') + "\"\n";
                }
                else
                {
                    if (!stack.Add(path)) throw new InvalidOperationException("recursive source include");
                    replacement = "\n" + Expand(content, Path.GetDirectoryName(path), stack, depth + 1) + "\n";
                    stack.Remove(path);
                }
                edits.Add((tokens[i].Offset, tokens[end - 1].Offset + tokens[end - 1].Length, replacement));
                i = end;
            }
            foreach (var edit in edits.OrderByDescending(e => e.Begin)) body = body.Remove(edit.Begin, edit.End - edit.Begin).Insert(edit.Begin, edit.Text);
            if (body.Length > 4 * 1024 * 1024) throw new InvalidOperationException("source expansion budget exceeded");
            return body;
        }

        internal string GenerateSource(int offset, bool uniform, int attributeChannel = -1)
        {
            string text = source;
            foreach (var block in programs.OrderByDescending(b => b.Offset))
            {
                if (!RewriteProgram(block.Body, offset, uniform, attributeChannel, out string body, out string issue, Original.keywordSpace.keywords.Select(k => k.name)))
                    throw new InvalidOperationException(issue);
                text = text.Remove(block.Offset, block.Length).Insert(block.Offset, block.Prefix + "\n" + body + "\n" + block.Suffix);
            }
            if (uniform && attributeChannel < 0)
            {
                string property = "\n[HideInInspector] " + OffsetProperty + " (\"Original vertex ID offset\", Integer) = 0\n";
                // Properties precede programs in ShaderLab, so program edits do
                // not move this insertion point or the shader-name token.
                if (layout.Properties >= 0 && layout.Programs.Any(p => p.Offset < layout.Properties))
                    throw new InvalidOperationException("properties must precede shader programs");
                text = layout.Properties >= 0 ? text.Insert(layout.Properties, property) : text.Insert(layout.Body, "\nProperties {" + property + "}\n");
            }
            using var sha = SHA256.Create();
            string key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").Substring(0, 24);
            return text.Remove(layout.Name.Offset, layout.Name.Length).Insert(layout.Name.Offset, "\"Hidden/ALTO/VertexID/" + key + "\"");
        }

        internal Shader CreateShader(int offset, bool uniform, int attributeChannel = -1)
        {
            var shader = ShaderUtil.CreateShaderAsset(GenerateSource(offset, uniform, attributeChannel), true);
            if (shader == null || ShaderUtil.ShaderHasError(shader))
            {
                string message = shader == null ? "no shader" : string.Join("; ", ShaderUtil.GetShaderMessages(shader).Select(m => m.message));
                if (shader != null) UnityEngine.Object.DestroyImmediate(shader);
                throw new InvalidOperationException("Vertex-ID adapter failed to compile: " + message);
            }
            return shader;
        }

        internal static bool RewriteProgram(string source, int offset, bool uniform, int attributeChannel,
            out string result, out string reason, IEnumerable<string> keywords = null)
        {
            result = null; reason = null;
            try
            {
                var tokens = PpLexer.Lex(source, trackOffsets: true);
                if (tokens.Any(t => t.Text == OffsetProperty || t.Text == OriginalAttribute))
                    throw new InvalidOperationException("generated input-adapter identifiers conflict with source identifiers");
                if (tokens.Any(t => t.Text == "__LINE__" || t.Text == "__FILE__" || t.Text == "__COUNTER__"))
                    throw new InvalidOperationException("source-location-sensitive programs need a separate adapter");
                var code = new List<PpToken>(); var entries = new HashSet<string>(StringComparer.Ordinal);
                var macros = new HashSet<string>(StringComparer.Ordinal); var directives = new List<(int Begin, int End)>();
                for (int i = 0; i < tokens.Count;)
                {
                    if (tokens[i].Text == "#")
                    {
                        int end = i + 1; while (end < tokens.Count && tokens[end].Kind != PpTokenKind.Newline) end++;
                        var directive = tokens.Skip(i + 1).Take(end - i - 1).ToArray();
                        if (directive.Any(t => ShaderInputQuery.VertexId.MatchesToken(t.Text)))
                            throw new InvalidOperationException("macro-generated vertex-ID declarations require a separate adapter");
                        if (directive.Length > 2 && directive[0].Text == "pragma" && directive[1].Text == "vertex")
                        {
                            if (directive.Length != 3 || directive[2].Kind != PpTokenKind.Identifier)
                                throw new InvalidOperationException("unsupported vertex-entry pragma");
                            entries.Add(directive[2].Text);
                        }
                        if (directive.Length > 1 && directive[0].Text == "pragma" && directive[1].Text == "surface")
                            throw new InvalidOperationException("generated surface-shader entry points require a separate adapter");
                        if (directive.Length > 1 && directive[0].Text == "define") macros.Add(directive[1].Text);
                        directives.Add((tokens[i].Offset, end < tokens.Count ? tokens[end - 1].Offset + tokens[end - 1].Length : source.Length));
                        i = end;
                    }
                    else { if (tokens[i].Kind != PpTokenKind.Newline) code.Add(tokens[i]); i++; }
                }
                // Remove only explicit semantic tokens, then prove that preprocessing
                // cannot synthesize another declaration through aliases or token pasting.
                // This also covers programs with no literal SV_VertexID in their source.
                string remaining = source;
                foreach (var token in code.Where(t => ShaderInputQuery.VertexId.MatchesToken(t.Text)).OrderByDescending(t => t.Offset))
                    remaining = remaining.Remove(token.Offset, token.Length).Insert(token.Offset, "TEXCOORD65500");
                ShaderPreprocessorGraph.Source[] Resolve(string origin, string include)
                {
                    string engine = Path.Combine(EditorApplication.applicationContentsPath, "CGIncludes");
                    var paths = new[] { Path.Combine(Path.GetDirectoryName(origin) ?? engine, include), ShaderPropertyUsage.PhysicalPath(include), Path.Combine(engine, include) };
                    ShaderPreprocessorGraph.Source Read(string path)
                    {
                        string content = File.ReadAllText(path);
                        if (PpLexer.Lex(content).Any(t => t.Text == "__LINE__" || t.Text == "__FILE__" || t.Text == "__COUNTER__"))
                            throw new InvalidOperationException("source-location-sensitive includes need a separate adapter");
                        return new ShaderPreprocessorGraph.Source(path, content);
                    }
                    return paths.Where(p => p != null).Select(Path.GetFullPath).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(Read).ToArray();
                }
                var absent = ShaderPreprocessorGraph.Analyze(new ShaderPreprocessorGraph.Source(
                    Path.Combine(EditorApplication.applicationContentsPath, "CGIncludes", "__ALTO_ADAPTER__"), remaining), Resolve,
                    ShaderInputQuery.VertexId, keywords ?? Array.Empty<string>());
                if (!absent.InputAbsent) throw new InvalidOperationException("preprocessing may introduce an unhandled vertex-ID declaration: " + absent.Issue);
                if (!code.Any(t => ShaderInputQuery.VertexId.MatchesToken(t.Text))) { result = source; return true; }
                if (entries.Count == 0 || entries.Any(macros.Contains) || code.Any(t => t.Text == "__LINE__" || t.Text == "__FILE__" || t.Text == "__COUNTER__"))
                    throw new InvalidOperationException("vertex entry or source-location contract is not explicit");
                var program = new ShaderInputProgram(code);
                if (program.Issue != null) throw new InvalidOperationException(program.Issue);
                var semanticTokens = new HashSet<int>();
                var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var pair in program.Structs)
                {
                    var names = new List<string>(); fields.Add(pair.Key, names);
                    for (int i = pair.Value.Begin; i < pair.Value.End; i++)
                    {
                        if (!ShaderInputQuery.VertexId.MatchesToken(program.At(i))) continue;
                        if (i < 3 || program.At(i - 1) != ":" || program.At(i - 3) != "uint" || program.At(i + 1) != ";" || macros.Contains(program.At(i - 2)))
                            throw new InvalidOperationException("unsupported vertex-ID member declaration");
                        if (directives.Any(d => d.Begin > code[pair.Value.Begin - 1].Offset && d.Begin < code[pair.Value.End].Offset))
                            throw new InvalidOperationException("conditional vertex-ID structure layout needs a separate adapter");
                        names.Add(program.At(i - 2)); semanticTokens.Add(i);
                    }
                }
                var edits = new List<(int Offset, string Text)>(); int changed = 0;
                foreach (string entry in entries)
                {
                    if (!program.Functions.TryGetValue(entry, out var functions)) throw new InvalidOperationException("unresolved vertex entry: " + entry);
                    if (functions.Count != 1) throw new InvalidOperationException("conditional or overloaded vertex entry");
                    var function = functions[0];
                    if (!function.SignatureComplete || function.Parameters.Any(p => p.Output || macros.Contains(p.Type) || macros.Contains(p.Name)))
                        throw new InvalidOperationException("unsupported vertex-entry signature");
                    int arguments = function.Header; while (program.At(arguments) != "(") arguments++;
                    int close = program.Partner[arguments];
                    if (code.Where((t, i) => t.Text == entry && program.At(i + 1) == "(").Count() != 1)
                        throw new InvalidOperationException("vertex entry is also called as a helper");
                    if (directives.Any(d => d.Begin > code[arguments].Offset && d.Begin < code[close].Offset))
                        throw new InvalidOperationException("conditional vertex-entry parameters");
                    var parameters = program.Split(arguments + 1, close).ToArray();
                    var writes = new List<string>();
                    for (int p = 0; p < function.Parameters.Length; p++)
                    {
                        var parameter = function.Parameters[p]; var range = parameters[p];
                        if (parameter.ArrayLength != 0 || Enumerable.Range(range.Begin, range.End - range.Begin).Any(i => program.At(i) == "const"))
                            throw new InvalidOperationException("array or const vertex-entry parameters");
                        for (int i = range.Begin; i < range.End; i++)
                            if (ShaderInputQuery.VertexId.MatchesToken(program.At(i)))
                            {
                                if (parameter.Type != "uint" || program.At(i - 1) != ":") throw new InvalidOperationException("unsupported vertex-ID parameter");
                                semanticTokens.Add(i); writes.Add(parameter.Name);
                            }
                        if (fields.TryGetValue(parameter.Type, out var members)) writes.AddRange(members.Select(f => parameter.Name + "." + f));
                    }
                    if (writes.Count == 0) continue;
                    if (attributeChannel >= 0)
                    {
                        string semantic = "TEXCOORD" + attributeChannel;
                        if (code.Any(t => string.Equals(t.Text, semantic, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("original-ID attribute conflicts with a shader interface");
                        edits.Add((code[close].Offset, ", float2 " + OriginalAttribute + " : " + semantic));
                    }
                    string value(string original) => attributeChannel >= 0 ? "(uint)" + OriginalAttribute + ".x" :
                        original + " + " + (uniform ? OffsetProperty : unchecked((uint)offset).ToString(CultureInfo.InvariantCulture) + "u");
                    edits.Add((code[function.Begin - 1].Offset + code[function.Begin - 1].Length,
                        "\n" + string.Join("\n", writes.Select(w => w + " = " + value(w) + ";")) + "\n"));
                    changed++;
                }
                if (changed == 0 || code.Where((t, i) => ShaderInputQuery.VertexId.MatchesToken(t.Text) && !semanticTokens.Contains(i)).Any())
                    throw new InvalidOperationException("not every vertex-ID declaration is covered by the adapter");
                result = source;
                foreach (var edit in edits.OrderByDescending(e => e.Offset)) result = result.Insert(edit.Offset, edit.Text);
                if (uniform && attributeChannel < 0) result = "int " + OffsetProperty + ";\n" + result;
                return true;
            }
            catch (Exception exception) { reason = exception.Message; return false; }
        }
    }
}
