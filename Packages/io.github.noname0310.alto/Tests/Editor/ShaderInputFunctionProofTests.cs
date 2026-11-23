using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static ShaderInputFunctionProof.Result FunctionInputProof(string source, string name, int argument,
            IReadOnlyDictionary<int, string> constants = null)
        {
            var program = new ShaderInputProgram(source);
            Assert.That(program.Issue, Is.Null, program.Issue);
            return ShaderInputFunctionProof.Analyze(source, program, program.Functions[name][0], argument, constants,
                (code, entry, stage) =>
                {
                    D3DShaderInputs.Compile(code, entry, stage, out var inputs, out string error);
                    return (inputs, error);
                });
        }

        [Test]
        public void ShaderInputFunctionProofUsesUnknownCompanionArguments()
        {
            RequireD3DAnalysis();
            const string code = "float Query(float value, float gain) { return gain == 0 ? 0 : value * gain; }";
            var proof = FunctionInputProof(code, "Query", 0);
            Assert.That(proof.Issue, Is.Null, proof.Issue);
            Assert.That(proof.Independent, Is.False, "A representative zero gain cannot stand for all possible gains.");
            proof = FunctionInputProof(code, "Query", 0, new Dictionary<int, string> { [1] = "0.0" });
            Assert.That(proof.Issue, Is.Null, proof.Issue);
            Assert.That(proof.Independent, Is.True);
        }

        [Test]
        public void ShaderInputFunctionProofHandlesConstantArrayArguments()
        {
            RequireD3DAnalysis();
            const string code = "float2 Regions(uint value, int limits[2]) { return float2(value < limits[0], value >= limits[1]); }\n" +
                "bool Query(uint value, int limits[2], float flags[2]) { return dot(Regions(value, limits), float2(flags[0], flags[1])); }";
            var proof = FunctionInputProof(code, "Query", 0, new Dictionary<int, string> { [2] = "{0.0,0.0}" });
            Assert.That(proof.Issue, Is.Null, proof.Issue); Assert.That(proof.Independent, Is.True);
            Assert.That(proof.Functions, Is.EquivalentTo(new[] { "Query", "Regions" }));
            proof = FunctionInputProof(code, "Query", 0, new Dictionary<int, string> { [2] = "{1.0,0.0}" });
            Assert.That(proof.Issue, Is.Null, proof.Issue); Assert.That(proof.Independent, Is.False);
        }

        [TestCase("uint Query(uint value) { return 16777216u + (value & 1u); }")]
        [TestCase("float4 Query(float value) { return float4(0,0,0,value); }")]
        [TestCase("bool4 Query(uint value) { return bool4(false,false,false,value != 0); }")]
        public void ShaderInputFunctionProofObservesAllReturnBitsAndComponents(string code)
        {
            RequireD3DAnalysis();
            var proof = FunctionInputProof(code, "Query", 0);
            Assert.That(proof.Issue, Is.Null, proof.Issue); Assert.That(proof.Independent, Is.False);
        }

        [TestCase("static uint sideEffect; uint Query(uint value) { sideEffect=value; return 0; }")]
        [TestCase("uint Query(uint value, out uint written) { written=value; return 0; }")]
        [TestCase("uint Query(uint value) { return Query(value); }")]
        [TestCase("uint Query(uint value) { return 0; } float Query(float value) { return value; }")]
        [TestCase("static uint sideEffect; float round(float value) { sideEffect=(uint)value; return 0; } float Query(float value) { return round(value); }")]
        [TestCase("float Query(float value) { clip(value); return 0; }")]
        public void ShaderInputFunctionProofRejectsUnverifiedEffects(string code)
        {
            var proof = FunctionInputProof(code, "Query", 0);
            Assert.That(proof.Independent, Is.False); Assert.That(proof.Issue, Is.Not.Null);
        }

        [Test]
        public void ShaderInputFunctionProofSharesClosedNumericCallsAcrossUnrelatedPrograms()
        {
            var probes = new List<string>();
            foreach (int unrelated in new[] { 1, 2 })
            {
                string source = "static const float Unrelated=" + unrelated + ";\n" +
                    "float4x4 Helper(uint value){return (float4x4)value;}\nfloat Query(uint value,float gain){return gain==0?0:Helper(value)[0][0];}";
                var program = new ShaderInputProgram(source);
                var proof = ShaderInputFunctionProof.Analyze(source, program, program.Functions["Query"][0], 0,
                    new Dictionary<int, string>{{1,"0"}}, (text, entry, stage) => { probes.Add(text); return (Array.Empty<D3DShaderInputs.Input>(), null); });
                Assert.That(proof.Independent, Is.True, proof.Issue);
            }
            Assert.That(probes[0], Is.EqualTo(probes[1]));
            Assert.That(probes[0], Does.Not.Contain("Unrelated"));
        }
    }
}
