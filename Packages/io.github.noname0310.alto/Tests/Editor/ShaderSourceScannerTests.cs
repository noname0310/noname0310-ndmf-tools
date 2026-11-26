using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void ShaderLineLexerMatchesCopiedLinesIncludingRangeBoundaries()
        {
            var random = new Random(5381);
            const string alphabet = "abc_X09 .e+-/*#=<>\\\"'\r\t\u00a0\ufeff";
            var output = new List<PpToken>();
            for (int test = 0; test < 1500; test++)
            {
                var text = new StringBuilder();
                for (int i = 0; i < random.Next(1, 90); i++) text.Append(alphabet[random.Next(alphabet.Length)]);
                string source = "identifier123" + text + "identifier456";
                int start = 13, length = text.Length;
                PpToken[] expected = null; string error = null;
                try { expected = PpLexer.Lex(source.Substring(start, length), "file.hlsl", 23).Where(t => t.Kind != PpTokenKind.Newline).ToArray(); }
                catch (PpException exception) { error = exception.Message; }
                output.Clear(); string actualError = null;
                try { PpLexer.LexLine(source, start, length, "file.hlsl", 23, output); }
                catch (PpException exception) { actualError = exception.Message; }
                Assert.That(actualError, Is.EqualTo(error), "Error case " + test);
                if (error != null) continue;
                Assert.That(output.Select(t => (t.Text, t.Kind, t.File, t.Line, t.Space, t.Offset, t.Length)),
                    Is.EqualTo(expected.Select(t => (t.Text, t.Kind, t.File, t.Line, t.Space, t.Offset, t.Length))), "Token case " + test);
            }
        }

        private static string ReferenceShaderComments(string source)
        {
            source = source.Replace("\\\r\n", "").Replace("\\\n", "");
            return Regex.Replace(source, @"""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|//[^\r\n]*|/\*[\s\S]*?\*/", m =>
                m.Value.StartsWith("//", StringComparison.Ordinal) || m.Value.StartsWith("/*", StringComparison.Ordinal)
                    ? new string(m.Value.Select(c => c == '\r' || c == '\n' ? c : ' ').ToArray()) : m.Value,
                RegexOptions.CultureInvariant);
        }

        private static bool ReferenceShaderToken(string text, ShaderInputQuery query)
        {
            foreach (var semantic in query.Semantics)
            {
                if (!text.StartsWith(semantic.Name, StringComparison.OrdinalIgnoreCase)) continue;
                string suffix = text.Substring(semantic.Name.Length);
                if (suffix.Length == 0) { if (!semantic.Index.HasValue || semantic.Index.Value == 0) return true; }
                else if (suffix.All(c => c >= '0' && c <= '9') && (!semantic.Index.HasValue ||
                    uint.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out uint index) && index == semantic.Index.Value)) return true;
            }
            return false;
        }

        private static string ReferenceShaderInspection(bool success, string code, ShaderInputQuery query)
        {
            string Unknown(string reason) => "unverified shader inputs: " + reason;
            if (!success || string.IsNullOrWhiteSpace(code)) return Unknown("a shader variant has no verified preprocessed program");
            if (code.Any(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'))
                return Unknown("shader preprocessing returned malformed source text");
            if (code.Length > 4 * 1024 * 1024) return Unknown("preprocessed shader size limit reached");
            code = ReferenceShaderComments(code);
            foreach (string line in code.Split('\n'))
            {
                string text = line.TrimStart();
                if (text.StartsWith("#", StringComparison.Ordinal))
                {
                    if (Regex.IsMatch(text, @"^#\s*(line\b|pragma\b|[0-9])", RegexOptions.CultureInvariant)) continue;
                    return Unknown("an unsupported directive remains after shader preprocessing");
                }
                foreach (Match token in Regex.Matches(text, @"\b[_A-Za-z][_A-Za-z0-9]*\b", RegexOptions.CultureInvariant))
                    if (ReferenceShaderToken(token.Value, query)) return "shader may depend on the queried inputs";
            }
            return null;
        }

        [Test]
        public void ShaderSourceScannersMatchPreviousInspectionGrammar()
        {
            var random = new Random(5376);
            string alphabet = "ab_X09 ;{}()[]+/*#=<>\\\"'\r\n\t\u00a0\u0300\u200c\u200d\u2028\u03b1\ud800\udfff";
            // Compare tokenization independently of indexed declaration coverage:
            // source arrays can span several indices from one semantic declaration.
            var query = new ShaderInputQuery(new ShaderInputQuery.Semantic("CUSTOM"));
            for (int test = 0; test < 1500; test++)
            {
                var text = new StringBuilder();
                for (int i = 0; i < 130; i++)
                    if (random.Next(20) == 0) text.Append(random.Next(2) == 0 ? "CUSTOM3" : "SV_VERTEXID");
                    else text.Append(alphabet[random.Next(alphabet.Length)]);
                string source = text.ToString();
                Assert.That(ShaderPropertyUsage.StripComments(source), Is.EqualTo(ReferenceShaderComments(source)), "Comment case " + test);
                Assert.That(ShaderMeshInputs.InspectPreprocessed(true, source, query), Is.EqualTo(ReferenceShaderInspection(true, source, query)), "Inspection case " + test);
            }
            foreach (string source in new[] { "#line 2 \"CUSTOM3\"\nfloat value;", "#pragma x CUSTOM3\nfloat value;",
                "#pragmax CUSTOM3", "# 3 \"CUSTOM3\"\nCUSTOM3", "/* CUSTOM3 */ float v;", "// CUSTOM3\rfloat v;",
                "\u200cCUSTOM3", "CUSTOM3\u200d", "\"unclosed /* CUSTOM3 */", "CUSTOM3\0" })
                Assert.That(ShaderMeshInputs.InspectPreprocessed(true, source, query), Is.EqualTo(ReferenceShaderInspection(true, source, query)), source);
        }

        [Test]
        public void ShaderInspectionPreservesEveryUnicodeBoundaryCharacter()
        {
            var query = new ShaderInputQuery(new ShaderInputQuery.Semantic("CUSTOM"));
            for (int value = char.MinValue; value <= char.MaxValue; value++)
            {
                string before = ";" + (char)value + "CUSTOM3;";
                string after = ";CUSTOM3" + (char)value + ";";
                Assert.That(ShaderMeshInputs.InspectPreprocessed(true, before, query),
                    Is.EqualTo(ReferenceShaderInspection(true, before, query)), "Before U+" + value.ToString("X4"));
                Assert.That(ShaderMeshInputs.InspectPreprocessed(true, after, query),
                    Is.EqualTo(ReferenceShaderInspection(true, after, query)), "After U+" + value.ToString("X4"));
            }
        }

        [Test]
        public void ShaderSemanticRangesAndNumericTypesMatchPreviousGrammar()
        {
            var numeric = new Regex(@"^(bool|int|uint|half|float|double)([1-4](x[1-4])?)?$", RegexOptions.CultureInvariant);
            foreach (string name in new[] { "bool", "int", "uint", "half", "float", "double", "", "floatx", "unsigned", "FLOAT" })
                foreach (string suffix in new[] { "", "1", "4", "5", "0", "11", "1x1", "4x4", "4x5", "\n", "1\n", "1\r\n", "1\n\n" })
                    Assert.That(ShaderInputProgram.IsNumericType(name + suffix), Is.EqualTo(numeric.IsMatch(name + suffix)), name + suffix);
            foreach (var query in new[] { new ShaderInputQuery(new ShaderInputQuery.Semantic("CUSTOM")),
                new ShaderInputQuery(new ShaderInputQuery.Semantic("CUSTOM", 0)), new ShaderInputQuery(new ShaderInputQuery.Semantic("CUSTOM", uint.MaxValue)) })
                foreach (string suffix in new[] { "", "0", "00", "3", "4294967295", "4294967296", "00004294967295", "0000000000000000000", "99999999999999999", "1x", "+0", "\u0660" })
                {
                    string name = "cUsToM" + suffix;
                    Assert.That(query.MatchesToken(name), Is.EqualTo(ReferenceShaderToken(name, query)), name);
                    Assert.That(query.MatchesToken("prefix" + name + "suffix", 6, name.Length), Is.EqualTo(ReferenceShaderToken(name, query)), name);
                }
        }
    }
}
