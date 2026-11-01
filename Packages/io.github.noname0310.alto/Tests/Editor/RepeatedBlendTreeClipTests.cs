using System;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.AnimatorOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private (AnimatorController controller, BlendTree tree, AnimationClip clip) RepeatedOneDimensionalClip(
            float value = 60, float duration = 0, int children = 2)
        {
            var clip = StaticCurve(Binding("a"), value);
            if (duration != 0)
                AnimationUtility.SetEditorCurve(clip, Binding("a"), AnimationCurve.Constant(0, duration, value));
            var tree = Track(new BlendTree {
                name = "Repeated constant clip", blendType = BlendTreeType.Simple1D,
                blendParameter = "Selection", useAutomaticThresholds = false
            });
            tree.children = Enumerable.Range(0, children).Select(i => new ChildMotion {
                motion = clip, threshold = -.4f + i * .6f, timeScale = 1, directBlendParameter = "Selection"
            }).ToArray();
            var source = TreeController(("Weight", tree), ("Other", StaticCurve(Binding("a"), 80)));
            source.AddParameter("Selection", AnimatorControllerParameterType.Float);
            return (source, tree, clip);
        }

        [TestCase(0f, 0f, 2)] [TestCase(60f, 0f, 2)] [TestCase(60f, 1.2f, 2)]
        [TestCase(0f, 0f, 3)] [TestCase(60f, 0f, 3)] [TestCase(60f, 1.2f, 3)]
        public void RepeatedOneDimensionalClipMatchesUnityWithDynamicInputsAndSiblingWeights(float value, float duration, int children)
        {
            Mesh("a");
            var fixture = RepeatedOneDimensionalClip(value, duration, children);
            var random = new System.Random(847163);
            var inputs = Enumerable.Range(0, 240).Select(_ => new Vector3(
                (float)random.NextDouble() * 6 - 3, (float)random.NextDouble() * 1.8f,
                (float)random.NextDouble())).ToArray();
            void Inputs(AnimatorControllerPlayable playable, int frame)
            {
                playable.SetFloat("Selection", inputs[frame].x);
                playable.SetFloat("Weight", inputs[frame].y);
                playable.SetFloat("Other", inputs[frame].z);
            }
            var expected = SampleFrozen(fixture.controller, Inputs, inputs.Length);
            var virtualController = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.controller);
            var result = AnimatorOptimizer.Optimize(root, new[] { virtualController }, new() { ConstantTrees = true },
                preservedParameters: new[] { "Selection", "Weight", "Other" });
            Assert.That(result.Trees, Is.EqualTo(1));
            Assert.That(virtualController.AllReachableNodes().OfType<VirtualBlendTree>()
                .Any(t => t.Name == fixture.tree.name), Is.False);
            var actual = SampleFrozen(CommitAnimator(virtualController, root), Inputs, inputs.Length);
            Assert.That(actual, Is.EqualTo(expected).Within(2e-5));
            Assert.That(fixture.tree.children.Length, Is.EqualTo(children));
            Assert.That(AnimationUtility.GetEditorCurve(fixture.clip, Binding("a")).keys.All(k => k.value == value), Is.True);
        }

        [TestCase("Disabled")] [TestCase("WDOff")] [TestCase("Mask")] [TestCase("Additive")]
        [TestCase("Direct")] [TestCase("TimeScale")] [TestCase("Offset")] [TestCase("Mirror")]
        [TestCase("DuplicateThreshold")] [TestCase("UnsortedThreshold")] [TestCase("VariableClip")]
        [TestCase("ObjectCurve")] [TestCase("Event")] [TestCase("DistinctClip")] [TestCase("SharedWDOff")]
        public void RepeatedClipSimplificationRetainsUnprovenContexts(string reason)
        {
            Mesh("a");
            var fixture = RepeatedOneDimensionalClip();
            var state = fixture.controller.layers[0].stateMachine.defaultState;
            if (reason == "WDOff") state.writeDefaultValues = false;
            if (reason == "Mask" || reason == "Additive")
            {
                var layers = fixture.controller.layers;
                if (reason == "Mask") layers[0].avatarMask = Track(new AvatarMask());
                else layers[0].blendingMode = AnimatorLayerBlendingMode.Additive;
                fixture.controller.layers = layers;
            }
            if (reason == "Direct") fixture.tree.blendType = BlendTreeType.Direct;
            var children = fixture.tree.children;
            if (reason == "TimeScale") children[1].timeScale = 2;
            if (reason == "Offset") children[1].cycleOffset = .2f;
            if (reason == "Mirror") children[1].mirror = true;
            if (reason == "DuplicateThreshold") children[1].threshold = children[0].threshold;
            if (reason == "UnsortedThreshold") children[1].threshold = children[0].threshold - 1;
            if (reason == "DistinctClip") children[1].motion = StaticCurve(Binding("a"), 60);
            fixture.tree.children = children;
            if (reason == "VariableClip")
                AnimationUtility.SetEditorCurve(fixture.clip, Binding("a"), AnimationCurve.Linear(0, 20, 1, 60));
            if (reason == "ObjectCurve")
                AnimationUtility.SetObjectReferenceCurve(fixture.clip,
                    EditorCurveBinding.PPtrCurve("Body", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = Track(new Material(Shader.Find("Standard"))) } });
            if (reason == "Event")
                AnimationUtility.SetAnimationEvents(fixture.clip, new[] { new AnimationEvent { functionName = "Observe" } });
            if (reason == "SharedWDOff")
            {
                var machine = Track(new AnimatorStateMachine());
                var other = Track(machine.AddState("Shared owner"));
                other.motion = fixture.tree; other.writeDefaultValues = false; machine.defaultState = other;
                fixture.controller.AddLayer(new AnimatorControllerLayer {
                    name = "Other owner", defaultWeight = 1, stateMachine = machine
                });
            }
            var virtualController = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.controller);
            var result = AnimatorOptimizer.Optimize(root, new[] { virtualController },
                new() { ConstantTrees = reason != "Disabled" },
                preservedParameters: new[] { "Selection", "Weight", "Other" });
            Assert.That(result.Trees, Is.Zero);
            Assert.That(virtualController.AllReachableNodes().OfType<VirtualBlendTree>()
                .Any(t => t.Name == fixture.tree.name), Is.True);
        }
    }
}
