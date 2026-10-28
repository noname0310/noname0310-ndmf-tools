using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

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

        private sealed class AssemblyUvFixture
        {
            internal readonly int[] Dimensions;
            internal readonly Vector4[][] Values;

            internal AssemblyUvFixture(int[] dimensions, int salt = 0)
            {
                Dimensions = dimensions;
                Values = Enumerable.Range(0, 8).Select(channel => Enumerable.Range(0, 3).Select(vertex =>
                    new Vector4((salt + channel + vertex + 1) / 64f, (salt + vertex + 2) / 64f,
                        (channel + vertex + 3) / 16f, (salt + channel + vertex + 4) / 32f)).ToArray()).ToArray();
            }

            internal void Apply(Mesh mesh)
            {
                for (int channel = 0; channel < 8; channel++)
                {
                    if (Dimensions[channel] == 0) mesh.SetUVs(channel, Array.Empty<Vector2>());
                    else if (Dimensions[channel] == 2) mesh.SetUVs(channel, Values[channel].Select(v => new Vector2(v.x, v.y)).ToList());
                    else if (Dimensions[channel] == 3) mesh.SetUVs(channel, Values[channel].Select(v => new Vector3(v.x, v.y, v.z)).ToList());
                    else mesh.SetUVs(channel, Values[channel].ToList());
                }
            }

            // Independent specification: resolve each requested input by searching
            // backward through the authored layout, then apply GPU component defaults.
            internal int Source(int channel)
            {
                while (channel >= 0 && Dimensions[channel] == 0) channel--;
                return channel;
            }

            internal Vector4 ShaderInput(int channel, int vertex)
            {
                int source = Source(channel);
                if (source < 0) return Vector4.zero;
                var value = Values[source][vertex];
                return Dimensions[source] switch
                {
                    2 => new Vector4(value.x, value.y, 0, 1),
                    3 => new Vector4(value.x, value.y, value.z, 1),
                    _ => value
                };
            }
        }

        private static Vector4[] AssemblyUvShaderInputs(Mesh mesh, int channel)
        {
            while (channel >= 0 && !mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + channel)) channel--;
            if (channel < 0) return Enumerable.Repeat(Vector4.zero, mesh.vertexCount).ToArray();
            int dimension = mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0 + channel);
            var values = new List<Vector4>(); mesh.GetUVs(channel, values);
            return values.Select(v => dimension == 2 ? new Vector4(v.x, v.y, 0, 1) :
                dimension == 3 ? new Vector4(v.x, v.y, v.z, 1) : v).ToArray();
        }

        [TestCase(3)] [TestCase(4)]
        public void MeshAssemblyPreservesAliasedUvDimensions(int dimension)
        {
            var bone = PolicyChild("Bone").transform;
            var first = AssemblyPart("First", new[] { bone }, Vector3.left);
            var second = AssemblyPart("Second", new[] { bone }, Vector3.right);
            var firstInput = new AssemblyUvFixture(new[] { dimension, 0, 0, 0, 0, 0, 0, 0 });
            var secondInput = new AssemblyUvFixture(new[] { 2, 2, 0, 0, 0, 0, 0, 0 }, 1);
            firstInput.Apply(first.sharedMesh); secondInput.Apply(second.sharedMesh);
            using var assembly = SkinnedMeshAssembler.Create(new[] { first, second }, false);
            Assert.That(assembly.Mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord1), Is.EqualTo(dimension));
            Assert.That(AssemblyUvShaderInputs(assembly.Mesh, 1), Is.EqualTo(
                Enumerable.Range(0, 3).Select(v => firstInput.ShaderInput(1, v))
                    .Concat(Enumerable.Range(0, 3).Select(v => secondInput.ShaderInput(1, v))).ToArray()));
            Assert.That(assembly.Mesh.HasVertexAttribute(VertexAttribute.TexCoord2), Is.False,
                "Trailing aliases must not allocate redundant vertex channels.");
        }

        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void MeshAssemblyRepresentsAbsentUvDefaultsExplicitly(int dimension)
        {
            var bone = PolicyChild("Bone").transform;
            var first = AssemblyPart("Missing UVs", new[] { bone }, Vector3.left);
            var second = AssemblyPart("Present UVs", new[] { bone }, Vector3.right);
            new AssemblyUvFixture(new int[8]).Apply(first.sharedMesh);
            var present = new AssemblyUvFixture(new[] { dimension, 0, 0, 0, 0, 0, 0, 0 }, 1);
            present.Apply(second.sharedMesh);
            using var assembly = SkinnedMeshAssembler.Create(new[] { first, second }, false);
            Assert.That(assembly.Mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0), Is.EqualTo(4),
                "An explicit w=0 is required for the missing input, even when the other input is only 2D/3D.");
            Assert.That(AssemblyUvShaderInputs(assembly.Mesh, 0), Is.EqualTo(Enumerable.Repeat(Vector4.zero, 3)
                .Concat(Enumerable.Range(0, 3).Select(v => present.ShaderInput(0, v))).ToArray()));
            Assert.That(assembly.Mesh.HasVertexAttribute(VertexAttribute.TexCoord1), Is.False,
                "An absent output channel can still alias the preceding channel.");
        }

        [TestCase(310)] [TestCase(20261029)]
        public void MeshAssemblyUvLayoutsMatchAuthoredInputFuzz(int seed)
        {
            var random = new System.Random(seed);
            var bone = PolicyChild("Bone").transform;
            var choices = new[] { 0, 0, 0, 2, 3, 4 };
            for (int sample = 0; sample < 128; sample++)
            {
                var parts = new List<SkinnedMeshRenderer>();
                try
                {
                    var fixtures = new List<AssemblyUvFixture>();
                    var kept = new List<int[]>();
                    bool trim = sample % 2 != 0;
                    int sourceCount = random.Next(1, 5);
                    for (int source = 0; source < sourceCount; source++)
                    {
                        var part = AssemblyPart("Input " + source, new[] { bone }, Vector3.zero); parts.Add(part);
                        int[] dimensions = Enumerable.Range(0, 8).Select(_ => choices[random.Next(choices.Length)]).ToArray();
                        if (sample % 16 == 0) Array.Clear(dimensions, 0, dimensions.Length);
                        var fixture = new AssemblyUvFixture(dimensions, source); fixtures.Add(fixture); fixture.Apply(part.sharedMesh);
                        bool sparse = random.Next(2) == 0;
                        if (sparse) part.sharedMesh.triangles = new[] { 0, 2, 2 };
                        kept.Add(trim && sparse ? new[] { 0, 2 } : new[] { 0, 1, 2 });
                    }
                    string replay = "seed=" + seed + ", sample=" + sample + ", trim=" + trim + ", layouts=" +
                        string.Join("/", fixtures.Select(f => string.Join(",", f.Dimensions))) + ", vertices=" +
                        string.Join("/", kept.Select(v => string.Join(",", v)));
                    using var assembly = SkinnedMeshAssembler.Create(parts, false, removeUnusedData: trim);
                    for (int channel = 0; channel < 8; channel++)
                    {
                        int expectedDimension = fixtures.Any(f => f.Dimensions[channel] != 0) ? fixtures.Max(f =>
                            f.Source(channel) < 0 ? 4 : f.Dimensions[f.Source(channel)]) : 0;
                        Assert.That(assembly.Mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0 + channel),
                            Is.EqualTo(expectedDimension), replay + ", channel=" + channel + " layout");
                        var expected = fixtures.SelectMany((f, source) => kept[source].Select(v => f.ShaderInput(channel, v))).ToArray();
                        Assert.That(AssemblyUvShaderInputs(assembly.Mesh, channel), Is.EqualTo(expected),
                            replay + ", channel=" + channel + " values");
                    }
                }
                finally
                {
                    foreach (var part in parts)
                    {
                        UnityEngine.Object.DestroyImmediate(part.sharedMesh);
                        foreach (var material in part.sharedMaterials) UnityEngine.Object.DestroyImmediate(material);
                        UnityEngine.Object.DestroyImmediate(part.gameObject);
                    }
                }
            }
        }

        [TestCase(3, true)] [TestCase(4, true)]
        [TestCase(2, false)] [TestCase(3, false)] [TestCase(0, true)]
        public void MeshAssemblyPreservesNativeUvAliasesAndComponentDefaults(int dimension, bool alias)
            => AssertNativeAssemblyUvs(dimension, alias, alias && dimension > 0 ? 2 : 4, false);

        [TestCase(2, false)] [TestCase(3, false)]
        [TestCase(2, true)] [TestCase(3, true)] [TestCase(4, true)]
        public void MeshAssemblyPreservesNativeMissingUvDefaults(int otherDimension, bool laterChannel)
            => AssertNativeAssemblyUvs(0, true, otherDimension, laterChannel);

        private void AssertNativeAssemblyUvs(int dimension, bool alias, int otherDimension, bool laterChannel)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                Assert.Ignore("This comparison requires a graphics device.");
            var bone = PolicyChild("Bone").transform;
            var first = AssemblyPart("First", new[] { bone }, new Vector3(-1.1f, 0, 0));
            var second = AssemblyPart("Second", new[] { bone }, new Vector3(0.1f, 0, 0));
            first.lightProbeUsage = second.lightProbeUsage = LightProbeUsage.Off;
            first.reflectionProbeUsage = second.reflectionProbeUsage = ReflectionProbeUsage.Off;
            new AssemblyUvFixture(new[] { dimension, alias ? 0 : dimension, 0, laterChannel ? 4 : 0, 0, 0, 0, 0 }).Apply(first.sharedMesh);
            new AssemblyUvFixture(new[] { 2, otherDimension, 0, 0, 0, 0, 0, 0 }, 1).Apply(second.sharedMesh);
            first.sharedMaterial.SetFloat("_UvProbe", 1); second.sharedMaterials = first.sharedMaterials;
            var candidates = MergeCandidates(new[] { first, second });
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(1),
                "This fixture must exercise inputs accepted by the lossless merge policy.");
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
                Assert.That(before.Any(p => p.b > 20), Is.True, "The fixture must actually render visible UV probes.");
                int difference = before.Zip(after, (a, b) => Math.Max(Math.Max(Math.Abs(a.r - b.r), Math.Abs(a.g - b.g)), Math.Abs(a.b - b.b))).Max();
                int pixel = Enumerable.Range(0, before.Length).First(i => Math.Max(Math.Max(Math.Abs(before[i].r - after[i].r),
                    Math.Abs(before[i].g - after[i].g)), Math.Abs(before[i].b - after[i].b)) == difference);
                Assert.That(difference, Is.LessThanOrEqualTo(1), "Native TEXCOORD1 changed: dimension=" + dimension + ", alias=" + alias +
                    ", otherDimension=" + otherDimension + ", laterChannel=" + laterChannel +
                    "; pixel=" + pixel + "; source RGB=" + before[pixel] + "; merged RGB=" + after[pixel] + " (RGB encodes UV.x, UV.z, UV.w)");
            }
            finally { RenderTexture.ReleaseTemporary(target); }
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
