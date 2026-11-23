using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("#if ENABLE\n#define S SV_VertexID\n#else\n#define S POSITION\n#endif\nuint a : S;\n", 1)]
        [TestCase("#define F(x) x\nuint a : F(SV_VertexID);\n", 1)]
        [TestCase("#define F(x) x\nuint a : F( SV_VertexID);\n", 1)]
        [TestCase("#define S(x) #x\n#define F(x) S(prefix x)\nF(SV_VertexID)\n", 2)]
        [TestCase("#define F(x) x\nuint a : F\n(SV_VertexID);\n", 2)]
        [TestCase("#define CAT(x,y) x ## y\nuint a : CAT(SV_,VertexID);\n", 2)]
        [TestCase("#define S(x) #x\nS(\"literal\")\n", 2)]
        [TestCase("#if 1 ? 1 : 0\nfloat a;\n#endif\n", 2)]
        [TestCase("#define F(...) __VA_ARGS__\nfloat a;\n", 2)]
        public void ShaderDialectSharingKeepsEveryObservedDialectDifference(string source, int dialects)
        {
            var graph = ShaderPreprocessorGraph.Analyze(new ShaderPreprocessorGraph.Source("root", source), null, ShaderInputQuery.VertexId);
            Assert.That(graph.ExecutedDialects, Is.EqualTo(dialects));
            CompareRepeatedDialectGraph(source, null);
        }

        [Test]
        public void ShaderDialectTraceSharingMatchesRepeatedExecution()
        {
            var random = new Random(78032);
            string[] bodies = { "uint a : S;", "uint a : F(S);", "F\n(S)", "STR(S)", "CAT(SV_,VertexID)",
                "#if ENABLE\nF(S)\n#else\nuint a : POSITION;\n#endif", "#if defined(S) && OTHER\nF(S)\n#endif", "F( S)" };
            for (int test = 0; test < 300; test++)
            {
                string source = "#if ENABLE\n#define S SV_VertexID\n#else\n#define S POSITION\n#endif\n" +
                    "#define F(x) " + (random.Next(2) == 0 ? "x" : "(x)") + "\n#define STR(x) #x\n#define CAT(a,b) a ## b\n" +
                    bodies[random.Next(bodies.Length)] + "\n" + bodies[random.Next(bodies.Length)] + "\n";
                CompareRepeatedDialectGraph(source, test % 2 == 0 ? new ShaderInputDemand(ShaderInputQuery.VertexId) : null);
            }
        }

        private static void CompareRepeatedDialectGraph(string text, ShaderInputDemand demand)
        {
            var method = typeof(ShaderPreprocessorGraph).GetMethod("AnalyzeCore", BindingFlags.Static | BindingFlags.NonPublic);
            var args = new object[] { new ShaderPreprocessorGraph.Source("root", text), null, ShaderInputQuery.VertexId,
                null, null, null, demand, false };
            var actual = (ShaderPreprocessorGraph)method.Invoke(null, args);
            args[7] = true;
            var expected = (ShaderPreprocessorGraph)method.Invoke(null, args);
            Assert.That(actual.Issue, Is.EqualTo(expected.Issue), text);
            Assert.That(actual.InputAbsent, Is.EqualTo(expected.InputAbsent), text);
            Assert.That(actual.CodeDependencies, Is.EquivalentTo(expected.CodeDependencies), text);
            Assert.That(actual.IncludesWithoutKeywordPragmas, Is.EqualTo(expected.IncludesWithoutKeywordPragmas), text);
        }
    }
}
