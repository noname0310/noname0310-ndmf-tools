using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor.Rendering;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static ShaderPreprocessorGraph Preprocessor(string text, bool skinning = false, Dictionary<string, string> files = null)
            => ShaderPreprocessorGraph.Analyze(new ShaderPreprocessorGraph.Source("root", text),
                (origin, path) => files != null && files.TryGetValue(path, out string source)
                    ? new[] { new ShaderPreprocessorGraph.Source(path, source) } : Array.Empty<ShaderPreprocessorGraph.Source>(), skinning);

        [Test]
        public void PreprocessorGraphIgnoresUnexpandedDefinitionsButRetainsIndirectUses()
        {
            const string declarations = "#if UNUSED\n#define UNUSED_INPUT SV_VertexID\n#endif\n" +
                "#if ENABLE\n#define SOURCE SV_VertexID\n#else\n#define SOURCE TEXCOORD0\n#endif\n#define ALIAS SOURCE\n";
            var graph = Preprocessor(declarations + "float4 position : POSITION;\n");
            Assert.That(graph.InputAbsent, Is.True); Assert.That(graph.CanIgnore("UNUSED"), Is.True);
            graph = Preprocessor(declarations + "uint value : ALIAS;\n");
            Assert.That(graph.InputAbsent, Is.False); Assert.That(graph.CanIgnore("ENABLE"), Is.False);
            Assert.That(graph.CanIgnore("UNUSED"), Is.True);
        }

        [Test]
        public void PreprocessorGraphSeparatesDefinednessFromReplacementValues()
        {
            var graph = Preprocessor("#if PRESENCE\n#define FLAG VALUE_ONLY\n#endif\n#if defined(FLAG)\nfloat4 p : POSITION;\n#endif\n");
            Assert.That(graph.CanIgnore("PRESENCE"), Is.False);
            Assert.That(graph.CanIgnore("VALUE_ONLY"), Is.True);
            Assert.That(graph.InputAbsent, Is.True);
        }

        [TestCase("0", true)] [TestCase("!1", true)] [TestCase("0x00u", true)]
        [TestCase("1", false)] [TestCase("4294967296", false)] [TestCase("(UNKNOWN + 1) / 2", false)]
        public void PreprocessorGraphKeepsUnknownArithmeticAndSkipsDefinitelyInactiveSuccessors(string condition, bool absent)
        {
            var graph = Preprocessor("#if " + condition + "\nuint value : SV_VertexID;\n#endif\nfloat4 p : POSITION;\n");
            Assert.That(graph.InputAbsent, Is.EqualTo(absent));
        }

        [Test]
        public void PreprocessorGraphTracksElifElseRedefinitionUndefinitionAndContinuedLines()
        {
            var graph = Preprocessor("#define SEM TEXCOORD0\n#if FIRST\n#undef SEM\n#define SEM SV_VertexID\n" +
                "#elif SECOND\n#undef SEM\n#define SEM SV_PrimitiveID\n#else\n#define OTHER 0\n#endif\n" +
                "#define WRAP(x) \\\n x\nuint value : WRAP(SEM);\n");
            Assert.That(graph.InputAbsent, Is.False);
            Assert.That(graph.CanIgnore("FIRST") || graph.CanIgnore("SECOND"), Is.False);
            Assert.That(Preprocessor("#if 0\n#include \"missing\"\n#elif 1\nfloat p;\n#else\nuint id : SV_VertexID;\n#endif\n").InputAbsent, Is.True);
        }

        [Test]
        public void PreprocessorGraphIncludesBothIncludeFormsAndAllReentryContexts()
        {
            var files = new Dictionary<string, string>
            {
                ["a"] = "#pragma once\n#include_with_pragmas \"b\"\nfloat p;\n",
                ["b"] = "#include \"a\"\n#define SEM SV_VertexID\n"
            };
            var graph = Preprocessor("#if A\n#include \"a\"\n#endif\n#if B\n#include \"a\"\n#endif\nuint id : SEM;\n", files: files);
            Assert.That(graph.Issue, Is.Null); Assert.That(graph.InputAbsent, Is.False);
            Assert.That(graph.CanIgnore("A") || graph.CanIgnore("B"), Is.False);
        }

        [TestCase("#include \"header\"\n", "float p;\n", true)]
        [TestCase("#include \"header\"\n", "#include_with_pragmas \"variants\"\n", false)]
        [TestCase("#include \"header\"\n", "#define FILE \"variants\"\n#include FILE\n", false)]
        [TestCase("#if 0\n#include \"missing\"\n#endif\n", "float p;\n", false)]
        public void PreprocessorGraphChecksTransitiveKeywordImports(string source, string header, bool verified)
        {
            var graph = Preprocessor(source, files: new Dictionary<string, string>
                { ["header"] = header, ["variants"] = "#pragma multi_compile _ A B\n" });
            Assert.That(graph.IncludesWithoutKeywordPragmas, Is.EqualTo(verified));
        }

        [TestCase("SV_ ## Vertex ## ID", false)]
        [TestCase("SV_ ## VertexID ## 17", false)]
        [TestCase("SV_ ## Part ## 0", true)]
        public void PreprocessorGraphRescansCompleteLiteralPasteChains(string replacement, bool absent)
        {
            var graph = Preprocessor("#define SEM " + replacement + "\nuint id : SEM;\n");
            Assert.That(graph.Issue, Is.Null); Assert.That(graph.InputAbsent, Is.EqualTo(absent));
        }

        [Test]
        public void PreprocessorGraphParameterizedPastesReachMacrosKeywordsAndIndexedSemantics()
        {
            var graph = Preprocessor("#if CONTROL\n#define PREFIX_VALUE SV_VertexID\n#endif\n" +
                "#define JOIN(x) PREFIX_ ## x\nuint id : JOIN(VALUE);\n");
            Assert.That(graph.InputAbsent, Is.False);
            Assert.That(graph.CanIgnore("CONTROL"), Is.False);
            Assert.That(graph.CanIgnore("PREFIX_KEYWORD"), Is.True, "Concrete macro arguments do not match unrelated pasted names.");
            Assert.That(graph.CanIgnore("UNRELATED"), Is.True);
            graph = Preprocessor("#define JOIN(x) SV_ ## x ## 123\nuint id : JOIN(VertexID);\n");
            Assert.That(graph.InputAbsent, Is.False, "The suffix language includes all numeric semantic indices.");
        }

        [Test]
        public void PreprocessorGraphStringificationCommentsAndStringsDoNotCreateInputs()
        {
            var graph = Preprocessor("#define STRING(x) #x\n// SV_VertexID\n/* SV_PrimitiveID */\n" +
                "float p;\nconst char* name = \"SV_VertexID\";\nSTRING(unrelated)\n");
            Assert.That(graph.InputAbsent, Is.True);
        }

        [TestCase("#include PATH\n")]
        [TestCase("#include \"missing\"\n")]
        [TestCase("#define F(...) __VA_ARGS__\nF(1)\n")]
        [TestCase("#if defined()\nfloat x;\n#endif\n")]
        [TestCase("#custom_command\n")]
        [TestCase("#define CAT(a,b) a##b\n#define F(a,b) CAT(a,b)\nF(SV_VertexID,__COUNTER__)\n")]
        [TestCase("#if FLAG\nfloat x;\n")]
        public void PreprocessorGraphUnknownConstructsNeverGrantAProof(string text)
        {
            var graph = Preprocessor(text);
            Assert.That(graph.InputAbsent, Is.False); Assert.That(graph.CanIgnore("ANY_KEYWORD"), Is.False);
            Assert.That(graph.Issue, Is.Not.Null);
        }

        [Test]
        public void PreprocessorGraphIncludesOrdinaryHlslKeywordUsesAndCapabilitySelectors()
        {
            var graph = Preprocessor("#pragma target 4.5 CAPABILITY\nfloat4 vert() { if (FEATURE) return 1; return 0; }\n");
            Assert.That(graph.CanIgnore("CAPABILITY") || graph.CanIgnore("FEATURE"), Is.False);
        }

        [Test]
        public void PreprocessorGraphCompilerCoverageIsBroaderThanInputDeclarationCoverage()
        {
            RequireD3DAnalysis();
            const string source = "struct V { float4 p : POSITION; uint id : SV_VertexID; };\n" +
                "float4 vert(V v) : SV_POSITION {\n#if READ_INDEX\nreturn v.p + v.id;\n#else\nreturn v.p;\n#endif\n}\n";
            var graph = Preprocessor(source);
            Assert.That(graph.CanIgnore("READ_INDEX"), Is.False);
            foreach (bool enabled in new[] { false, true })
            {
                Assert.That(D3DShaderInputs.Compile("#define READ_INDEX " + (enabled ? "1" : "0") + "\n" + source,
                    "vert", ShaderType.Vertex, out var inputs, out string error), Is.True, error);
                Assert.That(inputs.Any(i => i.Semantic == "SV_VertexID" && i.ReadMask != 0), Is.EqualTo(enabled));
            }
        }

        [Test]
        public void PreprocessorGraphProofsAgreeWithNativeCompilationAcrossGeneratedCases()
        {
            RequireD3DAnalysis(); var random = new Random(85437);
            for (int sample = 0; sample < 32; sample++)
            {
                bool indirect = random.Next(2) != 0, pasted = random.Next(2) != 0, live = random.Next(2) != 0;
                string semantic = pasted ? "SV_ ## Vertex ## ID" : "SV_VertexID";
                string source = "#if ENABLE\n#define SEM " + semantic + "\n#else\n#define SEM TEXCOORD0\n#endif\n" +
                    "#define ALIAS SEM\nstruct V { float4 p : POSITION; uint id : " + (indirect ? "ALIAS" : "SEM") + "; };\n" +
                    "float4 vert(V v) : SV_POSITION { return v.p" + (live ? " + v.id" : "") + "; }\n";
                var graph = Preprocessor(source);
                foreach (bool enabled in new[] { false, true })
                {
                    string preprocessed = PreprocessConcrete("#define ENABLE " + (enabled ? "1" : "0") + "\n" + source, dialect: PpDialect.UnityCaching);
                    Assert.That(D3DShaderInputs.Compile(preprocessed,
                        "vert", ShaderType.Vertex, out var inputs, out string error), Is.True, error + " case " + sample);
                    bool read = inputs.Any(i => i.Semantic == "SV_VertexID" && i.ReadMask != 0);
                    if (graph.InputAbsent) Assert.That(read, Is.False, "Graph absence must cover every native variant.");
                    if (read) Assert.That(graph.CanIgnore("ENABLE"), Is.False);
                }
            }
        }
    }
}
