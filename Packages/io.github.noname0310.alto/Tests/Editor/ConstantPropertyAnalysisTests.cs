using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed class ConstantSerializedValueFixture : ScriptableObject
    {
        public long Integer;
        public double Number;
        public UnityEngine.Object Reference;
    }

    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(16777216L, true)] [TestCase(16777217L, false)]
        [TestCase(2147483648L, false)] [TestCase(-2147483648L, true)]
        public void ConstantOutputValue_IntegerDefaultsDoNotLosePrecision(long number, bool expected)
        {
            var fixture = Track(ScriptableObject.CreateInstance<ConstantSerializedValueFixture>()); fixture.Integer = number;
            using var serialized = new SerializedObject(fixture);
            Assert.That(ConstantOutputValue.TryRead(serialized.FindProperty("Integer"), out var value), Is.EqualTo(expected));
            if (expected) Assert.That((long)value.Number, Is.EqualTo(number));
        }

        [TestCase(.5, true)] [TestCase(.1, false)] [TestCase(double.PositiveInfinity, false)]
        public void ConstantOutputValue_DoubleDefaultsRequireExactAnimationEncoding(double number, bool expected)
        {
            var fixture = Track(ScriptableObject.CreateInstance<ConstantSerializedValueFixture>()); fixture.Number = number;
            using var serialized = new SerializedObject(fixture);
            Assert.That(ConstantOutputValue.TryRead(serialized.FindProperty("Number"), out var value), Is.EqualTo(expected));
            if (expected) Assert.That((double)value.Number, Is.EqualTo(number));
            Assert.That(ConstantOutputValue.TryRead(serialized.FindProperty("Reference"), out var reference), Is.True);
            Assert.That(reference.Kind, Is.EqualTo(ConstantOutputKind.ObjectReference)); Assert.That(reference.Reference, Is.Null);
        }

        private ConstantPropertyAnalysis ConstantOutputs(AnimatorController source, out VirtualAnimatorController controller,
            IEnumerable<string> preserved = null)
        {
            controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            return new ConstantPropertyAnalysis(AvatarAnimationIndex.Create(root,
                new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, null, preserved));
        }

        private AnimationClip ConstantMaterialClip(Material material)
        {
            var clip = Clip();
            AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = material },
                    new ObjectReferenceKeyframe { time = .3f, value = material } });
            return clip;
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantOutputProof_NonzeroShapeMatchesNativeAnimator(bool writeDefaults)
        {
            Mesh("Shape"); renderer.SetBlendShapeWeight(0, 20);
            var clip = StaticCurve(Binding("Shape"), 70);
            var source = Controller(clip); source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var analysis = ConstantOutputs(source, out var controller);
            Assert.That(analysis.TryProve(renderer, "blendShape.Shape", ConstantOutputValue.Scalar(20), out var proof, out var reason), Is.True, reason);
            Assert.That(proof.Value.Number, Is.EqualTo(70)); Assert.That(proof.ChangesInitialValue, Is.True);
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(20), "Proofs are read-only");
            var expected = SampleFrozen(source, (p, i) => { }, 8, () => renderer.GetBlendShapeWeight(0), 20);
            renderer.SetBlendShapeWeight(0, proof.Value.Number);
            foreach (var writer in proof.Writers) MotionAnalysis.RemoveCurves(writer.Clip, new[] { writer.Binding });
            var actual = SampleFrozen(CommitAnimator(controller, root), (p, i) => { }, 8, () => renderer.GetBlendShapeWeight(0), proof.Value.Number);
            Assert.That(actual, Is.EqualTo(expected).Within(1e-5));
            Assert.That(AnimationUtility.GetEditorCurve(clip, Binding("Shape")), Is.Not.Null);
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantOutputProof_MaterialReferencesIncludeKnownNull(bool nullOutput)
        {
            Mesh("Shape");
            var initial = Track(new Material(Shader.Find("Hidden/InternalErrorShader")));
            var replacement = nullOutput ? null : Track(new Material(initial)); renderer.sharedMaterial = initial;
            var source = Controller(ConstantMaterialClip(replacement));
            var analysis = ConstantOutputs(source, out var controller);
            Assert.That(analysis.TryProve(renderer, "m_Materials.Array.data[0]", ConstantOutputValue.Object(initial),
                out var proof, out var reason), Is.True, reason);
            Assert.That(proof.Value.Kind, Is.EqualTo(ConstantOutputKind.ObjectReference));
            Assert.That(proof.Value.Reference, Is.SameAs(replacement));
            var expected = SampleFrozen(source, (p, i) => { }, 8, () => renderer.sharedMaterial == replacement ? 1 : 0);
            renderer.sharedMaterial = replacement;
            foreach (var writer in proof.Writers)
            {
                float duration = MotionAnalysis.EndTime(writer.Clip);
                writer.Clip.SetObjectCurve(writer.Binding, null); MotionAnalysis.PreserveDuration(writer.Clip, duration);
            }
            var actual = SampleFrozen(CommitAnimator(controller, root), (p, i) => { }, 8, () => renderer.sharedMaterial == replacement ? 1 : 0);
            Assert.That(actual, Is.EqualTo(expected)); Assert.That(actual, Is.All.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantOutputProof_FullUpperOverrideCanHideDifferentLowerValues(bool objectReference)
        {
            Mesh("Shape"); var first = Track(new Material(Shader.Find("Hidden/InternalErrorShader")));
            var second = Track(new Material(first)); renderer.sharedMaterial = first;
            var lower = objectReference ? ConstantMaterialClip(first) : StaticCurve(Binding("Shape"), 10);
            var upper = objectReference ? ConstantMaterialClip(second) : StaticCurve(Binding("Shape"), 70);
            var source = Controller(lower); var upperSource = Controller(upper);
            var layer = upperSource.layers[0]; layer.name = "Full override"; source.layers = new[] { source.layers[0], layer };
            var analysis = ConstantOutputs(source, out _);
            Assert.That(analysis.TryProve(renderer, objectReference ? "m_Materials.Array.data[0]" : "blendShape.Shape",
                objectReference ? ConstantOutputValue.Object(first) : ConstantOutputValue.Scalar(0), out var proof, out var reason), Is.True, reason);
            Assert.That(objectReference ? proof.Value.Reference == second : proof.Value.Number == 70, Is.True);
            var expected = SampleFrozen(source, (p, i) => { }, 8,
                () => objectReference ? renderer.sharedMaterial == second ? 1 : 0 : renderer.GetBlendShapeWeight(0));
            Assert.That(expected, Is.All.EqualTo(objectReference ? 1 : 70).Within(1e-5));
        }

        [TestCase("PartialLayer")] [TestCase("Additive")] [TestCase("LayerControl")]
        [TestCase("MissingOutput")] [TestCase("AnimatedOutput")] [TestCase("Events")]
        public void ConstantOutputProof_RejectsUnprovenFinalOutputs(string mode)
        {
            Mesh("Shape"); var clip = StaticCurve(Binding("Shape"), 70);
            if (mode == "AnimatedOutput") AnimationUtility.SetEditorCurve(clip, Binding("Shape"), AnimationCurve.Linear(0, 0, 1, 70));
            if (mode == "Events") AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { time = 0, functionName = "UnknownObserver" } });
            var source = Controller(Clip());
            var upper = mode == "MissingOutput" ? Controller(clip, Clip()) : Controller(clip);
            if (mode == "MissingOutput")
            {
                var nodes = upper.layers[0].stateMachine.states;
                var transition = Edge(nodes[0].state, nodes[1].state); transition.hasExitTime = true; transition.exitTime = 1;
            }
            var layer = upper.layers[0]; layer.name = "Candidate";
            if (mode == "PartialLayer") layer.defaultWeight = .5f;
            if (mode == "Additive") layer.blendingMode = AnimatorLayerBlendingMode.Additive;
            source.layers = new[] { source.layers[0], layer };
            if (mode == "LayerControl")
            {
                var control = layer.stateMachine.defaultState.AddStateMachineBehaviour<VRCPlayableLayerControl>(); Track(control);
                control.layer = VRC.SDKBase.VRC_PlayableLayerControl.BlendableLayer.FX; control.goalWeight = 0;
            }
            var analysis = ConstantOutputs(source, out _);
            Assert.That(analysis.TryProve(renderer, "blendShape.Shape", ConstantOutputValue.Scalar(0), out _, out var reason), Is.False);
            Assert.That(reason, Is.Not.Null.And.Not.Empty);
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantOutputProof_DynamicOneDimensionalReferencesRequireIdenticalOutputs(bool identical)
        {
            var first = Track(new Material(Shader.Find("Hidden/InternalErrorShader")));
            var second = identical ? first : Track(new Material(first)); renderer.sharedMaterial = first;
            var source = FixedOneDimensionalController(.5f,
                new[] { ConstantMaterialClip(first), ConstantMaterialClip(second) }, new[] { 0f, 1f });
            var analysis = ConstantOutputs(source, out _, new[] { "Selection" });
            Assert.That(analysis.TryProve(renderer, "m_Materials.Array.data[0]", ConstantOutputValue.Object(null), out _, out _), Is.EqualTo(identical));
        }

        [TestCase(.25f, 25f)] [TestCase(.5f, 50f)] [TestCase(.75f, 75f)]
        public void ConstantOutputProof_ContinuousInterpolationUsesSharedScalarRules(float input, float expected)
        {
            Mesh("Shape"); var source = FixedOneDimensionalController(input,
                new[] { StaticCurve(Binding("Shape"), 0), StaticCurve(Binding("Shape"), 100) }, new[] { 0f, 1f });
            var analysis = ConstantOutputs(source, out _);
            Assert.That(analysis.TryProve(renderer, "blendShape.Shape", ConstantOutputValue.Scalar(0), out var proof, out var reason), Is.True, reason);
            Assert.That(proof.Value.Number, Is.EqualTo(expected));
            var native = SampleFrozen(source, (p, i) => { }, 8, () => renderer.GetBlendShapeWeight(0));
            Assert.That(native, Is.All.EqualTo(expected).Within(1e-5));
        }
    }
}
