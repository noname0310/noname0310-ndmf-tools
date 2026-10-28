using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Expressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private AnimatorControllerLayer EmptyLayer(string name, bool defaults = true, AnimationClip clip = null)
        {
            var machine = Track(new AnimatorStateMachine { name = name });
            var state = Track(new AnimatorState { name = name, writeDefaultValues = defaults, motion = clip ?? Clip() });
            machine.states = new[] { new ChildAnimatorState { state = state } }; machine.defaultState = state;
            return new AnimatorControllerLayer { name = name, defaultWeight = 1, stateMachine = machine };
        }

        private AnimatorOptimizationReport RemoveEmptyLayers(VirtualAnimatorController controller,
            IDictionary<object, VirtualAnimatorController> entries = null)
        {
            var all = entries ?? new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var outputs = new ExpressionOutputUsage(root, all, null);
            var report = new AnimatorOptimizationReport();
            UnusedLayers.Apply(all.Values.Distinct().ToArray(), report, outputs.IsObservable, all);
            return report;
        }

        [Test]
        public void EmptyOverrideLayerPreservesNativeOutputs(
            [Values(false, true)] bool defaults, [Values(false, true)] bool above,
            [Values(false, true)] bool missingBinding, [Values(false, true)] bool writerDefaults,
            [Values("BlendShape", "Transform", "Material")] string output,
            [Values(false, true)] bool additiveWriter, [Values(.35f, 1f)] float emptyWeight)
        {
            Mesh("a");
            var clip = StaticCurve(Binding("a"), 70);
            Material initial = null, animated = null;
            if (output == "Transform") clip = StaticCurve(EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalScale.x"), 3);
            if (output == "Material")
            {
                initial = Track(new Material(Shader.Find("Hidden/InternalErrorShader")));
                animated = Track(new Material(initial)); renderer.sharedMaterial = initial; clip = Clip();
                AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = animated } });
            }
            var source = Controller(Clip()); source.layers[0].stateMachine.defaultState.writeDefaultValues = false;
            source.AddParameter("Gate", AnimatorControllerParameterType.Float);
            var writer = EmptyLayer("Writer", writerDefaults, clip);
            writer.blendingMode = additiveWriter ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override;
            var other = Track(writer.stateMachine.AddState("Unwritten")); other.motion = Clip(); other.writeDefaultValues = writerDefaults;
            Edge(writer.stateMachine.defaultState, other, "Gate", .5f, AnimatorConditionMode.Greater).duration = .1f;
            Edge(other, writer.stateMachine.defaultState, "Gate", .5f, AnimatorConditionMode.Less).duration = .1f;
            var empty = Clip();
            if (missingBinding) AnimationUtility.SetEditorCurve(empty,
                EditorCurveBinding.FloatCurve("Absent", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 0));
            var candidate = EmptyLayer("Candidate", defaults, empty);
            candidate.defaultWeight = emptyWeight;
            source.layers = source.layers.Concat(above ? new[] { writer, candidate } : new[] { candidate, writer }).ToArray();
            float Sample() => output == "Transform" ? root.transform.localScale.x : output == "Material" ?
                renderer.sharedMaterial == animated ? 1 : 0 : renderer.GetBlendShapeWeight(0);
            root.transform.localScale = Vector3.one;
            var expected = SampleFrozen(source, (p, i) => p.SetFloat("Gate", (i / 20) % 2), 80, Sample);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var result = RemoveEmptyLayers(controller);
            Assert.That(result.Layers, Is.EqualTo(defaults ? 1 : 0));
            root.transform.localScale = Vector3.one; if (initial != null) renderer.sharedMaterial = initial;
            var actual = SampleFrozen(CommitAnimator(controller, root), (p, i) => p.SetFloat("Gate", (i / 20) % 2), 80, Sample);
            Assert.That(actual, Is.EqualTo(expected).Within(1e-5));
            Assert.That(RemoveEmptyLayers(controller).Layers, Is.Zero);
            Assert.That(source.layers.Length, Is.EqualTo(3));
        }

        [TestCase("Additive")] [TestCase("Mask")] [TestCase("Event")] [TestCase("Behaviour")]
        [TestCase("Null")] [TestCase("Transition")] [TestCase("Output")] [TestCase("SyncedTarget")]
        [TestCase("OwnControl")]
        public void EmptyLayerKeepsUnprovenOrReferencedCases(string reason)
        {
            Mesh("a"); var source = Controller(Clip());
            var empty = EmptyLayer("Candidate");
            if (reason == "Additive") empty.blendingMode = AnimatorLayerBlendingMode.Additive;
            if (reason == "Mask") empty.avatarMask = Track(new AvatarMask());
            if (reason == "Event") AnimationUtility.SetAnimationEvents((AnimationClip)empty.stateMachine.defaultState.motion,
                new[] { new AnimationEvent { functionName = "Observe" } });
            if (reason == "Behaviour") Track(empty.stateMachine.defaultState.AddStateMachineBehaviour<VRCAnimatorTrackingControl>());
            if (reason == "Null") empty.stateMachine.defaultState.motion = null;
            if (reason == "Transition") Track(empty.stateMachine.defaultState.AddTransition(empty.stateMachine.defaultState));
            if (reason == "Output") empty.stateMachine.defaultState.motion = StaticCurve(Binding("a"), 50);
            source.layers = source.layers.Concat(new[] { empty }).ToArray();
            if (reason == "SyncedTarget")
            {
                var follower = EmptyLayer("Synced"); follower.syncedLayerIndex = 1;
                source.layers = source.layers.Concat(new[] { follower }).ToArray();
            }
            if (reason == "OwnControl")
            {
                var control = Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAnimatorLayerControl>());
                control.playable = VRC_AnimatorLayerControl.BlendableLayer.FX; control.layer = 1;
            }
            var controller = new CloneContext(VRChatPlatformAnimatorBindings.Instance).Clone(source, VRCAvatarDescriptor.AnimLayerType.FX);
            Assert.That(RemoveEmptyLayers(controller).Layers, Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void EmptyLayerRemovalPreservesNativeSameAndCrossPlayableLayerControls(bool crossPlayable)
        {
            Mesh("a"); var fx = Controller(StaticCurve(Binding("a"), 20));
            fx.layers = fx.layers.Concat(new[] { EmptyLayer("Before target"), EmptyLayer("Target", true, StaticCurve(Binding("a"), 40)), EmptyLayer("After target") }).ToArray();
            var action = Controller(Clip());
            var owner = crossPlayable ? action : fx;
            var control = Track(owner.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAnimatorLayerControl>());
            control.playable = VRC_AnimatorLayerControl.BlendableLayer.FX; control.layer = 2;
            root.AddComponent<Animator>(); var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.customizeAnimationLayers = true; descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            descriptor.baseAnimationLayers = new[] {
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx },
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.Action, animatorController = action }
            };
            var context = new BuildContext(root, null); var current = new VirtualControllerContext(); current.OnActivate(context);
            var entries = current.Controllers.ToDictionary(e => e.Key, e => e.Value);
            var result = RemoveEmptyLayers(entries[VRCAvatarDescriptor.AnimLayerType.FX], entries);
            Assert.That(result.Layers, Is.EqualTo(crossPlayable ? 1 : 2));
            current.OnDeactivate(context);
            var builtFx = (AnimatorController)descriptor.baseAnimationLayers[0].animatorController;
            var builtOwner = (AnimatorController)descriptor.baseAnimationLayers[crossPlayable ? 1 : 0].animatorController;
            TrackControllerAssets(builtFx); TrackControllerAssets(builtOwner);
            var targetIndex = builtOwner.layers[0].stateMachine.defaultState.behaviours.OfType<VRCAnimatorLayerControl>().Single().layer;
            Assert.That(builtFx.layers[targetIndex].name, Is.EqualTo("Target"));
            Assert.That(targetIndex, Is.EqualTo(crossPlayable ? 2 : 1));
            Assert.That(control.layer, Is.EqualTo(2)); Assert.That(fx.layers.Length, Is.EqualTo(4));
        }
    }
}
