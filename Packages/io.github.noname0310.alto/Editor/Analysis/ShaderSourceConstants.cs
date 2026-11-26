using System;
using System.Collections.Generic;
using System.Linq;

namespace Noname.AvatarTools.Analysis
{
    // A conservative source edit certificate, not a shader optimizer. Unknown
    // argument contracts keep the uniform; Unity still owns HLSL type checking.
    internal static class ShaderSourceConstants
    {
        private static readonly HashSet<string> ValueIntrinsics = new(new[] {
            "abs", "acos", "asin", "asfloat", "asint", "asuint", "atan", "atan2", "ceil", "clamp", "cos", "cosh", "cross", "degrees", "distance", "dot",
            "exp", "exp2", "floor", "fmod", "frac", "isfinite", "isinf", "isnan", "length", "lerp", "log", "log2", "log10",
            "max", "min", "mul", "normalize", "pow", "radians", "rcp", "reflect", "refract", "round", "rsqrt", "saturate",
            "sign", "sin", "sinh", "smoothstep", "sqrt", "step", "tan", "tanh", "trunc"
        }, StringComparer.Ordinal);

        internal static void RestrictDeclarationNames(string source, HashSet<string> candidates)
        {
            var tokens = PpLexer.Lex(source);
            for (int i = 0; i + 2 < tokens.Count; i++)
                if (tokens[i].Text == "#" && tokens[i + 1].Text == "define")
                {
                    string name = tokens[i + 2].Text;
                    // A declaration renamed by a macro need not denote the
                    // material property with the spelling seen before expansion.
                    candidates.Remove(name);
                    if (name == "float" || name == "half")
                    {
                        string type = i + 3 < tokens.Count ? tokens[i + 3].Text : "";
                        if ((type != "float" && type != "half" && type != "min16float" && type != "min10float") ||
                            i + 4 < tokens.Count && tokens[i + 4].Kind != PpTokenKind.Newline)
                            throw new InvalidOperationException("numeric declaration type has an unsupported macro definition");
                    }
                }
        }

        internal static void RestrictToReadOnly(string source, HashSet<string> candidates)
        {
            source = ShaderPropertyUsage.StripComments(source);
            var pastes = new ShaderPropertyUsage.Proof();
            ShaderPropertyUsage.ScanPasting(source, pastes);
            if (pastes.Uncertain != null) { candidates.Clear(); return; }
            candidates.RemoveWhere(name => pastes.PastePatterns.Values.Any(pattern => pattern.IsMatch(name)));
            var lexical = PpLexer.Lex(source);
            for (int i = 0; i + 1 < lexical.Count; i++)
                if (lexical[i].Text == "#" && lexical[i + 1].Text == "define")
                    while (i < lexical.Count && lexical[i].Kind != PpTokenKind.Newline)
                        candidates.Remove(lexical[i++].Text);
            var tokens = lexical.Where(t => t.Kind != PpTokenKind.Newline).ToArray();
            var shadowedCalls = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < tokens.Length; i++)
            {
                string name = tokens[i].Text;
                if (name == "__LINE__" || name == "__FILE__" || name == "__COUNTER__" || name == "__TIME__" || name == "__DATE__" || name == "__TIMESTAMP__")
                    throw new InvalidOperationException("source-location or clock-sensitive shader needs a separate source adapter");
                if (i + 2 < tokens.Length && name == "#" && tokens[i + 1].Text == "define") shadowedCalls.Add(tokens[i + 2].Text);
                if (i > 0 && i + 1 < tokens.Length && tokens[i + 1].Text == "(" &&
                    tokens[i - 1].Kind == PpTokenKind.Identifier && tokens[i - 1].Text != "return") shadowedCalls.Add(name);
                if (!candidates.Contains(name)) continue;
                int end = i + 1;
                // Scalar swizzles are legal lvalues too. Keep their original
                // uniform, including writes made through a macro replacement.
                while (end + 1 < tokens.Length && tokens[end].Text == "." && tokens[end + 1].Kind == PpTokenKind.Identifier) end += 2;
                string next = end < tokens.Length ? tokens[end].Text : "", previous = i > 0 ? tokens[i - 1].Text : "";
                if (next == "=" || next == "+=" || next == "-=" || next == "*=" || next == "/=" || next == "%=" ||
                    next == "&=" || next == "|=" || next == "^=" || next == "<<=" || next == ">>=" ||
                    next == "++" || next == "--" || previous == "++" || previous == "--" ||
                    next == "[" || previous == "&") candidates.Remove(name);
            }
            var unknownCalls = new Stack<bool>(); int unknownDepth = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                string name = tokens[i].Text;
                if (name == "(")
                {
                    string callee = i > 0 && tokens[i - 1].Kind == PpTokenKind.Identifier ? tokens[i - 1].Text : null;
                    bool unknown = callee != null && callee != "if" && callee != "while" && callee != "switch" &&
                        (shadowedCalls.Contains(callee) || !ShaderInputProgram.IsNumericType(callee) && !ValueIntrinsics.Contains(callee));
                    unknownCalls.Push(unknown); if (unknown) unknownDepth++;
                }
                else if (name == ")" && unknownCalls.Count != 0)
                { if (unknownCalls.Pop()) unknownDepth--; }
                else if (unknownDepth != 0) candidates.Remove(name);
            }
        }
    }
}
