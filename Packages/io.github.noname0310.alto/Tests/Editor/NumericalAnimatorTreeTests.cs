using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Expressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private AnimatorController NumericalZeroTree(string rewrite, AnimationClip clip, EditorCurveBinding other)
        {
            if (rewrite == "Shared1D") return FixedOneDimensionalController(.25f, new[] { clip, clip }, new[] { 0f, 1f });
            var source = TreeController(("One", clip), (rewrite == "SelectedDirect" ? "Zero" : "One",
                rewrite == "DirectMerge" ? StaticCurve(other, 20) : rewrite == "EmptyDirect" ? EmptyStaticMotion() :
                StaticCurve(AnimationUtility.GetCurveBindings(clip)[0], 1)));
            var parameters = source.parameters; parameters.Single(p => p.name == "One").defaultFloat = 1; source.parameters = parameters;
            return source;
        }

        private AnimatorOptimizationReport RewriteNumericalZeroTree(AnimatorController source, string rewrite, OptimizationPolicy policy,
            out VirtualAnimatorController controller, IEnumerable<string> preserved = null)
        {
            controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var outputs = new ExpressionOutputUsage(root, entries, null);
            var rounding = new AnimationOutputRounding(root, entries, null, policy, preserved);
            return AnimatorOptimizer.Optimize(root, new[] { controller }, new AnimatorOptimizationSettings {
                    ConstantTrees = rewrite != "DirectMerge", MergeClips = rewrite == "DirectMerge",
                    UnusedParameters = rewrite == "EmptyDirect", StateLogic = rewrite == "EmptyDirect" },
                observableBinding: outputs.IsObservable, controllerEntries: entries, preservedParameters: preserved,
                finalParameterDeclarations: true, allowNumericalDifferences: rounding.Allows);
        }

        private static IEnumerable<TestCaseData> NumericalStructuralZeroCases()
        {
            foreach (string rewrite in new[] { "Shared1D", "DirectMerge", "SelectedDirect", "EmptyDirect" })
            foreach (string consumer in new[] { "Transform", "Light", "BlendShape" })
            foreach (bool allow in new[] { false, true }) yield return new TestCaseData(rewrite, consumer, allow);
        }

        [TestCaseSource(nameof(NumericalStructuralZeroCases))]
        public void NumericalAnimatorTrees_NegativeZeroUsesContinuousConsumerPolicy(string rewrite, string consumer, bool allow)
        {
            Mesh("Zero", "Other"); var target = PolicyChild("Visual"); var light = target.AddComponent<Light>();
            var binding = consumer == "Transform" ? EditorCurveBinding.FloatCurve(target.name, typeof(Transform), "m_LocalPosition.x") :
                consumer == "Light" ? EditorCurveBinding.FloatCurve(target.name, typeof(Light), "m_Intensity") : Binding("Zero");
            var other = consumer == "Transform" ? EditorCurveBinding.FloatCurve(target.name, typeof(Transform), "m_LocalPosition.y") :
                consumer == "Light" ? EditorCurveBinding.FloatCurve(target.name, typeof(Light), "m_Range") : Binding("Other");
            var source = NumericalZeroTree(rewrite, StaticCurve(binding, BitConverter.Int32BitsToSingle(int.MinValue)), other);
            float Read() => consumer == "Transform" ? target.transform.localPosition.x : consumer == "Light" ? light.intensity : renderer.GetBlendShapeWeight(0);
            void Reset() { target.transform.localPosition = Vector3.zero; light.intensity = 0; renderer.SetBlendShapeWeight(0, 0); }
            Reset(); var before = SampleFrozen(source, (_, _) => { }, 12, Read); Reset();
            var report = RewriteNumericalZeroTree(source, rewrite, GeometryPolicy(allow), out var controller);
            var after = SampleFrozen(CommitAnimator(controller, root), (_, _) => { }, 12, Read);
            int changed = report.Trees + report.MergedClips + report.Branches;
            Assert.That(changed, allow ? Is.GreaterThan(0) : Is.Zero);
            for (int i = 0; i < before.Length; i++)
                Assert.That(NumericalEquivalence.Same(before[i], after[i], allow), Is.True, "Native output at frame " + i);
            Assert.That(source.layers[0].stateMachine.defaultState.motion, Is.TypeOf<BlendTree>(), "The source controller is immutable.");
        }

        [TestCase("Shared1D", false)] [TestCase("Shared1D", true)]
        [TestCase("DirectMerge", false)] [TestCase("DirectMerge", true)]
        [TestCase("SelectedDirect", false)] [TestCase("SelectedDirect", true)]
        [TestCase("EmptyDirect", false)] [TestCase("EmptyDirect", true)]
        public void NumericalAnimatorTrees_MaterialBitsRemainExact(string rewrite, bool mixedSigns)
        {
            Mesh("Other"); var material = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            renderer.sharedMaterial = material; material.SetFloat("_Scalar", .2f);
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Scalar");
            var clip = StaticCurve(binding, BitConverter.Int32BitsToSingle(int.MinValue));
            if (mixedSigns) AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(
                new Keyframe(0, 0, 0, float.PositiveInfinity), new Keyframe(.1f, BitConverter.Int32BitsToSingle(int.MinValue))));
            var source = NumericalZeroTree(rewrite, clip, Binding("Other"));
            var before = SampleMaterialZeroBits(source);
            var report = RewriteNumericalZeroTree(source, rewrite, GeometryPolicy(true), out var controller);
            var after = SampleMaterialZeroBits(CommitAnimator(controller, root));
            Assert.That(after, Is.EqualTo(before), "Material uniforms expose zero sign to shader instructions.");
            Assert.That(report.Trees + report.MergedClips + report.Branches, Is.Zero);
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void NumericalAnimatorTrees_MixedZeroPoseKeepsTimingObservers(bool allow, bool observesTime)
        {
            Mesh("Other"); var target = PolicyChild("Mixed").transform;
            var binding = EditorCurveBinding.FloatCurve(target.name, typeof(Transform), "m_LocalPosition.x");
            var clip = StaticCurve(binding, 0);
            AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(new Keyframe(0, 0, 0, float.PositiveInfinity),
                new Keyframe(.2f, BitConverter.Int32BitsToSingle(int.MinValue))));
            var source = NumericalZeroTree("Shared1D", clip, binding);
            if (observesTime)
            {
                var state = source.layers[0].stateMachine.defaultState;
                var transition = Track(state.AddExitTransition()); transition.hasExitTime = true; transition.exitTime = 2;
            }
            var before = SampleFrozen(source, (_, _) => { }, 10, () => target.localPosition.x);
            var report = RewriteNumericalZeroTree(source, "Shared1D", GeometryPolicy(allow), out var controller);
            Assert.That(report.Trees, Is.EqualTo(allow && !observesTime ? 1 : 0));
            var after = SampleFrozen(CommitAnimator(controller, root), (_, _) => { }, 10, () => target.localPosition.x);
            for (int i = 0; i < before.Length; i++) Assert.That(NumericalEquivalence.Same(before[i], after[i], allow), Is.True);
        }

        [TestCase("Physics")] [TestCase("Culling")] [TestCase("StrictDescendant")]
        [TestCase("ControlCurve")] [TestCase("UnknownInput")] [TestCase("WriteDefaultsOff")]
        public void NumericalAnimatorTrees_ContinuousPermissionDoesNotAuthorizeOtherReaders(string reason)
        {
            Mesh("Other"); var target = PolicyChild("Guarded").transform;
            var binding = EditorCurveBinding.FloatCurve(target.name, typeof(Transform), "m_LocalPosition.x");
            var clip = StaticCurve(binding, BitConverter.Int32BitsToSingle(int.MinValue));
            if (reason == "ControlCurve") AnimationUtility.SetEditorCurve(clip, ParameterBinding("Output"), new AnimationCurve(new Keyframe(0, 1)));
            var source = NumericalZeroTree("Shared1D", clip, binding);
            if (reason == "ControlCurve") source.AddParameter("Output", AnimatorControllerParameterType.Float);
            if (reason == "WriteDefaultsOff") source.layers[0].stateMachine.defaultState.writeDefaultValues = false;
            if (reason == "Physics") PolicyChild("Simulation").AddComponent<VRCPhysBone>().rootTransform = target;
            if (reason == "Culling")
            {
                root.AddComponent<Animator>().cullingMode = AnimatorCullingMode.CullCompletely;
                target.gameObject.AddComponent<MeshRenderer>();
            }
            var strict = reason == "StrictDescendant" ? PolicyChild("Strict", target).transform : null;
            var policy = GeometryPolicy(true, strict);
            var report = RewriteNumericalZeroTree(source, "Shared1D", policy, out _, reason == "UnknownInput" ? new[] { "Selection" } : null);
            Assert.That(report.Trees, Is.Zero);
        }

        [TestCase(NumericalOptimization.AnimationOutputs, true)]
        [TestCase(NumericalOptimization.BlendShapes, false)]
        public void NumericalAnimatorTrees_BlendShapeWeightUsesAnimationFeature(NumericalOptimization allowed, bool expected)
        {
            Mesh("Zero"); var clip = StaticCurve(Binding("Zero"), BitConverter.Int32BitsToSingle(int.MinValue));
            var source = NumericalZeroTree("Shared1D", clip, Binding("Zero"));
            var report = RewriteNumericalZeroTree(source, "Shared1D", GeometryPolicy(allowed), out _);
            Assert.That(report.Trees, Is.EqualTo(expected ? 1 : 0), "No mesh or BlendShape buffer is baked by a tree rewrite.");
        }

        [TestCase(false)] [TestCase(true)]
        public void NumericalAnimatorTrees_SharedRootsMeetEveryPermission(bool strictSecondRoot)
        {
            var first = PolicyChild("First"); var second = PolicyChild("Second");
            PolicyChild("Output", first.transform); PolicyChild("Output", second.transform);
            var a = first.AddComponent<Animator>(); var b = second.AddComponent<Animator>();
            a.cullingMode = b.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var binding = EditorCurveBinding.FloatCurve("Output", typeof(Transform), "m_LocalPosition.x");
            var source = NumericalZeroTree("Shared1D", StaticCurve(binding, BitConverter.Int32BitsToSingle(int.MinValue)), binding);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [a] = controller, [b] = controller };
            var rounding = new AnimationOutputRounding(root, entries, null, GeometryPolicy(true, strictSecondRoot ? second.transform : null));
            var report = AnimatorOptimizer.Optimize(root, new[] { controller }, new AnimatorOptimizationSettings { ConstantTrees = true },
                controllerEntries: entries, allowNumericalDifferences: rounding.Allows);
            Assert.That(report.Trees, Is.EqualTo(strictSecondRoot ? 0 : 1));
        }

        [TestCase(false)] [TestCase(true)]
        public void NumericalAnimatorTrees_PreservedInputsKeepLossyOutputsExact(bool negative)
        {
            Mesh("Zero"); var binding = Binding("Zero");
            var source = NumericalZeroTree("Shared1D", StaticCurve(binding, negative ? BitConverter.Int32BitsToSingle(int.MinValue) : 0), binding);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var rounding = new AnimationOutputRounding(root, entries, null, GeometryPolicy(true), new[] { "Selection" });
            var clip = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(rounding.Allows(clip, binding), Is.False, "The protected input also protects its lossy output rewrites.");
            var report = AnimatorOptimizer.Optimize(root, new[] { controller }, new AnimatorOptimizationSettings { ConstantTrees = true },
                controllerEntries: entries, preservedParameters: new[] { "Selection" }, allowNumericalDifferences: rounding.Allows);
            Assert.That(report.Trees, Is.EqualTo(negative ? 0 : 1), "Exact structural coverage is independent of numerical permission.");
        }

        [Test]
        public void NumericalAnimatorTrees_WriteDefaultsOffRetainsSparseDirectAccumulation()
        {
            Mesh("Value", "Other");
            var source = TreeController(("One", StaticCurve(Binding("Value"), 70)), ("One", StaticCurve(Binding("Other"), 20)));
            var parameters = source.parameters; parameters[0].defaultFloat = 1; source.parameters = parameters;
            source.layers[0].stateMachine.defaultState.writeDefaultValues = false;
            var before = SampleFrozen(source, (_, _) => { }, 8);
            var report = RewriteNumericalZeroTree(source, "DirectMerge", GeometryPolicy(true), out var controller);
            Assert.That(report.MergedClips + report.Trees + report.Branches, Is.Zero);
            var after = SampleFrozen(CommitAnimator(controller, root), (_, _) => { }, 8);
            Assert.That(after.Select(BitConverter.SingleToInt32Bits), Is.EqualTo(before.Select(BitConverter.SingleToInt32Bits)),
                "Output policy does not authorize retained-value accumulation changes.");
        }
    }
}
