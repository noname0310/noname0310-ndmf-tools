using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Noname.AvatarTools.Analysis
{
    // Conservative rasterization may cover pixels for a degenerate triangle.
    // Read ShaderLab state, not HLSL identifiers or material default guesses.
    // Named imports inspect the selected passes; fallback shaders inspect all.
    internal sealed class ShaderRasterizationState
    {
        private sealed class Pass
        {
            internal string Name;
            internal bool? Conservative;
        }

        private readonly List<Pass> passes = new();
        private string issue;
        private bool unsafeImport;
        private bool grabPass;

        internal string Issue(ISet<string> selectedPasses = null)
        {
            if (issue != null) return issue;
            if (grabPass) return "a GrabPass has observable drawing side effects";
            if (unsafeImport) return "imported pass has an unverified inherited conservative raster state";
            var selected = selectedPasses == null ? passes : passes.Where(p => p.Name != null && selectedPasses.Contains(p.Name));
            if (selectedPasses != null && selectedPasses.Any(n => !passes.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))))
                return "named pass raster state is unavailable";
            return selected.Any(p => p.Conservative != false) ? "a pass may use conservative rasterization" : null;
        }

        internal static ShaderRasterizationState Parse(string source)
        {
            try { return Parse(PpLexer.Lex(source)); }
            catch (Exception exception) { return new ShaderRasterizationState { issue = "unverified shader raster state: " + exception.Message }; }
        }

        internal static ShaderRasterizationState Parse(IReadOnlyList<PpToken> tokens)
        {
            var result = new ShaderRasterizationState();
            try { result.Read(tokens); }
            catch (Exception exception) { result.issue = "unverified shader raster state: " + exception.Message; }
            return result;
        }

        internal void Write(BinaryWriter writer)
        {
            writer.Write(issue ?? ""); writer.Write(unsafeImport); writer.Write(grabPass); writer.Write(passes.Count);
            foreach (var pass in passes)
            { writer.Write(pass.Name != null); if (pass.Name != null) writer.Write(pass.Name); writer.Write(pass.Conservative.HasValue); if (pass.Conservative.HasValue) writer.Write(pass.Conservative.Value); }
        }

        internal static ShaderRasterizationState Read(BinaryReader reader)
        {
            var result = new ShaderRasterizationState { issue = reader.ReadString(), unsafeImport = reader.ReadBoolean(), grabPass = reader.ReadBoolean() };
            if (result.issue.Length == 0) result.issue = null;
            int count = ContentFactCache.Count(reader);
            for (int i = 0; i < count; i++) result.passes.Add(new Pass { Name = reader.ReadBoolean() ? reader.ReadString() : null,
                Conservative = reader.ReadBoolean() ? reader.ReadBoolean() : (bool?)null });
            return result;
        }

        private void Read(IReadOnlyList<PpToken> raw)
        {
            var tokens = new List<PpToken>();
            for (int i = 0; i < raw.Count; i++)
            {
                var token = raw[i];
                if (token.Kind == PpTokenKind.Newline) continue;
                if (token.Kind == PpTokenKind.Identifier && (token.Text == "CGPROGRAM" || token.Text == "CGINCLUDE" ||
                    token.Text == "HLSLPROGRAM" || token.Text == "HLSLINCLUDE"))
                {
                    string end = token.Text.StartsWith("CG", StringComparison.Ordinal) ? "ENDCG" : "ENDHLSL";
                    // A subshader-level surface program inherits subshader state.
                    tokens.Add(new PpToken(token.Text.EndsWith("PROGRAM", StringComparison.Ordinal) ? "__Program" : "__Include", PpTokenKind.Identifier));
                    while (++i < raw.Count && (raw[i].Kind != PpTokenKind.Identifier || raw[i].Text != end)) { }
                    if (i == raw.Count) throw new InvalidOperationException("unterminated shader program");
                }
                else tokens.Add(token);
            }
            var partner = new int[tokens.Count]; var stack = new Stack<int>();
            for (int i = 0; i < tokens.Count; i++)
            {
                partner[i] = -1;
                if (tokens[i].Text == "{") stack.Push(i);
                else if (tokens[i].Text == "}")
                {
                    if (stack.Count == 0) throw new InvalidOperationException("unbalanced ShaderLab block");
                    int open = stack.Pop(); partner[open] = i; partner[i] = open;
                }
            }
            if (stack.Count != 0) throw new InvalidOperationException("unbalanced ShaderLab block");
            bool foundShader = false, foundSubshader = false;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Text != "Shader" || tokens[i].Kind != PpTokenKind.Identifier) continue;
                if (foundShader || i + 2 >= tokens.Count || tokens[i + 1].Kind != PpTokenKind.String || tokens[i + 2].Text != "{")
                    throw new InvalidOperationException("unexpected shader declaration");
                foundShader = true; int shaderEnd = partner[i + 2];
                for (int j = i + 3; j < shaderEnd; j++)
                {
                    if (tokens[j].Text == "SubShader" && tokens[j].Kind == PpTokenKind.Identifier)
                    {
                        if (j + 1 >= shaderEnd || tokens[j + 1].Text != "{") throw new InvalidOperationException("invalid subshader");
                        int end = partner[j + 1]; foundSubshader = true;
                        ReadSubshader(j + 2, end); j = end;
                    }
                    else if (tokens[j].Text == "{") j = partner[j];
                }
                i = shaderEnd;
            }
            if (!foundShader || !foundSubshader) throw new InvalidOperationException("no shader/subshader source");

            bool? State(int begin, int end, bool? inherited)
            {
                bool? value = inherited;
                for (int i = begin; i < end; i++)
                {
                    if (tokens[i].Text == "{") { i = partner[i]; continue; }
                    if (tokens[i].Kind != PpTokenKind.Identifier || !tokens[i].Text.Equals("Conservative", StringComparison.OrdinalIgnoreCase)) continue;
                    if (i + 1 >= end) return null;
                    string setting = tokens[++i].Text;
                    value = setting.Equals("False", StringComparison.OrdinalIgnoreCase) || setting == "0" ? false :
                        setting.Equals("True", StringComparison.OrdinalIgnoreCase) || setting == "1" ? true : (bool?)null;
                }
                return value;
            }
            void ReadSubshader(int begin, int end)
            {
                bool? inherited = State(begin, end, false);
                for (int i = begin; i < end; i++)
                {
                    var token = tokens[i];
                    if (token.Kind == PpTokenKind.Identifier && token.Text == "Pass")
                    {
                        if (i + 1 >= end || tokens[i + 1].Text != "{") throw new InvalidOperationException("invalid pass");
                        int stop = partner[i + 1]; string name = null;
                        for (int j = i + 2; j < stop; j++)
                        {
                            if (tokens[j].Text == "{") { j = partner[j]; continue; }
                            if (tokens[j].Text == "Name" && tokens[j].Kind == PpTokenKind.Identifier && j + 1 < stop && tokens[j + 1].Kind == PpTokenKind.String)
                                name = tokens[++j].Text.Trim('"');
                        }
                        passes.Add(new Pass { Name = name, Conservative = State(i + 2, stop, inherited) }); i = stop;
                    }
                    else if (token.Kind == PpTokenKind.Identifier && token.Text == "__Program") passes.Add(new Pass { Conservative = inherited });
                    else if (token.Kind == PpTokenKind.Identifier && token.Text == "GrabPass") grabPass = true;
                    else if (token.Kind == PpTokenKind.Identifier && token.Text == "UsePass" && inherited != false) unsafeImport = true;
                    else if (token.Text == "{") i = partner[i];
                }
            }
        }
    }
}
