using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("float", "")] [TestCase("half", "uniform ")]
        public void ShaderScalarListsSupportPartialReplacementAndSourceCertificates(string type, string storage)
        {
            string source = storage + type + " _A, _B, _C;\nfloat f(){return _A + _B + _C;}";
            var plan = ShaderConstantInputs.Prepare(source);
            var values = new Dictionary<string, float> { ["_A"] = .25f, ["_B"] = .5f, ["_C"] = .75f };
            var partial = new Dictionary<string, float> { ["_B"] = .5f };
            string candidate = plan.Apply(partial, out int count);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(candidate, Does.Contain(storage + type + " _A;").And.Contain(storage + type + " _C;"));
            Assert.That(candidate, Does.Contain("static const " + type + " _B = 0.5f;"));
            Assert.That(plan.MatchesSpecialization(candidate, values, out count), Is.True);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(plan.MatchesSpecialization(source, values, out count), Is.True); Assert.That(count, Is.Zero);
            Assert.That(plan.MatchesSpecialization(candidate.Replace("0.5f", "0.75f"), values, out _), Is.False);
            Assert.That(plan.MatchesSpecialization(candidate.Replace("_A + _B", "_A - _B"), values, out _), Is.False);
            var cached = ShaderConstantInputs.PrepareCached(source);
            Assert.That(ShaderConstantInputs.PrepareCached(source).Apply(partial, out _), Is.EqualTo(candidate));
            Assert.That(cached.UniformNames, Is.EquivalentTo(new[] { "_A", "_B", "_C" }));
        }

        [TestCase("float _A=0, _B;")] [TestCase("float _A[2], _B;")]
        [TestCase("static float _A, _B;")] [TestCase("float4 _A, _B;")]
        [TestCase("float _A : register(c0), _B;")]
        [TestCase("struct S {float _A, _B;};")]
        [TestCase("float f(){float _A, _B;return 0;}")]
        [TestCase("float _A,\n#if FEATURE\n_B\n#else\n_C\n#endif\n;")]
        public void ShaderScalarListsDoNotRewriteUnsupportedStorageOrGrammar(string source)
        {
            var plan = ShaderConstantInputs.Prepare(source);
            Assert.That(plan.Source, Is.EqualTo(ShaderConstantInputs.Normalize(source)));
            Assert.That(plan.Declarations, Is.Empty);
        }

        [Test]
        public void ShaderScalarListWritesDoNotBlockUnrelatedMembers()
        {
            var plan = ShaderConstantInputs.Prepare("float _A,_B; float f(){_A=2; return _A+_B;}");
            string candidate = plan.Apply(new Dictionary<string, float> { ["_A"] = 1, ["_B"] = .5f }, out int count);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(candidate, Does.Contain("float _A;").And.Contain("static const float _B = 0.5f;"));
        }

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
