using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using UnityEditor;
using UnityEditor.Rendering;

namespace Noname.AvatarTools.Analysis
{
    // The dictionary contains standalone scalar float/half declarations verified
    // in this exact original preprocessed source. Native metadata must independently
    // prove float32 scalar storage. Floating instructions stay ordered and opaque
    // unless exact input facts force one result bit pattern. Optional domains are
    // complete per-use value sets, never samples. No host FP arithmetic is used.
    internal static partial class D3DConstantEquivalence
    {
        internal static bool TryEquivalent(ShaderData.VariantCompileInfo original, ShaderData.VariantCompileInfo candidate,
            IReadOnlyDictionary<string, float> constants, ShaderType stage, out HashSet<string> unprovedConstants, out string issue,
            bool sameSource = false, IReadOnlyDictionary<string, float[]> uniformDomains = null)
        {
            unprovedConstants = new HashSet<string>(StringComparer.Ordinal); issue = null;
            try
            {
                // The caller supplies exact preprocessed token equality. This
                // preserves untouched stages outside the bounded evaluator.
                if (sameSource && IdenticalCompileArtifacts(original, candidate)) return true;
                var expressions = new Expressions();
                var left = Program.Read(original, stage, expressions, constants, uniformDomains: uniformDomains);
                var right = Program.Read(candidate, stage, expressions, constants, uniformDomains: uniformDomains);
                // Changed source can also leave an opaque stage untouched, once
                // the complete native slot ownership has independently been proved.
                if (IdenticalCompileArtifacts(original, candidate) && left.BindingIdentity == right.BindingIdentity) return true;
                var first = left.Evaluate(); var second = right.Evaluate();
                Require(left.Configuration == right.Configuration, "native execution declarations changed");
                bool equivalent = true;
                foreach (string key in first.Keys.Union(second.Keys, StringComparer.Ordinal))
                {
                    bool hasFirst = first.TryGetValue(key, out var a), hasSecond = second.TryGetValue(key, out var b);
                    if (hasFirst && hasSecond && a.Expression == b.Expression) continue;
                    equivalent = false;
                    if (hasFirst) unprovedConstants.UnionWith(a.Constants);
                    if (hasSecond) unprovedConstants.UnionWith(b.Constants);
                }
                if (!equivalent) issue = "native observable expressions differ after constant substitution";
                return equivalent;
            }
            catch (Exception exception) { unprovedConstants.Clear(); issue = Failure(exception); return false; }
        }

        // zeroIntegerUniforms is an additional semantic-only upload premise:
        // each named, standalone scalar has an actual typed material value of
        // positive zero. Integer metadata and zero bits are checked separately.
        // It never authorizes an integer from a nonzero/negative-zero float or a
        // value obtained through the wrong ShaderLab property getter.
        internal static bool TryInputs(ShaderNativeProgram original, IReadOnlyDictionary<string, float> constants,
            ShaderType stage, out D3DShaderInputs.Input[] inputs, out string issue, string outputSemantic = null, bool semanticInputsOnly = false,
            ISet<string> zeroIntegerUniforms = null, ISet<string> observedConstants = null)
        {
            inputs = null; issue = null;
            try
            {
                var expressions = new Expressions();
                var program = Program.Read(original, stage, expressions, constants, allowOpaqueBuffers: true, semanticInputsOnly: semanticInputsOnly);
                program.ZeroIntegerUniforms = semanticInputsOnly ? zeroIntegerUniforms : null;
                program.ObservedConstants = observedConstants;
                var outputs = program.Evaluate(outputSemantic);
                var dependencies = new Dictionary<(D3DShaderInputs.InputKind Kind, string Name, uint Index), byte>();
                var pending = new Stack<int>(outputs.Values.Select(v => v.Expression)); var visited = new HashSet<int>();
                while (pending.Count != 0)
                {
                    int id = pending.Pop(); if (!visited.Add(id)) continue;
                    var expression = expressions[id];
                    if (expression.Input.HasValue && (!semanticInputsOnly || expression.Input.Value.Kind == D3DShaderInputs.InputKind.Semantic))
                    {
                        var input = expression.Input.Value; var key = (input.Kind, input.Semantic, input.SemanticIndex);
                        dependencies.TryGetValue(key, out byte mask); dependencies[key] = (byte)(mask | input.ReadMask);
                    }
                    foreach (int child in expression.Children) pending.Push(child);
                }
                inputs = dependencies.Select(p => new D3DShaderInputs.Input(p.Key.Name, p.Value, p.Key.Index, p.Key.Kind)).ToArray(); return true;
            }
            catch (Exception exception) { issue = Failure(exception); return false; }
        }

