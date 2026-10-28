using System;
using System.Linq;
using Noname.AvatarTools.Occlusion;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private Mesh OcclusionMaskMesh()
        {
            var mesh = Track(new Mesh());
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, Vector3.forward + Vector3.right, Vector3.forward + Vector3.up };
            mesh.subMeshCount = 2; mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0);
            mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 1, true, 3);
            var a = new Vector3[6]; a[0] = Vector3.left;
            var b = new Vector3[6]; b[4] = Vector3.right;
            var both = new Vector3[6]; both[1] = both[4] = Vector3.up;
            mesh.AddBlendShapeFrame("Region A", 100, a, null, null); mesh.AddBlendShapeFrame("Region B", 100, b, null, null);
            mesh.AddBlendShapeFrame("Overlapping", 100, both, null, null);
            mesh.AddBlendShapeFrame("Multiple frames", 50, a, null, null); mesh.AddBlendShapeFrame("Multiple frames", 100, b, null, null);
            return mesh;
        }

        [Test]
        public void OcclusionMasksIncludeBoundaryTrianglesBaseVerticesAndEveryFrame()
        {
            var mask = OcclusionRegionMasks.Create(OcclusionMaskMesh(), new[] { "Region A", "Region B", "Overlapping", "Multiple frames" }, 0.001f);
            Assert.That(mask.Indices, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5 }));
            Assert.That(mask.Regions[0].Triangles, Is.EqualTo(new[] { 0 })); Assert.That(mask.Regions[1].Triangles, Is.EqualTo(new[] { 1 }));
            Assert.That(mask.Regions[2].Triangles, Is.EqualTo(new[] { 0, 1 })); Assert.That(mask.Regions[3].Triangles, Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public void OcclusionRegionsRequireHiddenCoverageInEveryPoseAndHandleOverlap()
        {
            var mask = OcclusionRegionMasks.Create(OcclusionMaskMesh(), new[] { "Region A", "Region B", "Overlapping" }, 0);
            var remove = mask.RemovalMask(new[] { new uint[] { 1, 3 }, new uint[] { 1, 1 } }, out var names);
            Assert.That(names, Is.EqualTo(new[] { "Region A" })); Assert.That(remove, Is.EqualTo(new[] { true, false }));
            Assert.That(mask.RemovalMask(new[] { new uint[] { 1, 1 }, new uint[] { 0, 3 } }, out names), Is.EqualTo(new[] { false, false }));
            Assert.That(names, Is.Empty, "Unobserved pixels are uncertainty, not proof of occlusion.");
        }

        [Test]
        public void OcclusionFilteringRetainsVertexMorphAndSubmeshInterfacesWithoutChangingTheSource()
        {
            var mesh = OcclusionMaskMesh(); var original = mesh.GetIndices(0); var vertices = mesh.vertices;
            var bounds = mesh.bounds; var mask = OcclusionRegionMasks.Create(mesh, new[] { "Region A" }, 0);
            var output = Track(mask.Remove(mesh, new[] { true, false }));
            Assert.That(mesh.GetIndices(0), Is.EqualTo(original)); Assert.That(output.GetIndices(0), Is.Empty);
            Assert.That(output.GetIndices(1), Is.EqualTo(new[] { 3, 4, 5 })); Assert.That(output.subMeshCount, Is.EqualTo(2));
            Assert.That(output.vertices, Is.EqualTo(vertices)); Assert.That(output.bounds, Is.EqualTo(bounds));
            Assert.That(output.blendShapeCount, Is.EqualTo(mesh.blendShapeCount));
            var before = new Vector3[vertices.Length]; var after = new Vector3[vertices.Length];
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
                {
                    mesh.GetBlendShapeFrameVertices(shape, frame, before, null, null);
                    output.GetBlendShapeFrameVertices(shape, frame, after, null, null); Assert.That(after, Is.EqualTo(before));
                }
        }

        private OcclusionSurface OcclusionCube(Vector3 center, Vector3 size, CullMode culling = CullMode.Back)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                cube.transform.SetParent(root.transform, false);
                var mesh = Track(Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh));
                mesh.vertices = mesh.vertices.Select(p => Vector3.Scale(p, size) + center).ToArray(); mesh.RecalculateBounds();
                return new OcclusionSurface { Mesh = mesh, Culling = culling };
            }
            finally { Object.DestroyImmediate(cube); }
        }

        private static void OcclusionGpuRequired()
        {
            var reason = OcclusionRasterizer.UnsupportedReason(); if (reason != null) Assert.Ignore(reason);
        }

        [TestCase(CullMode.Back)] [TestCase(CullMode.Off)]
        public void OcclusionGpuRecognizesAnEnclosingOpaqueShell(CullMode culling)
        {
            OcclusionGpuRequired();
            var vertices = new[] { new Vector3(-0.2f, -0.2f, 0), new Vector3(0.2f, -0.2f, 0), new Vector3(0, 0.2f, 0) };
            var result = OcclusionRasterizer.Evaluate(vertices, new[] { 0, 1, 2 },
                new[] { OcclusionCube(Vector3.zero, Vector3.one, culling) }, OcclusionRasterizer.Directions(64), 64, 1, 0.0001f);
            Assert.That(result, Is.EqualTo(new uint[] { 1 }));
        }

        [Test]
        public void OcclusionGpuNeverUsesAnotherCandidateAsAnOccluder()
        {
            OcclusionGpuRequired();
            var vertices = new[] { new Vector3(-0.2f, -0.2f, 0), new Vector3(0.2f, -0.2f, 0), new Vector3(0, 0.2f, 0),
                new Vector3(-0.2f, -0.2f, 0.2f), new Vector3(0.2f, -0.2f, 0.2f), new Vector3(0, 0.2f, 0.2f) };
            var result = OcclusionRasterizer.Evaluate(vertices, new[] { 0, 1, 2, 3, 4, 5 },
                new[] { OcclusionCube(Vector3.back, Vector3.one * 0.2f) }, new[] { Vector3.forward }, 64, 0, 0.0001f);
            Assert.That(result, Is.EqualTo(new uint[] { 3, 3 }));
        }

        [Test]
        public void OcclusionGpuBoundaryMarginPreservesGeometryNearTheSilhouette()
        {
            OcclusionGpuRequired();
            var vertices = new[] { new Vector3(0.46f, -0.2f, 0), new Vector3(0.49f, -0.2f, 0), new Vector3(0.49f, 0.2f, 0) };
            var occluders = new[] { OcclusionCube(Vector3.zero, Vector3.one) };
            var hidden = OcclusionRasterizer.Evaluate(vertices, new[] { 0, 1, 2 }, occluders, new[] { Vector3.forward }, 128, 0, 0);
            var boundary = OcclusionRasterizer.Evaluate(vertices, new[] { 0, 1, 2 }, occluders, new[] { Vector3.forward }, 128, 4, 0);
            Assert.That(hidden, Is.EqualTo(new uint[] { 1 })); Assert.That(boundary, Is.EqualTo(new uint[] { 3 }));
        }

        [Test]
        public void OcclusionGpuRetainsDegenerateAndUnobservedTriangles()
        {
            OcclusionGpuRequired();
            var result = OcclusionRasterizer.Evaluate(new[] { Vector3.zero }, new[] { 0, 0, 0 },
                new[] { OcclusionCube(Vector3.zero, Vector3.one) }, OcclusionRasterizer.Directions(6), 32, 0, 0);
            Assert.That(result, Is.EqualTo(new uint[] { 0 }));
        }

        [TestCase(0)] [TestCase(2)]
        public void OcclusionGpuDistinguishesExposedGeometryAfterDepthAttachmentSampling(int margin)
        {
            OcclusionGpuRequired();
            var boundary = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/io.github.noname0310.alto/Editor/Occlusion/OcclusionBoundary.compute");
            int kernel = boundary.FindKernel("Erode");
            var previousInput = Track(new RenderTexture(32, 32, 24, RenderTextureFormat.RFloat));
            Assert.That(previousInput.Create(), Is.True);
            try
            {
                // A previous consumer can select the depth aspect of this texture slot.
                // Our distances must subsequently come from the color attachment.
                boundary.SetTexture(kernel, "_Input", previousInput, 0, RenderTextureSubElement.Depth);
                var triangle = new[] { new Vector3(-0.2f, -0.2f, 0), new Vector3(0.2f, -0.2f, 0), new Vector3(0, 0.2f, 0) };
                var vertices = triangle.Concat(triangle.Select(v => v + Vector3.right * 2)).ToArray();
                var result = OcclusionRasterizer.Evaluate(vertices, Enumerable.Range(0, 6).ToArray(),
                    new[] { OcclusionCube(Vector3.zero, Vector3.one, CullMode.Off) },
                    OcclusionRasterizer.Directions(24), 128, margin, 0.0001f);
                Assert.That(result, Is.EqualTo(new uint[] { 1, 3 }), "An exposed triangle must never be classified as enclosed.");
            }
            finally
            {
                boundary.SetTexture(kernel, "_Input", previousInput, 0, RenderTextureSubElement.Color);
                previousInput.Release();
            }
        }

        [Test]
        public void OcclusionDirectionsCoverBothPolesAndNoOccluderMeansNoRemoval()
        {
            var directions = OcclusionRasterizer.Directions(64);
            Assert.That(directions.Length, Is.EqualTo(64)); Assert.That(directions, Has.Member(Vector3.up)); Assert.That(directions, Has.Member(Vector3.down));
            Assert.That(directions.All(v => Mathf.Abs(v.magnitude - 1) < 0.000001f), Is.True);
            Assert.That(OcclusionRasterizer.Evaluate(new[] { Vector3.zero, Vector3.right, Vector3.up }, new[] { 0, 1, 2 },
                Array.Empty<OcclusionSurface>(), directions, 32, 0, 0), Is.EqualTo(new uint[] { 3 }));
        }
    }
}
