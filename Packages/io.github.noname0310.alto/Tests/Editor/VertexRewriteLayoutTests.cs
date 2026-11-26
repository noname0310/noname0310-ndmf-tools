using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void VertexRewriteLayout_UsesBaseVerticesAndDeduplicatesAcrossSlots()
        {
            var mesh = Track(new Mesh { vertices = new Vector3[8], subMeshCount = 2 });
            mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0, false, 2);
            mesh.SetIndices(new[] { 2, 3, 4 }, MeshTopology.Triangles, 1, false);
            var plan = new VertexRewriteLayout(mesh);
            Assert.That(plan.UsedVertices, Is.EqualTo(3));
            Assert.That(plan.CompactVertexCount, Is.EqualTo(3));
            Assert.That(plan.RemovedBytes, Is.EqualTo(5 * mesh.GetVertexBufferStride(0)));
            Assert.That(VertexRewriteLayout.OriginalIdAttributeBytes(plan.CompactVertexCount), Is.EqualTo(24));
        }

        [Test]
        public void VertexRewriteLayout_CostsOnlyRetainedSparseMorphData()
        {
            var mesh = Track(new Mesh { vertices = new Vector3[6], normals = new Vector3[6], triangles = new[] { 0, 1, 2 } });
            var deltas = new Vector3[6]; deltas[3] = Vector3.right;
            mesh.AddBlendShapeFrame("Tiny", 100, deltas, new Vector3[6], new Vector3[6]);
            deltas = new Vector3[6]; deltas[4] = Vector3.up;
            mesh.AddBlendShapeFrame("Normals", 100, new Vector3[6], deltas, new Vector3[6]);
            var native = new Vector3[6]; mesh.GetBlendShapeFrameVertices(0, 0, native, null, null);
            Assert.That(native[3].x, Is.EqualTo(1), "The native mesh must retain the test delta.");
            var plan = new VertexRewriteLayout(mesh);
            Assert.That(plan.RemovedBytes, Is.EqualTo(3 * mesh.GetVertexBufferStride(0) + 80));
            Assert.That(plan.RemovedBytes, Is.EqualTo(3 * mesh.GetVertexBufferStride(0) + 80), "Cost queries reuse the captured result.");
        }

        [Test]
        public void VertexRewriteLayout_EmptyIndicesDoNotAuthorizeDiscardingAnUnreferencedVertexStream()
        {
            var mesh = Track(new Mesh { vertices = new Vector3[6], subMeshCount = 1 });
            var plan = new VertexRewriteLayout(mesh);
            Assert.That(plan.UsedVertices, Is.Zero);
            Assert.That(plan.CompactVertexCount, Is.EqualTo(6));
            Assert.That(plan.RemovedBytes, Is.Zero);
        }
    }
}