        private static string Failure(Exception exception) => exception is Unverified ? exception.Message :
            "native instruction proof failed: " + exception.GetType().Name;
        private sealed class Unverified : Exception { internal Unverified(string message) : base(message) { } }
        private static void Require(bool condition, string issue) { if (!condition) throw new Unverified(issue); }
        private sealed class Expression
        {
            internal string Operation;
            internal int[] Children;
            internal uint? Literal;
            internal D3DShaderInputs.Input? Input;
        }
        private sealed class Expressions
        {
            private readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);
            private readonly List<Expression> values = new();
            internal Expression this[int id] => values[id];
            internal int Add(string operation, int[] children = null, uint? literal = null, D3DShaderInputs.Input? input = null)
            {
                children ??= Array.Empty<int>(); string key = operation + "(" + string.Join(",", children) + ")";
                if (ids.TryGetValue(key, out int id)) return id;
                Require(values.Count < 262144, "native expression proof budget exhausted");
                id = values.Count; ids.Add(key, id); values.Add(new Expression { Operation = operation, Children = children, Literal = literal, Input = input }); return id;
            }
            internal int Literal(uint bits) => Add("bits:" + bits.ToString("x8", CultureInfo.InvariantCulture), literal: bits);
        }
        private sealed class Value
        {
            internal readonly int Expression;
            // Provenance belongs to this use, not its hash-consed expression:
            // two different uniforms can share the same literal expression.
            internal readonly HashSet<string> Constants;
            internal readonly uint[] Domain;
            internal readonly NumericFacts Numeric;
            internal Value(int expression, IEnumerable<string> constants = null, uint[] domain = null, NumericFacts numeric = NumericFacts.None)
            {
                Expression = expression; Domain = domain; Numeric = numeric;
                Constants = constants == null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(constants, StringComparer.Ordinal);
            }
        }
        private static uint FloatBits(float value) => unchecked((uint)BitConverter.ToInt32(BitConverter.GetBytes(value), 0));
        private static bool Normal(uint value) { uint exponent = (value >> 23) & 255; return exponent != 0 && exponent != 255; }
        private static bool NormalOrZero(uint value) => Normal(value) || (value & 0x7fffffff) == 0;
        private static bool Fold(string operation, uint[] operands, out uint result)
        {
            result = 0; uint a = operands[0], b = operands.Length > 1 ? operands[1] : 0;
            unchecked
            {
                switch (operation)
                {
                    // Native NaN source modifiers need not preserve the payload.
                    case "fabs": if ((a & 0x7fffffff) > 0x7f800000) return false; result = a & 0x7fffffff; return true;
                    case "fneg": if ((a & 0x7fffffff) > 0x7f800000) return false; result = a ^ 0x80000000; return true;
                    case "not": result = ~a; return true;
                    case "ineg": result = 0u - a; return true;
                    case "and": result = a & b; return true;
                    case "or": result = a | b; return true;
                    case "xor": result = a ^ b; return true;
                    case "iadd": result = a + b; return true;
                    case "imad": result = a * b + operands[2]; return true;
                    case "udiv-quotient": result = b == 0 ? uint.MaxValue : a / b; return true;
                    case "udiv-remainder": result = b == 0 ? uint.MaxValue : a % b; return true;
                    case "ishl": result = a << (int)(b & 31); return true;
                    case "ishr": result = (uint)((int)a >> (int)(b & 31)); return true;
                    case "ushr": result = a >> (int)(b & 31); return true;
                    case "ieq": result = a == b ? uint.MaxValue : 0; return true;
                    case "ine": result = a != b ? uint.MaxValue : 0; return true;
                    case "ige": result = (int)a >= (int)b ? uint.MaxValue : 0; return true;
                    case "ilt": result = (int)a < (int)b ? uint.MaxValue : 0; return true;
                    case "uge": result = a >= b ? uint.MaxValue : 0; return true;
                    case "ult": result = a < b ? uint.MaxValue : 0; return true;
                    case "imax": result = (int)a >= (int)b ? a : b; return true;
                    case "imin": result = (int)a <= (int)b ? a : b; return true;
                    case "umax": result = a >= b ? a : b; return true;
                    case "umin": result = a <= b ? a : b; return true;
                    case "itof": return ExactFloat((int)a, 0, true, out result);
                    case "utof": return ExactFloat(a, 0, true, out result);
                    case "eq": case "ne": case "ge": case "lt":
                        // Subnormals can flush before FP comparison. NaNs and
                        // infinity are also outside this exact folding subset.
                        if (!NormalOrZero(a) || !NormalOrZero(b)) return false;
                        int comparison = CompareFloat(a, b);
                        result = (operation == "eq" ? comparison == 0 : operation == "ne" ? comparison != 0 :
                            operation == "ge" ? comparison >= 0 : comparison < 0) ? uint.MaxValue : 0; return true;
                    case "add": return UniqueNormalSum(a, b, out result);
                    case "mad":
                        // A positive exact-zero product plus positive zero has
                        // no cancellation or rounding choice. Use the native
                        // signed-zero rules, not numerical ULP equality. Other
                        // zero-result MADs retain their operation and signs.
                        if (operands[2] == 0 && NormalOrZero(a) && NormalOrZero(b) &&
                            ((a & 0x7fffffff) == 0 || (b & 0x7fffffff) == 0) && ((a ^ b) & 0x80000000) == 0)
                            return true;
                        return Fold("mul", new[] { a, b }, out uint product) && Normal(product) && UniqueNormalSum(product, operands[2], out result);
                    case "mul":
                        if (!Normal(a) || !Normal(b)) return false;
                        Decode(a, out BigInteger significandA, out int exponentA); Decode(b, out BigInteger significandB, out int exponentB);
                        return ExactFloat(significandA * significandB, exponentA + exponentB, false, out result);
                    default: return false;
                }
            }
        }
        private static int CompareFloat(uint a, uint b)
        {
            if ((a & 0x7fffffff) == 0 && (b & 0x7fffffff) == 0) return 0;
            bool negativeA = (a & 0x80000000) != 0, negativeB = (b & 0x80000000) != 0;
            if (negativeA != negativeB) return negativeA ? -1 : 1;
            return negativeA ? b.CompareTo(a) : a.CompareTo(b);
        }
        private static void Decode(uint bits, out BigInteger significand, out int exponent)
        {
            significand = (bits & 0x7fffff) | 0x800000; if ((bits & 0x80000000) != 0) significand = -significand;
            exponent = (int)((bits >> 23) & 255) - 127 - 23;
        }
        private static bool ExactFloat(BigInteger significand, int exponent, bool allowZero, out uint bits)
        {
            bits = 0; if (significand.IsZero) return allowZero;
            uint sign = significand.Sign < 0 ? 0x80000000u : 0; significand = BigInteger.Abs(significand);
            while (significand.IsEven) { significand >>= 1; exponent++; }
            if (significand > 0xffffff) return false;
            int width = 0; for (BigInteger remaining = significand; !remaining.IsZero; remaining >>= 1) width++;
            int nativeExponent = exponent + width - 1 + 127;
            if (nativeExponent < 1 || nativeExponent > 254) return false;
            bits = sign | ((uint)nativeExponent << 23) | (((uint)significand << (24 - width)) & 0x7fffff); return true;
        }
        // For the runtime fallback the caller supplies complete forExternalTool:false
        // artifacts compiled for the same stage, variant and native environment.
        // The opaque Unity prefix is compared in full, never interpreted or guessed.
        internal static bool IdenticalCompileArtifacts(ShaderData.VariantCompileInfo first, ShaderData.VariantCompileInfo second)
        {
            if (!first.Success || !second.Success || first.ShaderData == null || second.ShaderData == null ||
                first.ShaderData.Length == 0 || second.ShaderData.Length == 0 ||
                !first.ShaderData.SequenceEqual(second.ShaderData)) return false;
            string FieldKey(ShaderData.ConstantInfo field) => field.Name + ":" + field.Index + ":" + (int)field.ConstantType + ":" +
                (int)field.DataType + ":" + field.Rows + ":" + field.Columns + ":" + field.ArraySize + ":" + field.StructSize + ":(" +
                string.Join(";", (field.StructFields ?? Array.Empty<ShaderData.ConstantInfo>()).Select(FieldKey)) + ")";
            string BufferKey(ShaderData.ConstantBufferInfo buffer) => buffer.Name + ":" + buffer.Size + ":" +
                string.Join(";", (buffer.Fields ?? Array.Empty<ShaderData.ConstantInfo>()).Select(FieldKey));
            string TextureKey(ShaderData.TextureBindingInfo binding) => binding.Name + ":" + binding.Index + ":" + binding.SamplerIndex + ":" +
                binding.Dim + ":" + binding.ArraySize + ":" + binding.Multisampled;
            return (first.ConstantBuffers ?? Array.Empty<ShaderData.ConstantBufferInfo>()).Select(BufferKey).SequenceEqual(
                    (second.ConstantBuffers ?? Array.Empty<ShaderData.ConstantBufferInfo>()).Select(BufferKey)) &&
                (first.TextureBindings ?? Array.Empty<ShaderData.TextureBindingInfo>()).Select(TextureKey).SequenceEqual(
                    (second.TextureBindings ?? Array.Empty<ShaderData.TextureBindingInfo>()).Select(TextureKey)) &&
                (first.Attributes ?? Array.Empty<UnityEngine.Rendering.VertexAttribute>()).SequenceEqual(
                    second.Attributes ?? Array.Empty<UnityEngine.Rendering.VertexAttribute>());
        }
    }
}
