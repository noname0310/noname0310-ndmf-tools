using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false, false)] [TestCase(false, true)]
        [TestCase(true, false)] [TestCase(true, true)]
        public void UnusedAnimatorCollectionRemovesControllerlessSubtrees(bool genericAvatar, bool animated)
        {
            var settings = ObjectSettings(); var owner = PolicyChild("Unused animator");
            var child = PolicyChild("Unobserved bone", owner.transform);
            var animator = owner.AddComponent<Animator>();
            if (genericAvatar)
            {
                animator.avatar = Track(AvatarBuilder.BuildGenericAvatar(owner, ""));
                Assert.That(animator.avatar.isValid && !animator.avatar.isHuman, Is.True);
            }
            var source = animated ? InitialTransformClip(child.transform) : Clip();
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(source));
            var messages = CollectObjects(settings, controller);
            Assert.That(animator == null && owner == null && child == null, Is.True);
            Assert.That(messages.Any(m => m.Contains("/ Animator")), Is.True);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurveBindings()
                .All(b => !b.path.StartsWith("Unused animator")), Is.True);
            if (animated)
            {
                Assert.That(AnimationUtility.GetCurveBindings(source).Length, Is.EqualTo(3));
                Assert.That(MotionAnalysis.EndTime(controller.AllReachableNodes().OfType<VirtualClip>().Single()), Is.EqualTo(.3f));
            }
            Assert.That(CollectObjects(settings, controller).Last(), Does.StartWith("Collected 0 unused components, 0 objects"));
        }

        [TestCase("Root")] [TestCase("Controller")] [TestCase("VirtualController")]
        [TestCase("Renderer")] [TestCase("SkinBone")] [TestCase("ProbeAnchor")]
        [TestCase("ComponentPreservation")] [TestCase("SubtreePreservation")] [TestCase("OutsideScope")]
        [TestCase("ControllerSwap")] [TestCase("ObjectReference")] [TestCase("PreservedInput")]
        [TestCase("AnimationEvent")] [TestCase("RuntimeReader")]
        public void UnusedAnimatorCollectionRetainsRuntimeOwnersAndObservableBoundaries(string reason)
        {
            var settings = ObjectSettings(reason == "OutsideScope" ? PolicyChild("Scope") : root);
            var owner = reason == "Root" ? root : PolicyChild("Animator boundary");
            var child = PolicyChild("Child", owner.transform);
            var animator = owner.AddComponent<Animator>();
            var clip = Clip();
            if (reason == "Controller") animator.runtimeAnimatorController = Controller(Clip());
            if (reason == "Renderer") child.AddComponent<MeshRenderer>();
            if (reason == "SkinBone") renderer.bones = new[] { child.transform };
            if (reason == "ProbeAnchor") renderer.probeAnchor = child.transform;
            if (reason == "ComponentPreservation") settings.ProtectedComponents.Add(ObjectRegistry.GetReference(animator));
            if (reason == "SubtreePreservation") settings.ProtectedSubtrees.Add(new OptimizationBuildScope { Root = owner.transform });
            if (reason == "ControllerSwap") AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve(owner.name, typeof(Animator), "m_Controller"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = Controller(Clip()) } });
            if (reason == "ObjectReference") root.AddComponent<DependencyAuthoringFixture>().Target = animator;
            if (reason == "PreservedInput") clip = InitialTransformClip(child.transform);
            if (reason == "AnimationEvent") AnimationUtility.SetAnimationEvents(clip,
                new[] { new AnimationEvent { functionName = "InspectAnimationBoundary" } });
            if (reason == "RuntimeReader") root.AddComponent<ParameterPolicySourceFixture>();
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controller = clone.Clone(reason == "PreservedInput" ? TreeController(("Preserved", clip)) : Controller(clip));
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            if (reason == "VirtualController") entries[animator] = clone.Clone(Controller(Clip()));
            CollectObjects(settings, controller, reason == "PreservedInput" ? new[] { "Preserved" } : null, entries);
            Assert.That(animator != null && owner != null, Is.EqualTo(reason != "PreservedInput"), reason);
            if (reason is "Renderer" or "SkinBone" or "ProbeAnchor") Assert.That(child != null, Is.True);
            if (reason == "PreservedInput")
            {
                Assert.That(child == null, Is.True);
                Assert.That(controller.Parameters.ContainsKey("Preserved"), Is.True);
            }
        }

        [Test]
        public void UnusedAnimatorCollectionRetainsBoundPlayablesWithoutAController()
        {
            var settings = ObjectSettings(); var owner = PolicyChild("Playable owner");
            var animator = owner.AddComponent<Animator>();
            var graph = PlayableGraph.Create("Bound child animator");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                AnimationPlayableOutput.Create(graph, "Output", animator).SetSourcePlayable(AnimationClipPlayable.Create(graph, Clip()));
                graph.Play(); graph.Evaluate(0);
                Assert.That(animator.hasBoundPlayables, Is.True);
                CollectObjects(settings);
                Assert.That(animator != null && owner != null, Is.True);
            }
            finally { graph.Destroy(); }
        }

        [Test]
        public void UnusedAnimatorCollectionMatchesNativeOutputAfterRemovingAnUnusedBoundary()
        {
            var fixture = InstanceFixture(); var settings = ObjectSettings();
            var owner = PolicyChild("Unused animator"); owner.AddComponent<Animator>();
            var input = new[] { .2f, .8f };
            var expected = input.Select(value => SampleInstanceController(fixture.source, false, 0, value)).ToArray();
            Assert.That(expected, Is.EqualTo(new[] { 20f, 80f }).Within(.001));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            CollectObjects(settings, controller);
            Assert.That(owner == null, Is.True);
            var built = CommitAnimator(controller, root);
            for (int i = 0; i < input.Length; i++)
                Assert.That(SampleInstanceController(built, false, 0, input[i]), Is.EqualTo(expected[i]).Within(.001));
        }
    }
}
