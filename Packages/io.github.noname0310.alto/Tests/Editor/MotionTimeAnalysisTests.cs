using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Expressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private (AnimatorController controller, BlendTree dead, AnimationClip live) TimedConstantTree()
        {
            Mesh("a");
            var missing = StaticCurve(EditorCurveBinding.FloatCurve("Absent target", typeof(GameObject), "m_IsActive"), 0);
            AnimationUtility.SetEditorCurve(missing,
                EditorCurveBinding.FloatCurve("Absent target", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 2.5f, 0));
            var dead = UnobservedNestedTree(missing);
            var live = StaticCurve(Binding("a"), 60);
            AnimationUtility.SetEditorCurve(live, Binding("a"), AnimationCurve.Constant(0, 1, 60));
            var source = TreeController(("P", live), ("Q", dead));
            source.AddParameter("NestedLeft", AnimatorControllerParameterType.Float);
            source.AddParameter("NestedRight", AnimatorControllerParameterType.Float);
            return (source, dead, live);
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantPosesWithUnobservedDurationLoseDeadTreesAndPreserveDynamicWeights(bool fixedTimeTransitions)
        {
            var fixture = TimedConstantTree();
            if (fixedTimeTransitions)
            {
                var machine = fixture.controller.layers[0].stateMachine;
                var second = Track(machine.AddState("Other constant pose"));
                second.writeDefaultValues = true;
                second.motion = StaticCurve(Binding("a"), 20);
                fixture.controller.AddParameter("Gate", AnimatorControllerParameterType.Float);
                var first = machine.defaultState;
                var a = Edge(first, second, "Gate", .5f, AnimatorConditionMode.Greater);
                var b = Edge(second, first, "Gate", .5f, AnimatorConditionMode.Less);
                a.hasExitTime = b.hasExitTime = false;
                a.hasFixedDuration = b.hasFixedDuration = true;
                a.duration = b.duration = .08f;
            }
            var random = new System.Random(48371);
            var weights = Enumerable.Range(0, 240).Select(_ => new Vector2((float)random.NextDouble() * 1.8f,
                (float)random.NextDouble())).ToArray();
            void Inputs(UnityEngine.Animations.AnimatorControllerPlayable playable, int frame)
            {
                playable.SetFloat("P", weights[frame].x); playable.SetFloat("Q", weights[frame].y);
                playable.SetFloat("NestedLeft", weights[frame].y); playable.SetFloat("NestedRight", 1 - weights[frame].y);
                if (fixedTimeTransitions) playable.SetFloat("Gate", frame % 60 < 30 ? 0 : 1);
            }
            renderer.SetBlendShapeWeight(0, 0);
            var expected = SampleFrozen(fixture.controller, Inputs, weights.Length, () => renderer.GetBlendShapeWeight(0));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.controller);
            var report = AnimatorOptimizer.Optimize(root, new[] { controller }, new() { UnusedParameters = true, StateLogic = true },
                observableBinding: (_, binding) => binding.path != "Absent target");
            Assert.That(report.Branches, Is.EqualTo(1));
            Assert.That(controller.AllReachableNodes().OfType<VirtualBlendTree>().Any(t => t.Name == fixture.dead.name), Is.False);
            renderer.SetBlendShapeWeight(0, 0);
            var actual = SampleFrozen(CommitAnimator(controller, root), Inputs, weights.Length, () => renderer.GetBlendShapeWeight(0));
            Assert.That(actual, Is.EqualTo(expected).Within(1e-5));
            Assert.That(((BlendTree)fixture.controller.layers[0].stateMachine.defaultState.motion).children.Length, Is.EqualTo(2));
        }

        [TestCase("ExitTime")] [TestCase("NormalizedTransition")] [TestCase("VariableSibling")]
        [TestCase("Behaviour")] [TestCase("SyncedSource")] [TestCase("SharedTimedOwner")]
        public void DurationSensitiveContextsRetainConstantDeadTrees(string reason)
        {
            var fixture = TimedConstantTree();
            var source = fixture.controller;
            var machine = source.layers[0].stateMachine;
            var state = machine.defaultState;
            if (reason == "VariableSibling")
                AnimationUtility.SetEditorCurve(fixture.live, Binding("a"), AnimationCurve.Linear(0, 0, 1, 100));
            if (reason == "Behaviour") Driver(state, "P", 1);
            if (reason == "SyncedSource" || reason == "SharedTimedOwner")
            {
                var other = Track(new AnimatorStateMachine());
                var owner = Track(other.AddState("Shared owner")); owner.motion = state.motion; owner.writeDefaultValues = true;
                if (reason == "SharedTimedOwner")
                {
                    var exit = owner.AddExitTransition(); exit.hasExitTime = true; exit.exitTime = .5f;
                }
                source.AddLayer(new AnimatorControllerLayer { name = "Other", stateMachine = other, defaultWeight = 1,
                    syncedLayerIndex = reason == "SyncedSource" ? 0 : -1 });
            }
            if (reason == "ExitTime" || reason == "NormalizedTransition")
            {
                var other = Track(machine.AddState("Other")); other.motion = fixture.live; other.writeDefaultValues = true;
                var transition = state.AddTransition(other);
                transition.hasExitTime = reason == "ExitTime"; transition.exitTime = .5f;
                transition.hasFixedDuration = reason != "NormalizedTransition"; transition.duration = .2f;
            }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            AnimatorOptimizer.Optimize(root, new[] { controller }, new() { UnusedParameters = true, StateLogic = true },
                observableBinding: (_, binding) => binding.path != "Absent target");
            Assert.That(controller.AllReachableNodes().OfType<VirtualBlendTree>().Any(t => t.Name == fixture.dead.name), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void MissingTargetsArePrunedOnlyAtFinalBoundaryWithTimingPreserved(bool final)
        {
            Mesh("a");
            var present = Track(new GameObject("Present")); present.transform.SetParent(root.transform, false);
            var clip = StaticCurve(Binding("a"), 20);
            var absentComponent = EditorCurveBinding.FloatCurve("Present", typeof(Light), "m_Intensity");
            var absentObject = EditorCurveBinding.PPtrCurve("Absent", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]");
            AnimationUtility.SetEditorCurve(clip, absentComponent, AnimationCurve.Constant(0, 2, 1));
            AnimationUtility.SetObjectReferenceCurve(clip, absentObject, new[] { new ObjectReferenceKeyframe { time = 3, value = null } });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var outputs = new ExpressionOutputUsage(root, entries, null);
            var report = AnimatorOptimizer.Optimize(root, new[] { controller }, new() { StateLogic = true },
                observableBinding: outputs.IsObservable, controllerEntries: entries, finalParameterDeclarations: final);
            var result = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(report.MissingBindings, Is.EqualTo(final ? 2 : 0));
            Assert.That(MotionAnalysis.EndTime(result), Is.EqualTo(3));
            Assert.That(result.GetFloatCurve(Binding("a")).Evaluate(0), Is.EqualTo(20));
            if (final)
            {
                Assert.That(result.GetFloatCurveBindings(), Has.No.Member(absentComponent));
                Assert.That(result.GetObjectCurveBindings(), Is.Empty);
                var repeat = AnimatorOptimizer.Optimize(root, new[] { controller }, new() { StateLogic = true },
                    observableBinding: outputs.IsObservable, controllerEntries: entries, finalParameterDeclarations: true);
                Assert.That(repeat.Changes, Is.Zero, "Duration placeholders must not keep the fixed point alive.");
            }
            Assert.That(AnimationUtility.GetObjectReferenceCurveBindings(clip), Has.Member(absentObject));
        }
    }
}
