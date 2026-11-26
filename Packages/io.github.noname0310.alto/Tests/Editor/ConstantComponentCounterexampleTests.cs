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
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("m_CastShadows", false)] [TestCase("m_CastShadows", true)]
        [TestCase("m_DynamicOccludee", false)] [TestCase("m_DynamicOccludee", true)]
        [TestCase("m_UpdateWhenOffscreen", false)] [TestCase("m_UpdateWhenOffscreen", true)]
        public void ConstantComponentCounterexamples_CullingSettingsKeepChangedDefaults(string property, bool identical)
        {
            var settings = ObjectSettings();
            root.AddComponent<Animator>().cullingMode = AnimatorCullingMode.CullCompletely;
            using (var serialized = new SerializedObject(renderer))
            {
                var field = serialized.FindProperty(property);
                if (field.propertyType == SerializedPropertyType.Boolean) field.boolValue = false;
                else field.intValue = 0;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), property);
            var index = ConstantOutputIndex(Controller(StaticCurve(binding, identical ? 0 : 1)), out _);
            Assert.That(ConstantComponentProperties.Apply(index, settings, _ => true, new List<string>()),
                Is.EqualTo(identical ? 1 : 0), "A change in visibility can start or stop the Animator and its state behaviours");
            using var result = new SerializedObject(renderer);
            Assert.That(ConstantOutputValue.TryRead(result.FindProperty(property), out var value), Is.True);
            Assert.That(value.Number, Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantComponentCounterexamples_LightCanAffectCullingOutsideItsOwnSubtree(bool identical)
        {
            var settings = ObjectSettings();
            PolicyChild("Separate Animator").AddComponent<Animator>().cullingMode = AnimatorCullingMode.CullCompletely;
            var light = PolicyChild("Shadow light").AddComponent<Light>();
            light.intensity = 0;
            var binding = EditorCurveBinding.FloatCurve(light.name, typeof(Light), "m_Intensity");
            var index = ConstantOutputIndex(Controller(StaticCurve(binding, identical ? 0 : 1)), out _);
            Assert.That(ConstantComponentProperties.Apply(index, settings, _ => true, new List<string>()),
                Is.EqualTo(identical ? 1 : 0));
            Assert.That(light.intensity, Is.Zero);
        }

        [TestCase("Renderer", false)] [TestCase("Renderer", true)]
        [TestCase("SkinBone", false)] [TestCase("SkinBone", true)]
        [TestCase("Light", false)] [TestCase("Light", true)]
        public void ConstantComponentCounterexamples_CullingPoseInputsKeepChangedDefaults(string reader, bool identical)
        {
            Mesh("Shape"); var settings = ObjectSettings();
            root.AddComponent<Animator>().cullingMode = AnimatorCullingMode.CullCompletely;
            var target = PolicyChild("Visibility input").transform;
            if (reader == "Renderer")
            {
                var visible = target.gameObject.AddComponent<SkinnedMeshRenderer>();
                visible.sharedMesh = renderer.sharedMesh;
            }
            else if (reader == "SkinBone")
            {
                renderer.rootBone = target; renderer.bones = new[] { target };
            }
            else target.gameObject.AddComponent<Light>();
            target.localPosition = new Vector3(0, 0, 2);
            var clip = InitialTransformClip(target);
            var initial = identical ? target.localPosition : new Vector3(10000, 0, 0);
            target.localPosition = initial;
            var index = ConstantOutputIndex(Controller(clip), out _);
            Assert.That(ConstantTransformOutputs.Apply(index, settings, _ => true, new List<string>()),
                Is.EqualTo(identical ? 3 : 0), "Moving into view before evaluation can start a previously culled state machine");
            Assert.That(target.localPosition, Is.EqualTo(initial));
        }

        [Test]
        public void ConstantComponentCounterexamples_AnimatedNativeRootProtectsCandidateHierarchy()
        {
            var settings = ObjectSettings();
            var bone = PolicyChild("Simulation").AddComponent<VRCPhysBone>();
            bone.rootTransform = PolicyChild("Initial root").transform;
            var alternate = PolicyChild("Animated root").transform;
            var target = PolicyChild("Animated child", alternate).transform;
            target.localPosition = Vector3.up;
            var clip = InitialTransformClip(target);
            target.localPosition = Vector3.zero;
            AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve(bone.name, typeof(VRCPhysBone), "rootTransform"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = alternate } });
            var index = ConstantOutputIndex(Controller(clip), out _);
            Assert.That(ConstantTransformOutputs.Apply(index, settings, _ => true, new List<string>()), Is.Zero);
            Assert.That(target.localPosition, Is.EqualTo(Vector3.zero));
        }

        private AnimationClip ConstantQuaternionClip(Transform target, Quaternion value, int channels = 4)
        {
            var clip = Clip();
            string path = AnimationUtility.CalculateTransformPath(target, root.transform);
            for (int i = 0; i < channels; i++)
                AnimationUtility.SetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalRotation." + "xyzw"[i]),
                    AnimationCurve.Constant(0, .3f, value[i]));
            return clip;
        }

        [TestCase(false, false)] [TestCase(true, false)]
        [TestCase(false, true)] [TestCase(true, true)]
        public void ConstantComponentCounterexamples_WholeQuaternionMatchesNativeAnimator(bool negative, bool tree)
        {
            Mesh("Shape"); var settings = ObjectSettings(); var target = PolicyChild("Rotation").transform;
            float component = negative ? -.5f : .5f;
            var output = new Quaternion(component, component, component, component);
            var source = tree ? FixedOneDimensionalController(.5f,
                new[] { ConstantQuaternionClip(target, output), ConstantQuaternionClip(target, output) }, new[] { 0f, 1f }) :
                Controller(ConstantQuaternionClip(target, output));
            var before = new List<Quaternion>();
            SampleFrozen(source, (p, i) => { }, 8, () => { before.Add(target.localRotation); return 0; });
            target.localRotation = Quaternion.identity;
            var index = ConstantOutputIndex(source, out var controller);
            Assert.That(ConstantTransformOutputs.Apply(index, settings, _ => true, new List<string>()), Is.EqualTo(tree ? 8 : 4));
            var after = new List<Quaternion>();
            SampleFrozen(CommitAnimator(controller, root), (p, i) => { }, 8, () => { after.Add(target.localRotation); return 0; });
            for (int i = 0; i < before.Count; i++)
            {
                Assert.That(Vector3.Distance(before[i] * Vector3.right, after[i] * Vector3.right), Is.LessThan(1e-6));
                Assert.That(Vector3.Distance(before[i] * Vector3.up, after[i] * Vector3.up), Is.LessThan(1e-6));
            }
            Assert.That(ConstantTransformOutputs.Apply(
                ConstantOutputIndex(CommitAnimator(controller, root), out _), settings, _ => true, new List<string>()), Is.Zero);
            Assert.That(source.animationClips.SelectMany(AnimationUtility.GetCurveBindings)
                .Count(b => b.propertyName.StartsWith("m_LocalRotation.")), Is.EqualTo(tree ? 8 : 4));
        }

        private static Quaternion GeneralUnitQuaternion(int example) => example == 0 ? new Quaternion(0, .6f, .8f, 0) :
            example == 1 ? new Quaternion(.36f, .48f, 0, .8f) :
            new Quaternion(.18257418f, .36514837f, .5477226f, .73029673f);

        private static IEnumerable<TestCaseData> GeneralQuaternionOutputCases()
        {
            foreach (int example in new[] { 0, 1, 2 })
            foreach (string context in new[] { "Clip", "OneDimensional", "Transition", "AnyState", "NestedTransition",
                "InstantTransition", "LowerTransitionOverride" })
            foreach (bool writeDefaults in new[] { false, true })
                yield return new TestCaseData(example, context, writeDefaults);
        }

        private static void RequireQuaternionEntryCondition(AnimatorController controller, AnimatorStateTransition transition)
        {
            const string parameter = "EnterQuaternionContext";
            controller.AddParameter(new AnimatorControllerParameter
                { name = parameter, type = AnimatorControllerParameterType.Bool, defaultBool = true });
            transition.AddCondition(AnimatorConditionMode.If, 0, parameter);
        }

        private static void AssertSameNormalizedRotation(Quaternion expected, Quaternion actual, string context)
        {
            expected = expected.normalized; actual = actual.normalized;
            if (Quaternion.Dot(expected, actual) < 0) actual = new Quaternion(-actual.x, -actual.y, -actual.z, -actual.w);
            for (int channel = 0; channel < 4; channel++)
            {
                // Quaternion sign and scalar zero sign do not change a rotation.
                // Every nonzero normalized component must otherwise match exactly.
                if (expected[channel] == 0 && actual[channel] == 0) continue;
                Assert.That(BitConverter.SingleToInt32Bits(actual[channel]),
                    Is.EqualTo(BitConverter.SingleToInt32Bits(expected[channel])), context + ", component " + channel +
                    ", expected " + expected.ToString("R") + ", actual " + actual.ToString("R"));
            }
        }

        [TestCaseSource(nameof(GeneralQuaternionOutputCases))]
        public void ConstantComponentCounterexamples_GeneralUnitQuaternionKeepsExactNativeOutput(int example, string context, bool writeDefaults)
        {
            Mesh("Shape"); var settings = ObjectSettings(); var target = PolicyChild("General rotation").transform;
            var value = GeneralUnitQuaternion(example);
            Assert.That(Vector4.Dot(new Vector4(value.x, value.y, value.z, value.w),
                new Vector4(value.x, value.y, value.z, value.w)), Is.EqualTo(1), "Exercise the accepted float-unit guard");
            var first = ConstantQuaternionClip(target, value); first.name = "First quaternion";
            var second = ConstantQuaternionClip(target, value); second.name = "Second quaternion";
            var source = context == "OneDimensional" ? FixedOneDimensionalController(.37f, new[] { first, second }, new[] { 0f, 1f }) :
                context == "Clip" || context == "NestedTransition" ? Controller(first) : Controller(first, second);
            var states = source.layers[0].stateMachine.states.Select(s => s.state).ToArray();
            foreach (var state in states) state.writeDefaultValues = writeDefaults;
            if (context == "NestedTransition")
            {
                var nested = Track(source.layers[0].stateMachine.AddStateMachine("Nested rotations"));
                var a = Track(nested.AddState("First nested")); a.motion = first; a.writeDefaultValues = writeDefaults;
                var b = Track(nested.AddState("Second nested")); b.motion = second; b.writeDefaultValues = writeDefaults;
                nested.defaultState = a;
                var enter = Track(states[0].AddTransition(nested)); enter.hasExitTime = false; enter.duration = 0;
                RequireQuaternionEntryCondition(source, enter);
                states = new[] { a, b };
            }
            if (context == "AnyState")
            {
                var transition = Track(source.layers[0].stateMachine.AddAnyStateTransition(states[1]));
                transition.hasExitTime = false; transition.canTransitionToSelf = false;
                transition.hasFixedDuration = true; transition.duration = .23f;
                RequireQuaternionEntryCondition(source, transition);
            }
            if (context == "Transition" || context == "NestedTransition" || context == "InstantTransition" || context == "LowerTransitionOverride")
                foreach (var pair in new[] { (states[0], states[1]), (states[1], states[0]) })
                {
                    var transition = Edge(pair.Item1, pair.Item2); transition.hasExitTime = true; transition.exitTime = .2f;
                    transition.hasFixedDuration = true; transition.duration = context == "InstantTransition" ? 0 : .23f;
                }
            if (context == "LowerTransitionOverride")
            {
                var upper = Controller(ConstantQuaternionClip(target, value)).layers[0];
                upper.name = "Full rotation override"; upper.defaultWeight = 1; upper.stateMachine.defaultState.writeDefaultValues = writeDefaults;
                source.layers = new[] { source.layers[0], upper };
            }
            var before = new List<Quaternion>(); int transitioning = 0;
            SampleFrozen(source, (p, frame) => { if (p.IsInTransition(0)) transitioning++; }, 75,
                () => { before.Add(target.localRotation); return 0; });
            Assert.That(before.Any(q => Quaternion.Angle(q, Quaternion.identity) > 1), Is.True, "The native clip must rotate the target");
            bool retainsTransition = context == "Transition" || context == "AnyState" || context == "NestedTransition";
            if (retainsTransition || context == "LowerTransitionOverride") Assert.That(transitioning, Is.GreaterThan(0), "Sample actual transition blends");
            target.localRotation = Quaternion.identity;
            var index = ConstantOutputIndex(source, out var controller);
            int removed = ConstantTransformOutputs.Apply(index, settings, _ => true, new List<string>());
            int writerCurves = source.animationClips.SelectMany(AnimationUtility.GetCurveBindings).Count(b => b.propertyName.StartsWith("m_LocalRotation."));
            Assert.That(removed, Is.EqualTo(retainsTransition ? 0 : writerCurves),
                "Native state blends retain their tracks; proved clip/1D/instant/full-override outputs remain optimizable");
            var after = new List<Quaternion>();
            SampleFrozen(CommitAnimator(controller, root), (p, frame) => { }, 75,
                () => { after.Add(target.localRotation); return 0; });
            for (int frame = 0; frame < before.Count; frame++)
                AssertSameNormalizedRotation(before[frame], after[frame], context + ", frame " + frame + ", removed " + removed);
        }

        [TestCase("PartialLayer", false)] [TestCase("PartialLayer", true)]
        [TestCase("DifferentLeaves", false)] [TestCase("DifferentLeaves", true)]
        [TestCase("OppositeLeaves", false)] [TestCase("OppositeLeaves", true)]
        public void ConstantComponentCounterexamples_QuaternionMixedContributorsKeepNativeOutput(string context, bool writeDefaults)
        {
            Mesh("Shape"); var settings = ObjectSettings(); var target = PolicyChild("Mixed rotation").transform;
            var value = GeneralUnitQuaternion(2);
            var other = context == "OppositeLeaves" ? new Quaternion(-value.x, -value.y, -value.z, -value.w) : Quaternion.identity;
            AnimatorController source;
            if (context == "PartialLayer")
            {
                source = Controller(ConstantQuaternionClip(target, other));
                var layer = Controller(ConstantQuaternionClip(target, value)).layers[0]; layer.name = "Partial rotation"; layer.defaultWeight = .37f;
                source.layers = new[] { source.layers[0], layer };
            }
            else source = FixedOneDimensionalController(.37f,
                new[] { ConstantQuaternionClip(target, value), ConstantQuaternionClip(target, other) }, new[] { 0f, 1f });
            foreach (var state in source.layers.SelectMany(l => l.stateMachine.states)) state.state.writeDefaultValues = writeDefaults;
            var before = new List<Quaternion>();
            SampleFrozen(source, (p, frame) => { }, 16, () => { before.Add(target.localRotation); return 0; });
            target.localRotation = Quaternion.identity;
            var index = ConstantOutputIndex(source, out var controller);
            Assert.That(ConstantTransformOutputs.Apply(index, settings, _ => true, new List<string>()), Is.Zero);
            var after = new List<Quaternion>();
            SampleFrozen(CommitAnimator(controller, root), (p, frame) => { }, 16, () => { after.Add(target.localRotation); return 0; });
            for (int frame = 0; frame < before.Count; frame++) AssertSameNormalizedRotation(before[frame], after[frame], context + ", frame " + frame);
        }

        [Test]
        public void ConstantComponentCounterexamples_NegatedQuaternionPreservesAuthoredStorageWithoutPermission()
        {
            var settings = ObjectSettings(); var target = PolicyChild("Rotation").transform;
            target.gameObject.AddComponent<MeshRenderer>();
            root.AddComponent<Animator>().cullingMode = AnimatorCullingMode.CullCompletely;
            var initial = target.localRotation;
            var index = ConstantOutputIndex(Controller(ConstantQuaternionClip(target, new Quaternion(0, 0, 0, -1))), out _);
            Assert.That(ConstantTransformOutputs.Apply(index, settings, _ => false, new List<string>()), Is.EqualTo(4));
            Assert.That(target.localRotation.Equals(initial), Is.True, "Equivalent signs must not rewrite the authored quaternion storage");
        }

        [TestCase("Partial")] [TestCase("Euler")] [TestCase("NonUnit")]
        [TestCase("Interpolated")] [TestCase("NoPermission")] [TestCase("Culling")]
        public void ConstantComponentCounterexamples_QuaternionRejectsUnsupportedOutputContracts(string reason)
        {
            var settings = ObjectSettings(); var target = PolicyChild("Rotation").transform;
            var value = reason == "NonUnit" ? new Quaternion(0, 0, 0, 2) : new Quaternion(.5f, .5f, .5f, .5f);
            var clip = ConstantQuaternionClip(target, value, reason == "Partial" ? 3 : 4);
            var source = reason == "Interpolated" ? FixedOneDimensionalController(.5f,
                new[] { ConstantQuaternionClip(target, new Quaternion(0, 0, 0, 2)),
                    ConstantQuaternionClip(target, new Quaternion(0, 0, 0, 0)) }, new[] { 0f, 1f }) : Controller(clip);
            if (reason == "Euler")
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(target.name, typeof(Transform), "localEulerAnglesRaw.y"),
                    AnimationCurve.Constant(0, .3f, 45));
            if (reason == "Culling")
            {
                target.gameObject.AddComponent<MeshRenderer>();
                root.AddComponent<Animator>().cullingMode = AnimatorCullingMode.CullCompletely;
            }
            var index = ConstantOutputIndex(source, out _);
            Assert.That(ConstantTransformOutputs.Apply(index, settings, _ => reason != "NoPermission", new List<string>()), Is.Zero);
            Assert.That(target.localRotation.Equals(Quaternion.identity), Is.True);
        }

        [TestCase("m_SortingOrder", 70000f)] [TestCase("m_SortingOrder", -70000f)]
        [TestCase("m_SortingOrder", 32767f)] [TestCase("m_SortingOrder", -32768f)]
        [TestCase("m_SortingOrder", 32768f)] [TestCase("m_SortingOrder", -32769f)]
        [TestCase("m_SortingOrder", 65536f)] [TestCase("m_SortingOrder", -65536f)]
        [TestCase("m_SortingLayerID", 123f)] [TestCase("m_Range", -1f)] [TestCase("m_Intensity", -1f)]
        public void ConstantComponentCounterexamples_SerializedAssignmentMatchesNativeScalarNormalization(string property, float output)
        {
            Mesh("Shape"); var settings = ObjectSettings();
            bool lightField = property == "m_Range" || property == "m_Intensity";
            var light = lightField ? PolicyChild("Light").AddComponent<Light>() : null;
            Component target = lightField ? (Component)light : renderer;
            float Read() => property == "m_Range" ? light.range : property == "m_Intensity" ? light.intensity :
                property == "m_SortingOrder" ? renderer.sortingOrder : renderer.sortingLayerID;
            float initial = Read();
            var binding = EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(target.transform, root.transform), target.GetType(), property);
            var source = Controller(StaticCurve(binding, output));
            var before = SampleFrozen(source, (p, i) => { }, 8, Read);
            if (property == "m_Range") light.range = initial;
            else if (property == "m_Intensity") light.intensity = initial;
            else if (property == "m_SortingOrder") renderer.sortingOrder = (int)initial;
            else renderer.sortingLayerID = (int)initial;
            var index = ConstantOutputIndex(source, out var controller);
            int removed = ConstantComponentProperties.Apply(index, settings, _ => true, new List<string>());
            if (property == "m_SortingOrder")
                Assert.That(removed, Is.EqualTo(output < short.MinValue || output > short.MaxValue ? 0 : 1));
            Assert.That(SampleFrozen(CommitAnimator(controller, root), (p, i) => { }, 8, Read), Is.EqualTo(before));
        }
    }
}
