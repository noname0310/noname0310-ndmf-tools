using System;
using System.Collections.Generic;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static ShaderInputUseSlice InputUseSlice(string source, string specialized = null)
            => AnalyzeInputUseSlice(source, specialized, new ShaderInputQuery(new ShaderInputQuery.Semantic("CUSTOM", 3)));

        private static ShaderInputUseSlice AnalyzeInputUseSlice(string source, string specialized, ShaderInputQuery query)
        {
            var program = new ShaderInputProgram(source);
            var constants = new ShaderInputProgram(specialized ?? source).ScalarConstants;
            return new ShaderInputUseSlice(specialized ?? source, program,
                query, "vert", constants,
                (code, entry, stage) =>
                {
                    D3DShaderInputs.Compile(code, entry, stage, out var inputs, out string error);
                    return (inputs, error);
                });
        }

        [Test]
        public void ShaderInputUseSliceCutsPureConsumersAfterLocalCopies()
        {
            RequireD3DAnalysis();
            const string source = "#line 1 \"query.hlsl\"\nstruct V { float4 p:POSITION; uint value:CUSTOM3; };\n" +
                "float Consume(uint value, float flags[2]) { return (value < 10) * flags[0] + (value > 20) * flags[1]; }\n" +
                "float4 vert(V v):SV_POSITION { float flags[2]={0,0}; uint temporary=v.value; float result=Consume(temporary,flags); return v.p+result; }\n";
            var slice = InputUseSlice(source);
            Assert.That(slice.Complete, Is.True, slice.Issue);
            Assert.That(slice.Cuts, Is.EqualTo(1));
            Assert.That(slice.Names, Does.Contain("temporary"));
            Assert.That(slice.Names, Does.Contain("Consume"));
            Assert.That(slice.WholeValues, Does.Contain("v"));
            Assert.That(slice.Regions.Count, Is.GreaterThan(0));
        }

        [TestCase("return v.p + v.value;")]
        [TestCase("uint x=v.value; if (x>0) return 0; return v.p;")]
        [TestCase("return (float4)v;")]
        [TestCase("uint x=v.value; x++; return v.p;")]
        [TestCase("return v.p + Consume(UnknownEffect(v.value), 0);")]
        [TestCase("uint written=0; uint copy=(written+=v.value); return v.p+Consume(copy,0);")]
        [TestCase("uint written=0; return v.p+Consume(written+=v.value,0);")]
        public void ShaderInputUseSliceRetainsUnprovedConsumers(string body)
        {
            var slice = InputUseSlice("struct V { float4 p:POSITION; uint value:CUSTOM3; };\n" +
                "float Consume(uint value, float gain) { return gain == 0 ? 0 : value * gain; }\nfloat4 vert(V v):SV_POSITION { " + body + " }");
            Assert.That(slice.Complete, Is.False, "Unproved input paths cannot be omitted.");
        }

        [Test]
        public void ShaderInputUseSliceDoesNotFreezeWrittenArrayArguments()
        {
            RequireD3DAnalysis();
            const string source = "struct V { float4 p:POSITION; uint value:CUSTOM3; };\n" +
                "float Consume(uint value, float flags[2]) { return value * flags[0]; }\n" +
                "float4 vert(V v):SV_POSITION { float flags[2]={0,0}; flags[0]=1; return v.p+Consume(v.value,flags); }";
            var slice = InputUseSlice(source);
            Assert.That(slice.Complete, Is.False);
        }

        [Test]
        public void ShaderInputUseSliceDoesNotFreezeOutputArgumentWrites()
        {
            RequireD3DAnalysis();
            const string source = "struct V { float4 p:POSITION; uint value:CUSTOM3; };\n" +
                "void Change(out float flags[2]) { flags[0]=1; flags[1]=1; }\n" +
                "float Consume(uint value, float flags[2]) { return value * flags[0]; }\n" +
                "float4 vert(V v):SV_POSITION { float flags[2]={0,0}; Change(flags); return v.p+Consume(v.value,flags); }";
            Assert.That(InputUseSlice(source).Complete, Is.False);
        }
    }
}
