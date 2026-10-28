using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private AvatarAnimationIndex RigidAnimations(AnimationClip clip = null)
        {
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip ?? Clip()));
            return AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController> { [root] = controller }, null);
        }

        private MeshRenderer RigidPart()
        {
            var obj = PolicyChild("Rigid"); var filter = obj.AddComponent<MeshFilter>();
            filter.sharedMesh = Mesh("Ignored Morph"); renderer.sharedMesh = null;
            var part = obj.AddComponent<MeshRenderer>();
            part.sharedMaterial = Track(new Material(Shader.Find("Hidden/Noname/Assembly Object Space")));
            return part;
        }

        [UnityTest] public IEnumerator RigidConversionRequiresAnExplicitObjectSpacePolicy() => RigidGpu(false);
        [UnityTest] public IEnumerator MirroredRigidConversionRequiresAnExplicitObjectSpacePolicy() => RigidGpu(true);

        private IEnumerator RigidGpu(bool mirrored)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("A graphics device is required.");
            var part = RigidPart(); var obj = part.gameObject; var original = obj.GetComponent<MeshFilter>().sharedMesh;
            part.sharedMaterial = Track(new Material(Shader.Find("Hidden/Noname/Rigid Object Space")));
            obj.transform.localPosition = new Vector3(-0.25f, 0.2f, 0);
            obj.transform.localRotation = Quaternion.Euler(10, -15, 18);
            obj.transform.localScale = new Vector3(mirrored ? -0.9f : 0.9f, 0.7f, 1.2f);
            var bounds = part.bounds;
            var worldVertices = original.vertices.Select(obj.transform.TransformPoint).ToArray();
            var camera = PolicyChild("Camera").AddComponent<Camera>(); camera.scene = scene; camera.enabled = false;
            camera.transform.localPosition = new Vector3(0, 0.5f, -4); camera.orthographic = true; camera.orthographicSize = 1.4f;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            var target = RenderTexture.GetTemporary(96, 96, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try
            {
                yield return null;
                var before = DrawAssembly(camera, target);
                var converted = RigidMeshConversion.Convert(part, RigidAnimations(), mesh => Track(mesh));
                yield return null;
                var after = DrawAssembly(camera, target);
                Assert.That(before.Any(p => p.r > 20), Is.True, "Fixture must draw visible geometry.");
                int difference = before.Zip(after, (a, b) => Math.Max(Math.Max(Math.Abs(a.r - b.r), Math.Abs(a.g - b.g)), Math.Abs(a.b - b.b))).Max();
                // This is a regression counterexample, not a successful automatic
                // conversion proof. Even DisableBatching does not preserve object space.
                Assert.That(difference, Is.GreaterThan(1), "Unity's skinned object-space convention must be treated as a policy change.");
                var convertedWorld = AssemblyWorldVertices(converted);
                for (int vertex = 0; vertex < worldVertices.Length; vertex++)
                    Assert.That(Vector3.Distance(worldVertices[vertex], convertedWorld[vertex]), Is.LessThan(0.00001f));
                Assert.That(Vector3.Distance(bounds.center, converted.bounds.center), Is.LessThan(0.00001f));
                Assert.That(Vector3.Distance(bounds.size, converted.bounds.size), Is.LessThan(0.00001f));
                Assert.That(original.blendShapeCount, Is.EqualTo(1)); Assert.That(converted.sharedMesh.blendShapeCount, Is.Zero);
                Assert.That(converted.bones.Single(), Is.EqualTo(obj.transform)); Assert.That(obj.GetComponent<MeshRenderer>(), Is.Null);
            }
            finally { RenderTexture.ReleaseTemporary(target); }
        }

        [TestCase(false)] [TestCase(true)]
        public void UnweightedSkinningRetainsNativeGeometryWithExplicitAndImplicitRoots(bool explicitRoot)
        {
            var part = PolicyChild("Unweighted").AddComponent<SkinnedMeshRenderer>(); part.sharedMesh = Mesh("Morph"); renderer.sharedMesh = null;
            part.transform.localPosition = Vector3.left; part.transform.localRotation = Quaternion.Euler(13, 20, 7);
            part.SetBlendShapeWeight(0, 50);
            var bone = explicitRoot ? PolicyChild("Other Root").transform : part.transform;
            if (explicitRoot) { bone.localPosition = Vector3.right; bone.localRotation = Quaternion.Euler(5, -12, 9); part.rootBone = bone; }
            var before = AssemblyWorldVertices(part); var original = part.sharedMesh;
            Assert.That(RigidMeshConversion.Unweighted(original), Is.True);
            var modified = Track(RigidMeshConversion.RigidMesh(original, true));
            part.sharedMesh = modified; part.bones = new[] { part.transform }; part.SetBlendShapeWeight(0, 50);
            var after = AssemblyWorldVertices(part);
            for (int vertex = 0; vertex < before.Length; vertex++) Assert.That(Vector3.Distance(before[vertex], after[vertex]), Is.LessThan(0.00001f));
        }

        [TestCase("Reader")] [TestCase("MissingBinding")] [TestCase("MeshAnimation")] [TestCase("AdditionalStream")]
        public void RigidConversionRetainsExternalOrUnverifiedInterfaces(string reason)
        {
            var part = RigidPart(); var clip = Clip();
            if (reason == "Reader") PolicyChild("Reader").AddComponent<DependencyAuthoringFixture>().Target = part;
            if (reason == "MissingBinding") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Rigid", typeof(SkinnedMeshRenderer), "blendShape.Ignored Morph"), AnimationCurve.Constant(0, 1, 100));
            if (reason == "MeshAnimation") AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("Rigid", typeof(MeshFilter), "m_Mesh"), new[] { new ObjectReferenceKeyframe { value = part.GetComponent<MeshFilter>().sharedMesh } });
            if (reason == "AdditionalStream") part.additionalVertexStreams = part.GetComponent<MeshFilter>().sharedMesh;
            var animations = RigidAnimations(clip);
            var graph = AvatarDependencyGraph.Create(root, animations, new ObjectOptimizationState { Scope = new OptimizationBuildScope { Root = root.transform } }, false);
            Assert.That(RigidMeshConversion.ValidateExplicitConversion(part, animations, graph, new ShaderPropertyUsage()), Is.Not.Null);
            Assert.That(part != null, Is.True);
        }

        [Test]
        public void RigidConversionRetargetsMutableClipsAndPreservesRendererSettings()
        {
            var part = RigidPart(); var original = part.GetComponent<MeshFilter>().sharedMesh; var clip = Clip();
            var binding = EditorCurveBinding.DiscreteCurve("Rigid", typeof(MeshRenderer), "m_Enabled");
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 1, 0));
            part.enabled = false; part.shadowCastingMode = ShadowCastingMode.TwoSided; part.receiveShadows = false;
            part.sortingOrder = 7; part.renderingLayerMask = 123; part.rendererPriority = 5; part.allowOcclusionWhenDynamic = false;
            var animations = RigidAnimations(clip); var curve = animations.Bindings.Single().Curve;
            var graph = AvatarDependencyGraph.Create(root, animations, new ObjectOptimizationState { Scope = new OptimizationBuildScope { Root = root.transform } }, false);
            Assert.That(RigidMeshConversion.ValidateExplicitConversion(part, animations, graph, new ShaderPropertyUsage()), Is.Null);
            var converted = RigidMeshConversion.Convert(part, animations, mesh => Track(mesh));
            var virtualClip = animations.Bindings.Single().Clip;
            Assert.That(virtualClip.GetFloatCurve(binding), Is.Null); binding.type = typeof(SkinnedMeshRenderer);
            Assert.That(virtualClip.GetFloatCurve(binding).keys, Is.EqualTo(curve.keys));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Single().type, Is.EqualTo(typeof(MeshRenderer)));
            Assert.That(converted.enabled, Is.False); Assert.That(converted.shadowCastingMode, Is.EqualTo(ShadowCastingMode.TwoSided));
            Assert.That(converted.receiveShadows || converted.allowOcclusionWhenDynamic, Is.False);
            Assert.That(converted.sortingOrder, Is.EqualTo(7)); Assert.That(converted.renderingLayerMask, Is.EqualTo(123));
            Assert.That(converted.rendererPriority, Is.EqualTo(5)); Assert.That(converted.sharedMesh, Is.Not.SameAs(original));
        }
    }
}
