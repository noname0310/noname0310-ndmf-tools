using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Noname.AvatarTools.Analysis
{
    internal enum PpTokenKind { Identifier, Number, String, Character, Punctuation, Newline, Symbol }
    internal enum PpDialect { Standard, UnityCaching }
    [Flags]
    internal enum PpDialectEffects
    {
        None = 0, MultilineCall = 1, Variadic = 2, Conditional = 4,
        LiteralStringification = 8, ArgumentPaste = 16, ArgumentSpacing = 32, Stringification = 64, ContinuedLineDirective = 128
    }

    // Immutable macro hide sets are shared by replacement tokens. The common
    // empty/single-macro cases are inline values and allocate no collection.
    internal readonly struct PpHideSet
    {
        private readonly string single;
        private readonly HashSet<string> many;
        private PpHideSet(string single) { this.single = single; many = null; }
        private PpHideSet(HashSet<string> owned)
        {
            single = null; many = owned.Count > 1 ? owned : null;
            if (owned.Count == 1) foreach (string value in owned) { single = value; break; }
        }
        internal bool Contains(string name) => many != null ? many.Contains(name) : single != null && single == name;
        internal PpHideSet With(string name)
        {
            if (Contains(name)) return this;
            if (many == null) return single == null ? new PpHideSet(name) :
                new PpHideSet(new HashSet<string>(StringComparer.Ordinal) { single, name });
            var result = new HashSet<string>(many, StringComparer.Ordinal) { name }; return new PpHideSet(result);
        }
        internal PpHideSet Union(PpHideSet other)
        {
            if (many == null) return single == null ? other : other.With(single);
            if (other.many == null) return other.single == null ? this : With(other.single);
            if (ReferenceEquals(many, other.many) || other.many.IsSubsetOf(many)) return this;
            if (many.IsSubsetOf(other.many)) return other;
            var result = new HashSet<string>(many, StringComparer.Ordinal); result.UnionWith(other.many); return new PpHideSet(result);
        }
        internal PpHideSet Intersect(PpHideSet other)
        {
            if (many == null) return single != null && other.Contains(single) ? this : default;
            if (other.many == null) return other.single != null && Contains(other.single) ? other : default;
            if (ReferenceEquals(many, other.many) || many.IsSubsetOf(other.many)) return this;
            if (other.many.IsSubsetOf(many)) return other;
            var result = new HashSet<string>(many, StringComparer.Ordinal); result.IntersectWith(other.many); return new PpHideSet(result);
        }
    }

    internal sealed class PpToken
    {
        internal readonly string Text, File;
        internal readonly PpTokenKind Kind;
        internal readonly int Line;
        internal readonly int Offset, Length;
        internal readonly bool Space;
        internal readonly BigInteger Dependencies;
        internal readonly PpHideSet Hidden;
        internal PpToken(string text, PpTokenKind kind, string file = "", int line = 1, bool space = false,
            BigInteger dependencies = default, PpHideSet hidden = default, int offset = -1, int length = 0)
        { Text = text; Kind = kind; File = file; Line = line; Space = space; Dependencies = dependencies; Hidden = hidden; Offset = offset; Length = length; }
        internal PpToken At(PpToken site, BigInteger dependencies, PpHideSet hide = default)
            => new(Text, Kind, site.File, site.Line, Space, Dependencies | dependencies, Hidden.Union(hide));
        internal PpToken Taint(BigInteger dependencies)
        {
            if (dependencies.IsZero || Dependencies == dependencies) return this;
            var combined = Dependencies.IsZero ? dependencies : Dependencies | dependencies;
            return combined == Dependencies ? this : new PpToken(Text, Kind, File, Line, Space, combined, Hidden);
        }
        internal PpToken WithSpace(bool space) => space == Space && Offset == -1 && Length == 0 ? this :
            new PpToken(Text, Kind, File, Line, space, Dependencies, Hidden);
    }

    internal sealed class PpException : Exception { internal PpException(string message) : base(message) { } }
    internal sealed class PpBudget
    {
        // Shared by every abstract successor. If the standard execution never
        // observes a dialect-dependent operation, its trace covers both models.
        internal PpDialectEffects DialectEffects;
        internal bool DialectSensitive => (DialectEffects & ~(PpDialectEffects.ArgumentSpacing | PpDialectEffects.Stringification)) != 0 ||
            (DialectEffects & (PpDialectEffects.ArgumentSpacing | PpDialectEffects.Stringification)) ==
                (PpDialectEffects.ArgumentSpacing | PpDialectEffects.Stringification);
        internal long ExpansionTicks, TextTicks, JoinTicks;
        internal int ExpansionCalls, Choices;
        internal int TextProjectionHits;
        private int remaining = 1000000;
        internal int Remaining => remaining;
        internal void Charge(int work = 1)
        { if ((remaining -= work) < 0) throw new PpException("Preprocessor total work limit exceeded"); }
    }

    // Translation phases: splice physical lines, then recognize comments and
    // preprocessing tokens. Locations retain physical line numbers for __LINE__.
    internal static class PpLexer
    {
        private static readonly string[] Operators = { "%:%:", ">>=", "<<=", "...", "##", "&&", "||", "==", "!=", "<=", ">=", "<<", ">>",
            "++", "--", "->", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "::", "<:", ":>", "<%", "%>", "%:" };
        private static readonly string[] Characters = CharacterTokens();
        private static string[] CharacterTokens()
        {
            var result = new string[128];
            for (int i = 0; i < result.Length; i++) result[i] = ((char)i).ToString();
            return result;
        }
        internal static bool IdentifierStart(char c) => c == '_' || c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z';
        internal static bool IdentifierPart(char c) => IdentifierStart(c) || c >= '0' && c <= '9';

        internal static List<PpToken> Lex(string source, string file = "", int firstLine = 1, bool trackOffsets = false)
        {
            string input = source; List<int> lines = null, offsets = null; int line = firstLine;
            // Most native output and token-paste operands need no translation.
            // Keep their source directly; only physical line splicing/CR conversion
            // needs a per-character position map.
            if (source.IndexOf('\r') >= 0 || source.IndexOf("\\\n", StringComparison.Ordinal) >= 0)
            {
                var text = new StringBuilder(source.Length); lines = new List<int>(source.Length);
                if (trackOffsets) offsets = new List<int>(source.Length);
                for (int i = 0; i < source.Length; i++)
                {
                    char c = source[i];
                    if (c == '\\' && i + 1 < source.Length && (source[i + 1] == '\n' || source[i + 1] == '\r' && i + 2 < source.Length && source[i + 2] == '\n'))
                    { i += source[i + 1] == '\r' ? 2 : 1; line++; continue; }
                    if (c == '\r') { if (i + 1 < source.Length && source[i + 1] == '\n') i++; c = '\n'; }
                    text.Append(c); lines.Add(line); offsets?.Add(i); if (c == '\n') line++;
                }
                input = text.ToString(); line = firstLine;
            }
            var result = new List<PpToken>();
            LexInto(input, 0, input.Length, file, firstLine, trackOffsets, lines, offsets, result);
            return result;
        }

        // Native HLSL is inspected a line at a time for #line mappings. Read a
        // source range into a reusable buffer instead of splitting/copying the
        // whole program and allocating a lexer list and array for each line.
        internal static void LexLine(string source, int start, int length, string file, int line, List<PpToken> output)
        {
            if (source.IndexOf('\r', start, length) >= 0)
            {
                foreach (var token in Lex(source.Substring(start, length), file, line))
                    if (token.Kind != PpTokenKind.Newline) output.Add(token);
            }
            else LexInto(source, start, start + length, file, line, false, null, null, output);
        }

        private static void LexInto(string input, int begin, int end, string file, int line, bool trackOffsets,
            List<int> lines, List<int> offsets, List<PpToken> result)
        {
            bool space = false;
            for (int i = begin; i < end;)
            {
                char c = input[i];
                if (c == '\ufeff' && i == begin) { i++; continue; }
                if (c == '\n') { result.Add(new PpToken("\n", PpTokenKind.Newline, file, lines == null ? line : lines[i], space)); i++; line++; space = false; continue; }
                if (char.IsWhiteSpace(c)) { space = true; i++; continue; }
                if (c == '/' && i + 1 < end && input[i + 1] == '/')
                { while (i < end && input[i] != '\n') i++; space = true; continue; }
                if (c == '/' && i + 1 < end && input[i + 1] == '*')
                {
                    i += 2; bool closed = false;
                    while (i < end)
                    {
                        if (input[i] == '*' && i + 1 < end && input[i + 1] == '/') { i += 2; closed = true; break; }
                        if (input[i] == '\n') { result.Add(new PpToken("\n", PpTokenKind.Newline, file, lines == null ? line : lines[i])); line++; }
                        i++;
                    }
                    if (!closed) throw new PpException("Unterminated block comment in " + file);
                    space = true; continue;
                }
                int start = i; PpTokenKind kind;
                bool utf8Literal = c == 'u' && i + 2 < end && input[i + 1] == '8' && input[i + 2] == '"';
                bool wideLiteral = (c == 'L' || c == 'u' || c == 'U') && i + 1 < end && (input[i + 1] == '\'' || input[i + 1] == '"');
                if (c == '"' || c == '\'' || wideLiteral || utf8Literal)
                {
                    if (wideLiteral) i++;
                    if (utf8Literal) i += 2;
                    char quote = input[i++]; kind = quote == '"' ? PpTokenKind.String : PpTokenKind.Character; bool closed = false;
                    while (i < end)
                    {
                        char next = input[i++];
                        if (next == '\\' && i < end) { i++; continue; }
                        if (next == quote) { closed = true; break; }
                        if (next == '\n') break;
                    }
                    if (!closed) throw new PpException("Unterminated literal in " + file + ":" + (lines == null ? line : lines[start]));
                }
                else if (IdentifierStart(c))
                { kind = PpTokenKind.Identifier; while (++i < end && IdentifierPart(input[i])) { } }
                else if (char.IsDigit(c) || c == '.' && i + 1 < end && char.IsDigit(input[i + 1]))
                {
                    kind = PpTokenKind.Number; i++;
                    while (i < end)
                    {
                        char next = input[i];
                        if (IdentifierPart(next) || next == '.') { i++; continue; }
                        if ((next == '+' || next == '-') && (input[i - 1] == 'e' || input[i - 1] == 'E' || input[i - 1] == 'p' || input[i - 1] == 'P')) { i++; continue; }
                        break;
                    }
                }
                else
                {
                    kind = PpTokenKind.Punctuation;
                    int length = 1;
                    for (int o = 0; o < Operators.Length; o++)
                    {
                        string op = Operators[o];
                        if (op[0] != c || i + op.Length > end || string.CompareOrdinal(input, i, op, 0, op.Length) != 0) continue;
                        length = op.Length; break;
                    }
                    i += length;
                }
                string value = i - start == 1 && input[start] < Characters.Length ? Characters[input[start]] : input.Substring(start, i - start);
                value = value switch { "%:" => "#", "%:%:" => "##", "<:" => "[", ":>" => "]", "<%" => "{", "%>" => "}", _ => value };
                result.Add(new PpToken(value, kind, file, lines == null ? line : lines[start], space,
                    offset: !trackOffsets ? -1 : offsets == null ? start : offsets[start],
                    length: !trackOffsets ? 0 : offsets == null ? i - start : offsets[i - 1] - offsets[start] + 1)); space = false;
            }
        }

        internal static string Spell(IEnumerable<PpToken> tokens) => string.Join(" ", tokens.Select(t => t.Text));
        internal static string Stringify(IReadOnlyList<PpToken> tokens)
        {
            var result = new StringBuilder("\"");
            for (int i = 0; i < tokens.Count; i++)
            {
                if (i != 0 && tokens[i].Space) result.Append(' ');
                foreach (char c in tokens[i].Text)
                {
                    if ((tokens[i].Kind == PpTokenKind.String || tokens[i].Kind == PpTokenKind.Character) && (c == '\\' || c == '"')) result.Append('\\');
                    result.Append(c);
                }
            }
            return result.Append('"').ToString();
        }
    }

    internal sealed class PpMacro
    {
        internal readonly string Name;
        internal readonly string[] Parameters;
        internal readonly bool Variadic;
        internal readonly PpToken[] Replacement;
        internal readonly BigInteger Dependencies;
        internal readonly bool Pastes, Stringifies;
        internal bool Function => Parameters != null;
        private string identity;
        internal string Identity => identity ??= (Function ? "(" + string.Join(",", Parameters) + ")" : "object") +
            (Variadic ? "..." : "") + ":" + string.Join("|", Replacement.Select(t => (int)t.Kind + ":" + (t.Space ? " " : "") + t.Text));
        internal PpMacro(string name, PpToken[] replacement, string[] parameters = null, bool variadic = false, BigInteger dependencies = default)
        {
            Name = name; Replacement = replacement; Parameters = parameters; Variadic = variadic; Dependencies = dependencies;
            foreach (var token in replacement) { Pastes |= token.Text == "##"; Stringifies |= token.Text == "#"; }
        }
    }

    // Standard substitution/rescanning with per-token hide sets. A macro's body
    // is expanded at the use site, never when its definition is encountered.
    internal struct PpExpander
    {
        private readonly Func<string, PpMacro> lookup;
        private readonly Func<PpToken, PpToken> builtin;
        private readonly PpDialect dialect;
        private readonly Func<string, PpValue> defined;
        private readonly PpBudget budget;
        private int steps;
        internal PpExpander(Func<string, PpMacro> lookup, Func<PpToken, PpToken> builtin = null, PpDialect dialect = PpDialect.Standard,
            Func<string, PpValue> defined = null, PpBudget budget = null)
        { this.lookup = lookup; this.builtin = builtin; this.dialect = dialect; this.defined = defined; this.budget = budget ?? new PpBudget(); steps = 0; }

        internal List<PpToken> Expand(IEnumerable<PpToken> source, int depth = 0)
        {
            if (depth > 128) throw new PpException("Macro expansion depth limit exceeded");
            var work = new List<PpToken>(source is ICollection<PpToken> collection ? collection.Count : 0); bool newline = false;
            foreach (var token in source)
                if (token.Kind == PpTokenKind.Newline) newline = true;
                else { work.Add(newline ? token.WithSpace(true) : token); newline = false; }
            for (int cursor = 0; cursor < work.Count;)
            {
                budget.Charge();
                if (++steps > 200000 || work.Count > 100000) throw new PpException("Macro expansion work limit exceeded");
                var token = work[cursor];
                if (token.Kind != PpTokenKind.Identifier) { cursor++; continue; }
                if (token.Text == "_Pragma" || token.Text == "__pragma")
                    throw new PpException("Pragma operators require compiler preprocessing");
                if (token.Text == "defined" && defined != null)
                {
                    int operand = cursor + 1; bool parentheses = operand < work.Count && work[operand].Text == "(";
                    if (parentheses) operand++;
                    if (operand >= work.Count || work[operand].Kind != PpTokenKind.Identifier) throw new PpException("defined requires an unexpanded identifier");
                    int end = operand + 1;
                    if (parentheses && (end == work.Count || work[end++].Text != ")")) throw new PpException("Unterminated defined operand");
                    var value = defined(work[operand].Text);
                    var result = new PpToken(value.Bits.HasValue ? value.Bits.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<defined>",
                        value.Bits.HasValue ? PpTokenKind.Number : PpTokenKind.Symbol, token.File, token.Line, token.Space,
                        token.Dependencies | work[operand].Dependencies | value.Dependencies);
                    work.RemoveRange(cursor, end - cursor); work.Insert(cursor++, result); continue;
                }
                var special = builtin?.Invoke(token);
                if (special != null) { work[cursor++] = special; continue; }
                var macro = lookup(token.Text);
                if (macro == null || token.Hidden.Contains(macro.Name)) { cursor++; continue; }
                int consumed = 1; List<List<PpToken>> arguments = null; PpToken closing = token;
                if (macro.Function)
                {
                    if (cursor + 1 == work.Count || work[cursor + 1].Text != "(") { cursor++; continue; }
                    // Unity's caching preprocessor recognizes function invocation
                    // only when the opening parenthesis is on the same logical line.
                    if (work[cursor + 1].File != token.File || work[cursor + 1].Line != token.Line)
                    {
                        budget.DialectEffects |= PpDialectEffects.MultilineCall;
                        if (dialect == PpDialect.UnityCaching) { cursor++; continue; }
                    }
                    arguments = Arguments(work, cursor + 1, out int end); closing = work[end]; consumed = end - cursor + 1;
                    if (macro.Parameters.Length == 0 && arguments.Count == 1 && arguments[0].Count == 0) arguments.Clear();
                    int required = macro.Parameters.Length - (macro.Variadic ? 1 : 0);
                    if (arguments.Count < required || !macro.Variadic && arguments.Count != required)
                        throw new PpException("Incorrect argument count for macro " + macro.Name);
                    if (macro.Variadic)
                    {
                        var tail = new List<PpToken>();
                        for (int a = required; a < arguments.Count; a++)
                        { if (a != required) tail.Add(new PpToken(",", PpTokenKind.Punctuation)); tail.AddRange(arguments[a]); }
                        arguments.RemoveRange(required, arguments.Count - required); arguments.Add(tail);
                    }
                }
                var hide = (macro.Function ? token.Hidden.Intersect(closing.Hidden) : token.Hidden).With(macro.Name);
                var replacement = Substitute(macro, arguments, token, depth);
                budget.Charge(replacement.Count + work.Count - cursor);
                if (replacement.Count != 0) replacement[0] = replacement[0].WithSpace(token.Space);
                work.RemoveRange(cursor, consumed);
                if (replacement.Count == 0 && cursor < work.Count && token.Space) work[cursor] = work[cursor].WithSpace(true);
                var expansionDependencies = token.Dependencies | macro.Dependencies;
                for (int r = 0; r < replacement.Count; r++) replacement[r] = replacement[r].At(token, expansionDependencies, hide);
                work.InsertRange(cursor, replacement);
                // The replacement is rescanned together with the remaining input.
                // This lets an object-like alias become a function-like invocation.
            }
            return work;
        }

        private static List<List<PpToken>> Arguments(List<PpToken> tokens, int opening, out int end)
        {
            var result = new List<List<PpToken>> { new() }; int nesting = 0;
            for (int i = opening + 1; i < tokens.Count; i++)
            {
                string value = tokens[i].Text;
                if (value == ")" && nesting == 0) { end = i; return result; }
                if (value == "," && nesting == 0) { result.Add(new List<PpToken>()); continue; }
                if (value == "(") nesting++; else if (value == ")") nesting--;
                result[result.Count - 1].Add(tokens[i]);
            }
            throw new PpException("Unterminated macro invocation");
        }

        private List<PpToken> Substitute(PpMacro macro, List<List<PpToken>> arguments, PpToken site, int depth)
        {
            if (!macro.Function && !macro.Pastes)
            {
                var direct = new List<PpToken>(macro.Replacement.Length);
                foreach (var token in macro.Replacement) if (token.Text.Length != 0) direct.Add(token);
                return direct;
            }
            var raw = new Dictionary<string, List<PpToken>>(StringComparer.Ordinal);
            var expanded = new Dictionary<string, List<PpToken>>(StringComparer.Ordinal);
            if (arguments != null) for (int i = 0; i < macro.Parameters.Length; i++) raw.Add(macro.Parameters[i], arguments[i]);
            var substituted = new List<PpToken>(); var body = macro.Replacement;
            for (int i = 0; i < body.Length; i++)
            {
                var token = body[i];
                if (token.Text == "#" && macro.Function)
                {
                    budget.DialectEffects |= PpDialectEffects.Stringification;
                    if (++i == body.Length || !raw.TryGetValue(body[i].Text, out var argument)) throw new PpException("Invalid stringification in " + macro.Name);
                    if (argument.Any(t => t.Kind == PpTokenKind.String || t.Text.Contains("\\")))
                    {
                        budget.DialectEffects |= PpDialectEffects.LiteralStringification;
                        if (dialect == PpDialect.UnityCaching) throw new PpException("Unity literal stringification requires compiler preprocessing");
                    }
                    substituted.Add(new PpToken(PpLexer.Stringify(argument), argument.Any(t => t.Kind == PpTokenKind.Symbol) ? PpTokenKind.Symbol : PpTokenKind.String, site.File, site.Line,
                        token.Space, argument.Aggregate(BigInteger.Zero, (d, t) => d | t.Dependencies)));
                    continue;
                }
                if (!raw.TryGetValue(token.Text, out var actual)) { substituted.Add(token); continue; }
                bool paste = i != 0 && body[i - 1].Text == "##" || i + 1 < body.Length && body[i + 1].Text == "##";
                if (paste) budget.DialectEffects |= PpDialectEffects.ArgumentPaste;
                if (!paste || dialect == PpDialect.UnityCaching)
                {
                    if (!expanded.TryGetValue(token.Text, out actual)) expanded.Add(token.Text, actual = Expand(raw[token.Text], depth + 1));
                }
                if (actual.Count == 0 && paste)
                {
                    if (dialect == PpDialect.UnityCaching) throw new PpException("Unity empty paste operand requires compiler preprocessing");
                    substituted.Add(new PpToken("", PpTokenKind.Punctuation));
                }
                else if (actual.Count != 0)
                {
                    if (actual[0].Space != token.Space) budget.DialectEffects |= PpDialectEffects.ArgumentSpacing;
                    substituted.Add(dialect == PpDialect.UnityCaching ? actual[0] : actual[0].WithSpace(token.Space));
                    for (int a = 1; a < actual.Count; a++) substituted.Add(actual[a]);
                }
            }
            var result = new List<PpToken>();
            for (int i = 0; i < substituted.Count; i++)
            {
                var token = substituted[i];
                if (token.Text != "##") { result.Add(token); continue; }
                if (result.Count == 0 || ++i == substituted.Count) throw new PpException("Token paste at a replacement-list boundary");
                var left = result[result.Count - 1]; var right = substituted[i]; result.RemoveAt(result.Count - 1);
                if (left.Kind == PpTokenKind.Symbol || right.Kind == PpTokenKind.Symbol)
                    throw new PpException("Token paste requires a concrete symbolic replacement");
                string joined = left.Text + right.Text;
                if (joined.Length == 0) { result.Add(left); continue; }
                var lexed = PpLexer.Lex(joined, site.File, site.Line);
                if (lexed.Count != 1 || lexed[0].Text != joined) throw new PpException("Token paste does not produce one preprocessing token: " + joined);
                var hidden = left.Hidden.Intersect(right.Hidden);
                result.Add(new PpToken(joined, lexed[0].Kind, site.File, site.Line, left.Space, left.Dependencies | right.Dependencies, hidden));
            }
            result.RemoveAll(t => t.Text.Length == 0); return result;
        }
    }
}
