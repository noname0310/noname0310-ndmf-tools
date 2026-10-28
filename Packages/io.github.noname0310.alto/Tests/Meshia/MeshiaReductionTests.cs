using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.MeshReduction.Tests
{
    public sealed class MeshiaReductionTests
    {
        private Scene scene;
        private GameObject root;
        private readonly List<Object> assets = new();
        private T Track<T>(T item) where T : Object { assets.Add(item); return item; }

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene(); root = Track(new GameObject("Reduction test"));
            SceneManager.MoveGameObjectToScene(root, scene); root.AddComponent<VRCAvatarDescriptor>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var asset in assets.AsEnumerable().Reverse()) if (asset != null) Object.DestroyImmediate(asset);
            assets.Clear(); EditorSceneManager.ClosePreviewScene(scene);
        }

        private SkinnedMeshRenderer Grid(string name, bool morph = false, int n = 20)
        {
            var child = new GameObject(name); child.transform.SetParent(root.transform, false);
            var renderer = child.AddComponent<SkinnedMeshRenderer>();
            var mesh = Track(new Mesh { name = name });
            var vertices = new Vector3[(n + 1) * (n + 1)]; var weights = new BoneWeight[vertices.Length];
            for (int y = 0; y <= n; y++) for (int x = 0; x <= n; x++)
            {
                int i = y * (n + 1) + x; vertices[i] = new Vector3(x / (float)n, y / (float)n, 0);
                weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1 - vertices[i].x, boneIndex1 = 1, weight1 = vertices[i].x };
            }
            var triangles = new List<int>();
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            { int i = y * (n + 1) + x; triangles.AddRange(new[] { i, i + 1, i + n + 1, i + 1, i + n + 2, i + n + 1 }); }
            mesh.vertices = vertices; mesh.uv = vertices.Select(v => new Vector2(v.x, v.y)).ToArray();
            mesh.triangles = triangles.ToArray(); mesh.boneWeights = weights; mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            if (morph)
            {
                var deltas = vertices.Select(v => new Vector3(0, 0, 0.1f + v.x * 0.1f)).ToArray();
                mesh.AddBlendShapeFrame("Morph", 50, deltas.Select(v => v * 0.5f).ToArray(), null, null);
                mesh.AddBlendShapeFrame("Morph", 100, deltas, null, null);
            }
            var boneA = new GameObject(name + " bone A"); boneA.transform.SetParent(root.transform, false);
            var boneB = new GameObject(name + " bone B"); boneB.transform.SetParent(root.transform, false);
            renderer.sharedMesh = mesh; renderer.bones = new[] { boneA.transform, boneB.transform }; renderer.rootBone = root.transform;
            renderer.sharedMaterial = Track(new Material(Shader.Find("Unlit/Texture")));
            return renderer;
        }

        private ReductionRequest Request(SkinnedMeshRenderer renderer, int target = 200, int minimum = 1) => new()
        {
            Renderer = renderer, Source = renderer.sharedMesh, Count = (int)MeshReductionPass.Triangles(renderer.sharedMesh),
            Target = target, Minimum = minimum, Floor = minimum, Weight = 1,
            Report = new MeshReductionEntryReport { Before = MeshReductionPass.Triangles(renderer.sharedMesh), After = MeshReductionPass.Triangles(renderer.sharedMesh) }
        };

        [Test]
        public void ReductionPreservesSkinningMorphFramesUvsAndBorderPositions()
        {
            var renderer = Grid("Skin", true); var source = renderer.sharedMesh;
            var originalVertices = source.vertices; var request = Request(renderer);
            MeshiaReductionBackend.Simplify(new[] { request }, true); Track(request.Result);
            MeshReductionPass.ValidateResult(request);
            var output = request.Result;
            Assert.That(MeshReductionPass.Triangles(output), Is.EqualTo(200));
            Assert.That(output.vertexCount, Is.LessThan(source.vertexCount));
            Assert.That(output.uv.Length, Is.EqualTo(output.vertexCount));
            var delta = new Vector3[output.vertexCount]; output.GetBlendShapeFrameVertices(0, 1, delta, null, null);
            var outputVertices = output.vertices;
            for (int i = 0; i < outputVertices.Length; i++)
            {
                Assert.That((delta[i] - new Vector3(0, 0, 0.1f + outputVertices[i].x * 0.1f)).magnitude, Is.LessThan(1e-5));
                Assert.That((output.uv[i] - new Vector2(outputVertices[i].x, outputVertices[i].y)).magnitude, Is.LessThan(1e-5));
            }
            foreach (var border in originalVertices.Where(v => v.x == 0 || v.x == 1 || v.y == 0 || v.y == 1))
                Assert.That(outputVertices.Any(v => (v - border).sqrMagnitude < 1e-10), Is.True, "Border vertex " + border);
            renderer.sharedMesh = output; renderer.SetBlendShapeWeight(0, 100); renderer.bones[1].localPosition = Vector3.up * 0.2f;
            var baked = Track(new Mesh()); renderer.BakeMesh(baked);
            for (int i = 0; i < outputVertices.Length; i++)
            {
                var expected = outputVertices[i] + new Vector3(0, 0.2f * outputVertices[i].x, 0.1f + outputVertices[i].x * 0.1f);
                Assert.That((baked.vertices[i] - expected).magnitude, Is.LessThan(2e-5));
            }
            Assert.That(source.vertices, Is.EqualTo(originalVertices)); Assert.That(source.triangles.Length / 3, Is.EqualTo(800));
        }

        [Test]
        public void BatchReductionRetainsSubmeshLayoutAndSourceBuffers()
        {
            var first = Grid("First"); var second = Grid("Second");
            var source = first.sharedMesh; var triangles = source.triangles;
            source.subMeshCount = 2; source.SetTriangles(triangles.Take(1200).ToArray(), 0); source.SetTriangles(triangles.Skip(1200).ToArray(), 1);
            var requests = new[] { Request(first), Request(second) };
            MeshiaReductionBackend.Simplify(requests, true);
            foreach (var request in requests) { Track(request.Result); MeshReductionPass.ValidateResult(request); }
            Assert.That(requests[0].Result.subMeshCount, Is.EqualTo(2));
            Assert.That(requests.All(r => MeshReductionPass.Triangles(r.Result) <= 200), Is.True);
            Assert.That(source.triangles, Is.EqualTo(triangles));
        }

        [Test]
        public void ReductionBudgetsIncludeProtectedFacesWithoutChangingTheirMesh()
        {
            var face = Grid("Face", true); var clothes = Grid("Clothes"); var originalFace = face.sharedMesh;
            var settings = root.AddComponent<ALTOMeshReduction>(); settings.ApplyOnPlatforms = OptimizationPlatforms.All;
            settings.PreserveRenderers.Add(face); settings.TriangleBudget = 1000; settings.OnBudgetUnreachable = BudgetExceededAction.Error;
            var context = new BuildContext(root, null);
            using (new ObjectRegistryScope(context.ObjectRegistry))
            {
                MeshReductionPass.Capture(context); MeshReductionPass.Prepare(context); MeshReductionPass.Apply(context);
                Track(clothes.sharedMesh);
            }
            var report = context.GetState<OptimizationBuildReport>().MeshReduction;
            Assert.That(report.FixedTriangles, Is.EqualTo(800)); Assert.That(report.OutputTriangles, Is.LessThanOrEqualTo(1000));
            Assert.That(face.sharedMesh, Is.SameAs(originalFace)); Assert.That(root.GetComponent<ALTOMeshReduction>(), Is.Null);
        }

        [Test]
        public void ReductionCountsUnselectedMeshesAndRefusesImpossibleErrorBudgets()
        {
            var selected = Grid("Selected"); var other = Grid("Unselected");
            var settings = root.AddComponent<ALTOMeshReduction>(); settings.ApplyOnPlatforms = OptimizationPlatforms.All;
            settings.TargetSelection = RendererSelectionMode.SelectedRenderers;
            settings.ReductionTargets.Add(selected); settings.TriangleBudget = 10; settings.OnBudgetUnreachable = BudgetExceededAction.Error;
            var context = new BuildContext(root, null);
            MeshReductionPass.Capture(context); MeshReductionPass.Prepare(context);
            Assert.Throws<InvalidOperationException>(() => MeshReductionPass.Apply(context)); Track(selected.sharedMesh);
            var report = context.GetState<OptimizationBuildReport>().MeshReduction;
            Assert.That(report.FixedTriangles, Is.EqualTo(800)); Assert.That(report.OutputTriangles, Is.GreaterThan(10));
            Assert.That(report.BudgetReached, Is.False); Assert.That(MeshReductionPass.Triangles(other.sharedMesh), Is.EqualTo(800));
        }

        [TestCase(RendererSelectionMode.AllRenderersInScope)]
        [TestCase(RendererSelectionMode.SelectedRenderers)]
        public void EmptyReductionSelectionHasExplicitAllOrNoneSemantics(RendererSelectionMode mode)
        {
            var first = Grid("Existing"); var original = first.sharedMesh;
            var settings = root.AddComponent<ALTOMeshReduction>(); settings.ApplyOnPlatforms = OptimizationPlatforms.All;
            settings.TargetSelection = mode; settings.TriangleBudget = 1000; settings.OnBudgetUnreachable = BudgetExceededAction.Error;
            var context = new BuildContext(root, null);
            MeshReductionPass.Capture(context);
            var generated = Grid("Generated"); generated.gameObject.SetActive(false); var generatedSource = generated.sharedMesh;
            MeshReductionPass.Prepare(context);
            if (mode == RendererSelectionMode.SelectedRenderers)
            {
                Assert.Throws<InvalidOperationException>(() => MeshReductionPass.Apply(context));
                Assert.That(first.sharedMesh, Is.SameAs(original)); Assert.That(generated.sharedMesh, Is.SameAs(generatedSource));
                Assert.That(context.GetState<OptimizationBuildReport>().MeshReduction.FixedTriangles, Is.EqualTo(1600));
            }
            else
            {
                MeshReductionPass.Apply(context); Track(first.sharedMesh); Track(generated.sharedMesh);
                Assert.That(context.GetState<OptimizationBuildReport>().MeshReduction.OutputTriangles, Is.LessThanOrEqualTo(1000));
                Assert.That(context.GetState<OptimizationBuildReport>().MeshReduction.Renderers.Count, Is.EqualTo(2));
            }
        }

        [Test]
        public void RendererMinimumIsRespectedWhenMeshiaCannotHitTheExactCount()
        {
            var renderer = Grid("Minimum", true, 1); var request = Request(renderer, 1, 1);
            var report = new MeshReductionReport { Budget = 1, InputTriangles = 2 };
            MeshReductionPass.Reduce(new List<ReductionRequest> { request }, report, false);
            if (request.Result != null) Track(request.Result);
            Assert.That(report.OutputTriangles, Is.GreaterThanOrEqualTo(1));
            Assert.That(request.Result == null || MeshReductionPass.Triangles(request.Result) >= 1, Is.True);
        }

        [Test]
        public void VariableLengthSkinningIsRetainedInsteadOfSilentlyTruncated()
        {
            var renderer = Grid("Five influences", false, 1);
            // Unity 2022 cannot convert an existing legacy BoneWeight stream in-place to five influences.
            var mesh = Track(new Mesh { vertices = renderer.sharedMesh.vertices, triangles = renderer.sharedMesh.triangles });
            renderer.sharedMesh = mesh;
            mesh.bindposes = Enumerable.Repeat(Matrix4x4.identity, 5).ToArray();
            using var counts = new NativeArray<byte>(Enumerable.Repeat((byte)5, mesh.vertexCount).ToArray(), Allocator.Temp);
            using var weights = new NativeArray<BoneWeight1>(Enumerable.Range(0, mesh.vertexCount * 5).Select(i => new BoneWeight1 { boneIndex = i % 5, weight = 0.2f }).ToArray(), Allocator.Temp);
            mesh.SetBoneWeights(counts, weights);
            Assert.That(MeshiaReductionBackend.Validate(renderer, mesh), Does.Contain("four"));
        }

        [Test]
        public void InactivePolicyDoesNotRequireMeshiaOrChangeMeshes()
        {
            var renderer = Grid("Unchanged"); var source = renderer.sharedMesh;
            var settings = root.AddComponent<ALTOMeshReduction>(); settings.ApplyOnPlatforms = OptimizationPlatforms.None;
            var context = new BuildContext(root, null); MeshReductionPass.Capture(context); MeshReductionPass.Prepare(context); MeshReductionPass.Apply(context);
            Assert.That(renderer.sharedMesh, Is.SameAs(source)); Assert.That(context.GetState<OptimizationBuildReport>().MeshReduction, Is.Null);
        }
    }
}
