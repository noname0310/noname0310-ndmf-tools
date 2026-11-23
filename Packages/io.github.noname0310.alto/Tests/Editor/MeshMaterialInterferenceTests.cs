using System;
using System.Collections;
using System.Collections.Generic;
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
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static EditorCurveBinding UniformBinding(string path, string name) =>
            EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "material." + name);

        private (SkinnedMeshRenderer First, SkinnedMeshRenderer Second) IndependentMaterialPair(bool commonFirst = false)
        {
            var pair = MergePair(); var neutral = pair.First.sharedMaterial;
            pair.First.sharedMaterial = Track(new Material(Shader.Find("Hidden/Noname/Independent Material A")));
            pair.Second.sharedMaterial = Track(new Material(Shader.Find("Hidden/Noname/Independent Material B")));
            if (commonFirst)
            {
                RepeatMaterialSlots(pair.First, new[] { neutral, pair.First.sharedMaterial });
                RepeatMaterialSlots(pair.Second, new[] { neutral, pair.Second.sharedMaterial });
            }
            return pair;
        }

        private AnimatorController IndependentMaterialController(string first, string second, bool writeDefaults)
        {
            var a = Clip(); a.name = "Independent A"; var b = Clip(); b.name = "Independent B";
            AnimationUtility.SetEditorCurve(a, UniformBinding(first, "_OnlyA"), AnimationCurve.Constant(0, 2, 1));
            AnimationUtility.SetEditorCurve(a, UniformBinding(first, "_OnlyB"), AnimationCurve.Constant(0, 2, 0.85f));
            AnimationUtility.SetEditorCurve(b, UniformBinding(second, "_OnlyB"), AnimationCurve.Constant(0, 2, 0.7f));
            AnimationUtility.SetEditorCurve(b, UniformBinding(second, "_OnlyA"), AnimationCurve.Constant(0, 2, 0.95f));
            var controller = TreeController(("GestureLeftWeight", a), ("GestureRightWeight", b));
            controller.AddParameter("Empty", AnimatorControllerParameterType.Bool);
            var machine = controller.layers[0].stateMachine; var driven = machine.defaultState; driven.name = "Driven";
            driven.writeDefaultValues = writeDefaults;
            var empty = Track(machine.AddState("Empty")); empty.motion = Clip(); empty.writeDefaultValues = writeDefaults;
            var enter = Track(driven.AddTransition(empty)); enter.hasExitTime = false; enter.duration = 0;
            enter.AddCondition(AnimatorConditionMode.If, 0, "Empty");
            var leave = Track(empty.AddTransition(driven)); leave.hasExitTime = false; leave.duration = 0;
            leave.AddCondition(AnimatorConditionMode.IfNot, 0, "Empty");
            return controller;
        }

        [Test]
        public void MaterialInterferenceUsesPreprocessorIdentifierAbsence()
        {
            var shader = Shader.Find("Hidden/Noname/Independent Material B"); var usage = new ShaderPropertyUsage();
            Assert.That(usage.Get(shader).Uncertain, Does.Contain("AbsentOptionalMaterialLibrary"));
            Assert.That(usage.MayRead(shader, "_OnlyA"), Is.False, "An inactive unavailable include must not invalidate a proven absence.");
            Assert.That(usage.MayRead(shader, "_OnlyB"), Is.True);
            var source = new ShaderPreprocessorGraph.Source("shader", "#define CAT(a,b) a##b\n#if ENABLE\nfloat CAT(_Only,A);\n#endif");
            var graph = ShaderPreprocessorGraph.AnalyzeDeclarations(source, (o, p) => Array.Empty<ShaderPreprocessorGraph.Source>(),
                ShaderInputQuery.Identifier("_OnlyA"), new[] { "ENABLE" });
            Assert.That(graph.Issue, Is.Null); Assert.That(graph.InputAbsent, Is.False, "Conditional token-pasted uniforms remain observable.");
            graph = ShaderPreprocessorGraph.AnalyzeDeclarations(source, (o, p) => Array.Empty<ShaderPreprocessorGraph.Source>(),
                ShaderInputQuery.Identifier("_Onlya"), new[] { "ENABLE" });
            Assert.That(graph.Issue, Is.Null); Assert.That(graph.InputAbsent, Is.True, "Uniform spelling is case-sensitive.");
        }

        [Test]
        public void MaterialInterferenceRelocatesIndependentProfilesAndDropsOnlyUnobservedWriters()
        {
            var pair = IndependentMaterialPair(); var source = IndependentMaterialController("First", "Second", true);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var group = MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, controller,
                preservedParameters: new[] { "Empty" }), true).Single();
            MeshOptimizationPass.Merge(group, "First", true, mesh => Track(mesh));
            var clips = controller.AllReachableNodes().OfType<VirtualClip>().Where(c => c.GetFloatCurveBindings().Any()).ToArray();
            Assert.That(clips.SelectMany(c => c.GetFloatCurveBindings()).Select(b => b.path), Is.All.EqualTo("First"));
            Assert.That(clips.Single(c => c.Name == "Independent A").GetFloatCurveBindings().Select(b => b.propertyName), Is.EqualTo(new[] { "material._OnlyA" }));
            Assert.That(clips.Single(c => c.Name == "Independent B").GetFloatCurveBindings().Select(b => b.propertyName), Is.EqualTo(new[] { "material._OnlyB" }));
            Assert.That(clips.Select(MotionAnalysis.EndTime), Is.All.EqualTo(2));
            Assert.That(source.animationClips.SelectMany(AnimationUtility.GetCurveBindings).Count(), Is.EqualTo(4));
        }

        [TestCase("UnanimatedReader")] [TestCase("SwapReader")] [TestCase("DifferentSignal")] [TestCase("ColorChannels")]
        public void MaterialInterferenceRetainsObservableConflicts(string boundary)
        {
            var pair = IndependentMaterialPair(); var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, UniformBinding("First", boundary == "ColorChannels" ? "_TintA.r" : "_OnlyA"), AnimationCurve.Constant(0, 2, 1));
            if (boundary == "SwapReader")
                AnimationUtility.SetObjectReferenceCurve(clip, MaterialSlot("Second", 0), new[] { new ObjectReferenceKeyframe { value = pair.First.sharedMaterial } });
            else pair.Second.sharedMaterial = pair.First.sharedMaterial;
            if (boundary == "DifferentSignal" || boundary == "ColorChannels")
                AnimationUtility.SetEditorCurve(clip, UniformBinding("Second", boundary == "ColorChannels" ? "_TintA.g" : "_OnlyA"), AnimationCurve.Constant(0, 2, 0.5f));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(MeshMergeAnalysis.Group(candidates, true, new MeshMergePolicy { AllowAnimatedMaterialDefaultChanges = true }).Count, Is.EqualTo(2));
            Assert.That(MeshMergeAnalysis.Incompatibility(candidates[0], candidates[1]), Does.Contain("interfering"));
        }

        [TestCase(false)] [TestCase(true)]
        public void MaterialInterferenceKeepsTheOriginalProducerDefaultUnlessExplicitlyAllowed(bool allowDefaultChange)
        {
            var pair = IndependentMaterialPair(); pair.Second.sharedMaterial.SetFloat("_OnlyB", 0.4f);
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, UniformBinding("Second", "_OnlyB"), AnimationCurve.Constant(0, 2, 1));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(TreeController(("GestureRightWeight", clip)));
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(MeshMergeAnalysis.Group(candidates, true, new MeshMergePolicy { AllowAnimatedMaterialDefaultChanges = allowDefaultChange }).Count,
                Is.EqualTo(allowDefaultChange ? 1 : 2));
        }

        [UnityTest] public IEnumerator MaterialInterferenceNativePixelsWithWriteDefaults() => VerifyIndependentMaterialPixels(true, false);
        [UnityTest] public IEnumerator MaterialInterferenceNativePixelsWithoutWriteDefaults() => VerifyIndependentMaterialPixels(false, false);
        [UnityTest] public IEnumerator MaterialInterferenceNativeSecondarySlotsWithWriteDefaults() => VerifyIndependentMaterialPixels(true, true);
        [UnityTest] public IEnumerator MaterialInterferenceNativeSecondarySlotsWithoutWriteDefaults() => VerifyIndependentMaterialPixels(false, true);

        private IEnumerator VerifyIndependentMaterialPixels(bool writeDefaults, bool commonFirst)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("GPU output comparison requires a graphics device.");
            var pair = IndependentMaterialPair(commonFirst);
            var source = IndependentMaterialController("First", "Second", writeDefaults);
            var builtParts = new[] { pair.First, pair.Second }.Select((part, i) =>
            {
                var copy = Track(UnityEngine.Object.Instantiate(part.gameObject, root.transform, false)).GetComponent<SkinnedMeshRenderer>();
                copy.name = "Built" + i; return copy;
            }).ToArray();
            var builtSource = IndependentMaterialController("Built0", "Built1", writeDefaults);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(builtSource);
            var group = MeshMergeAnalysis.Group(MergeCandidates(builtParts, controller, preservedParameters: new[] { "Empty" }), true).Single();
            MeshOptimizationPass.Merge(group, "Built0", true, mesh => Track(mesh)); builtParts[0].enabled = false;
            var camera = PolicyChild("Independent property camera").AddComponent<Camera>(); camera.scene = scene; camera.enabled = false;
            camera.transform.localPosition = new Vector3(0.5f, 0.5f, -4); camera.orthographic = true; camera.orthographicSize = 2;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            var target = RenderTexture.GetTemporary(96, 96, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Color32[][] Sample(AnimatorController runtime)
            {
                var animator = root.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var graph = PlayableGraph.Create("Independent material profiles");
                try
                {
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    var playable = AnimatorControllerPlayable.Create(graph, runtime);
                    AnimationPlayableOutput.Create(graph, "Avatar", animator).SetSourcePlayable(playable); graph.Play();
                    var frames = new[] { (0f, 0f, false), (0.4f, 0.7f, false), (1f, 0f, false), (1f, 1f, false),
                        (0f, 1f, false), (0f, 0f, true), (0f, 0f, false) };
                    return frames.Select(f =>
                    {
                        playable.SetFloat("GestureLeftWeight", f.Item1); playable.SetFloat("GestureRightWeight", f.Item2); playable.SetBool("Empty", f.Item3);
                        graph.Evaluate(1f / 60); graph.Evaluate(1f / 60); return DrawAssembly(camera, target);
                    }).ToArray();
                }
                finally { graph.Destroy(); UnityEngine.Object.DestroyImmediate(animator); }
            }
            try
            {
                var before = Sample(source);
                Assert.That(before[0].Zip(before[1], (a, b) => Math.Max(Math.Abs(a.r - b.r), Math.Abs(a.g - b.g))).Max(), Is.GreaterThan(20));
                pair.First.enabled = pair.Second.enabled = false; builtParts[0].enabled = true;
                yield return null;
                var after = Sample(CommitAnimator(controller, root));
                for (int f = 0; f < before.Length; f++)
                    Assert.That(before[f].Zip(after[f], (a, b) => Math.Max(Math.Max(Math.Abs(a.r - b.r), Math.Abs(a.g - b.g)), Math.Abs(a.b - b.b))).Max(),
                        Is.LessThanOrEqualTo(1), "Independent output changed at frame " + f);
            }
            finally { RenderTexture.ReleaseTemporary(target); }
        }
    }
}
