using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private MeshMergePolicy MergePolicy(bool coordinates = false, AnimationClip clip = null) => new()
        {
            ExpandBounds = true, AllowCoordinateSpaceChanges = coordinates,
            Mutations = TransformMutationAnalysis.Create(root, RigidAnimations(clip))
        };

        [TestCase(false)] [TestCase(true)]
        public void MeshMasksWithEqualInheritedWeightsCanShareAGroup(bool equal)
        {
            var pair = MergePair(); var firstParent = PolicyChild("First parent"); var secondParent = PolicyChild("Second parent");
            pair.First.transform.SetParent(firstParent.transform, true); pair.Second.transform.SetParent(secondParent.transform, true);
            var clip = Clip();
            foreach (var part in new[] { pair.First, pair.Second }) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(part.transform, root.transform),
                    typeof(SkinnedMeshRenderer), "m_Enabled"), AnimationCurve.Constant(0, 1, 1));
            var source = Controller(clip); var mask = Track(new AvatarMask()); mask.transformCount = 2;
            mask.SetTransformPath(0, "First parent"); mask.SetTransformActive(0, true);
            mask.SetTransformPath(1, "Second parent"); mask.SetTransformActive(1, equal);
            var layers = source.layers; layers[0].avatarMask = mask; source.layers = layers;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(equal ? 1 : 2));
        }

        [TestCase(false)] [TestCase(true)]
        public void MeshPassConvertsAndGroupsRigidGeometryOnlyOnOptedInPlatforms(bool enabled)
        {
            var pair = MergePair(); MaterialDescriptor();
            var rigidObject = PolicyChild("Rigid source"); rigidObject.transform.localPosition = new Vector3(3, 1, 2);
            var original = Track(UnityEngine.Object.Instantiate(pair.First.sharedMesh)); Frame(original, "Ignored", 100, Vector3.one);
            rigidObject.AddComponent<MeshFilter>().sharedMesh = original;
            var rigid = rigidObject.AddComponent<MeshRenderer>(); rigid.sharedMaterial = pair.First.sharedMaterial;
            rigid.lightProbeUsage = LightProbeUsage.Off; rigid.reflectionProbeUsage = ReflectionProbeUsage.Off;
            var expected = AssemblyWorldVertices(pair.First).Concat(AssemblyWorldVertices(pair.Second))
                .Concat(original.vertices.Select(rigidObject.transform.TransformPoint)).ToArray();
            var settings = root.AddComponent<ALTOTraceAndOptimizeMeshes>(); settings.AllowCoordinateSpaceChanges = true;
            settings.CoordinateSpaceChangesOnPlatforms = enabled ? OptimizationPlatforms.All : OptimizationPlatforms.None;
            settings.RebindStaticBones = false;
            var context = new BuildContext(root, null); MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (skin.sharedMesh != null) Track(skin.sharedMesh);
            if (!enabled) { Assert.That(rigid != null, Is.True); return; }
            Assert.That(rigid == null, Is.True); Assert.That(rigidObject.GetComponent<MeshRenderer>(), Is.Null);
            var output = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToArray();
            Assert.That(output.Length, Is.EqualTo(1)); Assert.That(output[0].sharedMesh.blendShapeCount, Is.Zero);
            var actual = AssemblyWorldVertices(output[0]); Assert.That(actual.Length, Is.EqualTo(expected.Length));
            foreach (var position in expected) Assert.That(actual.Any(p => Vector3.Distance(p, position) < 0.00001f), Is.True);
            Assert.That(original.blendShapeCount, Is.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void MeshPassPreservesExplicitRigidRendererExclusions(bool keep)
        {
            MaterialDescriptor(); var rigid = RigidPart(); rigid.lightProbeUsage = LightProbeUsage.Off; rigid.reflectionProbeUsage = ReflectionProbeUsage.Off;
            var settings = root.AddComponent<ALTOTraceAndOptimizeMeshes>(); settings.AllowCoordinateSpaceChanges = true;
            settings.CoordinateSpaceChangesOnPlatforms = OptimizationPlatforms.All;
            if (keep) settings.PreserveRenderers.Add(rigid);
            var context = new BuildContext(root, null); MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (skin.sharedMesh != null) Track(skin.sharedMesh);
            Assert.That(rigid != null, Is.EqualTo(keep));
        }

        [Test]
        public void MeshPassWeightsAnUnweightedSkinWithoutChangingItsMorphOrGeometry()
        {
            MaterialDescriptor(); var obj = PolicyChild("Unweighted source"); obj.transform.localPosition = new Vector3(2, 1, 3);
            var part = obj.AddComponent<SkinnedMeshRenderer>(); var original = Mesh("Morph"); part.sharedMesh = original;
            part.sharedMaterial = Track(new Material(Shader.Find("Hidden/Noname/Assembly Object Space")));
            part.lightProbeUsage = LightProbeUsage.Off; part.reflectionProbeUsage = ReflectionProbeUsage.Off; part.SetBlendShapeWeight(0, 42);
            var before = AssemblyWorldVertices(part);
            var settings = root.AddComponent<ALTOTraceAndOptimizeMeshes>(); settings.AllowCoordinateSpaceChanges = true;
            settings.CoordinateSpaceChangesOnPlatforms = OptimizationPlatforms.All; settings.RebindStaticBones = false;
            var context = new BuildContext(root, null); MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            Track(part.sharedMesh); Assert.That(RigidMeshConversion.Unweighted(part.sharedMesh), Is.False);
            Assert.That(part.GetBlendShapeWeight(0), Is.EqualTo(42)); Assert.That(original.blendShapeCount, Is.EqualTo(1));
            var after = AssemblyWorldVertices(part);
            for (int vertex = 0; vertex < before.Length; vertex++) Assert.That(Vector3.Distance(before[vertex], after[vertex]), Is.LessThan(0.00001f));
        }

        [TestCase(false)] [TestCase(true)]
        public void MeshBoundsPolicyGroupsByVisibilityAndUnionsEveryInputVolume(bool expand)
        {
            var pair = MergePair();
            pair.First.localBounds = new Bounds(Vector3.left, Vector3.one);
            pair.Second.localBounds = new Bounds(Vector3.right * 3, new Vector3(3, 4, 5));
            var candidates = MergeCandidates(new[] { pair.First, pair.Second });
            var policy = MergePolicy(); policy.ExpandBounds = expand;
            Assert.That(MeshMergeAnalysis.Group(candidates, true, policy).Count, Is.EqualTo(expand ? 1 : 2));
            if (!expand) return;
            var expected = candidates[0].Bounds; expected.Encapsulate(candidates[1].Bounds);
            MeshOptimizationPass.Merge(candidates, "First", true, mesh => Track(mesh), expandBounds: true);
            Assert.That(pair.First.localBounds, Is.EqualTo(expected));
        }

        [TestCase(false)] [TestCase(true)]
        public void MeshCoordinatePolicyRetainsDifferentRootsUnlessExplicitlyAllowed(bool allow)
        {
            var pair = MergePair(); var coordinateRoot = PolicyChild("Other coordinate root").transform;
            coordinateRoot.localPosition = new Vector3(2, 3, 4); coordinateRoot.localRotation = Quaternion.Euler(10, 15, 20);
            coordinateRoot.localScale = new Vector3(0.8f, 1.2f, 1.3f); pair.Second.rootBone = coordinateRoot;
            var candidates = MergeCandidates(new[] { pair.First, pair.Second });
            var policy = MergePolicy(allow);
            Assert.That(MeshMergeAnalysis.Group(candidates, true, policy).Count, Is.EqualTo(allow ? 1 : 2));
            if (!allow) return;
            var before = AssemblyWorldVertices(pair.First).Concat(AssemblyWorldVertices(pair.Second)).ToArray();
            var inputBounds = candidates.Select(c => c.Bounds).ToArray();
            MeshOptimizationPass.Merge(candidates, "First", true, mesh => Track(mesh), expandBounds: true);
            var after = AssemblyWorldVertices(pair.First);
            for (int i = 0; i < before.Length; i++) Assert.That(Vector3.Distance(before[i], after[i]), Is.LessThan(0.00001f));
            foreach (var bound in new[] { inputBounds[0], new Bounds(coordinateRoot.TransformPoint(inputBounds[1].center), Vector3.zero) })
                Assert.That(pair.First.localBounds.Contains(bound.center), Is.True);
        }

        [Test]
        public void MeshCoordinateAnalysisRecognizesDistinctFixedIdentityRoots()
        {
            var pair = MergePair(); pair.Second.rootBone = PolicyChild("Equivalent helper").transform;
            var candidates = MergeCandidates(new[] { pair.First, pair.Second });
            Assert.That(MeshMergeAnalysis.Group(candidates, true, MergePolicy()).Count, Is.EqualTo(1));
        }

        [TestCase("Descriptor", false)] [TestCase("Descriptor", true)]
        [TestCase("ContactSender", false)] [TestCase("ContactSender", true)]
        [TestCase("ContactReceiver", false)] [TestCase("ContactReceiver", true)]
        [TestCase("PhysBoneCollider", false)] [TestCase("PhysBoneCollider", true)]
        public void ReferencedSdkFramesDoNotInventScaleChangesButActualScaleWritersRemainProtected(string reader, bool animated)
        {
            var pair = MergePair(); var frame = PolicyChild("Referenced frame").transform;
            frame.localPosition = Vector3.up; pair.Second.rootBone = frame;
            var owner = PolicyChild("Frame consumer");
            if (reader == "ContactSender") owner.AddComponent<VRCContactSender>().rootTransform = frame;
            if (reader == "ContactReceiver") owner.AddComponent<VRCContactReceiver>().rootTransform = frame;
            if (reader == "PhysBoneCollider") owner.AddComponent<VRCPhysBoneCollider>().rootTransform = frame;
            if (reader == "Descriptor")
            {
                var descriptor = root.AddComponent<VRCAvatarDescriptor>();
                var eyes = descriptor.customEyeLookSettings; eyes.leftEye = frame; descriptor.customEyeLookSettings = eyes;
            }
            var clip = Clip();
            if (animated) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(frame.name, typeof(Transform), "m_LocalScale.x"), AnimationCurve.Linear(0, 1, 1, -1));
            var policy = MergePolicy(true, clip);
            Assert.That(policy.Mutations.Mutable, Does.Contain(frame), "Runtime transform interfaces remain retained.");
            Assert.That(policy.Mutations.MayChangeScale(frame), Is.EqualTo(animated));
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }), true, policy).Count,
                Is.EqualTo(animated ? 2 : 1), "Only actual independent scale writers prevent the opted-in coordinate merge.");
        }

        [TestCase("Toggle")] [TestCase("Shadow")] [TestCase("Layer")] [TestCase("StaticShadow")]
        [TestCase("AnimatedScale")] [TestCase("CollapsedRoot")]
        public void MeshCoordinatePolicyDoesNotOverrideIndependentRuntimeBehavior(string difference)
        {
            var pair = MergePair(); var other = PolicyChild("Other root").transform;
            other.localPosition = Vector3.one; pair.Second.rootBone = other; var clip = Clip();
            if (difference == "Toggle") pair.Second.gameObject.SetActive(false);
            if (difference == "Shadow") pair.Second.shadowCastingMode = ShadowCastingMode.Off;
            if (difference == "Layer") pair.Second.gameObject.layer = 2;
            if (difference == "StaticShadow") pair.Second.staticShadowCaster = !pair.First.staticShadowCaster;
            if (difference == "CollapsedRoot") other.localScale = Vector3.zero;
            if (difference == "AnimatedScale") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Other root", typeof(Transform), "m_LocalScale.x"), AnimationCurve.Linear(0, 1, 1, -1));
            var candidates = MergeCandidates(new[] { pair.First, pair.Second });
            Assert.That(MeshMergeAnalysis.Group(candidates, true, MergePolicy(true, clip)).Count, Is.EqualTo(2));
        }

        [Test]
        public void MeshCoordinatePolicySharesScaleChangesOnACommonAncestor()
        {
            var pair = MergePair(); pair.Second.rootBone = PolicyChild("Other root").transform;
            pair.Second.rootBone.localPosition = Vector3.right;
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalScale.x"), AnimationCurve.Linear(0, 1, 1, -1));
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }), true, MergePolicy(true, clip)).Count, Is.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void MeshCoordinateChangesAreGatedSeparatelyFromEquivalentOptimizations(bool enabledForCurrentPlatform)
        {
            MaterialDescriptor();
            var settings = root.AddComponent<ALTOTraceAndOptimizeMeshes>(); settings.AllowCoordinateSpaceChanges = true;
            settings.CoordinateSpaceChangesOnPlatforms = enabledForCurrentPlatform ? OptimizationPlatforms.All : OptimizationPlatforms.None;
            var context = new BuildContext(root, null); MeshOptimizationPass.Capture(context);
            var state = context.GetState<MeshOptimizationState>();
            Assert.That(state.AllowCoordinateSpaceChanges, Is.EqualTo(enabledForCurrentPlatform));
            Assert.That(state.MergeRenderers && state.ExpandBounds, Is.True);
        }

        [UnityTest] public IEnumerator MergedCoordinateRootsKeepPositiveCulling() => VerifyMergedCoordinateCulling(false);
        [UnityTest] public IEnumerator MergedCoordinateRootsKeepMirroredCulling() => VerifyMergedCoordinateCulling(true);
        [UnityTest] public IEnumerator MergedCoordinateRootsKeepWorldNormals() => VerifyMergedCoordinateCulling(false, 1);
        [UnityTest] public IEnumerator MergedCoordinateRootsKeepWorldTangents() => VerifyMergedCoordinateCulling(false, 2);
        [UnityTest] public IEnumerator MergedCoordinateRootsKeepWorldBitangents() => VerifyMergedCoordinateCulling(false, 3);
        [UnityTest] public IEnumerator MergedMirroredCoordinateRootsKeepWorldNormals() => VerifyMergedCoordinateCulling(true, 1);
        [UnityTest] public IEnumerator MergedMirroredCoordinateRootsKeepWorldTangents() => VerifyMergedCoordinateCulling(true, 2);
        [UnityTest] public IEnumerator MergedMirroredCoordinateRootsKeepWorldBitangents() => VerifyMergedCoordinateCulling(true, 3);
        [UnityTest] public IEnumerator MergedScaledRendererOwnersKeepVisiblePixels() => VerifyMergedCoordinateCulling(false, 0, true);
        [UnityTest] public IEnumerator MergedScaledOwnersAndMirroredRootsKeepVisiblePixels() => VerifyMergedCoordinateCulling(true, 0, true);

        private IEnumerator VerifyMergedCoordinateCulling(bool mirrored, int frameMode = 0, bool scaledOwners = false)
        {
            OcclusionGpuRequired(); var pair = MergePair(); var material = OcclusionMaterial();
            if (frameMode != 0)
            {
                material = Track(new Material(Shader.Find("Hidden/Noname/Assembly World Frame")));
                material.SetFloat("_Mode", frameMode);
            }
            pair.First.sharedMaterial = pair.Second.sharedMaterial = material;
            if (scaledOwners)
            {
                pair.First.transform.localScale = new Vector3(1.2f, 0.7f, 0.9f);
                pair.Second.transform.localScale = new Vector3(0.738f, 0.831f, 0.831f);
            }
            var other = PolicyChild("Other root").transform; other.localPosition = new Vector3(2, 1, 3);
            other.localRotation = Quaternion.Euler(10, 15, 20); other.localScale = new Vector3(mirrored ? -0.8f : 0.8f, 1.2f, 1.3f);
            pair.Second.rootBone = other;
            var candidates = MergeCandidates(new[] { pair.First, pair.Second });
            Assert.That(MeshMergeAnalysis.Group(candidates, true, MergePolicy(true)).Count, Is.EqualTo(1));
            // Build into fresh, unrendered inputs, as the NDMF build does. Replacing
            // the vertex layout of an already rendered preview renderer can leave
            // Unity's native skinning buffer stale across manual Camera.Render calls.
            var builtFirst = Track(UnityEngine.Object.Instantiate(pair.First.gameObject, root.transform, false)).GetComponent<SkinnedMeshRenderer>();
            var builtSecond = Track(UnityEngine.Object.Instantiate(pair.Second.gameObject, root.transform, false)).GetComponent<SkinnedMeshRenderer>();
            builtFirst.name = "Built first"; builtSecond.name = "Built second";
            var builtCandidates = MergeCandidates(new[] { builtFirst, builtSecond });
            MeshOptimizationPass.Merge(builtCandidates, "Built first", true, mesh => Track(mesh), expandBounds: true);
            builtFirst.enabled = false;
            var camera = PolicyChild("Camera").AddComponent<Camera>(); camera.scene = scene; camera.enabled = false;
            camera.transform.localPosition = new Vector3(0.5f, 0.5f, -4); camera.orthographic = true; camera.orthographicSize = 2;
            camera.renderingPath = RenderingPath.Forward; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            var texture = RenderTexture.GetTemporary(96, 96, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try
            {
                EditorApplication.QueuePlayerLoopUpdate();
                yield return null;
                var before = new List<Color32[]>();
                foreach (int direction in new[] { -1, 1 })
                {
                    camera.transform.localPosition = new Vector3(0.5f, 0.5f, direction * 4);
                    camera.transform.LookAt(new Vector3(0.5f, 0.5f, 0));
                    before.Add(DrawAssembly(camera, texture));
                }
                pair.First.enabled = false; pair.Second.enabled = false; builtFirst.enabled = true;
                EditorApplication.QueuePlayerLoopUpdate();
                yield return null;
                Assert.That(before.SelectMany(p => p).Any(p => p.r > 20), Is.True);
                int view = 0;
                foreach (int direction in new[] { -1, 1 })
                {
                    camera.transform.localPosition = new Vector3(0.5f, 0.5f, direction * 4);
                    camera.transform.LookAt(new Vector3(0.5f, 0.5f, 0));
                    Assert.That(DrawAssembly(camera, texture), Is.EqualTo(before[view++]),
                        "Changing the coordinate root must not change visible triangle faces.");
                }
            }
            finally { RenderTexture.ReleaseTemporary(texture); }
        }
    }
}
