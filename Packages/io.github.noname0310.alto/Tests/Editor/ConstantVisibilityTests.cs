using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.Occlusion;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false, false, false)] [TestCase(false, true, true)]
        [TestCase(true, false, true)] [TestCase(true, true, false)]
        public void ConstantFxVisibilityRetainsTheSceneValueUntilAnimatorEvaluation(bool rendererProperty, bool value, bool writeDefaults)
        {
            Mesh("Sample");
            var settings = ObjectSettings(); var obj = PolicyChild("Constant visibility");
            var target = obj.AddComponent<MeshRenderer>();
            if (rendererProperty) target.enabled = !value; else obj.SetActive(!value);
            var binding = EditorCurveBinding.FloatCurve(obj.name, rendererProperty ? typeof(MeshRenderer) : typeof(GameObject),
                rendererProperty ? "m_Enabled" : "m_IsActive");
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 0.3f, value ? 1 : 0));
            var source = Controller(clip); source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            OptimizeObjectCurves(controller, settings);
            Assert.That(rendererProperty ? target.enabled : obj.activeSelf, Is.EqualTo(!value));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(binding) != null), Is.True);
            var built = CommitAnimator(controller, root);
            if (rendererProperty) target.enabled = !value; else obj.SetActive(!value);
            var before = SampleFrozen(source, (p, frame) => { }, 30, () => (rendererProperty ? target.enabled : obj.activeSelf) ? 1 : 0);
            if (rendererProperty) target.enabled = !value; else obj.SetActive(!value);
            var after = SampleFrozen(built, (p, frame) => { }, 30, () => (rendererProperty ? target.enabled : obj.activeSelf) ? 1 : 0);
            Assert.That(after, Is.EqualTo(before));
        }

        [TestCase("PlayableWeight", false)] [TestCase("LayerWeight", false)]
        [TestCase("OtherLayer", false)] [TestCase("WeightOne", false)]
        [TestCase("MissingStateOutput", false)] [TestCase("AnimatedOutput", false)]
        [TestCase("Mask", false)] [TestCase("OptionOff", false)]
        public void ConstantVisibilityRequiresCompleteStableOutput(string mode, bool baked)
        {
            var settings = ObjectSettings(); var obj = PolicyChild("Visibility"); obj.SetActive(false);
            var binding = EditorCurveBinding.FloatCurve(obj.name, typeof(GameObject), "m_IsActive");
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, binding,
                mode == "AnimatedOutput" ? AnimationCurve.Linear(0, 0, 1, 1) : AnimationCurve.Constant(0, 1, 1));
            var source = mode == "MissingStateOutput" ? Controller(clip, Clip()) : Controller(clip);
            if (mode == "MissingStateOutput")
            {
                var states = source.layers[0].stateMachine.states;
                var transition = Edge(states[0].state, states[1].state); transition.hasExitTime = true; transition.exitTime = 1;
            }
            if (mode == "Mask")
            {
                var mask = Track(new AvatarMask()); mask.transformCount = 1; mask.SetTransformPath(0, obj.name); mask.SetTransformActive(0, true);
                var layers = source.layers; layers[0].avatarMask = mask; source.layers = layers;
            }
            if (mode == "OptionOff") settings.Component.BakeConstantVisibility = false;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            if (mode == "PlayableWeight")
            {
                var control = Track(ScriptableObject.CreateInstance<VRCPlayableLayerControl>()); control.layer = VRC_PlayableLayerControl.BlendableLayer.FX; control.goalWeight = 0;
                var state = controller.Layers.First().StateMachine.DefaultState; state.Behaviours = state.Behaviours.Add(control);
            }
            if (mode == "LayerWeight" || mode == "OtherLayer" || mode == "WeightOne")
            {
                var control = Track(ScriptableObject.CreateInstance<VRCAnimatorLayerControl>()); control.playable = VRC_AnimatorLayerControl.BlendableLayer.FX;
                control.layer = mode == "OtherLayer" ? 99 : controller.Layers.First().VirtualLayerIndex; control.goalWeight = mode == "WeightOne" ? 1 : 0;
                var state = controller.Layers.First().StateMachine.DefaultState; state.Behaviours = state.Behaviours.Add(control);
            }
            OptimizeObjectCurves(controller, settings);
            Assert.That(obj.activeSelf, Is.EqualTo(baked));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Any(c => c.GetFloatCurve(binding) != null), Is.EqualTo(!baked));
        }

        [Test]
        public void OcclusionUsesProvenConstantVisibilityAfterRedundantTracksAreRemoved()
        {
            OcclusionGpuRequired(); var occlusion = OcclusionFixture(out var shell); shell.gameObject.SetActive(true);
            var clip = Clip(); var binding = EditorCurveBinding.FloatCurve("Shell", typeof(GameObject), "m_IsActive");
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, 1));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var controllers = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var scope = OptimizationBuildScope.Capture(occlusion, root);
            var before = OcclusionEvaluation.Run(root, occlusion, scope, AvatarAnimationIndex.Create(root, controllers, null));
            Assert.That(before.Targets.Single().RemovedCount, Is.Zero);
            OptimizeObjectCurves(controller, ObjectSettings());
            Assert.That(shell.gameObject.activeSelf, Is.True);
            var after = OcclusionEvaluation.Run(root, occlusion, scope, AvatarAnimationIndex.Create(root, controllers, null));
            Assert.That(after.Targets.Single().RemovedCount, Is.EqualTo(1));
            Assert.That(renderer.sharedMesh.triangles.Length, Is.EqualTo(3), "Analysis must not edit the source mesh.");
        }
    }
}
