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
    public sealed partial class BlendShapeOptimizationTests
    {
        private static IEnumerable<TestCaseData> TypedVisibilityTwoDimensionalCases()
        {
            foreach (var type in new[] { BlendTreeType.SimpleDirectional2D,
                BlendTreeType.FreeformDirectional2D, BlendTreeType.FreeformCartesian2D })
            foreach (bool rendererProperty in new[] { false, true })
            foreach (bool writeDefaults in new[] { false, true })
            foreach (bool output in new[] { false, true })
                yield return new TestCaseData(type, rendererProperty, writeDefaults, output);
        }

        [TestCaseSource(nameof(TypedVisibilityTwoDimensionalCases))]
        public void ExistingTypedVisibilityTwoDimensionalOutputsMatchNative(BlendTreeType type,
            bool rendererProperty, bool writeDefaults, bool output)
        {
            Mesh("Sample"); var settings = ObjectSettings(); var obj = PolicyChild("Typed visibility");
            var target = obj.AddComponent<MeshRenderer>();
            var binding = EditorCurveBinding.FloatCurve(obj.name,
                rendererProperty ? typeof(MeshRenderer) : typeof(GameObject), rendererProperty ? "m_Enabled" : "m_IsActive");
            var clips = Enumerable.Range(0, 5).Select(_ => StaticCurve(binding, output ? 1 : 0)).ToArray();
            var source = CounterexampleTwoDimensionalController(type, clips);
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var external = new[] { "BlendX", "BlendY" };
            var positions = new[] { Vector2.zero, Vector2.right, Vector2.left, Vector2.up, Vector2.down,
                new Vector2(.2f, .3f), new Vector2(-.6f, .2f) };
            void Set(bool enabled) { if (rendererProperty) target.enabled = enabled; else obj.SetActive(enabled); }
            float Read() => (rendererProperty ? target.enabled : obj.activeSelf) ? 1 : 0;
            float[] Sample(AnimatorController controller) => SampleFrozen(controller,
                (p, frame) => { p.SetFloat("BlendX", positions[frame].x); p.SetFloat("BlendY", positions[frame].y); },
                positions.Length, Read);

            foreach (bool changeInitial in new[] { false, true })
            {
                Set(changeInitial ? !output : output);
                var before = Sample(source);
                Assert.That(before, Is.All.EqualTo(output ? 1f : 0f));
                Set(changeInitial ? !output : output);
                var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
                Assert.That(controller.AllReachableNodes().OfType<VirtualClip>()
                    .SelectMany(c => c.GetFloatCurveBindings()).Distinct().Single(b => b.propertyName == binding.propertyName),
                    Is.EqualTo(binding), "The caller must carry the native property type even for an ordinary FloatCurve.");
                if (changeInitial)
                    Assert.That(BakeVisibility(controller, settings, preserved: external), Is.EqualTo(1));
                else
                {
                    if (rendererProperty)
                    {
                        var entries = new Dictionary<object, VirtualAnimatorController>
                            { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
                        var index = AvatarAnimationIndex.Create(root, entries, null, external);
                        Assert.That(new InvariantComponentSettings(index, index.Reachability, entries)
                            .IsConstantProperty(target, "m_Enabled"), Is.True);
                    }
                    OptimizeObjectCurves(controller, settings);
                }
                Assert.That(Read(), Is.EqualTo(output ? 1f : 0f));
                Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(binding) != null), Is.False,
                    "Both established visibility consumers must keep their identical Boolean 2D coverage.");
                var after = Sample(CommitAnimator(controller, root));
                Assert.That(after, Is.EqualTo(before));
                Assert.That(clips.All(c => AnimationUtility.GetEditorCurve(c, binding) != null), Is.True,
                    "The source clips remain unchanged.");
            }
        }

        [TestCase(false, false)] [TestCase(false, true)]
        [TestCase(true, false)] [TestCase(true, true)]
        public void ExistingTypedVisibilityMixedZeroSignsPreserveNativeFalse(bool rendererProperty, bool firstNegative)
        {
            Mesh("Sample"); var settings = ObjectSettings(); var obj = PolicyChild("Zero visibility");
            var target = obj.AddComponent<MeshRenderer>();
            var binding = EditorCurveBinding.FloatCurve(obj.name,
                rendererProperty ? typeof(MeshRenderer) : typeof(GameObject), rendererProperty ? "m_Enabled" : "m_IsActive");
            float negative = BitConverter.Int32BitsToSingle(int.MinValue);
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(
                new Keyframe(0, firstNegative ? negative : 0), new Keyframe(.2f, firstNegative ? 0 : negative)));
            var source = Controller(clip); source.layers[0].stateMachine.defaultState.writeDefaultValues = false;
            void Set(bool enabled) { if (rendererProperty) target.enabled = enabled; else obj.SetActive(enabled); }
            float Read() => (rendererProperty ? target.enabled : obj.activeSelf) ? 1 : 0;
            foreach (bool changeInitial in new[] { false, true })
            {
                Set(changeInitial);
                var before = SampleFrozen(source, (_, _) => { }, 24, Read);
                Assert.That(before, Is.All.EqualTo(0));
                Set(changeInitial);
                var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
                if (changeInitial) Assert.That(BakeVisibility(controller, settings), Is.EqualTo(1));
                else OptimizeObjectCurves(controller, settings);
                Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(binding) != null), Is.False);
                Assert.That(SampleFrozen(CommitAnimator(controller, root), (_, _) => { }, 24, Read), Is.EqualTo(before));
            }
            Assert.That(AnimationUtility.GetEditorCurve(clip, binding), Is.Not.Null);
        }

        [TestCase(BlendTreeType.SimpleDirectional2D, false)] [TestCase(BlendTreeType.SimpleDirectional2D, true)]
        [TestCase(BlendTreeType.FreeformDirectional2D, false)] [TestCase(BlendTreeType.FreeformDirectional2D, true)]
        [TestCase(BlendTreeType.FreeformCartesian2D, false)] [TestCase(BlendTreeType.FreeformCartesian2D, true)]
        public void ExistingTypedIntegerInvariantsMatchNativeTwoDimensionalOutput(BlendTreeType type, bool writeDefaults)
        {
            Mesh("Sample"); var obj = PolicyChild("Integer output"); var target = obj.AddComponent<MeshRenderer>();
            target.sortingOrder = 7;
            var binding = EditorCurveBinding.FloatCurve(obj.name, typeof(MeshRenderer), "m_SortingOrder");
            var clips = Enumerable.Range(0, 5).Select(_ => StaticCurve(binding, 7)).ToArray();
            var source = CounterexampleTwoDimensionalController(type, clips);
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var index = AvatarAnimationIndex.Create(root, entries, null, new[] { "BlendX", "BlendY" });
            var invariant = new InvariantComponentSettings(index, index.Reachability, entries);
            Assert.That(invariant.IsConstantProperty(target, "m_SortingOrder"), Is.True);
            var inputs = new[] { Vector2.zero, Vector2.right, Vector2.left, new Vector2(.2f, .3f), new Vector2(-.6f, .2f) };
            float[] Sample(AnimatorController current) => SampleFrozen(current,
                (p, frame) => { p.SetFloat("BlendX", inputs[frame].x); p.SetFloat("BlendY", inputs[frame].y); },
                inputs.Length, () => target.sortingOrder);
            var before = Sample(source); Assert.That(before, Is.All.EqualTo(7));
            invariant.PrepareConstantCurveRemoval(target)();
            target.sortingOrder = 7;
            Assert.That(Sample(CommitAnimator(controller, root)), Is.EqualTo(before));
            Assert.That(clips.All(c => AnimationUtility.GetEditorCurve(c, binding) != null), Is.True);
        }
    }
}
