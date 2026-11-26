using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Noname.AvatarTools.Analysis
{
    internal static partial class D3DConstantEquivalence
    {
        private const int MaximumDomainValues = 64;

        // D3D11.3 3.1.2/3.1.3: add has 0.5 ULP tolerance; a fused operation
        // may be no less accurate than its worst unfused expansion. With an
        // exactly representable normal product, no intermediate rounding or
        // under/overflow remains. Require one uniquely permitted final normal
        // result. Halfway cases remain unknown; no host FP rounding is used.
        // https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm#3.1.3.2
        private static bool UniqueNormalSum(uint a, uint b, out uint result)
        {
            result = 0;
            if (!NormalOrZero(a) || !NormalOrZero(b)) return false;
            if ((a & 0x7fffffff) == 0) { if (!Normal(b)) return false; result = b; return true; }
            if ((b & 0x7fffffff) == 0) { if (!Normal(a)) return false; result = a; return true; }
            Decode(a, out BigInteger left, out int leftExponent); Decode(b, out BigInteger right, out int rightExponent);
            int exponent = Math.Min(leftExponent, rightExponent);
            BigInteger sum = (left << (leftExponent - exponent)) + (right << (rightExponent - exponent));
            if (sum.IsZero) return false;
            uint sign = sum.Sign < 0 ? 0x80000000u : 0; sum = BigInteger.Abs(sum);
            int width = 0; for (BigInteger remaining = sum; !remaining.IsZero; remaining >>= 1) width++;
            int nativeExponent = exponent + width - 1 + 127;
            if (nativeExponent < 1 || nativeExponent > 254) return false;
            if (width <= 24) return ExactFloat(sign == 0 ? sum : -sum, exponent, false, out result);
            int shift = width - 24;
            BigInteger significand = sum >> shift, remainder = sum - (significand << shift), midpoint = BigInteger.One << (shift - 1);
            // No rounding-mode choice may distinguish the two native programs.
            if (remainder == midpoint) return false;
            // Do not rely on the overflowing side of the largest finite value.
            if (nativeExponent == 254 && significand == 0xffffff && !remainder.IsZero) return false;
            if (remainder > midpoint) significand++;
            if (significand == 0x1000000) { significand >>= 1; nativeExponent++; }
            if (nativeExponent > 254) return false;
            result = sign | ((uint)nativeExponent << 23) | ((uint)significand & 0x7fffff); return true;
        }

        private sealed partial class Program
        {
            private int arithmeticDomainChecks;
            private uint[] UniformDomain(string name)
            {
                if (uniformDomains == null || !uniformDomains.TryGetValue(name, out float[] values) || values == null || values.Length == 0 || values.Length > 4096) return null;
                // A cap discards the fact; it never truncates the possible values.
                var bits = values.Select(FloatBits).Distinct().Take(MaximumDomainValues + 1).ToArray();
                return bits.Length <= MaximumDomainValues ? bits : null;
            }
            private uint[] KnownDomain(Value value) => expressions[value.Expression].Literal is uint literal ? new[] { literal } : value.Domain;
            private static uint[] UnionDomain(uint[] first, uint[] second)
            {
                if (first == null || second == null) return null;
                var values = first.Concat(second).Distinct().Take(MaximumDomainValues + 1).ToArray();
                return values.Length <= MaximumDomainValues ? values : null;
            }
            private bool TryZeroAdd(Value[] operands, out Value value)
            {
                value = null; if (operands.Length != 2) return false;
                uint? first = expressions[operands[0].Expression].Literal, second = expressions[operands[1].Expression].Literal;
                int zero = first.HasValue && (first.Value & 0x7fffffff) == 0 ? 0 :
                    second.HasValue && (second.Value & 0x7fffffff) == 0 ? 1 : -1;
                if (zero < 0) return false;
                var other = operands[1 - zero]; var domain = KnownDomain(other);
                if (domain == null || domain.Length == 0) return false;
                foreach (uint bits in domain)
                    if (++arithmeticDomainChecks > 16384 || !Normal(bits)) return false;
                // D3D requires normal x +/- zero to preserve x exactly. A
                // complete normal domain excludes signed-zero changes, denorm
                // flushing and NaN payload changes. This identity also holds
                // for precise ADD; it removes no permitted result bit pattern.
                value = other; return true;
            }
            private bool TryUnitMad(string operation, Value[] operands, out Value value)
            {
                value = null; if (operands.Length != 3) return false;
                uint? first = expressions[operands[0].Expression].Literal, second = expressions[operands[1].Expression].Literal;
                int factor = first == 0x3f800000 || first == 0xbf800000 ? 0 : second == 0x3f800000 || second == 0xbf800000 ? 1 : -1;
                if (factor < 0) return false;
                uint multiplier = expressions[operands[factor].Expression].Literal.Value;
                var input = operands[1 - factor]; var addend = operands[2];
                var inputDomain = KnownDomain(input); var addendDomain = KnownDomain(addend);
                if (inputDomain == null || addendDomain == null || inputDomain.Any(bits => !NormalOrZero(bits))) return false;
                foreach (uint x in inputDomain)
                foreach (uint c in addendDomain)
                {
                    if (++arithmeticDomainChecks > 16384) return false;
                    if (!UniqueNormalSum(multiplier == 0xbf800000 ? x ^ 0x80000000 : x, c, out _)) return false;
                }
                // A zero product is safe here only because every resulting sum
                // is normal and nonzero; its zero sign cannot affect that sum.
                // Preserve ADD and its precision unless a complete normal
                // domain proves its exact identity. In particular mad(x,1,+0)
                // never becomes x for arbitrary signed zero.
                Value product = multiplier == 0xbf800000 ? Apply("fneg", input) : input;
                value = Apply(operation.StartsWith("precise:", StringComparison.Ordinal) ? "precise:add" : "add", product, addend); return true;
            }
            private bool UniqueSums(Value first, Value second)
            {
                var left = KnownDomain(first); var right = KnownDomain(second);
                if (left == null || right == null) return false;
                foreach (uint a in left)
                foreach (uint b in right)
                {
                    if (++arithmeticDomainChecks > 16384 || !UniqueNormalSum(a, b, out _)) return false;
                }
                return true;
            }
        }
    }
}
