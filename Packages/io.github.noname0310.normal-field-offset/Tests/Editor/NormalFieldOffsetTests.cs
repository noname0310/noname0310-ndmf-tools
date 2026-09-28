using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.preview;
using Noname.AvatarTools.Editor;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Tests
{
    public sealed class NormalFieldOffsetTests
    {
        private Scene scene;
        private GameObject root;
        private readonly List<Mesh> ownedMeshes = new List<Mesh>();
        private SkinWeights originalQuality;

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("NormalFieldOffsetTests");
            SceneManager.MoveGameObjectToScene(root, scene);
            originalQuality = QualitySettings.skinWeights;
        }

        [TearDown]
        public void TearDown()
        {
            QualitySettings.skinWeights = originalQuality;
            EditorSceneManager.ClosePreviewScene(scene);
            foreach (var mesh in ownedMeshes) if (mesh != null) Object.DestroyImmediate(mesh);
            ownedMeshes.Clear();
        }

        private SkinnedMeshRenderer Renderer(string name, Vector3[] vertices, Vector3[] normals = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var bone = new GameObject(name + " Bone").transform;
            bone.SetParent(root.transform, false);
            var mesh = new Mesh { name = name + " Mesh" };
            ownedMeshes.Add(mesh);
            mesh.vertices = vertices;
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.normals = normals ?? Enumerable.Repeat(Vector3.forward, vertices.Length).ToArray();
            mesh.bindposes = new[] { bone.worldToLocalMatrix * go.transform.localToWorldMatrix };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, vertices.Length).ToArray();
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { bone };
            renderer.rootBone = bone;
            renderer.localBounds = mesh.bounds;
            renderer.quality = SkinQuality.Bone4;
            return renderer;
        }

        private SkinnedMeshRenderer Body() => Renderer("Body", new[] { new Vector3(-10, -10, 0), new Vector3(10, -10, 0), new Vector3(0, 10, 0) });
        private SkinnedMeshRenderer Clothing(float z = -0.01f) => Renderer("Clothing", new[] { new Vector3(-0.1f, -0.1f, z), new Vector3(0.1f, -0.1f, z), new Vector3(0, 0.1f, z) });

        private static Vector3[] Bake(SkinnedMeshRenderer renderer)
        {
            var mesh = new Mesh();
            try
            {
                renderer.BakeMesh(mesh, true);
                return mesh.vertices.Select(renderer.transform.TransformPoint).ToArray();
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        private OffsetResult Generate(SkinnedMeshRenderer clothing, SkinnedMeshRenderer body, float offset, float maxDistance = 0)
        {
            var result = NormalFieldProcessor.Generate(new[] { new OffsetRequest(clothing, body, offset, maxDistance) }).Single();
            ownedMeshes.Add(result.Mesh);
            return result;
        }

        private static void AssertVector(Vector3 expected, Vector3 actual, float tolerance = 0.00001f)
            => Assert.LessOrEqual(Vector3.Distance(expected, actual), tolerance, $"Expected {expected:G9}, actual {actual:G9}");

        [Test]
        public void FieldUsesClosestTriangleAndInterpolatedNormalsEvenInsideBody()
        {
            var points = new[] { Vector3.zero, new Vector3(4, 0, 0), new Vector3(0, 4, 0) };
            var normals = new[] { Vector3.forward, new Vector3(1, 0, 1).normalized, new Vector3(0, 1, 1).normalized };
            var field = new SurfaceNormalField(points, normals, new[] { 0, 1, 2 });
            Assert.IsTrue(field.Sample(new Vector3(1, 1, -0.1f), 0, out var normal, out var distance));
            AssertVector((normals[0] * 0.5f + normals[1] * 0.25f + normals[2] * 0.25f).normalized, normal);
            Assert.AreEqual(0.1f, distance, 0.00001f);
            Assert.Greater(normal.z, 0); // An already intersecting vertex must still move outwards.
            Assert.IsFalse(field.Sample(new Vector3(1, 1, 1), 0.5f, out _, out _));
        }

        [Test]
        public void BvhFindsNearestSurfaceAcrossBranchesAndIgnoresDegenerateFaces()
        {
            var points = new List<Vector3>();
            var normals = new List<Vector3>();
            var indices = new List<int>();
            for (int i = 0; i < 100; i++)
            {
                points.AddRange(new[] { new Vector3(i * 3, 0, 0), new Vector3(i * 3 + 1, 0, 0), new Vector3(i * 3, 1, 0) });
                normals.AddRange(Enumerable.Repeat(Vector3.forward, 3));
                indices.AddRange(new[] { i * 3, i * 3 + 1, i * 3 + 2 });
            }
            indices.AddRange(new[] { 0, 0, 0 });
            var field = new SurfaceNormalField(points.ToArray(), normals.ToArray(), indices.ToArray());
            for (int i = 0; i < 100; i++)
            {
                Assert.IsTrue(field.Sample(new Vector3(i * 3 + 0.25f, 0.25f, 0.3f), 0, out var normal, out var distance));
                AssertVector(Vector3.forward, normal);
                Assert.AreEqual(0.3f, distance, 0.00001f);
            }
            Assert.Throws<InvalidOperationException>(() => new SurfaceNormalField(points.ToArray(), normals.ToArray(), new[] { 0, 0, 0 }));
        }

        [TestCase(0.003f)]
        [TestCase(-0.004f)]
        public void SignedOffsetPreservesMeshDataAndBlendshapeFrames(float offset)
        {
            var body = Body();
            var clothing = Clothing();
            var source = clothing.sharedMesh;
            source.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            source.uv2 = source.uv;
            source.colors = new[] { Color.red, Color.green, Color.blue };
            source.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), 3).ToArray();
            source.subMeshCount = 2;
            source.SetTriangles(new[] { 0, 2, 1 }, 1);
            var delta = Enumerable.Repeat(new Vector3(0.01f, 0, 0.005f), 3).ToArray();
            var deltaNormal = Enumerable.Repeat(new Vector3(0.1f, 0, 0), 3).ToArray();
            source.AddBlendShapeFrame("Fit", 50, delta, deltaNormal, deltaNormal);
            source.AddBlendShapeFrame("Fit", 100, delta.Select(v => v * 2).ToArray(), deltaNormal, deltaNormal);
            clothing.SetBlendShapeWeight(0, 37);
            var original = source.vertices;
            var before = Bake(clothing);
            var result = Generate(clothing, body, offset);
            Assert.AreSame(source, clothing.sharedMesh);
            CollectionAssert.AreEqual(original, source.vertices);
            result.Apply();
            var after = Bake(clothing);
            for (int i = 0; i < before.Length; i++) AssertVector(before[i] + Vector3.forward * offset, after[i]);
            Assert.AreEqual(37, clothing.GetBlendShapeWeight(0));
            Assert.AreEqual(2, result.Mesh.subMeshCount);
            CollectionAssert.AreEqual(source.GetTriangles(1), result.Mesh.GetTriangles(1));
            CollectionAssert.AreEqual(source.normals, result.Mesh.normals);
            CollectionAssert.AreEqual(source.tangents, result.Mesh.tangents);
            CollectionAssert.AreEqual(source.uv2, result.Mesh.uv2);
            CollectionAssert.AreEqual(source.colors, result.Mesh.colors);
            CollectionAssert.AreEqual(source.bindposes, result.Mesh.bindposes);
            CollectionAssert.AreEqual(source.boneWeights, result.Mesh.boneWeights);
            Assert.AreEqual("Fit", result.Mesh.GetBlendShapeName(0));
            Assert.AreEqual(2, result.Mesh.GetBlendShapeFrameCount(0));
            var dv = new Vector3[3]; var dn = new Vector3[3]; var dt = new Vector3[3];
            result.Mesh.GetBlendShapeFrameVertices(0, 0, dv, dn, dt);
            CollectionAssert.AreEqual(delta, dv);
            CollectionAssert.AreEqual(deltaNormal, dn);
            CollectionAssert.AreEqual(deltaNormal, dt);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(4)]
        [TestCase(255)]
        public void WorldDistanceSurvivesPosedBonesDifferentRendererTransformsAndSkinQuality(int quality)
        {
            var body = Body();
            var clothing = Clothing();
            var bones = new Transform[5];
            for (int i = 0; i < bones.Length; i++)
            {
                bones[i] = new GameObject("Weighted Bone " + i).transform;
                bones[i].SetParent(root.transform, false);
                bones[i].localRotation = Quaternion.Euler(i * 9, i * 11, i * 4);
                bones[i].localScale = new Vector3(0.8f + i * 0.1f, 1.2f, 0.7f + i * 0.15f);
            }
            clothing.bones = bones;
            // Start with a fresh layout: Unity 2022 cannot convert a legacy boneWeights vertex stream
            // to the variable-count SetBoneWeights layout without logging a conversion error.
            var weightedMesh = new Mesh { vertices = clothing.sharedMesh.vertices, normals = clothing.sharedMesh.normals };
            weightedMesh.triangles = new[] { 0, 1, 2 };
            weightedMesh.bindposes = Enumerable.Repeat(Matrix4x4.identity, 5).ToArray();
            ownedMeshes.Add(weightedMesh);
            clothing.sharedMesh = weightedMesh;
            var influences = new[] { 0.35f, 0.25f, 0.2f, 0.15f, 0.05f };
            using (var counts = new NativeArray<byte>(new byte[] { 5, 5, 5 }, Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(Enumerable.Range(0, 15).Select(i => new BoneWeight1 { boneIndex = i % 5, weight = influences[i % 5] }).ToArray(), Allocator.Temp))
                clothing.sharedMesh.SetBoneWeights(counts, weights);
            QualitySettings.skinWeights = (SkinWeights)quality;
            clothing.quality = SkinQuality.Auto;
            root.transform.SetPositionAndRotation(new Vector3(3, -2, 1), Quaternion.Euler(15, 30, 10));
            root.transform.localScale = new Vector3(2, 0.7f, 1.5f);
            clothing.transform.localRotation = Quaternion.Euler(20, 15, 0);
            clothing.transform.localScale = new Vector3(100, 50, 75);
            var before = Bake(clothing);
            var result = Generate(clothing, body, 0.003f);
            result.Apply();
            var after = Bake(clothing);
            var normal = root.transform.worldToLocalMatrix.transpose.MultiplyVector(Vector3.forward).normalized;
            for (int i = 0; i < before.Length; i++) AssertVector(normal * 0.003f, after[i] - before[i], 0.00002f);
            using var outputWeights = result.Mesh.GetAllBoneWeights();
            Assert.AreEqual(15, outputWeights.Length);
        }

        [Test]
        public void BodyBlendshapeNormalsAreUsedWithoutChangingBody()
        {
            var body = Body();
            var clothing = Clothing();
            var deltaNormal = Enumerable.Repeat(new Vector3(0.6f, 0, -0.2f), 3).ToArray();
            body.sharedMesh.AddBlendShapeFrame("Surface", 100, new Vector3[3], deltaNormal, new Vector3[3]);
            body.SetBlendShapeWeight(0, 50);
            var source = body.sharedMesh;
            var result = Generate(clothing, body, 0.004f);
            AssertVector(new Vector3(0.3f, 0, 0.9f).normalized * 0.004f, result.Mesh.vertices[0] - clothing.sharedMesh.vertices[0]);
            Assert.AreSame(source, body.sharedMesh);
            Assert.AreEqual(50, body.GetBlendShapeWeight(0));
        }

        [Test]
        public void DistanceLimitFadesWithoutMovingFarVertices()
        {
            var body = Body();
            var clothing = Clothing(0.09f);
            var result = Generate(clothing, body, 0.01f, 0.1f);
            AssertVector(Vector3.forward * 0.005f, result.Mesh.vertices[0] - clothing.sharedMesh.vertices[0]);
            var far = Clothing(0.11f);
            var unchanged = Generate(far, body, 0.01f, 0.1f);
            Assert.AreEqual(0, unchanged.MovedVertices);
            CollectionAssert.AreEqual(far.sharedMesh.vertices, unchanged.Mesh.vertices);
        }

        [Test]
        public void SharedClothingAssetGetsIndependentOffsetsAndZeroIsANoOp()
        {
            var body = Body();
            var inner = Clothing();
            var outer = Clothing();
            outer.sharedMesh = inner.sharedMesh;
            var source = inner.sharedMesh;
            var results = NormalFieldProcessor.Generate(new[] { new OffsetRequest(inner, body, 0.002f, 0), new OffsetRequest(outer, body, 0.006f, 0) });
            ownedMeshes.AddRange(results.Select(r => r.Mesh));
            Assert.AreNotSame(results[0].Mesh, results[1].Mesh);
            AssertVector(Vector3.forward * 0.004f, results[1].Mesh.vertices[0] - results[0].Mesh.vertices[0]);
            Assert.AreSame(source, inner.sharedMesh);
            Assert.AreSame(source, outer.sharedMesh);
            Assert.IsEmpty(NormalFieldProcessor.Generate(new[] { new OffsetRequest(inner, body, 0, 0) }));
        }

        [Test]
        public void BuildAppliesToInactiveOutfitsSkipsDisabledComponentsAndStripsConfiguration()
        {
            var body = Body();
            var clothing = Clothing();
            var component = clothing.gameObject.AddComponent<NormalFieldOffset>();
            component.TargetBody = body;
            clothing.gameObject.SetActive(false);
            var disabled = Clothing();
            disabled.gameObject.AddComponent<NormalFieldOffset>().enabled = false;
            var source = clothing.sharedMesh;
            var disabledSource = disabled.sharedMesh;
            var context = new BuildContext(root, null);
            NormalFieldOffsetPlugin.Apply(context);
            ownedMeshes.Add(clothing.sharedMesh);
            Assert.AreNotSame(source, clothing.sharedMesh);
            AssertVector(Vector3.forward * 0.002f, clothing.sharedMesh.vertices[0] - source.vertices[0]);
            Assert.AreSame(disabledSource, disabled.sharedMesh);
            Assert.IsEmpty(root.GetComponentsInChildren<NormalFieldOffset>(true));
            Assert.IsFalse(clothing.gameObject.activeSelf);
        }

        [Test]
        public void PreviewChangesOnlyProxyMeshAndMatchesBuildProcessor()
        {
            var body = Body();
            var clothing = Clothing();
            var component = clothing.gameObject.AddComponent<NormalFieldOffset>();
            component.TargetBody = body;
            var source = clothing.sharedMesh;
            var bodyProxy = Renderer("Body Proxy", body.sharedMesh.vertices);
            bodyProxy.sharedMesh = body.sharedMesh;
            var clothingProxy = Clothing();
            clothingProxy.sharedMesh = clothing.sharedMesh;
            var expected = Generate(clothing, body, component.Offset);
            var filter = new NormalFieldOffsetPreview();
            var group = RenderGroup.For(new Renderer[] { body, clothing }).WithData(new[] { component }, (a, b) => a.SequenceEqual(b));
            var pairs = new (Renderer, Renderer)[] { (body, bodyProxy), (clothing, clothingProxy) };
            var node = filter.Instantiate(group, pairs, new ComputeContext("NormalFieldOffset test")).GetAwaiter().GetResult();
            try
            {
                node.OnFrame(clothing, clothingProxy);
                Assert.AreSame(source, clothing.sharedMesh);
                Assert.AreSame(body.sharedMesh, bodyProxy.sharedMesh);
                CollectionAssert.AreEqual(expected.Mesh.vertices, clothingProxy.sharedMesh.vertices);
                Assert.IsTrue(filter.StrictRenderGroup);
                Assert.IsNull(node.Refresh(pairs, new ComputeContext("refresh test"), RenderAspects.Shapes).GetAwaiter().GetResult());
            }
            finally { node.Dispose(); }
        }

        [Test]
        public void InvalidConfigurationAndSingularSkinningFailClearly()
        {
            var body = Body();
            var clothing = Clothing();
            Assert.IsNotNull(NormalFieldProcessor.Validate(new OffsetRequest(clothing, null, 0.002f, 0)));
            Assert.IsNotNull(NormalFieldProcessor.Validate(new OffsetRequest(clothing, clothing, 0.002f, 0)));
            Assert.IsNotNull(NormalFieldProcessor.Validate(new OffsetRequest(clothing, body, float.NaN, 0)));
            clothing.bones[0].localScale = new Vector3(1, 1, 0);
            Assert.Throws<InvalidOperationException>(() => Generate(clothing, body, 0.002f));
        }
    }
}
