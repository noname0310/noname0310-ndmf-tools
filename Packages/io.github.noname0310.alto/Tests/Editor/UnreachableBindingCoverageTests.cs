using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private (AnimatorController controller, AnimatorState first, AnimatorState second) BindingCoverageFixture(
            string defaults = "On", string layerKind = "Base", bool nested = false)
        {
            Mesh("live", "a", "b");
            var idle = StaticCurve(Binding("live"), 15); idle.name = "Idle";
            var source = Controller(idle);
            source.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            var machine = source.layers[0].stateMachine;
            var other = Track(machine.AddState("Other")); other.motion = StaticCurve(Binding("live"), 85);
            machine.defaultState.writeDefaultValues = defaults != "Off";
            other.writeDefaultValues = defaults == "On";
            var enter = Track(machine.defaultState.AddTransition(other));
            enter.hasExitTime = false; enter.duration = .1f;
            enter.AddCondition(AnimatorConditionMode.Equals, 2, "GestureLeft");
            var leave = Track(other.AddTransition(machine.defaultState));
            leave.hasExitTime = false; leave.duration = .1f;
            leave.AddCondition(AnimatorConditionMode.NotEqual, 2, "GestureLeft");
            var owner = machine;
            if (nested)
            {
                owner = Track(machine.AddStateMachine("Unreachable nested machine"));
                owner.defaultState = Track(owner.AddState("Nested default")); owner.defaultState.motion = Clip();
            }
            var a = StaticCurve(Binding("a"), 100); a.name = "Unreachable A";
            AnimationUtility.SetEditorCurve(a, EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalScale.x"),
                AnimationCurve.Linear(0, 3, 2, 8));
            var b = StaticCurve(Binding("b"), 95); b.name = "Unreachable B";
            AnimationUtility.SetEditorCurve(b, EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalScale.z"),
                AnimationCurve.Linear(0, 7, 4, 2));
            var first = Track(owner.AddState("Unreachable A")); first.motion = a; first.writeDefaultValues = true;
            var second = Track(owner.AddState("Unreachable B")); second.motion = b; second.writeDefaultValues = false;
            // Include a transition that must be pruned without making the carrier executable.
            Track(machine.defaultState.AddTransition(first)).mute = true;
            if (layerKind != "Base")
            {
                var bottom = Track(new AnimatorStateMachine());
                bottom.defaultState = Track(bottom.AddState("Underlying pose"));
                var pose = StaticCurve(Binding("a"), 68);
                AnimationUtility.SetEditorCurve(pose, Binding("b"), AnimationCurve.Constant(0, 1, 22));
                bottom.defaultState.motion = pose;
                var top = source.layers[0]; top.name = "Top";
                if (layerKind == "Additive") top.blendingMode = AnimatorLayerBlendingMode.Additive;
                if (layerKind == "Masked")
                {
                    top.avatarMask = Track(new AvatarMask());
                    top.avatarMask.AddTransformPath(root.transform);
                }
                source.layers = new[] { new AnimatorControllerLayer { name = "Underlying", stateMachine = bottom, defaultWeight = 1 }, top };
            }
            return (source, first, second);
        }

        private float[] SampleBindingCoverage(AnimatorController controller)
        {
            root.transform.localScale = new Vector3(1, 2, 3);
            renderer.SetBlendShapeWeight(0, 12); renderer.SetBlendShapeWeight(1, 31); renderer.SetBlendShapeWeight(2, 57);
            var animator = root.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var graph = PlayableGraph.Create("Unreachable binding coverage equivalence");
            var result = new List<float>();
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Avatar", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0);
                for (int frame = 0; frame < 180; frame++)
                {
                    playable.SetInteger("GestureLeft", frame % 40 < 20 ? 0 : 2);
                    if (controller.layers.Length > 1) playable.SetLayerWeight(1, (frame % 9) / 8f);
                    // A coverage regression is observable through these external writes,
                    // even though neither unreachable clip can ever be evaluated.
                    renderer.SetBlendShapeWeight(1, 1 + frame % 93);
                    renderer.SetBlendShapeWeight(2, 2 + frame % 87);
                    root.transform.localScale = new Vector3(5 + frame % 3, 2, 7 + frame % 5);
                    graph.Evaluate(1f / 60);
                    result.Add(renderer.GetBlendShapeWeight(0)); result.Add(renderer.GetBlendShapeWeight(1));
                    result.Add(renderer.GetBlendShapeWeight(2)); result.Add(root.transform.localScale.x);
                    result.Add(root.transform.localScale.z);
                    result.Add(playable.GetCurrentAnimatorStateInfo(controller.layers.Length - 1).fullPathHash);
                }
            }
            finally { graph.Destroy(); Object.DestroyImmediate(animator); }
            return result.ToArray();
        }

        [Test, Combinatorial]
        public void UnreachableCoverageMatchesUnityAcrossDefaultsLayersAndNestedMachines(
            [Values("On", "Off", "Mixed")] string defaults,
            [Values("Base", "Override", "Additive", "Masked")] string layerKind, [Values(false, true)] bool nested)
        {
            var fixture = BindingCoverageFixture(defaults, layerKind, nested);
            var expected = SampleBindingCoverage(fixture.controller);
            var sourceData = EditorJsonUtility.ToJson(fixture.first.motion);
            var output = OptimizeAnimator(fixture.controller, new() { Unreachable = true });
            Assert.That(output.report.Fallback, Is.Null);
            Assert.That(output.report.States, Is.EqualTo(1));
            Assert.That(output.report.Transitions, Is.EqualTo(1));
            var state = output.controller.AllReachableNodes().OfType<VirtualState>().Single(s => s.Name == fixture.first.name);
            Assert.That(state.WriteDefaultValues, Is.True);
            Assert.That(AnimatorReachability.Analyze(root, new[] { output.controller }).IsReachable(state), Is.False);
            var carrier = (VirtualClip)state.Motion;
            Assert.That(carrier.GetFloatCurveBindings().Count(), Is.EqualTo(4));
            Assert.That(carrier.GetFloatCurveBindings().All(b => carrier.GetFloatCurve(b).length == 1), Is.True);
            var actual = SampleBindingCoverage(CommitAnimator(output.controller, root));
            Assert.That(actual, Is.EqualTo(expected).Within(2e-5));
            Assert.That(EditorJsonUtility.ToJson(fixture.first.motion), Is.EqualTo(sourceData));
            var repeated = AnimatorOptimizer.Optimize(root, new[] { output.controller }, new() { Unreachable = true }, finalParameterDeclarations: true);
            Assert.That(repeated.Changes, Is.Zero);
        }

        [Test]
        public void BindingCoverageComparisonDetectsDeletingUniqueUnreachableMotions()
        {
            var fixture = BindingCoverageFixture();
            var expected = SampleBindingCoverage(fixture.controller);
            fixture.controller.layers[0].stateMachine.RemoveState(fixture.first);
            fixture.controller.layers[0].stateMachine.RemoveState(fixture.second);
            Assert.That(SampleBindingCoverage(fixture.controller), Is.Not.EqualTo(expected));
        }

        [TestCase("Disabled")] [TestCase("EarlyBoundary")] [TestCase("ObjectCurve")]
        [TestCase("Discrete")] [TestCase("AnimatorCurve")] [TestCase("Legacy")]
        [TestCase("Event")] [TestCase("Behaviour")] [TestCase("Marker")]
        [TestCase("AdditiveReference")] [TestCase("DefaultState")] [TestCase("Reachable")]
        [TestCase("Rotation")]
        public void UnreachableCoverageRetainsUnprovenOrDisabledContexts(string reason)
        {
            var fixture = BindingCoverageFixture();
            var a = (AnimationClip)fixture.first.motion;
            if (reason == "ObjectCurve") AnimationUtility.SetObjectReferenceCurve(a,
                EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = Track(new Material(Shader.Find("Standard"))) } });
            if (reason == "Discrete") AnimationUtility.SetEditorCurve(a,
                EditorCurveBinding.DiscreteCurve("", typeof(GameObject), "m_IsActive"), new AnimationCurve(new Keyframe(0, 1)));
            if (reason == "Rotation") AnimationUtility.SetEditorCurve(a,
                EditorCurveBinding.FloatCurve("", typeof(Transform), "localEulerAnglesRaw.x"), new AnimationCurve(new Keyframe(0, 30)));
            if (reason == "AnimatorCurve")
            {
                fixture.controller.AddParameter("Internal", AnimatorControllerParameterType.Float);
                AnimationUtility.SetEditorCurve(a, ParameterBinding("Internal"), new AnimationCurve(new Keyframe(0, 1)));
            }
            if (reason == "Legacy") a.legacy = true;
            if (reason == "Event") AnimationUtility.SetAnimationEvents(a, new[] { new AnimationEvent { functionName = "Observe" } });
            if (reason == "Behaviour") fixture.first.behaviours = new[] { Track(ScriptableObject.CreateInstance<UnsupportedReachabilityBehaviour>()) };
            if (reason == "DefaultState")
            {
                var machine = fixture.controller.layers[0].stateMachine;
                Track(fixture.first.AddTransition(machine.defaultState)).hasExitTime = true;
                machine.defaultState = fixture.first;
            }
            if (reason == "Reachable")
            {
                var edge = Track(fixture.controller.layers[0].stateMachine.defaultState.AddTransition(fixture.first));
                edge.hasExitTime = false; edge.AddCondition(AnimatorConditionMode.Equals, 3, "GestureLeft");
            }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.controller);
            var first = controller.AllReachableNodes().OfType<VirtualState>().Single(s => s.Name == fixture.first.name);
            if (reason == "Marker") first.Motion = VirtualClip.FromMarker(a);
            if (reason == "AdditiveReference") ((VirtualClip)first.Motion).AdditiveReferencePoseClip =
                new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Clip());
            var before = first.Motion;
            AnimatorOptimizer.Optimize(root, new[] { controller }, new() { Unreachable = reason != "Disabled" },
                finalParameterDeclarations: reason != "EarlyBoundary");
            Assert.That(first.Motion, Is.SameAs(before));
            Assert.That(controller.AllReachableNodes().OfType<VirtualState>().Any(s => s.Name == fixture.second.name), Is.True);
        }

        [Test]
        public void UnreachableCoverageAllowsOnlyStateBoundSdkEffects(
            [Values(typeof(VRCAnimatorTrackingControl), typeof(VRCAvatarParameterDriver), typeof(VRCAnimatorLayerControl),
                typeof(VRCPlayableLayerControl), typeof(VRCAnimatorLocomotionControl), typeof(VRCAnimatorTemporaryPoseSpace),
                typeof(VRCAnimatorPlayAudio), typeof(VRCAnimatorFaceTrackingBlendshapesSet))] Type behaviourType)
        {
            var fixture = BindingCoverageFixture();
            fixture.first.behaviours = new[] { (StateMachineBehaviour)Track(ScriptableObject.CreateInstance(behaviourType)) };
            fixture.second.behaviours = new[] { (StateMachineBehaviour)Track(ScriptableObject.CreateInstance(behaviourType)) };
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.controller);
            var first = controller.AllReachableNodes().OfType<VirtualState>().Single(s => s.Name == fixture.first.name);
            var behaviour = first.Behaviours.Single();
            var result = AnimatorOptimizer.Optimize(root, new[] { controller }, new() { Unreachable = true }, finalParameterDeclarations: true);
            Assert.That(result.States, Is.EqualTo(1));
            Assert.That(first.Behaviours.Single(), Is.SameAs(behaviour));
            Assert.That(AnimatorReachability.Analyze(root, new[] { controller }).IsReachable(first), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void UnreachableCoverageDoesNotMutateStatesSharedAcrossOwners(bool crossController)
        {
            var fixture = BindingCoverageFixture();
            var other = Controller(Clip());
            other.layers[0].stateMachine.AddState(fixture.first, Vector3.zero);
            if (!crossController) fixture.controller.AddLayer(other.layers[0]);
            var context = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controllers = crossController ? new[] { context.Clone(fixture.controller), context.Clone(other) } : new[] { context.Clone(fixture.controller) };
            var before = controllers.SelectMany(c => c.Layers).Select(l => MotionAnalysis.Bindings(l.StateMachine.DefaultState.Motion)
                .Union(l.AllReachableNodes().OfType<VirtualState>().SelectMany(s => MotionAnalysis.Bindings(s.Motion))).ToHashSet()).ToArray();
            AnimatorOptimizer.Optimize(root, controllers, new() { Unreachable = true }, finalParameterDeclarations: true);
            var after = controllers.SelectMany(c => c.Layers).Select(l => l.AllReachableNodes().OfType<VirtualState>()
                .SelectMany(s => MotionAnalysis.Bindings(s.Motion)).ToHashSet()).ToArray();
            for (int i = 0; i < before.Length; i++) Assert.That(after[i].SetEquals(before[i]), Is.True);
        }

        [Test, Combinatorial]
        public void UnreachableCoveragePreservesGraphsObservedByUnknownInitialization(
            [Values(false, true)] bool crossController, [Values(false, true)] bool machineBehaviour)
        {
            var fixture = BindingCoverageFixture();
            var host = crossController ? Controller(Clip()) : fixture.controller;
            var nested = Track(host.layers[0].stateMachine.AddStateMachine("Opaque initialization"));
            nested.defaultState = Track(nested.AddState("Unreachable default"));
            nested.defaultState.motion = StaticCurve(Binding("opaque"), 20);
            var opaque = new[] { Track(ScriptableObject.CreateInstance<UnsupportedReachabilityBehaviour>()) };
            if (machineBehaviour) nested.behaviours = opaque;
            else nested.defaultState.behaviours = opaque;
            var context = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controllers = crossController ? new[] { context.Clone(fixture.controller), context.Clone(host) } : new[] { context.Clone(fixture.controller) };
            Assert.That(AnimatorReachability.Analyze(root, controllers).FallbackReason, Is.Null);
            var first = controllers[0].AllReachableNodes().OfType<VirtualState>().Single(s => s.Name == fixture.first.name);
            var before = first.Motion;
            AnimatorOptimizer.Optimize(root, controllers, new() { Unreachable = true }, finalParameterDeclarations: true);
            Assert.That(first.Motion, Is.SameAs(before));
            Assert.That(controllers[0].AllReachableNodes().OfType<VirtualState>().Any(s => s.Name == fixture.second.name), Is.True);
        }

        [Test]
        public void UnreachableCoverageRetainsExactLayerBindingsUnderOverlapsAndSharedMotions()
        {
            var random = new System.Random(781492);
            for (int iteration = 0; iteration < 32; iteration++)
            {
                var source = Controller(StaticCurve(Binding("live"), 40));
                var machine = source.layers[0].stateMachine;
                var shared = StaticCurve(Binding("shared"), 12);
                for (int i = 0; i < 8; i++)
                {
                    var clip = StaticCurve(Binding("unique" + i), i);
                    for (int j = 0; j < 7; j++) if (random.Next(2) == 0)
                        AnimationUtility.SetEditorCurve(clip, Binding("overlap" + j), AnimationCurve.Linear(0, i, 2, j));
                    var tree = Track(new BlendTree { blendType = BlendTreeType.Simple1D, blendParameter = "GestureLeftWeight" });
                    tree.children = new[] { new ChildMotion { motion = clip, timeScale = 1 },
                        new ChildMotion { motion = shared, timeScale = 1, threshold = 1 } };
                    Track(machine.AddState("Dead" + i)).motion = tree;
                }
                source.AddParameter("GestureLeftWeight", AnimatorControllerParameterType.Float);
                var active = Track(new BlendTree { blendType = BlendTreeType.Simple1D, blendParameter = "GestureLeftWeight" });
                active.children = new[] { new ChildMotion { motion = machine.defaultState.motion, timeScale = 1 },
                    new ChildMotion { motion = shared, timeScale = 1, threshold = 1 } };
                machine.defaultState.motion = active;
                var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
                var before = controller.AllReachableNodes().OfType<VirtualState>().SelectMany(s => MotionAnalysis.Bindings(s.Motion)).ToHashSet();
                var result = AnimatorOptimizer.Optimize(root, new[] { controller }, new() { Unreachable = true }, finalParameterDeclarations: true);
                var states = controller.AllReachableNodes().OfType<VirtualState>().ToArray();
                Assert.That(result.States, Is.EqualTo(7));
                Assert.That(states.SelectMany(s => MotionAnalysis.Bindings(s.Motion)).ToHashSet().SetEquals(before), Is.True);
                Assert.That(MotionAnalysis.Bindings(states.Single(s => s.Name == "Dead0").Motion).Contains(Binding("shared")), Is.False);
                Assert.That(controller.Layers.First().StateMachine.DefaultState.Motion, Is.TypeOf<VirtualBlendTree>());
                Assert.That(AnimationUtility.GetEditorCurve(shared, Binding("shared")).Evaluate(0), Is.EqualTo(12));
                TrackControllerAssets(CommitAnimator(controller, root));
            }
        }

        [Test]
        public void UnreachableCoverageSurvivesFinalSdkCommitAndAssetReload()
        {
            var fixture = BindingCoverageFixture();
            var expected = SampleBindingCoverage(fixture.controller);
            var folder = "Assets/ALTOBindingCoverage_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            try
            {
                var descriptor = LayoutDescriptor(); descriptor.customizeAnimationLayers = true;
                descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer {
                    type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fixture.controller, isDefault = false } };
                var context = new BuildContext(root, folder + "/Generated"); context.AssetSaver.Dispose();
                context.GetState<AnimatorOptimizationState>().Settings = new() { Unreachable = true };
                AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context); AvatarOutputSdkFinalizer.Complete(root);
                var built = (AnimatorController)descriptor.baseAnimationLayers.Single().animatorController;
                Assert.That(built.layers[0].stateMachine.states.Length, Is.EqualTo(3));
                AssetDatabase.SaveAssets();
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(built, out string _, out long id), Is.True);
                var path = folder + "/Reloaded.asset"; File.Copy(AssetDatabase.GetAssetPath(built), path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var reloaded = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimatorController>().Single(c =>
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(c, out string _, out long candidate) && candidate == id);
                var motion = reloaded.layers[0].stateMachine.states.Single(s => s.state.name == fixture.first.name).state.motion as AnimationClip;
                Assert.That(EditorUtility.IsPersistent(motion), Is.True);
                Assert.That(AnimationUtility.GetCurveBindings(motion).Length, Is.EqualTo(4));
                Assert.That(SampleBindingCoverage(reloaded), Is.EqualTo(expected).Within(2e-5));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
    }
}
