using System;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("POSITION")] [TestCase("NORMAL")]
        public void HiddenGeometryLayout_FourthNativeComponentCanSeparateCoincidentCorners(string semantic)
        {
            var mesh = Track(new Mesh());
            int stream = semantic == "POSITION" ? 0 : 1;
            mesh.SetVertexBufferParams(3, semantic == "POSITION"
                ? new[] { new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 4) }
                : new[] { new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                    new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 4, 1) });
            if (stream != 0) mesh.SetVertexBufferData(new Vector3[3], 0, 0, 3, 0);
            var values = new[] { new Vector4(0, 0, 0, 0), new Vector4(0, 0, 0, 1), new Vector4(0, 0, 0, 2) };
            mesh.SetVertexBufferData(values, 0, 0, 3, stream);
            mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0, false);
            using (var data = UnityEngine.Mesh.AcquireReadOnlyMeshData(mesh))
                Assert.That(data[0].GetVertexData<Vector4>(stream).ToArray(), Is.EqualTo(values),
                    "The native vertex buffer retains the fourth component omitted by Vector3 getters.");
            string program = "float4 vert(float4 v:" + semantic + "):SV_POSITION { return float4(v.w,v.w*v.w,0,1); }";
            Assert.That(D3DShaderInputs.Compile(program, "vert", ShaderType.Vertex, out var reads, out string issue), Is.True, issue);
            Assert.That(reads.Any(r => r.Semantic == semantic && (r.ReadMask & 8) != 0), Is.True);
            var inputs = new ShaderPositionInputs(); inputs.Reads.Add((semantic, 0), 8);
            Assert.That(new HiddenTriangleProof(mesh).Equivalent(0, 0, inputs), Is.False,
                "Equal xyz values do not prove equality of the native shader's fourth component.");
        }

        [TestCase(false)] [TestCase(true)]
        public void HiddenGeometryLayout_WideIntegerAttributeIsNotProvedByRoundedFloatGetters(bool signed)
        {
            var mesh = Track(new Mesh());
            mesh.SetVertexBufferParams(3,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, signed ? VertexAttributeFormat.SInt32 : VertexAttributeFormat.UInt32, 1, 1));
            mesh.SetVertexBufferData(new Vector3[3], 0, 0, 3);
            var values = new uint[] { 16777216, 16777217, 16777218 };
            mesh.SetVertexBufferData(values, 0, 0, 3, 1);
            mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0, false);
            using (var data = UnityEngine.Mesh.AcquireReadOnlyMeshData(mesh))
                Assert.That(data[0].GetVertexData<uint>(1).ToArray(), Is.EqualTo(values));
            Assert.That((float)values[0], Is.EqualTo((float)values[1]), "A float getter cannot distinguish these native integers.");
            string program = "float4 vert(" + (signed ? "int" : "uint") + " v:TEXCOORD0):SV_POSITION { " +
                "float x=v-16777216" + (signed ? "" : "u") + "; return float4(x,x*x,0,1); }";
            Assert.That(D3DShaderInputs.Compile(program, "vert", ShaderType.Vertex, out var reads, out string issue), Is.True, issue);
            Assert.That(reads.Any(r => r.Semantic == "TEXCOORD" && r.ReadMask != 0), Is.True);
            var proof = new HiddenTriangleProof(mesh);
            var inputs = new ShaderPositionInputs(); inputs.Reads.Add(("TEXCOORD", 0), 1);
            Assert.That(proof.Equivalent(0, 0, inputs), Is.False);
            inputs.Reads.Clear(); inputs.Reads.Add(("POSITION", 0), 7);
            Assert.That(proof.Equivalent(0, 0, inputs), Is.True, "An unused custom attribute does not block the position proof.");
        }

        [TestCase(VertexAttributeFormat.Float16, true)] [TestCase(VertexAttributeFormat.UNorm8, true)]
        [TestCase(VertexAttributeFormat.SNorm8, true)] [TestCase(VertexAttributeFormat.UNorm16, true)]
        [TestCase(VertexAttributeFormat.SNorm16, true)]
        [TestCase(VertexAttributeFormat.UInt8, false)] [TestCase(VertexAttributeFormat.SInt8, false)]
        [TestCase(VertexAttributeFormat.UInt16, false)] [TestCase(VertexAttributeFormat.SInt16, false)]
        public void HiddenGeometryLayout_OnlySupportedExactAttributeGettersAuthorizeRemoval(VertexAttributeFormat format, bool supported)
        {
            var mesh = Track(new Mesh());
            mesh.SetVertexBufferParams(3,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, format, 4, 1));
            mesh.SetVertexBufferData(new Vector3[3], 0, 0, 3);
            var data = new byte[mesh.GetVertexBufferStride(1) * 3];
            mesh.SetVertexBufferData(data, 0, 0, data.Length, 1);
            mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0, false);
            var inputs = new ShaderPositionInputs(); inputs.Reads.Add(("TEXCOORD", 0), 15);
            Assert.That(new HiddenTriangleProof(mesh).Equivalent(0, 0, inputs), Is.EqualTo(supported));
        }
    }
}
