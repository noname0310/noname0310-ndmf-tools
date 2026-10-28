using System;
using System.Collections;
using System.Linq;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
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
        private static void RepeatMaterialSlots(SkinnedMeshRenderer part, Material[] materials)
        {
            var triangles = part.sharedMesh.GetTriangles(0); part.sharedMesh.subMeshCount = materials.Length;
            for (int i = 0; i < materials.Length; i++) part.sharedMesh.SetTriangles(triangles, i);
            part.sharedMaterials = materials;
        }

        [TestCase(false)] [TestCase(true)]
        public void AnimatedMaterialDefaultsIgnoreOnlyAdjacentIdenticalSlotRepetition(bool differentOrder)
        {
            var pair = MergePair(); var first = pair.First.sharedMaterial; var second = Track(new Material(first));
            RepeatMaterialSlots(pair.First, new[] { first, first, second });
            RepeatMaterialSlots(pair.Second, differentOrder ? new[] { second, first, first } : new[] { first, second, second });
            var clip = Clip();
            foreach (var part in new[] { pair.First, pair.Second }) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(part.name, typeof(SkinnedMeshRenderer), "material._Color.r"), AnimationCurve.Constant(0, 1, 0.5f));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, controller), true).Count,
                Is.EqualTo(differentOrder ? 2 : 1));
        }

        [TestCase(false)] [TestCase(true)]
        public void SlotReorderingCombinesSeparatedMaterialsAndKeepsTheFirstMaterial(bool reorder)
        {
            var pair = MergePair(); var first = pair.First.sharedMaterial; var second = Track(new Material(first));
            RepeatMaterialSlots(pair.First, new[] { first, second, first });
            pair.Second.sharedMaterial = second;
            using var assembly = SkinnedMeshAssembler.Create(new[] { pair.First, pair.Second }, true, reorderMaterialSlots: reorder);
            Assert.That(assembly.Materials, Is.EqualTo(reorder ? new[] { first, second } : new[] { first, second, first, second }));
            Assert.That(assembly.MaterialSlots[pair.First], Is.EqualTo(reorder ? new[] { 0, 1, 0 } : new[] { 0, 1, 2 }));
            Assert.That(assembly.MaterialSlots[pair.Second], Is.EqualTo(new[] { reorder ? 1 : 3 }));
            Assert.That(Enumerable.Range(0, assembly.Mesh.subMeshCount).Sum(i => (long)assembly.Mesh.GetIndexCount(i)), Is.EqualTo(12));
            Assert.That(pair.First.sharedMesh.subMeshCount, Is.EqualTo(3));
        }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(true, true)]
        public void ReorderingKeepsTheAnimatedFirstMaterialInterface(bool reorder, bool differentFirst)
        {
            var pair = MergePair(); var a = pair.First.sharedMaterial; var b = Track(new Material(a)); var c = Track(new Material(a));
            RepeatMaterialSlots(pair.First, new[] { a, b, c, a });
            RepeatMaterialSlots(pair.Second, differentFirst ? new[] { b, c, a } : new[] { a, c, b });
            var clip = Clip();
            foreach (var part in new[] { pair.First, pair.Second }) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(part.name, typeof(SkinnedMeshRenderer), "material._Color.r"), AnimationCurve.Constant(0, 1, 0.5f));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var groups = MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, controller), true,
                new MeshMergePolicy { AllowMaterialSlotReordering = reorder });
            Assert.That(groups.Count, Is.EqualTo(reorder && !differentFirst ? 1 : 2));
        }

        [TestCase(true, BuildTarget.Android, OptimizationPlatforms.Android, true, true)]
        [TestCase(true, BuildTarget.StandaloneWindows64, OptimizationPlatforms.Android, true, false)]
        [TestCase(true, BuildTarget.StandaloneWindows64, OptimizationPlatforms.All, true, true)]
        [TestCase(false, BuildTarget.Android, OptimizationPlatforms.All, true, false)]
        [TestCase(true, BuildTarget.Android, OptimizationPlatforms.All, false, false)]
        public void SlotReorderingWorksWithoutRendererMergingOnlyOnSelectedPlatforms(bool enabled, BuildTarget target,
            OptimizationPlatforms platforms, bool mergeSlots, bool reordered)
        {
            var pair = MergePair(); var first = pair.First.sharedMaterial; var other = Track(new Material(first));
            RepeatMaterialSlots(pair.First, new[] { first, other, first }); var source = pair.First.sharedMesh;
            MaterialDescriptor(); var settings = root.AddComponent<ALTOTraceAndOptimizeMeshes>(); settings.ScopeRoot = pair.First.transform;
            Assert.That(settings.AllowMaterialSlotReordering, Is.True);
            settings.MergeCompatibleSkinnedMeshes = false; settings.RebindStaticBones = settings.RemoveUnusedMeshData = false;
            settings.MergeIdenticalMaterialSlots = mergeSlots; settings.AllowMaterialSlotReordering = enabled;
            settings.MaterialSlotReorderingOnPlatforms = platforms;
            var context = new BuildContext(root, null);
            typeof(OptimizationBuildEnvironment).GetField("Target", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(context.GetState<OptimizationBuildEnvironment>(), target);
            MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            if (pair.First.sharedMesh != source) Track(pair.First.sharedMesh);
            Assert.That(pair.First.sharedMaterials.Length, Is.EqualTo(reordered ? 2 : 3));
            Assert.That(pair.First.sharedMaterials[0], Is.SameAs(first));
            Assert.That(source.subMeshCount, Is.EqualTo(3));
            Assert.That(pair.Second != null, Is.True);
        }

        private Color32[][] SampleMaterialFrames(AnimatorController controller, Camera camera, RenderTexture target)
        {
            var animator = root.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var graph = PlayableGraph.Create("Repeated material slot animation equivalence");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Avatar", animator).SetSourcePlayable(playable); graph.Play();
                return new[] { 0.1f, 0.8f, 0.3f, 1f }.Select(value =>
                {
                    playable.SetFloat("Amount", value); graph.Evaluate(1f / 60);
                    return DrawAssembly(camera, target);
                }).ToArray();
            }
            finally { graph.Destroy(); UnityEngine.Object.DestroyImmediate(animator); }
        }

        [UnityTest]
        public IEnumerator RepeatedSlotsKeepAnimatedPixelsWithWriteDefaults() => VerifyRepeatedSlotAnimation(true);

        [UnityTest]
        public IEnumerator RepeatedSlotsKeepAnimatedPixelsWithoutWriteDefaults() => VerifyRepeatedSlotAnimation(false);

        private IEnumerator VerifyRepeatedSlotAnimation(bool writeDefaults)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("This comparison requires a graphics device.");
            var pair = MergePair(); var material = Track(new Material(Shader.Find("VRChat/Mobile/Toon Standard")));
            material.SetFloat("_Culling", 0); material.SetColor("_Color", new Color(0.2f, 0.3f, 0.5f, 1));
            var originalColor = material.GetColor("_Color");
            RepeatMaterialSlots(pair.First, Enumerable.Repeat(material, 2).ToArray());
            RepeatMaterialSlots(pair.Second, Enumerable.Repeat(material, 3).ToArray());
            var clip = Clip(); var color = new Color(0.9f, 0.3f, 0.1f, 1);
            foreach (var part in new[] { pair.First, pair.Second })
                for (int channel = 0; channel < 4; channel++) AnimationUtility.SetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve(part.name, typeof(SkinnedMeshRenderer), "material._Color." + "rgba"[channel]),
                    AnimationCurve.Constant(0, 1, color[channel]));
            var source = TreeController(("Amount", clip)); source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var builtFirst = Track(UnityEngine.Object.Instantiate(pair.First.gameObject, root.transform, false)).GetComponent<SkinnedMeshRenderer>();
            var builtSecond = Track(UnityEngine.Object.Instantiate(pair.Second.gameObject, root.transform, false)).GetComponent<SkinnedMeshRenderer>();
            builtFirst.name = "Built first"; builtSecond.name = "Built second";
            var builtClip = Clip();
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var mapped = binding; mapped.path = binding.path == "First" ? "Built first" : "Built second";
                AnimationUtility.SetEditorCurve(builtClip, mapped, AnimationUtility.GetEditorCurve(clip, binding));
            }
            var builtSource = TreeController(("Amount", builtClip)); builtSource.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(builtSource);
            var group = MeshMergeAnalysis.Group(MergeCandidates(new[] { builtFirst, builtSecond }, controller), true).Single();
            MeshOptimizationPass.Merge(group, "Built first", true, mesh => Track(mesh));
            Assert.That(builtFirst.sharedMaterials.Length, Is.EqualTo(1)); builtFirst.enabled = false;
            var camera = PolicyChild("Camera").AddComponent<Camera>(); camera.scene = scene; camera.enabled = false;
            camera.transform.localPosition = new Vector3(0.5f, 0.5f, -4); camera.orthographic = true; camera.orthographicSize = 2;
            camera.renderingPath = RenderingPath.Forward; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            var light = PolicyChild("Sun").AddComponent<Light>(); light.type = LightType.Directional;
            light.transform.localRotation = Quaternion.Euler(25, 15, 0); light.intensity = 1;
            var target = RenderTexture.GetTemporary(96, 96, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try
            {
                var before = SampleMaterialFrames(source, camera, target);
                Assert.That(before[0].Zip(before[1], (a, b) => Math.Abs(a.r - b.r)).Max(), Is.GreaterThan(20),
                    "The fixture must render a changing material input.");
                pair.First.enabled = pair.Second.enabled = false; builtFirst.enabled = true;
                yield return null;
                var after = SampleMaterialFrames(CommitAnimator(controller, root), camera, target);
                for (int frame = 0; frame < before.Length; frame++)
                    Assert.That(before[frame].Zip(after[frame], (a, b) =>
                        Math.Max(Math.Max(Math.Abs(a.r - b.r), Math.Abs(a.g - b.g)), Math.Abs(a.b - b.b))).Max(),
                        Is.LessThanOrEqualTo(1), "Frame " + frame);
                Assert.That(pair.First.sharedMaterials.Length, Is.EqualTo(2));
                Assert.That(pair.Second.sharedMaterials.Length, Is.EqualTo(3));
                Assert.That(material.GetColor("_Color"), Is.EqualTo(originalColor));
                Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(8));
            }
            finally { RenderTexture.ReleaseTemporary(target); }
        }
    }
}
