using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Noname.AvatarTools.Analysis
{
    // Query-independent lexical facts for an exact preprocessed program. Preserve
    // the old scanner's ordering: whole-text validity comes first, then tokens
    // before the first unsupported directive, then that directive's failure.
    internal sealed class ShaderProgramInspection
    {
        internal const string InputIssue = "shader may depend on the queried inputs";
        private readonly HashSet<string> tokens = new(StringComparer.Ordinal);
        private readonly string prefixIssue, suffixIssue;
        private static readonly Regex Metadata = new(@"^#\s*(line\b|pragma\b|[0-9])", RegexOptions.CultureInvariant);
        internal int Cost { get; }

        internal ShaderProgramInspection(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            { prefixIssue = Unknown("a shader variant has no verified preprocessed program"); return; }
            for (int i = 0; i < code.Length; i++)
                if (char.IsControl(code[i]) && code[i] != '\r' && code[i] != '\n' && code[i] != '\t')
                { prefixIssue = Unknown("shader preprocessing returned malformed source text"); return; }
            if (code.Length > 4 * 1024 * 1024)
            { prefixIssue = Unknown("preprocessed shader size limit reached"); return; }
            code = ShaderPropertyUsage.StripComments(code);
            for (int lineStart = 0; lineStart < code.Length;)
            {
                int lineEnd = code.IndexOf('\n', lineStart);
                if (lineEnd < 0) lineEnd = code.Length;
                int cursor = lineStart;
                while (cursor < lineEnd && char.IsWhiteSpace(code[cursor])) cursor++;
                lineStart = lineEnd + 1;
                if (cursor < lineEnd && code[cursor] == '#')
                {
                    if (Metadata.IsMatch(code.Substring(cursor, lineEnd - cursor))) continue;
                    suffixIssue = Unknown("an unsupported directive remains after shader preprocessing"); break;
                }
                for (int i = cursor; i < lineEnd;)
                {
                    if (!PpLexer.IdentifierStart(code[i])) { i++; continue; }
                    int start = i++;
                    while (i < lineEnd && PpLexer.IdentifierPart(code[i])) i++;
                    if ((start == cursor || !RegexWord(code[start - 1])) && (i == lineEnd || !RegexWord(code[i])))
                    {
                        string token = code.Substring(start, i - start);
                        if (tokens.Add(token)) Cost += 48 + token.Length * 2;
                    }
                }
            }
        }

        internal string Inspect(bool success, ShaderInputQuery query)
        {
            if (!success) return Unknown("a shader variant has no verified preprocessed program");
            if (prefixIssue != null) return prefixIssue;
            return query.MatchesTokens(tokens) ? InputIssue : suffixIssue;
        }

        private static string Unknown(string reason) => "unverified shader inputs: " + reason;
        private static bool RegexWord(char c)
        {
            if (c < 128) return PpLexer.IdentifierPart(c);
            if (c == '\u200c' || c == '\u200d') return true;
            switch (char.GetUnicodeCategory(c))
            {
                case System.Globalization.UnicodeCategory.UppercaseLetter:
                case System.Globalization.UnicodeCategory.LowercaseLetter:
                case System.Globalization.UnicodeCategory.TitlecaseLetter:
                case System.Globalization.UnicodeCategory.ModifierLetter:
                case System.Globalization.UnicodeCategory.OtherLetter:
                case System.Globalization.UnicodeCategory.NonSpacingMark:
                case System.Globalization.UnicodeCategory.DecimalDigitNumber:
                case System.Globalization.UnicodeCategory.ConnectorPunctuation: return true;
                default: return false;
            }
        }
    }
}
