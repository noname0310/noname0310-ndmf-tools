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
        private static ShaderInputVariantProof.Result QueryVariants(string code, params string[] keywords)
        {
            var query = new ShaderInputQuery(new ShaderInputQuery.Semantic("CUSTOM", 3));
            (D3DShaderInputs.Input[] Inputs, string Error) Compile(string text, string entry, ShaderType stage)
            {
                D3DShaderInputs.Compile(text, entry, stage, out var inputs, out string error); return (inputs, error);
            }
            return ShaderInputVariantProof.Analyze(query, "vert", enabled =>
            {
                var program = new PpProgram(null); var state = new PpState();
                foreach (string keyword in enabled) state.Macros[keyword] = new PpBinding(new[] { new PpMacro(keyword, PpLexer.Lex("1").ToArray()) });
                var interpreter = new PpInterpreter(program);
                interpreter.Run(program.Parse(new ShaderPreprocessorGraph.Source("query.hlsl", code)), state);
                string concrete = string.Join("\n", interpreter.Output.Where(t => t.Length != 0)
                    .Select(t => "#line " + t[0].Line + " \"query.hlsl\"\n" + PpLexer.Spell(t)));
                var proof = Compile(concrete, "vert", ShaderType.Vertex);
                return new ShaderInputVariantProof.Sample { Original = concrete, Specialized = concrete,
                    Issue = proof.Error, InputLive = proof.Inputs?.Any(i => i.ReadMask != 0 && query.Matches(i.Semantic, i.SemanticIndex)) == true };
            }, demand => ShaderPreprocessorGraph.AnalyzeDemand(new ShaderPreprocessorGraph.Source("query.hlsl", code), null, demand, keywords),
                (graph, representative) => ShaderKeywordPlan.Enumerate(keywords, Array.Empty<string[]>(),
                    keywords.Where(representative).ToDictionary(k => k, k => false), new HashSet<string>(), 256), Compile);
        }

        [Test]
        public void ShaderInputVariantProofIgnoresIndependentOutputPermutations()
        {
            RequireD3DAnalysis();
            var names = Enumerable.Range(0, 12).Select(i => "LIGHT" + i).ToArray();
            string unrelated = string.Concat(names.Select(n => "#ifdef " + n + "\np.x += 0.01;\n#endif\n"));
            string code = "struct V {float4 p:POSITION; uint value:CUSTOM3;};\n" +
                "float Read(uint input, float gain) { return gain == 0 ? 0 : input * gain; }\n" +
                "float4 vert(V v):SV_POSITION {float4 p=v.p;\n" + unrelated + "\nreturn p + Read(v.value,0);}\n";
            var result = QueryVariants(code, names);
            Assert.That(result.Complete, Is.True, result.Issue);
            Assert.That(result.Samples, Is.EqualTo(1), "Unrelated output code may differ without changing the queried input's influence.");
        }

        [TestCase("#ifdef USE\n1\n#else\n0\n#endif")]
        [TestCase("0\n#ifdef USE\n + 1\n#endif")]
        public void ShaderInputVariantProofDiscoversValueChangesAtConsumerCalls(string argument)
        {
            RequireD3DAnalysis();
            string code = "struct V {float4 p:POSITION; uint value:CUSTOM3;};\n" +
                "float Read(uint input, float gain) { return gain == 0 ? 0 : input * gain; }\n" +
                "float4 vert(V v):SV_POSITION { return v.p + Read(v.value,\n" + argument + "\n); }";
            var result = QueryVariants(code, "USE");
            Assert.That(result.Complete, Is.False);
            Assert.That(result.InputLive, Is.True, result.Issue);
            Assert.That(result.Samples, Is.EqualTo(2));
        }

        [Test]
        public void ShaderInputVariantProofRevisitsNewlyDiscoveredAliases()
        {
            RequireD3DAnalysis();
            string code = "struct V {float4 p:POSITION; uint value:CUSTOM3;};\n" +
                "float Read(uint input, float gain) { return gain == 0 ? 0 : input * gain; }\n" +
                "float4 vert(V v):SV_POSITION {\n#ifdef COPY\nuint alias=v.value;\n#ifdef ESCAPE\nreturn v.p+alias;\n#endif\n" +
                "return v.p+Read(alias,0);\n#else\nreturn v.p;\n#endif\n}";
            var result = QueryVariants(code, "COPY", "ESCAPE");
            Assert.That(result.Complete, Is.False);
            Assert.That(result.InputLive, Is.True, result.Issue);
            Assert.That(result.Samples, Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void ShaderInputProjectionRespectsLogicalSourceRegions()
        {
            const string code = "#line 40 \"logical.hlsl\"\n#ifdef CHANGE\n0\n#else\n1\n#endif\n";
            var slice = InputUseSlice("#line 40 \"logical.hlsl\"\nstruct V {float4 p:POSITION;uint value:CUSTOM3;};\n" +
                "float4 vert(V v):SV_POSITION {return v.p;}");
            slice.Regions.Add(("logical.hlsl", 40, 50));
            var demand = new ShaderInputDemand(new ShaderInputQuery(new ShaderInputQuery.Semantic("CUSTOM", 3)));
            demand.Include(slice);
            var graph = ShaderPreprocessorGraph.AnalyzeDemand(new ShaderPreprocessorGraph.Source("physical.hlsl", code), null, demand);
            Assert.That(graph.Issue, Is.Null);
            Assert.That(graph.CodeDependencies, Does.Contain("CHANGE"));
        }

        [Test]
        public void ShaderInputVariantProofTracksGlobalInitializersUsedByCuts()
        {
            RequireD3DAnalysis();
            string code = "static const float GAIN =\n#ifdef ENABLE\n1;\n#else\n0;\n#endif\n" +
                "float Read(uint value) { return GAIN == 0 ? 0 : value * GAIN; }\n" +
                "struct V {float4 p:POSITION;uint value:CUSTOM3;};\nfloat4 vert(V v):SV_POSITION {return v.p+Read(v.value);}";
            var result = QueryVariants(code, "ENABLE");
            Assert.That(result.Complete, Is.False);
            Assert.That(result.InputLive, Is.True, result.Issue);
        }

        [Test]
        public void ShaderInputVariantProofScopesAggregateNamesToTheirFunctions()
        {
            RequireD3DAnalysis();
            var keywords = Enumerable.Range(0, 12).Select(i => "OTHER" + i).ToArray();
            string code = "struct V {float4 p:POSITION;uint value:CUSTOM3;};\nstruct Other {float4 p;};\n" +
                "float4 Echo(Other input) {return input.p;}\nfloat4 Unrelated(Other input) {float4 p=0;\n" +
                string.Concat(keywords.Select(k => "#ifdef " + k + "\np += Echo(input);\n#endif\n")) + "return p;}\n" +
                "float Read(uint value,float gain){return gain == 0 ? 0 : value*gain;}\nfloat4 vert(V input):SV_POSITION {return input.p+Read(input.value,0);}";
            var result = QueryVariants(code, keywords);
            Assert.That(result.Complete, Is.True, result.Issue);
            Assert.That(result.Samples, Is.EqualTo(1));
        }
    }
}
