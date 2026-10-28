using System.Collections;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private (SkinnedMeshRenderer Body, SkinnedMeshRenderer Head, Transform Frame, VRCHeadChop Chop) HeadChopPair()
        {
            var bodyBone = PolicyChild("Body bone").transform;
            var frame = PolicyChild("Head frame").transform; frame.localPosition = new Vector3(0, 0.3f, 0);
            var headBone = PolicyChild("Head bone", frame).transform;
            var first = AssemblyPart("Body part", new[] { bodyBone }, Vector3.left);
            var second = AssemblyPart("Head part", new[] { headBone }, Vector3.right); second.rootBone = frame;
            first.lightProbeUsage = second.lightProbeUsage = LightProbeUsage.Off;
            first.reflectionProbeUsage = second.reflectionProbeUsage = ReflectionProbeUsage.Off;
            second.sharedMaterials = first.sharedMaterials;
            var chop = PolicyChild("Chop settings").AddComponent<VRCHeadChop>();
            chop.globalScaleFactor = 0;
            chop.targetBones = new[] { new VRCHeadChop.HeadChopBone { transform = frame, scaleFactor = 1 } };
            return (first, second, frame, chop);
        }

        [TestCase(0f, false)] [TestCase(0f, true)] [TestCase(0.5f, false)]
        [TestCase(0.5f, true)] [TestCase(1f, false)] [TestCase(1f, true)]
        public void HeadChopScaleWritesRemainMutableWithoutInventingWindingReversals(float factor, bool constantTrack)
        {
            var pair = HeadChopPair(); pair.Chop.globalScaleFactor = factor;
            var clip = Clip();
            if (constantTrack) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(pair.Chop.name, typeof(VRCHeadChop), "globalScaleFactor"), AnimationCurve.Constant(0, 1, factor));
            var policy = MergePolicy(true, clip);
            Assert.That(policy.Mutations.Mutable, Does.Contain(pair.Frame));
            Assert.That(policy.Mutations.MayChangeScale(pair.Frame), Is.True);
            Assert.That(policy.Mutations.MayReverseScale(pair.Frame), Is.False);
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.Body, pair.Head }), true, policy).Count, Is.EqualTo(1));
        }

        [TestCase("NegativeGlobal")] [TestCase("NegativeBone")] [TestCase("NonfiniteGlobal")]
        [TestCase("NonfiniteBone")] [TestCase("NegativeInitialScale")]
        [TestCase("AnimatedFactor")] [TestCase("AnimatedTransformScale")] [TestCase("ScaleConstraint")]
        [TestCase("OutsideSkinBone")]
        public void HeadChopMergeRetainsUnprovenSignsAndIndependentCollapsedGeometry(string reason)
        {
            var pair = HeadChopPair(); var clip = Clip();
            switch (reason)
            {
                case "NegativeGlobal": pair.Chop.globalScaleFactor = -1; break;
                case "NegativeBone": pair.Chop.targetBones[0].scaleFactor = -1; break;
                case "NonfiniteGlobal": pair.Chop.globalScaleFactor = float.NaN; break;
                case "NonfiniteBone": pair.Chop.targetBones[0].scaleFactor = float.PositiveInfinity; break;
                case "NegativeInitialScale": pair.Frame.localScale = new Vector3(-1, 1, 1); break;
                case "AnimatedFactor": AnimationUtility.SetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve(pair.Chop.name, typeof(VRCHeadChop), "globalScaleFactor"), AnimationCurve.Linear(0, 0, 1, -1)); break;
                case "AnimatedTransformScale": AnimationUtility.SetEditorCurve(clip,
                    EditorCurveBinding.FloatCurve(pair.Frame.name, typeof(Transform), "m_LocalScale.x"), AnimationCurve.Linear(0, 1, 1, -1)); break;
                case "ScaleConstraint": pair.Frame.gameObject.AddComponent<VRCScaleConstraint>(); break;
                case "OutsideSkinBone": pair.Head.bones = pair.Body.bones; break;
            }
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.Body, pair.Head }), true, MergePolicy(true, clip)).Count, Is.EqualTo(2));
        }

        [Test]
        public void EmptyHeadChopTargetEntriesDoNotInventAdditionalScaleWriters()
        {
            var pair = HeadChopPair();
            pair.Chop.targetBones = pair.Chop.targetBones.Append(new VRCHeadChop.HeadChopBone { scaleFactor = 1 }).ToArray();
            var policy = MergePolicy(true);
            Assert.That(policy.Mutations.MayChangeScale(pair.Frame), Is.True);
            Assert.That(policy.Mutations.MayReverseScale(pair.Frame), Is.False);
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.Body, pair.Head }), true, policy).Count, Is.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void ScaleSignProofHonorsPreservedAnimatorInputs(bool preserved)
        {
            var pair = HeadChopPair(); var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(pair.Chop.name, typeof(VRCHeadChop), "globalScaleFactor"),
                AnimationCurve.Constant(0, 1, -1));
            var source = TreeController(("Keep", clip));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var index = AvatarAnimationIndex.Create(root, entries, null, preserved ? new[] { "Keep" } : null);
            var mutation = TransformMutationAnalysis.Create(root, index);
            Assert.That(mutation.MayChangeScale(pair.Frame), Is.True);
            Assert.That(mutation.MayReverseScale(pair.Frame), Is.EqualTo(preserved));
        }

        [Test]
        public void NonReversingHeadChopDoesNotMakeParentConstraintScaleInheritanceSafe()
        {
            var settings = ObjectSettings(); var pair = HeadChopPair(); pair.Frame.localPosition = Vector3.zero;
            var output = PolicyChild("Following target").transform;
            var c = FollowingParent(pair.Frame, output);
            Assert.That(SimplifyParents(settings), Is.Zero);
            Assert.That(c != null && output.parent == root.transform, Is.True);
        }

        [UnityTest] public IEnumerator HeadChopMergedRootsPreserveCullingThroughNonnegativeCollapse() => VerifyHeadChopGpu(0);
        [UnityTest] public IEnumerator HeadChopMergedRootsPreserveWorldNormalsThroughNonnegativeCollapse() => VerifyHeadChopGpu(1);
        [UnityTest] public IEnumerator HeadChopMergedRootsPreserveWorldTangentsThroughNonnegativeCollapse() => VerifyHeadChopGpu(2);
        [UnityTest] public IEnumerator HeadChopMergedRootsPreserveWorldBitangentsThroughNonnegativeCollapse() => VerifyHeadChopGpu(3);

        private IEnumerator VerifyHeadChopGpu(int frameMode)
        {
            OcclusionGpuRequired(); var pair = HeadChopPair(); var material = OcclusionMaterial();
            if (frameMode != 0)
            {
                material = Track(new Material(Shader.Find("Hidden/Noname/Assembly World Frame")));
                material.SetFloat("_Mode", frameMode);
            }
            pair.Body.sharedMaterial = pair.Head.sharedMaterial = material;
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.Body, pair.Head }), true, MergePolicy(true)).Count, Is.EqualTo(1));
            var first = Track(UnityEngine.Object.Instantiate(pair.Body.gameObject, root.transform, false)).GetComponent<SkinnedMeshRenderer>();
            var second = Track(UnityEngine.Object.Instantiate(pair.Head.gameObject, root.transform, false)).GetComponent<SkinnedMeshRenderer>();
            first.name = "Merged body"; second.name = "Merged head";
            MeshOptimizationPass.Merge(MergeCandidates(new[] { first, second }), first.name, true, mesh => Track(mesh), expandBounds: true);
            first.enabled = false;
            var camera = PolicyChild("Camera").AddComponent<Camera>(); camera.scene = scene; camera.enabled = false;
            camera.orthographic = true; camera.orthographicSize = 2; camera.renderingPath = RenderingPath.Forward;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            var texture = RenderTexture.GetTemporary(96, 96, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try
            {
                // The SDK component is authoring data. Exercise its documented
                // nonnegative bone scale domain; this is not an in-client simulation.
                foreach (float scale in new[] { 1f, 0.5f, 0.00001f, 0f, 1f })
                {
                    pair.Frame.localScale = Vector3.one * scale;
                    pair.Body.enabled = pair.Head.enabled = true; first.enabled = false;
                    EditorApplication.QueuePlayerLoopUpdate(); yield return null;
                    var before = new List<Color32[]>();
                    foreach (int direction in new[] { -1, 1 })
                    {
                        camera.transform.position = new Vector3(0.5f, 0.5f, direction * 4);
                        camera.transform.LookAt(new Vector3(0.5f, 0.5f, 0));
                        before.Add(DrawAssembly(camera, texture));
                    }
                    Assert.That(before.SelectMany(p => p).Any(p => p.r > 20), Is.True, "The retained body must remain visible.");
                    pair.Body.enabled = pair.Head.enabled = false; first.enabled = true;
                    EditorApplication.QueuePlayerLoopUpdate(); yield return null;
                    int view = 0;
                    foreach (int direction in new[] { -1, 1 })
                    {
                        camera.transform.position = new Vector3(0.5f, 0.5f, direction * 4);
                        camera.transform.LookAt(new Vector3(0.5f, 0.5f, 0));
                        Assert.That(DrawAssembly(camera, texture), Is.EqualTo(before[view++]), "Scale " + scale);
                    }
                }
            }
            finally { RenderTexture.ReleaseTemporary(texture); }
        }
    }
}
