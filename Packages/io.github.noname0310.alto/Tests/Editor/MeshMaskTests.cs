using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private AvatarMask DifferentRendererMask()
        {
            var mask = Track(new AvatarMask()); mask.transformCount = 3;
            mask.SetTransformPath(0, "First"); mask.SetTransformActive(0, true);
            mask.SetTransformPath(1, "Second"); mask.SetTransformActive(1, false);
            mask.SetTransformPath(2, "Unrelated"); mask.SetTransformActive(2, true);
            return mask;
        }

        private Vector3[][] SampleMaskedControllers(AnimatorController unrelated, AnimatorController output,
            bool rendererFirst, Func<Vector3[]> sample)
        {
            var animator = root.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var graph = PlayableGraph.Create("Independent controller mask equivalence");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var other = AnimatorControllerPlayable.Create(graph, unrelated);
                var rendererOutput = AnimatorControllerPlayable.Create(graph, output);
                var mixer = AnimationLayerMixerPlayable.Create(graph, 2);
                int rendererIndex = rendererFirst ? 0 : 1;
                graph.Connect(other, 0, mixer, 1 - rendererIndex);
                graph.Connect(rendererOutput, 0, mixer, rendererIndex);
                AnimationPlayableOutput.Create(graph, "Avatar", animator).SetSourcePlayable(mixer);
                graph.Play();
                var values = new[] { 0f, 0.2f, 1, 1, 1.4f, 0 };
                var weights = new[] { 1f, 1, 1, 1, 0.4f, 1 };
                var otherWeights = new[] { 1f, 1, 1, 0, 0.4f, 1 };
                var samples = new List<Vector3[]>();
                for (int i = 0; i < values.Length; i++)
                {
                    rendererOutput.SetFloat("Input", values[i]);
                    mixer.SetInputWeight(rendererIndex, weights[i]);
                    mixer.SetInputWeight(1 - rendererIndex, otherWeights[i]);
                    graph.Evaluate(1f / 60); samples.Add(sample());
                }
                return samples.ToArray();
            }
            finally { graph.Destroy(); UnityEngine.Object.DestroyImmediate(animator); }
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void UnrelatedControllerMasksPreserveNativeMergedMorphOutputs(bool writeDefaults, bool rendererFirst)
        {
            var pair = MergePair(); var helper = PolicyChild("Unrelated").transform;
            foreach (var part in new[] { pair.First, pair.Second }) Frame(part.sharedMesh, "Custom", 100, Vector3.up * 0.3f);
            var transformClip = Clip(); AnimationUtility.SetEditorCurve(transformClip,
                EditorCurveBinding.FloatCurve("Unrelated", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Constant(0, 1, 0.5f));
            var unrelated = Controller(transformClip); var layers = unrelated.layers;
            layers[0].avatarMask = DifferentRendererMask(); unrelated.layers = layers;
            unrelated.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, Binding("Custom", "First"), AnimationCurve.Constant(0, 1, 70));
            AnimationUtility.SetEditorCurve(clip, Binding("Custom", "Second"), AnimationCurve.Constant(0, 1, 40));
            var source = TreeController(("Input", clip)); source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var other = clone.Clone(unrelated); var output = clone.Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController>
            { [VRCAvatarDescriptor.AnimLayerType.Gesture] = other, [VRCAvatarDescriptor.AnimLayerType.FX] = output };
            var group = MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, entries: entries), true).Single();
            var before = SampleMaskedControllers(unrelated, source, rendererFirst,
                () => AssemblyWorldVertices(pair.First).Concat(AssemblyWorldVertices(pair.Second)).ToArray());
            Assert.That(before.Skip(1).Any(frame => frame.Zip(before[0], Vector3.Distance).Any(distance => distance > 0.1f)), Is.True,
                "Vary both playable weights so the renderer output is exercised in either layer order.");
            pair.First.SetBlendShapeWeight(0, 0); pair.Second.SetBlendShapeWeight(0, 0); helper.localPosition = Vector3.zero;
            MeshOptimizationPass.Merge(group, "First", true, mesh => Track(mesh));
            var after = SampleMaskedControllers(CommitAnimator(other, root), CommitAnimator(output, root), rendererFirst,
                () => AssemblyWorldVertices(pair.First));
            for (int frame = 0; frame < before.Length; frame++)
                for (int vertex = 0; vertex < before[frame].Length; vertex++)
                    Assert.That(Vector3.Distance(after[frame][vertex], before[frame][vertex]), Is.LessThan(0.00001f),
                        "Frame " + frame + ", vertex " + vertex);
        }

        [Test]
        public void ControllerWithOnlyActivationBindingsStillUsesItsVisibilityMask()
        {
            var pair = MergePair(); var clip = Clip();
            foreach (var part in new[] { pair.First, pair.Second }) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(part.name, typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            var source = Controller(clip); var layers = source.layers; layers[0].avatarMask = DifferentRendererMask(); source.layers = layers;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(2));
        }

        [Test]
        public void RendererBindingInAnotherLayerRetainsTheWholeControllerMaskProfile()
        {
            var pair = MergePair(); var source = Controller(Clip());
            var layers = source.layers; layers[0].avatarMask = DifferentRendererMask(); source.layers = layers;
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("First", typeof(SkinnedMeshRenderer), "m_Enabled"), AnimationCurve.Constant(0, 1, 1));
            var stateMachine = Track(new AnimatorStateMachine()); var state = stateMachine.AddState("Renderer"); state.motion = clip;
            source.AddLayer(new AnimatorControllerLayer { name = "Renderer", stateMachine = stateMachine, defaultWeight = 1 });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(candidates[0].MaskCoverage, Is.Not.EqualTo(candidates[1].MaskCoverage));
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(2));
        }

        [Test]
        public void IndependentAnimatorsSharingOneActivationClipCannotProveEqualVisibility()
        {
            var pair = MergePair(); var firstRoot = PolicyChild("First root"); var secondRoot = PolicyChild("Second root");
            pair.First.transform.SetParent(firstRoot.transform, true); pair.First.name = "Target";
            pair.Second.transform.SetParent(secondRoot.transform, true); pair.Second.name = "Target";
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Target", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(TreeController(("Input", clip)));
            var entries = new Dictionary<object, VirtualAnimatorController>
            { [firstRoot.AddComponent<Animator>()] = controller, [secondRoot.AddComponent<Animator>()] = controller };
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, assertEligible: false, entries: entries);
            Assert.That(candidates.All(c => c == null), Is.True);
        }

        [Test]
        public void OneNestedAnimatorCanStillProveMatchingActivationCurves()
        {
            var pair = MergePair(); var owner = PolicyChild("Nested Animator");
            pair.First.transform.SetParent(owner.transform, true); pair.Second.transform.SetParent(owner.transform, true);
            var clip = Clip();
            foreach (var part in new[] { pair.First, pair.Second }) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(part.name, typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(TreeController(("Input", clip)));
            var entries = new Dictionary<object, VirtualAnimatorController> { [owner.AddComponent<Animator>()] = controller };
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, entries: entries);
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(1));
        }
    }
}
