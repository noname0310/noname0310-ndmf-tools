using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
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
        private (AnimatorController Controller, AnimatorStateMachine Machine, AnimatorState Output, AnimatorStateTransition Any)
            VisibilityOverride(EditorCurveBinding binding, bool initial, bool writeDefaults)
        {
            var lower = StaticCurve(binding, initial ? 0 : 1);
            AnimationUtility.SetEditorCurve(lower, Binding("a"), AnimationCurve.Linear(0, 0, 1, 100));
            var source = Controller(lower); source.AddParameter("Gate", AnimatorControllerParameterType.Float);
            var machine = Track(new AnimatorStateMachine());
            var empty = Track(new AnimatorState { name = "Initial", motion = Clip(), writeDefaultValues = writeDefaults });
            var output = Track(new AnimatorState { name = "Fixed output", motion = StaticCurve(binding, initial ? 1 : 0), writeDefaultValues = writeDefaults });
            machine.states = new[] { new ChildAnimatorState { state = empty }, new ChildAnimatorState { state = output } };
            machine.defaultState = empty;
            source.layers = source.layers.Append(new AnimatorControllerLayer { name = "Overlay", defaultWeight = 1, stateMachine = machine }).ToArray();
            var any = Track(machine.AddAnyStateTransition(output)); any.hasExitTime = false; any.duration = 0; any.canTransitionToSelf = false;
            any.AddCondition(AnimatorConditionMode.Less, 0.995f, "Gate");
            return (source, machine, output, any);
        }

        [TestCase(false, false, false)] [TestCase(false, false, true)]
        [TestCase(false, true, false)] [TestCase(false, true, true)]
        [TestCase(true, false, false)] [TestCase(true, false, true)]
        [TestCase(true, true, false)] [TestCase(true, true, true)]
        public void ForcedFxOverridePreservesNativeVisibilityAndOtherOutputs(bool rendererProperty, bool initial, bool writeDefaults)
            => CheckForcedVisibilityOutput(rendererProperty, initial, writeDefaults, "Any");

        [TestCase(false, false, false, "Direct")] [TestCase(false, false, true, "Direct")]
        [TestCase(false, true, false, "Direct")] [TestCase(false, true, true, "Direct")]
        [TestCase(true, false, false, "Direct")] [TestCase(true, false, true, "Direct")]
        [TestCase(true, true, false, "Direct")] [TestCase(true, true, true, "Direct")]
        [TestCase(false, false, false, "ExitEntry")] [TestCase(false, false, true, "ExitEntry")]
        [TestCase(false, true, false, "ExitEntry")] [TestCase(false, true, true, "ExitEntry")]
        [TestCase(true, false, false, "ExitEntry")] [TestCase(true, false, true, "ExitEntry")]
        [TestCase(true, true, false, "ExitEntry")] [TestCase(true, true, true, "ExitEntry")]
        public void ImmediateInitialTransitionPreservesNativeVisibilityAndOtherOutputs(bool rendererProperty, bool initial,
            bool writeDefaults, string mode) => CheckForcedVisibilityOutput(rendererProperty, initial, writeDefaults, mode);

        private void CheckForcedVisibilityOutput(bool rendererProperty, bool initial, bool writeDefaults, string mode)
        {
            Mesh("a"); var settings = ObjectSettings(); var obj = PolicyChild("Visibility"); var target = obj.AddComponent<MeshRenderer>();
            if (rendererProperty) target.enabled = initial; else obj.SetActive(initial);
            var binding = EditorCurveBinding.FloatCurve(obj.name, rendererProperty ? typeof(MeshRenderer) : typeof(GameObject),
                rendererProperty ? "m_Enabled" : "m_IsActive");
            var fixture = VisibilityOverride(binding, initial, writeDefaults);
            if (mode != "Any") ReplaceWithInitialTransition(fixture.Machine, fixture.Output, mode == "ExitEntry");
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.Controller);
            int stateCount = controller.AllReachableNodes().OfType<VirtualState>().Count();
            int transitionCount = controller.AllReachableNodes().OfType<VirtualTransitionBase>().Count();
            float duration = controller.AllReachableNodes().OfType<VirtualClip>().Max(MotionAnalysis.EndTime);
            var before = SampleFrozen(fixture.Controller, (p, i) => { }, 36,
                () => renderer.GetBlendShapeWeight(0) + ((rendererProperty ? target.enabled : obj.activeSelf) ? 1000 : 0));
            if (rendererProperty) target.enabled = initial; else obj.SetActive(initial);
            OptimizeObjectCurves(controller, settings);
            Assert.That(rendererProperty ? target.enabled : obj.activeSelf, Is.EqualTo(initial));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(binding) != null), Is.False);
            Assert.That(controller.AllReachableNodes().OfType<VirtualState>().Count(), Is.EqualTo(stateCount));
            Assert.That(controller.AllReachableNodes().OfType<VirtualTransitionBase>().Count(), Is.EqualTo(transitionCount));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Max(MotionAnalysis.EndTime), Is.EqualTo(duration));
            var built = CommitAnimator(controller, root);
            var after = SampleFrozen(built, (p, i) => { }, 36,
                () => renderer.GetBlendShapeWeight(0) + ((rendererProperty ? target.enabled : obj.activeSelf) ? 1000 : 0));
            Assert.That(before.Last() - before.First(), Is.GreaterThan(40));
            Assert.That(before.All(v => v >= (initial ? 1000 : 0) && v < (initial ? 1100 : 100)), Is.True);
            Assert.That(after, Is.EqualTo(before).Within(0.001f));
            Assert.That(AnimationUtility.GetEditorCurve((AnimationClip)fixture.Output.motion, binding), Is.Not.Null);
        }

        private AnimatorStateTransition ReplaceWithInitialTransition(AnimatorStateMachine machine, AnimatorState output, bool exit)
        {
            machine.anyStateTransitions = new AnimatorStateTransition[0];
            var transition = Track(exit ? machine.defaultState.AddExitTransition() : machine.defaultState.AddTransition(output));
            transition.hasExitTime = false; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Less, 0.995f, "Gate");
            var entry = Track(machine.AddEntryTransition(output)); entry.AddCondition(AnimatorConditionMode.Less, 0.995f, "Gate");
            if (exit)
            {
                var reset = Track(output.AddExitTransition()); reset.hasExitTime = false; reset.duration = 0;
                reset.AddCondition(AnimatorConditionMode.Greater, 0.995f, "Gate");
            }
            return transition;
        }

        [TestCase("MissingEntry")] [TestCase("EntryReturnsToInitial")] [TestCase("DelayedInitial")]
        [TestCase("InitialExitTime")] [TestCase("PossibleReturn")] [TestCase("UnknownEntry")]
        public void InitialOutputProofRetainsUncertainExitAndEntryPaths(string mode)
        {
            Mesh("a"); var settings = ObjectSettings(); var obj = PolicyChild("Visibility"); obj.SetActive(false);
            var binding = EditorCurveBinding.FloatCurve(obj.name, typeof(GameObject), "m_IsActive");
            var fixture = VisibilityOverride(binding, false, true);
            var transition = ReplaceWithInitialTransition(fixture.Machine, fixture.Output, true);
            if (mode == "MissingEntry") fixture.Machine.entryTransitions = new AnimatorTransition[0];
            if (mode == "EntryReturnsToInitial") fixture.Machine.entryTransitions[0].destinationState = fixture.Machine.defaultState;
            if (mode == "DelayedInitial") transition.duration = 0.25f;
            if (mode == "InitialExitTime") { transition.hasExitTime = true; transition.exitTime = 0.5f; }
            if (mode == "PossibleReturn") Edge(fixture.Output, fixture.Machine.defaultState).duration = 0;
            if (mode == "UnknownEntry")
            {
                fixture.Controller.AddParameter("EntryGate", AnimatorControllerParameterType.Float);
                fixture.Machine.entryTransitions[0].conditions = new[] { new AnimatorCondition {
                    parameter = "EntryGate", mode = AnimatorConditionMode.Greater, threshold = 0.5f } };
                FreezeExpressions(new VRCExpressionParameters.Parameter { name = "EntryGate", valueType = VRCExpressionParameters.ValueType.Float });
            }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.Controller);
            OptimizeObjectCurves(controller, settings);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(binding) != null), Is.True);
            Assert.That(obj.activeSelf, Is.False);
        }

        [TestCase("EntryOnly")] [TestCase("Delay")] [TestCase("ExitTime")] [TestCase("Muted")]
        [TestCase("OtherAnyState")] [TestCase("StateExit")] [TestCase("ExternalInput")] [TestCase("DriverWrite")]
        [TestCase("PartialWeight")] [TestCase("WeightControl")] [TestCase("OtherPlayable")]
        [TestCase("FirstLayerMask")] [TestCase("NormalizedTree")] [TestCase("NestedMachine")] [TestCase("EventInDefault")]
        public void FinalOverrideProofRetainsUncertainOrObservableVisibility(string mode)
        {
            Mesh("a"); var settings = ObjectSettings(); var obj = PolicyChild("Visibility"); obj.SetActive(false);
            var binding = EditorCurveBinding.FloatCurve(obj.name, typeof(GameObject), "m_IsActive");
            var fixture = VisibilityOverride(binding, false, true);
            if (mode == "EntryOnly")
            {
                fixture.Machine.anyStateTransitions = new AnimatorStateTransition[0];
                var entry = Track(fixture.Machine.AddEntryTransition(fixture.Output)); entry.AddCondition(AnimatorConditionMode.Less, 0.995f, "Gate");
            }
            if (mode == "Delay") fixture.Any.duration = 0.25f;
            if (mode == "ExitTime") { fixture.Any.hasExitTime = true; fixture.Any.exitTime = 0.8f; }
            if (mode == "Muted") fixture.Any.mute = true;
            if (mode == "OtherAnyState")
            {
                var other = Track(fixture.Machine.AddAnyStateTransition(fixture.Machine.defaultState)); other.hasExitTime = false; other.duration = 0;
            }
            if (mode == "StateExit") Edge(fixture.Output, fixture.Machine.defaultState).duration = 0;
            if (mode == "ExternalInput") FreezeExpressions(new VRCExpressionParameters.Parameter { name = "Gate", valueType = VRCExpressionParameters.ValueType.Float });
            if (mode == "DriverWrite") Driver(fixture.Machine.defaultState, "Gate", 1);
            if (mode == "PartialWeight") { var layers = fixture.Controller.layers; layers[1].defaultWeight = 0.25f; fixture.Controller.layers = layers; }
            if (mode == "FirstLayerMask")
            {
                var mask = Track(new AvatarMask()); mask.transformCount = 1; mask.SetTransformPath(0, obj.name); mask.SetTransformActive(0, false);
                var layers = fixture.Controller.layers; layers[0].avatarMask = mask; fixture.Controller.layers = layers;
            }
            if (mode == "NormalizedTree")
            {
                var tree = Track(new BlendTree { blendType = BlendTreeType.Direct });
                tree.children = new[] { new ChildMotion { motion = fixture.Output.motion, directBlendParameter = "Gate", timeScale = 1 } };
                using (var serialized = new SerializedObject(tree)) { serialized.FindProperty("m_NormalizedBlendValues").boolValue = true; serialized.ApplyModifiedPropertiesWithoutUndo(); }
                fixture.Output.motion = tree;
            }
            if (mode == "NestedMachine") Track(fixture.Machine.AddStateMachine("Nested"));
            if (mode == "EventInDefault") AnimationUtility.SetAnimationEvents((AnimationClip)fixture.Machine.defaultState.motion,
                new[] { new AnimationEvent { time = 0, functionName = "UnverifiedCallback" } });
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance); var controller = clone.Clone(fixture.Controller);
            if (mode == "WeightControl")
            {
                var control = Track(ScriptableObject.CreateInstance<VRCAnimatorLayerControl>());
                control.playable = VRC_AnimatorLayerControl.BlendableLayer.FX; control.goalWeight = 0; control.layer = controller.Layers.Last().VirtualLayerIndex;
                var state = controller.Layers.First().StateMachine.DefaultState; state.Behaviours = state.Behaviours.Add(control);
            }
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            if (mode == "OtherPlayable") entries.Add(VRCAvatarDescriptor.AnimLayerType.Gesture, clone.Clone(Controller(StaticCurve(binding, 1))));
            InvariantObjectCurves.Apply(root, entries, null, settings, AnimatorReachability.Analyze(root, entries.Values), new List<string>());
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(binding) != null), Is.True);
            Assert.That(obj.activeSelf, Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void MissingUpperTrackCanExposeLowerVisibilityEvenWithWriteDefaults(bool writeDefaults)
        {
            Mesh("a"); var settings = ObjectSettings(); var obj = PolicyChild("Visibility"); obj.SetActive(false);
            var binding = EditorCurveBinding.FloatCurve(obj.name, typeof(GameObject), "m_IsActive");
            var fixture = VisibilityOverride(binding, false, writeDefaults);
            fixture.Machine.anyStateTransitions = new AnimatorStateTransition[0];
            var entry = Track(fixture.Machine.AddEntryTransition(fixture.Output)); entry.AddCondition(AnimatorConditionMode.Less, 0.995f, "Gate");
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.Controller);
            OptimizeObjectCurves(controller, settings); var built = CommitAnimator(controller, root);
            var before = SampleFrozen(fixture.Controller, (p, i) => { }, 4, () => obj.activeSelf ? 1 : 0);
            obj.SetActive(false);
            var after = SampleFrozen(built, (p, i) => { }, 4, () => obj.activeSelf ? 1 : 0);
            Assert.That(before, Is.All.EqualTo(1)); Assert.That(after, Is.EqualTo(before));
        }
    }
}
