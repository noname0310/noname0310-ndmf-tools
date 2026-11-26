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
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static IEnumerable<TestCaseData> SignedZeroBlendCases()
        {
            foreach (var type in new[] { BlendTreeType.Simple1D, BlendTreeType.SimpleDirectional2D,
                BlendTreeType.FreeformDirectional2D, BlendTreeType.FreeformCartesian2D, BlendTreeType.Direct })
            foreach (bool negative in new[] { false, true })
            foreach (bool writeDefaults in new[] { false, true })
                yield return new TestCaseData(type, negative, writeDefaults);
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantOutputCounterexample_StateAndLayerBlendsPreserveNativeNegativeZeroBits(bool partialLayer)
        {
            Mesh("Other");
            float zero = BitConverter.Int32BitsToSingle(int.MinValue);
            var material = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            material.SetFloat("_Scalar", zero); renderer.sharedMaterial = material;
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Scalar");
            var clips = new[] { StaticCurve(binding, zero), StaticCurve(binding, zero) };
            var source = Controller(partialLayer ? clips.Take(1).ToArray() : clips);
            source.AddParameter("Gate", AnimatorControllerParameterType.Float);
            if (partialLayer)
            {
                var extra = Controller(clips[1]).layers[0]; extra.name = "Half weight"; extra.defaultWeight = .5f;
                source.layers = new[] { source.layers[0], extra };
            }
            else
            {
                var states = source.layers[0].stateMachine.states;
                var transition = Edge(states[0].state, states[1].state, "Gate", .5f, AnimatorConditionMode.Greater);
                transition.hasFixedDuration = true; transition.duration = .2f;
            }
            foreach (var layer in source.layers)
                foreach (var state in layer.stateMachine.states) state.state.writeDefaultValues = true;
            int[] Sample(AnimatorController current)
            {
                var samples = new List<int>();
                try
                {
                    SampleFrozen(current, (p, frame) => p.SetFloat("Gate", frame >= 6 ? 1 : 0), 32, () =>
                    {
                        var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                        samples.Add(BitConverter.SingleToInt32Bits(block.HasProperty("_Scalar") ? block.GetFloat("_Scalar") : renderer.sharedMaterial.GetFloat("_Scalar")));
                        return 0;
                    });
                }
                finally { renderer.SetPropertyBlock(null); }
                return samples.ToArray();
            }
            var expected = Sample(source);
            var outputs = ConstantOutputs(source, out var controller, new[] { "Gate" });
            bool proved = outputs.TryProve(renderer, binding.propertyName, ConstantOutputValue.Scalar(zero), out _, out _);
            var report = new AnimatorOptimizationReport();
            MaterialConstants.Optimize(root, new Dictionary<object, VirtualAnimatorController> {
                [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, null, copy => Track(copy), report,
                allowInitialChanges: _ => true, preservedInputs: new[] { "Gate" });
            var actual = Sample(CommitAnimator(controller, root));
            TestContext.WriteLine("Native bits=" + string.Join(",", expected) + "; explicit proof=" + proved + "; removed=" + report.MaterialCurves);
            Assert.That(actual, Is.EqualTo(expected), "State/layer blending is part of the property output contract.");
            Assert.That(report.MaterialCurves, Is.EqualTo(partialLayer ? 2 : 0),
                "The native-passing partial-layer invariant remains removable; the state crossfade does not.");
            if (!partialLayer) Assert.That(proved, Is.False, "The explicit proof includes transition accumulation.");
        }

        private static IEnumerable<TestCaseData> FiniteBlendBitCases()
        {
            foreach (var type in new[] { BlendTreeType.Simple1D, BlendTreeType.SimpleDirectional2D,
                BlendTreeType.FreeformDirectional2D, BlendTreeType.FreeformCartesian2D })
            foreach (float value in new[] { .7f, 1.1f, 70f }) yield return new TestCaseData(type, value);
        }

        [TestCaseSource(nameof(FiniteBlendBitCases))]
        public void ConstantOutputCounterexample_IdenticalFiniteLeavesMatchNativeMaterialBits(BlendTreeType type, float value)
        {
            Mesh("Other");
            var material = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            material.SetFloat("_Scalar", .2f); renderer.sharedMaterial = material;
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Scalar");
            var clip = StaticCurve(binding, value);
            var source = type == BlendTreeType.Simple1D ? FixedOneDimensionalController(.25f,
                    new[] { clip, clip }, new[] { 0f, 1f })
                : CounterexampleTwoDimensionalController(type, Enumerable.Repeat(clip, 5).ToArray());
            source.layers[0].stateMachine.defaultState.writeDefaultValues = true;
            var external = type == BlendTreeType.Simple1D ? new[] { "Selection" } : new[] { "BlendX", "BlendY" };
            var outputs = ConstantOutputs(source, out var controller, external);
            bool proved = outputs.TryProve(renderer, binding.propertyName, ConstantOutputValue.Scalar(.2f), out var proof, out _);
            var reachability = AnimatorReachability.Analyze(root, new[] { controller }, external);
            bool matches = ConstantMotionOutput.Matches(controller.Layers.First().StateMachine.DefaultState.Motion,
                binding, value, true, reachability, new(), new(), new(), true, true);
            var positions = new[] { 0f, .1f, .17f, .23f, .31f, .37f, .43f, .51f, .63f, .71f, .83f, 1f };
            int[] Sample(AnimatorController current)
            {
                var samples = new List<int>();
                try
                {
                    SampleFrozen(current, (p, frame) =>
                    {
                        if (type == BlendTreeType.Simple1D) p.SetFloat("Selection", positions[frame]);
                        else { p.SetFloat("BlendX", positions[frame]); p.SetFloat("BlendY", positions[(frame + 5) % positions.Length] * .37f); }
                    }, positions.Length, () =>
                    {
                        var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                        samples.Add(BitConverter.SingleToInt32Bits(block.HasProperty("_Scalar") ? block.GetFloat("_Scalar") : material.GetFloat("_Scalar")));
                        return 0;
                    });
                }
                finally { renderer.SetPropertyBlock(null); }
                return samples.ToArray();
            }
            var expected = Sample(source);
            var report = AnimatorOptimizer.Optimize(root, new[] { controller }, new AnimatorOptimizationSettings { ConstantTrees = true },
                preservedParameters: external);
            var actual = Sample(CommitAnimator(controller, root));
            TestContext.WriteLine("Input bits=" + BitConverter.SingleToInt32Bits(value) + "; native=" + string.Join(",", expected) +
                "; proof=" + proved + "; matches=" + matches + "; rewritten trees=" + report.Trees);
            Assert.That(proved, Is.EqualTo(type == BlendTreeType.Simple1D));
            Assert.That(matches, Is.EqualTo(type == BlendTreeType.Simple1D), "The invariant fallback uses the same exact output contract.");
            if (proved) Assert.That(expected, Is.All.EqualTo(BitConverter.SingleToInt32Bits(proof.Value.Number)),
                "Shader asuint can observe differences below ordinary float-test tolerances.");
            Assert.That(actual, Is.EqualTo(expected), "Existing structural rewrites preserve the same native material bits.");
        }

        [TestCase(WeightedMode.None, false)] [TestCase(WeightedMode.None, true)]
        [TestCase(WeightedMode.Both, false)] [TestCase(WeightedMode.Both, true)]
        [TestCase(WeightedMode.In, true)] [TestCase(WeightedMode.Out, true)]
        public void ConstantOutputCounterexample_FlatWeightedCurveProofMatchesNativeZeroBits(WeightedMode mode, bool negative)
        {
            Mesh("Other");
            var material = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            material.SetFloat("_Scalar", .2f); renderer.sharedMaterial = material;
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Scalar");
            float zero = BitConverter.Int32BitsToSingle(negative ? int.MinValue : 0);
            var curve = new AnimationCurve(new Keyframe(0, zero, 0, 0, .7f, .2f) { weightedMode = mode },
                new Keyframe(.1f, zero, 0, 0, .1f, .8f) { weightedMode = mode },
                new Keyframe(.3f, zero, 0, 0, .6f, .4f) { weightedMode = mode });
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, binding, curve);
            var source = Controller(clip); source.layers[0].stateMachine.defaultState.writeDefaultValues = true;
            var expected = SampleMaterialZeroBits(source);
            var outputs = ConstantOutputs(source, out _);
            bool proved = outputs.TryProve(renderer, binding.propertyName, ConstantOutputValue.Scalar(.2f), out var proof, out _);
            TestContext.WriteLine("Native bits=" + string.Join(",", expected.Distinct()) + "; proof=" + proved);
            if (proved) Assert.That(expected, Is.All.EqualTo(BitConverter.SingleToInt32Bits(proof.Value.Number)),
                "A weighted constant curve still requires an exact native zero-sign proof.");
            if (!negative) Assert.That(proved, Is.True);
        }

        [TestCase("Shared1D", false)] [TestCase("Shared1D", true)]
        [TestCase("DirectMerge", false)] [TestCase("DirectMerge", true)]
        [TestCase("SelectedDirect", false)] [TestCase("SelectedDirect", true)]
        [TestCase("EmptyDirect", false)] [TestCase("EmptyDirect", true)]
        public void ConstantOutputCounterexample_StructuralTreeRewritePreservesNativeZeroBits(string rewrite, bool negative)
        {
            Mesh("Other");
            var material = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            material.SetFloat("_Scalar", .2f); renderer.sharedMaterial = material;
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Scalar");
            var clip = StaticCurve(binding, BitConverter.Int32BitsToSingle(negative ? int.MinValue : 0));
            AnimatorController source;
            if (rewrite == "Shared1D")
                source = FixedOneDimensionalController(.25f, new[] { clip, clip }, new[] { 0f, 1f });
            else
            {
                source = TreeController(("One", clip), (rewrite == "SelectedDirect" ? "Zero" : "One",
                    rewrite == "DirectMerge" ? StaticCurve(Binding("Other"), 20) :
                        rewrite == "EmptyDirect" ? EmptyStaticMotion() : StaticCurve(binding, 1)));
                var parameters = source.parameters; parameters.Single(p => p.name == "One").defaultFloat = 1; source.parameters = parameters;
            }
            source.layers[0].stateMachine.defaultState.writeDefaultValues = true;
            var expected = SampleMaterialZeroBits(source);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var report = AnimatorOptimizer.Optimize(root, new[] { controller }, new AnimatorOptimizationSettings {
                ConstantTrees = rewrite != "DirectMerge", MergeClips = rewrite == "DirectMerge",
                UnusedParameters = rewrite == "EmptyDirect", StateLogic = rewrite == "EmptyDirect" },
                // Liveness preserves empty-clip timing with a missing-target
                // curve. The build's state-output analysis resolves that target
                // before classifying the branch as unobserved.
                observableBinding: (_, curve) => string.IsNullOrEmpty(curve.path) || root.transform.Find(curve.path) != null,
                finalParameterDeclarations: true);
            var actual = SampleMaterialZeroBits(CommitAnimator(controller, root));
            TestContext.WriteLine("Native before=" + string.Join(",", expected.Distinct()) + "; after=" +
                string.Join(",", actual.Distinct()) + "; trees=" + report.Trees + "; merged=" + report.MergedClips + "; branches=" + report.Branches);
            Assert.That(actual, Is.EqualTo(expected), "A structural tree rewrite must preserve material output bits.");
            if (!negative) Assert.That(report.Trees + report.MergedClips + report.Branches, Is.GreaterThan(0),
                "Ordinary positive-zero optimization coverage remains available.");
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantOutputCounterexample_FixedSingleLeafPreservesNativeNegativeZero(bool writeDefaults)
        {
            Mesh("Other");
            var material = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            material.SetFloat("_Scalar", .2f); renderer.sharedMaterial = material;
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Scalar");
            var source = FixedOneDimensionalController(0,
                new[] { StaticCurve(binding, BitConverter.Int32BitsToSingle(int.MinValue)), StaticCurve(binding, 0) }, new[] { 0f, 1f });
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var outputs = ConstantOutputs(source, out _);
            Assert.That(outputs.TryProve(renderer, binding.propertyName, ConstantOutputValue.Scalar(.2f), out var proof, out string reason), Is.True, reason);
            Assert.That(BitConverter.SingleToInt32Bits(proof.Value.Number), Is.EqualTo(int.MinValue));
            try
            {
                SampleFrozen(source, (p, frame) => { }, 6, () =>
                {
                    var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                    Assert.That(block.HasProperty("_Scalar"), Is.True);
                    Assert.That(BitConverter.SingleToInt32Bits(block.GetFloat("_Scalar")), Is.EqualTo(int.MinValue));
                    return 0;
                });
            }
            finally { renderer.SetPropertyBlock(null); }
        }

        [TestCase(false, false, false)] [TestCase(false, false, true)]
        [TestCase(false, true, false)] [TestCase(false, true, true)]
        [TestCase(true, false, false)] [TestCase(true, false, true)]
        [TestCase(true, true, false)] [TestCase(true, true, true)]
        public void ConstantOutputCounterexample_SparseDirectZeroDefaultMatchesNativeBits(bool negative, bool writeDefaults, bool zeroWriter)
        {
            Mesh("Other");
            var material = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            float zero = BitConverter.Int32BitsToSingle(negative ? int.MinValue : 0);
            material.SetFloat("_Scalar", zero); renderer.sharedMaterial = material;
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Scalar");
            var source = TreeController(("One", StaticCurve(binding, zeroWriter ? 70 : zero)),
                ("OtherWeight", StaticCurve(Binding("Other"), 20)));
            var parameters = source.parameters; parameters.Single(p => p.name == "One").defaultFloat = zeroWriter ? 0 : 1; source.parameters = parameters;
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var reachability = AnimatorReachability.Analyze(root, new[] { controller }, new[] { "OtherWeight" });
            var motion = controller.Layers.First().StateMachine.DefaultState.Motion;
            bool matches = ConstantMotionOutput.Matches(motion, binding, zero, true, reachability, new(), new(), new(), true, writeDefaults);
            bool evaluated = ConstantMotionOutput.TryEvaluateScalar(motion, binding, zero, reachability, out float value, writeDefaults);
            var samples = new List<int>();
            try
            {
                SampleFrozen(source, (p, frame) => p.SetFloat("OtherWeight", .5f), 6, () =>
                {
                    var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                    samples.Add(BitConverter.SingleToInt32Bits(block.HasProperty("_Scalar") ? block.GetFloat("_Scalar") : material.GetFloat("_Scalar")));
                    return 0;
                });
            }
            finally { renderer.SetPropertyBlock(null); }
            TestContext.WriteLine("Native bits=" + string.Join(",", samples) + "; matches=" + matches + "; evaluated=" + evaluated);
            if (matches) Assert.That(samples, Is.All.EqualTo(BitConverter.SingleToInt32Bits(zero)));
            if (evaluated) Assert.That(samples, Is.All.EqualTo(BitConverter.SingleToInt32Bits(value)));
        }

        [TestCaseSource(nameof(SignedZeroBlendCases))]
        public void ConstantOutputCounterexample_ZeroBlendProofMatchesNativeBits(BlendTreeType type, bool negative, bool writeDefaults)
        {
            Mesh("Other");
            var material = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            material.SetFloat("_Scalar", .2f); renderer.sharedMaterial = material;
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Scalar");
            float zero = BitConverter.Int32BitsToSingle(negative ? int.MinValue : 0);
            var clips = Enumerable.Range(0, 5).Select(_ => StaticCurve(binding, zero)).ToArray();
            AnimatorController source;
            string[] external;
            if (type == BlendTreeType.Simple1D)
            {
                source = FixedOneDimensionalController(.25f, clips.Take(2).ToArray(), new[] { 0f, 1f });
                external = new[] { "Selection" };
            }
            else if (type == BlendTreeType.Direct)
            {
                source = TreeController(("One", clips[0]), ("OtherWeight", StaticCurve(Binding("Other"), 20)));
                var parameters = source.parameters; parameters.Single(p => p.name == "One").defaultFloat = 1; source.parameters = parameters;
                external = new[] { "OtherWeight" };
            }
            else
            {
                source = CounterexampleTwoDimensionalController(type, clips);
                external = new[] { "BlendX", "BlendY" };
            }
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var outputs = ConstantOutputs(source, out var controller, external);
            bool proved = outputs.TryProve(renderer, binding.propertyName, ConstantOutputValue.Scalar(.2f), out var proof, out _);
            var samples = new List<int>();
            try
            {
                SampleFrozen(source, (p, frame) =>
                {
                    if (type == BlendTreeType.Simple1D) p.SetFloat("Selection", new[] { .25f, 0f, .5f, 1f, .75f, .1f }[frame]);
                    else if (type == BlendTreeType.Direct) p.SetFloat("OtherWeight", .5f);
                    else { p.SetFloat("BlendX", frame % 2 == 0 ? .2f : 0); p.SetFloat("BlendY", frame % 2 == 0 ? .3f : 0); }
                }, 6, () =>
                {
                    var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                    samples.Add(BitConverter.SingleToInt32Bits(block.HasProperty("_Scalar") ? block.GetFloat("_Scalar") : material.GetFloat("_Scalar")));
                    return 0;
                });
            }
            finally { renderer.SetPropertyBlock(null); }
            TestContext.WriteLine("Native bits=" + string.Join(",", samples) + "; proof=" + proved +
                (proved ? "; proof bits=" + BitConverter.SingleToInt32Bits(proof.Value.Number) : ""));
            if (proved) Assert.That(samples, Is.All.EqualTo(BitConverter.SingleToInt32Bits(proof.Value.Number)),
                "A normalized or Direct blend must preserve the exact shader-visible zero sign.");
            var reachability = AnimatorReachability.Analyze(root, new[] { controller }, external);
            foreach (float goal in new[] { 0f, BitConverter.Int32BitsToSingle(int.MinValue) })
                if (ConstantMotionOutput.Matches(controller.Layers.First().StateMachine.DefaultState.Motion, binding, goal,
                    false, reachability, new(), new(), new(), true, writeDefaults))
                    Assert.That(samples, Is.All.EqualTo(BitConverter.SingleToInt32Bits(goal)), "The invariant fallback also preserves zero bits.");
        }

        private AnimatorController CounterexampleTwoDimensionalController(BlendTreeType type, AnimationClip[] clips)
        {
            var positions = new[] { Vector2.zero, Vector2.right, Vector2.left, Vector2.up, Vector2.down };
            var tree = Track(new BlendTree { name = "Constant 2D output", blendType = type,
                blendParameter = "BlendX", blendParameterY = "BlendY" });
            tree.children = clips.Select((clip, i) => new ChildMotion { motion = clip, position = positions[i], timeScale = 1 }).ToArray();
            var source = Controller(clips[0]); source.layers[0].stateMachine.defaultState.motion = tree;
            source.AddParameter("BlendX", AnimatorControllerParameterType.Float);
            source.AddParameter("BlendY", AnimatorControllerParameterType.Float);
            return source;
        }

        [TestCase(BlendTreeType.SimpleDirectional2D, false)] [TestCase(BlendTreeType.SimpleDirectional2D, true)]
        [TestCase(BlendTreeType.FreeformDirectional2D, false)] [TestCase(BlendTreeType.FreeformDirectional2D, true)]
        [TestCase(BlendTreeType.FreeformCartesian2D, false)] [TestCase(BlendTreeType.FreeformCartesian2D, true)]
        public void ConstantOutputCounterexample_TwoDimensionalTypedOutputsRetainNativeScalarResiduals(BlendTreeType type, bool writeDefaults)
        {
            Mesh("Constant");
            var initial = Track(new Material(Shader.Find("Hidden/InternalErrorShader")));
            var output = Track(new Material(initial)); renderer.sharedMaterial = initial;
            renderer.updateWhenOffscreen = false;
            var offscreen = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "m_UpdateWhenOffscreen");
            var clips = Enumerable.Range(0, 5).Select(_ =>
            {
                var clip = ConstantMaterialClip(output);
                AnimationUtility.SetEditorCurve(clip, Binding("Constant"), AnimationCurve.Constant(0, .3f, 0));
                AnimationUtility.SetEditorCurve(clip, offscreen, AnimationCurve.Constant(0, .3f, 1));
                return clip;
            }).ToArray();
            var source = CounterexampleTwoDimensionalController(type, clips);
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var analysis = ConstantOutputs(source, out var controller, new[] { "BlendX", "BlendY" });
            Assert.That(analysis.TryProve(renderer, "blendShape.Constant", ConstantOutputValue.Scalar(20), out _, out _), Is.False,
                "Zero leaf values do not prove the absence of a residual default contribution.");
            var queries = new[] {
                new ConstantPropertyQuery("m_UpdateWhenOffscreen", ConstantOutputValue.Discrete(0)),
                new ConstantPropertyQuery("m_Materials.Array.data[0]", ConstantOutputValue.Object(initial))
            };
            Assert.That(analysis.TryProveGroup(renderer, queries, out var proofs, out var reason), Is.True, reason);
            var inputs = new[] { Vector2.zero, Vector2.right, Vector2.left, Vector2.up, Vector2.down,
                new Vector2(.2f, .3f), new Vector2(-.6f, .2f) };
            float Read()
            {
                Assert.That(renderer.sharedMaterial, Is.SameAs(output));
                Assert.That(renderer.updateWhenOffscreen, Is.True);
                return renderer.GetBlendShapeWeight(0);
            }
            var before = SampleFrozen(source, (p, frame) =>
            { p.SetFloat("BlendX", inputs[frame].x); p.SetFloat("BlendY", inputs[frame].y); }, inputs.Length, Read, 20);
            if (type == BlendTreeType.FreeformDirectional2D)
                Assert.That(before.Any(value => value != 0), Is.True, "Native 2D accumulation exposes part of the nonzero default.");
            renderer.sharedMaterial = output; renderer.updateWhenOffscreen = true;
            foreach (var proof in proofs)
            foreach (var use in proof.Writers) MotionAnalysis.RemoveCurves(use.Clip, new[] { use.Binding });
            var after = SampleFrozen(CommitAnimator(controller, root), (p, frame) =>
            { p.SetFloat("BlendX", inputs[frame].x); p.SetFloat("BlendY", inputs[frame].y); }, inputs.Length, Read, 20);
            Assert.That(after, Is.EqualTo(before));
        }

        [TestCase(BlendTreeType.SimpleDirectional2D, false)] [TestCase(BlendTreeType.SimpleDirectional2D, true)]
        [TestCase(BlendTreeType.FreeformDirectional2D, false)] [TestCase(BlendTreeType.FreeformDirectional2D, true)]
        [TestCase(BlendTreeType.FreeformCartesian2D, false)] [TestCase(BlendTreeType.FreeformCartesian2D, true)]
        public void ConstantOutputCounterexample_TwoDimensionalMissingOrDifferentLeafRemainsUnknown(BlendTreeType type, bool missing)
        {
            Mesh("Constant");
            var clips = Enumerable.Range(0, 5).Select(_ => StaticCurve(Binding("Constant"), 70)).ToArray();
            clips[0] = missing ? Clip() : StaticCurve(Binding("Constant"), 25);
            var source = CounterexampleTwoDimensionalController(type, clips);
            var analysis = ConstantOutputs(source, out _, new[] { "BlendX", "BlendY" });
            Assert.That(analysis.TryProve(renderer, "blendShape.Constant", ConstantOutputValue.Scalar(20), out _, out _), Is.False);
            var before = SampleFrozen(source, (p, frame) =>
            { p.SetFloat("BlendX", frame == 0 ? 0 : 1); p.SetFloat("BlendY", 0); }, 2,
                () => renderer.GetBlendShapeWeight(0), 20);
            Assert.That(before[0], Is.EqualTo(missing ? 20 : 25).Within(.00001f));
            Assert.That(before[1], Is.EqualTo(70).Within(.00001f));
        }

        [TestCase(BlendTreeType.SimpleDirectional2D, "ZeroInvariant")] [TestCase(BlendTreeType.SimpleDirectional2D, "ChangedInitial")]
        [TestCase(BlendTreeType.SimpleDirectional2D, "LowerWriter")] [TestCase(BlendTreeType.SimpleDirectional2D, "FiniteLeaves")]
        [TestCase(BlendTreeType.FreeformDirectional2D, "ZeroInvariant")] [TestCase(BlendTreeType.FreeformDirectional2D, "ChangedInitial")]
        [TestCase(BlendTreeType.FreeformDirectional2D, "LowerWriter")] [TestCase(BlendTreeType.FreeformDirectional2D, "FiniteLeaves")]
        [TestCase(BlendTreeType.FreeformCartesian2D, "ZeroInvariant")] [TestCase(BlendTreeType.FreeformCartesian2D, "ChangedInitial")]
        [TestCase(BlendTreeType.FreeformCartesian2D, "LowerWriter")] [TestCase(BlendTreeType.FreeformCartesian2D, "FiniteLeaves")]
        public void ConstantOutputCounterexample_TwoDimensionalMaterialBakeRequiresGlobalZeroInvariant(BlendTreeType type, string contract)
        {
            Mesh("Other");
            float initial = contract == "ChangedInitial" || contract == "FiniteLeaves" ? .2f : 0;
            float value = contract == "FiniteLeaves" ? .7f : 0;
            var material = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            material.SetFloat("_Scalar", initial); renderer.sharedMaterial = material;
            var clips = Enumerable.Range(0, 5).Select(_ =>
                ConstantMaterialPropertyClip("_Scalar", "", new Vector4(value, 0, 0, 0))).ToArray();
            var source = CounterexampleTwoDimensionalController(type, clips);
            if (contract == "ZeroInvariant" || contract == "LowerWriter")
            {
                var lower = Controller(ConstantMaterialPropertyClip("_Scalar", "", new Vector4(contract == "LowerWriter" ? 70 : 0, 0, 0, 0))).layers[0];
                lower.name = "Lower writer"; source.layers = new[] { lower, source.layers[0] };
            }
            var inputs = new[] { Vector2.zero, Vector2.right, Vector2.left, Vector2.up, Vector2.down,
                new Vector2(.2f, .3f), new Vector2(-.6f, .2f) };
            int[] Sample(AnimatorController current)
            {
                var values = new List<int>();
                try
                {
                    SampleFrozen(current, (p, frame) =>
                    { p.SetFloat("BlendX", inputs[frame].x); p.SetFloat("BlendY", inputs[frame].y); }, inputs.Length, () =>
                    {
                        var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                        values.Add(BitConverter.SingleToInt32Bits(block.HasProperty("_Scalar") ? block.GetFloat("_Scalar") : renderer.sharedMaterial.GetFloat("_Scalar")));
                        return 0;
                    });
                }
                finally { renderer.SetPropertyBlock(null); }
                return values.ToArray();
            }
            var before = Sample(source);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var report = new AnimatorOptimizationReport();
            MaterialConstants.Optimize(root,
                new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller },
                null, copy => Track(copy), report, allowInitialChanges: _ => true, preservedInputs: new[] { "BlendX", "BlendY" });
            Assert.That(report.MaterialCurves, Is.EqualTo(contract == "ZeroInvariant" ? 6 : 0),
                "Only the invariant with zero initial, lower-layer and leaf values authorizes scalar curve removal.");
            Assert.That(renderer.sharedMaterial.GetFloat("_Scalar"), Is.EqualTo(initial));
            Assert.That(material.GetFloat("_Scalar"), Is.EqualTo(initial));
            Assert.That(Sample(CommitAnimator(controller, root)), Is.EqualTo(before));
        }

        [TestCase(BlendTreeType.SimpleDirectional2D, false)] [TestCase(BlendTreeType.SimpleDirectional2D, true)]
        [TestCase(BlendTreeType.FreeformDirectional2D, false)] [TestCase(BlendTreeType.FreeformDirectional2D, true)]
        [TestCase(BlendTreeType.FreeformCartesian2D, false)] [TestCase(BlendTreeType.FreeformCartesian2D, true)]
        public void ConstantOutputCounterexample_UnboundTwoDimensionalTreePreservesExactDefault(BlendTreeType type, bool writeDefaults)
        {
            Mesh("Other");
            var material = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            material.SetFloat("_Scalar", .7f); renderer.sharedMaterial = material;
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Scalar");
            var source = CounterexampleTwoDimensionalController(type,
                Enumerable.Range(0, 5).Select(_ => StaticCurve(Binding("Other"), 20)).ToArray());
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var analysis = AnimatorReachability.Analyze(root, new[] { controller }, new[] { "BlendX", "BlendY" });
            Assert.That(ConstantMotionOutput.Matches(controller.Layers.First().StateMachine.DefaultState.Motion,
                binding, .7f, true, analysis, new(), new(), new(), true, writeDefaults), Is.True,
                "A tree that never registers this property does not blend its default.");
            try
            {
                SampleFrozen(source, (p, frame) => { p.SetFloat("BlendX", frame * .17f); p.SetFloat("BlendY", .31f); }, 6, () =>
                {
                    var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                    float value = block.HasProperty("_Scalar") ? block.GetFloat("_Scalar") : material.GetFloat("_Scalar");
                    Assert.That(BitConverter.SingleToInt32Bits(value), Is.EqualTo(BitConverter.SingleToInt32Bits(.7f)));
                    return 0;
                });
            }
            finally { renderer.SetPropertyBlock(null); }
        }

        [TestCase("Scalar", false)] [TestCase("Scalar", true)]
        [TestCase("Discrete", false)] [TestCase("Discrete", true)]
        [TestCase("Object", false)] [TestCase("Object", true)]
        public void ConstantOutputCounterexample_SparseDirectTreeProofMatchesNative(string kind, bool writeDefaults)
        {
            Mesh("Constant", "Other");
            var initialMaterial = Track(new Material(Shader.Find("Hidden/InternalErrorShader")));
            var outputMaterial = Track(new Material(initialMaterial));
            renderer.sharedMaterial = initialMaterial;
            renderer.updateWhenOffscreen = false;
            var binding = kind == "Object" ?
                EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]") :
                kind == "Discrete" ? EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "m_UpdateWhenOffscreen") :
                Binding("Constant");
            var writer = kind == "Object" ? ConstantMaterialClip(outputMaterial) : StaticCurve(binding, kind == "Discrete" ? 1 : 70);
            var source = TreeController(("One", writer), ("OtherWeight", StaticCurve(Binding("Other"), 90)));
            var parameters = source.parameters;
            parameters.Single(p => p.name == "One").defaultFloat = 1;
            source.parameters = parameters;
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var initial = kind == "Object" ? ConstantOutputValue.Object(initialMaterial) :
                kind == "Discrete" ? ConstantOutputValue.Discrete(0) : ConstantOutputValue.Scalar(20);
            var analysis = ConstantOutputs(source, out var controller, new[] { "OtherWeight" });
            if (!analysis.TryProve(renderer, binding.propertyName, initial, out var proof, out var reason))
            {
                Assert.That(reason, Is.Not.Null.And.Not.Empty);
                return;
            }

            var weights = new[] { 2f, .25f, .5f, 1f, 2f, -.5f, 0f };
            float Read() => kind == "Object" ? renderer.sharedMaterial == outputMaterial ? 1 : 0 :
                kind == "Discrete" ? renderer.updateWhenOffscreen ? 1 : 0 : renderer.GetBlendShapeWeight(0);
            var before = SampleFrozen(source, (p, frame) => p.SetFloat("OtherWeight", weights[frame]), weights.Length, Read, 20);
            float expected = kind == "Scalar" ? proof.Value.Number : 1;
            Assert.That(before, Is.All.EqualTo(expected).Within(.00001f),
                "An explicit constant proof must include the defaults contributed by sparse Direct children.");

            if (kind == "Object") renderer.sharedMaterial = proof.Value.Reference as Material;
            else if (kind == "Discrete") renderer.updateWhenOffscreen = proof.Value.Number != 0;
            foreach (var use in proof.Writers) MotionAnalysis.RemoveCurves(use.Clip, new[] { use.Binding });
            var after = SampleFrozen(CommitAnimator(controller, root),
                (p, frame) => p.SetFloat("OtherWeight", weights[frame]), weights.Length, Read, kind == "Scalar" ? proof.Value.Number : 20);
            Assert.That(after, Is.EqualTo(before).Within(.00001f));
        }

        [TestCase(false, 0f, false)] [TestCase(true, 0f, false)]
        [TestCase(false, 20f, false)] [TestCase(true, 20f, false)]
        [TestCase(false, 0f, true)] [TestCase(true, 0f, true)]
        [TestCase(false, 20f, true)] [TestCase(true, 20f, true)]
        public void ConstantOutputCounterexample_SparseDirectDefaultMatchingIncludesRetainedValues(bool writeDefaults, float initial, bool zeroWriter)
        {
            Mesh("Other"); var light = root.AddComponent<Light>(); light.intensity = initial;
            var binding = EditorCurveBinding.FloatCurve("", typeof(Light), "m_Intensity");
            var source = TreeController(("One", StaticCurve(binding, zeroWriter ? 70 : initial)), ("OtherWeight", StaticCurve(Binding("Other"), 90)));
            var parameters = source.parameters; parameters.Single(p => p.name == "One").defaultFloat = zeroWriter ? 0 : 1; source.parameters = parameters;
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var index = AvatarAnimationIndex.Create(root, entries, null, new[] { "OtherWeight" });
            bool expected = writeDefaults || initial == 0;
            Assert.That(new InvariantComponentSettings(index, index.Reachability, entries)
                .IsConstantProperty(light, "m_Intensity"), Is.EqualTo(expected));
            Assert.That(ConstantScalarOutput.TryEvaluate(controller.Layers.First().StateMachine.DefaultState.Motion,
                binding, initial, index.Reachability, out float value, writeDefaults), Is.EqualTo(expected));
            if (expected) Assert.That(value, Is.EqualTo(initial));
            var samples = SampleFrozen(source, (p, frame) => p.SetFloat("OtherWeight", .5f), 4, () => light.intensity);
            if (expected) Assert.That(samples, Is.All.EqualTo(initial).Within(.00001f));
            else if (!zeroWriter) Assert.That(samples.Last(), Is.GreaterThan(initial), "Sparse WD-off children retain and accumulate previous output.");
        }

        [Test]
        public void ConstantOutputCounterexample_SharedMotionCachesIncludeWriteDefaults()
        {
            Mesh("Other"); var light = root.AddComponent<Light>(); light.intensity = 20;
            var binding = EditorCurveBinding.FloatCurve("", typeof(Light), "m_Intensity");
            var source = TreeController(("One", StaticCurve(binding, 20)), ("OtherWeight", StaticCurve(Binding("Other"), 90)));
            var parameters = source.parameters; parameters.Single(p => p.name == "One").defaultFloat = 1; source.parameters = parameters;
            source.AddParameter("Switch", AnimatorControllerParameterType.Bool);
            var machine = source.layers[0].stateMachine;
            var first = machine.defaultState; first.writeDefaultValues = true;
            var second = Track(machine.AddState("Shared motion without defaults")); second.motion = first.motion; second.writeDefaultValues = false;
            Edge(first, second, "Switch", 0, AnimatorConditionMode.If).duration = 0;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var index = AvatarAnimationIndex.Create(root, entries, null, new[] { "OtherWeight", "Switch" });
            Assert.That(index.ConstantOutputs.TryProve(light, "m_Intensity", ConstantOutputValue.Scalar(20), out _, out _), Is.False);
            Assert.That(new InvariantComponentSettings(index, index.Reachability, entries).IsConstant(light), Is.False);
            var samples = SampleFrozen(source, (p, frame) =>
            { p.SetFloat("OtherWeight", .5f); p.SetBool("Switch", frame >= 2); }, 8, () => light.intensity);
            Assert.That(samples[0], Is.EqualTo(20).Within(.00001f));
            Assert.That(samples.Last(), Is.GreaterThan(20));
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantOutputCounterexample_LateFirstReferenceKeyMatchesNative(bool writeDefaults)
        {
            Mesh("Other");
            var initial = Track(new Material(Shader.Find("Hidden/InternalErrorShader")));
            var output = Track(new Material(initial)); renderer.sharedMaterial = initial;
            var clip = ConstantMaterialClip(output);
            var binding = AnimationUtility.GetObjectReferenceCurveBindings(clip).Single();
            AnimationUtility.SetObjectReferenceCurve(clip, binding,
                new[] { new ObjectReferenceKeyframe { time = .5f, value = output } });
            var source = Controller(clip); source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var analysis = ConstantOutputs(source, out _);
            if (!analysis.TryProve(renderer, binding.propertyName, ConstantOutputValue.Object(initial), out var proof, out var reason))
            {
                Assert.That(reason, Is.Not.Null.And.Not.Empty);
                return;
            }
            var samples = SampleFrozen(source, (p, frame) => { }, 45,
                () => renderer.sharedMaterial == proof.Value.Reference ? 1 : 0);
            Assert.That(samples, Is.All.EqualTo(1), "A late first PPtr key must not leave an unmodeled initial interval.");
        }

        [TestCase(0f, .5f)] [TestCase(.25f, .75f)] [TestCase(0f, 2f)]
        public void ConstantOutputCounterexample_RemovalPreservesExplicitClipTimeRange(float start, float stop)
        {
            Mesh("Constant");
            var binding = Binding("Constant");
            var clip = StaticCurve(binding, 70);
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, 70));
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.startTime = start; settings.stopTime = stop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            var source = Controller(clip);
            var analysis = ConstantOutputs(source, out var controller);
            Assert.That(analysis.TryProve(renderer, binding.propertyName, ConstantOutputValue.Scalar(20), out var proof, out var reason), Is.True, reason);
            foreach (var writer in proof.Writers) MotionAnalysis.RemoveCurves(writer.Clip, new[] { writer.Binding });
            var built = CommitAnimator(controller, root).animationClips.Single();
            Assert.That(built.length, Is.EqualTo(clip.length).Within(.000001f),
                "Curve removal must preserve the effective clip duration, including explicit start/stop settings.");
        }
    }
}
