using System;
using System.Collections.Generic;

namespace Noname.AvatarTools.Analysis
{
    internal sealed partial class ShaderInputProgram
    {
        // Source specialization may keep a writable static declaration when a
        // raw macro's argument contract is unknown. After preprocessing, recover
        // literal values only when every use in this program is read-only. These
        // facts participate in the same dependency projection as authored consts.
        private void ReadImmutableInitializers()
        {
            var candidates = new Dictionary<string, (int Begin, int End, string Literal)>(StringComparer.Ordinal);
            foreach (var pair in GlobalDeclarations)
            {
                int cursor = pair.Value.Begin;
                bool isStatic = false, isConst = false;
                while (cursor < pair.Value.End && Qualifiers.Contains(At(cursor)))
                { isStatic |= At(cursor) == "static"; isConst |= At(cursor) == "const"; cursor++; }
                // A float literal must not bypass a conversion through half,
                // integer or an aggregate type when passed to a probe argument.
                if (!isStatic || isConst || At(cursor) != "float" || At(cursor + 2) != "=") continue;
                int value = cursor + 3, last = pair.Value.End - 1;
                int number = At(value) == "+" || At(value) == "-" ? value + 1 : value;
                if (number + 1 != last || Tokens[number].Kind != PpTokenKind.Number) continue;
                candidates.Add(pair.Key, (pair.Value.Begin, pair.Value.End, Spell(value, last)));
            }
            if (candidates.Count == 0) return;
            var delimiters = new Stack<int>();
            var calls = new List<(int Open, int Argument)>();
            for (int i = 0; i < Tokens.Length; i++)
            {
                string token = At(i);
                if (token == "(" || token == "[" || token == "{")
                {
                    delimiters.Push(i);
                    if (token == "(" && i > 0 && Tokens[i - 1].Kind == PpTokenKind.Identifier &&
                        At(i - 1) != "if" && At(i - 1) != "while" && At(i - 1) != "for" && At(i - 1) != "switch")
                        calls.Add((i, 0));
                }
                else if (token == ")" || token == "]" || token == "}")
                {
                    int open = delimiters.Pop();
                    if (calls.Count > 0 && calls[calls.Count - 1].Open == open) calls.RemoveAt(calls.Count - 1);
                }
                else if (token == "," && calls.Count > 0 && delimiters.Peek() == calls[calls.Count - 1].Open)
                {
                    var call = calls[calls.Count - 1]; calls[calls.Count - 1] = (call.Open, call.Argument + 1);
                }
                if (!candidates.TryGetValue(token, out var declaration) || i >= declaration.Begin && i < declaration.End) continue;
                int begin = i, end = i + 1;
                while (At(end) == "." && Tokens[end + 1].Kind == PpTokenKind.Identifier) end += 2;
                while (At(end) == ")" && Partner[end] == begin - 1) { begin--; end++; }
                if (InitializerWrite(At(end)) || At(end) == "[" || At(begin - 1) == "++" || At(begin - 1) == "--" || At(begin - 1) == "&")
                { candidates.Remove(token); continue; }
                foreach (var call in calls)
                    if (!ReadOnlyArgument(At(call.Open - 1), call.Argument)) { candidates.Remove(token); break; }
            }
            foreach (var pair in candidates) ScalarConstants[pair.Key] = pair.Value.Literal;
        }

        private bool ReadOnlyArgument(string name, int argument)
        {
            if (Functions.TryGetValue(name, out var overloads))
            {
                foreach (var function in overloads)
                    if (!function.SignatureComplete || argument >= function.Parameters.Length || function.Parameters[argument].Output) return false;
                return true;
            }
            return ShaderInputFunctionProof.IsPureIntrinsic(name);
        }

        private static bool InitializerWrite(string token) => token == "=" || token == "+=" || token == "-=" || token == "*=" ||
            token == "/=" || token == "%=" || token == "&=" || token == "|=" || token == "^=" || token == ">>=" || token == "<<=" ||
            token == "++" || token == "--";
    }
}
