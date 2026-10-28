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
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private (AnimatorController source, AnimatorState first, AnimatorState second, BlendTree tree)
            DefaultTreeFixture(string kind, float firstWeight, float secondWeight, string output = "BlendShape")
        {
            Mesh("a");
            var writer = StaticCurve(Binding("a"), 70);
            if (output == "Scale")
                writer = StaticCurve(EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalScale.x"), 3);
            var source = Controller(writer);
            source.AddParameter("Gate", AnimatorControllerParameterType.Float);
            source.AddParameter(new AnimatorControllerParameter { name = "WeightA", type = AnimatorControllerParameterType.Float, defaultFloat = firstWeight });
            source.AddParameter(new AnimatorControllerParameter { name = "WeightB", type = AnimatorControllerParameterType.Float, defaultFloat = secondWeight });
            source.AddParameter(new AnimatorControllerParameter { name = "Position", type = AnimatorControllerParameterType.Float, defaultFloat = .4f });
            AnimationClip Duration(float length)
            {
                var clip = Clip();
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("AbsentDurationTarget", typeof(GameObject), "m_IsActive"),
                    AnimationCurve.Constant(0, length, 0));
                return clip;
            }
            var tree = Track(new BlendTree { name = "Default weights", blendType = kind == "1D" ? BlendTreeType.Simple1D : BlendTreeType.Direct,
                blendParameter = "Position", useAutomaticThresholds = false });
            tree.children = new[] {
                new ChildMotion { motion = Duration(.2f), directBlendParameter = "WeightA", timeScale = 1, threshold = 0 },
                new ChildMotion { motion = Duration(.8f), directBlendParameter = "WeightB", timeScale = 1, threshold = 1 }
            };
            using (var serialized = new SerializedObject(tree))
            { serialized.FindProperty("m_NormalizedBlendValues").boolValue = kind == "Normalized"; serialized.ApplyModifiedPropertiesWithoutUndo(); }
            if (kind == "Nested")
            {
                var nested = tree;
                tree = Track(new BlendTree { name = "Nested defaults", blendType = BlendTreeType.Direct });
                tree.children = new[] { new ChildMotion { motion = nested, directBlendParameter = "One", timeScale = 1 } };
                using (var serialized = new SerializedObject(tree))
                { serialized.FindProperty("m_NormalizedBlendValues").boolValue = false; serialized.ApplyModifiedPropertiesWithoutUndo(); }
                source.AddParameter(new AnimatorControllerParameter { name = "One", type = AnimatorControllerParameterType.Float, defaultFloat = 1 });
            }
            var machine = Track(new AnimatorStateMachine());
            var first = Track(machine.AddState("Clip defaults"));
            var second = Track(machine.AddState("Tree defaults"));
            first.motion = Duration(.1f); second.motion = tree;
            first.writeDefaultValues = second.writeDefaultValues = true; machine.defaultState = first;
            var toTree = Edge(first, second, "Gate", .5f, AnimatorConditionMode.Greater);
            var toClip = Edge(second, first, "Gate", .5f, AnimatorConditionMode.Less);
            toTree.duration = toClip.duration = .08f;
            var driverMachine = Track(new AnimatorStateMachine());
            var driver = Track(driverMachine.AddState("Condition producer")); driverMachine.defaultState = driver;
            var conditionClip = Clip();
            AnimationUtility.SetEditorCurve(conditionClip, ParameterBinding("Gate"), AnimationCurve.Linear(0, 0, 1, 1));
            driver.motion = conditionClip; driver.writeDefaultValues = false;
            source.layers = source.layers.Concat(new[] {
                new AnimatorControllerLayer { name = "Unused defaults", stateMachine = machine, defaultWeight = 1,
                    blendingMode = kind == "Additive" ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override },
                new AnimatorControllerLayer { name = "Condition producer", stateMachine = driverMachine, defaultWeight = 1 }
            }).ToArray();
            return (source, first, second, tree);
        }

        private (VirtualAnimatorController controller, AnimatorOptimizationReport report) OptimizeDefaultTree(
            AnimatorController source, IEnumerable<string> preserved = null)
        {
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var outputs = new ExpressionOutputUsage(root,
                new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller },
                new Dictionary<string, string>());
            var report = AnimatorOptimizer.Optimize(root, new[] { controller }, new() { StateLogic = true },
                preservedParameters: preserved, observableBinding: outputs.IsObservable);
            return (controller, report);
        }

        private static IEnumerable<TestCaseData> UniformDefaultCases()
        {
            foreach (string output in new[] { "BlendShape", "Scale", "Material" })
            foreach (var item in new[] { ("Direct", 1f, 0f), ("Direct", .25f, .75f),
                ("Direct", .25f, .25f), ("Direct", 1.5f, .5f), ("Direct", -1f, 2f),
                ("Normalized", 0f, 0f), ("Normalized", 0f, .5f), ("Normalized", float.MaxValue, float.MaxValue),
                ("Normalized", .5f, .5f), ("Normalized", 2f, 1f), ("1D", 0f, 0f), ("Nested", .25f, .75f), ("Additive", .25f, .75f) })
                yield return new TestCaseData(item.Item1, item.Item2, item.Item3, output);
        }

        [TestCaseSource(nameof(UniformDefaultCases))]
        public void UniformDefaultTreesCollapseWithNativeOutputPreserved(string kind, float firstWeight, float secondWeight, string output)
        {
            var fixture = DefaultTreeFixture(kind, firstWeight, secondWeight, output);
            Material first = null, second = null;
            if (output == "Material")
            {
                var shader = Shader.Find("Hidden/InternalErrorShader");
                Assert.That(shader, Is.Not.Null);
                first = Track(new Material(shader)); second = Track(new Material(shader));
                renderer.sharedMaterial = first;
                var clip = (AnimationClip)fixture.source.layers[0].stateMachine.defaultState.motion;
                AnimationUtility.SetObjectReferenceCurve(clip,
                    EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = second } });
            }
            float Sample() => output == "Scale" ? root.transform.localScale.x :
                output == "Material" ? (renderer.sharedMaterial == second ? 1 : 0) : renderer.GetBlendShapeWeight(0);
            bool visitedTree = false;
            void Inputs(UnityEngine.Animations.AnimatorControllerPlayable playable, int frame)
            {
                visitedTree |= playable.GetCurrentAnimatorStateInfo(1).shortNameHash == Animator.StringToHash("Tree defaults");
                playable.SetFloat("Gate", frame % 20 < 10 ? 0 : 1);
            }
            root.transform.localScale = Vector3.one;
            var expected = SampleFrozen(fixture.source, Inputs, 120, Sample);
            Assert.That(visitedTree, Is.True, "The native baseline must exercise the tree state");
            var optimized = OptimizeDefaultTree(fixture.source);
            Assert.That(optimized.report.StateMachines, Is.EqualTo(1));
            Assert.That(optimized.report.States, Is.EqualTo(1));
            Assert.That(optimized.report.ParameterCurves, Is.EqualTo(1), "The condition's unused producer should also be removed");
            root.transform.localScale = Vector3.one;
            if (first != null) renderer.sharedMaterial = first;
            var actual = SampleFrozen(CommitAnimator(optimized.controller, root), Inputs, 120, Sample);
            Assert.That(actual, Is.EqualTo(expected).Within(1e-5));
            Assert.That(fixture.source.layers[1].stateMachine.states.Length, Is.EqualTo(2));
        }

        [TestCase("MixedDefaults")] [TestCase("NullChild")] [TestCase("Mirror")]
        [TestCase("ObservableChild")]
        [TestCase("ExistingTarget")] [TestCase("Event")]
        public void UnprovenDefaultTreesKeepTheirStateLogicAndConditionProducer(string reason)
        {
            var fixture = DefaultTreeFixture("Direct", .5f, .5f);
            if (reason == "MixedDefaults") fixture.second.writeDefaultValues = false;
            var children = fixture.tree.children;
            if (reason == "NullChild") children[0].motion = null;
            if (reason == "Mirror") children[0].mirror = true;
            if (reason == "ObservableChild") children[0].motion = StaticCurve(Binding("a"), 5);
            if (reason == "ExistingTarget") Track(new GameObject("AbsentDurationTarget")).transform.SetParent(root.transform, false);
            if (reason == "Event") AnimationUtility.SetAnimationEvents((AnimationClip)children[0].motion,
                new[] { new AnimationEvent { functionName = "Observe" } });
            fixture.tree.children = children;
            var optimized = OptimizeDefaultTree(fixture.source);
            Assert.That(optimized.report.StateMachines, Is.Zero);
            Assert.That(optimized.report.ParameterCurves, Is.Zero);
            Assert.That(optimized.controller.Layers.ElementAt(1).StateMachine.States.Count, Is.EqualTo(2));
        }

        [Test]
        public void ImplicitDefaultsDoNotDependOnOtherControllersWeightDefaults()
        {
            var fixture = DefaultTreeFixture("Direct", .5f, .5f);
            var other = Controller(Clip());
            other.AddParameter(new AnimatorControllerParameter { name = "WeightA", type = AnimatorControllerParameterType.Float, defaultFloat = 0 });
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controllers = new[] { clone.Clone(fixture.source), clone.Clone(other) };
            var outputs = new ExpressionOutputUsage(root, new Dictionary<object, VirtualAnimatorController> {
                [VRCAvatarDescriptor.AnimLayerType.FX] = controllers[0],
                [VRCAvatarDescriptor.AnimLayerType.Action] = controllers[1]
            }, new Dictionary<string, string>());
            var report = AnimatorOptimizer.Optimize(root, controllers, new() { StateLogic = true }, observableBinding: outputs.IsObservable);
            Assert.That(report.StateMachines, Is.EqualTo(1));
            Assert.That(report.ParameterCurves, Is.EqualTo(1));
        }

        [TestCase("WeightA")] [TestCase("Gate")]
        public void PreservedValuesDoNotRetainTheirEffectFreeStateConsumers(string preserved)
        {
            var fixture = DefaultTreeFixture("Direct", .5f, .5f);
            float parameter = 0;
            void Inputs(UnityEngine.Animations.AnimatorControllerPlayable playable, int frame)
            {
                parameter = playable.GetFloat(preserved);
                playable.SetFloat("WeightA", frame % 8 * .2f);
            }
            var expected = SampleFrozen(fixture.source, Inputs, 120, () => parameter);
            var result = OptimizeDefaultTree(fixture.source, new[] { preserved });
            Assert.That(result.report.StateMachines, Is.EqualTo(1));
            Assert.That(result.report.ParameterCurves, Is.EqualTo(preserved == "Gate" ? 0 : 1));
            Assert.That(result.controller.Parameters.ContainsKey(preserved), Is.True);
            var actual = SampleFrozen(CommitAnimator(result.controller, root), Inputs, 120, () => parameter);
            Assert.That(actual, Is.EqualTo(expected).Within(1e-5));
        }

        [Test]
        public void SharedEmptyClipsDoNotMakePreservationRetainUnrelatedStateLogic()
        {
            var fixture = DefaultTreeFixture("Direct", .5f, .5f);
            fixture.source.AddParameter("VisibleInput", AnimatorControllerParameterType.Float);
            var live = Track(new BlendTree { blendType = BlendTreeType.Direct });
            live.children = new[] {
                new ChildMotion { motion = StaticCurve(Binding("a"), 70), directBlendParameter = "VisibleInput", timeScale = 1 },
                new ChildMotion { motion = fixture.first.motion, directBlendParameter = "VisibleInput", timeScale = 1 }
            };
            fixture.source.layers[0].stateMachine.defaultState.motion = live;
            root.AddComponent<VRCAvatarDescriptor>();
            FreezeExpressions(new VRCExpressionParameters.Parameter { name = "VisibleInput", valueType = VRCExpressionParameters.ValueType.Float });
            void Inputs(UnityEngine.Animations.AnimatorControllerPlayable playable, int frame) => playable.SetFloat("VisibleInput", frame % 8 * .15f);
            var expected = SampleFrozen(fixture.source, Inputs, 120);
            var result = OptimizeDefaultTree(fixture.source, new[] { "VisibleInput" });
            Assert.That(result.report.StateMachines, Is.EqualTo(1));
            Assert.That(result.report.ParameterCurves, Is.EqualTo(1));
            var actual = SampleFrozen(CommitAnimator(result.controller, root), Inputs, 120);
            Assert.That(actual, Is.EqualTo(expected).Within(1e-5));
        }

        [Test]
        public void DefaultWeightProofRejectsCyclicMotionGraphs()
        {
            var fixture = DefaultTreeFixture("Direct", .5f, .5f);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var analysis = AnimatorReachability.Analyze(root, new[] { controller });
            var state = controller.Layers.ElementAt(1).StateMachine.States.Single(s => s.State.Name == "Tree defaults").State;
            var tree = (VirtualBlendTree)state.Motion;
            var child = tree.Children[0]; child.Motion = tree;
            tree.Children = tree.Children.SetItem(0, child);
            Assert.That(new DefaultWriteAnalysis().Uniform(new[] { state }), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void EmptyStateLogicCollapsesWithoutChangingOtherLayerOutput(bool defaults)
        {
            Mesh("a");
            var source = Controller(StaticCurve(Binding("a"), 70));
            source.AddParameter("Toggle", AnimatorControllerParameterType.Bool);
            var machine = Track(new AnimatorStateMachine());
            var first = Track(machine.AddState("Off")); var second = Track(machine.AddState("On"));
            machine.defaultState = first; first.motion = Clip(); second.motion = Clip();
            first.writeDefaultValues = second.writeDefaultValues = defaults;
            Edge(first, second, "Toggle", 0, AnimatorConditionMode.If);
            Edge(second, first, "Toggle", 0, AnimatorConditionMode.IfNot);
            source.layers = source.layers.Concat(new[] { new AnimatorControllerLayer { name = "Unused", stateMachine = machine, defaultWeight = 1 } }).ToArray();
            var expected = SampleFrozen(source, (p, i) => p.SetBool("Toggle", i % 8 > 3), 32);
            var result = OptimizeAnimator(source, new() { StateLogic = true });
            Assert.That(result.report.StateMachines, Is.EqualTo(1));
            Assert.That(result.report.States, Is.EqualTo(1));
            Assert.That(result.report.Transitions, Is.EqualTo(2));
            Assert.That(result.controller.Layers.Count(), Is.EqualTo(2));
            Assert.That(result.controller.Layers.Last().StateMachine.DefaultState.Name, Is.EqualTo("Off"));
            Assert.That(result.controller.Layers.Last().StateMachine.DefaultState.Motion, Is.Not.Null);
            renderer.SetBlendShapeWeight(0, 0);
            var actual = SampleFrozen(CommitAnimator(result.controller, root), (p, i) => p.SetBool("Toggle", i % 8 > 3), 32);
            Assert.That(actual, Is.EqualTo(expected).Within(1e-5));
            Assert.That(machine.states.Length, Is.EqualTo(2), "The source asset is not edited");
            Assert.That(AnimatorOptimizer.Optimize(root, new[] { result.controller }, new() { StateLogic = true }).StateMachines, Is.Zero);
        }

        [TestCase("MixedDefaults")] [TestCase("NullMotion")]
        [TestCase("TrackingBehaviour")] [TestCase("Event")]
        public void UnusedStateLogicRetainsImplicitOrExternalEffects(string reason)
        {
            var source = Controller(Clip(), Clip()); source.AddParameter("Toggle", AnimatorControllerParameterType.Bool);
            var states = source.layers[0].stateMachine.states.Select(s => s.state).ToArray();
            Edge(states[0], states[1], "Toggle", 0, AnimatorConditionMode.If);
            if (reason == "MixedDefaults") states[1].writeDefaultValues = false;
            if (reason == "NullMotion") states[1].motion = null;
            if (reason == "TrackingBehaviour") Track(states[1].AddStateMachineBehaviour<VRCAnimatorTrackingControl>());
            if (reason == "Event") AnimationUtility.SetAnimationEvents((AnimationClip)states[1].motion, new[] { new AnimationEvent { functionName = "Observe" } });
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var result = AnimatorOptimizer.Optimize(root, new[] { clone }, new() { StateLogic = true });
            Assert.That(result.StateMachines, Is.Zero);
            Assert.That(clone.Layers.First().StateMachine.States.Count, Is.EqualTo(2));
        }

        [TestCase(false)] [TestCase(true)]
        public void StateOutputLivenessDistinguishesDeadDriverCyclesFromNetworkOutputs(bool network)
        {
            if (network)
            {
                root.AddComponent<VRCAvatarDescriptor>();
                FreezeExpressions(new VRCExpressionParameters.Parameter { name = "Output", networkSynced = true });
            }
            var source = Controller(Clip(), Clip());
            source.AddParameter("Input", AnimatorControllerParameterType.Float);
            source.AddParameter("Output", AnimatorControllerParameterType.Float);
            var states = source.layers[0].stateMachine.states.Select(s => s.state).ToArray();
            Edge(states[0], states[1], "Input", 0.5f, AnimatorConditionMode.Greater);
            Driver(states[0], "Input", 0, VRC_AvatarParameterDriver.ChangeType.Copy, "Output");
            Driver(states[1], "Output", 0, VRC_AvatarParameterDriver.ChangeType.Copy, "Input");
            var result = OptimizeAnimator(source, new() { StateLogic = true });
            Assert.That(result.report.DriverOperations, Is.EqualTo(network ? 0 : 2));
            Assert.That(result.report.StateMachines, Is.EqualTo(network ? 0 : 1));
            Assert.That(result.controller.Parameters.Keys, Is.EquivalentTo(new[] { "Input", "Output" }));
        }

        [Test]
        public void ImplicitDefaultsProtectConditionsEvenWhenOnlyParameterOutputsExist()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            FreezeExpressions(new VRCExpressionParameters.Parameter { name = "Output", networkSynced = true });
            var source = Controller(StaticCurve(ParameterBinding("Output"), 1));
            source.AddParameter("Output", AnimatorControllerParameterType.Float);
            source.AddParameter("Gate", AnimatorControllerParameterType.Float);
            var machine = Track(new AnimatorStateMachine());
            var first = Track(machine.AddState("Defaults")); var second = Track(machine.AddState("No defaults"));
            first.motion = Clip(); second.motion = Clip(); second.writeDefaultValues = false; machine.defaultState = first;
            Edge(first, second, "Gate", 0.5f, AnimatorConditionMode.Greater);
            var writer = Track(new AnimatorStateMachine()); var write = Track(writer.AddState("Writer")); writer.defaultState = write;
            write.motion = StaticCurve(ParameterBinding("Gate"), 1);
            source.layers = source.layers.Concat(new[] {
                new AnimatorControllerLayer { name = "Implicit defaults", stateMachine = machine, defaultWeight = 1 },
                new AnimatorControllerLayer { name = "Writer", stateMachine = writer, defaultWeight = 1 }
            }).ToArray();
            var result = OptimizeAnimator(source, new() { StateLogic = true });
            Assert.That(result.report.ParameterCurves, Is.Zero);
            Assert.That(result.report.StateMachines, Is.Zero);
        }

        [Test]
        public void RandomDriverExecutionKeepsItsStateConditionCalculations()
        {
            var source = Controller(StaticCurve(ParameterBinding("Condition"), 1), Clip());
            source.AddParameter("Condition", AnimatorControllerParameterType.Float);
            source.AddParameter("RandomOutput", AnimatorControllerParameterType.Float);
            var states = source.layers[0].stateMachine.states.Select(s => s.state).ToArray();
            Edge(states[0], states[1], "Condition", 0.5f, AnimatorConditionMode.Greater);
            Driver(states[1], "RandomOutput", 1, VRC_AvatarParameterDriver.ChangeType.Random);
            var result = OptimizeAnimator(source, new() { StateLogic = true });
            Assert.That(result.report.ParameterCurves, Is.Zero);
            Assert.That(result.report.DriverOperations, Is.Zero);
            Assert.That(result.report.StateMachines, Is.Zero);
        }
    }
}
