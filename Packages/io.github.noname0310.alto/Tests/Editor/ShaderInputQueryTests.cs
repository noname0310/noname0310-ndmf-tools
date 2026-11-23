using System;
using System.Linq;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void ShaderProjectionSingletonProofMatchesGeneralExpansion()
        {
            var state = new PpState();
            state.Macros["ALIAS"] = new PpBinding(new[] { new PpMacro("ALIAS", PpLexer.Lex("CUSTOM3").ToArray()) });
            state.Macros["UNRELATED"] = new PpBinding(new[] { new PpMacro("UNRELATED", PpLexer.Lex("ordinary").ToArray()) });
            state.Macros["FUNCTION"] = new PpBinding(new[] { new PpMacro("FUNCTION", PpLexer.Lex("CUSTOM3").ToArray(), new[] { "x" }) });
            foreach (var query in new[] { ShaderInputQuery.Identifier("CUSTOM3"), new ShaderInputQuery(new ShaderInputQuery.Semantic("CUSTOM", 3)) })
                foreach (string text in new[] { "CUSTOM3", "ordinary", "ALIAS", "UNRELATED", "FUNCTION", "__COUNTER__", "__LINE__", "__FILE__", "_Pragma", ";", "12" })
                    foreach (string next in new[] { ".", ";", "(" })
                    {
                        var token = PpLexer.Lex(text, "probe.hlsl", 12).Single(); var following = PpLexer.Lex(next).Single();
                        var demand = new ShaderInputDemand(query);
                        var fast = new PpProjection(state, demand); var reference = new PpProjection(state, demand);
                        Assert.That(fast.MayContribute(token, following), Is.EqualTo(reference.MayContribute(new[] { token }, following)), text + next);
                    }
        }

        [Test]
        public void ShaderInputQueriesSupportArbitrarySemanticsAndIndependentIndices()
        {
            var family = new ShaderInputQuery(new ShaderInputQuery.Semantic("customInput"));
            var indexed = new ShaderInputQuery(new ShaderInputQuery.Semantic("customInput", 3));
            Assert.That(family.MatchesToken("CUSTOMINPUT99"), Is.True);
            Assert.That(indexed.MatchesToken("custominput3"), Is.True);
            Assert.That(indexed.MatchesToken("custominput30"), Is.False);
            Assert.That(indexed.MatchesToken("custominput"), Is.False);
            Assert.That(indexed.Matches("CustomInput", 3), Is.True);
            Assert.That(indexed.Matches("CustomInput", 0), Is.False);
            Assert.That(indexed.MatchesToken("customInputExtra"), Is.False);
            Assert.That(family.Signature, Is.Not.EqualTo(indexed.Signature));
            Assert.That(new ShaderInputQuery(new ShaderInputQuery.Semantic("customInput3")).Signature, Is.EqualTo(indexed.Signature));
            Assert.Throws<ArgumentException>(() => new ShaderInputQuery.Semantic("customInput3", 2));
        }

        [Test]
        public void ShaderInputQueriesRetainNativeSemanticIndices()
        {
            RequireD3DAnalysis();
            Assert.That(D3DShaderInputs.Compile("float4 vert(float4 p:POSITION, float2 uv:TEXCOORD3):SV_POSITION { return p + uv.x; }",
                "vert", ShaderType.Vertex, out var inputs, out string error), Is.True, error);
            var queried = inputs.Single(i => string.Equals(i.Semantic, "TEXCOORD", StringComparison.OrdinalIgnoreCase));
            Assert.That(queried.SemanticIndex, Is.EqualTo(3));
            Assert.That(queried.ReadMask, Is.Not.EqualTo(0));
        }

        [Test]
        public void ImportedShaderPassProofsShareOnlyIdenticalSourceContexts()
        {
            var owner = MeshInputShader("UsePassOwner");
            var root = MeshInputShader("UsePassSelected");
            var sources = new ShaderPropertyUsage();
            var ownerPrograms = sources.GetPrograms(owner);
            var rootPrograms = sources.GetPrograms(root);
            var owned = ownerPrograms.Passes.First(p => p.Pass.Name == "CLEAN");
            var imported = rootPrograms.Passes.First(p => p.Shader == owner && p.Pass.Name == "CLEAN");
            Assert.That(imported.Pass, Is.SameAs(owned.Pass));
            Assert.That(rootPrograms.IncludeDirectories, Is.EquivalentTo(ownerPrograms.IncludeDirectories));
            var query = ShaderInputQuery.MeshIndices;
            var original = sources.PreprocessorGraph(owner, owner, owned.Pass, query);
            Assert.That(sources.PreprocessorGraph(root, owner, imported.Pass, query), Is.SameAs(original));
            Assert.That(sources.PreprocessorGraph(root, owner, imported.Pass, ShaderInputQuery.Identifier("vert")), Is.Not.SameAs(original));
            rootPrograms.IncludeDirectories.Add("different/include/search/root");
            Assert.That(sources.PreprocessorGraph(root, owner, imported.Pass, query), Is.Not.SameAs(original));
        }

        [Test]
        public void ShaderInputQueryProofCachesDoNotMixDifferentQuestions()
        {
            RequireD3DAnalysis();
            var shader = MeshInputShader("LocalKeyword");
            var sources = new ShaderPropertyUsage();
            var absent = new ShaderInputQuery(new ShaderInputQuery.Semantic("UNRELATED_INPUT"));
            Assert.That(sources.MeshInputs.Issue(shader, absent, BuildTarget.StandaloneWindows64), Is.Null);
            Assert.That(sources.MeshInputs.Issue(shader, ShaderInputQuery.MeshIndices, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
            Assert.That(sources.MeshInputs.Issue(shader, absent, BuildTarget.StandaloneWindows64), Is.Null);
            var graph = ShaderPreprocessorGraph.Analyze(new ShaderPreprocessorGraph.Source("root", "uint a : CUSTOM0;"), null, absent);
            Assert.That(graph.InputAbsent, Is.True);
            graph = ShaderPreprocessorGraph.Analyze(new ShaderPreprocessorGraph.Source("root", "uint a : CUSTOM0;"), null,
                new ShaderInputQuery(new ShaderInputQuery.Semantic("CUSTOM", 0)));
            Assert.That(graph.InputAbsent, Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void ShaderInputProjectionDoesNotExpandUnrelatedMacroCrossProducts(bool queried)
        {
            string choices = string.Concat(Enumerable.Range(0, 12).Select(i =>
                "#if K" + i + "\n#define V" + i + " 1\n#else\n#define V" + i + " 2\n#endif\n"));
            string code = choices + "#define LIGHTING " + string.Join(" + ", Enumerable.Range(0, 12).Select(i => "V" + i)) +
                "\nfloat light = LIGHTING;\n" + (queried ? "#if INPUT\nuint id : CUSTOM3;\n#endif\n" : "float p;\n");
            var graph = ShaderPreprocessorGraph.AnalyzeDeclarations(new ShaderPreprocessorGraph.Source("root", code), null,
                new ShaderInputQuery(new ShaderInputQuery.Semantic("CUSTOM", 3)));
            Assert.That(graph.Issue, Is.Null, graph.Issue);
            Assert.That(graph.InputAbsent, Is.EqualTo(!queried));
            Assert.That(graph.CodeDependencies.Any(n => n.StartsWith("K", StringComparison.Ordinal)), Is.False);
            Assert.That(graph.CanIgnore("INPUT"), Is.False,
                "Declaration projection alone cannot authorize native DCE coverage reductions.");
        }

        [TestCase("#define MAKE(a,b) a ## b\nuint id : MAKE(CUSTOM,3);\n")]
        [TestCase("#define GET(a,b) a ## b\n#define PREFIX_VALUE CUSTOM3\nuint id : GET(PREFIX_,VALUE);\n")]
        public void ShaderInputProjectionRetainsPastedInputsAndPastedMacroReferences(string code)
        {
            var graph = ShaderPreprocessorGraph.AnalyzeDeclarations(new ShaderPreprocessorGraph.Source("root", code), null,
                new ShaderInputQuery(new ShaderInputQuery.Semantic("CUSTOM", 3)));
            Assert.That(graph.InputAbsent, Is.False);
        }

        [TestCase("#define F(v) v.uv\nF(input)", false)]
        [TestCase("#define F(v) v.value\nF(input)", true)]
        [TestCase("#define F(v) unrelated\nF(input)", false)]
        [TestCase("#define F(v) v\nF(input)", true)]
        [TestCase("#define F(v) v\nF(input).uv", false)]
        [TestCase("#define F(v) G\n#define G(v) v.value\nF(unused)(input)", true)]
        public void ShaderInputProjectionSeparatesMacroArgumentsFromAggregateReads(string code, bool relevant)
        {
            var demand = new ShaderInputDemand(new ShaderInputQuery(new ShaderInputQuery.Semantic("CUSTOM", 3)));
            demand.Names.Add("value"); demand.WholeValues.Add("input");
            var graph = ShaderPreprocessorGraph.AnalyzeDemand(new ShaderPreprocessorGraph.Source("root", code), null, demand);
            Assert.That(graph.Issue, Is.Null, graph.Issue);
            Assert.That(graph.CodeDependencies.Contains("value") || graph.CodeDependencies.Contains("input"), Is.EqualTo(relevant));
        }

        [Test]
        public void ShaderInputProjectionCoversGeneratedConcretePasteVariants()
        {
            for (int sample = 0; sample < 48; sample++)
            {
                string name = "VALUE" + sample;
                string code = "#define CAT(a,b) a ## b\n#define EXPAND(a,b) CAT(a,b)\n#if CHOICE\n#define SUFFIX " + sample +
                    "\n#else\n#define SUFFIX " + (sample + 1) + "\n#endif\nEXPAND(VALUE,SUFFIX)\n";
                var query = new ShaderInputQuery(new ShaderInputQuery.Semantic(name));
                var graph = ShaderPreprocessorGraph.AnalyzeDeclarations(new ShaderPreprocessorGraph.Source("root", code), null, query);
                foreach (var dialect in new[] { PpDialect.Standard, PpDialect.UnityCaching })
                    for (int enabled = 0; enabled < 2; enabled++)
                    {
                        string concrete = PreprocessConcrete("#define CHOICE " + enabled + "\n" + code, dialect: dialect);
                        if (PpLexer.Lex(concrete).Any(t => query.MatchesToken(t.Text)))
                            Assert.That(graph.InputAbsent, Is.False, code + "\n" + concrete);
                    }
            }
        }
    }
}
