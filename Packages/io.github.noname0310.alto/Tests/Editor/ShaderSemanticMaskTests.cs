using System.Linq;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor.Rendering;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(1, 2, "x", 1)] [TestCase(1, 2, "y", 2)]
        [TestCase(2, 2, "x", 1)] [TestCase(2, 2, "y", 2)]
        [TestCase(3, 1, "x", 1)] [TestCase(1, 3, "z", 4)]
        [TestCase(0, 4, "w", 8)]
        public void CompiledShaderInputsUseLogicalComponentsOfPackedSemantics(int prefixWidth, int valueWidth, string component, int expected)
        {
            RequireD3DAnalysis();
            string Type(int width) => width == 1 ? "float" : "float" + width;
            string prefix = prefixWidth == 0 ? "" : Type(prefixWidth) + " unused:TEXCOORD0, ";
            string source = "float4 main(" + prefix + Type(valueWidth) + " value:TEXCOORD1):SV_Target { return value." + component + "; }";
            Assert.That(D3DShaderInputs.CompileUncached(source, "main", ShaderType.Fragment, out var inputs, out string issue), Is.True, issue);
            var read = inputs.Single(i => i.Kind == D3DShaderInputs.InputKind.Semantic && i.Semantic == "TEXCOORD" && i.SemanticIndex == 1);
            Assert.That(read.ReadMask, Is.EqualTo(expected), "Dependencies use the HLSL value's components, independent of native register packing");
            if (prefixWidth != 0)
                Assert.That(inputs.Single(i => i.Kind == D3DShaderInputs.InputKind.Semantic && i.Semantic == "TEXCOORD" && i.SemanticIndex == 0).ReadMask, Is.Zero);
        }
    }
}
