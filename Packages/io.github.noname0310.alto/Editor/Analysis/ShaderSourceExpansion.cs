using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace Noname.AvatarTools.Analysis
{
    // Source expansion is shared; each rewrite supplies its own semantic guards.
    internal static class ShaderSourceExpansion
    {
        internal static string Expand(string body, string directory, HashSet<string> stack, int depth,
            bool retainUnresolvedIncludes = false, bool rejectEngineVertexId = false)
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
                if (path == null)
                {
                    // Preserve optional includes in their original branches.
                    // The caller owns semantic and compiler validation.
                    if (retainUnresolvedIncludes) { i = end; continue; }
                    throw new InvalidOperationException("unresolved include: " + include);
                }
                // Engine includes do not define user entry points. Keep them in their
                // original compiler environment; reject hidden system-ID declarations.
                string content = File.ReadAllText(path);
                string replacement;
                if (path.StartsWith(engine + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    if (rejectEngineVertexId && PpLexer.Lex(content).Any(t => t.Kind == PpTokenKind.Identifier && ShaderInputQuery.VertexId.MatchesToken(t.Text)))
                        throw new InvalidOperationException("engine include declares a vertex-ID interface");
                    replacement = "\n#include \"" + path.Replace('\\', '/') + "\"\n";
                }
                else
                {
                    if (!stack.Add(path)) throw new InvalidOperationException("recursive source include");
                    replacement = "\n" + Expand(content, Path.GetDirectoryName(path), stack, depth + 1, retainUnresolvedIncludes, rejectEngineVertexId) + "\n";
                    stack.Remove(path);
                }
                edits.Add((tokens[i].Offset, tokens[end - 1].Offset + tokens[end - 1].Length, replacement));
                i = end;
            }
            foreach (var edit in edits.OrderByDescending(e => e.Begin)) body = body.Remove(edit.Begin, edit.End - edit.Begin).Insert(edit.Begin, edit.Text);
            if (body.Length > 4 * 1024 * 1024) throw new InvalidOperationException("source expansion budget exceeded");
            return body;
        }

    }
}
