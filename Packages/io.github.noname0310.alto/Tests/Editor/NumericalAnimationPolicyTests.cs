using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static readonly Vector2[] NumericalBlendInputs = {
            Vector2.zero, Vector2.right, Vector2.left, Vector2.up, Vector2.down,
            new(.17f, .23f), new(.71f, -.37f), new(-.43f, .51f), new(.2f, .3f)
        };

        private AvatarAnimationIndex NumericalIndex(AnimatorController source, out VirtualAnimatorController controller)
        {
            controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            return AvatarAnimationIndex.Create(root,
                new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller },
                null, new[] { "OtherWeight" });
        }

        private void DeclareFiniteBlendInputs(AnimatorController controller)
        {
            var driver = Track(controller.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
            driver.parameters = new List<VRC_AvatarParameterDriver.Parameter> {
                new() { name = "BlendX", type = VRC_AvatarParameterDriver.ChangeType.Random, valueMin = -1, valueMax = 1 },
                new() { name = "BlendY", type = VRC_AvatarParameterDriver.ChangeType.Random, valueMin = -1, valueMax = 1 }
            };
        }

        private static IEnumerable<TestCaseData> NumericalTwoDimensionalCases()
        {
            foreach (var type in new[] { BlendTreeType.SimpleDirectional2D, BlendTreeType.FreeformDirectional2D, BlendTreeType.FreeformCartesian2D })
            foreach (bool light in new[] { false, true })
            foreach (bool writeDefaults in new[] { false, true })
            foreach (bool allow in new[] { false, true }) yield return new TestCaseData(type, light, writeDefaults, allow);
        }

        [TestCaseSource(nameof(NumericalTwoDimensionalCases))]
        public void NumericalAnimationPolicy_IdenticalTwoDimensionalContributorsMatchNative(
            BlendTreeType type, bool lightOutput, bool writeDefaults, bool allow)
        {
            Mesh("Other"); var settings = ObjectSettings(); var target = PolicyChild("Continuous output").transform;
            var initial = new Vector3(.7f, 1.1f, 70); target.localPosition = initial;
            var light = lightOutput ? target.gameObject.AddComponent<Light>() : null;
            if (light != null) light.intensity = initial.x;
            var clips = Enumerable.Range(0, 5).Select(_ => lightOutput
                ? StaticCurve(EditorCurveBinding.FloatCurve(target.name, typeof(Light), "m_Intensity"), initial.x)
                : InitialTransformClip(target)).ToArray();
            var source = CounterexampleTwoDimensionalController(type, clips);
            DeclareFiniteBlendInputs(source);
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            Vector3 Read() => lightOutput ? new Vector3(light.intensity, 0, 0) : target.localPosition;
            List<Vector3> Sample(AnimatorController current)
            {
                var result = new List<Vector3>();
                SampleFrozen(current, (p, frame) => {
                    p.SetFloat("BlendX", NumericalBlendInputs[frame].x); p.SetFloat("BlendY", NumericalBlendInputs[frame].y);
                }, NumericalBlendInputs.Length, () => { result.Add(Read()); return 0; });
                return result;
            }
            var before = Sample(source); target.localPosition = initial; if (light != null) light.intensity = initial.x;
            var index = NumericalIndex(source, out var controller); var policy = GeometryPolicy(allow);
            int removed = lightOutput ? ConstantComponentProperties.Apply(index, settings, _ => false, new List<string>(), policy)
                : ConstantTransformOutputs.Apply(index, settings, _ => false, new List<string>(), policy);
            Assert.That(removed, Is.EqualTo(allow ? clips.Length * (lightOutput ? 1 : 3) : 0));
            var after = Sample(CommitAnimator(controller, root));
            for (int frame = 0; frame < before.Count; frame++)
                for (int channel = 0; channel < 3; channel++)
                    Assert.That(NumericalEquivalence.Same(before[frame][channel], after[frame][channel], allow), Is.True,
                        type + ", frame " + frame + ", channel " + channel + ": " + before[frame] + " -> " + after[frame]);
            Assert.That(clips.All(c => AnimationUtility.GetCurveBindings(c).Length == (lightOutput ? 1 : 3)), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void NumericalAnimationPolicy_ExistingInvariantTransformConsumerUsesPolicy(bool allow)
        {
            Mesh("Other"); var settings = ObjectSettings(); var target = PolicyChild("Invariant").transform;
            target.localPosition = new Vector3(.7f, 1.1f, 70);
            var clips = Enumerable.Range(0, 5).Select(_ => InitialTransformClip(target)).ToArray();
            var source = CounterexampleTwoDimensionalController(BlendTreeType.FreeformCartesian2D, clips);
            DeclareFiniteBlendInputs(source);
            var index = NumericalIndex(source, out var controller);
            InvariantObjectCurves.Apply(root, index.ControllerEntries, null, settings, index.Reachability,
                new List<string>(), GeometryPolicy(allow), index);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Sum(c => c.GetFloatCurveBindings().Count(b =>
                    b.type == typeof(Transform) && b.path == target.name && b.propertyName.StartsWith("m_LocalPosition."))),
                Is.EqualTo(allow ? 0 : 15));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().All(c => MotionAnalysis.EndTime(c) == .3f), Is.True,
                "The removed curves' timing is retained by duration guards.");
        }

        private void NumericalWeightControl(AnimatorController source, bool playable)
        {
            var state = source.layers[0].stateMachine.defaultState;
            if (playable)
            {
                var control = Track(state.AddStateMachineBehaviour<VRCPlayableLayerControl>());
                control.layer = VRC_PlayableLayerControl.BlendableLayer.FX;
                control.goalWeight = .5f; control.blendDuration = .2f;
            }
            else
            {
                var control = Track(state.AddStateMachineBehaviour<VRCAnimatorLayerControl>());
                control.playable = VRC_AnimatorLayerControl.BlendableLayer.FX;
                control.layer = 1; control.goalWeight = .5f; control.blendDuration = .2f;
            }
        }

        private AvatarAnimationIndex NumericalVrchatIndex(AnimatorController source, out VirtualAnimatorController controller)
        {
            controller = new CloneContext(VRChatPlatformAnimatorBindings.Instance).Clone(source, VRCAvatarDescriptor.AnimLayerType.FX);
            return AvatarAnimationIndex.Create(root,
                new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, null);
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void NumericalInvariantTransforms_ExactProofSurvivesDynamicSdkWeights(bool allow, bool playable)
        {
            Mesh("Other"); var settings = ObjectSettings(); var target = PolicyChild("Exact invariant").transform;
            target.localPosition = Vector3.one * 2;
            var clips = new[] { InitialTransformClip(target), InitialTransformClip(target) };
            var source = Controller(clips[0]); var upper = Controller(clips[1]).layers[0]; upper.name = "Weighted invariant";
            source.layers = new[] { source.layers[0], upper }; NumericalWeightControl(source, playable);
            List<Vector3> Sample(AnimatorController current)
            {
                var result = new List<Vector3>();
                SampleFrozen(current, (p, frame) => p.SetLayerWeight(1, (frame % 5) * .25f), 15,
                    () => { result.Add(target.localPosition); return 0; });
                return result;
            }
            var before = Sample(source); target.localPosition = Vector3.one * 2;
            var index = NumericalVrchatIndex(source, out var controller);
            Assert.That(FxPropertyOutput.WeightCanChange(index.ControllerEntries, controller, controller.Layers.Last(), index.Reachability), Is.True,
                "The fixture must exercise the SDK-weight rejection that previously hid the exact proof.");
            InvariantObjectCurves.Apply(root, index.ControllerEntries, null, settings, index.Reachability,
                new List<string>(), GeometryPolicy(allow), index);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings())
                .Any(b => b.type == typeof(Transform) && b.path == target.name), Is.False);
            target.localPosition = Vector3.one * 2;
            var after = Sample(CommitAnimator(controller, root));
            for (int frame = 0; frame < before.Count; frame++)
                for (int axis = 0; axis < 3; axis++)
                    Assert.That(MotionAnalysis.Same(before[frame][axis], after[frame][axis]), Is.True);
            Assert.That(before.All(p => p == Vector3.one * 2), Is.True);
            Assert.That(clips.All(c => AnimationUtility.GetCurveBindings(c).Length == 3), Is.True);
        }

        [TestCase("DifferentContributor")] [TestCase("RoundedOnly")]
        public void NumericalInvariantTransforms_DynamicSdkWeightsKeepUnprovedOutputs(string reason)
        {
            Mesh("Other"); var settings = ObjectSettings(); var target = PolicyChild("Weighted output").transform;
            target.localPosition = new Vector3(.7f, 1.1f, 70);
            var initial = target.localPosition;
            var source = Controller(InitialTransformClip(target));
            var upperController = reason == "RoundedOnly"
                ? CounterexampleTwoDimensionalController(BlendTreeType.FreeformCartesian2D,
                    Enumerable.Range(0, 5).Select(_ => InitialTransformClip(target)).ToArray())
                : Controller(InitialTransformClip(target));
            if (reason == "RoundedOnly")
            {
                source.AddParameter("BlendX", AnimatorControllerParameterType.Float);
                source.AddParameter("BlendY", AnimatorControllerParameterType.Float);
                DeclareFiniteBlendInputs(source);
            }
            else AnimationUtility.SetEditorCurve((AnimationClip)source.layers[0].stateMachine.defaultState.motion,
                EditorCurveBinding.FloatCurve(target.name, typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Constant(0, .3f, 3));
            var upper = upperController.layers[0]; upper.name = "Weighted output";
            source.layers = new[] { source.layers[0], upper }; NumericalWeightControl(source, true);
            var before = SampleFrozen(source, (p, frame) => p.SetLayerWeight(1, (frame % 5) * .25f), 15, () => target.localPosition.x);
            if (reason == "DifferentContributor") Assert.That(before.Max() - before.Min(), Is.GreaterThan(1));
            target.localPosition = initial;
            var index = NumericalVrchatIndex(source, out var controller);
            InvariantObjectCurves.Apply(root, index.ControllerEntries, null, settings, index.Reachability,
                new List<string>(), GeometryPolicy(true), index);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings())
                .Any(b => b.type == typeof(Transform) && b.path == target.name && b.propertyName == "m_LocalPosition.x"), Is.True);
            target.localPosition = initial;
            var after = SampleFrozen(CommitAnimator(controller, root),
                (p, frame) => p.SetLayerWeight(1, (frame % 5) * .25f), 15, () => target.localPosition.x);
            Assert.That(after, Is.EqualTo(before));
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void NumericalInvariantComponents_ConstraintConsumerUsesItsOwnFeature(bool constraints, bool animation)
        {
            InNativeConstraintScene(() =>
            {
                Mesh("Other"); var settings = ObjectSettings(); var input = PolicyChild("Source").transform;
                var output = PolicyChild("Solved output").transform; var constraint = FollowingParent(input, output);
                var clips = Enumerable.Range(0, 5).Select(_ => ConstantConstraintClip(constraint, "GlobalWeight", Vector3.right)).ToArray();
                var source = CounterexampleTwoDimensionalController(BlendTreeType.FreeformCartesian2D, clips);
                DeclareFiniteBlendInputs(source);
                List<Matrix4x4> Sample(AnimatorController current)
                {
                    var result = new List<Matrix4x4>();
                    SampleFrozen(current, (p, frame) => {
                        p.SetFloat("BlendX", NumericalBlendInputs[frame].x); p.SetFloat("BlendY", NumericalBlendInputs[frame].y);
                        input.localPosition = new Vector3(frame * .13f, frame * .07f, frame * -.03f);
                        input.localRotation = Quaternion.Euler(frame * 3, frame * 7, frame * 2);
                    }, NumericalBlendInputs.Length, () => {
                        if (constraint != null) constraint.ApplyConfigurationChanges();
                        EvaluateNativeConstraints(); result.Add(output.localToWorldMatrix); return 0;
                    });
                    return result;
                }
                EvaluateNativeConstraints(constraint);
                var before = Sample(source);
                Assert.That(before.Select(m => m.m03).Distinct().Count(), Is.GreaterThan(2), "Exercise a moving native solver.");
                input.localPosition = output.localPosition = Vector3.zero;
                input.localRotation = output.localRotation = Quaternion.identity;
                constraint.GlobalWeight = 1; constraint.ApplyConfigurationChanges();
                var index = NumericalVrchatIndex(source, out var controller);
                var allowed = (constraints ? NumericalOptimization.Constraints : NumericalOptimization.None) |
                    (animation ? NumericalOptimization.AnimationOutputs : NumericalOptimization.None);
                var messages = new List<string>();
                Assert.That(ParentConstraintOptimization.Apply(root, index.ControllerEntries, null, settings, null,
                    messages, commitNativePaths: true, numericPolicy: GeometryPolicy(allowed)), Is.EqualTo(constraints ? 1 : 0));
                if (constraints) Assert.That(ProofSummary(messages, "parent constraints"),
                    Does.Contain("strict proof=0; numerical permission=1 (Constraints=1); initialization changes=0"));
                else Assert.That(messages.Any(m => m.StartsWith("Proof assumptions [")), Is.False);
                Assert.That(constraint == null, Is.EqualTo(constraints));
                Assert.That(output.parent, Is.SameAs(constraints ? input : root.transform));
                var after = Sample(CommitAnimator(controller, root));
                for (int frame = 0; frame < before.Count; frame++)
                    Assert.That(NumericalEquivalence.Same(before[frame], after[frame], constraints), Is.True, "Frame " + frame);
                Assert.That(clips.All(c => AnimationUtility.GetCurveBindings(c).Length == 1), Is.True);
            });
        }

        [TestCase("StrictOwner")] [TestCase("StrictTarget")] [TestCase("StrictDescendant")] [TestCase("StrictSource")]
        [TestCase("ContactReader")] [TestCase("AnimatedNativeReader")] [TestCase("OtherSolver")]
        [TestCase("Culling")] [TestCase("NativeWriter")] [TestCase("DynamicSdkWeight")]
        [TestCase("DifferentLeaf")] [TestCase("UnknownInput")] [TestCase("PreservedInput")]
        public void NumericalInvariantComponents_ConstraintRoundingRetainsUnprovedConsumers(string reason)
        {
            Mesh("Other"); var settings = ObjectSettings(); var input = PolicyChild("Source").transform;
            var output = PolicyChild("Solved output").transform; var constraint = FollowingParent(input, output);
            var clips = Enumerable.Range(0, 5).Select(_ => ConstantConstraintClip(constraint, "GlobalWeight", Vector3.right)).ToArray();
            if (reason == "DifferentLeaf") AnimationUtility.SetEditorCurve(clips[2],
                EditorCurveBinding.FloatCurve(constraint.name, typeof(VRCParentConstraint), "GlobalWeight"), AnimationCurve.Constant(0, .3f, .5f));
            var source = CounterexampleTwoDimensionalController(BlendTreeType.FreeformCartesian2D, clips);
            DeclareFiniteBlendInputs(source);
            if (reason == "DynamicSdkWeight") NumericalWeightControl(source, true);
            if (reason == "UnknownInput")
            {
                var driver = Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
                driver.parameters = new List<VRC_AvatarParameterDriver.Parameter> {
                    new() { name = "BlendX", type = VRC_AvatarParameterDriver.ChangeType.Copy, source = "UnknownRuntimeInput" }
                };
            }
            if (reason == "ContactReader") PolicyChild("Contact", output).AddComponent<VRCContactReceiver>();
            if (reason == "AnimatedNativeReader")
            {
                var reader = PolicyChild("Simulation").AddComponent<VRCPhysBone>();
                reader.rootTransform = PolicyChild("Initial simulation root").transform;
                foreach (var clip in clips) AnimationUtility.SetObjectReferenceCurve(clip,
                    EditorCurveBinding.PPtrCurve(reader.name, typeof(VRCPhysBone), "rootTransform"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = output } });
            }
            VRCParentConstraint otherSolver = null;
            if (reason == "OtherSolver")
            {
                otherSolver = FollowingParent(output, PolicyChild("Other solved output").transform, "Other solver");
                // Keep a real native consumer: a full-weight one-source reader
                // can itself legitimately be replaced with exact parenting.
                otherSolver.GlobalWeight = .5f; otherSolver.ApplyConfigurationChanges();
            }
            if (reason == "NativeWriter") output.gameObject.AddComponent<VRCPositionConstraint>();
            if (reason == "Culling")
            {
                output.gameObject.AddComponent<MeshRenderer>();
                root.AddComponent<Animator>().cullingMode = AnimatorCullingMode.CullCompletely;
            }
            Transform strict = reason == "StrictOwner" ? constraint.transform : reason == "StrictTarget" ? output :
                reason == "StrictSource" ? input : reason == "StrictDescendant" ? PolicyChild("Strict child", output).transform : null;
            if (strict != null) strict.gameObject.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences =
                NumericalOptimization.All & ~NumericalOptimization.Constraints;
            var policy = GeometryPolicy(true); var index = NumericalVrchatIndex(source, out var controller);
            var preservedInputs = reason == "PreservedInput" ? new[] { "BlendX" } : null;
            var snapshot = new ObjectAnalysisSnapshot(root, index.ControllerEntries, null, settings, preservedInputs, null);
            if (reason == "PreservedInput")
            {
                Assert.That(snapshot.Index.ForObject(constraint), Is.Not.Empty);
                Assert.That(snapshot.Index.ForObject(constraint).All(u => u.PreservedInput), Is.True,
                    "The production snapshot must receive the preserved blend input.");
            }
            if (otherSolver != null)
                Assert.That(snapshot.Dependencies(false).DependsOn(otherSolver, output), Is.True,
                    "The retained native solver must actually read the candidate output.");
            int removed = ParentConstraintOptimization.Apply(root, index.ControllerEntries, null, settings, preservedInputs,
                new List<string>(), snapshot: snapshot, commitNativePaths: true, numericPolicy: policy);
            Assert.That(constraint != null && output.parent == root.transform, Is.True);
            if (reason == "OtherSolver") Assert.That(otherSolver != null && otherSolver.GlobalWeight == .5f, Is.True);
            Assert.That(removed, Is.Zero);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings())
                .Any(b => b.type == typeof(VRCParentConstraint) && b.propertyName == "GlobalWeight"), Is.True);
        }

        [TestCase("Allowed")] [TestCase("StrictNumerics")] [TestCase("NegativeLeaf")]
        [TestCase("UnknownInput")] [TestCase("DynamicSdkWeight")]
        public void NumericalInvariantComponents_HeadChopProvesOnlyScaleSignWithoutRemovingCurves(string reason)
        {
            Mesh("Other"); var pair = HeadChopPair(); pair.Chop.globalScaleFactor = .7f;
            var binding = EditorCurveBinding.FloatCurve(pair.Chop.name, typeof(VRCHeadChop), "globalScaleFactor");
            var clips = Enumerable.Range(0, 5).Select(_ => StaticCurve(binding, .7f)).ToArray();
            if (reason == "NegativeLeaf") AnimationUtility.SetEditorCurve(clips[2], binding, AnimationCurve.Constant(0, .3f, -1));
            var source = CounterexampleTwoDimensionalController(BlendTreeType.FreeformCartesian2D, clips);
            DeclareFiniteBlendInputs(source);
            if (reason == "DynamicSdkWeight") NumericalWeightControl(source, true);
            if (reason == "UnknownInput")
            {
                var driver = Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
                driver.parameters = new List<VRC_AvatarParameterDriver.Parameter> {
                    new() { name = "BlendX", type = VRC_AvatarParameterDriver.ChangeType.Copy, source = "UnknownRuntimeInput" }
                };
            }
            GeometryPolicy(reason != "StrictNumerics");
            var values = SampleFrozen(source, (p, frame) => {
                p.SetFloat("BlendX", NumericalBlendInputs[frame].x); p.SetFloat("BlendY", NumericalBlendInputs[frame].y);
            }, NumericalBlendInputs.Length, () => pair.Chop.globalScaleFactor);
            pair.Chop.globalScaleFactor = .7f;
            var index = NumericalVrchatIndex(source, out var controller); var mutations = TransformMutationAnalysis.Create(root, index);
            bool known = reason == "Allowed" || reason == "StrictNumerics";
            Assert.That(mutations.MayChangeScale(pair.Frame), Is.True, "Sign evidence must never make the frame static.");
            Assert.That(mutations.MayReverseScale(pair.Frame), Is.EqualTo(!known));
            if (known) Assert.That(values.All(v => MotionAnalysis.Finite(v) && v >= 0), Is.True);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings())
                .Count(b => b.Equals(binding)), Is.EqualTo(5), "No magnitude or curve may be rewritten by the sign query.");
            Assert.That(clips.All(c => AnimationUtility.GetCurveBindings(c).Length == 1), Is.True);
        }

        [TestCase("ChangedInitial")] [TestCase("DifferentLeaf")] [TestCase("VariableLowerLayer")]
        [TestCase("DuplicatePosition")] [TestCase("CollinearPositions")] [TestCase("NonFinitePosition")]
        [TestCase("PhysicsReader")] [TestCase("CullingReader")] [TestCase("StrictDescendant")]
        [TestCase("NaNInput")] [TestCase("InfiniteInput")] [TestCase("UnknownInput")] [TestCase("ExtremeOutput")]
        [TestCase("ExtremePositiveInput")] [TestCase("ExtremeNegativeInput")]
        public void NumericalAnimationPolicy_UnknownOrDifferentContributorsStayAnimated(string reason)
        {
            Mesh("Other"); var settings = ObjectSettings(); var target = PolicyChild("Guarded output").transform;
            target.localPosition = new Vector3(.7f, 1.1f, 70);
            if (reason == "ExtremeOutput") target.localPosition = new Vector3(float.MaxValue, 1.1f, 70);
            var clips = Enumerable.Range(0, 5).Select(_ => InitialTransformClip(target)).ToArray();
            if (reason == "DifferentLeaf") AnimationUtility.SetEditorCurve(clips[2],
                EditorCurveBinding.FloatCurve(target.name, typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Constant(0, .3f, 2));
            var source = CounterexampleTwoDimensionalController(BlendTreeType.FreeformCartesian2D, clips);
            if (reason == "NaNInput" || reason == "InfiniteInput" || reason == "ExtremePositiveInput" || reason == "ExtremeNegativeInput")
            {
                var parameters = source.parameters;
                parameters.Single(p => p.name == "BlendX").defaultFloat = reason == "NaNInput" ? float.NaN :
                    reason == "InfiniteInput" ? float.PositiveInfinity : reason == "ExtremePositiveInput" ? float.MaxValue : -float.MaxValue;
                source.parameters = parameters;
            }
            if (reason == "UnknownInput")
            {
                var driver = Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
                driver.parameters = new List<VRC_AvatarParameterDriver.Parameter> {
                    new() { name = "BlendX", type = VRC_AvatarParameterDriver.ChangeType.Copy, source = "UnknownRuntimeInput" }
                };
            }
            var tree = (BlendTree)source.layers[0].stateMachine.defaultState.motion;
            if (reason == "DuplicatePosition" || reason == "CollinearPositions" || reason == "NonFinitePosition")
            {
                var children = tree.children;
                if (reason == "DuplicatePosition") children[1].position = children[0].position;
                if (reason == "CollinearPositions") for (int i = 0; i < children.Length; i++) children[i].position = new Vector2(i, 0);
                if (reason == "NonFinitePosition") children[1].position = new Vector2(float.PositiveInfinity, 0);
                tree.children = children;
            }
            if (reason == "ChangedInitial") target.localPosition = Vector3.one * 20;
            if (reason == "VariableLowerLayer")
            {
                var lower = InitialTransformClip(target);
                AnimationUtility.SetEditorCurve(lower, EditorCurveBinding.FloatCurve(target.name, typeof(Transform), "m_LocalPosition.x"),
                    AnimationCurve.Linear(0, -100, .3f, 100));
                var upper = source.layers[0]; upper.name = "Final identical tree";
                source.layers = new[] { Controller(lower).layers[0], upper };
            }
            if (reason == "PhysicsReader")
                PolicyChild("Simulation").AddComponent<VRCPhysBone>().rootTransform = target;
            if (reason == "CullingReader")
            {
                target.gameObject.AddComponent<MeshRenderer>();
                root.AddComponent<Animator>().cullingMode = AnimatorCullingMode.CullCompletely;
            }
            if (reason == "StrictDescendant")
                PolicyChild("Strict output", target).AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences =
                    NumericalOptimization.All & ~NumericalOptimization.AnimationOutputs;
            var index = NumericalIndex(source, out _);
            Assert.That(ConstantTransformOutputs.Apply(index, settings, _ => true, new List<string>(), GeometryPolicy(true)), Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void NumericalAnimationPolicy_RetainedDirectAccumulationIsNeverRoundoff(bool allow)
        {
            Mesh("Other"); var settings = ObjectSettings(); var light = PolicyChild("Accumulating").AddComponent<Light>();
            light.intensity = 20;
            var binding = EditorCurveBinding.FloatCurve(light.name, typeof(Light), "m_Intensity");
            var source = TreeController(("One", StaticCurve(binding, 70)), ("OtherWeight", StaticCurve(Binding("Other"), 90)));
            var parameters = source.parameters; parameters.Single(p => p.name == "One").defaultFloat = 1; source.parameters = parameters;
            source.layers[0].stateMachine.defaultState.writeDefaultValues = false;
            var before = SampleFrozen(source, (p, _) => p.SetFloat("OtherWeight", .25f), 24, () => light.intensity);
            Assert.That(before.Max() - before.Min(), Is.GreaterThan(1), "Exercise a real retained-value recurrence.");
            light.intensity = 20;
            var index = NumericalIndex(source, out var controller);
            Assert.That(ConstantComponentProperties.Apply(index, settings, _ => true, new List<string>(), GeometryPolicy(allow)), Is.Zero);
            var after = SampleFrozen(CommitAnimator(controller, root), (p, _) => p.SetFloat("OtherWeight", .25f), 24, () => light.intensity);
            Assert.That(after, Is.EqualTo(before));
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void NumericalAnimationPolicy_IdenticalQuaternionTransitionsUseTheDeclaredContract(bool allow, bool writeDefaults)
        {
            Mesh("Other"); var settings = ObjectSettings(); var target = PolicyChild("Roundoff rotation").transform;
            var output = GeneralUnitQuaternion(2);
            var clips = new[] { ConstantQuaternionClip(target, output), ConstantQuaternionClip(target, output) };
            var source = Controller(clips); var states = source.layers[0].stateMachine.states.Select(s => s.state).ToArray();
            foreach (var state in states) state.writeDefaultValues = writeDefaults;
            foreach (var pair in new[] { (states[0], states[1]), (states[1], states[0]) })
            {
                var transition = Edge(pair.Item1, pair.Item2); transition.hasExitTime = true; transition.exitTime = .2f;
                transition.hasFixedDuration = true; transition.duration = .23f;
            }
            var before = new List<Matrix4x4>(); int blends = 0;
            SampleFrozen(source, (p, _) => { if (p.IsInTransition(0)) blends++; }, 75,
                () => { before.Add(Matrix4x4.Rotate(target.localRotation)); return 0; });
            Assert.That(blends, Is.GreaterThan(0)); target.localRotation = Quaternion.identity;
            var index = ConstantOutputIndex(source, out var controller);
            var messages = new List<string>();
            Assert.That(ConstantTransformOutputs.Apply(index, settings, _ => true, messages, GeometryPolicy(allow)),
                Is.EqualTo(allow ? 8 : 0));
            if (allow) Assert.That(ProofSummary(messages, "constant local poses"),
                Does.Contain("strict proof=0; numerical permission=1 (AnimationOutputs=1); initialization changes=1"));
            else Assert.That(messages.Any(m => m.StartsWith("Proof assumptions [")), Is.False);
            int frame = 0;
            SampleFrozen(CommitAnimator(controller, root), (_, _) => { }, 75, () => {
                Assert.That(NumericalEquivalence.Same(before[frame++], Matrix4x4.Rotate(target.localRotation), allow), Is.True); return 0;
            });
            Assert.That(clips.All(c => AnimationUtility.GetCurveBindings(c).Length == 4), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void NumericalAnimationPolicy_KnownVisualZeroSignsDoNotChangeShaderProofs(bool allow)
        {
            Mesh("Other"); var settings = ObjectSettings(); var target = PolicyChild("Zero pose").transform;
            float negative = BitConverter.Int32BitsToSingle(int.MinValue);
            var clip = InitialTransformClip(target);
            var binding = EditorCurveBinding.FloatCurve(target.name, typeof(Transform), "m_LocalPosition.x");
            AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(new Keyframe(0, negative), new Keyframe(.3f, 0)));
            var source = Controller(clip); var index = ConstantOutputIndex(source, out var controller);
            Assert.That(ConstantTransformOutputs.Apply(index, settings, _ => false, new List<string>(), GeometryPolicy(allow)),
                Is.EqualTo(allow ? 3 : 0));
            var samples = SampleFrozen(CommitAnimator(controller, root), (_, _) => { }, 24, () => target.localPosition.x);
            Assert.That(samples, Is.All.EqualTo(0));
            var materialBinding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Scalar");
            var shaderClip = StaticCurve(materialBinding, negative);
            var shaderSource = CounterexampleTwoDimensionalController(BlendTreeType.FreeformCartesian2D,
                Enumerable.Repeat(shaderClip, 5).ToArray());
            var shaderIndex = NumericalIndex(shaderSource, out _);
            Assert.That(shaderIndex.ConstantOutputs.TryProve(renderer, materialBinding.propertyName,
                ConstantOutputValue.Scalar(negative), out _, out _), Is.False,
                "An explicit visual policy does not turn an untyped material/shader observer into a continuous one.");
        }

        [Test]
        public void NumericalAnimationPolicy_DistinctTinyRotationStillRequiresInitialPermission()
        {
            Mesh("Other"); var settings = ObjectSettings(); var target = PolicyChild("Small initial change").transform;
            var output = Quaternion.AngleAxis(.00001f, Vector3.up);
            Assert.That(output.Equals(Quaternion.identity), Is.False);
            var index = ConstantOutputIndex(Controller(ConstantQuaternionClip(target, output)), out _);
            Assert.That(ConstantTransformOutputs.Apply(index, settings, _ => false, new List<string>(), GeometryPolicy(true)), Is.Zero);
            Assert.That(target.localRotation.Equals(Quaternion.identity), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void NumericalAnimationPolicy_MixedZeroSignsCarryRoundoffCertificate(bool firstNegative)
        {
            Mesh("Other"); var light = PolicyChild("Zero certificate").AddComponent<Light>(); light.intensity = 0;
            float negative = BitConverter.Int32BitsToSingle(int.MinValue);
            var binding = EditorCurveBinding.FloatCurve(light.name, typeof(Light), "m_Intensity");
            var source = FixedOneDimensionalController(.37f, new[] {
                StaticCurve(binding, firstNegative ? negative : 0), StaticCurve(binding, firstNegative ? 0 : negative)
            }, new[] { 0f, 1f });
            var index = ConstantOutputIndex(source, out _);
            Assert.That(index.ConstantOutputs.TryProve(light, binding.propertyName, ConstantOutputValue.Scalar(0),
                out var proof, out _, allowSmallNumericalDifferences: true), Is.True);
            Assert.That(proof.UsesVendorRoundoff, Is.True);
            Assert.That(proof.AbsoluteErrorBound, Is.Null, "No machine error coefficient is fabricated for the vendor interpolation contract.");
            var strict = new ConstantMotionOutput(binding, ConstantOutputKind.Scalar, index.Reachability, new(), true);
            if (strict.TryEvaluate(index.ControllerEntries.Values.Single().Layers.First().StateMachine.DefaultState.Motion, out _))
                Assert.That(strict.UsesVendorRoundoff(index.ControllerEntries.Values.Single().Layers.First().StateMachine.DefaultState.Motion), Is.False);
        }

        [Test]
        public void NumericalAnimationPolicy_StrictZeroRemovalPreservesNativeVertexBytesAndLayout()
        {
            float negative = BitConverter.Int32BitsToSingle(int.MinValue);
            var mesh = Track(new Mesh());
            mesh.SetVertexBufferParams(3,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4, 1),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.UInt32, 1, 2));
            mesh.SetVertexBufferData(new[] { new Vector3(negative, 0, negative), Vector3.right, Vector3.up }, 0, 0, 3);
            mesh.SetVertexBufferData(Enumerable.Repeat(new Vector4(1, negative, 0, negative), 3).ToArray(), 0, 0, 3, 1);
            mesh.SetVertexBufferData(new uint[] { 16777216, 16777217, 16777218 }, 0, 0, 3, 2);
            mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0, false);
            mesh.bounds = new Bounds(new Vector3(1, 2, 3), new Vector3(7, 8, 9));
            Frame(mesh, "Zero", 100, Vector3.up); renderer.sharedMesh = mesh;
            var context = LateShapeContext(permitNumericalDifferences: false);
            var attributes = mesh.GetVertexAttributes(); var buffers = new List<byte[]>();
            using (var data = UnityEngine.Mesh.AcquireReadOnlyMeshData(mesh))
                for (int stream = 0; stream < mesh.vertexBufferCount; stream++) buffers.Add(data[0].GetVertexData<byte>(stream).ToArray());
            Assert.That(LateShapes(context, LateShapeEntries(Controller(Clip()))), Is.EqualTo(1));
            Assert.That(renderer.sharedMesh.GetVertexAttributes(), Is.EqualTo(attributes));
            Assert.That(renderer.sharedMesh.bounds, Is.EqualTo(mesh.bounds));
            using (var data = UnityEngine.Mesh.AcquireReadOnlyMeshData(renderer.sharedMesh))
                for (int stream = 0; stream < buffers.Count; stream++)
                    Assert.That(data[0].GetVertexData<byte>(stream).ToArray(), Is.EqualTo(buffers[stream]));
            Assert.That(mesh.blendShapeCount, Is.EqualTo(1));
        }

        [TestCase(false, 0f)] [TestCase(true, 0f)] [TestCase(false, 37f)] [TestCase(true, 37f)]
        public void NumericalAnimationPolicy_NonzeroMorphBakeHonorsStrictMode(bool allow, float weight)
        {
            var original = Mesh("Constant"); renderer.SetBlendShapeWeight(0, weight);
            var context = LateShapeContext(permitNumericalDifferences: allow);
            var before = AssemblyWorldVertices(renderer); var entries = LateShapeEntries(Controller(Clip()));
            Assert.That(LateShapes(context, entries), Is.EqualTo(allow || weight == 0 ? 1 : 0));
            var after = AssemblyWorldVertices(renderer);
            for (int vertex = 0; vertex < before.Length; vertex++)
                for (int channel = 0; channel < 3; channel++)
                    Assert.That(NumericalEquivalence.Same(before[vertex][channel], after[vertex][channel], allow), Is.True);
            Assert.That(original.blendShapeCount, Is.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void NumericalAnimationPolicy_NativeMorphReadersKeepNonzeroStaticArithmetic(bool culling)
        {
            var original = Mesh("Constant"); renderer.SetBlendShapeWeight(0, 37);
            var options = root.AddComponent<ALTOTraceAndOptimizeBlendShapes>();
            options.PreserveMmdBlendShapes = false; options.OptimizeZeroResetAnimations = false;
            options.FreezeConstantAnimatedBlendShapes = false; options.RemoveIneffectiveBlendShapes = false;
            if (culling)
            {
                root.AddComponent<Animator>().cullingMode = AnimatorCullingMode.CullCompletely;
                renderer.updateWhenOffscreen = true;
            }
            else
            {
                var particle = PolicyChild("Emitter").AddComponent<ParticleSystem>();
                var shape = particle.shape; shape.shapeType = ParticleSystemShapeType.SkinnedMeshRenderer; shape.skinnedMeshRenderer = renderer;
            }
            var context = LateShapeContext();
            Assert.That(LateShapes(context, LateShapeEntries(Controller(Clip()))), Is.Zero);
            Assert.That(renderer.sharedMesh, Is.SameAs(original));
        }

        [TestCase(AnimatorCullingMode.CullUpdateTransforms, false)]
        [TestCase(AnimatorCullingMode.CullUpdateTransforms, true)]
        [TestCase(AnimatorCullingMode.CullCompletely, false)]
        [TestCase(AnimatorCullingMode.CullCompletely, true)]
        public void NumericalAnimationPolicy_FixedCullingBoundsPermitExistingMorphBaking(AnimatorCullingMode culling, bool reset)
        {
            var original = Mesh("First", "Second");
            renderer.SetBlendShapeWeight(0, 37); renderer.SetBlendShapeWeight(1, 23);
            root.AddComponent<Animator>().cullingMode = culling;
            renderer.updateWhenOffscreen = false;
            renderer.localBounds = new Bounds(new Vector3(.3f, -.2f, .1f), new Vector3(4, 5, 6));
            var options = root.AddComponent<ALTOTraceAndOptimizeBlendShapes>();
            options.PreserveMmdBlendShapes = false; options.OptimizeZeroResetAnimations = reset;
            options.FreezeConstantAnimatedBlendShapes = false; options.RemoveIneffectiveBlendShapes = false;
            var context = LateShapeContext();
            var before = AssemblyWorldVertices(renderer); var bounds = renderer.localBounds; var world = renderer.bounds;
            var clip = reset ? Clip("First", "Second") : Clip();
            var entries = LateShapeEntries(Controller(clip));
            Assert.That(LateShapes(context, entries), Is.EqualTo(reset ? 1 : 2));
            Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(reset ? 1 : 0));
            Assert.That(renderer.localBounds.center, Is.EqualTo(bounds.center));
            Assert.That(renderer.localBounds.extents, Is.EqualTo(bounds.extents));
            Assert.That(renderer.bounds.center, Is.EqualTo(world.center));
            Assert.That(renderer.bounds.extents, Is.EqualTo(world.extents));
            Assert.That(renderer.updateWhenOffscreen, Is.False);
            Assert.That(root.GetComponent<Animator>().cullingMode, Is.EqualTo(culling));
            Assert.That(AssemblyWorldVertices(renderer).Zip(before, Vector3.Distance).Max(), Is.LessThan(1e-6));
            Assert.That(original.blendShapeCount, Is.EqualTo(2));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(reset ? 2 : 0));
        }

        [Test]
        public void NumericalAnimationPolicy_AnimatedDynamicBoundsRetainNonzeroMorphArithmetic()
        {
            var original = Mesh("Constant"); renderer.SetBlendShapeWeight(0, 37);
            root.AddComponent<Animator>().cullingMode = AnimatorCullingMode.CullCompletely;
            renderer.updateWhenOffscreen = false;
            var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(
                AnimationUtility.CalculateTransformPath(renderer.transform, root.transform), typeof(SkinnedMeshRenderer),
                "m_UpdateWhenOffscreen"), AnimationCurve.Linear(0, 0, 1, 1));
            var context = LateShapeContext();
            Assert.That(LateShapes(context, LateShapeEntries(Controller(clip))), Is.Zero);
            Assert.That(renderer.sharedMesh, Is.SameAs(original));
        }

        [TestCase("PhysBone", false)] [TestCase("PhysBone", true)]
        [TestCase("Contact", false)] [TestCase("Contact", true)]
        [TestCase("Constraint", false)] [TestCase("Constraint", true)]
        public void NumericalAnimationPolicy_TransformConsumersDoNotReadDescendantMorphVertices(string kind, bool animated)
        {
            Mesh("Constant"); renderer.SetBlendShapeWeight(0, 37);
            var holder = PolicyChild("Frame consumer"); Component component; string field;
            if (kind == "PhysBone") { var value = holder.AddComponent<VRCPhysBone>(); value.rootTransform = root.transform; component = value; field = "rootTransform"; }
            else if (kind == "Contact") { var value = holder.AddComponent<VRCContactReceiver>(); value.rootTransform = root.transform; component = value; field = "rootTransform"; }
            else { var value = holder.AddComponent<VRCPositionConstraint>(); value.TargetTransform = root.transform; component = value; field = "TargetTransform"; }
            var clip = Clip();
            if (animated) AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve(holder.name, component.GetType(), field),
                new[] { new ObjectReferenceKeyframe { time = 0, value = root.transform } });
            var context = LateShapeContext(); var before = AssemblyWorldVertices(renderer);
            Assert.That(LateShapes(context, LateShapeEntries(Controller(clip))), Is.EqualTo(1));
            Assert.That(renderer.sharedMesh.blendShapeCount, Is.Zero);
            Assert.That(AssemblyWorldVertices(renderer).Zip(before, Vector3.Distance).Max(), Is.LessThan(1e-6));
        }

        [TestCase(false)] [TestCase(true)]
        public void NumericalAnimationPolicy_NonzeroCombinedMorphArithmeticHonorsStrictMode(bool allow)
        {
            Mesh("First", "Second"); renderer.SetBlendShapeWeight(0, 20); renderer.SetBlendShapeWeight(1, 40);
            var reset = Clip("First", "Second"); var usage = Usage(reset).usage;
            var plan = OptimizationPlan.Create(renderer, usage, true, new HashSet<string>(),
                allowSmallNumericalDifferences: allow);
            Assert.That(plan.Groups.Count, Is.EqualTo(allow ? 1 : 0));
            if (allow)
            {
                var before = AssemblyWorldVertices(renderer); var output = Track(MeshOptimization.Generate(plan, MeshOptimization.CreateRecipe(plan)));
                MeshOptimization.Apply(plan, output);
                Assert.That(AssemblyWorldVertices(renderer).Zip(before, Vector3.Distance).Max(), Is.LessThan(1e-6));
            }
        }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void NumericalAnimationPolicy_MergedMorphKeepsDestroyedSourcePermission(bool strictSource, bool wholeOutputMeet)
        {
            var pair = MergePair();
            foreach (var part in new[] { pair.First, pair.Second })
            { Frame(part.sharedMesh, "Constant", 100, Vector3.up); part.SetBlendShapeWeight(0, 20); }
            if (strictSource) pair.Second.gameObject.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences =
                NumericalOptimization.All & ~NumericalOptimization.BlendShapes;
            var context = LateShapeContext(); var entries = LateShapeEntries(Controller(Clip()));
            var before = AssemblyWorldVertices(pair.First).Concat(AssemblyWorldVertices(pair.Second)).ToArray();
            MeshOptimizationPass.Merge(MergeCandidates(new[] { pair.First, pair.Second }, entries.Values.Single()), "First", true,
                value => Track(value), shapeProvenance: context.GetState<OptimizationState>().Provenance,
                numericalPolicy: wholeOutputMeet ? context.GetState<OptimizationPolicy>() : null);
            Assert.That(pair.Second == null, Is.True);
            int removed = strictSource ? (wholeOutputMeet ? 0 : 1) : 2;
            Assert.That(LateShapes(context, entries), Is.EqualTo(removed));
            Assert.That(pair.First.sharedMesh.blendShapeCount, Is.EqualTo(2 - removed));
            var after = AssemblyWorldVertices(pair.First);
            Assert.That(after.Zip(before, Vector3.Distance).Max(), Is.LessThan(1e-6));
        }

        private OptimizationPolicy NumericalFeaturePolicy(NumericalOptimization allowed)
        {
            if (root.GetComponent<VRCAvatarDescriptor>() == null) root.AddComponent<VRCAvatarDescriptor>();
            root.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences = allowed;
            var context = new BuildContext(root, null); OptimizationPolicy.Capture(context);
            return context.GetState<OptimizationPolicy>();
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void NumericalAnimationFeatures_TransformRoundingUsesOnlyItsOwnCheckbox(bool animation, bool deformation)
        {
            Mesh("Other"); var settings = ObjectSettings(); var target = PolicyChild("Independent animation permission").transform;
            target.localPosition = new Vector3(.7f, 1.1f, 70);
            var source = CounterexampleTwoDimensionalController(BlendTreeType.FreeformCartesian2D,
                Enumerable.Range(0, 5).Select(_ => InitialTransformClip(target)).ToArray());
            DeclareFiniteBlendInputs(source);
            var index = NumericalIndex(source, out _);
            var allowed = (animation ? NumericalOptimization.AnimationOutputs : NumericalOptimization.None) |
                (deformation ? NumericalOptimization.BlendShapes : NumericalOptimization.None);
            Assert.That(ConstantTransformOutputs.Apply(index, settings, _ => false, new List<string>(), NumericalFeaturePolicy(allowed)),
                Is.EqualTo(animation ? 15 : 0));
        }

        private static IEnumerable<TestCaseData> NumericalFeatureMorphCases()
        {
            foreach (string motion in new[] { "Static", "ExactClip", "TwoDimensional", "ZeroTwoDimensional" })
            foreach (bool animation in new[] { false, true })
            foreach (bool deformation in new[] { false, true }) yield return new TestCaseData(motion, animation, deformation);
        }

        [TestCaseSource(nameof(NumericalFeatureMorphCases))]
        public void NumericalAnimationFeatures_MorphProofAndDeformationPermissionsAreIndependent(string motion,
            bool animation, bool deformation)
        {
            var original = Mesh("Constant", "Sentinel");
            renderer.gameObject.AddComponent<BlendShapeOptimizationExclude>().BlendShapes.Add("Sentinel");
            float weight = motion == "ZeroTwoDimensional" ? 0 : 37;
            renderer.SetBlendShapeWeight(0, weight);
            var clip = motion == "Static" ? Clip() : StaticCurve(Binding("Constant"), weight);
            bool tree = motion == "TwoDimensional" || motion == "ZeroTwoDimensional";
            var source = tree ? CounterexampleTwoDimensionalController(BlendTreeType.FreeformCartesian2D,
                Enumerable.Range(0, 5).Select(_ => StaticCurve(Binding("Constant"), weight)).ToArray()) : Controller(clip);
            if (tree) DeclareFiniteBlendInputs(source);
            List<Vector3[]> Sample(AnimatorController current, float initialWeight)
            {
                var result = new List<Vector3[]>(); var baked = Track(new Mesh());
                SampleFrozen(current, (p, frame) => {
                    if (!tree) return;
                    p.SetFloat("BlendX", NumericalBlendInputs[frame].x); p.SetFloat("BlendY", NumericalBlendInputs[frame].y);
                }, NumericalBlendInputs.Length, () => { renderer.BakeMesh(baked); result.Add(baked.vertices); return 0; }, initialWeight);
                return result;
            }
            var before = Sample(source, weight); renderer.SetBlendShapeWeight(0, weight); renderer.SetBlendShapeWeight(1, 0);
            var allowed = (animation ? NumericalOptimization.AnimationOutputs : NumericalOptimization.None) |
                (deformation ? NumericalOptimization.BlendShapes : NumericalOptimization.None);
            var context = LateShapeContext(numericalPermissions: allowed); var entries = LateShapeEntries(source);
            bool remove = tree ? animation && (deformation || weight == 0) : deformation;
            Assert.That(LateShapes(context, entries), Is.EqualTo(remove ? 1 : 0));
            var messages = context.GetState<OptimizationBuildReport>().Messages;
            if (remove)
            {
                string features = tree ? weight == 0 ? "AnimationOutputs=1" : "AnimationOutputs=1, BlendShapes=1" : "BlendShapes=1";
                Assert.That(ProofSummary(messages, "BlendShape outputs"),
                    Does.Contain("strict proof=0; numerical permission=1 (" + features + "); initialization changes=0"));
            }
            else Assert.That(messages.Any(m => m.StartsWith("Proof assumptions [")), Is.False);
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("Constant") == -1, Is.EqualTo(remove));
            var after = Sample(CommitAnimator(entries.Values.Single(), root), remove ? 0 : weight);
            for (int frame = 0; frame < before.Count; frame++)
                for (int vertex = 0; vertex < before[frame].Length; vertex++)
                    for (int channel = 0; channel < 3; channel++)
                        Assert.That(NumericalEquivalence.Same(before[frame][vertex][channel], after[frame][vertex][channel],
                            animation || deformation), Is.True, motion + ", frame " + frame + ", vertex " + vertex);
            Assert.That(original.blendShapeCount, Is.EqualTo(2));
        }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void NumericalAnimationFeatures_MergedAnimationPermissionSurvivesContributorDestruction(bool sourceAllows, bool wholeOutputMeet)
        {
            var pair = MergePair();
            foreach (var part in new[] { pair.First, pair.Second })
            { Frame(part.sharedMesh, "Constant", 100, Vector3.up); part.SetBlendShapeWeight(0, 37); }
            pair.Second.gameObject.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences = sourceAllows
                ? NumericalOptimization.All : NumericalOptimization.All & ~NumericalOptimization.AnimationOutputs;
            var clips = Enumerable.Range(0, 5).Select(_ => {
                var value = Clip();
                foreach (var part in new[] { pair.First, pair.Second })
                    AnimationUtility.SetEditorCurve(value, Binding("Constant", part.name), AnimationCurve.Constant(0, .3f, 37));
                return value;
            }).ToArray();
            var source = CounterexampleTwoDimensionalController(BlendTreeType.FreeformCartesian2D, clips);
            DeclareFiniteBlendInputs(source);
            var expected = AssemblyWorldVertices(pair.First).Concat(AssemblyWorldVertices(pair.Second)).ToArray();
            var context = LateShapeContext(); var entries = LateShapeEntries(source);
            MeshOptimizationPass.Merge(MergeCandidates(new[] { pair.First, pair.Second }, entries.Values.Single()), "First", true,
                value => Track(value), shapeProvenance: context.GetState<OptimizationState>().Provenance,
                numericalPolicy: wholeOutputMeet ? context.GetState<OptimizationPolicy>() : null);
            Assert.That(pair.Second == null, Is.True);
            int removed = sourceAllows ? 2 : wholeOutputMeet ? 0 : 1;
            Assert.That(LateShapes(context, entries), Is.EqualTo(removed));
            Assert.That(pair.First.sharedMesh.blendShapeCount, Is.EqualTo(2 - removed));
            Assert.That(AssemblyWorldVertices(pair.First).Zip(expected, Vector3.Distance).Max(), Is.LessThan(1e-6));
        }
    }
}
