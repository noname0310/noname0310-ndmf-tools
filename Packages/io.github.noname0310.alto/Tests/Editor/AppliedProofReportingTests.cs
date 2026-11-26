using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static string ProofSummary(IEnumerable<string> messages, string consumer)
            => messages.Single(m => m.StartsWith("Proof assumptions [" + consumer + "]:"));

        [TestCase(false)] [TestCase(true)]
        public void AppliedProofReporting_StrictOutputsDoNotConsumeEnabledNumericalPermissions(bool tree)
        {
            Mesh("Other"); var settings = ObjectSettings();
            var light = PolicyChild("Strict light").AddComponent<Light>(); light.intensity = 2;
            var binding = EditorCurveBinding.FloatCurve(light.name, typeof(Light), "m_Intensity");
            var clips = new[] { StaticCurve(binding, 2), StaticCurve(binding, 2) };
            var source = tree ? FixedOneDimensionalController(.37f, clips, new[] { 0f, 1f }) : Controller(clips[0]);
            var before = SampleFrozen(source, (_, _) => { }, 12, () => light.intensity);
            light.intensity = 2;
            var index = ConstantOutputIndex(source, out var controller); var policy = GeometryPolicy(true);
            var messages = new List<string>();
            Assert.That(ConstantComponentProperties.Apply(index, settings, _ => false, messages, policy), Is.EqualTo(tree ? 2 : 1));
            var summary = ProofSummary(messages, "constant component properties");
            Assert.That(summary, Does.Contain("strict proof=1; numerical permission=0; initialization changes=0"));
            Assert.That(SampleFrozen(CommitAnimator(controller, root), (_, _) => { }, 12, () => light.intensity), Is.EqualTo(before));
            Assert.That(clips.All(c => AnimationUtility.GetCurveBindings(c).Length == 1), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void AppliedProofReporting_InitializationPermissionIsSeparateAndRejectedOutputsAreNotCounted(bool allowInitial)
        {
            Mesh("Other"); var settings = ObjectSettings();
            var light = PolicyChild("Initial light").AddComponent<Light>(); light.intensity = 1;
            var binding = EditorCurveBinding.FloatCurve(light.name, typeof(Light), "m_Intensity");
            var clip = StaticCurve(binding, 2); var source = Controller(clip);
            var before = SampleFrozen(source, (_, _) => { }, 12, () => light.intensity); light.intensity = 1;
            var index = ConstantOutputIndex(source, out var controller); var messages = new List<string>();
            Assert.That(ConstantComponentProperties.Apply(index, settings, _ => allowInitial, messages, GeometryPolicy(true)),
                Is.EqualTo(allowInitial ? 1 : 0));
            if (allowInitial)
                Assert.That(ProofSummary(messages, "constant component properties"),
                    Does.Contain("strict proof=1; numerical permission=0; initialization changes=1"));
            else Assert.That(messages.Any(m => m.StartsWith("Proof assumptions [")), Is.False);
            Assert.That(SampleFrozen(CommitAnimator(controller, root), (_, _) => { }, 12, () => light.intensity), Is.EqualTo(before));
            Assert.That(AnimationUtility.GetEditorCurve(clip, binding), Is.Not.Null);
        }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void AppliedProofReporting_RoundedPoseRecordsOnlyAcceptedFeatureProofs(bool invariant, bool strictDescendant)
        {
            Mesh("Other"); var settings = ObjectSettings(); var target = PolicyChild("Rounded pose").transform;
            var child = PolicyChild("Child", target).transform;
            var initial = new Vector3(.7f, 1.1f, 70); target.localPosition = initial;
            var clips = Enumerable.Range(0, 5).Select(_ => InitialTransformClip(target)).ToArray();
            var source = CounterexampleTwoDimensionalController(BlendTreeType.FreeformCartesian2D, clips);
            DeclareFiniteBlendInputs(source);
            List<Vector3> Sample(AnimatorController current)
            {
                var samples = new List<Vector3>();
                SampleFrozen(current, (p, frame) => {
                    p.SetFloat("BlendX", NumericalBlendInputs[frame].x); p.SetFloat("BlendY", NumericalBlendInputs[frame].y);
                }, NumericalBlendInputs.Length, () => { samples.Add(child.position); return 0; });
                return samples;
            }
            var before = Sample(source); target.localPosition = initial;
            var index = NumericalIndex(source, out var controller);
            var policy = GeometryPolicy(true, strictDescendant ? child : null); var messages = new List<string>();
            if (invariant) InvariantObjectCurves.Apply(root, index.ControllerEntries, null, settings, index.Reachability, messages, policy, index);
            else Assert.That(ConstantTransformOutputs.Apply(index, settings, _ => false, messages, policy), Is.EqualTo(strictDescendant ? 0 : 15));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().SelectMany(c => c.GetFloatCurveBindings())
                .Count(b => b.type == typeof(Transform) && b.path == target.name), Is.EqualTo(strictDescendant ? 15 : 0));
            if (strictDescendant) Assert.That(messages.Any(m => m.StartsWith("Proof assumptions [")), Is.False);
            else Assert.That(ProofSummary(messages, invariant ? "invariant object outputs" : "constant local poses"),
                Does.Contain("strict proof=0; numerical permission=1 (AnimationOutputs=1); initialization changes=0"));
            var after = Sample(CommitAnimator(controller, root));
            for (int frame = 0; frame < before.Count; frame++)
                for (int channel = 0; channel < 3; channel++)
                    Assert.That(NumericalEquivalence.Same(before[frame][channel], after[frame][channel], !strictDescendant), Is.True);
        }

        [Test]
        public void AppliedProofReporting_QuaternionNormalizationRecordsItsOwnPermission()
        {
            Mesh("Other"); var settings = ObjectSettings(); var target = PolicyChild("Normalized pose").transform;
            var unit = GeneralUnitQuaternion(2);
            var output = new Quaternion(unit.x * 1.0000001f, unit.y * 1.0000001f, unit.z * 1.0000001f, unit.w * 1.0000001f);
            Assert.That(Vector4.Dot(new Vector4(output.x, output.y, output.z, output.w),
                new Vector4(output.x, output.y, output.z, output.w)), Is.Not.EqualTo(1));
            var clip = ConstantQuaternionClip(target, output); var source = Controller(clip);
            var before = new List<Matrix4x4>();
            SampleFrozen(source, (_, _) => { }, 12, () => { before.Add(Matrix4x4.Rotate(target.localRotation)); return 0; });
            target.localRotation = Quaternion.identity;
            var index = ConstantOutputIndex(source, out var controller); var messages = new List<string>();
            Assert.That(ConstantTransformOutputs.Apply(index, settings, _ => true, messages, GeometryPolicy(true)), Is.EqualTo(4));
            Assert.That(ProofSummary(messages, "constant local poses"),
                Does.Contain("strict proof=0; numerical permission=1 (AnimationOutputs=1); initialization changes=1"));
            int frame = 0;
            SampleFrozen(CommitAnimator(controller, root), (_, _) => { }, 12, () => {
                Assert.That(NumericalEquivalence.Same(before[frame++], Matrix4x4.Rotate(target.localRotation)), Is.True); return 0;
            });
            Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(4));
        }
    }
}
