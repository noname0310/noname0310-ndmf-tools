using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.MaterialAtlas;
using Noname.AvatarTools.Materials;
using Noname.AvatarTools.MeshReduction;
using Noname.AvatarTools.Occlusion;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterLayout;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void InputPreparationDefersUnusedShaderOutputsUntilConversionFinishes()
        {
            Mesh(); renderer.sharedMaterial = OutputMaterial();
            var clip = Track(new AnimationClip()); var binding = MaterialBinding("_RemovedLightingControl.x");
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Linear(0, 0, 1, 1));
            MaterialDescriptor(Controller(clip)); root.AddComponent<ALTOTraceAndOptimizeAnimator>();
            var context = new BuildContext(root, null);
            using var registry = new ObjectRegistryScope(context.ObjectRegistry);
            AnimatorOptimizationPass.Capture(context);
            bool HasBinding()
            {
                using var lease = OptimizationSession.Acquire(context);
                return lease.Services.ControllerContext.Controllers.Values.SelectMany(c => c.AllReachableNodes()).OfType<VirtualClip>()
                    .Any(c => c.GetFloatCurve(binding) != null);
            }
            OptimizationFixedPoint.PrepareInputs(context);
            Assert.That(HasBinding(), Is.True, "The reference shader can introduce a previously unused property.");
            OptimizationFixedPoint.PrepareInputs(context, inspectMaterials: true);
            Assert.That(HasBinding(), Is.False, "Once conversion finishes the unused output can be removed.");
            Assert.That(AnimationUtility.GetEditorCurve(clip, binding), Is.Not.Null);
        }

        [Test]
        public void TransportPreparationSettlesLogicalOutputsButRetainsGeneratorReaders()
        {
            Mesh("Face");
            var hidden = PolicyChild("Late hidden renderer"); hidden.SetActive(false);
            hidden.AddComponent<SkinnedMeshRenderer>().sharedMesh = renderer.sharedMesh;
            var needed = PolicyChild("Generator input");
            var metadata = PolicyChild("Generator").AddComponent<DependencyAuthoringFixture>(); metadata.Target = needed.transform;
            ObjectSettings().Component.RemoveUnusedObjects = true;
            var clip = Track(new AnimationClip());
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(hidden.name, typeof(SkinnedMeshRenderer), "blendShape.Face"),
                AnimationCurve.Linear(0, 0, 1, 100));
            var source = TreeController(("DeadInput", clip));
            var context = ExpressionBuild(null, source); ObjectOptimizationPass.Capture(context);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            AvatarOutputSdkFinalizer.PrepareTransport(root, asset => Track(asset));
            Assert.That(hidden == null, Is.True, "Logical cleanup precedes any generated transport reader.");
            Assert.That(needed != null && metadata != null && metadata.Target == needed.transform, Is.True,
                "A later compressor/generator can still read its authoring inputs.");
            Assert.That(context.GetState<ObjectOptimizationState>().RuntimeDependenciesOnly, Is.False);
            var descriptor = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            var entries = new Dictionary<object, VirtualAnimatorController>();
            var clone = new CloneContext(VRChatPlatformAnimatorBindings.Instance);
            foreach (var (key, controller, _) in VRChatPlatformAnimatorBindings.Instance.GetInnateControllers(root))
                if (controller != null) entries[key] = clone.Clone(controller, key);
            Assert.That(entries.Values.SelectMany(c => c.AllReachableNodes()).OfType<VirtualClip>()
                .SelectMany(c => c.GetFloatCurveBindings()).Any(b => b.path == "Late hidden renderer"), Is.False);
            foreach (var layer in descriptor.baseAnimationLayers)
                if (layer.animatorController is UnityEditor.Animations.AnimatorController controller) TrackControllerAssets(controller);
            AvatarOutputSdkFinalizer.Complete(root, asset => Track(asset));
        }

        [TestCase(false)] [TestCase(true)]
        public void PhysBoneBudgetFailsOnlyAfterFinalConsumersSettle(bool survives)
        {
            var bone = PhysBone("Temporary secondary motion");
            var context = new BuildContext(root, null);
            var state = context.GetState<PhysBoneFilterState>();
            state.Applied = true;
            state.Budget = new PhysBoneBudget { Components = 0, OnExceeded = BudgetExceededAction.Error };
            Assert.DoesNotThrow(() => PhysBoneFilterPass.CheckBudget(context, final: false));
            Assert.That(context.GetState<OptimizationBuildReport>().Physics.Components, Is.GreaterThan(0));
            if (survives) Assert.Throws<InvalidOperationException>(() => PhysBoneFilterPass.CheckBudget(context));
            else
            {
                Object.DestroyImmediate(bone);
                Assert.DoesNotThrow(() => PhysBoneFilterPass.CheckBudget(context));
                Assert.That(context.GetState<OptimizationBuildReport>().Physics.Components, Is.Zero);
            }
        }

        private Mesh ReductionQuad()
        {
            var mesh = Track(new Mesh { name = "Reduction source" });
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.one, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            return mesh;
        }

        [TestCase("RemovedRenderer")] [TestCase("RemovedTriangles")] [TestCase("PreservedFace")]
        public void ReductionBudgetsCurrentGeometryOnceAfterSdkGenerators(string change)
        {
            MaterialDescriptor(); renderer.sharedMesh = ReductionQuad();
            var cloth = PolicyChild("Cloth").AddComponent<SkinnedMeshRenderer>(); cloth.sharedMesh = ReductionQuad();
            var faceSource = renderer.sharedMesh;
            var settings = root.AddComponent<ALTOMeshReduction>(); settings.ApplyOnPlatforms = OptimizationPlatforms.All;
            settings.TriangleBudget = change == "RemovedRenderer" ? 2 : 3;
            settings.PreserveRenderers.Add(renderer);
            settings.OnBudgetUnreachable = BudgetExceededAction.Error;
            var context = new BuildContext(root, null);
            using var registry = new ObjectRegistryScope(context.ObjectRegistry);
            int calls = 0;
            var simplify = MeshReductionBackend.Simplify; var validate = MeshReductionBackend.Validate;
            try
            {
                MeshReductionBackend.Validate = (_, _) => null;
                MeshReductionBackend.Simplify = (requests, _) =>
                {
                    calls++;
                    foreach (var request in requests)
                    {
                        Assert.That(request.Renderer, Is.SameAs(cloth), "Preserved face boundaries survive deferred assembly.");
                        request.Result = Object.Instantiate(request.Source);
                        request.Result.triangles = request.Source.triangles.Take(request.Target * 3).ToArray();
                    }
                };
                AvatarOutputSdkFinalizer.Begin(root);
                MeshReductionPass.Capture(context); MeshReductionPass.Prepare(context); MeshReductionPass.Apply(context);
                Assert.That(calls, Is.Zero, "NDMF only captures the reduction recipe for SDK builds.");
                Assert.That(settings == null, Is.True, "The recipe cannot depend on a surviving authoring component.");
                Assert.That(context.GetState<MeshReductionState>().Pending, Is.True);
                if (change == "RemovedRenderer") Object.DestroyImmediate(cloth);
                if (change == "RemovedTriangles") cloth.sharedMesh.triangles = new[] { 0, 1, 2 };
                MeshReductionPass.ApplyFinal(context, new Dictionary<object, VirtualAnimatorController>(), null, asset => Track(asset));
                var result = context.GetState<OptimizationBuildReport>().MeshReduction;
                Assert.That(result.OutputTriangles, Is.EqualTo(change == "RemovedRenderer" ? 2 : 3));
                Assert.That(calls, Is.EqualTo(change == "PreservedFace" ? 1 : 0));
                Assert.That(renderer.sharedMesh, Is.SameAs(faceSource));
                Assert.That(context.GetState<MeshReductionState>().Pending, Is.False);
                MeshReductionPass.ApplyFinal(context, new Dictionary<object, VirtualAnimatorController>(), null, asset => Track(asset));
                Assert.That(calls, Is.EqualTo(change == "PreservedFace" ? 1 : 0), "A finalized recipe never simplifies its own result.");
            }
            finally
            {
                MeshReductionBackend.Simplify = simplify; MeshReductionBackend.Validate = validate;
                AvatarOutputSdkFinalizer.Complete(root, asset => Track(asset));
            }
        }

        [TestCase("Enabled", false)] [TestCase("Enabled", true)]
        [TestCase("Mesh", false)] [TestCase("Mesh", true)]
        [TestCase("MaterialSlot", false)] [TestCase("MaterialSlot", true)]
        [TestCase("Active", false)] [TestCase("Active", true)]
        public void OcclusionDistinguishesInvariantWritersFromChangedSamplingInputs(string property, bool changes)
        {
            renderer.sharedMesh = Mesh(); renderer.sharedMaterial = Track(new Material(Shader.Find("Standard")));
            var clip = Track(new AnimationClip());
            string name = property == "Enabled" ? "m_Enabled" : property == "Active" ? "m_IsActive" :
                property == "Mesh" ? "m_Mesh" : "m_Materials.Array.data[0]";
            Object target = property == "Active" ? root : renderer;
            if (property == "Mesh" || property == "MaterialSlot")
                AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), name),
                    new[] { new ObjectReferenceKeyframe { value = changes ? null : property == "Mesh" ? (Object)renderer.sharedMesh : renderer.sharedMaterial } });
            else AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", property == "Active" ? typeof(GameObject) : typeof(SkinnedMeshRenderer), name),
                AnimationCurve.Constant(0, 1, changes ? 0 : 1));
            var index = AtlasIndex(clip, out _);
            Assert.That(new InvariantRenderInputs(index).Unchanged(target, name), Is.EqualTo(!changes));
            if (property == "Mesh") Assert.That(OcclusionEvaluation.MeshCanChange(index, renderer), Is.EqualTo(changes));
        }

        [TestCase(false)] [TestCase(true)]
        public void DeferredTextureBakingRetainsLateOutsideOrUnresolvedSwapConsumers(bool unresolved)
        {
            Mesh();
            var mesh = renderer.sharedMesh; Object.DestroyImmediate(renderer);
            renderer = PolicyChild("Inside").AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
            var source = Track(new Material(Shader.Find("Standard")));
            var texture = Track(new Texture2D(4, 4)); source.SetTexture("_MainTex", texture); renderer.sharedMaterial = source;
            MaterialDescriptor(Controller(Clip()));
            var optimizer = root.AddComponent<ALTOTraceAndOptimizeMaterials>();
            optimizer.BakeLilToonMainTextures = true; optimizer.ScopeRoot = renderer.transform;
            var context = new BuildContext(root, null); MaterialOptimizationPass.Capture(context);
            var atlas = EarlyAtlas(context, AtlasGroup("Standard"), renderer.transform);
            using var registry = new ObjectRegistryScope(context.ObjectRegistry);
            var backend = MaterialTextureBake.LilToon; int calls = 0;
            try
            {
                MaterialTextureBake.LilToon = (_, _) => { calls++; return null; };
                atlas.Early = EarlyMaterialConversion.Apply(context, atlas);
                Assert.That(calls, Is.Zero);
                var output = renderer.sharedMaterial;
                if (!unresolved) PolicyChild("Outside").AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
                var clip = Track(new AnimationClip());
                AnimationUtility.SetObjectReferenceCurve(clip,
                    EditorCurveBinding.PPtrCurve(unresolved ? "Missing" : "Outside", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = output } });
                var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
                var index = AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController> { [0] = controller }, null);
                atlas.Early.BakeInputs(context, new[] { renderer }, index);
                Assert.That(calls, Is.Zero, "A late swap consumer must satisfy the same scope proof as an assigned material.");
                Assert.That(output.GetTexture("_MainTex"), Is.SameAs(texture));
            }
            finally { MaterialTextureBake.LilToon = backend; TrackEarlyAtlasAssets(); }
        }

        [TestCase("Dead")] [TestCase("GeneratorTexture")] [TestCase("GeneratorUv")]
        [TestCase("Live")] [TestCase("UnrelatedWriter")] [TestCase("GeneratorReader")]
        public void DeferredTextureBakingUsesLiveUnmodifiedPerMaterialRecipes(string change)
        {
            Mesh();
            var source = Track(new Material(Shader.Find("Standard")));
            var originalTexture = Track(new Texture2D(4, 4)); source.SetTexture("_MainTex", originalTexture);
            renderer.sharedMaterial = source;
            var other = PolicyChild("Other").AddComponent<SkinnedMeshRenderer>(); other.sharedMesh = renderer.sharedMesh;
            var otherMaterial = Track(new Material(source)); other.sharedMaterial = otherMaterial;
            var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Other", typeof(SkinnedMeshRenderer), "material._Color.r"),
                AnimationCurve.Linear(0, 0, 1, 1));
            MaterialDescriptor(Controller(clip));
            var optimizer = root.AddComponent<ALTOTraceAndOptimizeMaterials>(); optimizer.BakeLilToonMainTextures = true;
            var context = new BuildContext(root, null); MaterialOptimizationPass.Capture(context);
            var atlas = EarlyAtlas(context, AtlasGroup("Standard"));
            if (change == "GeneratorReader") root.AddComponent<DependencyAuthoringFixture>().Target = renderer;
            using var registry = new ObjectRegistryScope(context.ObjectRegistry);
            var backend = MaterialTextureBake.LilToon;
            int calls = 0; HashSet<string> blocked = null;
            var outputTexture = Track(new Texture2D(4, 4));
            try
            {
                MaterialTextureBake.LilToon = (input, properties) =>
                {
                    if (input == source) { calls++; blocked = new(properties); }
                    var material = new Material(input); material.SetTexture("_MainTex", outputTexture);
                    return new MaterialTextureBakeResult { Material = material, Texture = outputTexture, ReplacedTextureProperty = "_MainTex" };
                };
                atlas.Early = EarlyMaterialConversion.Apply(context, atlas);
                Assert.That(calls, Is.EqualTo(change == "GeneratorReader" ? 1 : 0),
                    "Only a generator reader requires the image before late input planning.");
                var output = renderer.sharedMaterial;
                if (change == "GeneratorTexture") output.SetTexture("_MainTex", Track(new Texture2D(4, 4)));
                if (change == "GeneratorUv") output.SetTextureScale("_MainTex", new Vector2(2, 3));
                atlas.Early.BakeInputs(context, change == "Dead" ? Array.Empty<Renderer>() : new[] { renderer });
                bool expected = change == "Live" || change == "UnrelatedWriter" || change == "GeneratorReader";
                Assert.That(calls, Is.EqualTo(expected ? 1 : 0));
                if (expected)
                {
                    Assert.That(blocked, Does.Not.Contain("_Color"), "Another renderer's writer is not a writer of this source.");
                    Assert.That(output.GetTexture("_MainTex"), Is.SameAs(outputTexture));
                }
                Assert.That(source.GetTexture("_MainTex"), Is.SameAs(originalTexture));
                atlas.Early.BakeInputs(context, new[] { renderer });
                Assert.That(calls, Is.EqualTo(change == "Dead" ? 1 : expected ? 1 : 0));
            }
            finally { MaterialTextureBake.LilToon = backend; TrackEarlyAtlasAssets(); }
        }
    }
}
