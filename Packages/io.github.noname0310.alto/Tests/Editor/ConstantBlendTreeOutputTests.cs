using System;
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
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private AnimatorController FixedOneDimensionalController(float input, AnimationClip[] clips, float[] thresholds)
        {
            var source = TreeController(clips.Select(c => ("Selection", (Motion)c)).ToArray());
            var tree = (BlendTree)source.layers[0].stateMachine.defaultState.motion;
            tree.blendType = BlendTreeType.Simple1D; tree.blendParameter = "Selection"; tree.useAutomaticThresholds = false;
            var children = tree.children;
            for (int i = 0; i < children.Length; i++) children[i].threshold = thresholds[i];
            tree.children = children;
            var parameters = source.parameters; parameters.Single(p => p.name == "Selection").defaultFloat = input;
            source.parameters = parameters;
            return source;
        }

        private static IEnumerable<TestCaseData> FixedOneDimensionalTransformCases()
        {
            var cases = new[] { (-2f, 0, 0), (-1f, 0, 0), (-0.5f, 0, 1), (0f, 1, 1),
                (0.25f, 1, 2), (0.5f, 2, 2), (0.75f, 2, 3), (1f, 3, 3), (2f, 3, 3) };
            foreach (var (input, left, right) in cases)
            foreach (bool writeDefaults in new[] { false, true })
            foreach (string property in new[] { "m_LocalPosition", "m_LocalScale" })
                yield return new TestCaseData(input, left, right, writeDefaults, property);
        }

        [TestCaseSource(nameof(FixedOneDimensionalTransformCases))]
        public void FixedOneDimensionalTransformsPreserveNativePoseDurationAndOtherOutputs(
            float input, int left, int right, bool writeDefaults, string property)
        {
            Mesh("Face"); var settings = ObjectSettings(); var target = PolicyChild("Body adjustment").transform;
            var initial = property == "m_LocalScale" ? new Vector3(1.25f, -0.75f, 0) : new Vector3(0.2f, -0.3f, 0.4f);
            if (property == "m_LocalScale") target.localScale = initial; else target.localPosition = initial;
            var clips = Enumerable.Range(0, 4).Select(i =>
            {
                var clip = InitialTransformClip(target, property);
                if (i != left && i != right)
                    foreach (char axis in "xyz") AnimationUtility.SetEditorCurve(clip,
                        EditorCurveBinding.FloatCurve(target.name, typeof(Transform), property + "." + axis),
                        AnimationCurve.Linear(0, 4, 0.3f, 6));
                AnimationUtility.SetEditorCurve(clip, Binding("Face"), AnimationCurve.Linear(0, 0, 0.3f, 70));
                return clip;
            }).ToArray();
            var source = FixedOneDimensionalController(input, clips, new[] { -1f, 0, 0.5f, 1 });
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var originalClips = clips.Select(EditorJsonUtility.ToJson).ToArray();
            var beforePoses = new List<Vector3>();
            var before = SampleFrozen(source, (p, i) => { }, 30, () =>
            {
                beforePoses.Add(property == "m_LocalScale" ? target.localScale : target.localPosition);
                return renderer.GetBlendShapeWeight(0);
            });
            if (property == "m_LocalScale") target.localScale = initial; else target.localPosition = initial;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            OptimizeObjectCurves(controller, settings);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings())
                .Any(b => b.path == target.name), Is.False);
            var output = CommitAnimator(controller, root);
            var afterPoses = new List<Vector3>();
            var after = SampleFrozen(output, (p, i) => { }, 30, () =>
            {
                afterPoses.Add(property == "m_LocalScale" ? target.localScale : target.localPosition);
                return renderer.GetBlendShapeWeight(0);
            });
            Assert.That(after, Is.EqualTo(before).Within(0.00001f));
            Assert.That(after.Max() - after.Min(), Is.GreaterThan(30), "The other animated output must still advance in time");
            for (int i = 0; i < beforePoses.Count; i++)
            {
                Assert.That(Vector3.Distance(beforePoses[i], initial), Is.LessThan(0.000001f));
                Assert.That(Vector3.Distance(afterPoses[i], beforePoses[i]), Is.LessThan(0.000001f));
            }
            Assert.That(clips.Select(EditorJsonUtility.ToJson), Is.EqualTo(originalClips));
        }

        [TestCase("Preserved")] [TestCase("ExternalInput")] [TestCase("OtherController")]
        [TestCase("DuplicateThreshold")] [TestCase("MirroredChild")] [TestCase("MissingMotion")]
        [TestCase("NonzeroNeighbour")]
        public void FixedOneDimensionalProofRetainsUnverifiedBranches(string reason)
        {
            var settings = ObjectSettings(); var target = PolicyChild("Body adjustment").transform;
            var first = InitialTransformClip(target, "m_LocalScale");
            target.localScale = Vector3.one * 2;
            var second = InitialTransformClip(target, "m_LocalScale"); target.localScale = Vector3.one;
            var source = FixedOneDimensionalController(reason == "NonzeroNeighbour" ? 0.25f : 0,
                new[] { first, second }, new[] { 0f, 1 });
            var tree = (BlendTree)source.layers[0].stateMachine.defaultState.motion;
            var children = tree.children;
            if (reason == "DuplicateThreshold") children[1].threshold = 0;
            if (reason == "MirroredChild") children[0].mirror = true;
            if (reason == "MissingMotion") children[0].motion = null;
            tree.children = children;
            if (reason == "ExternalInput")
            {
                var parameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
                parameters.parameters = new[] { new VRCExpressionParameters.Parameter
                    { name = "Selection", valueType = VRCExpressionParameters.ValueType.Float } };
                root.GetComponent<VRCAvatarDescriptor>().expressionParameters = parameters;
            }
            var cloner = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controller = cloner.Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            if (reason == "OtherController") entries[VRCAvatarDescriptor.AnimLayerType.Gesture] =
                cloner.Clone(FixedOneDimensionalController(1, new[] { Clip() }, new[] { 0f }));
            var analysis = AnimatorReachability.Analyze(root, entries.Values,
                reason == "Preserved" ? new[] { "Selection" } : Array.Empty<string>());
            InvariantObjectCurves.Apply(root, entries, null, settings, analysis, new List<string>());
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings())
                .Any(b => b.path == target.name), Is.True);
        }

        [Test]
        public void UnsortedOneDimensionalThresholdsPreserveNativeConstantOutput()
        {
            Mesh("Face"); var settings = ObjectSettings(); var target = PolicyChild("Body adjustment").transform;
            target.localPosition = Vector3.one * 2; var inactive = InitialTransformClip(target);
            target.localPosition = Vector3.one; var active = InitialTransformClip(target);
            var source = FixedOneDimensionalController(0.25f, new[] { inactive, active, active }, new[] { 1f, 0, 0.5f });
            var before = SampleFrozen(source, (p, i) => { }, 4, () => target.localPosition.x);
            target.localPosition = Vector3.one;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            OptimizeObjectCurves(controller, settings);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings())
                .Any(b => b.path == target.name), Is.False);
            var after = SampleFrozen(CommitAnimator(controller, root), (p, i) => { }, 4, () => target.localPosition.x);
            Assert.That(after, Is.EqualTo(before).Within(0.000001f));
            Assert.That(before.All(v => Mathf.Abs(v - 1) < 0.000001f), Is.True);
        }

        [TestCase(false, false, 0f)] [TestCase(false, true, 0f)]
        [TestCase(true, false, 0f)] [TestCase(true, true, 0f)]
        [TestCase(false, false, 0.5f)] [TestCase(false, true, 0.5f)]
        [TestCase(true, false, 0.5f)] [TestCase(true, true, 0.5f)]
        public void FixedOneDimensionalVisibilityPreservesNativeDiscreteOutput(bool initial, bool writeDefaults, float input)
        {
            Mesh("Face"); var settings = ObjectSettings(); var target = PolicyChild("Visibility"); target.SetActive(initial);
            var binding = EditorCurveBinding.FloatCurve(target.name, typeof(GameObject), "m_IsActive");
            var inactive = StaticCurve(binding, initial ? 0 : 1); var active = StaticCurve(binding, initial ? 1 : 0);
            var source = FixedOneDimensionalController(input, new[] { inactive, active, active }, new[] { -1f, 0, 1 });
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var before = SampleFrozen(source, (p, i) => { }, 4, () => target.activeSelf ? 1 : 0);
            target.SetActive(initial);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            OptimizeObjectCurves(controller, settings);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings())
                .Any(b => b.path == target.name), Is.False);
            var after = SampleFrozen(CommitAnimator(controller, root), (p, i) => { }, 4, () => target.activeSelf ? 1 : 0);
            Assert.That(after, Is.EqualTo(before));
            Assert.That(before.All(v => v == (initial ? 1 : 0)), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void FixedOneDimensionalConstraintSettingsPermitOnlyInvariantSourcePruning(bool preserved)
        {
            Mesh("Face"); var settings = ObjectSettings(); var sourceBone = PolicyChild("Source").transform;
            var unused = PolicyChild("Unused").transform; var target = PolicyChild("Output").transform;
            var constraint = ConstraintWithZeroSources(typeof(VRCParentConstraint), sourceBone, unused, target, "Writer");
            var binding = EditorCurveBinding.FloatCurve("Writer", typeof(VRCParentConstraint), "Sources.source0.Weight");
            var zero = StaticCurve(binding, 0); var varying = Clip();
            AnimationUtility.SetEditorCurve(varying, binding, AnimationCurve.Linear(0, 0, 1, 1));
            var source = FixedOneDimensionalController(0, new[] { zero, varying }, new[] { 0f, 1 });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var preservedInputs = preserved ? new[] { "Selection" } : Array.Empty<string>();
            Assert.That(ConstraintSourcePruning.Apply(root, entries, null, settings, preservedInputs, new List<string>()),
                Is.EqualTo(preserved ? 1 : 2));
            Assert.That(constraint.Sources.Count, Is.EqualTo(preserved ? 2 : 1));
            Assert.That(constraint.Sources[preserved ? 1 : 0].SourceTransform, Is.SameAs(sourceBone));
            Assert.That(constraint.Sources[preserved ? 1 : 0].Weight, Is.EqualTo(1));
            if (preserved) Assert.That(controller.AllReachableNodes().OfType<VirtualClip>()
                .SelectMany(c => c.GetFloatCurveBindings()), Does.Contain(binding), "The preserved dynamic source remains animated");
        }

        [Test]
        public void InterpolatedScalarTransformsPreserveNativeOutput(
            [Values(0.25f, 0.5f, 0.75f)] float input,
            [Values("m_LocalPosition", "m_LocalScale")] string property,
            [Values(false, true)] bool writeDefaults, [Values(false, true)] bool directWrapper)
        {
            Mesh("Face"); var settings = ObjectSettings(); var target = PolicyChild("Constant result").transform;
            var left = property == "m_LocalPosition" ? new Vector3(-2, -4, 2) : new Vector3(0.5f, 1, 0);
            var right = property == "m_LocalPosition" ? -left : new Vector3(2.5f, -1, 2);
            var initial = left * (1 - input) + right * input;
            void Set(Vector3 value) { if (property == "m_LocalPosition") target.localPosition = value; else target.localScale = value; }
            Set(left); var first = InitialTransformClip(target, property);
            Set(right); var second = InitialTransformClip(target, property);
            foreach (var clip in new[] { first, second })
                AnimationUtility.SetEditorCurve(clip, Binding("Face"), AnimationCurve.Linear(0, 0, 0.2f, 70));
            Set(initial);
            var source = FixedOneDimensionalController(input, new[] { first, second }, new[] { 0f, 1 });
            var state = source.layers[0].stateMachine.defaultState; state.writeDefaultValues = writeDefaults;
            if (directWrapper)
            {
                source.AddParameter("Unit", AnimatorControllerParameterType.Float);
                var parameters = source.parameters; parameters.Single(p => p.name == "Unit").defaultFloat = 1; source.parameters = parameters;
                var tree = Track(new BlendTree { blendType = BlendTreeType.Direct }); tree.AddChild(state.motion);
                var children = tree.children; children[0].directBlendParameter = "Unit"; tree.children = children;
                state.motion = tree;
            }
            var sourceClips = new[] { first, second }.Select(EditorJsonUtility.ToJson).ToArray();
            var beforePose = new List<Vector3>();
            var before = SampleFrozen(source, (p, frame) => { }, 32, () =>
            {
                beforePose.Add(property == "m_LocalPosition" ? target.localPosition : target.localScale);
                return renderer.GetBlendShapeWeight(0);
            });
            Set(initial);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var motion = controller.Layers.First().StateMachine.DefaultState.Motion;
            var analysis = AnimatorReachability.Analyze(root, new[] { controller });
            foreach (char axis in "xyz")
            {
                var binding = EditorCurveBinding.FloatCurve(target.name, typeof(Transform), property + "." + axis);
                Assert.That(ConstantScalarOutput.TryEvaluate(motion, binding, initial["xyz".IndexOf(axis)], analysis, out float value), Is.True);
                Assert.That(value, Is.EqualTo(initial["xyz".IndexOf(axis)]));
            }
            OptimizeObjectCurves(controller, settings);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings())
                .Any(b => b.path == target.name), Is.False, "Weighted output equals the existing initial pose");
            Assert.That(property == "m_LocalPosition" ? target.localPosition : target.localScale, Is.EqualTo(initial));
            var afterPose = new List<Vector3>();
            var after = SampleFrozen(CommitAnimator(controller, root), (p, frame) => { }, 32, () =>
            {
                afterPose.Add(property == "m_LocalPosition" ? target.localPosition : target.localScale);
                return renderer.GetBlendShapeWeight(0);
            });
            Assert.That(after, Is.EqualTo(before).Within(0.00001f));
            Assert.That(after.Max() - after.Min(), Is.GreaterThan(30), "Other outputs and state timing still advance");
            for (int i = 0; i < beforePose.Count; i++)
            {
                Assert.That(Vector3.Distance(beforePose[i], initial), Is.LessThan(0.000001f));
                Assert.That(Vector3.Distance(afterPose[i], beforePose[i]), Is.LessThan(0.000001f));
            }
            Assert.That(new[] { first, second }.Select(EditorJsonUtility.ToJson), Is.EqualTo(sourceClips));
        }

        [TestCase("Variable")] [TestCase("Preserved")] [TestCase("NonconstantCurve")]
        [TestCase("MissingDefault")] [TestCase("Discrete")] [TestCase("ThresholdOverflow")]
        [TestCase("DuplicateThreshold")] [TestCase("Event")] [TestCase("NormalizedDirect")] [TestCase("Quaternion")]
        public void InterpolatedScalarProofRetainsUnverifiedValues(string reason)
        {
            ObjectSettings(); var target = PolicyChild("Target").transform;
            var binding = EditorCurveBinding.FloatCurve(target.name, typeof(Transform), "m_LocalScale.y");
            if (reason == "Discrete") binding = EditorCurveBinding.DiscreteCurve(target.name, typeof(Transform), "m_LocalScale.y");
            if (reason == "Quaternion") binding.propertyName = "m_LocalRotation.x";
            var first = StaticCurve(binding, 0); var second = StaticCurve(binding, 2);
            if (reason == "NonconstantCurve") AnimationUtility.SetEditorCurve(second, binding, AnimationCurve.Linear(0, 0, 1, 2));
            if (reason == "MissingDefault") AnimationUtility.SetEditorCurve(second, binding, null);
            if (reason == "Event") AnimationUtility.SetAnimationEvents(second, new[] { new AnimationEvent { functionName = "Observe" } });
            var source = FixedOneDimensionalController(reason == "ThresholdOverflow" ? 0 : 0.5f, new[] { first, second },
                reason == "ThresholdOverflow" ? new[] { -float.MaxValue, float.MaxValue } :
                reason == "DuplicateThreshold" ? new[] { 0f, 0f } : new[] { 0f, 1f });
            if (reason == "Variable") FreezeExpressions(new VRCExpressionParameters.Parameter
                { name = "Selection", valueType = VRCExpressionParameters.ValueType.Float });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var tree = (VirtualBlendTree)controller.Layers.First().StateMachine.DefaultState.Motion;
            if (reason == "NormalizedDirect") { tree.BlendType = BlendTreeType.Direct; tree.NormalizedBlendValues = true; }
            var analysis = AnimatorReachability.Analyze(root, new[] { controller }, reason == "Preserved" ? new[] { "Selection" } : Array.Empty<string>());
            Assert.That(ConstantScalarOutput.TryEvaluate(tree, binding, reason == "MissingDefault" ? null : 1f, analysis, out _), Is.False);
        }

        [Test]
        public void InterpolatedComponentFloatSettingsCanReleaseUnusedSources()
        {
            Mesh("Face"); var settings = ObjectSettings(); var input = PolicyChild("Source").transform; var unused = PolicyChild("Unused").transform;
            var target = PolicyChild("Output").transform;
            var constraint = ConstraintWithZeroSources(typeof(VRCParentConstraint), input, unused, target, "Writer");
            var binding = EditorCurveBinding.FloatCurve("Writer", typeof(VRCParentConstraint), "Sources.source0.Weight");
            var source = FixedOneDimensionalController(0.5f, new[] { StaticCurve(binding, -1), StaticCurve(binding, 1) }, new[] { 0f, 1 });
            var samples = SampleFrozen(source, (p, i) => { }, 4, () => constraint.Sources[0].Weight);
            Assert.That(samples, Is.All.EqualTo(0), "Native Animator interpolation also produces zero source weight");
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            Assert.That(ConstraintSourcePruning.Apply(root, entries, null, settings, null, new List<string>()), Is.EqualTo(2));
            Assert.That(constraint.Sources.Count, Is.EqualTo(1));
            Assert.That(constraint.Sources[0].SourceTransform, Is.SameAs(input));
        }

        [Test]
        public void InterpolatedBooleanConstraintSettingsRetainTheirDiscreteTracks()
        {
            var settings = ObjectSettings(); var input = PolicyChild("Source").transform;
            var constraint = ConstraintWithZeroSources(typeof(VRCParentConstraint), input,
                PolicyChild("Unused").transform, PolicyChild("Output").transform, "Writer");
            var binding = EditorCurveBinding.FloatCurve("Writer", typeof(VRCParentConstraint), "Locked");
            var source = FixedOneDimensionalController(0.5f, new[] { StaticCurve(binding, 0), StaticCurve(binding, 2) }, new[] { 0f, 1 });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            Assert.That(ConstraintSourcePruning.Apply(root, entries, null, settings, null, new List<string>()), Is.Zero,
                "A float-valued animation binding does not turn a serialized Boolean into a continuous scalar");
            Assert.That(constraint.Sources.Count, Is.EqualTo(3));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Count(c => c.GetFloatCurve(binding) != null), Is.EqualTo(2));
        }

        [Test]
        public void InterpolatedNearIdentityScaleIsNotAnInvariantInitialPose()
        {
            Mesh("Face"); var settings = ObjectSettings(); var target = PolicyChild("Body adjustment").transform;
            target.localScale = Vector3.one * 0.95f; var first = InitialTransformClip(target, "m_LocalScale");
            target.localScale = Vector3.one * 1.0526f; var second = InitialTransformClip(target, "m_LocalScale");
            target.localScale = Vector3.one;
            var source = FixedOneDimensionalController(0.5f, new[] { first, second }, new[] { 0f, 1 });
            var samples = SampleFrozen(source, (p, i) => { }, 4, () => target.localScale.x);
            Assert.That(samples.All(v => v > 1.001f && v < 1.002f), Is.True);
            target.localScale = Vector3.one;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            OptimizeObjectCurves(controller, settings);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings())
                .Count(b => b.path == target.name), Is.EqualTo(6));
            Assert.That(target.localScale, Is.EqualTo(Vector3.one));
        }
    }
}
