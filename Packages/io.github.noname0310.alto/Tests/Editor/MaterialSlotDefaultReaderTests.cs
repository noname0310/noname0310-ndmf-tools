using System.Linq;
using Noname.AvatarTools.AnimatorOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private (Material Initial, Material Replacement, AnimatorController Controller) MaterialSlotDefaultFixture(
            bool sameDefault, bool completeOutput = false)
        {
            Mesh("Timing");
            var initial = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            var replacement = Track(new Material(initial));
            initial.SetFloat("_Scalar", .2f); replacement.SetFloat("_Scalar", sameDefault ? .2f : .8f);
            renderer.sharedMaterial = initial;
            var active = ConstantMaterialClip(replacement); active.name = "Explicit scalar";
            var defaults = ConstantMaterialClip(replacement); defaults.name = "Later scalar output";
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Scalar");
            AnimationUtility.SetEditorCurve(active, binding, AnimationCurve.Linear(0, .4f, .3f, .6f));
            if (completeOutput) AnimationUtility.SetEditorCurve(defaults, binding, AnimationCurve.Linear(0, .7f, .3f, .3f));
            var source = Controller(active, defaults); source.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
            var states = source.layers[0].stateMachine.states;
            var transition = Edge(states[0].state, states[1].state, "GestureLeft", 1); transition.duration = 0;
            return (initial, replacement, source);
        }

        private float[] SampleMaterialSlotScalar(AnimatorController source, Material initial)
        {
            renderer.sharedMaterial = initial; renderer.SetPropertyBlock(null);
            try
            {
                return SampleFrozen(source, (playable, frame) => playable.SetInteger("GestureLeft", frame >= 8 ? 1 : 0), 24, () =>
                {
                    var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                    return block.HasProperty("_Scalar") ? block.GetFloat("_Scalar") : renderer.sharedMaterial.GetFloat("_Scalar");
                });
            }
            finally { renderer.SetPropertyBlock(null); }
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantMaterialSlots_PreservesLaterNativeWriteDefaults(bool sameDefault)
        {
            var fixture = MaterialSlotDefaultFixture(sameDefault);
            var expected = SampleMaterialSlotScalar(fixture.Controller, fixture.Initial);
            var changedInitial = SampleMaterialSlotScalar(fixture.Controller, fixture.Replacement);
            Assert.That(expected.Last(), Is.EqualTo(.2f).Within(.00001f));
            Assert.That(changedInitial.Last(), Is.EqualTo(sameDefault ? .2f : .8f).Within(.00001f),
                "The first material is a persistent native default reader, even when every state swaps to the same replacement.");
            renderer.sharedMaterial = fixture.Initial;
            var index = ConstantOutputIndex(fixture.Controller, out var controller);
            var report = new AnimatorOptimizationReport();
            Assert.That(ConstantMaterialSlots.Apply(index, _ => true, null, report), Is.EqualTo(sameDefault ? 2 : 0));
            Assert.That(renderer.sharedMaterial, Is.SameAs(sameDefault ? fixture.Replacement : fixture.Initial));
            var actual = SampleMaterialSlotScalar(CommitAnimator(controller, root), renderer.sharedMaterial);
            Assert.That(actual, Is.EqualTo(expected).Within(.00001f));
            Assert.That(fixture.Initial.GetFloat("_Scalar"), Is.EqualTo(.2f));
        }

        [Test]
        public void ConstantMaterialSlots_AllowsFullyDrivenDynamicMaterialOutput()
        {
            var fixture = MaterialSlotDefaultFixture(false, completeOutput: true);
            var expected = SampleMaterialSlotScalar(fixture.Controller, fixture.Initial);
            renderer.sharedMaterial = fixture.Initial;
            var index = ConstantOutputIndex(fixture.Controller, out var controller);
            Assert.That(ConstantMaterialSlots.Apply(index, _ => true, null, new AnimatorOptimizationReport()), Is.EqualTo(2));
            Assert.That(renderer.sharedMaterial, Is.SameAs(fixture.Replacement));
            var actual = SampleMaterialSlotScalar(CommitAnimator(controller, root), renderer.sharedMaterial);
            Assert.That(actual, Is.EqualTo(expected).Within(.00001f));
            Assert.That(expected.Max() - expected.Min(), Is.GreaterThan(.1f), "The material output must remain dynamic.");
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantMaterialSlots_SparseDirectCoverageIncludesNativeRetainedDefaults(bool writeDefaults)
        {
            Mesh("Timing");
            var initial = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            var replacement = Track(new Material(initial));
            initial.SetFloat("_Scalar", .2f); replacement.SetFloat("_Scalar", .8f);
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Scalar");
            var source = TreeController(("One", StaticCurve(binding, .3f)), ("Other", StaticCurve(Binding("Timing"), 30)));
            var parameters = source.parameters;
            parameters.Single(p => p.name == "One").defaultFloat = 1;
            parameters.Single(p => p.name == "Other").defaultFloat = .5f; source.parameters = parameters;
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var slotSource = Controller(ConstantMaterialClip(replacement));
            slotSource.layers[0].stateMachine.defaultState.writeDefaultValues = false;
            var slotLayer = slotSource.layers[0]; slotLayer.name = "Constant material slot";
            source.layers = new[] { source.layers[0], slotLayer };
            float[] Sample(AnimatorController sampledController, Material first)
            {
                renderer.sharedMaterial = first; renderer.SetPropertyBlock(null);
                try
                {
                    return SampleFrozen(sampledController, (playable, frame) => { }, 8, () =>
                    {
                        var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                        return block.HasProperty("_Scalar") ? block.GetFloat("_Scalar") : renderer.sharedMaterial.GetFloat("_Scalar");
                    });
                }
                finally { renderer.SetPropertyBlock(null); }
            }
            var expected = Sample(source, initial); var differentDefault = Sample(source, replacement);
            if (writeDefaults) Assert.That(differentDefault, Is.EqualTo(expected).Within(.00001f));
            else Assert.That(expected.Zip(differentDefault, (a, b) => Mathf.Abs(a - b)).Max(), Is.GreaterThan(.01f),
                "Sparse WD-off material output must expose the captured starting value.");
            renderer.sharedMaterial = initial;
            var index = ConstantOutputIndex(source, out var controller);
            Assert.That(index.MaterialInputs.FullyDriven(renderer, "_Scalar", new[] { "material._Scalar" }), Is.EqualTo(writeDefaults));
            Assert.That(ConstantMaterialSlots.Apply(index, _ => true, null, new AnimatorOptimizationReport()), Is.EqualTo(writeDefaults ? 1 : 0));
            Assert.That(Sample(CommitAnimator(controller, root), renderer.sharedMaterial), Is.EqualTo(expected).Within(.00001f));
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantMaterialSlots_RequiresCompleteNativeVectorChannelCoverage(bool completeOutput)
        {
            Mesh("Timing");
            var initial = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            var replacement = Track(new Material(initial));
            initial.SetVector("_Vector", new Vector4(.2f, .3f, .4f, .5f));
            replacement.SetVector("_Vector", new Vector4(.2f, .8f, .6f, .5f));
            renderer.sharedMaterial = initial;
            var clip = ConstantMaterialClip(replacement);
            foreach (char channel in completeOutput ? "xyzw" : "x")
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer),
                    "material._Vector." + channel), AnimationCurve.Linear(0, .1f, .3f, .9f));
            var source = Controller(clip);
            var expected = EvaluateMaterialVector(source, "_Vector");
            renderer.sharedMaterial = initial; renderer.SetPropertyBlock(null);
            var index = ConstantOutputIndex(source, out var controller);
            Assert.That(ConstantMaterialSlots.Apply(index, _ => true, null, new AnimatorOptimizationReport()), Is.EqualTo(completeOutput ? 1 : 0));
            var actual = EvaluateMaterialVector(CommitAnimator(controller, root), "_Vector");
            Assert.That(Vector4.Distance(actual, expected), Is.LessThan(.00001f));
        }

        [Test]
        public void ConstantMaterialSlots_BakesIndependentSecondarySlotWhenFirstDefaultIsObserved()
        {
            var fixture = MaterialSlotDefaultFixture(false);
            var secondary = Track(new Material(fixture.Replacement));
            renderer.sharedMaterials = new[] { fixture.Initial, fixture.Initial };
            foreach (var clip in fixture.Controller.animationClips)
                AnimationUtility.SetObjectReferenceCurve(clip,
                    EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[1]"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = secondary } });
            var index = ConstantOutputIndex(fixture.Controller, out var controller);
            Assert.That(ConstantMaterialSlots.Apply(index, _ => true, null, new AnimatorOptimizationReport()), Is.EqualTo(2));
            Assert.That(renderer.sharedMaterials, Is.EqualTo(new[] { fixture.Initial, secondary }));
            Assert.That(controller.AllReachableNodes().OfType<nadena.dev.ndmf.animator.VirtualClip>()
                .SelectMany(c => c.GetObjectCurveBindings()).Select(b => b.propertyName).Distinct(),
                Is.EqualTo(new[] { "m_Materials.Array.data[0]" }));
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantMaterialSlots_RetainsUnknownMaterialBindingInterface(bool nullReplacement)
        {
            Mesh("Timing");
            var initial = Track(new Material(Shader.Find("Hidden/Noname/ALTO/ConstantMaterialOutputs")));
            var replacement = nullReplacement ? null : Track(new Material(Shader.Find("Hidden/InternalErrorShader")));
            renderer.sharedMaterial = initial;
            var clip = ConstantMaterialClip(replacement);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer),
                "material._Scalar"), AnimationCurve.Constant(0, .3f, .4f));
            var index = ConstantOutputIndex(Controller(clip), out _);
            Assert.That(ConstantMaterialSlots.Apply(index, _ => true, null, new AnimatorOptimizationReport()), Is.Zero);
            Assert.That(renderer.sharedMaterial, Is.SameAs(initial));
        }
    }
}
