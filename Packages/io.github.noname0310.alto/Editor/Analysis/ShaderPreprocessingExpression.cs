using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Noname.AvatarTools.Analysis
{
    internal readonly struct PpValue
    {
        internal readonly ulong? Bits;
        internal readonly bool Unsigned;
        internal readonly BigInteger Dependencies;
        internal bool? Truth => Bits.HasValue ? Bits.Value != 0 : null;
        internal PpValue(ulong? bits, BigInteger dependencies = default, bool unsigned = false)
        { Bits = bits; Dependencies = dependencies; Unsigned = unsigned; }
        internal PpValue Taint(BigInteger dependencies) => new(Bits, Dependencies | dependencies, Unsigned);
    }

    // Unity's caching preprocessor and the platform HLSL preprocessors differ in
    // signedness and overflow rules. Only a value agreed on by both integer models
    // can remove a branch. Undefined arithmetic is unknown, never an invented zero.
    internal struct PpExpression
    {
        private readonly IReadOnlyList<PpToken> tokens;
        private readonly bool unsignedRules;
        private int cursor;
        private PpExpression(IReadOnlyList<PpToken> tokens, bool unsignedRules)
        { this.tokens = tokens; this.unsignedRules = unsignedRules; cursor = 0; }

        internal static PpValue Evaluate(IReadOnlyList<PpToken> tokens)
        {
            var a = new PpExpression(tokens, false).Parse();
            var b = new PpExpression(tokens, true).Parse();
            return new PpValue(a.Bits == b.Bits ? a.Bits : null, a.Dependencies | b.Dependencies);
        }
        private PpValue Parse()
        {
            var result = Conditional();
            if (cursor != tokens.Count) throw new PpException("Unexpected token in preprocessor expression: " + tokens[cursor].Text);
            return result;
        }
        private bool Take(string value)
        { if (cursor < tokens.Count && tokens[cursor].Text == value) { cursor++; return true; } return false; }
        private void Require(string value) { if (!Take(value)) throw new PpException("Expected " + value + " in preprocessor expression"); }
        private PpValue Conditional()
        {
            var condition = Binary(1);
            if (!Take("?")) return condition;
            var yes = Conditional(); Require(":"); var no = Conditional();
            if (condition.Truth.HasValue)
            {
                var selected = condition.Truth.Value ? yes : no;
                return new PpValue(selected.Bits, selected.Dependencies | condition.Dependencies, yes.Unsigned || no.Unsigned);
            }
            return new PpValue(yes.Bits == no.Bits ? yes.Bits : null,
                yes.Dependencies | no.Dependencies | (yes.Bits.HasValue && yes.Bits == no.Bits ? BigInteger.Zero : condition.Dependencies),
                yes.Unsigned || no.Unsigned);
        }
        private static int Precedence(string op) => op switch
        {
            "||" => 1, "&&" => 2, "|" => 3, "^" => 4, "&" => 5, "==" or "!=" => 6,
            "<" or ">" or "<=" or ">=" => 7, "<<" or ">>" => 8, "+" or "-" => 9, "*" or "/" or "%" => 10, _ => 0
        };
        private PpValue Binary(int minimum)
        {
            var left = Unary();
            while (cursor < tokens.Count && Precedence(tokens[cursor].Text) >= minimum)
            {
                string op = tokens[cursor++].Text;
                left = Apply(op, left, Binary(Precedence(op) + 1));
            }
            return left;
        }
        private PpValue Unary()
        {
            if (Take("(")) { var nested = Conditional(); Require(")"); return nested; }
            if (cursor == tokens.Count) throw new PpException("Incomplete preprocessor expression");
            string op = tokens[cursor].Text;
            if (op == "!" || op == "~" || op == "+" || op == "-")
            {
                cursor++; var value = Unary();
                if (!value.Bits.HasValue) return new PpValue(null, value.Dependencies, op != "!" && value.Unsigned);
                ulong bits = value.Bits.Value;
                if (op == "-" && !value.Unsigned && bits == 0x8000000000000000UL) return new PpValue(null, value.Dependencies);
                return Portable(new PpValue(op switch { "!" => bits == 0 ? 1UL : 0UL, "~" => ~bits, "-" => unchecked(0UL - bits), _ => bits },
                    value.Dependencies, op != "!" && value.Unsigned));
            }
            var token = tokens[cursor++];
            if (token.Kind == PpTokenKind.Symbol) return new PpValue(null, token.Dependencies);
            if (token.Kind == PpTokenKind.Identifier) return new PpValue(0, token.Dependencies);
            if (token.Kind == PpTokenKind.Character) return Character(token);
            if (token.Kind != PpTokenKind.Number) throw new PpException("Non-integer preprocessor operand: " + token.Text);
            string text = token.Text; int end = text.Length;
            while (end > 0 && "uUlL".IndexOf(text[end - 1]) >= 0) end--;
            string suffix = text.Substring(end).ToLowerInvariant();
            if (suffix != "" && suffix != "u" && suffix != "l" && suffix != "ul" && suffix != "lu" && suffix != "ll" && suffix != "ull" && suffix != "llu")
                throw new PpException("Invalid integer suffix: " + token.Text);
            string digits = text.Substring(0, end); int radix = 10, start = 0;
            if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { radix = 16; start = 2; }
            else if (digits.Length > 1 && digits[0] == '0') { radix = 8; start = 1; }
            if (start == digits.Length) throw new PpException("Invalid integer constant: " + text);
            BigInteger integer = 0;
            for (int i = start; i < digits.Length; i++)
            {
                int digit = "0123456789abcdef".IndexOf(char.ToLowerInvariant(digits[i]));
                if (digit < 0 || digit >= radix) throw new PpException("Invalid integer constant: " + text);
                integer = integer * radix + digit;
            }
            if (integer > ulong.MaxValue) return new PpValue(null, token.Dependencies);
            return Portable(new PpValue((ulong)integer, token.Dependencies, unsignedRules && (suffix.Contains("u") || integer > long.MaxValue)));
        }
        private static PpValue Character(PpToken token)
        {
            string text = token.Text; int quote = text.IndexOf('\''); string body = text.Substring(quote + 1, text.Length - quote - 2);
            ulong? value = body.Length == 1 && body[0] <= 127 ? (ulong)body[0] : null;
            if (body.StartsWith("\\", StringComparison.Ordinal))
            {
                string escape = body.Substring(1);
                if (escape.Length == 1) value = escape[0] switch
                { 'a' => 7UL, 'b' => 8UL, 'f' => 12UL, 'n' => 10UL, 'r' => 13UL, 't' => 9UL, 'v' => 11UL, '\\' => 92UL, '\'' => 39UL, '"' => 34UL, '?' => 63UL, _ => null };
                if (escape.StartsWith("x", StringComparison.Ordinal) && ulong.TryParse(escape.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) value = hex <= 127 ? hex : null;
                else if (escape.Length <= 3 && escape.All(c => c >= '0' && c <= '7'))
                { ulong octal = 0; foreach (char c in escape) octal = octal * 8 + (ulong)(c - '0'); value = octal <= 127 ? octal : null; }
            }
            return new PpValue(value, token.Dependencies);
        }
        private static PpValue Apply(string op, PpValue left, PpValue right)
        {
            BigInteger dependencies = left.Dependencies | right.Dependencies; bool unsigned = left.Unsigned || right.Unsigned;
            if (op == "&&" && (left.Truth == false || right.Truth == false))
                return new PpValue(0, (left.Truth == false ? left : right).Dependencies);
            if (op == "||" && (left.Truth == true || right.Truth == true))
                return new PpValue(1, (left.Truth == true ? left : right).Dependencies);
            if (!left.Bits.HasValue || !right.Bits.HasValue) return new PpValue(null, dependencies, unsigned);
            ulong a = left.Bits.Value, b = right.Bits.Value; long x = unchecked((long)a), y = unchecked((long)b);
            try
            {
                ulong? value = op switch
                {
                    "+" => unsigned ? unchecked(a + b) : unchecked((ulong)checked(x + y)),
                    "-" => unsigned ? unchecked(a - b) : unchecked((ulong)checked(x - y)),
                    "*" => unsigned ? unchecked(a * b) : unchecked((ulong)checked(x * y)),
                    "/" => b == 0 ? null : unsigned ? a / b : unchecked((ulong)(x / y)),
                    "%" => b == 0 ? null : unsigned ? a % b : unchecked((ulong)(x % y)),
                    "<<" => b > 63 || !left.Unsigned && (x < 0 || x > (long.MaxValue >> (int)b)) ? null : a << (int)b,
                    ">>" => b > 63 || !left.Unsigned && x < 0 ? null : a >> (int)b,
                    "&" => a & b, "^" => a ^ b, "|" => a | b,
                    "==" => a == b ? 1UL : 0, "!=" => a != b ? 1UL : 0,
                    "<" => (unsigned ? a < b : x < y) ? 1UL : 0, ">" => (unsigned ? a > b : x > y) ? 1UL : 0,
                    "<=" => (unsigned ? a <= b : x <= y) ? 1UL : 0, ">=" => (unsigned ? a >= b : x >= y) ? 1UL : 0,
                    "&&" => a != 0 && b != 0 ? 1UL : 0, "||" => a != 0 || b != 0 ? 1UL : 0,
                    _ => throw new PpException("Unknown preprocessor operator: " + op)
                };
                bool resultUnsigned = op == "<<" || op == ">>" ? left.Unsigned :
                    (Precedence(op) >= 8 || op == "&" || op == "^" || op == "|") && unsigned;
                return Portable(new PpValue(value, dependencies, resultUnsigned));
            }
            catch (OverflowException) { return new PpValue(null, dependencies, unsigned); }
        }
        private static PpValue Portable(PpValue value)
        {
            // Legacy preprocessors also differ in integer width. Keep both
            // successors when a result requires more than the common signed
            // 32-bit range instead of guessing a compiler's wraparound behavior.
            if (!value.Bits.HasValue) return value;
            long signed = unchecked((long)value.Bits.Value);
            if (value.Unsigned ? value.Bits.Value > int.MaxValue : signed < int.MinValue || signed > int.MaxValue)
                return new PpValue(null, value.Dependencies, value.Unsigned);
            return value;
        }
    }
}
