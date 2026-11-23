using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor.Rendering;

namespace Noname.AvatarTools.Analysis
{
    // Prove a cut in an input-use path: a pure call's result is independent of a
    // selected argument, for every value of its other unconstrained arguments.
    // The native compiler checks the generated probe and its live input mask.
    internal static class ShaderInputFunctionProof
    {
        internal sealed class Result
        {
            internal bool Independent;
            internal string Issue;
            internal readonly HashSet<string> Functions = new(StringComparer.Ordinal);
            internal readonly HashSet<string> GlobalReads = new(StringComparer.Ordinal);
        }
        private static readonly HashSet<string> Intrinsics = new(new[] {
            "abs", "acos", "all", "any", "asin", "atan", "atan2", "ceil", "clamp", "cos", "cosh", "cross",
            "degrees", "determinant", "distance", "dot", "exp", "exp2", "floor", "fmod", "frac", "isfinite",
            "isinf", "isnan", "length", "lerp", "log", "log2", "log10", "max", "min", "mul", "normalize",
            "pow", "radians", "reflect", "refract", "round", "rsqrt", "saturate", "sign", "sin", "sinh",
            "smoothstep", "sqrt", "step", "tan", "tanh", "transpose", "trunc" }, StringComparer.Ordinal);
        private static readonly HashSet<string> Control = new(new[] { "if", "while", "for", "switch", "return" }, StringComparer.Ordinal);
        private static readonly HashSet<string> Syntax = new(new[] { "else", "do", "break", "continue", "case", "default", "true", "false",
            "const", "static", "inline", "uniform", "in", "out", "inout", "precise" }, StringComparer.Ordinal);
        private static readonly HashSet<string> Assignments = new(new[] { "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", ">>=", "<<=" }, StringComparer.Ordinal);

        internal static Result Analyze(string source, ShaderInputProgram program, ShaderInputProgram.Function function,
            int argument, IReadOnlyDictionary<int, string> constants,
            Func<string, string, ShaderType, (D3DShaderInputs.Input[] Inputs, string Error)> compile)
        {
            var result = new Result();
            if (program.Issue != null) { result.Issue = program.Issue; return result; }
            try
            {
                Pure(program, function, result, new HashSet<string>(StringComparer.Ordinal));
                if (!function.SignatureComplete || argument < 0 || argument >= function.Parameters.Length ||
                    function.Parameters[argument].ArrayLength != 0 || !ScalarOrVector(function.ReturnType) ||
                    !ScalarOrVector(function.Parameters[argument].Type)) throw new PpException("Unsupported input-probe signature");
                // A closed numeric call graph has no external type/global state.
                // Compile that exact closure so unrelated lighting declarations
                // and material values do not create distinct probe compilations.
                var closure = result.Functions.Select(n => program.Functions[n].Single()).OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
                if (result.GlobalReads.Count == 0 && closure.All(f => f.SignatureComplete && ShaderInputProgram.IsNumericType(f.ReturnType) &&
                    f.Parameters.All(p => ShaderInputProgram.IsNumericType(p.Type)) && !program.ContainsAny(f.Header, f.End + 1, program.Types)))
                    source = string.Join("\n", closure.Select(f => program.Spell(f.Header, f.Begin - 1) + ";")) + "\n" +
                        string.Join("\n", closure.Select(f => program.Spell(f.Header, f.End + 1)));
                string prefix = "ALTO_InputProbe_";
                if (program.Tokens.Any(t => t.Kind == PpTokenKind.Identifier && t.Text.StartsWith(prefix, StringComparison.Ordinal)))
                    throw new PpException("Input-probe identifier collision");
                var globals = new StringBuilder(); var locals = new StringBuilder(); var args = new List<string>();
                for (int i = 0; i < function.Parameters.Length; i++)
                {
                    var parameter = function.Parameters[i];
                    if (!ScalarOrVector(parameter.Type)) throw new PpException("Unsupported input-probe argument type");
                    if (i == argument) { args.Add(prefix + "value"); continue; }
                    string name = prefix + "arg" + i;
                    string declaration = parameter.Type + " " + name + (parameter.ArrayLength == 0 ? "" : "[" + parameter.ArrayLength + "]");
                    if (constants != null && constants.TryGetValue(i, out string initializer))
                    {
                        // Only literal expressions enter a probe as fixed values.
                        // The caller must separately prove their value at the call.
                        var tokens = PpLexer.Lex(initializer);
                        if (tokens.Any(t => t.Kind == PpTokenKind.Identifier && t.Text != "true" && t.Text != "false") ||
                            tokens.Any(t => t.Kind == PpTokenKind.Punctuation && !new[] { "-", "+", "{", "}", ",", "(", ")" }.Contains(t.Text)))
                            throw new PpException("Input-probe initializer is not a literal");
                        locals.Append(declaration).Append(" = ").Append(initializer).Append(";\n");
                    }
                    else globals.Append("uniform ").Append(declaration).Append(";\n");
                    args.Add(name);
                }
                string entry = prefix + "entry";
                string expression = function.Name + "(" + string.Join(",", args) + ")";
                // Observe every component in its original numeric representation.
                // Casting an integer result to float could hide low-bit changes.
                string outputType = function.ReturnType.StartsWith("bool", StringComparison.Ordinal) ?
                    "uint" + function.ReturnType.Substring(4) : function.ReturnType;
                string output = prefix + "output";
                string probe = source + "\n" + globals + "\nstruct " + output +
                    " { float4 position : SV_POSITION; " + outputType + " result : TEXCOORD0; };\n" + output + " " + entry +
                    "(" + function.Parameters[argument].Type + " " + prefix + "value : ALTOQUERY) {\n" + locals + output +
                    " o; o.position=float4(0,0,0,1); o.result=(" + outputType + ")" + expression + "; return o;\n}\n";
                var proof = compile(probe, entry, ShaderType.Vertex);
                if (proof.Inputs == null) { result.Issue = proof.Error ?? "Input-probe reflection unavailable"; return result; }
                result.Independent = !proof.Inputs.Any(i => i.ReadMask != 0 && string.Equals(i.Semantic, "ALTOQUERY", StringComparison.OrdinalIgnoreCase));
            }
            catch (PpException exception) { result.Issue = exception.Message; }
            return result;
        }

