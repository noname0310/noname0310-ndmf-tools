using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    // These fixtures compile real DXBC without importing assets, changing editor
    // settings, or mutating a scene. RDEF stripping exercises Unity's metadata path.
    public sealed class D3DConstantEquivalenceTests
    {
        [SetUp] public void RequireCompiler()
        { if (Application.platform != RuntimePlatform.WindowsEditor) Assert.Ignore("The native D3D compiler is required."); }

        [TestCase("_Value", ".25", .25f)]
        [TestCase("1-_Value", ".75", .25f)]
        [TestCase("_Value*2", ".5", .25f)]
        public void NativeShaderCorrespondence_ExactNormalExpressionsMatch(string expression, string literal, float value)
        {
            var original = Compile("float _Value;float4 main():SV_Target{return " + expression + ";}");
            var candidate = Compile("float4 main():SV_Target{return " + literal + ";}");
            Assert.That(Equivalent(original, candidate, value, out _, out string issue), Is.True, issue);
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeShaderCorrespondence_PreciseInstructionsRetainMismatchProvenance(bool wholeVector)
        {
            string originalSource = wholeVector ? "float _Value;float4 main():SV_Target{precise float4 value=_Value*.9;return value;}" :
                "float _Value;float4 main():SV_Target{precise float value=_Value*.9;return float4(value,2,3,4);}";
            var original = Compile(originalSource);
            Assert.That(Disassembly(original), Does.Contain(wholeVector ? "[precise]" : "[precise(x)]"));
            var candidate = Compile(wholeVector ? "float4 main():SV_Target{return .7*.9;}" : "float4 main():SV_Target{return float4(.7*.9,2,3,4);}");
            Assert.That(Equivalent(original, candidate, .7f, out var names, out string issue), Is.False, issue);
            Assert.That(names, Does.Contain("_Value"), "A parsed numerical mismatch must identify the implicated source edit.");
        }

        [Test]
        public void NativeShaderCorrespondence_PreciseExactProductCanFold()
        {
            var original = Compile("float _Value;float4 main():SV_Target{precise float4 value=_Value*2;return value;}");
            var candidate = Compile("float4 main():SV_Target{return .5;}");
            Assert.That(Equivalent(original, candidate, .25f, out _, out string issue), Is.True, issue);
        }

        [Test]
        public void NativeShaderCorrespondence_PrecisionContractIsPartOfDynamicArithmetic()
        {
            var original = Compile("float _Value;float4 main(float4 input:TEXCOORD):SV_Target{precise float4 value=_Value*input;return value;}");
            var candidate = Compile("float4 main(float4 input:TEXCOORD):SV_Target{return .25*input;}");
            Assert.That(Equivalent(original, candidate, .25f, out _, out _), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeShaderCorrespondence_VectorizedAlternateNeedsCompleteOtherInputDomain(bool domain)
        {
            const string source = "float _Value;float Other;float4 main():SV_Target{return float4(_Value,1-Other,0,1);}";
            var original = Compile(source); var candidate = Compile(source.Replace("float _Value;", "static const float _Value=.25;"));
            Assert.That(Disassembly(original), Does.Contain("mad"));
            IReadOnlyDictionary<string, float[]> values = domain ? new Dictionary<string, float[]> { ["Other"] = new[] { .2f, .8f } } : null;
            Assert.That(D3DConstantEquivalence.TryEquivalent(original, candidate, Constants(.25f), ShaderType.Fragment, out _, out string issue,
                uniformDomains: values), Is.EqualTo(domain), issue);
        }

        [TestCase("Normal", true)] [TestCase("NormalRange", true)] [TestCase("Missing", false)]
        [TestCase("PositiveZero", false)] [TestCase("NegativeZero", false)] [TestCase("Subnormal", false)]
        [TestCase("NaN", false)] [TestCase("Infinity", false)] [TestCase("Mixed", false)] [TestCase("TooMany", false)]
        public void NativeShaderCorrespondence_VectorizedPassthroughRequiresCompleteNormalDomain(string mode, bool expected)
        {
            const string source = "float _Value;float Other;float4 main():SV_Target{return float4(0,1-_Value,Other,1);}";
            var original = Compile(source); var candidate = Compile(source.Replace("float _Value;", "static const float _Value=.25;"));
            Assert.That(Disassembly(original), Does.Contain("mad"));
            Assert.That(Disassembly(candidate), Does.Not.Contain("mad"));
            float[] possible = mode switch
            {
                "NormalRange" => new[] { .2f, -.8f, float.MaxValue, -float.MaxValue, Scalar(0x00800000), Scalar(0x80800000) },
                "PositiveZero" => new[] { 0f }, "NegativeZero" => new[] { Scalar(0x80000000) },
                "Subnormal" => new[] { Scalar(1) }, "NaN" => new[] { Scalar(0x7fc00001) },
                "Infinity" => new[] { float.PositiveInfinity }, "Mixed" => new[] { .2f, Scalar(0x80000000) },
                "TooMany" => Enumerable.Range(1, 65).Select(i => (float)i).ToArray(), _ => new[] { .2f }
            };
            IReadOnlyDictionary<string, float[]> domains = mode == "Missing" ? null : new Dictionary<string, float[]> { ["Other"] = possible };
            Assert.That(D3DConstantEquivalence.TryEquivalent(original, candidate, Constants(.25f), ShaderType.Fragment,
                out _, out string issue, uniformDomains: domains), Is.EqualTo(expected), issue);
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeShaderCorrespondence_NormalAddZeroPreservesInputProvenance(bool precise)
        {
            var original = Compile("float _Value;float Other;float4 main():SV_Target{" + (precise ? "precise " : "") +
                "float value=Other+_Value;return value;}");
            var candidate = Compile("float Other;float4 main():SV_Target{return Other;}");
            var domains = new Dictionary<string, float[]> { ["Other"] = new[] { .2f, -.8f } };
            Assert.That(Disassembly(original), Does.Contain("add "));
            Assert.That(D3DConstantEquivalence.TryEquivalent(original, candidate, Constants(0), ShaderType.Fragment,
                out _, out string issue, uniformDomains: domains), Is.True, issue);
            var changed = Compile("float Other;float4 main():SV_Target{return Other+1;}");
            Assert.That(D3DConstantEquivalence.TryEquivalent(original, changed, Constants(0), ShaderType.Fragment,
                out var names, out _, uniformDomains: domains), Is.False);
            Assert.That(names, Does.Contain("_Value"), "The removed zero operand still contributes its proof provenance.");
        }

        [TestCase("Exact", true)] [TestCase("UniqueRound", true)] [TestCase("Halfway", false)]
        [TestCase("PositiveZero", true)] [TestCase("NegativeZero", true)] [TestCase("Subnormal", false)]
        [TestCase("BothPositiveZero", false)] [TestCase("BothNegativeZero", false)]
        [TestCase("Cancellation", false)] [TestCase("Underflow", false)] [TestCase("Overflow", false)]
        [TestCase("NaN", false)] [TestCase("Infinity", false)] [TestCase("TooMany", false)]
        public void NativeShaderCorrespondence_UnitMadUsesUniqueBoundedDomainResults(string mode, bool expected)
        {
            const string source = "float _Value;float Other;float Addend;float4 main():SV_Target{return Other*_Value+Addend;}";
            float factor = mode == "Exact" || mode == "UniqueRound" || mode == "Halfway" ? -1 : 1;
            float x = mode switch { "Exact" => .8f, "UniqueRound" => .2f, "Halfway" => Scalar(0x33000000),
                "PositiveZero" => 0, "NegativeZero" => Scalar(0x80000000), "BothPositiveZero" => 0,
                "BothNegativeZero" => Scalar(0x80000000), "Subnormal" => Scalar(1),
                "Cancellation" => Scalar(0x00800000), "Underflow" => Scalar(0x00800001), "Overflow" => float.MaxValue,
                "NaN" => Scalar(0x7fc00001), "Infinity" => float.PositiveInfinity, _ => .25f };
            float addend = mode == "BothPositiveZero" ? 0 : mode == "BothNegativeZero" ? Scalar(0x80000000) :
                mode == "Cancellation" || mode == "Underflow" ? Scalar(0x80800000) : mode == "Overflow" ? float.MaxValue : 1;
            var original = Compile(source); var candidate = Compile(source.Replace("float _Value;", "static const float _Value=" + (factor < 0 ? "-1" : "1") + ";"));
            Assert.That(Disassembly(original), Does.Contain("mad"));
            var domain = new Dictionary<string, float[]> { ["Other"] = mode == "TooMany" ? Enumerable.Range(1, 65).Select(i => (float)i).ToArray() : new[] { x }, ["Addend"] = new[] { addend } };
            Assert.That(D3DConstantEquivalence.TryEquivalent(original, candidate, Constants(factor), ShaderType.Fragment, out _, out string issue,
                uniformDomains: domain), Is.EqualTo(expected), issue);
        }

        [TestCase(0u, 0x3f800000u, 0u, true)]
        [TestCase(0x80000000u, 0xbf800000u, 0u, true)]
        [TestCase(0u, 0xbf800000u, 0u, false)]
        [TestCase(0x80000000u, 0x3f800000u, 0u, false)]
        [TestCase(0u, 0x3f800000u, 0x80000000u, false)]
        [TestCase(0x3f800000u, 0xbf800000u, 0x3f800000u, false)]
        [TestCase(1u, 0x3f800000u, 0u, false)]
        [TestCase(0x7f800000u, 0u, 0u, false)]
        [TestCase(0x7fc00001u, 0u, 0u, false)]
        public void NativeShaderCorrespondence_LiteralZeroMadRequiresPositiveExactProductAndSum(uint input, uint factor, uint addend, bool expected)
        {
            var original = Compile("float _Value;float Factor;float Addend;uint4 main():SV_Target{return asuint(_Value*Factor+Addend);}");
            var candidate = Compile("uint4 main():SV_Target{return 0u;}");
            Assert.That(Disassembly(original), Does.Contain("mad"));
            var constants = new Dictionary<string, float> { ["_Value"] = Scalar(input), ["Factor"] = Scalar(factor), ["Addend"] = Scalar(addend) };
            Assert.That(D3DConstantEquivalence.TryEquivalent(original, candidate, constants, ShaderType.Fragment, out _, out string issue),
                Is.EqualTo(expected), issue);
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeShaderCorrespondence_VectorizedZeroRetainsSignSensitiveRewrite(bool negative)
        {
            const string source = "float _Value;float Other;float4 main():SV_Target{return float4(_Value,1-Other,0,1);}";
            var original = Compile(source); var candidate = Compile(source.Replace("float _Value;", "static const float _Value=0;"));
            Assert.That(Disassembly(original), Does.Contain("mad"));
            var domains = new Dictionary<string, float[]> { ["Other"] = new[] { .2f, .8f } };
            Assert.That(D3DConstantEquivalence.TryEquivalent(original, candidate, Constants(negative ? Scalar(0x80000000) : 0),
                ShaderType.Fragment, out var names, out string issue, uniformDomains: domains), Is.EqualTo(!negative), issue);
            if (negative) Assert.That(names, Does.Contain("_Value"));
        }

        [TestCase(0u)] [TestCase(0x80000000u)] [TestCase(1u)]
        public void NativeShaderCorrespondence_RawUniformBitsAreNotCanonicalized(uint bits)
        {
            var original = Compile("float _Value;uint4 main():SV_Target{return asuint(_Value);}");
            var candidate = Compile("uint4 main():SV_Target{return " + bits + "u;}");
            Assert.That(Equivalent(original, candidate, Scalar(bits), out _, out string issue), Is.True, issue);
        }

        [Test]
        public void NativeShaderCorrespondence_ChangedZeroSignHasPropertyProvenance()
        {
            var original = Compile("float _Value;uint4 main():SV_Target{return asuint(_Value);}");
            var candidate = Compile("uint4 main():SV_Target{return 0u;}");
            Assert.That(Equivalent(original, candidate, Scalar(0x80000000), out var names, out _), Is.False);
            Assert.That(names, Is.EquivalentTo(new[] { "_Value" }));
        }

        [Test]
        public void NativeShaderCorrespondence_RoundedProductRemainsOpaque()
        {
            var original = Compile("float _Value;uint4 main():SV_Target{return asuint(_Value*.9);}");
            var candidate = Compile("uint4 main():SV_Target{return asuint(.7*.9);}");
            Assert.That(Equivalent(original, candidate, .7f, out var names, out _), Is.False);
            Assert.That(names, Is.EquivalentTo(new[] { "_Value" }));
        }

        [Test]
        public void NativeShaderCorrespondence_NaNNegationDoesNotAssumePayloadBits()
        {
            var original = Compile("float _Value;uint4 main():SV_Target{return asuint(-_Value);}");
            var candidate = Compile("uint4 main():SV_Target{return 0xffc00001u;}");
            Assert.That(Equivalent(original, candidate, Scalar(0x7fc00001), out _, out _), Is.False);
        }

        [Test]
        public void NativeShaderCorrespondence_SelectedBranchAndConditionalReturnMatch()
        {
            var original = Compile("float _Value;float4 main(float4 x:TEXCOORD):SV_Target{if(_Value>.5)return x;else return 1;}");
            var candidate = Compile("float4 main(float4 x:TEXCOORD):SV_Target{return x;}");
            Assert.That(Equivalent(original, candidate, .7f, out _, out string issue), Is.True, issue);
        }

        [Test]
        public void NativeShaderCorrespondence_TextureOperandsPreserveNativeOperation()
        {
            const string texture = "Texture2D T;SamplerState S;float4 main(float2 uv:TEXCOORD):SV_Target{return T.Sample(S,uv)*";
            var original = Compile("float _Value;" + texture + "_Value;}"); var candidate = Compile(texture + ".25;}");
            Assert.That(Equivalent(original, candidate, .25f, out _, out string issue), Is.True, issue);
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeShaderCorrespondence_InlineSamplerOnlyPermitsSemanticDependencyProof(bool vertexAddress)
        {
            var program = Compile("Texture2D T;SamplerState InlineSampler;float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION{" +
                "return p+T.SampleLevel(InlineSampler,float2(" + (vertexAddress ? "id" : "0") + ",0),0);}", strip: true, vertex: true);
            program = With(program, "TextureBindings", new[] { Data<ShaderData.TextureBindingInfo>(
                ("Name", "T"), ("Index", 0), ("SamplerIndex", -1), ("ArraySize", 1), ("Dim", TextureDimension.Tex2D)) });
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out _, out string namedIssue), Is.False);
            Assert.That(namedIssue, Does.Contain("metadata binding"));
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out var inputs, out string issue,
                "SV_POSITION", semanticInputsOnly: true), Is.True, issue);
            Assert.That(inputs.All(i => i.Kind == D3DShaderInputs.InputKind.Semantic), Is.True,
                "Opaque bindings cannot make claims about named uniforms or resources.");
            Assert.That(inputs.Any(i => i.Semantic == "POSITION"), Is.True);
            Assert.That(inputs.Any(i => i.Semantic == "SV_VERTEXID"), Is.EqualTo(vertexAddress));
            Assert.That(D3DConstantEquivalence.TryEquivalent(program, program, Constants(0), ShaderType.Vertex, out _, out _), Is.False,
                "Runtime correspondence still requires verified resource ownership.");
        }

        [Test]
        public void NativeShaderCorrespondence_SharedSamplerKeepsEveryTextureAddressDependency()
        {
            var program = Compile("Texture2D A;Texture2D B;SamplerState Shared;float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION{" +
                "return p+A.SampleLevel(Shared,float2(0,0),0)+B.SampleLevel(Shared,float2(id,0),0);}", strip: true, vertex: true);
            program = With(program, "TextureBindings", new[] { "A", "B" }.Select((name, index) => Data<ShaderData.TextureBindingInfo>(
                ("Name", name), ("Index", index), ("SamplerIndex", 0), ("ArraySize", 1), ("Dim", TextureDimension.Tex2D))).ToArray());
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out _, out _), Is.False);
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out var inputs, out string issue,
                "SV_POSITION", semanticInputsOnly: true), Is.True, issue);
            Assert.That(inputs.Any(i => i.Semantic == "SV_VERTEXID"), Is.True);
            Assert.That(inputs.All(i => i.Kind == D3DShaderInputs.InputKind.Semantic), Is.True);
        }

        [Test]
        public void NativeShaderCorrespondence_PositionSliceUsesActualNativeBranch()
        {
            var program = Compile("float _Value;float _Unused;float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION{return _Value>.5?p:float4(id,0,0,1);}", vertex: true);
            var observed = new HashSet<string>(); var constants = Constants(.7f); constants.Add("_Unused", 1);
            Assert.That(D3DConstantEquivalence.TryInputs(program, constants, ShaderType.Vertex, out var inputs, out string issue, "SV_POSITION",
                observedConstants: observed), Is.True, issue);
            Assert.That(observed, Is.EquivalentTo(new[] { "_Value" }), "Keep the eliminated branch condition, not an unused declaration.");
            Assert.That(inputs.Any(i => i.Semantic == "POSITION" && i.ReadMask != 0), Is.True);
            Assert.That(inputs.Any(i => i.Semantic == "SV_VERTEXID" && i.ReadMask != 0), Is.False);
        }

        [Test]
        public void NativeShaderCorrespondence_DiscardedQuotientKeepsVertexIdRemainderDependency()
        {
            var program = Compile("float4 main(uint id:SV_VertexID):SV_POSITION{return float4(id%3==1?.8:-.8,id%3==2?.8:-.8,0,1);}", vertex: true);
            Assert.That(Disassembly(program), Does.Contain("udiv null,"));
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out var inputs, out string issue, "SV_POSITION"),
                Is.True, issue);
            Assert.That(inputs.Where(i => i.Kind == D3DShaderInputs.InputKind.Semantic).Select(i => (i.Semantic, i.ReadMask)),
                Is.EquivalentTo(new[] { ("SV_VERTEXID", (byte)1) }));
        }

        [Test]
        public void NativeShaderCorrespondence_QuotientAndRemainderAreDistinctObservableOutputs()
        {
            var quotient = Compile("uint4 main(uint value:TEXCOORD):SV_Target{return value/3u;}");
            var remainder = Compile("uint4 main(uint value:TEXCOORD):SV_Target{return value%3u;}");
            Assert.That(Disassembly(quotient), Does.Contain(", null,"));
            Assert.That(Disassembly(remainder), Does.Contain("udiv null,"));
            Assert.That(D3DConstantEquivalence.TryEquivalent(quotient, remainder, Constants(0), ShaderType.Fragment, out _, out string issue), Is.False);
            Assert.That(issue, Does.Contain("observable expressions differ"));
        }

        [TestCase(7u, 3u)] [TestCase(uint.MaxValue, 3u)] [TestCase(0u, 3u)] [TestCase(7u, 0u)]
        public void NativeShaderCorrespondence_UnsignedDivideFoldsBothNativeDestinations(uint value, uint divisor)
        {
            var original = Compile("float _Value;float Divisor;uint4 main():SV_Target{uint v=asuint(_Value),d=asuint(Divisor);return uint4(v/d,v%d,0,1);}");
            string assembly = Disassembly(original);
            Assert.That(assembly, Does.Contain("udiv "));
            Assert.That(assembly, Does.Not.Contain("udiv null,").And.Not.Contain(", null,"));
            uint quotient = divisor == 0 ? uint.MaxValue : value / divisor, remainder = divisor == 0 ? uint.MaxValue : value % divisor;
            var candidate = Compile("uint4 main():SV_Target{return uint4(" + quotient + "u," + remainder + "u,0,1);}");
            var constants = new Dictionary<string, float> { ["_Value"] = Scalar(value), ["Divisor"] = Scalar(divisor) };
            Assert.That(D3DConstantEquivalence.TryEquivalent(original, candidate, constants, ShaderType.Fragment, out _, out string issue), Is.True, issue);
        }

        [Test]
        public void NativeShaderCorrespondence_UnsignedDivideReadsInputsBeforeBothOutputs()
        {
            var original = Compile("float _Value;float Divisor;uint4 main():SV_Target{uint v=asuint(_Value)^256u,d=asuint(Divisor);" +
                "uint q=v/d,r=v%d;return uint4(q^16u,r^32u,0,1);}");
            Assert.That(Disassembly(original), Does.Match(@"udiv (r\d+\.[xyzw]), r\d+\.[xyzw], \1,"),
                "The quotient destination must alias the numerator to exercise simultaneous output writes.");
            var candidate = Compile("uint4 main():SV_Target{return uint4(71u,34u,0,1);}");
            var constants = new Dictionary<string, float> { ["_Value"] = Scalar(7), ["Divisor"] = Scalar(3) };
            Assert.That(D3DConstantEquivalence.TryEquivalent(original, candidate, constants, ShaderType.Fragment, out _, out string issue), Is.True, issue);
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeShaderCorrespondence_PositionDependenciesExcludeFragmentOnlyVaryings(bool displace)
        {
            const string declarations = "struct V { float4 p:POSITION; float3 n:NORMAL; float2 uv:TEXCOORD0; };\n" +
                "struct O { float4 p:SV_POSITION; float2 uv:TEXCOORD0; };\n";
            string source = declarations + "O main(V v) { O o; o.p=v.p" + (displace ? "+float4(v.n,0)" : "") +
                "; o.uv=v.uv; return o; }";
            var program = Compile(source, vertex: true);
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex,
                out var inputs, out string issue, "SV_POSITION"), Is.True, issue);
            Assert.That(inputs.Any(i => i.Kind == D3DShaderInputs.InputKind.Semantic && i.Semantic == "TEXCOORD" && i.ReadMask != 0), Is.False);
            Assert.That(inputs.Any(i => i.Kind == D3DShaderInputs.InputKind.Semantic && i.Semantic == "NORMAL" && i.ReadMask != 0), Is.EqualTo(displace));
        }

        [Test]
        public void NativeShaderCorrespondence_OutputSlicingRetainsNativeSideEffects()
        {
            var program = Compile("RWStructuredBuffer<float> Output;float4 main(float4 p:TEXCOORD):SV_Target{Output[0]=p.x;return p;}");
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Fragment,
                out _, out _), Is.False, "An output slice must not hide native resource writes.");
        }

        [Test]
        public void NativeShaderCorrespondence_PackedPhysicalLaneIsNotLogicalComponentIdentity()
        {
            var original = Compile("float4 main(float a:TEXCOORD0,float2 b:TEXCOORD1):SV_Target{return b.x;}");
            var changed = Compile("float4 main(float2 b:TEXCOORD1):SV_Target{return b.y;}");
            Assert.That(Equivalent(original, changed, 0, out _, out _), Is.False);
            var repacked = Compile("float4 main(float2 b:TEXCOORD1):SV_Target{return b.x;}");
            foreach (var program in new[] { original, repacked })
            {
                Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Fragment, out var inputs, out string issue), Is.True, issue);
                Assert.That(inputs.Single(i => i.Kind == D3DShaderInputs.InputKind.Semantic && i.Semantic == "TEXCOORD" && i.SemanticIndex == 1).ReadMask,
                    Is.EqualTo(1), "Both layouts read logical X, regardless of the physical register lane.");
            }
        }

        [Test]
        public void NativeShaderCorrespondence_SubnormalComparisonCannotHideVertexInput()
        {
            var program = Compile("float _Value;float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION{return _Value>0?p:float4(id,0,0,1);}", vertex: true);
            bool proved = D3DConstantEquivalence.TryInputs(program, Constants(Scalar(1)), ShaderType.Vertex, out var inputs, out _, "SV_POSITION");
            Assert.That(!proved || inputs.Any(i => i.Semantic == "SV_VERTEXID" && i.ReadMask != 0), Is.True);
        }

        [Test]
        public void NativeShaderCorrespondence_IntegerSourceNegationCannotUseFloatSignBits()
        {
            var program = Compile("float _Value;float4 main(uint id:SV_VertexID):SV_POSITION{return int(asuint(_Value))-int(id)>0?float4(id,0,0,1):float4(1,0,0,1);}", vertex: true);
            bool proved = D3DConstantEquivalence.TryInputs(program, Constants(.7f), ShaderType.Vertex, out var inputs, out _, "SV_POSITION");
            Assert.That(!proved || inputs.Any(i => i.Semantic == "SV_VERTEXID" && i.ReadMask != 0), Is.True);
        }

        [TestCase(.25f, false)] [TestCase(-.25f, false)]
        [TestCase(.25f, true)] [TestCase(-.25f, true)]
        public void NativeShaderCorrespondence_ConditionalMoveModifiersUseFloatValuesAndRawConditionBits(float value, bool absolute)
        {
            string expression = absolute ? "-abs(_Value)" : "-_Value";
            string program = "float4 main(float condition:TEXCOORD0,float other:TEXCOORD1):SV_Target {return condition>0?" + expression + ":abs(other);}";
            var original = Compile("float _Value;" + program);
            var candidate = Compile("static const float _Value=" + value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ";" + program);
            Assert.That(Disassembly(original), Does.Match(@"movc[^\r\n]*[-|]"), "Exercise the actual native MOVC operand modifier.");
            Assert.That(Equivalent(original, candidate, value, out _, out string issue), Is.True, issue);
            var changed = Compile("static const float _Value=" + value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ";" +
                program.Replace("condition>0", "condition<0"));
            Assert.That(Equivalent(original, changed, value, out _, out _), Is.False,
                "Supporting value modifiers must not erase the conditional selection.");
        }

        [TestCase("int")] [TestCase("uint")] [TestCase("bool")] [TestCase("float2")]
        public void NativeShaderCorrespondence_NonscalarNativeUniformIsNeverSeeded(string type)
        {
            string value = type == "float2" ? "_Value.x" : "_Value";
            var program = Compile(type + " _Value;float4 main():SV_Target{return " + value + ";}");
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(.25f), ShaderType.Fragment, out var inputs, out string issue), Is.True, issue);
            Assert.That(inputs.Any(i => i.Kind == D3DShaderInputs.InputKind.Uniform && i.Semantic == "_Value"), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeShaderCorrespondence_UniqueSparseBufferMappingIgnoresMetadataOrder(bool reverse)
        {
            var program = Compile("cbuffer A:register(b0){float _Value:packoffset(c2.x);};cbuffer B:register(b1){float Other;};float4 main():SV_Target{return float4(_Value,Other,0,1);}", strip: true);
            var a = BufferInfo("A", 80, FieldInfo("_Value", 32)); var b = BufferInfo("B", 16, FieldInfo("Other", 0));
            program = With(program, "ConstantBuffers", reverse ? new[] { b, a } : new[] { a, b });
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(.25f), ShaderType.Fragment, out var inputs, out string issue), Is.True, issue);
            Assert.That(inputs.Where(i => i.Kind == D3DShaderInputs.InputKind.Uniform).Select(i => i.Semantic), Is.EquivalentTo(new[] { "Other" }));
        }

        [TestCase("Ambiguous")] [TestCase("Extra")] [TestCase("Missing")] [TestCase("Duplicate")] [TestCase("EmptyFields")]
        public void NativeShaderCorrespondence_BufferMappingRequiresExactlyOneCompleteBijection(string layout)
        {
            var program = Compile("cbuffer A:register(b0){float _Value;};cbuffer B:register(b1){float Other;};float4 main():SV_Target{return float4(_Value,Other,0,1);}", strip: true);
            var fields = new List<ShaderData.ConstantBufferInfo> { BufferInfo("A", 16, FieldInfo("_Value", 0)), BufferInfo("B", 16, FieldInfo("Other", 0)) };
            if (layout == "Extra") fields.Add(BufferInfo("C", 16, FieldInfo("Third", 0)));
            if (layout == "Missing") fields.RemoveAt(1);
            if (layout == "Duplicate") fields[1] = fields[0];
            if (layout == "EmptyFields") fields[1] = BufferInfo("B", 16);
            program = With(program, "ConstantBuffers", fields.ToArray());
            bool proved = D3DConstantEquivalence.TryInputs(program, Constants(.25f), ShaderType.Fragment, out var inputs, out string issue);
            Assert.That(proved, Is.EqualTo(layout == "Ambiguous"), issue);
            if (proved) Assert.That(inputs.Where(i => i.Kind == D3DShaderInputs.InputKind.Uniform).Select(i => i.Semantic),
                Is.EquivalentTo(new[] { "_Value", "Other" }), "Ambiguity must retain every possible uniform, including the supplied constant.");
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeShaderCorrespondence_DynamicBufferKeepsAddressAndUniformDependencies(bool vertexIndex)
        {
            string input = vertexIndex ? "uint id:SV_VertexID" : "float4 p:POSITION";
            string index = vertexIndex ? "id" : "Selector";
            string result = vertexIndex ? "Values[" + index + "&3]" : "p+Values[" + index + "&3]";
            var program = Compile("uint Selector;float4 Values[4];float4 main(" + input + "):SV_POSITION{return " + result + ";}", vertex: true);
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(.25f), ShaderType.Vertex, out var inputs, out string issue, "SV_POSITION"), Is.True, issue);
            Assert.That(inputs.Any(i => i.Kind == D3DShaderInputs.InputKind.Uniform && i.Semantic == "Values"), Is.True);
            Assert.That(inputs.Any(i => i.Kind == D3DShaderInputs.InputKind.Semantic && i.Semantic == (vertexIndex ? "SV_VERTEXID" : "POSITION")), Is.True);
            if (!vertexIndex) Assert.That(inputs.Any(i => i.Kind == D3DShaderInputs.InputKind.Uniform && i.Semantic == "Selector"), Is.True);
        }

        [TestCase("Small")] [TestCase("Hole")] [TestCase("Overlap")]
        public void NativeShaderCorrespondence_IncompleteScalarStorageRemainsUnknown(string layout)
        {
            var program = Compile("cbuffer A:register(b0){float _Value:packoffset(c2.x);};float4 main():SV_Target{return _Value;}", strip: true);
            var fields = layout == "Overlap" ? new[] { FieldInfo("_Value", 32), FieldInfo("Alias", 32) } : new[] { FieldInfo("_Value", layout == "Hole" ? 36 : layout == "Small" ? 0 : 32) };
            program = With(program, "ConstantBuffers", new[] { BufferInfo("A", layout == "Small" ? 16 : 48, fields) });
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(.25f), ShaderType.Fragment, out _, out _), Is.False);
        }

        [Test]
        public void NativeShaderCorrespondence_UnchangedUnsupportedProgramUsesVerifiedBindings()
        {
            var program = Compile("uint Count;float4 main(float4 x:TEXCOORD):SV_Target{[loop]for(uint i=0;i<Count;i++)x=sin(x);return x;}");
            Assert.That(D3DConstantEquivalence.TryEquivalent(program, program, Constants(.25f), ShaderType.Fragment, out _, out _, true), Is.True);
            Assert.That(D3DConstantEquivalence.TryEquivalent(program, program, Constants(.25f), ShaderType.Fragment, out _, out _, false), Is.True);
            var changed = With(program, "Attributes", new[] { VertexAttribute.Normal });
            Assert.That(D3DConstantEquivalence.TryEquivalent(program, changed, Constants(.25f), ShaderType.Fragment, out _, out _, true), Is.False);
        }

        [Test]
        public void NativeShaderCorrespondence_IdenticalAmbiguousBuffersNeedUnchangedSourcePremise()
        {
            var program = Compile("cbuffer A:register(b0){float _Value;};cbuffer B:register(b1){float Other;};float4 main():SV_Target{return float4(_Value,Other,0,1);}", strip: true);
            program = With(program, "ConstantBuffers", new[] { BufferInfo("A", 16, FieldInfo("_Value", 0)), BufferInfo("B", 16, FieldInfo("Other", 0)) });
            Assert.That(D3DConstantEquivalence.TryEquivalent(program, program, Constants(.25f), ShaderType.Fragment, out _, out _, false), Is.False);
            Assert.That(D3DConstantEquivalence.TryEquivalent(program, program, Constants(.25f), ShaderType.Fragment, out _, out _, true), Is.True);
        }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)]
        public void NativeShaderSemantics_ForcedBufferPairSurvivesOtherAmbiguousSlots(bool reverse, bool ambiguousValue)
        {
            var program = Compile("cbuffer A:register(b0){float _Value:packoffset(c2.x);};" +
                "cbuffer B:register(b1){float Other;};cbuffer C:register(b2){float Third;};" +
                "float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION{return p+Other+Third+(_Value>0?id:0);}", strip: true, vertex: true);
            var buffers = new[] { BufferInfo("A", 64, FieldInfo("_Value", 32)),
                BufferInfo("B", ambiguousValue ? 64 : 16, FieldInfo("Other", 0)),
                BufferInfo("C", 16, FieldInfo("Third", 0)) };
            if (reverse) Array.Reverse(buffers); program = With(program, "ConstantBuffers", buffers);
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out var named, out string issue), Is.True, issue);
            Assert.That(named.Any(i => i.Semantic == "SV_VERTEXID"), Is.True, "The named-input API cannot seed an ambiguous complete layout.");
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out var semantics, out issue,
                semanticInputsOnly: true), Is.True, issue);
            Assert.That(semantics.Any(i => i.Semantic == "SV_VERTEXID"), Is.EqualTo(ambiguousValue));
            Assert.That(D3DConstantEquivalence.TryEquivalent(program, program, Constants(0), ShaderType.Vertex, out _, out _), Is.False,
                "Forced partial ownership is not a runtime correspondence certificate.");
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeShaderSemantics_PureLoopAndSineCosineKeepOnlyLiveInputDependencies(bool enabled)
        {
            var program = Compile("float _Value;uint Count;float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION{" +
                "float4 q=p;[loop]for(uint i=0;i<Count;i++){float s,c;sincos(q.x,s,c);q.x=s+c;}return q+(_Value>0?id:0);}", vertex: true);
            Assert.That(Disassembly(program), Does.Contain("loop").And.Contain("sincos"));
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(enabled ? 1 : 0), ShaderType.Vertex,
                out _, out _), Is.False, "The exact/named evaluator keeps its bounded instruction contract.");
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(enabled ? 1 : 0), ShaderType.Vertex,
                out var inputs, out string issue, semanticInputsOnly: true), Is.True, issue);
            Assert.That(inputs.Any(i => i.Semantic == "SV_VERTEXID"), Is.EqualTo(enabled));
            Assert.That(inputs.Any(i => i.Semantic == "POSITION"), Is.True);
        }

        [TestCase("Initial")] [TestCase("Address")] [TestCase("Count")]
        public void NativeShaderSemantics_LoopRetainsInitialAddressAndTerminationDependencies(string use)
        {
            string initial = use == "Initial" ? "id" : "p.x";
            string count = use == "Count" ? "(Count^id)" : "Count";
            string body = use == "Address" ? "Values[(id+i)&3].x" : "sin(p.x+i)";
            var program = Compile("uint Count;float4 Values[4];float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION{" +
                "float q=" + initial + ";[loop]for(uint i=0;i<" + count + ";i++)q=" + body + ";return float4(q,p.yzw);}", vertex: true);
            Assert.That(Disassembly(program), Does.Contain("loop"));
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out var inputs, out string issue,
                semanticInputsOnly: true), Is.True, issue);
            Assert.That(inputs.Any(i => i.Semantic == "SV_VERTEXID"), Is.True);
        }

        [Test]
        public void NativeShaderSemantics_SwitchWritesHaveDistinctValueIdentities()
        {
            var program = Compile("uint Selector;float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION{" +
                "float a=0,b=0;switch(Selector){case 0:a=p.x;b=p.y;break;case 1:a=p.y;b=p.z;break;default:a=p.z;b=p.x;break;}return id?a:b;}", vertex: true);
            Assert.That(Disassembly(program), Does.Contain("switch"));
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out var inputs, out string issue,
                semanticInputsOnly: true), Is.True, issue);
            Assert.That(inputs.Any(i => i.Semantic == "SV_VERTEXID"), Is.True);
        }

        [TestCase(0f, false)] [TestCase(1f, true)] [TestCase(-1f, true)]
        public void NativeShaderSemantics_DisabledFiniteMaskDoesNotObserveSelectedVertexId(float enabled, bool observed)
        {
            var program = Compile("float _Value;uint Selector;uint Offset;float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION{" +
                "uint selected=0;switch(Selector){case 0:selected=(uint)p.x;break;case 1:selected=id;break;default:selected=Offset;break;}" +
                "float4 unit=saturate(float4(int(selected-Offset),int(selected-Offset-1),int(selected-Offset-2),int(selected-Offset-3)));" +
                "float mask=dot(unit,round(_Value.xxxx));return mask!=0?float4(asfloat(0x7fc00000),0,0,1):p;}", vertex: true);
            Assert.That(Disassembly(program), Does.Contain("switch").And.Contain("dp4"));
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(enabled), ShaderType.Vertex, out var inputs, out string issue,
                semanticInputsOnly: true), Is.True, issue);
            Assert.That(inputs.Any(i => i.Semantic == "SV_VERTEXID"), Is.EqualTo(observed));
        }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void NativeShaderSemantics_NumericZeroDoesNotEraseSignOrNonFiniteReaders(bool negativeZero, bool rawBits)
        {
            string multiplier = rawBits ? "float(int(id)-1)" : "asfloat(id)";
            string result = rawBits ? "float(asuint(z)>>31)" : "(z!=0?1:0)";
            var program = Compile("float _Value;float4 main(uint id:SV_VertexID):SV_POSITION{float z=_Value*" + multiplier + ";return " + result + ";}", vertex: true);
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(negativeZero ? Scalar(0x80000000) : 0), ShaderType.Vertex,
                out var inputs, out string issue, semanticInputsOnly: true), Is.True, issue);
            Assert.That(inputs.Any(i => i.Semantic == "SV_VERTEXID"), Is.True,
                "A numerical zero fact neither fixes its sign bits nor makes zero times NaN finite.");
        }

        [TestCase(0f, false)] [TestCase(1f, true)]
        public void NativeShaderSemantics_IntegerMaskComplementRequiresExactBooleanProvenance(float enabled, bool observed)
        {
            var program = Compile("float _Value;float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION{" +
                "uint flags=(uint)round(_Value);bool selected=id!=0&&(id&flags)==id;return selected?p+1:p;}", vertex: true);
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(enabled), ShaderType.Vertex, out var inputs, out string issue,
                semanticInputsOnly: true), Is.True, issue);
            Assert.That(inputs.Any(i => i.Semantic == "SV_VERTEXID"), Is.EqualTo(observed));
        }

        [Test]
        public void NativeShaderSemantics_LoopWithEarlyReturnRetainsUnsupportedControl()
        {
            var program = Compile("uint Count;float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION{" +
                "[loop]for(uint i=0;i<Count;i++){if(i==id)return p+1;}return p;}", vertex: true);
            Assert.That(Disassembly(program), Does.Contain("loop"));
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out var inputs, out _,
                semanticInputsOnly: true) && !inputs.Any(i => i.Semantic == "SV_VERTEXID"), Is.False);
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void NativeShaderSemantics_StructuredUniformsRemainOpaqueAndKeepDynamicAddresses(bool stripped, bool addressUsesVertexId)
        {
            var program = Compile("float _Value;struct TransformData{float4 row;float flag;};" +
                "cbuffer Instances:register(b1){TransformData Transforms[2];};" +
                "float4 main(float4 p:POSITION,uint id:SV_VertexID,uint instance:SV_InstanceID):SV_POSITION{" +
                "float4 value=Transforms[" + (addressUsesVertexId ? "id" : "instance") + "&1].row;return p+value+(_Value>0?id:0);}", strip: stripped, vertex: true);
            if (stripped)
            {
                var structure = Data<ShaderData.ConstantInfo>(("Name", "Transforms"), ("Index", 0), ("ConstantType", ShaderConstantType.Struct),
                    ("ArraySize", 2), ("StructSize", 32), ("StructFields", new[] { FieldInfo("flag", 16) }));
                program = With(program, "ConstantBuffers", new[] { BufferInfo("$Globals", 16, FieldInfo("_Value", 0)), BufferInfo("Instances", 64, structure) });
            }
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out _, out _), Is.False,
                "Named uniform absence cannot use opaque structure metadata.");
            Assert.That(D3DConstantEquivalence.TryEquivalent(program, program, Constants(0), ShaderType.Vertex, out _, out _), Is.False,
                "Opaque structures do not certify runtime correspondence.");
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out var inputs, out string issue,
                semanticInputsOnly: true), Is.True, issue);
            Assert.That(inputs.Any(i => i.Semantic == "SV_VERTEXID"), Is.EqualTo(addressUsesVertexId));
            Assert.That(inputs.Any(i => i.Semantic == "SV_INSTANCEID"), Is.EqualTo(!addressUsesVertexId));
            Assert.That(inputs.All(i => i.Kind == D3DShaderInputs.InputKind.Semantic), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeShaderSemantics_StructuredScalarMemberCannotSeedVertexIdBranch(bool stripped)
        {
            var program = Compile("struct Data{float _Value;float3 padding;};cbuffer Instances:register(b0){Data Values[2];};" +
                "float4 main(float4 p:POSITION,uint id:SV_VertexID,uint instance:SV_InstanceID):SV_POSITION{" +
                "return Values[instance&1]._Value>0?p+id:p;}", strip: stripped, vertex: true);
            if (stripped)
            {
                var structure = Data<ShaderData.ConstantInfo>(("Name", "Values"), ("Index", 0), ("ConstantType", ShaderConstantType.Struct),
                    ("ArraySize", 2), ("StructSize", 16), ("StructFields", new[] { FieldInfo("_Value", 0) }));
                program = With(program, "ConstantBuffers", new[] { BufferInfo("Instances", 32, structure) });
            }
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out var inputs, out string issue,
                semanticInputsOnly: true), Is.True, issue);
            Assert.That(inputs.Any(i => i.Semantic == "SV_VERTEXID"), Is.True,
                "A field name inside an opaque structure cannot inherit a standalone material scalar value.");
            Assert.That(inputs.Any(i => i.Semantic == "SV_INSTANCEID"), Is.True);
        }

        [TestCase("Independent", false)] [TestCase("Index", true)] [TestCase("Store", true)] [TestCase("Control", true)]
        [TestCase("Loop", true)]
        public void NativeShaderSemantics_IndexableTemporariesPreserveEveryPotentialReader(string use, bool observed)
        {
            string change = use == "Store" ? "values[0]=p+id;" : use == "Control" ? "if(id!=0)values[0]=p+1;" :
                use == "Loop" ? "[loop]for(uint i=0;i<Count;i++)values[0]=p+id;" : "";
            string address = use == "Index" ? "id&3" : "Selector&3";
            var program = Compile("uint Selector;uint Count;float _Value;float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION{" +
                "float4 values[4]={p,p.yzwx,p.zwxy,p.wxyz};" + change + "return values[" + address + "]+(_Value>0?id:0);}", vertex: true);
            Assert.That(Disassembly(program), Does.Contain("dcl_indexableTemp"));
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out _, out _), Is.False);
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out var inputs, out string issue,
                semanticInputsOnly: true), Is.True, issue);
            Assert.That(inputs.Any(i => i.Semantic == "SV_VERTEXID"), Is.EqualTo(observed));
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeShaderSemantics_UnboundedTemporaryAccessCannotProveInputAbsence(bool store)
        {
            var program = Compile("uint Selector;float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION{" +
                "float4 values[4]={p,p.yzwx,p.zwxy,p.wxyz};" + (store ? "values[id]=p;" : "") + "return values[Selector];}", vertex: true);
            Assert.That(Disassembly(program), Does.Contain("dcl_indexableTemp"));
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(0), ShaderType.Vertex, out var inputs, out _,
                semanticInputsOnly: true) && !inputs.Any(i => i.Semantic == "SV_VERTEXID"), Is.False);
        }

        [TestCase("int", 0, true, false)] [TestCase("uint", 0, true, false)] [TestCase("bool", 0, true, false)]
        [TestCase("int", 0, false, true)] [TestCase("int", 1, true, true)] [TestCase("int", -1, true, true)]
        [TestCase("float2", 0, true, true)]
        public void NativeShaderSemantics_IntegerZeroRequiresExplicitScalarUploadEvidence(string type, int value, bool evidence, bool observed)
        {
            string test = type == "float2" ? "_Value.x" : "_Value";
            var program = Compile(type + " _Value;float4 main(float4 p:POSITION,uint id:SV_VertexID):SV_POSITION{return " + test + "!=0?p+id:p;}", vertex: true);
            float supplied = value == -1 ? Scalar(0x80000000) : value;
            var zero = evidence ? new HashSet<string> { "_Value" } : null;
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(supplied), ShaderType.Vertex, out var inputs, out string issue,
                semanticInputsOnly: true, zeroIntegerUniforms: zero), Is.True, issue);
            Assert.That(inputs.Any(i => i.Semantic == "SV_VERTEXID"), Is.EqualTo(observed));
            Assert.That(D3DConstantEquivalence.TryInputs(program, Constants(supplied), ShaderType.Vertex, out var strict, out issue,
                zeroIntegerUniforms: zero), Is.True, issue);
            Assert.That(strict.Any(i => i.Semantic == "SV_VERTEXID"), Is.True, "The exact/named contract does not consume the semantic-only zero upload premise.");
        }

        private static Dictionary<string, float> Constants(float value) => new() { ["_Value"] = value };
        private static float Scalar(uint bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        private static bool Equivalent(ShaderData.VariantCompileInfo original, ShaderData.VariantCompileInfo candidate, float value,
            out HashSet<string> names, out string issue) => D3DConstantEquivalence.TryEquivalent(original, candidate, Constants(value), ShaderType.Fragment, out names, out issue);
        private static ShaderData.ConstantInfo FieldInfo(string name, int offset) => Data<ShaderData.ConstantInfo>(
            ("Name", name), ("Index", offset), ("Rows", 1), ("Columns", 1), ("ConstantType", ShaderConstantType.Vector), ("DataType", ShaderParamType.Float));
        private static ShaderData.ConstantBufferInfo BufferInfo(string name, int size, params ShaderData.ConstantInfo[] fields) =>
            Data<ShaderData.ConstantBufferInfo>(("Name", name), ("Size", size), ("Fields", fields));
        private static T Data<T>(params (string Name, object Value)[] fields) where T : struct
        { object value = default(T); foreach (var field in fields) Set(value, field.Name, field.Value); return (T)value; }
        private static T With<T>(T original, string field, object value) where T : struct
        { object boxed = original; Set(boxed, field, value); return (T)boxed; }
        private static void Set(object owner, string name, object value)
        {
            var field = owner.GetType().GetField("<" + name + ">k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, "The fixture requires Unity's declared immutable compile-result metadata."); field.SetValue(owner, value);
        }

        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        private static extern int D3DCompile(byte[] text, UIntPtr length, string name, IntPtr defines, IntPtr includes,
            string entry, string target, uint flags, uint effectFlags, out IntPtr code, out IntPtr errors);
        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int D3DStripShader(byte[] code, UIntPtr length, uint flags, out IntPtr stripped);
        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int D3DDisassemble(byte[] code, UIntPtr length, uint flags, IntPtr comments, out IntPtr text);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr Pointer(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate UIntPtr Size(IntPtr self);
        private static byte[] Blob(IntPtr value)
        {
            if (value == IntPtr.Zero) return Array.Empty<byte>(); IntPtr table = Marshal.ReadIntPtr(value);
            int length = checked((int)Marshal.GetDelegateForFunctionPointer<Size>(Marshal.ReadIntPtr(table, 4 * IntPtr.Size))(value).ToUInt64());
            var bytes = new byte[length]; Marshal.Copy(Marshal.GetDelegateForFunctionPointer<Pointer>(Marshal.ReadIntPtr(table, 3 * IntPtr.Size))(value), bytes, 0, length); return bytes;
        }
        private static string Disassembly(ShaderData.VariantCompileInfo program)
        {
            IntPtr text = IntPtr.Zero;
            try { Assert.That(D3DDisassemble(program.ShaderData, (UIntPtr)program.ShaderData.Length, 0x80, IntPtr.Zero, out text), Is.GreaterThanOrEqualTo(0)); return Encoding.UTF8.GetString(Blob(text)); }
            finally { if (text != IntPtr.Zero) Marshal.Release(text); }
        }
        private static ShaderData.VariantCompileInfo Compile(string source, bool strip = false, bool vertex = false)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(source); IntPtr code = IntPtr.Zero, errors = IntPtr.Zero;
            try
            {
                int result = D3DCompile(bytes, (UIntPtr)bytes.Length, "ALTO native correspondence fixture", IntPtr.Zero, IntPtr.Zero,
                    "main", vertex ? "vs_5_0" : "ps_5_0", 1u << 15, 0, out code, out errors);
                Assert.That(result, Is.GreaterThanOrEqualTo(0), Encoding.UTF8.GetString(Blob(errors))); bytes = Blob(code);
            }
            finally { if (code != IntPtr.Zero) Marshal.Release(code); if (errors != IntPtr.Zero) Marshal.Release(errors); }
            if (strip)
            {
                code = IntPtr.Zero;
                try { Assert.That(D3DStripShader(bytes, (UIntPtr)bytes.Length, 1, out code), Is.GreaterThanOrEqualTo(0)); bytes = Blob(code); }
                finally { if (code != IntPtr.Zero) Marshal.Release(code); }
            }
            return Data<ShaderData.VariantCompileInfo>(("Success", true), ("ShaderData", bytes));
        }
    }
}
