using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private List<string> PruneTransforms(VirtualAnimatorController controller, ObjectOptimizationState settings,
            IEnumerable<string> preserved = null, Dictionary<object, VirtualAnimatorController> entries = null)
        {
            var messages = new List<string>();
            UnusedTransformOutputs.Apply(root, entries ?? new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller },
                null, settings, preserved, messages);
            return messages;
        }

        [TestCase(false)] [TestCase(true)]
        public void PreservedInputDoesNotRootAnUnobservedTransformChain(bool active)
        {
            Mesh("Face"); var settings = ObjectSettings();
            var input = PolicyChild("Unused source"); var target = PolicyChild("Unused target");
            target.SetActive(active);
            var constraint = target.AddComponent<PositionConstraint>();
            constraint.AddSource(new ConstraintSource { sourceTransform = input.transform, weight = 1 });
            var clip = InitialTransformClip(input.transform);
            AnimationUtility.SetEditorCurve(clip, Binding("Face"), AnimationCurve.Linear(0, 0, 0.3f, 100));
            var source = TreeController(("Preserved", clip));
            var sourceJson = EditorJsonUtility.ToJson(clip);
            var expected = SampleFrozen(source, (p, frame) => p.SetFloat("Preserved", (frame % 7) / 6f), 32);
            renderer.SetBlendShapeWeight(0, 0);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            PruneTransforms(controller, settings, new[] { "Preserved" });
            CollectObjects(settings, controller, new[] { "Preserved" });
            Assert.That(input == null && target == null && constraint == null, Is.True,
                "Input preservation keeps its live outputs, not otherwise unobserved transform writers");
            Assert.That(controller.Parameters.ContainsKey("Preserved"), Is.True);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings())
                .Any(b => b.type == typeof(Transform)), Is.False);
            var actual = SampleFrozen(CommitAnimator(controller, root),
                (p, frame) => p.SetFloat("Preserved", (frame % 7) / 6f), 32);
            Assert.That(actual, Is.EqualTo(expected).Within(0.00001f));
            Assert.That(actual.Max() - actual.Min(), Is.GreaterThan(10));
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(sourceJson));
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstraintChainsFollowConsumersInsteadOfAnimationWriters(bool consumed)
        {
            var settings = ObjectSettings();
            var source = PolicyChild("Source"); var middle = PolicyChild("Middle"); var target = PolicyChild("Target");
            var a = middle.AddComponent<PositionConstraint>(); a.AddSource(new ConstraintSource { sourceTransform = source.transform, weight = 1 });
            var b = target.AddComponent<ParentConstraint>(); b.AddSource(new ConstraintSource { sourceTransform = middle.transform, weight = 1 });
            if (consumed) { var child = PolicyChild("Visible", target.transform); child.AddComponent<MeshRenderer>(); child.SetActive(false); }
            var clip = InitialTransformClip(source.transform); var native = Controller(clip);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(native);
            PruneTransforms(controller, settings);
            Assert.That(a != null && b != null, Is.EqualTo(consumed));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings()).Any(x => x.path == source.name), Is.EqualTo(consumed));
            Assert.That(MotionAnalysis.EndTime(controller.AllReachableNodes().OfType<VirtualClip>().Single()), Is.EqualTo(0.3f));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(3));
        }

        [TestCase("SkinBone")] [TestCase("RootBone")] [TestCase("PhysBoneChild")] [TestCase("Contact")]
        [TestCase("Preserved")] [TestCase("OutsideScope")] [TestCase("PreservedInput")] [TestCase("PPtrValue")]
        public void TransformDependenciesRetainIndirectAndProtectedOutputs(string consumer)
        {
            var parent = PolicyChild("Scope"); var target = PolicyChild("Target", parent.transform);
            var settings = ObjectSettings(consumer == "OutsideScope" ? PolicyChild("Other scope") : parent);
            var constraint = target.AddComponent<PositionConstraint>();
            var clip = InitialTransformClip(target.transform);
            if (consumer == "SkinBone") renderer.bones = new[] { target.transform };
            if (consumer == "RootBone") renderer.rootBone = target.transform;
            if (consumer == "PhysBoneChild") parent.AddComponent<VRCPhysBone>();
            if (consumer == "Contact") target.AddComponent<VRCContactSender>();
            if (consumer == "Preserved") settings.ProtectedComponents.Add(ObjectRegistry.GetReference(constraint));
            if (consumer == "PPtrValue") AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_RootBone"), new[] { new ObjectReferenceKeyframe { time = 0, value = target.transform } });
            var native = consumer == "PreservedInput" ? TreeController(("Keep", clip)) : Controller(clip);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(native);
            PruneTransforms(controller, settings, consumer == "PreservedInput" ? new[] { "Keep" } : null);
            bool observed = consumer != "PreservedInput";
            Assert.That(constraint != null, Is.EqualTo(observed));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings()).Any(b => b.type == typeof(Transform)), Is.EqualTo(observed));
            if (!observed) Assert.That(controller.Parameters.ContainsKey("Keep"), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void VrcConstraintUsesItsConfiguredTargetRatherThanItsOwner(bool consumed)
        {
            var settings = ObjectSettings(); var owner = PolicyChild("Constraint owner"); owner.AddComponent<MeshRenderer>();
            var target = PolicyChild("Target"); if (consumed) renderer.bones = new[] { target.transform };
            var type = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "VRC.SDK3.Dynamics.Constraint")
                .GetType("VRC.SDK3.Dynamics.Constraint.Components.VRCParentConstraint");
            var constraint = owner.AddComponent(type); type.GetField("TargetTransform").SetValue(constraint, target.transform);
            var binding = EditorCurveBinding.FloatCurve(owner.name, type, "GlobalWeight");
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(StaticCurve(binding, 1)));
            PruneTransforms(controller, settings);
            Assert.That(constraint != null, Is.EqualTo(consumed));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings()).Any(b => b.type == type), Is.EqualTo(consumed));
        }

        [Test]
        public void AnimatedConstraintTargetsAndTheirPossibleSourcesRemainLive()
        {
            var settings = ObjectSettings(); var owner = PolicyChild("Constraint owner"); var target = PolicyChild("Target");
            var type = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "VRC.SDK3.Dynamics.Constraint")
                .GetType("VRC.SDK3.Dynamics.Constraint.Components.VRCParentConstraint");
            var constraint = owner.AddComponent(type);
            var clip = Clip(); AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve(owner.name, type, "TargetTransform"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = target.transform } });
            var writer = target.AddComponent<PositionConstraint>();
            PruneTransforms(new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip)), settings);
            Assert.That(constraint != null && writer != null, Is.True);
        }

        [Test]
        public void FinalTransformAnalysisSurvivesRemovalOfItsSettingsMarker()
        {
            var settings = ObjectSettings();
            var context = new BuildContext(root, null); ObjectOptimizationPass.Capture(context);
            var constraint = PolicyChild("Unobserved").AddComponent<PositionConstraint>();
            UnityEngine.Object.DestroyImmediate(settings.Component);
            ObjectOptimizationPass.ApplyFinal(context);
            Assert.That(constraint == null, Is.True);
        }

        [Test]
        public void ImmutableMarkersRetainTheirTargetsButNotUnrelatedConstraints()
        {
            var settings = ObjectSettings(); var target = PolicyChild("Marker target"); var unrelated = PolicyChild("Unused");
            var retained = target.AddComponent<PositionConstraint>(); var removed = unrelated.AddComponent<PositionConstraint>();
            var clip = InitialTransformClip(target.transform);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip()));
            controller.Layers.First().StateMachine.DefaultState.Motion = VirtualClip.FromMarker(clip);
            PruneTransforms(controller, settings);
            Assert.That(retained != null, Is.True); Assert.That(removed == null, Is.True);
            Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(3));
        }

        [Test]
        public void ASharedClipWithDifferentAnimatorOwnersRetainsEveryVisibleTarget()
        {
            var settings = ObjectSettings(); var first = PolicyChild("First"); var second = PolicyChild("Second");
            var unused = PolicyChild("Target", first.transform); var visible = PolicyChild("Target", second.transform);
            visible.AddComponent<MeshRenderer>();
            var clip = StaticCurve(EditorCurveBinding.FloatCurve("Target", typeof(Transform), "m_LocalPosition.x"), 2);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            PruneTransforms(controller, settings, entries: new Dictionary<object, VirtualAnimatorController>
            {
                [first.AddComponent<Animator>()] = controller, [second.AddComponent<Animator>()] = controller
            });
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurveBindings().Single().path, Is.EqualTo("Target"));
        }

        [Test]
        public void AmbiguousPathsAndEventReadersDisableUnsafeTransformRemoval()
        {
            var settings = ObjectSettings(); var target = PolicyChild("Duplicate"); var other = PolicyChild("Duplicate");
            var a = target.AddComponent<PositionConstraint>(); var b = other.AddComponent<PositionConstraint>();
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(InitialTransformClip(target.transform)));
            PruneTransforms(controller, settings);
            Assert.That(a != null && b != null, Is.True);
            var eventClip = Clip(); AnimationUtility.SetAnimationEvents(eventClip, new[] { new AnimationEvent { functionName = "ReadHierarchy" } });
            var messages = PruneTransforms(new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(eventClip)), settings);
            Assert.That(messages.Single(), Does.Contain("skipped"));
            Assert.That(a != null && b != null, Is.True);
        }
    }
}
