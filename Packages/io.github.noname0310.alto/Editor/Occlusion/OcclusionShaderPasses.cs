using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Occlusion
{
    // Inspect ShaderLab render states, not the meaning of a shader program.
    // UsePass and standard LightMode tags are engine contracts, not shader-name
    // compatibility rules. Unknown dependencies or cycles retain the surface.
    internal sealed class OcclusionShaderPasses
    {
        internal sealed class Pass
        {
            internal string Name, LightMode, States;
        }
        private sealed class Block
        {
            internal int Start, Length;
            internal string Text;
        }
        private static readonly Regex Programs = new(@"\b(CGPROGRAM|CGINCLUDE|HLSLPROGRAM|HLSLINCLUDE)\b[\s\S]*?\b(ENDCG|ENDHLSL)\b", RegexOptions.CultureInvariant);
        private static readonly Regex Strings = new(@"""(?:\\.|[^""\\])*""", RegexOptions.CultureInvariant);
        private static readonly Regex UsePass = new(@"\bUsePass\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private readonly Dictionary<Shader, List<List<Pass>>> cache = new();
        private readonly HashSet<Shader> pending = new();

        internal static string WithoutStrings(string text) => Strings.Replace(text, match => new string(' ', match.Length));

        internal List<List<Pass>> Get(Shader shader)
        {
            if (shader == null || ShaderUtil.ShaderHasError(shader)) return null;
            if (cache.TryGetValue(shader, out var found)) return found;
            if (pending.Count >= 32 || cache.Count >= 64 || !pending.Add(shader)) return null;
            List<List<Pass>> result = null;
            try { result = Read(shader); }
            catch (IOException) { }
            finally { pending.Remove(shader); cache[shader] = result; }
            return result;
        }

        private List<List<Pass>> Read(Shader shader)
        {
            string path = ShaderPropertyUsage.PhysicalPath(AssetDatabase.GetAssetPath(shader));
            if (path == null || !File.Exists(path) || Path.GetExtension(path) != ".shader" || new FileInfo(path).Length > 4 * 1024 * 1024) return null;
            string source = Programs.Replace(ShaderPropertyUsage.StripComments(File.ReadAllText(path)), "");
            if (Regex.IsMatch(WithoutStrings(source), @"\bDependency\b|\bGrabPass\b|^\s*#", RegexOptions.Multiline)) return null;
            var result = new List<List<Pass>>();
            foreach (var subshader in Blocks(source, "SubShader"))
            {
                var localPasses = Blocks(subshader.Text, "Pass").ToArray();
                var inherited = subshader.Text.ToCharArray();
                foreach (var block in localPasses) Blank(inherited, block.Start, block.Length);
                // Nested blocks such as Stencil/Tags remain in the inherited
                // state. Pass blocks and UsePass declarations do not.
                string parent = new(inherited);
                var imports = UsePass.Matches(parent).Cast<Match>().ToArray();
                foreach (var import in imports) Blank(inherited, import.Index, import.Length);
                parent = WithoutStrings(new string(inherited));
                var passes = localPasses.Select(block => new Pass
                {
                    Name = Regex.Match(block.Text, @"\bName\s+""([^""]+)""", RegexOptions.IgnoreCase).Groups[1].Value,
                    LightMode = Regex.Match(block.Text, @"""LightMode""\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase).Groups[1].Value,
                    States = parent + "\n" + WithoutStrings(block.Text)
                }).ToList();
                foreach (var import in imports)
                {
                    string name = import.Groups[1].Value; int split = name.LastIndexOf('/');
                    if (split <= 0) return null;
                    var imported = Get(Shader.Find(name.Substring(0, split))); if (imported == null) return null;
                    var matches = imported.SelectMany(group => group).Where(p => p.Name.Equals(name.Substring(split + 1), StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length == 0 || matches.Select(p => p.States).Distinct(StringComparer.Ordinal).Count() != 1 ||
                        matches.Select(p => p.LightMode).Distinct(StringComparer.Ordinal).Count() != 1) return null;
                    // Ambiguous source subshaders must agree at policy evaluation.
                    // Keep every possible imported version together with its own
                    // state, instead of assuming the first subshader is supported.
                    var match = matches[0];
                    passes.Add(new Pass { Name = match.Name, LightMode = match.LightMode, States = parent + "\n" + match.States });
                }
                result.Add(passes);
            }
            return result.Count == 0 ? null : result;
        }

        private static void Blank(char[] characters, int start, int length)
        {
            for (int i = start; i < start + length; i++) if (characters[i] != '\r' && characters[i] != '\n') characters[i] = ' ';
        }

        private static IEnumerable<Block> Blocks(string source, string keyword)
        {
            var masked = WithoutStrings(source);
            foreach (Match match in Regex.Matches(masked, @"\b" + keyword + @"\s*\{", RegexOptions.IgnoreCase))
            {
                int start = match.Index + match.Length, depth = 1, end = start;
                for (; end < masked.Length && depth > 0; end++)
                {
                    if (masked[end] == '{') depth++;
                    else if (masked[end] == '}') depth--;
                }
                if (depth != 0) throw new IOException("Unbalanced ShaderLab block.");
                yield return new Block { Start = match.Index, Length = end - match.Index, Text = source.Substring(start, end - start - 1) };
            }
        }
    }
}
