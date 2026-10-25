using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private AnimationClip StaticCurve(EditorCurveBinding binding, float value)
        {
            var clip = Track(new AnimationClip { name = binding.propertyName });
            AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(new Keyframe(0, value)));
            return clip;
        }

        private static EditorCurveBinding ParameterBinding(string name) => EditorCurveBinding.FloatCurve("", typeof(Animator), name);

        private AnimatorController TreeController(params (string parameter, Motion motion)[] children)
        {
            var controller = Controller(Clip());
            var tree = Track(new BlendTree { name = "Direct", blendType = BlendTreeType.Direct });
            tree.children = children.Select(c => new ChildMotion { directBlendParameter = c.parameter, motion = c.motion, timeScale = 1 }).ToArray();
            using (var serialized = new SerializedObject(tree))
            { serialized.FindProperty("m_NormalizedBlendValues").boolValue = false; serialized.ApplyModifiedPropertiesWithoutUndo(); }
            controller.layers[0].stateMachine.defaultState.motion = tree;
            foreach (string name in children.Select(c => c.parameter).Distinct()) controller.AddParameter(name, AnimatorControllerParameterType.Float);
            return controller;
        }

        private (VirtualAnimatorController controller, AnimatorOptimizationReport report) OptimizeAnimator(AnimatorController source,
            AnimatorOptimizationSettings settings)
        {
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            return (controller, AnimatorOptimizer.Optimize(root, new[] { controller }, settings));
        }

        private AnimatorController CommitAnimator(VirtualAnimatorController controller, GameObject target)
        {
            var animator = target.AddComponent<Animator>();
            var context = new BuildContext(target, null);
            var controllers = context.ActivateExtensionContextRecursive<VirtualControllerContext>();
            controllers.Controllers[animator] = controller;
            context.DeactivateExtensionContext<VirtualControllerContext>();
            var result = (AnimatorController)animator.runtimeAnimatorController;
            TrackControllerAssets(result);
            Object.DestroyImmediate(animator);
            return result;
        }

        [TestCase(false)] [TestCase(true)]
        public void UnusedFeedbackCycleIsRemovedOnlyWhenItHasNoObservableConsumer(bool consumer)
        {
            var a = StaticCurve(ParameterBinding("A"), 1); var b = StaticCurve(ParameterBinding("B"), 1);
            var controller = TreeController(("B", a), ("A", b));
            if (consumer)
            {
                var tree = (BlendTree)controller.layers[0].stateMachine.defaultState.motion;
                tree.children = tree.children.Concat(new[] { new ChildMotion { motion = StaticCurve(Binding("a"), 100), directBlendParameter = "A", timeScale = 1 } }).ToArray();
            }
            var output = OptimizeAnimator(controller, new() { UnusedParameters = true });
            Assert.That(output.report.ParameterCurves, Is.EqualTo(consumer ? 0 : 2));
            Assert.That(AnimationUtility.GetCurveBindings(a).Length, Is.EqualTo(1));
            Assert.That(output.controller.Parameters.Keys, Is.EquivalentTo(new[] { "A", "B" }));
        }

        [Test]
        public void ExpressionOutputsAndCrossControllerDriverCopiesKeepTheirInputs()
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.expressionParameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            descriptor.expressionParameters.parameters = new[] { new VRCExpressionParameters.Parameter { name = "Output" } };
            var a = TreeController(("Weight", StaticCurve(ParameterBinding("Input"), 1)));
            a.AddParameter("Input", AnimatorControllerParameterType.Float);
            var b = Controller(Clip()); b.AddParameter("Output", AnimatorControllerParameterType.Float); b.AddParameter("Input", AnimatorControllerParameterType.Float);
            Driver(b.layers[0].stateMachine.defaultState, "Output", 0, VRC_AvatarParameterDriver.ChangeType.Copy, "Input");
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var report = AnimatorOptimizer.Optimize(root, new[] { clone.Clone(a), clone.Clone(b) }, new() { UnusedParameters = true });
            Assert.That(report.ParameterCurves, Is.Zero);
        }

        [Test]
        public void NormalizedSiblingWeightsAreDependenciesOfVisibleOutput()
        {
            var writer = StaticCurve(ParameterBinding("Denominator"), 1);
            var controller = TreeController(("Denominator", StaticCurve(ParameterBinding("Dead"), 1)), ("Visible", StaticCurve(Binding("a"), 100)));
            controller.AddParameter("Dead", AnimatorControllerParameterType.Float);
            var tree = (BlendTree)controller.layers[0].stateMachine.defaultState.motion;
            using (var serialized = new SerializedObject(tree))
            { serialized.FindProperty("m_NormalizedBlendValues").boolValue = true; serialized.ApplyModifiedPropertiesWithoutUndo(); }
            var extra = Track(new AnimatorStateMachine()); extra.defaultState = Track(extra.AddState("Writer")); extra.defaultState.motion = writer;
            controller.layers = controller.layers.Concat(new[] { new AnimatorControllerLayer { name = "Writer", stateMachine = extra, defaultWeight = 1 } }).ToArray();
            var output = OptimizeAnimator(controller, new() { UnusedParameters = true });
            Assert.That(output.report.ParameterCurves, Is.EqualTo(1));
            Assert.That(output.controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(ParameterBinding("Denominator")) != null), Is.True);
        }

        [Test]
        public void RemovingTheLastUnusedCurvePreservesExitTimeDuration()
        {
            var clip = StaticCurve(ParameterBinding("Unused"), 1);
            AnimationUtility.SetEditorCurve(clip, ParameterBinding("Unused"), AnimationCurve.Constant(0, 4, 1));
            var controller = Controller(clip); controller.AddParameter("Unused", AnimatorControllerParameterType.Float);
            var output = OptimizeAnimator(controller, new() { UnusedParameters = true });
            Assert.That(output.report.ParameterCurves, Is.EqualTo(1));
            Assert.That(MotionAnalysis.EndTime(output.controller.AllReachableNodes().OfType<VirtualClip>().Single()), Is.EqualTo(4));
        }

        [TestCase(false)] [TestCase(true)]
        public void UnreachableStateRemovalPreservesWriteDefaultsBindingCoverage(bool uniqueBinding)
        {
            var fixture = ReachabilityFixture();
            if (!uniqueBinding) fixture.idle.motion = fixture.face.motion;
            Edge(fixture.idle, fixture.face).mute = true;
            var output = OptimizeAnimator(fixture.controller, new() { Unreachable = true });
            Assert.That(output.report.States, Is.EqualTo(uniqueBinding ? 0 : 1));
            Assert.That(output.report.Transitions, Is.EqualTo(1));
            Assert.That(fixture.controller.layers[0].stateMachine.states.Length, Is.EqualTo(2));
        }

        [Test]
        public void StateTimeParameterIsLiveEvenWhenItsMotionHasNoObservableCurves()
        {
            var writer = StaticCurve(ParameterBinding("Clock"), 0.5f);
            var controller = Controller(writer, Clip()); controller.AddParameter("Clock", AnimatorControllerParameterType.Float);
            var state = controller.layers[0].stateMachine.states[1].state;
            state.timeParameterActive = true; state.timeParameter = "Clock";
            Assert.That(OptimizeAnimator(controller, new() { UnusedParameters = true }).report.ParameterCurves, Is.Zero);
        }

        [Test]
        public void DurationDependentTreeWeightsAreKeptEvenForUnusedPoseOutput()
        {
            var clip = StaticCurve(ParameterBinding("Dead"), 1);
            AnimationUtility.SetEditorCurve(clip, ParameterBinding("Dead"), AnimationCurve.Constant(0, 2, 1));
            var controller = TreeController(("Weight", clip)); controller.AddParameter("Dead", AnimatorControllerParameterType.Float);
            var state = Track(controller.layers[0].stateMachine.AddState("Weight writer")); state.motion = StaticCurve(ParameterBinding("Weight"), 1);
            var output = OptimizeAnimator(controller, new() { UnusedParameters = true });
            Assert.That(output.report.ParameterCurves, Is.EqualTo(1));
            Assert.That(output.controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(ParameterBinding("Weight")) != null), Is.True);
        }

        [Test]
        public void RemovingMutedSoloDoesNotReactivateSuppressedTransitions()
        {
            var fixture = ReachabilityFixture(); Edge(fixture.idle, fixture.face);
            var solo = Edge(fixture.idle, fixture.idle); solo.solo = solo.mute = true;
            var output = OptimizeAnimator(fixture.controller, new() { Unreachable = true });
            Assert.That(output.controller.Layers.First().StateMachine.DefaultState.Transitions, Is.Empty);
        }

        [Test]
        public void SameWeightClipMergeMatchesUnityAtSeveralWeightsAndKeepsSources()
        {
            Mesh("a", "b");
            var source = TreeController(("P", StaticCurve(Binding("a"), 20)), ("P", StaticCurve(Binding("b"), 70)));
            source.AddParameter("Q", AnimatorControllerParameterType.Float);
            var output = OptimizeAnimator(source, new() { MergeClips = true });
            Assert.That(output.report.MergedClips, Is.EqualTo(1));
            var result = CommitAnimator(output.controller, root);
            foreach (float weight in new[] { 0f, 0.25f, 1f, 1.5f })
            {
                renderer.SetBlendShapeWeight(0, 0); renderer.SetBlendShapeWeight(1, 0);
                float expected = Evaluate(root, renderer, source, new Vector2(weight, 0));
                renderer.SetBlendShapeWeight(0, 0); renderer.SetBlendShapeWeight(1, 0);
                Assert.That(Evaluate(root, renderer, result, new Vector2(weight, 0)), Is.EqualTo(expected).Within(1e-5));
            }
            Assert.That(((BlendTree)source.layers[0].stateMachine.defaultState.motion).children.Length, Is.EqualTo(2));
        }

        [TestCase("WDOff")] [TestCase("Normalized")] [TestCase("Duration")] [TestCase("Overlap")] [TestCase("TimeScale")]
        public void IncompatibleDirectTreesAreNotMerged(string reason)
        {
            var first = StaticCurve(Binding("a"), 20); var second = StaticCurve(Binding(reason == "Overlap" ? "a" : "b"), 70);
            var controller = TreeController(("P", first), ("P", second));
            var state = controller.layers[0].stateMachine.defaultState; var tree = (BlendTree)state.motion;
            if (reason == "WDOff") state.writeDefaultValues = false;
            if (reason == "Normalized") using (var serialized = new SerializedObject(tree))
                { serialized.FindProperty("m_NormalizedBlendValues").boolValue = true; serialized.ApplyModifiedPropertiesWithoutUndo(); }
            if (reason == "Duration") AnimationUtility.SetEditorCurve(second, Binding("b"), AnimationCurve.Constant(0, 1, 70));
            if (reason == "TimeScale") { var children = tree.children; children[1].timeScale = 2; tree.children = children; }
            Assert.That(OptimizeAnimator(controller, new() { MergeClips = true }).report.MergedClips, Is.Zero);
        }

        [TestCase("Constant", true)] [TestCase("IsLocal", false)] [TestCase("GestureLeft", false)]
        public void ConstantOneDimensionalTreeRequiresAProvenParameter(string parameter, bool reduced)
        {
            var controller = TreeController((parameter, StaticCurve(Binding("a"), 20)), (parameter, StaticCurve(Binding("a"), 70)));
            var tree = (BlendTree)controller.layers[0].stateMachine.defaultState.motion;
            tree.blendType = BlendTreeType.Simple1D; tree.blendParameter = parameter;
            var children = tree.children; children[0].threshold = 0; children[1].threshold = 1; tree.children = children;
            Assert.That(OptimizeAnimator(controller, new() { ConstantTrees = true }).report.Trees, Is.EqualTo(reduced ? 1 : 0));
        }

        [Test]
        public void ConstantBranchWithUniqueBindingsIsPreserved()
        {
            var controller = TreeController(("P", StaticCurve(Binding("a"), 20)), ("P", StaticCurve(Binding("b"), 70)));
            var tree = (BlendTree)controller.layers[0].stateMachine.defaultState.motion; tree.blendType = BlendTreeType.Simple1D; tree.blendParameter = "P";
            Assert.That(OptimizeAnimator(controller, new() { ConstantTrees = true }).report.Trees, Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantMaterialBakeUsesBuildCopiesAndRespectsMaterialSwaps(bool swap)
        {
            var source = Track(new Material(Shader.Find("Standard"))); source.SetFloat("_Glossiness", 0.3f); renderer.sharedMaterial = source;
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Glossiness");
            var clip = StaticCurve(binding, 0.8f);
            if (swap) AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = source } });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var report = new AnimatorOptimizationReport();
            MaterialConstants.Optimize(root, new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller },
                null, material => Track(material), report);
            Assert.That(report.MaterialCurves, Is.EqualTo(swap ? 0 : 1));
            Assert.That(source.GetFloat("_Glossiness"), Is.EqualTo(0.3f).Within(1e-6));
            Assert.That(renderer.sharedMaterial.GetFloat("_Glossiness"), Is.EqualTo(swap ? 0.3f : 0.8f).Within(1e-6));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(1));
        }

        [Test]
        public void VariableDirectWeightPreventsMaterialBakingEvenWithConstantKeys()
        {
            var source = Track(new Material(Shader.Find("Standard"))); renderer.sharedMaterial = source;
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Glossiness");
            var controller = TreeController(("IsLocal", StaticCurve(binding, source.GetFloat("_Glossiness"))));
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(controller);
            var report = new AnimatorOptimizationReport();
            MaterialConstants.Optimize(root, new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = clone },
                null, material => Track(material), report);
            Assert.That(report.MaterialCurves, Is.Zero);
        }

        [Test]
        public void AParameterDeclaredOnlyInAnotherControllerIsNotAssumedToBeALocalParameterCurve()
        {
            var clip = StaticCurve(ParameterBinding("Undeclared"), 1);
            var owner = TreeController(("Weight", clip));
            var other = Controller(Clip()); other.AddParameter("Undeclared", AnimatorControllerParameterType.Float);
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var report = AnimatorOptimizer.Optimize(root, new[] { clone.Clone(owner), clone.Clone(other) }, new() { UnusedParameters = true });
            Assert.That(report.ParameterCurves, Is.Zero);
        }
    }
}
