using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private (SkinnedMeshRenderer First, SkinnedMeshRenderer Second) MergePair()
        {
            var bone = PolicyChild("Bone").transform;
            var first = AssemblyPart("First", new[] { bone }, Vector3.left);
            var second = AssemblyPart("Second", new[] { bone }, Vector3.right);
            first.lightProbeUsage = second.lightProbeUsage = LightProbeUsage.Off;
            first.reflectionProbeUsage = second.reflectionProbeUsage = ReflectionProbeUsage.Off;
            second.sharedMaterials = first.sharedMaterials;
            return (first, second);
        }

        private MeshMergeCandidate[] MergeCandidates(SkinnedMeshRenderer[] parts, VirtualAnimatorController controller = null,
            bool assertEligible = true, Dictionary<object, VirtualAnimatorController> entries = null, IEnumerable<string> preservedParameters = null)
        {
            controller ??= new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip()));
            var index = AvatarAnimationIndex.Create(root, entries ?? new Dictionary<object, VirtualAnimatorController>
            { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, null, preservedParameters);
            var references = AvatarDependencyGraph.Create(root, index, new ObjectOptimizationState
            { Scope = new OptimizationBuildScope { Root = root.transform } }, false);
            var shaders = new ShaderPropertyUsage();
            return parts.Select(part =>
            {
                var candidate = MeshMergeAnalysis.Inspect(part, root, index, references, shaders, out string reason);
                if (assertEligible) Assert.That(reason, Is.Null, part.name + ": " + reason);
                return candidate;
            }).ToArray();
        }

        [Test]
        public void MeshMergePreservesDescriptorMorphIndicesAndRetargetsDistinctAnimatedShapes()
        {
            var pair = MergePair();
            foreach (var part in new[] { pair.First, pair.Second }) Frame(part.sharedMesh, "Close", 100, Vector3.up * 0.1f);
            var descriptor = root.AddComponent<VRCAvatarDescriptor>(); descriptor.VisemeSkinnedMesh = pair.First;
            descriptor.VisemeBlendShapes = new[] { "Close" };
            var eyelids = descriptor.customEyeLookSettings; eyelids.eyelidsSkinnedMesh = pair.First; eyelids.eyelidsBlendshapes = new[] { 0 };
            descriptor.customEyeLookSettings = eyelids;
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, Binding("Close", "First"), AnimationCurve.Linear(0, -20, 2, 120));
            AnimationUtility.SetEditorCurve(clip, Binding("Close", "Second"), AnimationCurve.Linear(0, 100, 2, 0));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var candidates = MergeCandidates(new[] { pair.Second, pair.First }, controller);
            var groups = MeshMergeAnalysis.Group(candidates, true); Assert.That(groups.Count, Is.EqualTo(1));
            Assert.That(groups[0][0].Renderer, Is.SameAs(pair.First));
            var sourceFirst = pair.First.sharedMesh; var sourceSecond = pair.Second.sharedMesh;
            var before = AssemblyWorldVertices(pair.First).Concat(AssemblyWorldVertices(pair.Second)).ToArray();
            MeshOptimizationPass.Merge(groups[0], "First", true, mesh => Track(mesh));
            Assert.That(pair.Second == null, Is.True); Assert.That(root.transform.Find("Second"), Is.Not.Null);
            Assert.That(descriptor.VisemeSkinnedMesh, Is.SameAs(pair.First));
            Assert.That(descriptor.customEyeLookSettings.eyelidsBlendshapes, Is.EqualTo(new[] { 0 }));
            Assert.That(pair.First.sharedMesh.GetBlendShapeName(0), Is.EqualTo("Close"));
            Assert.That(pair.First.sharedMesh.blendShapeCount, Is.EqualTo(2));
            Assert.That(pair.First.localBounds, Is.EqualTo(groups[0][0].Bounds));
            var output = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(MotionAnalysis.EndTime(output), Is.EqualTo(2));
            Assert.That(output.GetFloatCurveBindings().Select(b => b.path), Is.All.EqualTo("First"));
            Assert.That(output.GetFloatCurve(Binding("__AdvancedMerged_1_Close", "First")).Evaluate(1), Is.EqualTo(50).Within(0.0001f));
            Assert.That(sourceFirst.blendShapeCount, Is.EqualTo(1)); Assert.That(sourceSecond.blendShapeCount, Is.EqualTo(1));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Any(b => b.path == "Second"), Is.True);
            var after = AssemblyWorldVertices(pair.First);
            for (int i = 0; i < before.Length; i++) Assert.That(Vector3.Distance(before[i], after[i]), Is.LessThan(0.00001f));
        }

        [TestCase("RootBone")] [TestCase("Bounds")] [TestCase("Shadow")] [TestCase("Probe")]
        [TestCase("Layer")] [TestCase("Quality")] [TestCase("Inactive")]
        public void MeshMergeSeparatesDifferentRenderingAndVisibilitySettings(string property)
        {
            var pair = MergePair();
            switch (property)
            {
                case "RootBone": pair.Second.rootBone = pair.Second.bones[0]; break;
                case "Bounds": pair.Second.localBounds = new Bounds(Vector3.one, Vector3.one); break;
                case "Shadow": pair.Second.shadowCastingMode = ShadowCastingMode.Off; break;
                case "Probe":
                    pair.First.lightProbeUsage = pair.Second.lightProbeUsage = LightProbeUsage.BlendProbes;
                    pair.First.probeAnchor = pair.First.transform; pair.Second.probeAnchor = pair.Second.bones[0];
                    break;
                case "Layer": pair.Second.gameObject.layer = 2; break;
                case "Quality": pair.Second.quality = SkinQuality.Bone1; break;
                case "Inactive": pair.Second.gameObject.SetActive(false); break;
            }
            var candidates = MergeCandidates(new[] { pair.First, pair.Second });
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(2));
        }

        [TestCase(false)] [TestCase(true)]
        public void MeshMergeMatchesWholeVisibilityCurvesAndTheirInitialValues(bool different)
        {
            var pair = MergePair(); var clip = Clip();
            foreach (var part in new[] { pair.First, pair.Second })
            {
                var binding = EditorCurveBinding.DiscreteCurve(part.name, typeof(GameObject), "m_IsActive");
                AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, different && part == pair.Second ? 1 : 0));
            }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(different ? 2 : 1));
        }

        [TestCase("PropertyBlock")] [TestCase("Reader")] [TestCase("PPtr")] [TestCase("RelativeController")] [TestCase("DynamicBounds")]
        public void MeshMergeRetainsUnverifiedRendererConsumers(string reason)
        {
            var pair = MergePair(); var clip = Clip(); Dictionary<object, VirtualAnimatorController> entries = null;
            if (reason == "DynamicBounds") pair.Second.updateWhenOffscreen = true;
            if (reason == "PropertyBlock")
            {
                var block = new MaterialPropertyBlock(); block.SetFloat("_Any", 1); pair.Second.SetPropertyBlock(block);
            }
            if (reason == "Reader") PolicyChild("Reader").AddComponent<DependencyAuthoringFixture>().Target = pair.Second;
            if (reason == "PPtr") AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("Second", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { value = pair.Second.sharedMaterial } });
            if (reason == "RelativeController")
            {
                Frame(pair.Second.sharedMesh, "Shape", 100, Vector3.up);
                AnimationUtility.SetEditorCurve(clip, Binding("Shape"), AnimationCurve.Constant(0, 1, 100));
            }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            if (reason == "RelativeController") entries = new() { [pair.Second.gameObject.AddComponent<Animator>()] = controller };
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller, false, entries);
            Assert.That(candidates[1], Is.Null, reason);
        }

        [Test]
        public void MeshMergeDoesNotCombineTwoIndependentDescriptorInterfaces()
        {
            var pair = MergePair(); var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            Frame(pair.Second.sharedMesh, "Close", 100, Vector3.up);
            descriptor.VisemeSkinnedMesh = pair.First;
            var eyes = descriptor.customEyeLookSettings; eyes.eyelidsSkinnedMesh = pair.Second; descriptor.customEyeLookSettings = eyes;
            var candidates = MergeCandidates(new[] { pair.First, pair.Second });
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(2));
        }

        [TestCase(false)] [TestCase(true)]
        public void MeshMergePreservesTransformMaskMembership(bool equal)
        {
            var pair = MergePair(); var clip = Clip();
            foreach (var part in new[] { pair.First, pair.Second }) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(part.name, typeof(SkinnedMeshRenderer), "m_Enabled"), AnimationCurve.Constant(0, 1, 1));
            var controllerAsset = Controller(clip);
            var mask = Track(new AvatarMask()); mask.transformCount = 2;
            mask.SetTransformPath(0, "First"); mask.SetTransformActive(0, true);
            mask.SetTransformPath(1, "Second"); mask.SetTransformActive(1, equal);
            var layers = controllerAsset.layers; layers[0].avatarMask = mask; controllerAsset.layers = layers;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(controllerAsset);
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(equal ? 1 : 2));
        }

        [Test]
        public void MeshMergeRejectsDifferentMaterialDefaultsEvenWhenCurvesMatch()
        {
            var pair = MergePair(); pair.Second.sharedMaterial = Track(new Material(pair.First.sharedMaterial));
            var clip = Clip();
            foreach (var part in new[] { pair.First, pair.Second }) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(part.name, typeof(SkinnedMeshRenderer), "material._Color.r"), AnimationCurve.Constant(0, 1, 0.5f));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(2));
        }

        [Test]
        public void MeshMergeDoesNotActivatePreviouslyMissingMorphTracks()
        {
            var pair = MergePair(); Frame(pair.Second.sharedMesh, "Close", 100, Vector3.up);
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, Binding("__AdvancedMerged_1_Close", "First"), AnimationCurve.Constant(0, 1, 100));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(candidates.All(c => !c.Pinned), Is.True);
            var group = MeshMergeAnalysis.Group(candidates, true).Single();
            MeshOptimizationPass.Merge(group, "First", true, mesh => Track(mesh));
            Assert.That(pair.First.sharedMesh.GetBlendShapeIndex("__AdvancedMerged_1_Close"), Is.EqualTo(-1));
            Assert.That(pair.First.sharedMesh.GetBlendShapeName(0), Is.EqualTo("___AdvancedMerged_1_Close"));
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurve(Binding("__AdvancedMerged_1_Close", "First")), Is.Not.Null);
        }

        [Test]
        public void MaterialSlotCoalescingDoesNotReorderInterveningMaterials()
        {
            var pair = MergePair(); var material = pair.First.sharedMaterial;
            var other = Track(new Material(material));
            pair.First.sharedMesh.subMeshCount = 3;
            pair.First.sharedMesh.SetTriangles(new[] { 0, 1, 2 }, 1); pair.First.sharedMesh.SetTriangles(new[] { 0, 1, 2 }, 2);
            pair.First.sharedMaterials = new[] { material, other, material };
            using var assembly = SkinnedMeshAssembler.Create(new[] { pair.First, pair.Second }, true);
            Assert.That(assembly.Materials, Is.EqualTo(new[] { material, other, material }));
            Assert.That(assembly.MaterialSlots[pair.Second], Is.EqualTo(new[] { 2 }));
            Assert.That(assembly.Mesh.GetIndexCount(2), Is.EqualTo(6));
        }

        [Test]
        public void MeshMergeDoesNotActivateAnInvalidDescriptorIndex()
        {
            var pair = MergePair(); var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            var eyes = descriptor.customEyeLookSettings; eyes.eyelidsSkinnedMesh = pair.First; eyes.eyelidsBlendshapes = new[] { 0 };
            descriptor.customEyeLookSettings = eyes; Frame(pair.Second.sharedMesh, "Shape", 100, Vector3.up);
            Assert.That(MergeCandidates(new[] { pair.First }, assertEligible: false)[0], Is.Null);
        }

        [Test]
        public void MeshMergeDoesNotActivateMissingDescriptorVisemes()
        {
            var pair = MergePair(); var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.VisemeSkinnedMesh = pair.First; descriptor.VisemeBlendShapes = new[] { "__AdvancedMerged_1_Close" };
            Frame(pair.Second.sharedMesh, "Close", 100, Vector3.up);
            var group = MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }), true).Single();
            MeshOptimizationPass.Merge(group, "First", true, mesh => Track(mesh));
            Assert.That(pair.First.sharedMesh.GetBlendShapeIndex(descriptor.VisemeBlendShapes[0]), Is.EqualTo(-1));
        }

        [TestCase("Indices")] [TestCase("DisableBatching")]
        public void MeshDataOptimizationRespectsShaderBatchingConstraints(string constraint)
        {
            var pair = MergePair();
            if (constraint == "Indices") pair.First.sharedMaterial = Track(new Material(Shader.Find("Hidden/Noname/Mesh Index Consumer")));
            else pair.First.sharedMaterial.SetOverrideTag("DisableBatching", "True");
            Assert.That(MergeCandidates(new[] { pair.First }, assertEligible: false)[0], Is.Null);
        }

        [TestCase(false)] [TestCase(true)]
        public void UnusedMeshDataBuildPassWorksWithoutRendererOrSlotMerging(bool enabled)
        {
            var pair = MergePair(); var source = pair.First.sharedMesh; var outside = pair.Second.sharedMesh;
            var orphan = PolicyChild("Unused palette entry").transform;
            pair.First.bones = pair.First.bones.Append(orphan).ToArray();
            source.bindposes = source.bindposes.Append(Matrix4x4.identity).ToArray();
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.baseAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            var settings = root.AddComponent<ALTOTraceAndOptimizeMeshes>(); settings.ScopeRoot = pair.First.transform;
            settings.MergeCompatibleSkinnedMeshes = false; settings.MergeIdenticalMaterialSlots = false; settings.RemoveUnusedMeshData = enabled;
            var context = new BuildContext(root, null);
            MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            if (pair.First.sharedMesh != source) Track(pair.First.sharedMesh);
            Assert.That(pair.First.sharedMesh == source, Is.EqualTo(!enabled));
            Assert.That(pair.First.bones.Length, Is.EqualTo(enabled ? 1 : 2));
            Assert.That(source.bindposes.Length, Is.EqualTo(2));
            Assert.That(pair.Second.sharedMesh, Is.SameAs(outside));
            Assert.That(pair.First != null && pair.Second != null, Is.True);
            Assert.That(root.GetComponent<ALTOTraceAndOptimizeMeshes>() == null, Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void MeshFinalizationCollectsEmptySourcesButRetainsLiveSkinBones(bool usedAsBone)
        {
            var pair = MergePair(); var sourceObject = pair.Second.gameObject;
            if (usedAsBone) { pair.Second.bones = new[] { pair.Second.transform }; pair.Second.sharedMesh.bindposes = new[] { Matrix4x4.identity }; }
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.baseAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            root.AddComponent<ALTOTraceAndOptimizeMeshes>().RebindStaticBones = false;
            var objects = root.AddComponent<ALTOTraceAndOptimizeObjects>(); objects.RemoveUnusedObjects = true;
            var context = new BuildContext(root, null);
            ObjectOptimizationPass.Capture(context); MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context);
            MeshOptimizationPass.Apply(context); Track(pair.First.sharedMesh);
            Assert.That(sourceObject != null && pair.Second == null, Is.True);
            OptimizationPipeline.AfterMeshes(context);
            Assert.That(sourceObject != null, Is.EqualTo(usedAsBone));
            Assert.That(pair.First != null && pair.First.sharedMesh.vertexCount == 6, Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void MeshMergeRetainsExternallyMergedProtectedInputs(bool outsideScope)
        {
            var pair = MergePair(); var state = new MeshOptimizationState { Component = root.AddComponent<ALTOTraceAndOptimizeMeshes>() };
            state.Inputs.Add((pair.First, true, false)); state.Inputs.Add((pair.Second, !outsideScope, !outsideScope));
            state.Component.BuildInputRenderers = new Renderer[] { pair.First, pair.First };
            UnityEngine.Object.DestroyImmediate(pair.Second);
            Assert.That(MeshOptimizationPass.InputProtected(state, pair.First, out string reason), Is.True);
            Assert.That(reason, Does.Contain(outsideScope ? "out-of-scope" : "preserved"));
        }

        [UnityTest]
        public IEnumerator MeshMergePreservesForwardLightingForSeparateSurfaces() => VerifyForwardLighting(false);

        [UnityTest]
        public IEnumerator MeshMergePreservesForwardLightingForOverlappingSurfaces() => VerifyForwardLighting(true);

        private IEnumerator VerifyForwardLighting(bool overlap)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("This comparison requires a graphics device.");
            var shader = Shader.Find("VRChat/Mobile/Toon Standard");
            if (shader == null) Assert.Ignore("This SDK shader fixture is not installed.");
            var pair = MergePair(); var material = Track(new Material(shader)); material.SetFloat("_Culling", 0);
            pair.First.sharedMaterial = pair.Second.sharedMaterial = material;
            if (overlap) pair.Second.sharedMesh.bindposes = pair.First.sharedMesh.bindposes;
            var candidates = MergeCandidates(new[] { pair.First, pair.Second });
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(1));
            // Compare original inputs with a fresh build result. Hot-swapping an
            // already rendered SMR can leave Unity's native skinning layout stale.
            var builtFirst = Track(UnityEngine.Object.Instantiate(pair.First.gameObject, root.transform, false)).GetComponent<SkinnedMeshRenderer>();
            var builtSecond = Track(UnityEngine.Object.Instantiate(pair.Second.gameObject, root.transform, false)).GetComponent<SkinnedMeshRenderer>();
            builtFirst.name = "Built first"; builtSecond.name = "Built second";
            MeshOptimizationPass.Merge(MergeCandidates(new[] { builtFirst, builtSecond }), "Built first", true, mesh => Track(mesh));
            builtFirst.enabled = false;
            var camera = PolicyChild("Camera").AddComponent<Camera>(); camera.scene = scene; camera.enabled = false;
            camera.transform.localPosition = new Vector3(0.5f, 0.5f, -4); camera.orthographic = true; camera.orthographicSize = 2;
            camera.renderingPath = RenderingPath.Forward; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            var directional = PolicyChild("Sun").AddComponent<Light>(); directional.type = LightType.Directional;
            directional.transform.localRotation = Quaternion.Euler(25, 15, 0); directional.intensity = 0.5f;
            for (int i = 0; i < 2; i++)
            {
                var point = PolicyChild("Point " + i).AddComponent<Light>(); point.type = LightType.Point; point.renderMode = LightRenderMode.ForcePixel;
                point.transform.localPosition = new Vector3(i == 0 ? -1 : 1, 0.5f, -1); point.range = 10; point.intensity = 2;
                point.color = i == 0 ? Color.red : Color.blue;
            }
            var target = RenderTexture.GetTemporary(96, 96, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try
            {
                var before = DrawAssembly(camera, target);
                pair.First.enabled = false; pair.Second.enabled = false; builtFirst.enabled = true;
                yield return null;
                var after = DrawAssembly(camera, target);
                Assert.That(before.Any(p => p.r > 20), Is.True);
                int difference = before.Zip(after, (a, b) => Math.Max(Math.Max(Math.Abs(a.r - b.r), Math.Abs(a.g - b.g)), Math.Abs(a.b - b.b))).Max();
                if (difference > 1)
                {
                    System.IO.Directory.CreateDirectory("Library/ALTO");
                    foreach (var sample in new[] { ("before", before), ("after", after) })
                    {
                        var image = new Texture2D(96, 96, TextureFormat.RGBA32, false, true);
                        try { image.SetPixels32(sample.Item2); image.Apply(); System.IO.File.WriteAllBytes("Library/ALTO/lighting-" + overlap + "-" + sample.Item1 + ".png", image.EncodeToPNG()); }
                        finally { UnityEngine.Object.DestroyImmediate(image); }
                    }
                    TestContext.WriteLine("Visible before/after " + before.Count(p => p.r > 20) + "/" + after.Count(p => p.r > 20) + "; bounds=" + pair.First.localBounds);
                }
                Assert.That(difference, Is.LessThanOrEqualTo(1), "Forward lighting or pass ordering changed.");
            }
            finally { RenderTexture.ReleaseTemporary(target); }
        }
    }
}
