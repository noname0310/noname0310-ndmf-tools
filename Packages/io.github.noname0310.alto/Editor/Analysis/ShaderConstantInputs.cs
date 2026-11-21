using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        private static readonly Regex Token = new(@"""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[_A-Za-z][_A-Za-z0-9]*|[^\s]", RegexOptions.CultureInvariant);
        private static readonly Regex Location = new(@"^\s*#\s*(?:line\b|[0-9])[^\r\n]*", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex SimpleGlobal = new(@"^(?:uniform\s+)?(float|half|int|uint|bool)\s+([_A-Za-z][_A-Za-z0-9]*)\s*;$", RegexOptions.CultureInvariant);
        private static readonly Regex Directive = new(@"^\s*#.*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        internal static string Normalize(string source) => Location.Replace(ShaderPropertyUsage.StripComments(source), "");

        internal static string Specialize(string source, IReadOnlyDictionary<string, float> constants, out int specialized)
        {
            specialized = 0;
            source = Normalize(source);
            if (constants == null || constants.Count == 0) return source;
            // Preserve compiler pragmas in the result but exclude them from the
            // declaration scanner, including identifiers in file-name strings.
            string scan = Directive.Replace(source, m => new string(' ', m.Length));
            var tokens = Token.Matches(scan).Cast<Match>().ToArray();
            var assignedNames = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i + 2 < tokens.Length; i++)
            {
                string next = tokens[i + 1].Value, after = tokens[i + 2].Value;
                if (next == "=" && after != "=" || "+-*/%&|^".Contains(next) && after == "=" ||
                    (next == "+" || next == "-") && after == next) assignedNames.Add(tokens[i].Value);
                if ((tokens[i].Value == "+" || tokens[i].Value == "-") && next == tokens[i].Value)
                    assignedNames.Add(after);
            }
            var edits = new List<(int Start, int Length, string Text)>();
            int braces = 0, parentheses = 0, statement = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Value;
                if (token == "{") { braces++; continue; }
                if (token == "}") { braces--; if (braces == 0) statement = i + 1; continue; }
                if (token == "(") parentheses++;
                if (token == ")") parentheses--;
                if (token != ";" || braces != 0 || parentheses != 0) continue;
                if (statement <= i)
                {
                    int start = tokens[statement].Index, end = tokens[i].Index + tokens[i].Length;
                    string declaration = string.Join(" ", tokens.Skip(statement).Take(i - statement + 1).Select(t => t.Value));
                    var match = SimpleGlobal.Match(declaration);
                    if (match.Success && constants.TryGetValue(match.Groups[2].Value, out float value) &&
                        MotionAnalysis.Finite(value) && Literal(match.Groups[1].Value, value, out string literal))
                    {
                        string name = match.Groups[2].Value;
                        // Avoid unnecessary compilation failures from shader-local
                        // assignments. An indirect out/inout write is still rejected
                        // by the compiler after substitution, never assumed safe.
                        if (!assignedNames.Contains(name)) edits.Add((start, end - start, "static const " + match.Groups[1].Value + " " + name + " = " + literal + ";"));
                    }
                }
                statement = i + 1;
            }
            var output = new StringBuilder(source);
            foreach (var edit in edits.OrderByDescending(e => e.Start)) output.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
            specialized = edits.Count;
            return output.ToString();
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
