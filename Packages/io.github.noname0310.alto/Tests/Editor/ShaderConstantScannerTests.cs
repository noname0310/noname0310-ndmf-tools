using System;
using System.Text;
using System.Text.RegularExpressions;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void ShaderDirectiveScannerMatchesMultilineRegexIncludingCrossLineWhitespace()
        {
            var location = new Regex(@"^\s*#\s*(?:line\b|[0-9])[^\r\n]*", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            var directive = new Regex(@"^\s*#.*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            var random = new Random(71811);
            string[] pieces = { " ", "\t", "\n", "\r", "\r\n", "#", "#line", "# line", "#12", "pragma", "line", "_", "float x;",
                "/* comment */", "// line", "\"file.hlsl\"", "\u0085", "\u00a0", "\u2028", "\u2007", "\u03b1", "\u200c", "\u200d", "\0" };
            for (int test = 0; test < 5000; test++)
            {
                var source = new StringBuilder();
                for (int i = 0; i < 60; i++) source.Append(pieces[random.Next(pieces.Length)]);
                string text = source.ToString();
                Assert.That(ShaderConstantInputs.ScanDirectives(text, true), Is.EqualTo(location.Replace(text, "")), "Location case " + test);
                Assert.That(ShaderConstantInputs.ScanDirectives(text, false), Is.EqualTo(directive.Replace(text, m => new string(' ', m.Length))), "Directive case " + test);
            }
        }

        [Test]
        public void ShaderConstantScannerMatchesPreviousTokenGrammar()
        {
            var reference = new Regex(@"""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[_A-Za-z][_A-Za-z0-9]*|[^\s]", RegexOptions.CultureInvariant);
            var random = new Random(6721);
            string alphabet = "abc_X019 ;{}()[]+/*%=<>\\\"'\r\n\t\0\u0085\u00a0\u2028\u2007\u03b1\ud800\udfff";
            for (int test = 0; test < 1500; test++)
            {
                var source = new StringBuilder();
                for (int i = 0; i < 100; i++) source.Append(alphabet[random.Next(alphabet.Length)]);
                string text = source.ToString();
                var expected = reference.Matches(text);
                var actual = ShaderConstantInputs.Tokenize(text);
                Assert.That(actual.Count, Is.EqualTo(expected.Count), "Token count in case " + test);
                for (int i = 0; i < actual.Count; i++)
                {
                    Assert.That(actual[i].Text, Is.EqualTo(expected[i].Value), "Token spelling in case " + test);
                    Assert.That(actual[i].Index, Is.EqualTo(expected[i].Index));
                    Assert.That(actual[i].Length, Is.EqualTo(expected[i].Length));
                }
            }
        }
    }
}
