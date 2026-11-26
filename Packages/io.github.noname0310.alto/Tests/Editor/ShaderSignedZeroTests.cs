using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(0x80000000u)]
        [TestCase(0x00000001u)] [TestCase(0x80000001u)]
        [TestCase(0x007fffffu)] [TestCase(0x807fffffu)]
        public void ShaderSignedZeroAndSubnormals_NativeConditionalVertexInputRemainsObservedAcrossCachedQueries(uint bits)
        {
            RequireD3DAnalysis();
            var material = Track(new Material(MeshInputShader("SignedZero"))); renderer.sharedMaterial = material;
            var sources = new ShaderPropertyUsage();
            material.SetFloat("_Zero", SignedZero(false));
            Assert.That(sources.MeshInputs.Issue(material, renderer, FrameIndex(), ShaderInputQuery.VertexId,
                BuildTarget.StandaloneWindows64), Is.Null);
            material.SetFloat("_Zero", System.BitConverter.Int32BitsToSingle(unchecked((int)bits)));
            string issue = sources.MeshInputs.Issue(material, renderer, FrameIndex(), ShaderInputQuery.VertexId,
                BuildTarget.StandaloneWindows64);
            Assert.That(issue, Is.Not.Null,
                "The native asuint branch can read SV_VertexID for this payload; positive-zero absence certificates cannot cover it.");
            Assert.That(issue, Does.Contain("may depend"));
            material.SetFloat("_Zero", SignedZero(false));
            Assert.That(sources.MeshInputs.Issue(material, renderer, FrameIndex(), ShaderInputQuery.VertexId,
                BuildTarget.StandaloneWindows64), Is.Null);
        }
    }
}
