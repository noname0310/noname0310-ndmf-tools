using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void PreprocessorLexerDirectSourceMatchesTranslatedPhysicalLines()
        {
            const string source = "#define F(a) a + 1\n/* first\n second */ float4 v = F(2); // tail\nconst char *s = \"a\\\"b\"; x >>= 1;\n";
            string translated = source.Replace("\n", "\r\n");
            var direct = PpLexer.Lex(source, "source.hlsl", 9, true);
            var normalized = PpLexer.Lex(translated, "source.hlsl", 9, true);
            Assert.That(normalized.Count, Is.EqualTo(direct.Count));
            for (int i = 0; i < direct.Count; i++)
            {
                var a = direct[i]; var b = normalized[i];
                Assert.That((b.Text, b.Kind, b.File, b.Line, b.Space), Is.EqualTo((a.Text, a.Kind, a.File, a.Line, a.Space)));
                if (a.Kind == PpTokenKind.Newline) continue;
                Assert.That(source.Substring(a.Offset, a.Length), Is.EqualTo(a.Text));
                Assert.That(translated.Substring(b.Offset, b.Length), Is.EqualTo(b.Text));
            }
        }

        private static string PreprocessConcrete(string text, Dictionary<string, string> files = null, PpDialect dialect = PpDialect.Standard)
        {
            var program = new PpProgram((origin, path) => files != null && files.TryGetValue(path, out var value)
                ? new[] { new ShaderPreprocessorGraph.Source(path, value) } : Array.Empty<ShaderPreprocessorGraph.Source>());
            var source = program.Parse(new ShaderPreprocessorGraph.Source("root", text));
            var interpreter = new PpInterpreter(program, dialect); interpreter.Run(source, new PpState());
            return PpLexer.Spell(interpreter.Output.SelectMany(o => o));
        }

        private static readonly string[] PreprocessorMacroCases =
        {
            "#define X 1\nALTO_BEGIN X\n#undef X\n#define X 2\nX ALTO_END\n",
            "#define A B\n#define B 7\nALTO_BEGIN A\n#undef B\n#define B 9\nA ALTO_END\n",
            "#define CAT(a,b) a ## b\n#define EXPAND(a,b) CAT(a,b)\n#define PREFIX SV_\nALTO_BEGIN EXPAND(PREFIX,VertexID) ALTO_END\n",
            "#define SEM SV_ ## Vertex ## ID\nALTO_BEGIN SEM ALTO_END\n",
            "#define STR(x) #x\n#define EXPAND(x) STR(x)\n#define X 42\nALTO_BEGIN STR(X) EXPAND(X) ALTO_END\n",
            "#define STR(x) #x\nALTO_BEGIN STR(a  +\n b /* comment */ + c) ALTO_END\n",
            "#define F(x) (x+x)\n#define ALIAS F\nALTO_BEGIN ALIAS(2) ALTO_END\n",
            "#define F(x) x\nALTO_BEGIN F(F(3)) ALTO_END\n",
            "#define A B\n#define B A\nALTO_BEGIN A B ALTO_END\n",
            "#define F(x) x F\nALTO_BEGIN F(1)(2) ALTO_END\n",
            "#define PAIR(a,b) a b\n#define COMMA a,b\n#define FORWARD(x) PAIR(x)\nALTO_BEGIN FORWARD(COMMA) ALTO_END\n",
            "#define CONTINUED ab\\\ncd\nALTO_BEGIN CONTINUED ALTO_END\n",
            "#define X 9 // continued\\\n#define Y 7\nALTO_BEGIN X Y ALTO_END\n",
            "#define OBJ (1)\n#define FN(x) x\nALTO_BEGIN OBJ FN(2) ALTO_END\n",
            "#define FLAG\n#if defined FLAG && defined(FLAG)\nALTO_BEGIN yes ALTO_END\n#else\nALTO_BEGIN no ALTO_END\n#endif\n",
            "#if 2 + 3 * 4 == 14 && (16 >> 2) == 4\nALTO_BEGIN yes ALTO_END\n#else\nALTO_BEGIN no ALTO_END\n#endif\n",
            "#if 0\n#error dead\n#elif 1\nALTO_BEGIN live ALTO_END\n#else\n#error dead\n#endif\n",
            "#define S(x) #x\n#define E(x) S(x)\n#define F(x) a x b\nALTO_BEGIN E(F(c+d)) ALTO_END\n",
            "#define F(a,b) a ## b\n#define X token\nALTO_BEGIN F(X,suffix) ALTO_END\n",
            "#define F(x) x\n#define X F\nALTO_BEGIN X\n(5) ALTO_END\n"
        };

        [TestCaseSource(nameof(PreprocessorMacroCases))]
        public void ConventionalPreprocessorMatchesUnityTokenExpansion(string body)
        {
            string actual = BetweenMarkers(PreprocessConcrete(body, dialect: PpDialect.UnityCaching));
            Assert.That(actual, Is.EqualTo(UnityPreprocessorTokens(body)), body);
        }

        private static string BetweenMarkers(string text)
        {
            var tokens = PpLexer.Lex(text).Where(t => t.Kind != PpTokenKind.Newline).ToArray();
            int start = Array.FindIndex(tokens, t => t.Text == "ALTO_BEGIN");
            int end = Array.FindIndex(tokens, t => t.Text == "ALTO_END");
            Assert.That(start, Is.GreaterThanOrEqualTo(0), text); Assert.That(end, Is.GreaterThan(start), text);
            // Compiler #line records are metadata inserted between emitted lines.
            var output = new List<PpToken>();
            for (int i = start + 1; i < end; i++)
            {
                if (tokens[i].Text == "#")
                { int line = tokens[i].Line; while (i + 1 < end && tokens[i + 1].Line == line) i++; continue; }
                output.Add(tokens[i]);
            }
            return PpLexer.Spell(output);
        }

        private static string UnityPreprocessorTokens(string body, ShaderCompilerPlatform backend = ShaderCompilerPlatform.D3D,
            BuildTarget target = BuildTarget.StandaloneWindows64, ShaderType stage = ShaderType.Vertex, GraphicsTier tier = GraphicsTier.Tier2)
        {
            string text = "Shader \"Hidden/Noname/ALTO/Oracle" + Guid.NewGuid().ToString("N") + "\" { SubShader { Pass { HLSLPROGRAM\n" +
                "#pragma target 4.5\n#pragma vertex vert\n#pragma fragment frag\n" + body +
                "\nfloat4 vert(float4 p:POSITION):SV_POSITION {return p;}\nfloat4 frag():SV_Target {return 1;}\nENDHLSL } } }";
            var shader = ShaderUtil.CreateShaderAsset(text, false);
            try
            {
                var pass = ShaderUtil.GetShaderData(shader).GetSubshader(0).GetPass(0);
                var result = pass.PreprocessVariant(stage, Array.Empty<string>(), backend, target, tier, false);
                Assert.That(result.Success, Is.True, string.Join("\n", ShaderUtil.GetShaderMessages(shader).Select(m => m.message)));
                if ((backend == ShaderCompilerPlatform.Vulkan || backend == ShaderCompilerPlatform.GLES3x || backend == ShaderCompilerPlatform.OpenGLCore) &&
                    (string.IsNullOrWhiteSpace(result.PreprocessedCode) || result.PreprocessedCode.Contains('\0') ||
                        !result.PreprocessedCode.Contains("ALTO_BEGIN") || result.PreprocessedCode.IndexOf("ALTO_END", StringComparison.Ordinal) < result.PreprocessedCode.IndexOf("ALTO_BEGIN", StringComparison.Ordinal)))
                    Assert.Ignore("Unity did not expose one inspectable preprocessed stage for " + backend + "/" + stage + ". Native absence proofs are disabled for this backend.");
                return BetweenMarkers(result.PreprocessedCode);
            }
            finally { UnityEngine.Object.DestroyImmediate(shader); }
        }

        [Test]
        public void ConventionalPreprocessorHandlesIncludeGuardsOnceAndMacroIncludes()
        {
            var files = new Dictionary<string, string>
            {
                ["guard"] = "#ifndef GUARD\n#define GUARD\nguarded\n#endif\n",
                ["once"] = "#pragma once\nonce\n#include \"once\"\n",
                ["reentrant"] = "VALUE\n"
            };
            string source = "#define PATH \"guard\"\n#include PATH\n#include PATH\n#include_with_pragmas \"once\"\n#include \"once\"\n" +
                "#define VALUE first\n#include \"reentrant\"\n#undef VALUE\n#define VALUE second\n#include \"reentrant\"\n";
            Assert.That(PreprocessConcrete(source, files), Is.EqualTo("guarded once first second"));
        }

        [Test]
        public void ConventionalPreprocessorHonorsLineLocationsAndExpansionSites()
        {
            const string source = "#define LINE __LINE__\n#line 50 \"other\"\nLINE __LINE__ __FILE__\n";
            Assert.That(PreprocessConcrete(source), Is.EqualTo("50 50 \"other\""));
        }

        [Test]
        public void SequentialIncludesPreserveCountersDefinitionsAndCallerLocations()
        {
            var files = new Dictionary<string, string>
            {
                ["outer"] = "#pragma once\n#define VALUE 7\n#include \"inner\"\n#line 90 \"outer-remapped\"\n__LINE__ __FILE__\n",
                ["inner"] = "__COUNTER__\n#undef VALUE\n#define VALUE 9\n#include \"outer\"\n"
            };
            const string source = "#line 40 \"caller\"\n#include \"outer\"\n#include \"outer\"\nVALUE __COUNTER__ __LINE__ __FILE__\n";
            Assert.That(PreprocessConcrete(source, files), Is.EqualTo("0 90 \"outer-remapped\" 9 1 42 \"caller\""));
        }

        [Test]
        public void UndefinedOrdinaryNamesCanBecomeInputsAfterConditionalTokenPasting()
        {
            const string source = "PREFIX_FEATURE;\n#define JOIN(a,b) a##b\n" +
                "#if JOIN(PREFIX_,FEATURE)\nuint id : SV_VertexID;\n#endif\n";
            var graph = Preprocessor(source);
            Assert.That(graph.Issue, Is.Null);
            Assert.That(graph.InputAbsent, Is.False);
            Assert.That(graph.CanIgnore("PREFIX_FEATURE"), Is.False);
        }

        [Test]
        public void ConventionalPreprocessorSupportsVariadicSubstitutionWithoutCompilerExtensions()
        {
            Assert.That(PreprocessConcrete("#define F(x,...) x __VA_ARGS__\nF(a,b,c)\nF(a)\n"), Is.EqualTo("a b , c a"));
        }

        [TestCase("#define CAT(a,b) a##b\nCAT(,token) CAT(token,) CAT(,)\n", "token token")]
        [TestCase("#define CAT(a,b) a##b\n#define X token\nCAT(X,suffix)\n", "Xsuffix")]
        [TestCase("#define F(x) x\n#define X F\nX\n(5)\n", "5")]
        [TestCase("#define STR(x) #x\nSTR(\"foo\")\n", "\"\\\"foo\\\"\"")]
        public void ConventionalPreprocessorPreservesStandardSemanticsWhereUnityDiffers(string source, string expected)
            => Assert.That(PreprocessConcrete(source), Is.EqualTo(expected));

        [TestCase("#define CAT(a,b) a##b\nCAT(,x)\n")]
        [TestCase("#define STR(x) #x\nSTR(\"foo\")\n")]
        public void ConventionalPreprocessorRejectsUnmodeledUnityTokenSerialization(string source)
        {
            Assert.Throws<PpException>(() => PreprocessConcrete(source, dialect: PpDialect.UnityCaching));
            Assert.That(Preprocessor(source).InputAbsent, Is.False);
        }

        [TestCase("#define F(x) x\nF(1,2)\n")]
        [TestCase("#define F(x) x\nF(1\n")]
        [TestCase("#define F 1\n#define F 2\n")]
        [TestCase("#define F(a,) a\n")]
        [TestCase("#define F ## x\n")]
        [TestCase("#if 1\nx\n")]
        [TestCase("#else\n")]
        [TestCase("#if defined()\nx\n#endif\n")]
        public void ConventionalPreprocessorRejectsMalformedPrograms(string source)
            => Assert.Throws<PpException>(() => PreprocessConcrete(source));

        [TestCase("-1 < 1U")]
        [TestCase("(1 ? -1 : 1U) < 1")]
        [TestCase("(-1 & 0xffffffffffffffffU) < 1")]
        [TestCase("9223372036854775807 + 1 < 0")]
        [TestCase("1 / 0")]
        [TestCase("1 << 64")]
        [TestCase("0xffffffff + 1 == 0")]
        [TestCase("2147483647 + 1 > 0")]
        [TestCase("'ab' == 1")]
        public void ConventionalPreprocessorDoesNotInventNonportableIntegerResults(string expression)
            => Assert.That(PpExpression.Evaluate(PpLexer.Lex(expression)).Bits, Is.Null);

        [Test]
        public void ConventionalPreprocessorRescansMacroGeneratedDefinedOperators()
        {
            const string body = "#define FEATURE 1\n#define ACTIVE (defined(FEATURE) && !defined(OTHER))\n" +
                "#if ACTIVE\nALTO_BEGIN yes ALTO_END\n#else\nALTO_BEGIN no ALTO_END\n#endif\n";
            Assert.That(BetweenMarkers(PreprocessConcrete(body)), Is.EqualTo(UnityPreprocessorTokens(body)));
            var graph = Preprocessor("#define ACTIVE (defined(FEATURE) && !defined(OTHER))\n#if ACTIVE\nuint id : SV_VertexID;\n#endif\n");
            Assert.That(graph.InputAbsent, Is.False);
            Assert.That(graph.CanIgnore("FEATURE"), Is.False);
        }

        [TestCase("2 + 3 * 4", 14UL)]
        [TestCase("(2 + 3) * 4", 20UL)]
        [TestCase("0xff & 017", 15UL)]
        [TestCase("!0 + ~0", 0UL)]
        [TestCase("'\\n'", 10UL)]
        [TestCase("1 != 2 && 3 >= 2", 1UL)]
        public void ConventionalPreprocessorEvaluatesPortableIntegerExpressions(string expression, ulong expected)
            => Assert.That(PpExpression.Evaluate(PpLexer.Lex(expression)).Bits, Is.EqualTo(expected));

        [Test]
        public void ConventionalPreprocessorDependencyJoinDropsKilledDefinitions()
        {
            var graph = Preprocessor("#if FEATURE\n#define VALUE SV_VertexID\n#else\n#define VALUE TEXCOORD0\n#endif\n" +
                "#undef VALUE\n#define VALUE TEXCOORD1\nuint id : VALUE;\n");
            Assert.That(graph.Issue, Is.Null); Assert.That(graph.InputAbsent, Is.True); Assert.That(graph.CanIgnore("FEATURE"), Is.True);
        }

        [TestCase(ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64, ShaderType.Vertex, GraphicsTier.Tier1)]
        [TestCase(ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64, ShaderType.Vertex, GraphicsTier.Tier2)]
        [TestCase(ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64, ShaderType.Vertex, GraphicsTier.Tier3)]
        [TestCase(ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64, ShaderType.Fragment, GraphicsTier.Tier2)]
        [TestCase(ShaderCompilerPlatform.GLES3x, BuildTarget.Android, ShaderType.Vertex, GraphicsTier.Tier2)]
        [TestCase(ShaderCompilerPlatform.GLES3x, BuildTarget.Android, ShaderType.Fragment, GraphicsTier.Tier2)]
        [TestCase(ShaderCompilerPlatform.Vulkan, BuildTarget.Android, ShaderType.Vertex, GraphicsTier.Tier2)]
        [TestCase(ShaderCompilerPlatform.Vulkan, BuildTarget.Android, ShaderType.Fragment, GraphicsTier.Tier2)]
        [TestCase(ShaderCompilerPlatform.Metal, BuildTarget.iOS, ShaderType.Vertex, GraphicsTier.Tier2)]
        [TestCase(ShaderCompilerPlatform.Metal, BuildTarget.iOS, ShaderType.Fragment, GraphicsTier.Tier2)]
        public void ConventionalPreprocessorCompilerContextAgreesWithUnityPreprocessing(ShaderCompilerPlatform backend, BuildTarget target, ShaderType stage, GraphicsTier tier)
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target)) Assert.Ignore("Target module is not installed.");
            var definitions = ShaderPreprocessorContext.Definitions(target, backend, stage, tier);
            string body = "ALTO_BEGIN\n";
            foreach (var definition in definitions)
                body += (definition.Value == null ? "#ifdef " + definition.Key : "#if !defined(" + definition.Key + ") || " + definition.Key + " != 1") +
                    "\nMISMATCH_" + definition.Key + "\n#endif\n";
            body += "ALTO_END\n";
            Assert.That(UnityPreprocessorTokens(body, backend, target, stage, tier), Is.Empty);
        }

        [Test]
        public void ConventionalPreprocessorCompilerMacrosAreInputsEvenOutsideConditionals()
        {
            const string code = "#define CAT(a,b) a##b\n#define EXPAND(a,b) CAT(a,b)\nuint id : EXPAND(SV_VertexID,UNITY_VERSION);\n";
            var graph = ShaderPreprocessorGraph.Analyze(new ShaderPreprocessorGraph.Source("root", code), null, false, Array.Empty<string>());
            Assert.That(graph.InputAbsent, Is.False);
            Assert.That(graph.Issue, Does.Contain("symbolic"));
        }

        [Test]
        public void ConventionalPreprocessorDoesNotDiscardExplicitBuiltinOverrides()
        {
            var graph = ShaderPreprocessorGraph.Analyze(new ShaderPreprocessorGraph.Source("root",
                "#undef __LINE__\n#define __LINE__ SV_VertexID\nuint id : __LINE__;\n"), null, false, Array.Empty<string>());
            Assert.That(graph.Issue, Is.Null); Assert.That(graph.InputAbsent, Is.False);
        }

        [Test]
        public void ConventionalPreprocessorRetainsNonlocalCounterDependencies()
        {
            const string code = "#if K\n#if __COUNTER__ == 0\n#endif\n#endif\n#if __COUNTER__ != 0\nuint id : SV_VertexID;\n#endif\n";
            var graph = ShaderPreprocessorGraph.Analyze(new ShaderPreprocessorGraph.Source("root", code), null, false, new[] { "K" });
            Assert.That(graph.InputAbsent, Is.False); Assert.That(graph.CanIgnore("K"), Is.False);
        }

        [TestCase("_Pragma(\"pop_macro(\\\"INPUT\\\")\")\nfloat x;\n")]
        [TestCase("__pragma(pop_macro(\"INPUT\"))\nfloat x;\n")]
        public void ConventionalPreprocessorDoesNotIgnoreMacroStateOperators(string source)
        {
            var graph = Preprocessor(source);
            Assert.That(graph.InputAbsent, Is.False); Assert.That(graph.Issue, Does.Contain("Pragma"));
        }

        [TestCase("\0float4 p;")]
        [TestCase("float4 p;\u0001")]
        public void ConventionalPreprocessorNeverAcceptsMalformedNativeSource(string source)
            => Assert.That(ShaderMeshInputs.InspectPreprocessed(true, source, false), Does.Contain("malformed"));

        [Test]
        public void ConventionalPreprocessorDependencyJoinKeepsOnlyLiveMacroChoices()
        {
            var graph = Preprocessor("#if FEATURE\n#define LIVE SV_VertexID\n#define DEAD 1\n#else\n#define LIVE TEXCOORD0\n#define DEAD 2\n#endif\nuint id : LIVE;\n");
            Assert.That(graph.Issue, Is.Null); Assert.That(graph.InputAbsent, Is.False); Assert.That(graph.CanIgnore("FEATURE"), Is.False);
        }

        [Test]
        public void ConventionalPreprocessorDependencyProofsCoverGeneratedConcretePrograms()
        {
            var random = new System.Random(81357); var names = new[] { "A", "B", "C" };
            for (int sample = 0; sample < 80; sample++)
            {
                string code = "#define CAT(a,b) a##b\n#define CALL(x) x\n";
                for (int i = 0; i < names.Length; i++)
                {
                    string yes = random.Next(3) == 0 ? "SV_VertexID" : "TEXCOORD" + i;
                    string no = random.Next(3) == 0 ? "SV_PrimitiveID" : "TEXCOORD" + i;
                    code += "#if " + names[i] + "\n#define V" + i + " " + yes + "\n#else\n#define V" + i + " " + no + "\n#endif\n";
                    if (random.Next(3) == 0) code += "#undef V" + i + "\n#define V" + i + " TEXCOORD9\n";
                    if (random.Next(2) == 0) code += "uint v" + i + " : CALL(V" + i + ");\n";
                }
                var graph = ShaderPreprocessorGraph.Analyze(new ShaderPreprocessorGraph.Source("root", code), null, true, names);
                Assert.That(graph.Issue, Is.Null, code);
                foreach (var dialect in new[] { PpDialect.Standard, PpDialect.UnityCaching })
                {
                    var variants = new string[8];
                    for (int mask = 0; mask < variants.Length; mask++)
                    {
                        string prefix = string.Concat(names.Select((n, bit) => (mask & (1 << bit)) != 0 ? "#define " + n + " 1\n" : ""));
                        variants[mask] = PreprocessConcrete(prefix + code, dialect: dialect);
                        if (graph.InputAbsent) Assert.That(PpLexer.Lex(variants[mask]).Any(t => ShaderMeshInputs.IsInput(t.Text, true)), Is.False, code);
                    }
                    for (int bit = 0; bit < names.Length; bit++)
                        if (graph.CanIgnore(names[bit]))
                            for (int mask = 0; mask < variants.Length; mask++) Assert.That(variants[mask], Is.EqualTo(variants[mask ^ (1 << bit)]), code);
                }
            }
        }
    }
}
