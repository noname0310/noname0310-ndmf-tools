using System;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private AnimationClip SharedMaterialCurve(SkinnedMeshRenderer first, SkinnedMeshRenderer second, string property = "_UvProbe")
        {
            var clip = Clip();
            foreach (var part in new[] { first, second }) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(part.name, typeof(SkinnedMeshRenderer), "material." + property), AnimationCurve.Constant(0, 1, 1));
            return clip;
        }

        [TestCase(false, 0f)] [TestCase(false, 0.5f)] [TestCase(false, 1f)]
        [TestCase(true, 0f)] [TestCase(true, 0.5f)] [TestCase(true, 1f)]
        public void MaterialMergeDistinguishesEqualDefaultsFromFullWeightCoverage(bool equalDefaults, float weight)
        {
            var pair = MergePair(); pair.Second.sharedMaterial = Track(new Material(pair.First.sharedMaterial));
            pair.First.sharedMaterial.SetFloat("_UvProbe", 0.2f); pair.Second.sharedMaterial.SetFloat("_UvProbe", equalDefaults ? 0.2f : 0.8f);
            var source = TreeController(("Amount", SharedMaterialCurve(pair.First, pair.Second)));
            var parameters = source.parameters; parameters[0].defaultFloat = weight; source.parameters = parameters;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(equalDefaults || weight == 1 ? 1 : 2));
        }

        [TestCase(false)] [TestCase(true)]
        public void MaterialMergeFullCoverageWorksWithBothWriteDefaultsModes(bool writeDefaults)
        {
            var pair = MergePair(); pair.Second.sharedMaterial = Track(new Material(pair.First.sharedMaterial));
            pair.First.sharedMaterial.SetFloat("_UvProbe", 0.2f); pair.Second.sharedMaterial.SetFloat("_UvProbe", 0.8f);
            var source = Controller(SharedMaterialCurve(pair.First, pair.Second)); source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, controller), true).Count, Is.EqualTo(1));
        }

        [TestCase("VariableWeight")] [TestCase("MissingState")] [TestCase("LayerControl")]
        [TestCase("PartialLayer")] [TestCase("Additive")]
        public void MaterialMergeRetainsObservableDefaultDifferences(string reason)
        {
            var pair = MergePair(); pair.Second.sharedMaterial = Track(new Material(pair.First.sharedMaterial));
            pair.First.sharedMaterial.SetFloat("_UvProbe", 0.2f); pair.Second.sharedMaterial.SetFloat("_UvProbe", 0.8f);
            var clip = SharedMaterialCurve(pair.First, pair.Second);
            var source = reason == "VariableWeight" ? TreeController(("GestureLeftWeight", clip)) : Controller(clip);
            if (reason == "MissingState")
            {
                source = Controller(clip, Clip()); source.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
                var machine = source.layers[0].stateMachine;
                var transition = Track(machine.defaultState.AddTransition(machine.states[1].state)); transition.hasExitTime = false;
                transition.AddCondition(AnimatorConditionMode.Greater, 0, "GestureLeft");
            }
            if (reason == "LayerControl")
            {
                var control = Track(ScriptableObject.CreateInstance<VRC.SDK3.Avatars.Components.VRCPlayableLayerControl>());
                control.layer = VRC_PlayableLayerControl.BlendableLayer.FX; control.goalWeight = 0.5f;
                source.layers[0].stateMachine.defaultState.behaviours = new StateMachineBehaviour[] { control };
            }
            if (reason == "PartialLayer")
            {
                var empty = Controller(Clip()); var layers = source.layers; layers[0].defaultWeight = 0.5f;
                source.layers = new[] { empty.layers[0], layers[0] };
            }
            if (reason == "Additive")
            { var layers = source.layers; layers[0].blendingMode = AnimatorLayerBlendingMode.Additive; source.layers = layers; }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(2));
            Assert.That(MeshMergeAnalysis.Group(candidates, true,
                new MeshMergePolicy { AllowAnimatedMaterialDefaultChanges = true }).Count, Is.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void MaterialMergeChecksWholeColorOutputInsteadOfOnlyTheBoundChannel(bool allChannels)
        {
            var pair = MergePair(); var material = Track(new Material(Shader.Find("Hidden/Noname/Assembly Vertex Inputs")));
            pair.First.sharedMaterial = material; pair.Second.sharedMaterial = Track(new Material(material));
            pair.First.sharedMaterial.SetColor("_Color", new Color(0.2f, 0.3f, 0.4f, 1));
            pair.Second.sharedMaterial.SetColor("_Color", new Color(0.2f, 0.8f, 0.4f, 1));
            var clip = SharedMaterialCurve(pair.First, pair.Second, "_Color.r");
            if (allChannels)
                foreach (var part in new[] { pair.First, pair.Second })
                    foreach (char channel in "gba") AnimationUtility.SetEditorCurve(clip,
                        EditorCurveBinding.FloatCurve(part.name, typeof(SkinnedMeshRenderer), "material._Color." + channel), AnimationCurve.Constant(0, 1, 1));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(allChannels ? 1 : 2));
            Assert.That(MeshMergeAnalysis.Group(candidates, true,
                new MeshMergePolicy { AllowAnimatedMaterialDefaultChanges = true }).Count, Is.EqualTo(allChannels ? 1 : 2),
                "Unanimated channels of a shared native color/vector output remain protected.");
        }

        [TestCase(false)] [TestCase(true)]
        public void MaterialMergeKeepsNativeAnimatedValuesAcrossPartialWeights(bool writeDefaults)
        {
            var pair = MergePair(); pair.Second.sharedMaterial = Track(new Material(pair.First.sharedMaterial));
            pair.First.sharedMaterial.SetFloat("_UvProbe", 0.2f); pair.Second.sharedMaterial.SetFloat("_UvProbe", 0.2f);
            var source = TreeController(("GestureLeftWeight", SharedMaterialCurve(pair.First, pair.Second)));
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var before = SampleMergeProperty(source, new[] { pair.First, pair.Second });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var group = MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, controller), true).Single();
            var originalMaterials = new[] { pair.First.sharedMaterial, pair.Second.sharedMaterial };
            MeshOptimizationPass.Merge(group, "First", true, mesh => Track(mesh));
            var after = SampleMergeProperty(CommitAnimator(controller, root), new[] { pair.First });
            for (int frame = 0; frame < before.Length; frame++)
                Assert.That(before[frame].All(v => Mathf.Abs(v - after[frame][0]) < 1e-6f), Is.True, "Frame " + frame);
            Assert.That(before[0][0], Is.EqualTo(0.2f).Within(1e-6f));
            Assert.That(before[1][0], Is.EqualTo(0.6f).Within(1e-6f));
            Assert.That(pair.First.sharedMaterials, Is.EqualTo(originalMaterials));
            Assert.That(originalMaterials.All(m => m.GetFloat("_UvProbe") == 0.2f), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void ZeroWeightAnimationWritesRendererDefaultsAcrossDistinctMaterialSlots(bool writeDefaults)
        {
            var pair = MergePair(); pair.Second.sharedMaterial = Track(new Material(pair.First.sharedMaterial));
            pair.First.sharedMaterial.SetFloat("_UvProbe", 0.2f); pair.Second.sharedMaterial.SetFloat("_UvProbe", 0.8f);
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }), true).Count, Is.EqualTo(1),
                "Unanimated material values may differ between slots.");
            var source = TreeController(("GestureLeftWeight", SharedMaterialCurve(pair.First, pair.Second)));
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var policy = new MeshMergePolicy { AllowShaderIndexChanges = true };
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, controller, policy: policy), true, policy).Count,
                Is.EqualTo(2), "Shader-index permission must not relax material animation defaults.");
            var before = SampleMergeProperty(source, new[] { pair.First, pair.Second });
            // Deliberately assemble the otherwise rejected pair to measure Unity's
            // renderer-wide output. This is a test fixture, not an optimizer bypass.
            using var assembly = SkinnedMeshAssembler.Create(new[] { pair.First, pair.Second }, true);
            var combined = AssemblyOutput(assembly); Track(combined.sharedMesh);
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(combined.name, typeof(SkinnedMeshRenderer), "material._UvProbe"), AnimationCurve.Constant(0, 1, 1));
            var mergedController = TreeController(("GestureLeftWeight", clip));
            mergedController.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var after = SampleMergeProperty(mergedController, new[] { combined });
            Assert.That(before[0][0], Is.EqualTo(0.2f).Within(1e-6));
            Assert.That(before[0][1], Is.EqualTo(0.8f).Within(1e-6));
            Assert.That(after[0][0], Is.EqualTo(0.2f).Within(1e-6));
            Assert.That(before[1][1], Is.EqualTo(0.9f).Within(1e-6));
            Assert.That(after[1][0], Is.EqualTo(0.6f).Within(1e-6));
            Assert.That(combined.sharedMaterials[1].GetFloat("_UvProbe"), Is.EqualTo(0.8f),
                "The original material value survives but the renderer-wide animation overrides it.");
        }

        [TestCase(false)] [TestCase(true)]
        public void AnimatedDefaultPermissionUsesRetainedValuesAcrossZeroPartialAndFullWeights(bool writeDefaults)
        {
            var pair = MergePair(); pair.Second.sharedMaterial = Track(new Material(pair.First.sharedMaterial));
            pair.First.sharedMaterial.SetFloat("_UvProbe", 0.2f); pair.Second.sharedMaterial.SetFloat("_UvProbe", 0.8f);
            var materials = new[] { pair.First.sharedMaterial, pair.Second.sharedMaterial };
            var source = TreeController(("GestureLeftWeight", SharedMaterialCurve(pair.First, pair.Second)));
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var before = SampleMergeProperty(source, new[] { pair.First, pair.Second });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            var group = MeshMergeAnalysis.Group(candidates, true, new MeshMergePolicy { AllowAnimatedMaterialDefaultChanges = true }).Single();
            var changes = candidates[0].Animations.MaterialInputs.DefaultChanges(pair.First, pair.Second).ToArray();
            Assert.That(changes, Is.EqualTo(new[] { ("material._UvProbe", 0.8f, 0.2f) }));
            MeshOptimizationPass.Merge(group, "First", true, mesh => Track(mesh));
            var after = SampleMergeProperty(CommitAnimator(controller, root), new[] { pair.First });
            for (int frame = 0; frame < after.Length; frame++)
                Assert.That(after[frame][0], Is.EqualTo(before[frame][0]).Within(1e-6f));
            Assert.That(before[0][1], Is.EqualTo(0.8f).Within(1e-6f));
            Assert.That(after[0][0], Is.EqualTo(0.2f).Within(1e-6f));
            Assert.That(after[1][0], Is.EqualTo(0.6f).Within(1e-6f));
            Assert.That(after[2][0], Is.EqualTo(1f).Within(1e-6f));
            Assert.That(pair.First.sharedMaterials, Is.EqualTo(materials));
            Assert.That(materials[1].GetFloat("_UvProbe"), Is.EqualTo(0.8f));
        }

        [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void AnimatedDefaultPermissionRequiresEveryInputAndMatchingCurves(bool bothAllowed, bool sameCurve)
        {
            var pair = MergePair(); pair.Second.sharedMaterial = Track(new Material(pair.First.sharedMaterial));
            pair.First.sharedMaterial.SetFloat("_UvProbe", 0.2f); pair.Second.sharedMaterial.SetFloat("_UvProbe", 0.8f);
            var clip = SharedMaterialCurve(pair.First, pair.Second);
            if (!sameCurve) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(pair.Second.name, typeof(SkinnedMeshRenderer), "material._UvProbe"), AnimationCurve.Constant(0, 1, 0.5f));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(TreeController(("GestureLeftWeight", clip)));
            var policy = new MeshMergePolicy { PermissionsFor = r => bothAllowed || r == pair.First
                ? MeshOptimizationPermissions.AnimatedMaterialDefaultChanges : MeshOptimizationPermissions.None };
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, controller), true, policy).Count,
                Is.EqualTo(bothAllowed && sameCurve ? 1 : 2));
        }

        private float[][] SampleMergeProperty(AnimatorController controller, SkinnedMeshRenderer[] parts)
        {
            var animator = root.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var graph = PlayableGraph.Create("Material merge default equivalence");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Avatar", animator).SetSourcePlayable(playable); graph.Play();
                return new[] { 0f, 0.5f, 1f, 0.5f, 0f }.Select(weight =>
                {
                    playable.SetFloat("GestureLeftWeight", weight); graph.Evaluate(1f / 60);
                    return parts.Select(part => { var block = new MaterialPropertyBlock(); part.GetPropertyBlock(block); return block.GetFloat("_UvProbe"); }).ToArray();
                }).ToArray();
            }
            finally
            {
                graph.Destroy(); UnityEngine.Object.DestroyImmediate(animator);
                foreach (var part in parts) part.SetPropertyBlock(null);
            }
        }
    }
}
