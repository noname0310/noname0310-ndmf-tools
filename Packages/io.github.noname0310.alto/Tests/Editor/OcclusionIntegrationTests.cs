using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Occlusion;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private Material OcclusionMaterial() => Track(new Material(AssetDatabase.LoadAssetAtPath<Shader>(
            "Packages/io.github.noname0310.alto/Tests/Editor/OcclusionPolicy.shader")));

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void OcclusionMaterialPolicyResolvesPropertyCullingAndDefaultStencil(int cull)
        {
            var material = OcclusionMaterial(); material.SetFloat("_Cull", cull);
            Assert.That(new OcclusionMaterialPolicy().TryCulling(material, null, out var value), Is.True);
            Assert.That(value, Is.EqualTo((CullMode)cull));
        }

        [TestCase("Alpha")] [TestCase("Queue")] [TestCase("StencilTest")] [TestCase("StencilWrite")]
        [TestCase("DepthWrite")] [TestCase("ColorMask")] [TestCase("InvalidCull")] [TestCase("Blend")]
        public void AutomaticOccludersRejectAmbiguousOrRestrictedSurfaceState(string restriction)
        {
            var material = OcclusionMaterial();
            if (restriction == "Alpha") material.SetOverrideTag("RenderType", "TransparentCutout");
            if (restriction == "Queue") material.renderQueue = 3000;
            if (restriction == "StencilTest") material.SetFloat("_StencilComp", (float)CompareFunction.Equal);
            if (restriction == "StencilWrite") material.SetFloat("_StencilPass", (float)StencilOp.Replace);
            if (restriction == "DepthWrite") material.SetFloat("_ZWrite", 0);
            if (restriction == "ColorMask") material.SetFloat("_ColorMask", 0);
            if (restriction == "InvalidCull") material.SetFloat("_Cull", 0.5f);
            if (restriction == "Blend") material.SetFloat("_DestinationBlend", (float)BlendMode.One);
            var policy = new OcclusionMaterialPolicy();
            Assert.That(policy.TryCulling(material, null, out _), Is.False);
            if (restriction != "InvalidCull")
                Assert.That(policy.TryCulling(material, new OcclusionOccluder { Culling = OcclusionCulling.Back }, out _), Is.False,
                    "A culling override does not imply permission to treat restricted shaders as opaque.");
            Assert.That(policy.TryCulling(material, new OcclusionOccluder { AssumeOpaque = true, Culling = OcclusionCulling.Back }, out _), Is.True);
        }

        [Test]
        public void OccluderUsePassResolvesOnlyEnabledSurfacePassesAndTheirPropertyStates()
        {
            var material = Track(new Material(AssetDatabase.LoadAssetAtPath<Shader>(
                "Packages/io.github.noname0310.alto/Tests/Editor/OcclusionUsePass.shader")));
            var policy = new OcclusionMaterialPolicy();
            Assert.That(policy.TryCulling(material, null, out var culling), Is.True); Assert.That(culling, Is.EqualTo(CullMode.Back));
            material.SetFloat("_Cull", 0);
            Assert.That(policy.TryCulling(material, null, out culling), Is.True); Assert.That(culling, Is.EqualTo(CullMode.Off));
            material.SetShaderPassEnabled("SURFACE", false);
            Assert.That(policy.TryCulling(material, null, out _), Is.False, "A shadow-only pass is not a color occluder.");
            material.SetShaderPassEnabled("SURFACE", true); material.SetFloat("_StencilComp", (float)CompareFunction.Equal);
            Assert.That(policy.TryCulling(material, null, out _), Is.False);
        }

        private ALTORemoveOccludedMesh OcclusionFixture(out Renderer shell)
        {
            MaterialDescriptor();
            var mesh = Mesh("Mask", "Customize");
            mesh.vertices = new[] { new Vector3(-0.2f, -0.2f, 0), new Vector3(0.2f, -0.2f, 0), new Vector3(0, 0.2f, 0) };
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
            renderer.bones = new[] { renderer.transform };
            renderer.sharedMaterial = OcclusionMaterial();
            var cube = Track(GameObject.CreatePrimitive(PrimitiveType.Cube)); cube.name = "Shell";
            cube.transform.SetParent(root.transform, false);
            Object.DestroyImmediate(cube.GetComponent<Collider>());
            shell = cube.GetComponent<MeshRenderer>(); shell.sharedMaterial = OcclusionMaterial();
            var settings = root.AddComponent<ALTORemoveOccludedMesh>();
            settings.ApplyOnPlatforms = OptimizationPlatforms.All;
            settings.Targets.Add(new OcclusionTarget { Renderer = renderer, BlendShapes = new() { "Mask" } });
            settings.ViewCount = 24; settings.RenderResolution = 64; settings.BoundaryMarginPixels = 1;
            return settings;
        }

        [TestCase("Normal")] [TestCase("Nonuniform")] [TestCase("Collapsed")]
        public void OcclusionPoseCopiesRespectWorldTransformsAndOnlyResetMaskShapes(string scale)
        {
            var settings = OcclusionFixture(out _);
            root.transform.SetPositionAndRotation(new Vector3(3, 4, 5), Quaternion.Euler(13, 31, 19));
            root.transform.localScale = scale == "Nonuniform" ? new Vector3(0.7f, 1.3f, 0.9f) : scale == "Collapsed" ? Vector3.zero : Vector3.one;
            renderer.SetBlendShapeWeight(0, 85); renderer.SetBlendShapeWeight(1, 23);
            var before = EditorJsonUtility.ToJson(renderer); int scenes = EditorSceneManager.previewSceneCount;
            using (var snapshot = new OcclusionPoseSnapshot(root, settings.ReferencePose))
            {
                var output = snapshot.WorldMesh(renderer, new[] { "Mask" });
                var expected = renderer.sharedMesh.vertices.Select(v => renderer.transform.TransformPoint(v + Vector3.right * 0.46f)).ToArray();
                for (int i = 0; i < expected.Length; i++) Assert.That(Vector3.Distance(output.vertices[i], expected[i]), Is.LessThan(0.000005f));
            }
            Assert.That(EditorJsonUtility.ToJson(renderer), Is.EqualTo(before));
            Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
        }

        [Test]
        public void OcclusionPoseSamplingRemapsPathsAndNeverEditsSourceMaterialOrTransforms()
        {
            var settings = OcclusionFixture(out var shell);
            var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("OldShell", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Constant(0, 1, 3));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("OldShell", typeof(MeshRenderer), "material._Color.r"), AnimationCurve.Constant(0, 1, 0.2f));
            var material = shell.sharedMaterial; var before = EditorJsonUtility.ToJson(material);
            var paths = new Dictionary<string, string> { ["OldShell"] = "Shell" };
            using (var snapshot = new OcclusionPoseSnapshot(root, new OcclusionPose { Clip = clip }, paths))
            {
                Assert.That(snapshot.Copy(shell).transform.localPosition.x, Is.EqualTo(3));
                Assert.That(snapshot.Copy(shell).sharedMaterial, Is.Not.SameAs(material));
            }
            Assert.That(shell.transform.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(EditorJsonUtility.ToJson(material), Is.EqualTo(before));
            Assert.That(AnimationUtility.GetCurveBindings(clip).All(b => b.path == "OldShell"), Is.True);
        }

        [Test]
        public void OcclusionPoseFailuresCleanUpTheirPreviewScene()
        {
            OcclusionFixture(out _); int scenes = EditorSceneManager.previewSceneCount; var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Missing", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Constant(0, 1, 3));
            Assert.Throws<InvalidOperationException>(() => new OcclusionPoseSnapshot(root, new OcclusionPose { Clip = clip }));
            Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(scenes));
        }

        [Test]
        public void OcclusionCoordinatorRequiresAllPosesAndPreservesSourceAssets()
        {
            OcclusionGpuRequired(); var settings = OcclusionFixture(out var shell);
            var scope = OptimizationBuildScope.Capture(settings, root); var mesh = renderer.sharedMesh;
            var hidden = OcclusionEvaluation.Run(root, settings, scope);
            Assert.That(hidden.Targets.Single().RemovedCount, Is.EqualTo(1));
            var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Shell", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Constant(0, 1, 3));
            settings.AdditionalPoses.Add(new OcclusionPose { Clip = clip });
            var visible = OcclusionEvaluation.Run(root, settings, scope);
            Assert.That(visible.Targets.Single().RemovedCount, Is.Zero);
            Assert.That(mesh.triangles, Is.EqualTo(new[] { 0, 1, 2 })); Assert.That(renderer.sharedMesh, Is.SameAs(mesh));
            Assert.That(shell.transform.localPosition, Is.EqualTo(Vector3.zero));
        }

        [TestCase("Excluded")] [TestCase("NotSelected")] [TestCase("Inactive")] [TestCase("Disabled")]
        [TestCase("AnimatedToggle")] [TestCase("AnimatedMaterial")] [TestCase("Protected")]
        [TestCase("ShadowsOnly")] [TestCase("ForceOff")] [TestCase("LOD")]
        [TestCase("PropertyBlock")]
        public void OcclusionCoordinatorRetainsRegionsWithoutAStableSelectedOccluder(string mode)
        {
            OcclusionGpuRequired(); var settings = OcclusionFixture(out var shell);
            var clip = Clip();
            if (mode == "Excluded") settings.ExcludedOccluders.Add(shell);
            if (mode == "NotSelected") settings.OccluderSelection = RendererSelectionMode.SelectedRenderers;
            if (mode == "Inactive") shell.gameObject.SetActive(false);
            if (mode == "Disabled") shell.enabled = false;
            if (mode == "ShadowsOnly") shell.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            if (mode == "ForceOff") shell.forceRenderingOff = true;
            if (mode == "PropertyBlock")
            {
                var block = new MaterialPropertyBlock(); block.SetFloat("_StencilComp", (float)CompareFunction.Equal); shell.SetPropertyBlock(block);
            }
            if (mode == "LOD") shell.gameObject.AddComponent<LODGroup>().SetLODs(new[] { new LOD(0.5f, new[] { shell }), new LOD(0.01f, Array.Empty<Renderer>()) });
            if (mode == "AnimatedToggle") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Shell", typeof(GameObject), "m_IsActive"), AnimationCurve.Linear(0, 0, 1, 1));
            if (mode == "AnimatedMaterial") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Shell", typeof(MeshRenderer), "material._Color.a"), AnimationCurve.Linear(0, 0, 1, 1));
            var controller = Usage(clip).controller;
            var index = AvatarAnimationIndex.Create(root, new Dictionary<object, nadena.dev.ndmf.animator.VirtualAnimatorController> { [new object()] = controller }, null);
            var result = OcclusionEvaluation.Run(root, settings, OptimizationBuildScope.Capture(settings, root), index, r => mode == "Protected" && r == renderer);
            Assert.That(result.Targets.Sum(t => t.RemovedCount), Is.Zero);
        }

        [Test]
        public void OcclusionBuildPassProducesASeparateMeshAndRemovesItsSettings()
        {
            OcclusionGpuRequired(); OcclusionFixture(out _); var source = renderer.sharedMesh;
            var context = new BuildContext(root, null);
            using var registry = new ObjectRegistryScope(context.ObjectRegistry);
            var sourceReference = ObjectRegistry.GetReference(source);
            OcclusionPass.Capture(context); OcclusionPass.Apply(context);
            var output = Track(renderer.sharedMesh);
            Assert.That(output, Is.Not.SameAs(source)); Assert.That(output.triangles, Is.Empty);
            Assert.That(ObjectRegistry.GetReference(output), Is.SameAs(sourceReference));
            Assert.That(source.triangles, Is.EqualTo(new[] { 0, 1, 2 }));
            Assert.That(root.GetComponent<ALTORemoveOccludedMesh>(), Is.Null);
        }

        [TestCase("ShaderValue", true)] [TestCase("PropertyBlock", true)]
        [TestCase("Active", false)] [TestCase("Enabled", false)] [TestCase("MaterialSwap", false)]
        public void ExplicitOpaqueGeometryPolicyOnlyOverridesNumericShaderState(string mode, bool removed)
        {
            OcclusionGpuRequired(); var settings = OcclusionFixture(out var shell);
            settings.Occluders.Add(new OcclusionOccluder { Renderer = shell, AssumeOpaque = true, Culling = OcclusionCulling.Back });
            var clip = Clip();
            if (mode == "ShaderValue") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Shell", typeof(MeshRenderer), "material._Color.a"), AnimationCurve.Linear(0, 0, 1, 1));
            if (mode == "PropertyBlock")
            {
                var block = new MaterialPropertyBlock(); block.SetFloat("_Cull", 0); shell.SetPropertyBlock(block);
            }
            if (mode == "Active") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Shell", typeof(GameObject), "m_IsActive"), AnimationCurve.Linear(0, 0, 1, 1));
            if (mode == "Enabled") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Shell", typeof(MeshRenderer), "m_Enabled"), AnimationCurve.Linear(0, 0, 1, 1));
            if (mode == "MaterialSwap") AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("Shell", typeof(MeshRenderer), "m_Materials.Array.data[0]"), new[] { new ObjectReferenceKeyframe { time = 0, value = null } });
            var index = AvatarAnimationIndex.Create(root,
                new Dictionary<object, nadena.dev.ndmf.animator.VirtualAnimatorController> { [new object()] = Usage(clip).controller }, null);
            var result = OcclusionEvaluation.Run(root, settings, OptimizationBuildScope.Capture(settings, root), index);
            Assert.That(result.Targets.Single().RemovedCount, Is.EqualTo(removed ? 1 : 0));
        }

        [UnityTest] public IEnumerator OcclusionRigidProxyKeepsMirroredCulling() => OcclusionProxyCulling(false, true);
        [UnityTest] public IEnumerator OcclusionSkinProxyKeepsMirroredCulling() => OcclusionProxyCulling(true, true);
        [UnityTest] public IEnumerator OcclusionRigidProxyKeepsPositiveCulling() => OcclusionProxyCulling(false, false);
        [UnityTest] public IEnumerator OcclusionSkinProxyKeepsPositiveCulling() => OcclusionProxyCulling(true, false);
        [UnityTest] public IEnumerator OcclusionSkinProxyKeepsMirroredRootCulling() => OcclusionProxyCulling(true, false, 1);
        [UnityTest] public IEnumerator OcclusionSkinProxyKeepsDetachedBoneCulling() => OcclusionProxyCulling(true, false, 2);
        [UnityTest] public IEnumerator OcclusionSkinProxyKeepsPositiveRootWithMirroredOwnerCulling() => OcclusionProxyCulling(true, true, 3);
        [UnityTest] public IEnumerator OcclusionSkinProxyKeepsMirroredRootAndOwnerCulling() => OcclusionProxyCulling(true, true, 1);

        private IEnumerator OcclusionProxyCulling(bool skinned, bool mirrored, int rootConfiguration = 0)
        {
            OcclusionGpuRequired();
            var mesh = Mesh(); renderer.sharedMesh = null;
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
            var sourceObject = PolicyChild("Surface");
            sourceObject.transform.localScale = new Vector3(mirrored ? -0.8f : 0.8f, 0.7f, 1.1f);
            Renderer surface;
            if (skinned)
            {
                var skin = sourceObject.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh;
                skin.bones = new[] { skin.transform }; surface = skin;
                if (rootConfiguration != 0)
                {
                    var bone = PolicyChild("Root bone").transform;
                    bone.localScale = new Vector3(rootConfiguration == 3 ? 1 : -1, 1, 1);
                    skin.bones = new[] { bone }; skin.rootBone = rootConfiguration == 2 ? null : bone;
                }
            }
            else
            {
                sourceObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                surface = sourceObject.AddComponent<MeshRenderer>();
            }
            surface.sharedMaterial = OcclusionMaterial();
            var proxyObject = PolicyChild("World proxy");
            var proxy = proxyObject.AddComponent<MeshRenderer>(); proxy.sharedMaterial = surface.sharedMaterial; proxy.enabled = false;
            using (var snapshot = new OcclusionPoseSnapshot(root, new OcclusionPose()))
                proxyObject.AddComponent<MeshFilter>().sharedMesh = Track(Object.Instantiate(snapshot.WorldMesh(surface)));
            var camera = PolicyChild("Camera").AddComponent<Camera>(); camera.scene = scene; camera.enabled = false;
            camera.renderingPath = RenderingPath.Forward; camera.orthographic = true; camera.orthographicSize = 1.3f;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            var texture = RenderTexture.GetTemporary(96, 96, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            int visiblePixels = 0;
            try
            {
                foreach (int direction in new[] { -1, 1 })
                {
                    camera.transform.position = new Vector3(0, 0.3f, direction * 4);
                    camera.transform.LookAt(new Vector3(0, 0.3f, 0));
                    surface.enabled = true; proxy.enabled = false; yield return null;
                    var before = DrawAssembly(camera, texture);
                    surface.enabled = false; proxy.enabled = true; yield return null;
                    var after = DrawAssembly(camera, texture);
                    visiblePixels += before.Count(p => p.r > 20);
                    Assert.That(after, Is.EqualTo(before), "World-space proxy winding must match native renderer culling. " +
                        "Direction " + direction + "; lit pixels " + before.Count(p => p.r > 20) + " / " + after.Count(p => p.r > 20) +
                        "; renderer matrix determinant " + surface.localToWorldMatrix.determinant +
                        "; transform determinant " + surface.transform.localToWorldMatrix.determinant);
                }
                Assert.That(visiblePixels, Is.GreaterThan(0));
            }
            finally { RenderTexture.ReleaseTemporary(texture); }
        }
    }
}
