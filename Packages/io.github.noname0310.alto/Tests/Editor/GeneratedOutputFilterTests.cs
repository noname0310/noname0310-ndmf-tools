using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed class RenderingDependencyBehaviour : StateMachineBehaviour
    {
        public Component Target;
    }

    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("ExternalOptimizers")] [TestCase("SdkPreparation")] [TestCase("SdkFinal")]
        public void PhysBoneFilterRechecksLaterGeneratedPhysicsAndOrphanColliders(string boundary)
        {
            var filter = PhysFilter(); filter.FilterMode = PhysBoneFilterMode.KeepListed;
            var kept = PhysBone("Kept"); filter.PhysBones.Add(kept);
            var context = ExpressionBuild(null, Controller(Clip()));
            PhysBoneFilterPass.Capture(context); PhysBoneFilterPass.Apply(context);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            var late = PhysBone("Late generated"); var bone = late.transform;
            var collider = PolicyChild("Generated collider").AddComponent<VRCPhysBoneCollider>(); late.colliders.Add(collider);
            var shared = PolicyChild("Shared collider").AddComponent<VRCPhysBoneCollider>(); kept.colliders.Add(shared); late.colliders.Add(shared);
            if (boundary == "ExternalOptimizers") OptimizationPipeline.AfterExternalOptimizers(context);
            if (boundary == "SdkPreparation") AvatarOutputSdkFinalizer.PrepareTransport(root, o => Track(o));
            if (boundary == "SdkFinal") AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            Assert.That(late == null && collider == null, Is.True);
            Assert.That(kept != null && shared != null && bone != null, Is.True);
            Assert.That(PhysBoneFilterPass.HasPendingRemovals(context), Is.False);
            TrackControllerAssets((AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController);
        }

        [Test]
        public void PhysBoneFilterRechecksOrphanCollidersEvenWithoutNewPhysBones()
        {
            var filter = PhysFilter(); filter.RemoveUnreferencedColliders = true;
            var context = ExpressionBuild(null, Controller(Clip()));
            PhysBoneFilterPass.Capture(context); PhysBoneFilterPass.Apply(context);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            var collider = PolicyChild("Late orphan collider").AddComponent<VRCPhysBoneCollider>();
            AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            Assert.That(collider == null, Is.True);
            TrackControllerAssets((AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController);
        }

        [Test]
        public void PhysBoneFilterLateSelectionStillRejectsPreservedInputs()
        {
            var filter = PhysFilter(); filter.FilterMode = PhysBoneFilterMode.KeepListed;
            Policy().PreserveParameters.Names.Add("Late_IsGrabbed");
            var context = new BuildContext(root, null); ParameterPolicyPass.Capture(context);
            PhysBoneFilterPass.Capture(context); PhysBoneFilterPass.Apply(context);
            var late = PhysBone("Late"); late.parameter = "Late";
            Assert.Throws<InvalidOperationException>(() => GeneratedOutputFilterPass.Apply(context));
            Assert.That(late != null, Is.True);
        }

        [TestCase(PhysBoneFilterMode.KeepListed)] [TestCase(PhysBoneFilterMode.RemoveListed)]
        public void PhysBoneFilterSdkUsesReplacementRegistryForItsSelection(PhysBoneFilterMode mode)
        {
            var filter = PhysFilter(); filter.FilterMode = mode;
            var selected = PhysBone("Selected"); filter.PhysBones.Add(selected);
            var context = ExpressionBuild(null, Controller(Clip())); VRCPhysBone replacement;
            using (new ObjectRegistryScope(new ObjectRegistry(root.transform)))
            {
                PhysBoneFilterPass.Capture(context);
                AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
                replacement = PhysBone("Replacement"); ObjectRegistry.RegisterReplacedObject(selected, replacement);
                UnityEngine.Object.DestroyImmediate(selected);
            }
            AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            Assert.That(replacement != null, Is.EqualTo(mode == PhysBoneFilterMode.KeepListed));
            TrackControllerAssets((AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController);
        }

        [TestCase(RendererFilterTypes.ParticleSystems)] [TestCase(RendererFilterTypes.Lines)] [TestCase(RendererFilterTypes.Trails)]
        public void RenderingFilterSdkFinalizerRechecksGeneratedRendererTypes(RendererFilterTypes type)
        {
            if (root.GetComponent<VRCAvatarDescriptor>() == null) root.AddComponent<VRCAvatarDescriptor>();
            var filter = root.AddComponent<ALTORenderingFilter>(); filter.ApplyOnPlatforms = OptimizationPlatforms.All;
            filter.ExcludedRendererTypes = type;
            var context = ExpressionBuild(null, Controller(Clip())); BuildFilterPass.RemoveEarly(context);
            MaterialFilterPass.Apply(context);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            var owner = PolicyChild("Generated renderer");
            if (type == RendererFilterTypes.ParticleSystems) owner.AddComponent<ParticleSystem>();
            if (type == RendererFilterTypes.Lines) owner.AddComponent<LineRenderer>();
            if (type == RendererFilterTypes.Trails) owner.AddComponent<TrailRenderer>();
            var generated = owner.GetComponent<Renderer>();
            AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            Assert.That(generated == null && owner.GetComponent<ParticleSystem>() == null, Is.True);
            Assert.That(owner != null, Is.True);
            TrackControllerAssets((AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController);
        }

        [Test]
        public void GeneratedOutputFilteringDoesNotReplayConsumedSlotAndRegionSelections()
        {
            MaterialDescriptor(); renderer.sharedMesh = OcclusionMaskMesh();
            renderer.sharedMaterials = Enumerable.Range(0, 3).Select(i => Track(new Material(Shader.Find("Unlit/Texture")))).ToArray();
            var filter = LateRenderingFilter();
            filter.MaterialExclusions.Add(new MaterialExclusion { Renderer = renderer, Slot = 2 });
            filter.BlendShapeExclusions.Add(new BlendShapeMeshExclusion { Renderer = renderer, BlendShapes = new() { "Region A" } });
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            ApplyRegionFilter(context); MaterialFilterPass.Apply(context);
            var rewritten = Track(UnityEngine.Object.Instantiate(renderer.sharedMesh)); rewritten.ClearBlendShapes(); renderer.sharedMesh = rewritten;
            renderer.sharedMaterials = renderer.sharedMaterials.Concat(new[] { Track(new Material(Shader.Find("Unlit/Texture"))) }).ToArray();
            var mesh = renderer.sharedMesh; var materials = renderer.sharedMaterials;
            GeneratedOutputFilterPass.Apply(context, new Dictionary<object, VirtualAnimatorController>());
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh)); Assert.That(renderer.sharedMaterials, Is.EqualTo(materials));
        }

        [Test]
        public void GeneratedRenderingFilterRetainsStateMachineBehaviourDependencies()
        {
            var context = new BuildContext(root, null);
            var state = context.GetState<RenderingFilterState>();
            state.Scope = new OptimizationBuildScope { Root = root.transform }; state.ExcludedRendererTypes = RendererFilterTypes.Lines;
            var line = PolicyChild("Referenced line").AddComponent<LineRenderer>();
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip()));
            var behaviour = Track(ScriptableObject.CreateInstance<RenderingDependencyBehaviour>()); behaviour.Target = line;
            var machine = controller.AllReachableNodes().OfType<VirtualStateMachine>().Single();
            machine.Behaviours = machine.Behaviours.Add(behaviour);
            GeneratedOutputFilterPass.Apply(context, new Dictionary<object, VirtualAnimatorController> { [root.AddComponent<Animator>()] = controller });
            Assert.That(line != null, Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void RenderingFilterRechecksAdditionalPassesWithoutReapplyingExplicitSlots(bool preserve)
        {
            MaterialFixture(2, 4);
            var filter = RenderingFilter(); filter.RemoveAdditionalMaterialPasses = true;
            filter.MaterialExclusions.Add(new MaterialExclusion { Renderer = renderer, Slot = 2 });
            if (preserve) filter.PreserveAutomaticRenderers.Add(renderer);
            var source = renderer.sharedMesh;
            var context = ExpressionBuild(null, Controller(Clip())); BuildFilterPass.RemoveEarly(context);
            MaterialFilterPass.Apply(context);
            int afterInitial = renderer.sharedMaterials.Length;
            var materials = renderer.sharedMaterials.ToList(); materials.Add(materials[0]); renderer.sharedMaterials = materials.ToArray();
            var clip = SlotClip(materials.Count - 1, materials[0]);
            var entries = ConstraintControllers(clip);
            GeneratedOutputFilterPass.Apply(context, entries);
            Assert.That(renderer.sharedMesh, Is.SameAs(source));
            Assert.That(renderer.sharedMaterials.Length, Is.EqualTo(preserve ? afterInitial + 1 : 2));
            var builtClip = entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(builtClip.GetObjectCurveBindings().Any(), Is.EqualTo(preserve));
            GeneratedOutputFilterPass.Apply(context, entries);
            Assert.That(renderer.sharedMaterials.Length, Is.EqualTo(preserve ? afterInitial + 1 : 2));
        }

        [Test]
        public void RenderingFilterSdkRechecksAdditionalPassesWithoutOtherOptimizers()
        {
            MaterialFixture(1, 1);
            RenderingFilter().RemoveAdditionalMaterialPasses = true;
            var context = ExpressionBuild(null, Controller(Clip())); BuildFilterPass.RemoveEarly(context);
            MaterialFilterPass.Apply(context);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            renderer.sharedMaterials = new[] { renderer.sharedMaterial, renderer.sharedMaterial };
            AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            Assert.That(renderer.sharedMaterials.Length, Is.EqualTo(1));
            TrackControllerAssets((AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController);
        }
    }
}
