using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using nadena.dev.ndmf.animator;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private AnimationClip EmptyStaticMotion()
        {
            var clip = Clip();
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.stopTime = 0;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            return clip;
        }

        private BlendTree UnobservedNestedTree(AnimationClip leaf)
        {
            var inner = Track(new BlendTree { name = "Nested dead outputs", blendType = BlendTreeType.Simple1D, blendParameter = "NestedLeft" });
            inner.children = new[] {
                new ChildMotion { motion = leaf, threshold = 0, timeScale = 1 },
                new ChildMotion { motion = leaf, threshold = 1, timeScale = 1 }
            };
            var outer = Track(new BlendTree { name = "Dead output gate", blendType = BlendTreeType.Simple1D, blendParameter = "NestedRight" });
            outer.children = new[] {
                new ChildMotion { motion = inner, threshold = 0, timeScale = 1 },
                new ChildMotion { motion = leaf, threshold = 1, timeScale = 1 }
            };
            return outer;
        }

        [TestCase(false)] [TestCase(true)]
        public void UnobservedNestedMotionIsRemovedWithoutChangingLiveOutput(bool missingTargetCurve)
        {
            Mesh("a", "b");
            var leaf = missingTargetCurve
                ? StaticCurve(EditorCurveBinding.FloatCurve("Missing object", typeof(GameObject), "m_IsActive"), 1)
                : EmptyStaticMotion();
            var dead = UnobservedNestedTree(leaf);
            var source = TreeController(("P", StaticCurve(Binding("a"), 60)), ("Q", dead));
            source.AddParameter("NestedLeft", AnimatorControllerParameterType.Float);
            source.AddParameter("NestedRight", AnimatorControllerParameterType.Float);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var report = AnimatorOptimizer.Optimize(root, new[] { controller }, new() { UnusedParameters = true, StateLogic = true },
                observableBinding: (_, binding) => binding.path != "Missing object", finalParameterDeclarations: true);
            Assert.That(report.Branches, Is.EqualTo(1));
            Assert.That(controller.AllReachableNodes().OfType<VirtualBlendTree>().Select(tree => tree.Name), Does.Not.Contain(dead.name));
            var result = CommitAnimator(controller, root);
            foreach (float weight in new[] { 0f, 0.25f, 1f, 1.5f })
            {
                renderer.SetBlendShapeWeight(0, 0); renderer.SetBlendShapeWeight(1, 0);
                float expected = Evaluate(root, renderer, source, new Vector2(weight, 0.8f));
                renderer.SetBlendShapeWeight(0, 0); renderer.SetBlendShapeWeight(1, 0);
                Assert.That(Evaluate(root, renderer, result, new Vector2(weight, 0.8f)), Is.EqualTo(expected).Within(1e-5));
            }
            Assert.That(((BlendTree)source.layers[0].stateMachine.defaultState.motion).children.Length, Is.EqualTo(2));
        }

        [TestCase("Normalized")] [TestCase("WDOff")] [TestCase("Additive")] [TestCase("Mask")]
        [TestCase("Event")] [TestCase("ObjectCurve")] [TestCase("Visible")]
        [TestCase("TimeScale")] [TestCase("Mirror")] [TestCase("CycleOffset")]
        public void UnobservedNestedMotionRetainsUnsafeContexts(string reason)
        {
            var leaf = EmptyStaticMotion();
            var dead = UnobservedNestedTree(leaf);
            var live = StaticCurve(Binding("a"), 60);
            var source = TreeController(("P", live), ("Q", dead));
            source.AddParameter("NestedLeft", AnimatorControllerParameterType.Float);
            source.AddParameter("NestedRight", AnimatorControllerParameterType.Float);
            var state = source.layers[0].stateMachine.defaultState;
            var direct = (BlendTree)state.motion;
            if (reason == "Normalized") using (var serialized = new SerializedObject(direct))
                { serialized.FindProperty("m_NormalizedBlendValues").boolValue = true; serialized.ApplyModifiedPropertiesWithoutUndo(); }
            if (reason == "WDOff") state.writeDefaultValues = false;
            if (reason == "Additive" || reason == "Mask")
            {
                var layers = source.layers;
                if (reason == "Additive") layers[0].blendingMode = AnimatorLayerBlendingMode.Additive;
                else layers[0].avatarMask = Track(new AvatarMask());
                source.layers = layers;
            }
            if (reason == "Event") AnimationUtility.SetAnimationEvents(leaf, new[] { new AnimationEvent { functionName = "ObservableEvent", time = 0 } });
            if (reason == "ObjectCurve") AnimationUtility.SetObjectReferenceCurve(leaf,
                EditorCurveBinding.PPtrCurve("Missing object", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = null } });
            if (reason == "Visible") AnimationUtility.SetEditorCurve(leaf, Binding("a"), new AnimationCurve(new Keyframe(0, 30)));
            if (reason == "TimeScale" || reason == "Mirror" || reason == "CycleOffset")
            {
                var children = dead.children;
                if (reason == "TimeScale") children[0].timeScale = 2;
                if (reason == "Mirror") children[0].mirror = true;
                if (reason == "CycleOffset") children[0].cycleOffset = 0.5f;
                dead.children = children;
            }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            AnimatorOptimizer.Optimize(root, new[] { controller }, new() { UnusedParameters = true, StateLogic = true },
                observableBinding: (_, binding) => binding.Equals(Binding("a")));
            Assert.That(controller.AllReachableNodes().OfType<VirtualBlendTree>().Any(tree => tree.Name == dead.name), Is.True);
        }

        [Test]
        public void UnobservedMotionAnalysisRejectsCycles()
        {
            var source = UnobservedNestedTree(EmptyStaticMotion());
            var tree = (VirtualBlendTree)new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var children = tree.Children.ToBuilder();
            var child = children[0]; child.Motion = tree; children[0] = child;
            tree.Children = children.ToImmutable();
            Assert.That(new StaticMotionOutputs(null).IsUnobserved(tree), Is.False);
        }
    }
}
