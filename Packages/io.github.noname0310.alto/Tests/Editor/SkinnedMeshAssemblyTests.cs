using System;
using System.Linq;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private SkinnedMeshRenderer AssemblyPart(string name, Transform[] bones, Vector3 position)
        {
            var obj = PolicyChild(name); obj.transform.localPosition = position;
            var part = obj.AddComponent<SkinnedMeshRenderer>(); part.rootBone = root.transform;
            part.bones = bones; part.localBounds = new Bounds(Vector3.zero, Vector3.one * 20);
            var mesh = Track(new Mesh { name = name });
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.normals = Enumerable.Repeat(Vector3.back, 3).ToArray();
            mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), 3).ToArray();
            mesh.uv = new[] { new Vector2(0.2f, 0.3f), new Vector2(0.6f, 0.1f), new Vector2(0.2f, 0.8f) };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.bindposes = bones.Select(b => b.worldToLocalMatrix * part.localToWorldMatrix).ToArray();
            using var counts = new NativeArray<byte>(new[] { (byte)bones.Length, (byte)bones.Length, (byte)bones.Length }, Allocator.Temp);
            using var weights = new NativeArray<BoneWeight1>(Enumerable.Range(0, 3).SelectMany(_ =>
                Enumerable.Range(0, bones.Length).Select(i => new BoneWeight1 { boneIndex = i, weight = 1f / bones.Length })).ToArray(), Allocator.Temp);
            mesh.SetBoneWeights(counts, weights);
            part.sharedMesh = mesh;
            part.sharedMaterials = new[] { Track(new Material(Shader.Find("Hidden/Noname/Assembly Vertex Inputs"))) };
            return part;
        }

        private static Vector3[] AssemblyWorldVertices(SkinnedMeshRenderer part)
        {
            var baked = new Mesh();
            try { part.BakeMesh(baked, useScale: true); return baked.vertices.Select(part.transform.TransformPoint).ToArray(); }
            finally { UnityEngine.Object.DestroyImmediate(baked); }
        }

        private SkinnedMeshRenderer AssemblyOutput(SkinnedMeshAssembly assembly)
        {
            var output = PolicyChild("Assembled").AddComponent<SkinnedMeshRenderer>();
            output.sharedMesh = assembly.Mesh; output.bones = assembly.Bones; output.rootBone = root.transform;
            output.sharedMaterials = assembly.Materials; output.localBounds = new Bounds(Vector3.zero, Vector3.one * 20);
            for (int shape = 0; shape < assembly.ShapeWeights.Length; shape++) output.SetBlendShapeWeight(shape, assembly.ShapeWeights[shape]);
            return output;
        }

        [TestCase(1)] [TestCase(5)]
        public void MeshAssemblyPreservesSkinningAndCollapsedBonesWithoutInvertingThePose(int influences)
        {
            var bones = Enumerable.Range(0, influences).Select(i => PolicyChild("Bone " + i).transform).ToArray();
            var first = AssemblyPart("First", bones, Vector3.left); var second = AssemblyPart("Second", bones, Vector3.right);
            var originalFirst = first.sharedMesh.vertices; var originalSecond = second.sharedMesh.vertices;
            using var assembly = SkinnedMeshAssembler.Create(new[] { first, second }, true);
            var combined = AssemblyOutput(assembly);
            for (int pose = 0; pose < 3; pose++)
            {
                bones[0].localRotation = Quaternion.Euler(10 * pose, 20 * pose, -15 * pose);
                bones[0].localScale = pose == 2 ? Vector3.zero : Vector3.one;
                var before = AssemblyWorldVertices(first).Concat(AssemblyWorldVertices(second)).ToArray();
                var after = AssemblyWorldVertices(combined);
                Assert.That(after.Length, Is.EqualTo(before.Length));
                for (int vertex = 0; vertex < before.Length; vertex++)
                    Assert.That(Vector3.Distance(after[vertex], before[vertex]), Is.LessThan(0.00001f), "Pose " + pose + ", vertex " + vertex);
            }
            using var counts = combined.sharedMesh.GetBonesPerVertex();
            Assert.That(counts.ToArray(), Is.All.EqualTo(influences));
            Assert.That(first.sharedMesh.vertices, Is.EqualTo(originalFirst)); Assert.That(second.sharedMesh.vertices, Is.EqualTo(originalSecond));
        }

        [Test]
        public void MeshAssemblyPreservesDistinctSameNameMorphsFramesAndExtrapolatedWeights()
        {
            var bone = PolicyChild("Bone").transform;
            var first = AssemblyPart("First", new[] { bone }, Vector3.left);
            var second = AssemblyPart("Second", new[] { bone }, Vector3.right);
            foreach (var part in new[] { first, second })
            {
                Frame(part.sharedMesh, "Same", 50, Vector3.up * 0.1f); Frame(part.sharedMesh, "Same", 100, Vector3.right * 0.3f);
                part.SetBlendShapeWeight(0, part == first ? -25 : 145);
            }
            using var assembly = SkinnedMeshAssembler.Create(new[] { first, second }, true);
            var combined = AssemblyOutput(assembly);
            Assert.That(assembly.ShapeNames[first]["Same"], Is.EqualTo("Same"));
            Assert.That(assembly.ShapeNames[second]["Same"], Is.Not.EqualTo("Same"));
            Assert.That(combined.sharedMesh.blendShapeCount, Is.EqualTo(2));
            var before = AssemblyWorldVertices(first).Concat(AssemblyWorldVertices(second)).ToArray();
            var after = AssemblyWorldVertices(combined);
            for (int vertex = 0; vertex < before.Length; vertex++) Assert.That(Vector3.Distance(after[vertex], before[vertex]), Is.LessThan(0.00001f));
            Assert.That(first.sharedMesh.blendShapeCount, Is.EqualTo(1)); Assert.That(second.GetBlendShapeWeight(0), Is.EqualTo(145));
        }

        [TestCase(false)] [TestCase(true)]
        public void MeshAssemblyHandlesMaterialSlotMappingsAndSubmeshBaseVertices(bool mergeSlots)
        {
            var bone = PolicyChild("Bone").transform;
            var first = AssemblyPart("First", new[] { bone }, Vector3.left);
            var second = AssemblyPart("Second", new[] { bone }, Vector3.right); second.sharedMaterials = first.sharedMaterials;
            second.sharedMesh.SetIndices(new[] { 0, 1, 0 }, MeshTopology.Triangles, 0, false, 1);
            using var assembly = SkinnedMeshAssembler.Create(new[] { first, second }, mergeSlots);
            Assert.That(assembly.Mesh.subMeshCount, Is.EqualTo(mergeSlots ? 1 : 2));
            var indices = Enumerable.Range(0, assembly.Mesh.subMeshCount).SelectMany(s => assembly.Mesh.GetIndices(s)).ToArray();
            Assert.That(indices, Is.EqualTo(new[] { 0, 1, 2, 4, 5, 4 }));
            Assert.That(assembly.MaterialSlots[second][0], Is.EqualTo(mergeSlots ? 0 : 1));
        }

        [TestCase(1)] [TestCase(5)]
        public void UnusedMeshDataRemovalPreservesReferencedVertexDeformationAndAttributes(int influences)
        {
            var bones = Enumerable.Range(0, influences).Select(i => PolicyChild("Used " + i).transform).ToArray();
            var orphan = PolicyChild("Orphan").transform;
            var source = AssemblyPart("Source", bones, Vector3.left); var mesh = source.sharedMesh;
            var positions = new[] { Vector3.one * 10, Vector3.zero, Vector3.right, Vector3.one * 20, Vector3.up };
            mesh.vertices = positions; mesh.normals = Enumerable.Repeat(Vector3.back, 5).ToArray();
            mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), 5).ToArray();
            mesh.colors32 = Enumerable.Range(0, 5).Select(i => new Color32((byte)(i * 30), 100, 150, 255)).ToArray();
            mesh.uv = Enumerable.Range(0, 5).Select(i => new Vector2(i * 0.1f, i * 0.2f)).ToArray();
            mesh.SetUVs(2, Enumerable.Range(0, 5).Select(i => new Vector3(i, -i, i * 2)).ToList());
            source.bones = bones.Append(orphan).ToArray(); mesh.bindposes = source.bones.Select(b => b.worldToLocalMatrix * source.localToWorldMatrix).ToArray();
            using (var counts = new NativeArray<byte>(new[] { (byte)1, (byte)influences, (byte)influences, (byte)1, (byte)influences }, Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(Enumerable.Range(0, 5).SelectMany(v => v == 0 || v == 3 ?
                new[] { new BoneWeight1 { boneIndex = influences, weight = 1 } } :
                Enumerable.Range(0, influences).Select(i => new BoneWeight1 { boneIndex = i, weight = 1f / influences })).ToArray(), Allocator.Temp))
                mesh.SetBoneWeights(counts, weights);
            mesh.SetIndices(new[] { 0, 1, 3 }, MeshTopology.Triangles, 0, false, 1);
            Frame(mesh, "Morph", 50, Vector3.up * 0.1f); Frame(mesh, "Morph", 100, Vector3.right * 0.2f); source.SetBlendShapeWeight(0, 145);
            Assert.That(SkinnedMeshAssembler.HasUnusedData(source), Is.True);
            using var assembly = SkinnedMeshAssembler.Create(new[] { source }, false, removeUnusedData: true);
            var output = AssemblyOutput(assembly); var kept = new[] { 1, 2, 4 };
            Assert.That(assembly.Mesh.vertexCount, Is.EqualTo(3)); Assert.That(assembly.Bones.Length, Is.EqualTo(influences));
            Assert.That(assembly.Mesh.triangles, Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(assembly.Mesh.colors32, Is.EqualTo(kept.Select(i => mesh.colors32[i]).ToArray()));
            Assert.That(assembly.Mesh.uv, Is.EqualTo(kept.Select(i => mesh.uv[i]).ToArray()));
            var uv = new System.Collections.Generic.List<Vector3>(); assembly.Mesh.GetUVs(2, uv);
            Assert.That(uv, Is.EqualTo(kept.Select(i => new Vector3(i, -i, i * 2)).ToArray()));
            for (int pose = 0; pose < 3; pose++)
            {
                bones[0].localRotation = Quaternion.Euler(10 * pose, 15 * pose, 20 * pose);
                bones[0].localScale = pose == 2 ? Vector3.zero : Vector3.one;
                var before = AssemblyWorldVertices(source); var after = AssemblyWorldVertices(output);
                for (int i = 0; i < kept.Length; i++) Assert.That(Vector3.Distance(before[kept[i]], after[i]), Is.LessThan(0.00001f));
            }
            Assert.That(mesh.vertexCount, Is.EqualTo(5)); Assert.That(source.bones.Length, Is.EqualTo(influences + 1));
            Assert.That(source.GetBlendShapeWeight(0), Is.EqualTo(145));
        }

        private Color32[] DrawAssembly(Camera camera, RenderTexture target)
        {
            var previous = RenderTexture.active;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, true);
            try
            {
                camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply(); return image.GetPixels32();
            }
            finally { RenderTexture.active = previous; camera.targetTexture = null; UnityEngine.Object.DestroyImmediate(image); }
        }

        [TestCase(false)] [TestCase(true)]
        public void MeshAssemblyPreservesGpuVertexInputs(bool objectSpace)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("This comparison requires a graphics device.");
            var bone = PolicyChild("Bone").transform;
            var first = AssemblyPart("First", new[] { bone }, new Vector3(-1.1f, 0, 0));
            var second = AssemblyPart("Second", new[] { bone }, new Vector3(0.1f, 0, 0));
            first.sharedMesh.tangents = Array.Empty<Vector4>();
            second.sharedMesh.colors = Enumerable.Repeat(new Color(0.3f, 0.4f, 0.5f, 1), 3).ToArray();
            second.sharedMesh.uv2 = new[] { Vector2.one * 0.2f, Vector2.one * 0.8f, Vector2.one * 0.2f };
            if (objectSpace)
            {
                first.sharedMaterials = new[] { Track(new Material(Shader.Find("Hidden/Noname/Assembly Object Space"))) };
                first.transform.localRotation = Quaternion.Euler(40, 20, -10);
                second.transform.localRotation = Quaternion.Euler(-30, -40, 20);
                second.transform.localScale = new Vector3(2, 0.5f, 1.5f);
                root.transform.localRotation = Quaternion.Euler(0, 0, 15);
                root.transform.localPosition = new Vector3(0.3f, 0.2f, 0);
            }
            second.sharedMaterials = first.sharedMaterials;
            using var assembly = SkinnedMeshAssembler.Create(new[] { first, second }, true);
            var output = AssemblyOutput(assembly); output.enabled = false;
            var camera = PolicyChild("Camera").AddComponent<Camera>(); camera.scene = scene;
            camera.transform.localPosition = new Vector3(0, 0.5f, -4); camera.orthographic = true; camera.orthographicSize = 1.3f;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; camera.enabled = false;
            var target = RenderTexture.GetTemporary(96, 96, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try
            {
                var before = DrawAssembly(camera, target); first.enabled = false; second.enabled = false; output.enabled = true;
                var after = DrawAssembly(camera, target);
                Assert.That(before.Any(p => p.r > 20), Is.True, "The fixture must actually render visible triangles.");
                int difference = before.Zip(after, (a, b) => Math.Max(Math.Max(Math.Abs(a.r - b.r), Math.Abs(a.g - b.g)), Math.Abs(a.b - b.b))).Max();
                Assert.That(difference, Is.LessThanOrEqualTo(1), "Vertex inputs changed on the GPU.");
            }
            finally { RenderTexture.ReleaseTemporary(target); }
        }
    }
}
