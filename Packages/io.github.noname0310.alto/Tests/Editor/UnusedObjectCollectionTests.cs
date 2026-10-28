using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private List<string> CollectObjects(ObjectOptimizationState settings, VirtualAnimatorController controller = null,
            IEnumerable<string> preserved = null, Dictionary<object, VirtualAnimatorController> entries = null)
        {
            var messages = new List<string>();
            controller ??= new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip()));
            UnusedObjectCollection.Apply(root, entries ?? new Dictionary<object, VirtualAnimatorController>
            { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, null, settings, preserved, messages);
            return messages;
        }

        [Test]
        public void ObjectsWithOnlyAnimationWritersAreCollectedWithoutChangingClipDuration()
        {
            var settings = ObjectSettings(); var target = PolicyChild("Unobserved");
            var clip = InitialTransformClip(target.transform);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            CollectObjects(settings, controller);
            Assert.That(target == null, Is.True);
            var output = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(output.GetFloatCurveBindings().All(b => b.path != "Unobserved"), Is.True);
            Assert.That(MotionAnalysis.EndTime(output), Is.EqualTo(0.3f));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(3));
        }

        [TestCase("GameObject")] [TestCase("Renderer")]
        public void PermanentlyInactiveDrawOutputsAreCollected(string disabled)
        {
            var settings = ObjectSettings(); var target = PolicyChild("Inactive");
            var filter = target.AddComponent<MeshFilter>(); filter.sharedMesh = Mesh();
            var draw = target.AddComponent<MeshRenderer>();
            if (disabled == "GameObject") target.SetActive(false); else draw.enabled = false;
            CollectObjects(settings);
            Assert.That(draw == null && filter == null && target == null, Is.True);
            Assert.That(root != null && renderer != null, Is.True);
        }

        [TestCase("GameObject")] [TestCase("Renderer")] [TestCase("Parent")]
        public void AnimationThatCanActivateAnOutputRetainsItsWholeDependencyChain(string disabled)
        {
            var settings = ObjectSettings(); var parent = PolicyChild("Parent"); var target = PolicyChild("Child", parent.transform);
            var draw = target.AddComponent<MeshRenderer>(); var filter = target.AddComponent<MeshFilter>();
            GameObject animated = target;
            if (disabled == "Renderer") draw.enabled = false;
            else { animated = disabled == "Parent" ? parent : target; animated.SetActive(false); }
            var binding = EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(animated.transform, root.transform),
                disabled == "Renderer" ? typeof(MeshRenderer) : typeof(GameObject), disabled == "Renderer" ? "m_Enabled" : "m_IsActive");
            var clip = StaticCurve(binding, 1);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            CollectObjects(settings, controller);
            Assert.That(target != null && parent != null && draw != null && filter != null, Is.True);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurveBindings(), Does.Contain(binding));
        }

        [Test]
        public void AnInactiveObjectUsedAsASkinBoneSurvivesWhileItsDisabledRendererIsRemoved()
        {
            var settings = ObjectSettings(); var bone = PolicyChild("Inactive bone"); bone.SetActive(false);
            var draw = bone.AddComponent<MeshRenderer>(); bone.AddComponent<MeshFilter>();
            renderer.bones = new[] { bone.transform }; renderer.rootBone = bone.transform;
            var constraint = bone.AddComponent<PositionConstraint>(); var source = PolicyChild("Constraint source");
            constraint.AddSource(new ConstraintSource { sourceTransform = source.transform, weight = 1 });
            CollectObjects(settings);
            Assert.That(bone != null && source != null && constraint != null, Is.True);
            Assert.That(draw == null, Is.True);
        }

        [TestCase("Component")] [TestCase("Subtree")] [TestCase("OutsideScope")]
        public void ObjectCollectionRespectsExplicitPreservationAndScope(string reason)
        {
            var target = PolicyChild("Protected"); var child = PolicyChild("Child", target.transform);
            var draw = child.AddComponent<MeshRenderer>(); child.SetActive(false);
            var settings = ObjectSettings(reason == "OutsideScope" ? PolicyChild("Scope") : root);
            if (reason == "Component") settings.ProtectedComponents.Add(ObjectRegistry.GetReference(draw));
            if (reason == "Subtree")
                settings.ProtectedSubtrees.Add(new Noname.AvatarTools.Build.OptimizationBuildScope { Root = target.transform });
            CollectObjects(settings);
            Assert.That(target != null && child != null && draw != null, Is.True);
        }

        [Test]
        public void PhysBoneColliderCollectionFollowsReferencesAndKeepsTheBoneHierarchy()
        {
            var settings = ObjectSettings(); var owner = PolicyChild("Physics"); var child = PolicyChild("End", owner.transform);
            var bone = owner.AddComponent<VRCPhysBone>();
            var used = PolicyChild("Used collider").AddComponent<VRCPhysBoneCollider>();
            var unused = PolicyChild("Unused collider").AddComponent<VRCPhysBoneCollider>();
            bone.colliders.Add(used);
            CollectObjects(settings);
            Assert.That(bone != null && child != null && used != null, Is.True);
            Assert.That(unused == null, Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void GlobalAndAnimatedGlobalCollidersAreObservableWithoutLocalReferences(bool animated)
        {
            var settings = ObjectSettings(); var collider = PolicyChild("Global").AddComponent<VRCPhysBoneCollider>();
            var clip = Clip();
            if (animated) AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Global", typeof(VRCPhysBoneCollider), "globalCollision"),
                AnimationCurve.Constant(0, 1, 1));
            else collider.globalCollision = VRC.Dynamics.VRCPhysBoneBase.AdvancedBool.True;
            CollectObjects(settings, new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip)));
            Assert.That(collider != null, Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void ReceiversAreRemovedOnlyAfterTheirPreviouslyObservedInputWasPruned(bool pruned)
        {
            var settings = ObjectSettings(); var receiver = PolicyChild("Receiver").AddComponent<VRCContactReceiver>();
            receiver.parameter = "Observed";
            var external = PolicyChild("External receiver").AddComponent<VRCContactReceiver>(); external.parameter = "ExternalOnly";
            if (pruned) settings.PrunedSensorInputs.Add("Observed");
            CollectObjects(settings);
            Assert.That(receiver == null, Is.EqualTo(pruned)); Assert.That(external != null, Is.True);
        }

        [TestCase("ObjectKey")] [TestCase("PreservedParameter")] [TestCase("Marker")] [TestCase("AmbiguousPath")]
        public void CollectionDistinguishesObjectReferencesFromParameterPreservation(string reason)
        {
            var settings = ObjectSettings(); var target = PolicyChild("Retained");
            var clip = InitialTransformClip(target.transform);
            if (reason == "ObjectKey") AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_RootBone"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = target.transform } });
            if (reason == "AmbiguousPath") PolicyChild("Retained");
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(
                reason == "PreservedParameter" ? TreeController(("FT", clip)) : Controller(clip));
            if (reason == "Marker") controller.Layers.First().StateMachine.DefaultState.Motion = VirtualClip.FromMarker(clip);
            CollectObjects(settings, controller, reason == "PreservedParameter" ? new[] { "FT" } : null);
            Assert.That(target != null, Is.EqualTo(reason != "PreservedParameter"));
            if (reason == "PreservedParameter") Assert.That(controller.Parameters.ContainsKey("FT"), Is.True);
        }

        [TestCase("Event")] [TestCase("RuntimeComponent")]
        public void UnknownRuntimeReadersDisableHierarchyCollection(string reason)
        {
            var settings = ObjectSettings(); var target = PolicyChild("Potentially observed"); var clip = Clip();
            if (reason == "Event") AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { functionName = "InspectHierarchy" } });
            else root.AddComponent<ParameterPolicySourceFixture>();
            var messages = CollectObjects(settings, new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip)));
            Assert.That(target != null, Is.True); Assert.That(messages.Single(), Does.Contain("skipped"));
        }

        [Test]
        public void CollectionSettingsSurviveExternalRemovalOfTheAuthoringMarker()
        {
            var settings = ObjectSettings(); var unused = PolicyChild("Unused");
            var context = new BuildContext(root, null); ObjectOptimizationPass.Capture(context);
            UnityEngine.Object.DestroyImmediate(settings.Component);
            ObjectOptimizationPass.ApplyFinal(context);
            Assert.That(unused == null, Is.True);
        }

        [TestCase("UnusedResult")]
        [TestCase("PrunedParameters")]
        [TestCase("ObservedParameter")]
        [TestCase("PartiallyPrunedParameters")]
        [TestCase("SkinBone")]
        [TestCase("ChildRenderer")]
        [TestCase("ContactSender")]
        [TestCase("ConstraintConsumer")]
        [TestCase("OutsideAvatar")]
        [TestCase("PreserveComponent")]
        [TestCase("OutsideScope")]
        [TestCase("AnimatedResult")]
        public void RaycastCollectionTracksBothResultTransformsAndParameterOutputs(string consumer)
        {
            var settings = ObjectSettings(consumer == "OutsideScope" ? PolicyChild("Other scope") : root);
            var owner = PolicyChild("Raycast"); var result = PolicyChild("Raycast result");
            var raycast = owner.AddComponent<VRCRaycast>(); raycast.ResultTransform = result.transform;
            var clip = Clip();
            if (consumer == "PrunedParameters" || consumer == "ObservedParameter" || consumer == "PartiallyPrunedParameters")
            {
                raycast.Parameter = "Distance probe";
                if (consumer == "PrunedParameters")
                    settings.PrunedSensorInputs.UnionWith(AvatarParameterInputs.RaycastSuffixes.Select(s => raycast.Parameter + s));
                if (consumer == "PartiallyPrunedParameters") settings.PrunedSensorInputs.Add(raycast.Parameter + VRCRaycast.PARAM_HIT);
            }
            if (consumer == "SkinBone") renderer.bones = new[] { result.transform };
            if (consumer == "ChildRenderer") PolicyChild("Visible result", result.transform).AddComponent<MeshRenderer>();
            if (consumer == "ContactSender") result.AddComponent<VRCContactSender>();
            if (consumer == "ConstraintConsumer")
            {
                var target = PolicyChild("Constrained output"); target.AddComponent<MeshRenderer>();
                target.AddComponent<PositionConstraint>().AddSource(new ConstraintSource { sourceTransform = result.transform, weight = 1 });
            }
            if (consumer == "OutsideAvatar") result.transform.SetParent(null, false);
            if (consumer == "PreserveComponent") settings.ProtectedComponents.Add(ObjectRegistry.GetReference(raycast));
            if (consumer == "AnimatedResult") AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve(owner.name, typeof(VRCRaycast), "resultTransform"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = result.transform } });
            CollectObjects(settings, new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip)));
            bool removed = consumer == "UnusedResult" || consumer == "PrunedParameters";
            Assert.That(raycast == null && owner == null && result == null, Is.EqualTo(removed));
            if (!removed) Assert.That(raycast != null && owner != null && result != null, Is.True);
        }

        [Test]
        public void AuthoringMetadataRetainsExplicitReferencesWithoutRootingTheWholeAvatar()
        {
            var settings = ObjectSettings(); var target = PolicyChild("Referenced"); var unused = PolicyChild("Unused");
            var draw = target.AddComponent<MeshRenderer>(); draw.enabled = false;
            root.AddComponent<DependencyAuthoringFixture>().Target = draw;
            CollectObjects(settings);
            Assert.That(target != null && draw != null, Is.True);
            Assert.That(unused == null, Is.True);
        }

        [Test]
        public void SharedAnimationRootBindingsRetainTheOutputUsedByEitherOwner()
        {
            var settings = ObjectSettings(); var first = PolicyChild("First"); var second = PolicyChild("Second");
            var unused = PolicyChild("Target", first.transform); var visible = PolicyChild("Target", second.transform);
            visible.AddComponent<MeshRenderer>();
            var clip = StaticCurve(EditorCurveBinding.FloatCurve("Target", typeof(Transform), "m_LocalPosition.x"), 2);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            CollectObjects(settings, controller, entries: new Dictionary<object, VirtualAnimatorController>
            { [first.AddComponent<Animator>()] = controller, [second.AddComponent<Animator>()] = controller });
            Assert.That(unused == null, Is.True); Assert.That(visible != null, Is.True);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurveBindings().Single().path, Is.EqualTo("Target"));
        }
    }
}
