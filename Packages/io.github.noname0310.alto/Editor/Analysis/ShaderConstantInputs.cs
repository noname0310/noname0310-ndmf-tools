using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Noname.AvatarTools.Analysis
{
    // This is deliberately not an HLSL parser. Only scalar globals
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

        // Unity can retain diagnostics directives from includes while removing
        // the same directives after inlining. They do not affect generated code.
        // Preserve all other pragmas, and preserve offsets for declaration spans.
        internal static string WithoutDiagnosticPragmas(string source)
        {
            var tokens = PpLexer.Lex(source, trackOffsets: true); char[] result = null;
            for (int i = 0; i + 2 < tokens.Count; i++)
            {
                if (tokens[i].Text != "#" || tokens[i + 1].Text != "pragma" || tokens[i + 2].Text != "warning") continue;
                int end = i + 3; while (end < tokens.Count && tokens[end].Kind != PpTokenKind.Newline) end++;
                int stop = tokens[end - 1].Offset + tokens[end - 1].Length;
                result ??= source.ToCharArray();
                for (int p = tokens[i].Offset; p < stop; p++) if (result[p] != '\r' && result[p] != '\n') result[p] = ' ';
                i = end;
            }
            return result == null ? source : new string(result);
        }

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
            internal Plan(string source) { Source = new ShaderTextKey(source).Text; }
            internal string Apply(IReadOnlyDictionary<string, float> constants, out int specialized, bool floatingPointOnly = false,
                ISet<string> specializedNames = null, ISet<string> readOnlyNames = null, ISet<string> remainingNames = null)
            {
                specialized = 0;
                if (constants == null || constants.Count == 0) return Source;
                StringBuilder output = null; int cursor = 0;
                foreach (var declaration in Declarations)
                {
                    if (floatingPointOnly && declaration.Type != "float" && declaration.Type != "half" ||
                        !constants.TryGetValue(declaration.Name, out float value) || !MotionAnalysis.Finite(value) ||
                        !Literal(declaration.Type, value, out string literal))
                    {
                        if (!floatingPointOnly || declaration.Type == "float" || declaration.Type == "half")
                            remainingNames?.Add(declaration.Name);
                        continue;
                    }
                    output ??= new StringBuilder(Source.Length);
                    output.Append(Source, cursor, declaration.Start - cursor);
                    output.Append(readOnlyNames == null || readOnlyNames.Contains(declaration.Name) ? "static const " : "static ")
                        .Append(declaration.Type).Append(' ').Append(declaration.Name)
                        .Append(" = ").Append(literal).Append(';');
                    cursor = declaration.Start + declaration.Length; specialized++;
                    specializedNames?.Add(declaration.Name);
                }
                return output == null ? Source : output.Append(Source, cursor, Source.Length - cursor).ToString();
            }

            // A shared imported pass may specialize fewer inputs than its caller:
            // all callers of that pass contribute to its constant intersection.
            // Accept only an exact subset of the authorized declaration edits.
            // Every other token, including control flow and shader pragmas, stays
            // identical. This is a source certificate, not a numeric proof.
            internal bool MatchesSpecialization(string candidate, IReadOnlyDictionary<string, float> constants,
                out int specialized)
            {
                specialized = 0;
                var original = PpLexer.Lex(WithoutDiagnosticPragmas(Source), trackOffsets: true).Where(t => t.Kind != PpTokenKind.Newline).ToArray();
                var actual = PpLexer.Lex(WithoutDiagnosticPragmas(ExpandScalarDeclarationLists(Normalize(candidate)))).Where(t => t.Kind != PpTokenKind.Newline).ToArray();
                int first = 0, second = 0;
                bool SameUntil(int end)
                {
                    while (first < original.Length && original[first].Offset < end)
                        if (second >= actual.Length || original[first++].Text != actual[second++].Text) return false;
                    return true;
                }
                foreach (var declaration in Declarations)
                {
                    if (!SameUntil(declaration.Start)) return false;
                    if ((declaration.Type != "float" && declaration.Type != "half") || constants == null ||
                        !constants.TryGetValue(declaration.Name, out float value) || !MotionAnalysis.Finite(value) ||
                        !Literal(declaration.Type, value, out string literal)) continue;
                    var replacement = PpLexer.Lex("static const " + declaration.Type + " " + declaration.Name + " = " + literal + ";");
                    bool matches = second + replacement.Count <= actual.Length;
                    for (int i = 0; matches && i < replacement.Count; i++) matches = replacement[i].Text == actual[second + i].Text;
                    if (!matches) continue;
                    while (first < original.Length && original[first].Offset < declaration.Start + declaration.Length) first++;
                    second += replacement.Count; specialized++;
                }
                return SameUntil(int.MaxValue) && second == actual.Length;
            }
        }

        internal static Plan PrepareCached(string source)
        {
            if (!ContentFactCache.Enabled) return Prepare(source);
            const string kind = "shader declarations";
            var cache = ContentFactCache.Shared;
            string key = cache.Key(kind, ContentFactCache.TextDigest(source));
            if (cache.TryRead(kind, key, ReadPlan, out Plan previous)) return previous;
            var plan = Prepare(source);
            cache.Store(kind, key, writer =>
            {
                writer.Write(plan.Source); writer.Write(plan.Declarations.Count);
                foreach (var declaration in plan.Declarations)
                { writer.Write(declaration.Start); writer.Write(declaration.Length); writer.Write(declaration.Type); writer.Write(declaration.Name); }
                writer.Write(plan.UniformNames.Length);
                foreach (string name in plan.UniformNames) writer.Write(name);
            });
            return plan;
        }

        private static Plan ReadPlan(BinaryReader reader)
        {
            var plan = new Plan(reader.ReadString()); int count = ContentFactCache.Count(reader);
            for (int i = 0; i < count; i++)
            {
                int start = reader.ReadInt32(), length = reader.ReadInt32();
                if (start < 0 || length < 0 || (long)start + length > plan.Source.Length) throw new InvalidDataException();
                plan.Declarations.Add((start, length, reader.ReadString(), reader.ReadString()));
            }
            plan.UniformNames = new string[ContentFactCache.Count(reader)];
            for (int i = 0; i < plan.UniformNames.Length; i++) plan.UniformNames[i] = reader.ReadString();
            return plan;
        }

        internal static Plan Prepare(string source)
        {
            using var cost = AnalysisCost.Measure("Shader specialization preparation");
            source = ExpandScalarDeclarationLists(Normalize(source));
            var plan = new Plan(source);
            // Preserve compiler pragmas in the result but exclude them from the
            // declaration scanner, including identifiers in file-name strings.
            string scan = ScanDirectives(source, false);
            var tokens = Tokenize(scan);
            var assignedNames = new HashSet<string>(StringComparer.Ordinal);
            // Literal assignment targets inside a macro body are writes too.
            // Stripping all directives here hid writes supplied by custom shader
            // hooks. Macro-generated names still require native certification.
            var assignmentTokens = Tokenize(source);
            for (int i = 0; i + 2 < assignmentTokens.Count; i++)
            {
                string next = assignmentTokens[i + 1].Text, after = assignmentTokens[i + 2].Text;
                if (next == "=" && after != "=" || "+-*/%&|^".Contains(next) && after == "=" ||
                    (next == "+" || next == "-") && after == next) assignedNames.Add(assignmentTokens[i].Text);
                if ((assignmentTokens[i].Text == "+" || assignmentTokens[i].Text == "-") && next == assignmentTokens[i].Text)
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
                    if (typeIndex + 2 == i && source.IndexOf('#', start, end - start) < 0)
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

        // Canonicalize only the grammar [uniform] scalar name (, name)+ ;.
        // Splitting these globals preserves declaration order, type and storage.
        // Initializers, arrays, register annotations, aggregate members and any
        // declaration crossing a directive remain untouched. The same canonical
        // form is used by the source certificate, including partial replacement.
        private static string ExpandScalarDeclarationLists(string source)
        {
            var tokens = Tokenize(ScanDirectives(source, false));
            int braces = 0, parentheses = 0, statement = 0, copied = 0;
            StringBuilder output = null;
            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i].Text;
                if (token == "{") { braces++; continue; }
                if (token == "}") { braces--; if (braces == 0) statement = i + 1; continue; }
                if (token == "(") parentheses++;
                if (token == ")") parentheses--;
                if (token != ";" || braces != 0 || parentheses != 0) continue;
                int first = statement; statement = i + 1;
                bool uniform = tokens[first].Text == "uniform";
                int typeIndex = first + (uniform ? 1 : 0);
                if (typeIndex + 4 > i || (i - typeIndex) % 2 != 0) continue;
                string type = tokens[typeIndex].Text;
                if (type != "float" && type != "half" && type != "int" && type != "uint" && type != "bool") continue;
                int start = tokens[first].Index, end = tokens[i].Index + tokens[i].Length;
                if (source.IndexOf('#', start, end - start) >= 0) continue;
                bool supported = true;
                for (int j = typeIndex + 1; supported && j < i; j++)
                {
                    string text = tokens[j].Text;
                    if ((j - typeIndex) % 2 == 0) supported = text == ",";
                    else
                    {
                        supported = text.Length != 0 && PpLexer.IdentifierStart(text[0]);
                        for (int k = 1; supported && k < text.Length; k++) supported = PpLexer.IdentifierPart(text[k]);
                    }
                }
                if (!supported) continue;
                output ??= new StringBuilder(source.Length);
                output.Append(source, copied, start - copied);
                for (int j = typeIndex + 1; j < i; j += 2)
                {
                    if (j != typeIndex + 1) output.Append(' ');
                    if (uniform) output.Append("uniform ");
                    output.Append(type).Append(' ').Append(tokens[j].Text).Append(';');
                }
                copied = end;
            }
            return output == null ? source : output.Append(source, copied, source.Length - copied).ToString();
        }

        private static bool Literal(string type, float value, out string literal)
        {
            literal = null;
            if (type == "float" || type == "half")
            {
                // Native HLSL constant folding canonicalizes negative zero and
                // flushes subnormal literals. Even an asfloat initializer loses
                // negative zero. Runtime uniforms preserve those observable bits.
                int bits = BitConverter.SingleToInt32Bits(value);
                if ((bits & 0x7fffffff) < 0x00800000 && bits != 0)
                    return false;
                string number = value.ToString("R", CultureInfo.InvariantCulture);
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
