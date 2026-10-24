using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private (AnimatorController controller, AnimatorState idle, AnimatorState face) ReachabilityFixture(string parameter = "Mode")
        {
            var idleClip = Clip(); idleClip.name = "Idle";
            var faceClip = Clip("a"); faceClip.name = "Expression";
            AnimationUtility.SetEditorCurve(faceClip, Binding("a"), AnimationCurve.Constant(0, 4, 80));
            var controller = Controller(idleClip, faceClip);
            controller.parameters = new[] { new AnimatorControllerParameter { name = parameter, type = AnimatorControllerParameterType.Int } };
            return (controller, controller.layers[0].stateMachine.states[0].state, controller.layers[0].stateMachine.states[1].state);
        }

        private AnimatorStateTransition Edge(AnimatorState from, AnimatorState to, string parameter = null, float threshold = 1,
            AnimatorConditionMode mode = AnimatorConditionMode.Equals)
        {
            var transition = Track(from.AddTransition(to)); transition.hasExitTime = false;
            if (parameter != null) transition.AddCondition(mode, threshold, parameter);
            return transition;
        }

        private VRCAvatarParameterDriver Driver(AnimatorState state, string name, float value,
            VRC_AvatarParameterDriver.ChangeType operation = VRC_AvatarParameterDriver.ChangeType.Set, string source = null)
        {
            var driver = Track(state.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
            driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter { name = name, value = value, type = operation, source = source });
            return driver;
        }

        private (AnimatorReachability analysis, VirtualAnimatorController controller) Analyze(AnimatorController controller,
            params AnimatorController[] others)
        {
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controllers = new[] { controller }.Concat(others).Select(c => clone.Clone(c)).ToArray();
            return (AnimatorReachability.Analyze(root, controllers), controllers[0]);
        }

        private static bool HasExpression((AnimatorReachability analysis, VirtualAnimatorController controller) result)
            => result.analysis.Clips(result.controller).Any(c => c.Name == "Expression");

        [Test]
        public void MutedStateCanFreezeOnlyWhenTheOptionIsEnabledAndSourcesStayIntact()
        {
            Mesh("a"); renderer.SetBlendShapeWeight(0, 40);
            var fixture = ReachabilityFixture(); Edge(fixture.idle, fixture.face).mute = true;
            var result = Analyze(fixture.controller);
            Assert.That(HasExpression(result), Is.False);
            var usage = new AnimationUsage();
            usage.AddController(root.transform, root.transform, result.controller, reachableClips: result.analysis.Clips(result.controller));
            Assert.That(OptimizationPlan.Create(renderer, usage, true, new HashSet<string>()).HasChanges, Is.False);
            var plan = OptimizationPlan.Create(renderer, usage, true, new HashSet<string>(), ignoreUnreachableStates: true);
            Assert.That(plan.Frozen.Select(s => s.Name), Is.EqualTo(new[] { "a" }));
            plan.RewriteCurves();
            var dead = result.controller.AllReachableNodes().OfType<VirtualClip>().Single(c => c.Name == "Expression");
            Assert.That(dead.GetFloatCurve(Binding("a")), Is.Null);
            Assert.That(dead.GetFloatCurveBindings().SelectMany(b => dead.GetFloatCurve(b).keys).Max(k => k.time), Is.EqualTo(4));
            Assert.That(result.controller.AllReachableNodes().OfType<VirtualState>().Count(), Is.EqualTo(2));
            Assert.That(AnimationUtility.GetEditorCurve((AnimationClip)fixture.face.motion, Binding("a")).Evaluate(0), Is.EqualTo(80));
        }

        [Test]
        public void DefaultStateAndAlternativeLiveReferencesAreKeptDespiteMutedEdges()
        {
            var fixture = ReachabilityFixture(); Edge(fixture.idle, fixture.face).mute = true;
            fixture.controller.layers[0].stateMachine.defaultState = fixture.face;
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.True);
            fixture.controller.layers[0].stateMachine.defaultState = fixture.idle;
            Edge(fixture.idle, fixture.face);
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.True);
            fixture.idle.transitions = Array.Empty<AnimatorStateTransition>();
            fixture.idle.motion = fixture.face.motion;
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void SoloFiltersSiblingTransitionsAndMuteTakesPrecedence(bool muteSolo)
        {
            var fixture = ReachabilityFixture();
            Edge(fixture.idle, fixture.face);
            var solo = Edge(fixture.idle, fixture.idle); solo.solo = true; solo.mute = muteSolo;
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.False);
        }

        [Test]
        public void UnreachableDriverDoesNotMakeItsOwnEntryConditionPossible()
        {
            var fixture = ReachabilityFixture(); Edge(fixture.idle, fixture.face, "Mode"); Driver(fixture.face, "Mode", 1);
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.False);
        }

        [TestCase(1, false)] [TestCase(2, true)]
        public void ReachableAssignmentsPreserveFiniteValueSets(float condition, bool expected)
        {
            var fixture = ReachabilityFixture(); Edge(fixture.idle, fixture.face, "Mode", condition); Driver(fixture.idle, "Mode", 2);
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.EqualTo(expected));
        }

        [Test]
        public void CopyDependenciesAreRevisitedWhenAnotherControllerWritesTheSource()
        {
            var fixture = ReachabilityFixture(); Edge(fixture.idle, fixture.face, "Mode");
            fixture.controller.AddParameter("Source", AnimatorControllerParameterType.Int);
            Driver(fixture.idle, "Mode", 0, VRC_AvatarParameterDriver.ChangeType.Copy, "Source");
            var writer = ReachabilityFixture("Source"); Driver(writer.idle, "Source", 1);
            Assert.That(HasExpression(Analyze(fixture.controller, writer.controller)), Is.True);
        }

        [Test]
        public void RandomRangesCanProveAConditionImpossibleButAddIsConservative()
        {
            var fixture = ReachabilityFixture(); Edge(fixture.idle, fixture.face, "Mode", 1);
            var driver = Driver(fixture.idle, "Mode", 1, VRC_AvatarParameterDriver.ChangeType.Random);
            driver.parameters[0].valueMin = 3; driver.parameters[0].valueMax = 5;
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.False);
            driver.parameters[0].type = VRC_AvatarParameterDriver.ChangeType.Add;
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.True);
        }

        [TestCase("Expression")] [TestCase("Contact")] [TestCase("PhysBone")] [TestCase("BuiltIn")] [TestCase("Curve")]
        public void RuntimeWritersPreventDefaultValuesFromBeingTreatedAsConstants(string writer)
        {
            string parameter = writer == "BuiltIn" ? "GestureLeft" : writer == "PhysBone" ? "Tail_IsGrabbed" : "Mode";
            var fixture = ReachabilityFixture(parameter); Edge(fixture.idle, fixture.face, parameter);
            if (writer == "Expression")
            {
                var descriptor = root.AddComponent<VRCAvatarDescriptor>();
                descriptor.expressionParameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
                descriptor.expressionParameters.parameters = new[] { new VRCExpressionParameters.Parameter {
                    name = parameter, valueType = VRCExpressionParameters.ValueType.Int, saved = true } };
            }
            if (writer == "Contact") root.AddComponent<VRCContactReceiver>().parameter = parameter;
            if (writer == "PhysBone") root.AddComponent<VRCPhysBone>().parameter = "Tail";
            if (writer == "Curve") AnimationUtility.SetEditorCurve((AnimationClip)fixture.idle.motion,
                EditorCurveBinding.FloatCurve("", typeof(Animator), parameter), AnimationCurve.Linear(0, 0, 1, 2));
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.True);
        }

        [Test]
        public void ContradictoryConditionsRemainImpossibleForExternalInputs()
        {
            var fixture = ReachabilityFixture("GestureLeft");
            var transition = Edge(fixture.idle, fixture.face, "GestureLeft", 1);
            transition.AddCondition(AnimatorConditionMode.Equals, 2, "GestureLeft");
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.False);
            transition.conditions = Array.Empty<AnimatorCondition>();
            transition.AddCondition(AnimatorConditionMode.Greater, 10, "GestureLeft");
            transition.AddCondition(AnimatorConditionMode.Less, 0, "GestureLeft");
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.False);
        }

        [Test]
        public void CurvesInDeadStatesDoNotCreateSpuriousParameterWriters()
        {
            var fixture = ReachabilityFixture(); Edge(fixture.idle, fixture.face, "Mode");
            AnimationUtility.SetEditorCurve((AnimationClip)fixture.face.motion,
                EditorCurveBinding.FloatCurve("", typeof(Animator), "Mode"), AnimationCurve.Constant(0, 1, 1));
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.False);
        }

        [Test]
        public void ZeroWeightLayerDriversStillParticipateInAnalysis()
        {
            var fixture = ReachabilityFixture(); Edge(fixture.idle, fixture.face, "Mode");
            var machine = Track(new AnimatorStateMachine());
            var state = Track(machine.AddState("Writer")); machine.defaultState = state; Driver(state, "Mode", 1);
            fixture.controller.layers = fixture.controller.layers.Concat(new[] {
                new AnimatorControllerLayer { name = "Writer", defaultWeight = 0, stateMachine = machine } }).ToArray();
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.True);
        }

        [Test]
        public void NestedEntryExitAndAncestorAnyStatePathsAreIncluded()
        {
            var fixture = ReachabilityFixture(); var machine = fixture.controller.layers[0].stateMachine;
            var nested = Track(machine.AddStateMachine("Nested"));
            var nestedIdle = Track(nested.AddState("Nested Idle")); nested.defaultState = nestedIdle;
            var entry = Track(fixture.idle.AddTransition(nested)); entry.hasExitTime = false;
            var exit = Track(nestedIdle.AddExitTransition()); exit.hasExitTime = false;
            Track(machine.AddStateMachineTransition(nested, fixture.face));
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.True);
            machine.SetStateMachineTransitions(nested, Array.Empty<AnimatorTransition>());
            var any = Track(machine.AddAnyStateTransition(fixture.face)); any.hasExitTime = false;
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.True);
        }

        [Test]
        public void UnsupportedReachableBehavioursPreserveAllControllerReferences()
        {
            var fixture = ReachabilityFixture();
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.controller);
            var state = controller.Layers.First().StateMachine.DefaultState;
            state.Behaviours = state.Behaviours.Add(Track(ScriptableObject.CreateInstance<UnsupportedReachabilityBehaviour>()));
            var result = (analysis: AnimatorReachability.Analyze(root, new[] { controller }), controller);
            Assert.That(HasExpression(result), Is.True);
            Assert.That(result.analysis.FallbackReason, Is.Not.Null);
        }

        [Test]
        public void SharedClipAtALiveControllerRootIsNotClassifiedAsUnreachable()
        {
            Mesh("a");
            var fixture = ReachabilityFixture(); var active = Controller((AnimationClip)fixture.face.motion);
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var dead = clone.Clone(fixture.controller); var liveController = clone.Clone(active);
            var analysis = AnimatorReachability.Analyze(root, new[] { dead, liveController });
            var usage = new AnimationUsage();
            usage.AddController(root.transform, root.transform, dead, reachableClips: analysis.Clips(dead));
            usage.AddController(root.transform, root.transform, liveController, reachableClips: analysis.Clips(liveController));
            Assert.That(OptimizationPlan.Create(renderer, usage, true, new HashSet<string>(), ignoreUnreachableStates: true).HasChanges, Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void IgnoringDeadClipsPreservesWriteDefaultsAcrossLayers(bool identicalCoverage)
        {
            Mesh("a", "b"); renderer.SetBlendShapeWeight(0, 20); renderer.SetBlendShapeWeight(1, 60);
            var controller = Controller(Clip("a", "b"));
            controller.AddParameter("P", AnimatorControllerParameterType.Float); controller.AddParameter("Q", AnimatorControllerParameterType.Float);
            var upper = Track(new AnimatorStateMachine());
            var idle = Track(upper.AddState("Idle")); idle.motion = Clip(); idle.writeDefaultValues = true; upper.defaultState = idle;
            foreach (string shape in identicalCoverage ? new[] { "a", "b" } : new[] { "a" })
            {
                var state = Track(upper.AddState("Dead " + shape)); var clip = Clip(shape); state.motion = clip;
                AnimationUtility.SetEditorCurve(clip, Binding(shape), AnimationCurve.Constant(0, 1, 90));
            }
            controller.layers = controller.layers.Concat(new[] { new AnimatorControllerLayer {
                name = "Upper", defaultWeight = 1, stateMachine = upper } }).ToArray();
            var result = Analyze(controller); var usage = new AnimationUsage();
            usage.AddController(root.transform, root.transform, result.controller, reachableClips: result.analysis.Clips(result.controller));
            var plan = OptimizationPlan.Create(renderer, usage, true, new HashSet<string>(), ignoreUnreachableStates: true);
            Assert.That(plan.Groups.Count, Is.EqualTo(identicalCoverage ? 1 : 2));
            var outputMesh = Track(MeshOptimization.Generate(plan, MeshOptimization.CreateRecipe(plan)));
            float before = Evaluate(root, renderer, controller, Vector2.zero);
            MeshOptimization.Apply(plan, outputMesh); plan.RewriteCurves();
            var animator = root.AddComponent<Animator>();
            var context = new BuildContext(root, null);
            context.ActivateExtensionContextRecursive<VirtualControllerContext>().Controllers[animator] = result.controller;
            context.DeactivateExtensionContext<VirtualControllerContext>();
            var output = (AnimatorController)animator.runtimeAnimatorController; TrackControllerAssets(output);
            UnityEngine.Object.DestroyImmediate(animator);
            Assert.That(Evaluate(root, renderer, output, Vector2.zero), Is.EqualTo(before).Within(1e-5));
        }
    }
}
