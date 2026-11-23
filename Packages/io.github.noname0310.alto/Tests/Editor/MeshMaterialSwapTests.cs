using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static EditorCurveBinding MaterialSlot(string path, int slot) =>
            EditorCurveBinding.PPtrCurve(path, typeof(SkinnedMeshRenderer), "m_Materials.Array.data[" + slot + "]");

        [TestCase(false)] [TestCase(true)]
        public void MeshMaterialSwapsKeepIndependentSlotsAndRelocateDestinationCurves(bool reorder)
        {
            var pair = MergePair(); var initial = pair.First.sharedMaterial;
            var a = Track(new Material(initial)); var b = Track(new Material(initial));
            RepeatMaterialSlots(pair.First, new[] { initial, initial, initial });
            RepeatMaterialSlots(pair.Second, new[] { initial, initial });
            var clip = Clip();
            AnimationUtility.SetObjectReferenceCurve(clip, MaterialSlot("First", 2), new[] {
                new ObjectReferenceKeyframe { time = 0, value = a }, new ObjectReferenceKeyframe { time = 2, value = null } });
            AnimationUtility.SetObjectReferenceCurve(clip, MaterialSlot("Second", 0), new[] {
                new ObjectReferenceKeyframe { time = 0, value = b }, new ObjectReferenceKeyframe { time = 2, value = initial } });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(1));
            var output = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            MeshOptimizationPass.Merge(candidates, "First", true, mesh => Track(mesh), reorderMaterialSlots: reorder);
            Assert.That(pair.First.sharedMaterials.Length, Is.EqualTo(reorder ? 3 : 4));
            Assert.That(output.GetObjectCurveBindings().Select(k => k.path), Is.All.EqualTo("First"));
            Assert.That(output.GetObjectCurve(MaterialSlot("First", 1)).Select(k => k.value), Is.EqualTo(new UnityEngine.Object[] { a, null }));
            Assert.That(output.GetObjectCurve(MaterialSlot("First", 2)).Select(k => k.value), Is.EqualTo(new UnityEngine.Object[] { b, initial }));
            Assert.That(output.GetObjectCurve(MaterialSlot("First", 1)).Select(k => k.time), Is.EqualTo(new[] { 0f, 2f }));
            Assert.That(AnimationUtility.GetObjectReferenceCurve(clip, MaterialSlot("First", 2))[0].value, Is.SameAs(a), "The source clip is immutable.");
        }

        [Test]
        public void MeshMaterialSwapsDoNotCauseRepeatedSlotOnlyMerges()
        {
            var pair = MergePair(); RepeatMaterialSlots(pair.First, new[] { pair.First.sharedMaterial, pair.First.sharedMaterial });
            var clip = Clip(); AnimationUtility.SetObjectReferenceCurve(clip, MaterialSlot("First", 1),
                new[] { new ObjectReferenceKeyframe { value = pair.First.sharedMaterial } });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var candidate = MergeCandidates(new[] { pair.First }, controller);
            Assert.That(MeshMergeAnalysis.HasMergeableMaterialSlots(candidate, false), Is.False);
            Assert.That(MeshMergeAnalysis.HasMergeableMaterialSlots(candidate, true), Is.False);
        }

        [UnityTest]
        public IEnumerator MeshMaterialSwapLiteralKeysPreserveNativeGpuOutput() => VerifyMaterialSwapPixels(2, false);

        [UnityTest]
        public IEnumerator MeshMaterialSwapUniformKeysPreserveNativeGpuOutput() => VerifyMaterialSwapPixels(3, true);

        private Color32[][] SampleMaterialSwapFrames(AnimationClip clip, float[] times, Camera camera, RenderTexture target,
            Action<int> inspect = null)
        {
            var animator = root.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var graph = PlayableGraph.Create("Material swap equivalence");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimationClipPlayable.Create(graph, clip);
                playable.SetApplyFootIK(false); playable.SetApplyPlayableIK(false);
                AnimationPlayableOutput.Create(graph, "Avatar", animator).SetSourcePlayable(playable); graph.Play();
                return times.Select((time, i) =>
                {
                    playable.SetTime(time); graph.Evaluate(0); inspect?.Invoke(i);
                    return DrawAssembly(camera, target);
                }).ToArray();
            }
            finally { graph.Destroy(); UnityEngine.Object.DestroyImmediate(animator); }
        }

        private IEnumerator VerifyMaterialSwapPixels(int count, bool uniform)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("GPU output comparison requires a graphics device.");
            var pair = MergePair();
            var parts = new List<SkinnedMeshRenderer> { pair.First, pair.Second };
            if (count == 3)
            {
                var third = AssemblyPart("Third", pair.First.bones, Vector3.zero);
                third.sharedMaterial = pair.First.sharedMaterial; third.lightProbeUsage = LightProbeUsage.Off; third.reflectionProbeUsage = ReflectionProbeUsage.Off;
                parts.Add(third);
            }
            var indexMaterial = Track(new Material(Shader.Find("Hidden/Noname/Vertex ID Preservation")));
            var clip = Clip();
            foreach (var part in parts) AnimationUtility.SetObjectReferenceCurve(clip, MaterialSlot(part.name, 0), new[] {
                new ObjectReferenceKeyframe { time = 0, value = indexMaterial },
                new ObjectReferenceKeyframe { time = 0.5f, value = part.sharedMaterial },
                new ObjectReferenceKeyframe { time = 1, value = null },
                new ObjectReferenceKeyframe { time = 1.5f, value = indexMaterial } });
            // Build into a separate, previously unrendered hierarchy. Replacing
            // an already drawn SMR's layout in the same Editor frame reuses
            // Unity's native skinning cache and does not model a built avatar.
            var builtParts = parts.Select((part, i) =>
            {
                var copy = Track(UnityEngine.Object.Instantiate(part.gameObject, root.transform, false)).GetComponent<SkinnedMeshRenderer>();
                copy.name = "Built" + i; return copy;
            }).ToArray();
            var builtClip = Clip();
            for (int i = 0; i < parts.Count; i++) AnimationUtility.SetObjectReferenceCurve(builtClip, MaterialSlot(builtParts[i].name, 0),
                AnimationUtility.GetObjectReferenceCurve(clip, MaterialSlot(parts[i].name, 0)));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(builtClip));
            var candidates = MergeCandidates(builtParts, controller, policy: new MeshMergePolicy { PreserveVertexIds = true });
            Assert.That(candidates.All(c => c.VertexIdMaterials.Contains(indexMaterial)), Is.True, "A shader present only in a swap key still participates in input analysis.");
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(1));
            using var plan = VertexIdMergePlan.Create(candidates, true, 3);
            Assert.That(plan.UniformShaders, Is.EqualTo(uniform ? 1 : 0));
            var destination = builtParts[0];
            MeshOptimizationPass.Merge(candidates, destination.name, true, mesh => Track(mesh), true,
                vertexIds: plan, saveOther: asset => Track(asset), reorderMaterialSlots: true);
            destination.enabled = false;
            var camera = PolicyChild("Swap camera").AddComponent<Camera>(); camera.scene = scene;
            camera.transform.localPosition = new Vector3(0, 0.5f, -4); camera.orthographic = true; camera.orthographicSize = 3;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; camera.enabled = false;
            var target = RenderTexture.GetTemporary(128, 128, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try
            {
                var times = new[] { 0.25f, 0.75f, 1.25f, 1.75f };
                var before = SampleMaterialSwapFrames(clip, times, camera, target);
                Assert.That(before[0].Any(p => p.b > 100), Is.True);
                Assert.That(before[0].Zip(before[1], (a, b) => Math.Abs(a.g - b.g)).Max(), Is.GreaterThan(20), "The fixture must change materials.");
                var virtualClip = controller.AllReachableNodes().OfType<VirtualClip>().Single();
                var output = Track(new AnimationClip());
                foreach (var binding in virtualClip.GetObjectCurveBindings())
                    AnimationUtility.SetObjectReferenceCurve(output, binding, virtualClip.GetObjectCurve(binding));
                Assert.That(destination.sharedMaterials.Length, Is.EqualTo(count));
                Assert.That(virtualClip.GetObjectCurveBindings().Select(b => b.path), Is.All.EqualTo(destination.name));
                foreach (var part in parts) part.enabled = false;
                destination.enabled = true;
                yield return null;
                var afterFrames = SampleMaterialSwapFrames(output, times, camera, target, i =>
                {
                    Assert.That(destination.sharedMaterials.Select(m => m == null ? "null" : m.shader.name),
                        Is.EqualTo(virtualClip.GetObjectCurveBindings().OrderBy(b => b.propertyName).Select(b =>
                        {
                            var material = virtualClip.GetObjectCurve(b).Last(k => k.time <= times[i]).value as Material;
                            return material == null ? "null" : material.shader.name;
                        })), "Native animation must select the relocated material keys.");
                });
                for (int i = 0; i < times.Length; i++)
                {
                    var after = afterFrames[i];
                    Assert.That(before[i].Zip(after, (a, b) => Math.Max(Math.Max(Math.Abs(a.r - b.r), Math.Abs(a.g - b.g)), Math.Abs(a.b - b.b))).Max(),
                        Is.LessThanOrEqualTo(1), "Material-swap output changed at " + times[i] + "; before " +
                        string.Join(";", before[i].GroupBy(c => c).OrderByDescending(g => g.Count()).Take(5).Select(g => g.Key + ":" + g.Count())) +
                        "; after " + string.Join(";", after.GroupBy(c => c).OrderByDescending(g => g.Count()).Take(5).Select(g => g.Key + ":" + g.Count())));
                }
            }
            finally { RenderTexture.ReleaseTemporary(target); }
        }

        [Test]
        public void MeshMaterialSwapRejectsUnsafeAnimatedShaderEvenWhenInitialShaderIsSafe()
        {
            var pair = MergePair(); var clip = Clip();
            var material = Track(new Material(Shader.Find("Hidden/Noname/Vertex ID Preservation")));
            AnimationUtility.SetObjectReferenceCurve(clip, MaterialSlot("Second", 0), new[] { new ObjectReferenceKeyframe { value = material } });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            Assert.That(MergeCandidates(new[] { pair.Second }, controller, assertEligible: false)[0], Is.Null);
        }

        [Test]
        public void MeshMaterialSwapRetainsChangingFirstPropertyInterface()
        {
            var pair = MergePair(); var clip = Clip();
            AnimationUtility.SetObjectReferenceCurve(clip, MaterialSlot("Second", 0),
                new[] { new ObjectReferenceKeyframe { value = Track(new Material(pair.Second.sharedMaterial)) } });
            foreach (var part in new[] { pair.First, pair.Second }) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(part.name, typeof(SkinnedMeshRenderer), "material._Color.r"), AnimationCurve.Constant(0, 1, 0.5f));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(MeshMergeAnalysis.Incompatibility(candidates[0], candidates[1]), Does.Contain("animated first material"));
        }

        [TestCase(-1)] [TestCase(1)]
        public void MeshMaterialSwapRetainsInvalidSlotBindings(int slot)
        {
            var pair = MergePair(); var clip = Clip();
            AnimationUtility.SetObjectReferenceCurve(clip, MaterialSlot("Second", slot),
                new[] { new ObjectReferenceKeyframe { value = pair.Second.sharedMaterial } });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            Assert.That(MergeCandidates(new[] { pair.Second }, controller, assertEligible: false)[0], Is.Null);
        }
    }
}