        private static bool ScalarOrVector(string type) => ShaderInputProgram.IsNumericType(type) && !type.Contains("x") && !type.StartsWith("double", StringComparison.Ordinal);
        internal static bool IsPureIntrinsic(string name) => Intrinsics.Contains(name) || ShaderInputProgram.IsNumericType(name);

        private static void Pure(ShaderInputProgram program, ShaderInputProgram.Function function, Result result, HashSet<string> active)
        {
            if (result.Functions.Contains(function.Name)) return;
            if (!active.Add(function.Name)) throw new PpException("Recursive function in input-probe slice");
            if (!function.SignatureComplete || function.Parameters.Any(p => p.Output) || program.Functions[function.Name].Count != 1)
                throw new PpException("Ambiguous or output-writing function in input-probe slice");
            var locals = program.LocalVariables(function);
            for (int i = function.Begin; i < function.End; i++)
            {
                string token = program.At(i);
                if (token == "discard" || token == "asm" || token == "interface") throw new PpException("Observable effect in input-probe slice");
                if (Assignments.Contains(token) || token == "++" || token == "--")
                {
                    int root = program.LValueRoot(i - 1);
                    if ((token == "++" || token == "--") && root < 0) root = i + 1;
                    if (root < function.Begin || root >= function.End || !locals.ContainsKey(program.At(root)) || program.GlobalIdentifiers.Contains(program.At(root)))
                        throw new PpException("Nonlocal or ambiguous write in input-probe slice");
                }
                if (program.Tokens[i].Kind != PpTokenKind.Identifier) continue;
                if (!locals.ContainsKey(token) && program.GlobalIdentifiers.Contains(token) && !Control.Contains(token) && !Syntax.Contains(token) &&
                    !ShaderInputProgram.IsNumericType(token) && !program.Types.Contains(token) && !program.Functions.ContainsKey(token) && !Intrinsics.Contains(token))
                    result.GlobalReads.Add(token);
                if (program.At(i + 1) != "(" || Control.Contains(token) || ShaderInputProgram.IsNumericType(token)) continue;
                // User definitions take precedence over the intrinsic whitelist.
                if (program.Functions.TryGetValue(token, out var callees))
                { if (callees.Count != 1) throw new PpException("Ambiguous callee in input-probe slice"); Pure(program, callees[0], result, active); }
                else if (!Intrinsics.Contains(token)) throw new PpException("Unverified callee in input-probe slice: " + token);
            }
            active.Remove(function.Name); result.Functions.Add(function.Name);
        }
    }
}
