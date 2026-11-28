using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("Empty")] [TestCase("MissingFloat")] [TestCase("MissingObject")]
        public void CurveRemovalWithoutExistingTargetsPreservesAuthoredEmptyMotion(string selection)
        {
            var empty = Track(new AnimationClip { name = "Authored empty motion" });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(empty));
            var clip = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            var requested = selection == "Empty" ? Array.Empty<EditorCurveBinding>() : new[] {
                selection == "MissingObject" ? EditorCurveBinding.PPtrCurve("Absent", typeof(MeshRenderer), "m_Materials.Array.data[0]") :
                    EditorCurveBinding.FloatCurve("Absent", typeof(GameObject), "m_IsActive") };
            MotionAnalysis.RemoveCurves(clip, requested);
            Assert.That(clip.GetFloatCurveBindings(), Is.Empty);
            Assert.That(clip.GetObjectCurveBindings(), Is.Empty);
            var built = CommitAnimator(controller, root).animationClips.Single();
            Assert.That(built.empty, Is.True);
            Assert.That(built.length, Is.EqualTo(empty.length));
            Assert.That(AnimationUtility.GetAnimationClipSettings(built).stopTime,
                Is.EqualTo(AnimationUtility.GetAnimationClipSettings(empty).stopTime));
        }

        [TestCase(false, 20)] [TestCase(false, 60)] [TestCase(true, 20)] [TestCase(true, 60)]
        public void ParameterPruningPreservesHumanoidReleaseToLowerLayer(bool writeDefaults, int fps)
        {
            var animator = SpatialHumanoid(withFingers: true);
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var finger = animator.GetBoneTransform(HumanBodyBones.LeftIndexProximal);
            var muscle = EditorCurveBinding.FloatCurve("", typeof(Animator), "LeftHand.Index.1 Stretched");
            var lower = StaticCurve(muscle, -.6f); lower.name = "Controller gesture";
            var held = StaticCurve(muscle, 1); held.name = "Held pose";
            var empty = Track(new AnimationClip { name = "Authored release" });
            var source = Controller(lower);
            var upper = Controller(empty, held).layers[0]; upper.name = "Pose override"; upper.defaultWeight = 1;
            source.layers = new[] { source.layers[0], upper };
            source.AddParameter("Held", AnimatorControllerParameterType.Bool);
            var idle = upper.stateMachine.states[0].state;
            var active = upper.stateMachine.states[1].state;
            idle.writeDefaultValues = active.writeDefaultValues = writeDefaults;
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            Edge(idle, active, "Held", 0, AnimatorConditionMode.If).duration = 0;
            Edge(active, idle, "Held", 0, AnimatorConditionMode.IfNot).duration = 0;
            Track(active.AddStateMachineBehaviour<VRCAnimatorTrackingControl>()).trackingLeftFingers =
                VRC_AnimatorTrackingControl.TrackingType.Animation;
            Track(idle.AddStateMachineBehaviour<VRCAnimatorTrackingControl>()).trackingLeftFingers =
                VRC_AnimatorTrackingControl.TrackingType.Tracking;

            Quaternion[] Sample(AnimatorController current)
            {
                var graph = PlayableGraph.Create("Humanoid release equivalence");
                try
                {
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    var playable = AnimatorControllerPlayable.Create(graph, current);
                    AnimationPlayableOutput.Create(graph, "Avatar", animator).SetSourcePlayable(playable);
                    graph.Play();
                    var samples = new Quaternion[30];
                    for (int frame = 0; frame < samples.Length; frame++)
                    {
                        playable.SetBool("Held", frame >= 3 && frame < 15);
                        graph.Evaluate(1f / fps);
                        samples[frame] = finger.localRotation;
                    }
                    return samples;
                }
                finally { graph.Destroy(); }
            }

            var before = Sample(source);
            Assert.That(Quaternion.Angle(before[2], before[14]), Is.GreaterThan(10), "The held pose must be evaluated.");
            Assert.That(Quaternion.Angle(before[2], before[29]), Is.LessThan(.01f), "The original releases to the controller gesture.");
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var report = new AnimatorOptimizationReport();
            ParameterLiveness.RemoveUnusedCurves(root, new[] { controller }, report);
            var commitTarget = PolicyChild("Controller commit");
            var built = CommitAnimator(controller, commitTarget);
            var after = Sample(built);
            for (int frame = 0; frame < before.Length; frame++)
                Assert.That(Quaternion.Angle(before[frame], after[frame]), Is.LessThan(.01f), "Frame " + frame);
            Assert.That(report.ParameterCurves, Is.Zero);
            Assert.That(built.animationClips.Single(c => c.name == empty.name).empty, Is.True);
            Assert.That(built.layers[1].stateMachine.states.SelectMany(s => s.state.behaviours)
                .OfType<VRCAnimatorTrackingControl>().Select(b => b.trackingLeftFingers),
                Is.EquivalentTo(new[] { VRC_AnimatorTrackingControl.TrackingType.Tracking, VRC_AnimatorTrackingControl.TrackingType.Animation }));
        }

        [TestCase(false)] [TestCase(true)]
        public void CurveRemovalOfLastRealTrackRetainsNonemptyZeroDurationMotion(bool objectCurve)
        {
            var source = Track(new AnimationClip { name = "Nonempty zero-duration motion" });
            var binding = objectCurve ? EditorCurveBinding.PPtrCurve("Absent", typeof(MeshRenderer), "m_Materials.Array.data[0]") :
                EditorCurveBinding.FloatCurve("Absent", typeof(GameObject), "m_IsActive");
            if (objectCurve) AnimationUtility.SetObjectReferenceCurve(source, binding,
                new[] { new ObjectReferenceKeyframe { time = 0, value = null } });
            else AnimationUtility.SetEditorCurve(source, binding, new AnimationCurve(new Keyframe(0, 0)));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(source));
            var clip = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            MotionAnalysis.RemoveCurves(clip, new[] { binding });
            var built = CommitAnimator(controller, root).animationClips.Single();
            Assert.That(built.empty, Is.False, "Removing the last binding must not introduce authored-empty pose semantics.");
            Assert.That(built.length, Is.EqualTo(source.length).Within(1e-6));
            Assert.That(AnimationUtility.GetCurveBindings(built).Single().path, Does.StartWith("__advanced_optimizer_duration_"));
            Assert.That(AnimationUtility.GetObjectReferenceCurveBindings(built), Is.Empty);
        }
    }
}
