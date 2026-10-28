using System.Linq;
using Noname.AvatarTools.MaterialAtlas;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void AtlasVertexSplitsPreserveMorphFramesArbitrarySkinningAndExtraUvDimensions()
        {
            var source = Track(new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, normals = Enumerable.Repeat(Vector3.forward, 3).ToArray(),
                tangents = Enumerable.Repeat(new Vector4(1, 0, 0, -1), 3).ToArray(), colors = new[] { Color.red, Color.green, Color.blue },
                bindposes = Enumerable.Repeat(Matrix4x4.identity, 5).ToArray() });
            source.SetUVs(0, new[] { new Vector4(0, 0, 7, 8), new Vector4(1, 0, 7, 8), new Vector4(0, 1, 7, 8) }.ToList());
            source.SetUVs(6, new[] { new Vector3(4, 5, 6), new Vector3(7, 8, 9), new Vector3(1, 2, 3) }.ToList());
            source.subMeshCount = 2; source.SetTriangles(new[] { 0, 1, 2 }, 0); source.SetTriangles(new[] { 2, 1, 0 }, 1);
            using (var counts = new NativeArray<byte>(new byte[] { 5, 5, 5 }, Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(Enumerable.Range(0, 15).Select(i => new BoneWeight1 { boneIndex = i % 5, weight = 0.2f }).ToArray(), Allocator.Temp))
                source.SetBoneWeights(counts, weights);
            var deltas = new[] { new Vector3(1, 2, 3), new Vector3(4, 5, 6), new Vector3(7, 8, 9) };
            source.AddBlendShapeFrame("Morph", 50, deltas, deltas, deltas);
            source.AddBlendShapeFrame("Morph", 100, deltas.Select(v => v * 2).ToArray(), deltas, deltas);
            var output = Track(AtlasMeshRemap.Apply(source, new Rect?[] { new Rect(0.1f, 0.2f, 0.3f, 0.4f), null }, new Rect(0, 0, 1, 1)));
            Assert.That(output.vertexCount, Is.EqualTo(6)); Assert.That(source.vertexCount, Is.EqualTo(3));
            Assert.That(output.GetBlendShapeFrameCount(0), Is.EqualTo(2)); Assert.That(output.bindposes, Is.EqualTo(source.bindposes));
            using var resultCounts = output.GetBonesPerVertex(); using var resultWeights = output.GetAllBoneWeights();
            Assert.That(resultCounts.All(c => c == 5), Is.True); Assert.That(resultWeights.Length, Is.EqualTo(30));
            var mapped = new Vector3[6]; output.GetBlendShapeFrameVertices(0, 1, mapped, null, null);
            var uv = new System.Collections.Generic.List<Vector4>(); output.GetUVs(0, uv);
            var extraUv = new System.Collections.Generic.List<Vector3>(); output.GetUVs(6, extraUv);
            for (int slot = 0; slot < 2; slot++)
            {
                var before = source.GetTriangles(slot); var after = output.GetTriangles(slot);
                for (int i = 0; i < before.Length; i++)
                {
                    Assert.That(output.vertices[after[i]], Is.EqualTo(source.vertices[before[i]]));
                    Assert.That(mapped[after[i]], Is.EqualTo(deltas[before[i]] * 2));
                    Assert.That(output.colors[after[i]], Is.EqualTo(source.colors[before[i]]));
                    Assert.That(output.tangents[after[i]], Is.EqualTo(source.tangents[before[i]]));
                    Assert.That(uv[after[i]].z, Is.EqualTo(7)); Assert.That(uv[after[i]].w, Is.EqualTo(8));
                    var expected = source.uv[before[i]];
                    if (slot == 0) expected = Vector2.Scale(expected, new Vector2(0.3f, 0.4f)) + new Vector2(0.1f, 0.2f);
                    Assert.That((Vector2)uv[after[i]], Is.EqualTo(expected));
                }
            }
            Assert.That(extraUv[0], Is.EqualTo(extraUv[output.GetTriangles(1)[2]]));
        }
    }
}
