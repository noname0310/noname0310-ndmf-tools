using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Noname.AvatarTools.Analysis
{
    // This is deliberately not an HLSL parser. Only standalone scalar globals
    // with no initializer, register, array or aggregate layout are specialized.
    // Unity expands includes/macros first; the native compiler checks all uses of
    // the resulting constants. Unsupported declarations keep their runtime input.
    internal static class ShaderConstantInputs
    {
        private static readonly Regex LineWord = new(@"\Gline\b", RegexOptions.CultureInvariant);

        internal readonly struct Lexeme
        {
            internal readonly string Text;
            internal readonly int Index, Length;
            internal Lexeme(string source, int start, int length)
            { Text = length == 1 && source[start] < Characters.Length ? Characters[source[start]] : source.Substring(start, length); Index = start; Length = length; }
        }
        private static readonly string[] Characters = CharacterTokens();
        private static string[] CharacterTokens()
        {
            var result = new string[128];
            for (int i = 0; i < result.Length; i++) result[i] = ((char)i).ToString();
            return result;
        }

        // This declaration scanner intentionally keeps punctuation and digits as
        // separate tokens. It is not the preprocessing lexer or an HLSL parser.
        internal static List<Lexeme> Tokenize(string source)
        {
            var tokens = new List<Lexeme>();
            for (int cursor = 0; cursor < source.Length;)
            {
                if (char.IsWhiteSpace(source[cursor])) { cursor++; continue; }
                int start = cursor++; char first = source[start];
                if (PpLexer.IdentifierStart(first))
                    while (cursor < source.Length && PpLexer.IdentifierPart(source[cursor])) cursor++;
                else if (first == '"' || first == '\'')
                {
                    // Preserve the former scanner's treatment of malformed
                    // literals: only a fully closed literal is a single token.
                    int end = cursor;
                    while (end < source.Length)
                    {
                        if (source[end] == first) { cursor = end + 1; break; }
                        if (source[end] == '\\')
                        {
                            if (end + 1 >= source.Length || source[end + 1] == '\n') break;
                            end += 2;
                        }
                        else end++;
                    }
                }
                tokens.Add(new Lexeme(source, start, cursor - start));
            }
            return tokens;
        }

        internal static string Normalize(string source) => ScanDirectives(ShaderPropertyUsage.StripComments(source), true);

        // The former multiline ^\s* expression rescanned long whitespace runs at
        // every line start, especially after comments had been blanked. Visit each
        // run once while retaining its cross-line whitespace and CR/LF semantics.
        internal static string ScanDirectives(string source, bool locationsOnly)
        {
            StringBuilder output = null;
            int start = 0, copied = 0;
            while (start < source.Length)
            {
                int hash = start;
                while (hash < source.Length && char.IsWhiteSpace(source[hash])) hash++;
                if (hash == source.Length) break;
                int end = hash;
                bool replace = source[hash] == '#';
                if (replace && locationsOnly)
                {
                    int operand = hash + 1;
                    while (operand < source.Length && char.IsWhiteSpace(source[operand])) operand++;
                    replace = operand < source.Length && (source[operand] >= '0' && source[operand] <= '9' ||
                        source[operand] == 'l' && LineWord.Match(source, operand).Success);
                    if (replace)
                    {
                        end = operand;
                        while (end < source.Length && source[end] != '\r' && source[end] != '\n') end++;
                    }
                }
                else if (replace)
                {
                    end = source.IndexOf('\n', hash);
                    if (end < 0) end = source.Length;
                }
                if (replace)
                {
                    output ??= new StringBuilder(source.Length);
                    output.Append(source, copied, start - copied);
                    if (!locationsOnly) output.Append(' ', end - start);
                    copied = end;
                }
                int newline = source.IndexOf('\n', replace ? end : hash);
                if (newline < 0) break;
                start = newline + 1;
            }
            return output == null ? source : output.Append(source, copied, source.Length - copied).ToString();
        }

        internal static string Specialize(string source, IReadOnlyDictionary<string, float> constants, out int specialized)
        {
            specialized = 0;
            if (constants == null || constants.Count == 0) return Normalize(source);
            return Prepare(source).Apply(constants, out specialized);
        }

        // Source structure does not depend on material values. Keep the exact
        // declaration edits once, then apply each material in one forward copy.
        internal sealed class Plan
        {
            internal readonly string Source;
            internal readonly List<(int Start, int Length, string Type, string Name)> Declarations = new();
            internal string[] UniformNames;
            internal Plan(string source) { Source = source; }
            internal string Apply(IReadOnlyDictionary<string, float> constants, out int specialized)
            {
                specialized = 0;
                if (constants == null || constants.Count == 0) return Source;
                StringBuilder output = null; int cursor = 0;
                foreach (var declaration in Declarations)
                {
                    if (!constants.TryGetValue(declaration.Name, out float value) || !MotionAnalysis.Finite(value) ||
                        !Literal(declaration.Type, value, out string literal)) continue;
                    output ??= new StringBuilder(Source.Length);
                    output.Append(Source, cursor, declaration.Start - cursor);
                    output.Append("static const ").Append(declaration.Type).Append(' ').Append(declaration.Name)
                        .Append(" = ").Append(literal).Append(';');
                    cursor = declaration.Start + declaration.Length; specialized++;
                }
                return output == null ? Source : output.Append(Source, cursor, Source.Length - cursor).ToString();
            }
        }

        internal static Plan Prepare(string source)
        {
            using var cost = AnalysisCost.Measure("Shader specialization preparation");
            source = Normalize(source);
            var plan = new Plan(source);
            // Preserve compiler pragmas in the result but exclude them from the
            // declaration scanner, including identifiers in file-name strings.
            string scan = ScanDirectives(source, false);
            var tokens = Tokenize(scan);
            var assignedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i + 2 < tokens.Count; i++)
            {
                string next = tokens[i + 1].Text, after = tokens[i + 2].Text;
                if (next == "=" && after != "=" || "+-*/%&|^".Contains(next) && after == "=" ||
                    (next == "+" || next == "-") && after == next) assignedNames.Add(tokens[i].Text);
                if ((tokens[i].Text == "+" || tokens[i].Text == "-") && next == tokens[i].Text)
                    assignedNames.Add(after);
            }
            int braces = 0, parentheses = 0, statement = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i].Text;
                if (token == "{") { braces++; continue; }
                if (token == "}") { braces--; if (braces == 0) statement = i + 1; continue; }
                if (token == "(") parentheses++;
                if (token == ")") parentheses--;
                if (token != ";" || braces != 0 || parentheses != 0) continue;
                if (statement <= i)
                {
                    int start = tokens[statement].Index, end = tokens[i].Index + tokens[i].Length;
                    int typeIndex = statement + (tokens[statement].Text == "uniform" ? 1 : 0);
                    if (typeIndex + 2 == i)
                    {
                        string type = tokens[typeIndex].Text, name = tokens[typeIndex + 1].Text;
                        bool identifier = name.Length != 0 && PpLexer.IdentifierStart(name[0]);
                        for (int n = 1; identifier && n < name.Length; n++) identifier = PpLexer.IdentifierPart(name[n]);
                        // Avoid unnecessary compilation failures from shader-local
                        // assignments. An indirect out/inout write is still rejected
                        // by the compiler after substitution, never assumed safe.
                        if (identifier && (type == "float" || type == "half" || type == "int" || type == "uint" || type == "bool") &&
                            !assignedNames.Contains(name)) plan.Declarations.Add((start, end - start, type, name));
                    }
                }
                statement = i + 1;
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var declaration in plan.Declarations) names.Add(declaration.Name);
            plan.UniformNames = new string[names.Count]; names.CopyTo(plan.UniformNames);
            Array.Sort(plan.UniformNames, StringComparer.Ordinal);
            return plan;
        }

        private static bool Literal(string type, float value, out string literal)
        {
            literal = null;
            if (type == "float" || type == "half")
            {
                string number = value == 0 && float.IsNegativeInfinity(1 / value) ? "-0.0" : value.ToString("R", CultureInfo.InvariantCulture);
                if (!number.Contains(".") && !number.Contains("E") && !number.Contains("e")) number += ".0";
                literal = number + "f"; return true;
            }
            // ShaderLab Float/Int and HLSL integer upload conventions need not
            // agree. Positive zero is represented identically by both layouts.
            // Do not reinterpret other material values without a typed ABI proof.
            if (value != 0 || float.IsNegativeInfinity(1 / value)) return false;
            literal = type == "bool" ? "false" : type == "uint" ? "0u" : "0";
            return true;
        }
    }
}
