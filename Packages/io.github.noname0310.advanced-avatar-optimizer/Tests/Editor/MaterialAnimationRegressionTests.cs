using System.Collections.Generic;
using Noname.AvatarTools.AnimatorOptimization;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("_Vector", "xyzw")]
        [TestCase("_Color", "rgba")]
        public void MaterialBakingPreservesConstantChannelsAlongsideAnimatedChannels(string property, string channels)
        {
            Mesh();
            var shader = Shader.Find("Hidden/Noname/AdvancedAvatarOptimizer/MaterialAnimationRegression");
            Assert.That(shader, Is.Not.Null);
            var material = Track(new Material(shader));
            renderer.sharedMaterial = material;
            var clip = Clip();
            for (int channel = 0; channel < 4; channel++)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer),
                    "material." + property + "." + channels[channel]), channel == 0 ? AnimationCurve.Linear(0, 0, 1, 1) :
                    AnimationCurve.Constant(0, 1, 1));
            var source = Controller(clip);
            var expected = EvaluateMaterialVector(source, property);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            MaterialConstants.Optimize(root, new Dictionary<object, VirtualAnimatorController> {
                [VRCAvatarDescriptor.AnimLayerType.FX] = controller
            }, null, value => Track(value), new AnimatorOptimizationReport());
            var actual = EvaluateMaterialVector(CommitAnimator(controller, root), property);
            for (int channel = 0; channel < 4; channel++)
                Assert.That(actual[channel], Is.EqualTo(expected[channel]).Within(1e-5),
                    property + "." + channels[channel] + " must retain its evaluated value.");
            Assert.That(actual.w, Is.EqualTo(1).Within(1e-5), "Alpha/gamma must not become zero.");
        }

        private Vector4 EvaluateMaterialVector(AnimatorController controller, string property)
        {
            renderer.SetPropertyBlock(null);
            var animator = root.AddComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var graph = PlayableGraph.Create("Material channel equivalence test");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                var output = AnimationPlayableOutput.Create(graph, "Test", animator);
                output.SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0); graph.Evaluate(0.25f);
                var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                return block.HasProperty(property) ? block.GetVector(property) : renderer.sharedMaterial.GetVector(property);
            }
            finally { graph.Destroy(); Object.DestroyImmediate(animator); }
        }
    }
}
