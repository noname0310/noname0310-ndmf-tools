using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Noname.AvatarTools.Analysis
{
    // Structural facts used by input queries. HLSL parsing/type checking remains
    // the native compiler's responsibility. Unsupported facts never grant a proof.
    internal sealed class ShaderInputProgram
    {
        internal sealed class Parameter
        {
            internal string Type, Name;
            internal int ArrayLength;
            internal bool Output;
        }
        internal sealed class Function
        {
            internal string Name, ReturnType;
            internal int Header, Begin, End;
            internal Parameter[] Parameters;
            internal bool SignatureComplete;
        }
        internal readonly PpToken[] Tokens;
        internal readonly int[] Partner;
        internal readonly Dictionary<string, List<Function>> Functions = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, (int Begin, int End)> Structs = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Types = new(StringComparer.Ordinal);
        internal readonly HashSet<string> GlobalIdentifiers = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> ScalarConstants = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, (int Begin, int End)> GlobalDeclarations = new(StringComparer.Ordinal);
        internal string Issue;
        private static readonly HashSet<string> NumericTypes = NumericTypeNames();
        private static HashSet<string> NumericTypeNames()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (string scalar in new[] { "bool", "int", "uint", "half", "float", "double" })
            {
                result.Add(scalar);
                for (int x = 1; x <= 4; x++)
                {
                    result.Add(scalar + x);
                    for (int y = 1; y <= 4; y++) result.Add(scalar + x + "x" + y);
                }
            }
            return result;
        }
        private static readonly HashSet<string> Qualifiers = new(new[] { "static", "inline", "const", "uniform", "precise", "in", "out", "inout" }, StringComparer.Ordinal);

        internal ShaderInputProgram(string source, string defaultFile = "")
        {
            using var cost = AnalysisCost.Measure("Shader input program parsing");
            Tokens = Array.Empty<PpToken>(); Partner = Array.Empty<int>();
            try
            {
                Tokens = LocatedTokens(source, defaultFile).ToArray(); Partner = Enumerable.Repeat(-1, Tokens.Length).ToArray();
                MatchDelimiters(); ParseTopLevel();
            }
            catch (PpException exception) { Issue = exception.Message; }
        }

        internal ShaderInputProgram(IEnumerable<PpToken> tokens)
        {
            Tokens = tokens.ToArray(); Partner = Enumerable.Repeat(-1, Tokens.Length).ToArray();
            try { MatchDelimiters(); ParseTopLevel(); }
            catch (PpException exception) { Issue = exception.Message; }
        }

        private static IEnumerable<PpToken> LocatedTokens(string source, string defaultFile)
        {
            string file = defaultFile; int line = 1;
            source = ShaderPropertyUsage.StripComments(source);
            var tokens = new List<PpToken>();
            for (int cursor = 0; cursor <= source.Length;)
            {
                int end = source.IndexOf('\n', cursor);
                if (end < 0) end = source.Length;
                tokens.Clear(); PpLexer.LexLine(source, cursor, end - cursor, file, line, tokens);
                cursor = end + 1;
                if (tokens.Count != 0 && tokens[0].Text == "#")
                {
                    int number = tokens.Count > 1 && tokens[1].Text == "line" ? 2 : 1;
                    if (number < tokens.Count && int.TryParse(tokens[number].Text, out int nextLine))
                    {
                        line = nextLine;
                        if (number + 1 < tokens.Count && tokens[number + 1].Kind == PpTokenKind.String)
                        {
                            string literal = tokens[number + 1].Text;
                            file = literal.Substring(1, literal.Length - 2).Replace("\\\\", "\\").Replace("\\\"", "\"");
                            if (file.Length == 0) file = defaultFile;
                        }
                        continue;
                    }
                    if (tokens.Count < 2 || tokens[1].Text != "pragma") throw new PpException("Unprocessed directive in HLSL input-use facts");
                }
                else foreach (var token in tokens) yield return token;
                line++;
            }
        }

        internal static bool IsNumericType(string name) => name != null && (NumericTypes.Contains(name) ||
            name.EndsWith("\n", StringComparison.Ordinal) && NumericTypes.Contains(name.Substring(0, name.Length - 1)));
        internal string Spell(int begin, int end)
        {
            var text = new StringBuilder();
            for (int i = begin; i < end; i++)
            {
                if (i != begin) text.Append(' ');
                text.Append(Tokens[i].Text);
            }
            return text.ToString();
        }
        internal string At(int index) => index < 0 || index >= Tokens.Length ? "" : Tokens[index].Text;

        internal bool ContainsAny(int begin, int end, ISet<string> names)
        {
            for (int i = begin; i < end; i++) if (names.Contains(Tokens[i].Text)) return true;
            return false;
        }

        private void AddGlobalIdentifiers(int begin, int end)
        {
            for (int i = begin; i < end; i++)
                if (Tokens[i].Kind == PpTokenKind.Identifier) GlobalIdentifiers.Add(Tokens[i].Text);
        }

        private bool HasAssignment(int begin, int end)
        {
            for (int i = begin; i < end; i++) if (Tokens[i].Text == "=") return true;
            return false;
        }

        private void MatchDelimiters()
        {
            var stack = new Stack<int>();
            for (int i = 0; i < Tokens.Length; i++)
            {
                string token = At(i);
                if (token == "(" || token == "[" || token == "{") stack.Push(i);
                else if (token == ")" || token == "]" || token == "}")
                {
                    if (stack.Count == 0) throw new PpException("Unbalanced HLSL delimiters");
                    int open = stack.Pop();
                    if (At(open) != (token == ")" ? "(" : token == "]" ? "[" : "{")) throw new PpException("Mismatched HLSL delimiters");
                    Partner[open] = i; Partner[i] = open;
                }
            }
            if (stack.Count != 0) throw new PpException("Unclosed HLSL delimiter");
        }

        private void ParseTopLevel()
        {
            for (int start = 0; start < Tokens.Length;)
            {
                if (At(start) == ";") { start++; continue; }
                int declaration = start;
                while (At(declaration) == "[") declaration = Partner[declaration] + 1;
                int stop = declaration;
                while (stop < Tokens.Length && At(stop) != ";" && At(stop) != "{")
                { if (At(stop) == "(" || At(stop) == "[") stop = Partner[stop] + 1; else stop++; }
                if (stop == Tokens.Length) throw new PpException("Unterminated HLSL declaration");
                if (At(stop) == ";")
                {
                    AddGlobalIdentifiers(declaration, stop);
                    ReadConstant(declaration, stop); start = stop + 1; continue;
                }
                int close = Partner[stop];
                if (At(declaration) == "struct")
                {
                    if (declaration + 2 != stop || Tokens[declaration + 1].Kind != PpTokenKind.Identifier)
                        throw new PpException("Unsupported HLSL aggregate declaration");
                    Types.Add(At(declaration + 1));
                    Structs[At(declaration + 1)] = (stop + 1, close);
                }
                else if (At(declaration) == "cbuffer" || At(declaration) == "tbuffer")
                    AddGlobalIdentifiers(stop, close);
                else if (HasAssignment(declaration, stop))
                {
                    while (close + 1 < Tokens.Length && At(close + 1) != ";") close++;
                    AddGlobalIdentifiers(declaration, close);
                }
                else
                {
                    int arguments = declaration;
                    while (arguments < stop && At(arguments) != "(") arguments++;
                    if (arguments == stop || arguments == declaration || Tokens[arguments - 1].Kind != PpTokenKind.Identifier)
                        throw new PpException("Unsupported HLSL definition");
                    var function = new Function { Name = At(arguments - 1), Header = declaration, Begin = stop + 1, End = close };
                    int returnCount = 0;
                    for (int i = declaration; i < arguments - 1; i++)
                        if (!Qualifiers.Contains(Tokens[i].Text)) { function.ReturnType = Tokens[i].Text; returnCount++; }
                    if (returnCount != 1) function.ReturnType = null;
                    function.Parameters = Split(arguments + 1, Partner[arguments]).Select(p => ReadParameter(p.Begin, p.End)).ToArray();
                    function.SignatureComplete = function.ReturnType != null && function.Parameters.All(p => p != null);
                    if (!Functions.TryGetValue(function.Name, out var list)) Functions.Add(function.Name, list = new List<Function>());
                    list.Add(function);
                }
                start = close + 1;
                // A struct/constant buffer declaration has a terminating semicolon.
                if (At(start) == ";") start++;
            }
        }

        internal IEnumerable<(int Begin, int End)> Split(int begin, int end)
        {
            int start = begin;
            for (int i = begin; i < end; i++)
            {
                if (At(i) == "(" || At(i) == "[" || At(i) == "{") { i = Partner[i]; continue; }
                if (At(i) != ",") continue;
                yield return (start, i); start = i + 1;
            }
            if (start < end) yield return (start, end);
        }

        private Parameter ReadParameter(int begin, int end)
        {
            var result = new Parameter();
            while (begin < end && Qualifiers.Contains(At(begin)))
            { result.Output |= At(begin) == "out" || At(begin) == "inout"; begin++; }
            if (begin + 2 > end || Tokens[begin].Kind != PpTokenKind.Identifier || Tokens[begin + 1].Kind != PpTokenKind.Identifier) return null;
            result.Type = At(begin); result.Name = At(begin + 1); begin += 2;
            if (At(begin) == "[")
            {
                if (begin + 3 > end || At(begin + 2) != "]" || !int.TryParse(At(begin + 1), out int count) || count < 1 || count > 256) return null;
                result.ArrayLength = count; begin += 3;
            }
            if (begin == end || At(begin) == ":") return result;
            return null;
        }

        private void ReadConstant(int begin, int end)
        {
            int declaration = begin;
            bool constant = false;
            while (begin < end && Qualifiers.Contains(At(begin))) { constant |= At(begin) == "const"; begin++; }
            if (begin + 2 <= end && IsNumericType(At(begin)) && Tokens[begin + 1].Kind == PpTokenKind.Identifier && At(begin + 2) != "(")
                GlobalDeclarations[At(begin + 1)] = (declaration, end + 1);
            if (!constant || begin + 4 > end || !IsNumericType(At(begin)) || At(begin + 2) != "=") return;
            int value = begin + 3;
            if (At(value) == "-" || At(value) == "+") value++;
            if (value + 1 != end || Tokens[value].Kind != PpTokenKind.Number && At(value) != "true" && At(value) != "false") return;
            string name = At(begin + 1);
            if (ScalarConstants.ContainsKey(name)) { ScalarConstants.Remove(name); return; }
            ScalarConstants.Add(name, Spell(begin + 3, end));
        }

        internal Dictionary<string, Parameter> LocalVariables(Function function)
        {
            var variables = new Dictionary<string, Parameter>(StringComparer.Ordinal);
            if (function.Parameters != null)
                foreach (var parameter in function.Parameters.Where(p => p != null)) variables[parameter.Name] = parameter;
            for (int i = function.Begin; i + 1 < function.End; i++)
            {
                if (!IsNumericType(At(i)) && !Types.Contains(At(i))) continue;
                if (Tokens[i + 1].Kind != PpTokenKind.Identifier || At(i + 2) == "(") continue;
                int end = i + 2;
                if (At(end) == "[") end = Partner[end] + 1;
                var variable = ReadParameter(i, end);
                if (variable != null) variables[variable.Name] = variable;
            }
            return variables;
        }

        internal int LValueRoot(int end)
        {
            int cursor = end;
            while (cursor >= 0)
            {
                if (At(cursor) == "]") { cursor = Partner[cursor] - 1; continue; }
                if (Tokens[cursor].Kind != PpTokenKind.Identifier) return -1;
                if (At(cursor - 1) == ".") { cursor -= 2; continue; }
                return cursor;
            }
            return -1;
        }
    }
}
