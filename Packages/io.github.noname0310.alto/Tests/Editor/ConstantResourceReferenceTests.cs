using System.Collections.Generic;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private AnimationClip ConstantResourceClip(Component target, string property, Object value)
        {
            var clip = Clip();
            AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve(
                AnimationUtility.CalculateTransformPath(target.transform, root.transform), target.GetType(), property),
                new[] { new ObjectReferenceKeyframe { time = 0, value = value }, new ObjectReferenceKeyframe { time = .3f, value = value } });
            return clip;
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantResourceReferences_StaticMeshMatchesNativeAnimatorWithoutChangingAssets(bool nullOutput)
        {
            Mesh("Shape"); var settings = ObjectSettings();
            var target = PolicyChild("Static mesh"); target.AddComponent<MeshRenderer>();
            var filter = target.AddComponent<MeshFilter>(); filter.sharedMesh = renderer.sharedMesh;
            var output = nullOutput ? null : Track(Object.Instantiate(renderer.sharedMesh));
            if (output != null) { output.vertices = new[] { Vector3.zero, Vector3.up * 2, Vector3.right * 3 }; output.RecalculateBounds(); }
            var initial = filter.sharedMesh; var initialVertices = initial.vertices;
            var outputVertices = output != null ? output.vertices : null;
            var clip = ConstantResourceClip(filter, "m_Mesh", output); var source = Controller(clip);
            SampleFrozen(source, (p, i) => { }, 8, () => { Assert.That(filter.sharedMesh, Is.SameAs(output)); return 0; });
            filter.sharedMesh = initial;
            var index = ConstantOutputIndex(source, out var controller);
            var snapshot = new ObjectAnalysisSnapshot(root, index.ControllerEntries, null, settings, null, null);
            Assert.That(snapshot.Rewrite(() => ConstantResourceReferences.Apply(snapshot, settings, _ => true, new List<string>())), Is.EqualTo(1));
            SampleFrozen(CommitAnimator(controller, root), (p, i) => { }, 8,
                () => { Assert.That(filter.sharedMesh, Is.SameAs(output)); return 0; });
            Assert.That(initial.vertices, Is.EqualTo(initialVertices));
            if (output != null) Assert.That(output.vertices, Is.EqualTo(outputVertices));
            Assert.That(AnimationUtility.GetObjectReferenceCurveBindings(clip).Length, Is.EqualTo(1));
            Assert.That(ConstantResourceReferences.Apply(snapshot, settings, _ => true, new List<string>()), Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantResourceReferences_NonfunctionalLightCookieTrackCannotChangeTheDefault(bool nullOutput)
        {
            Mesh("Shape"); var settings = ObjectSettings(); var light = PolicyChild("Light").AddComponent<Light>();
            light.type = LightType.Spot;
            var initial = Track(new Texture2D(2, 2)); var output = nullOutput ? null : Track(new Texture2D(4, 4)); light.cookie = initial;
            var clip = ConstantResourceClip(light, "m_Cookie", output); var source = Controller(clip);
            SampleFrozen(source, (p, i) => { }, 8, () => { Assert.That(light.cookie, Is.SameAs(initial)); return 0; });
            light.cookie = initial;
            var index = ConstantOutputIndex(source, out var controller);
            var snapshot = new ObjectAnalysisSnapshot(root, index.ControllerEntries, null, settings, null, null);
            Assert.That(snapshot.Rewrite(() => ConstantResourceReferences.Apply(snapshot, settings, _ => true, new List<string>())), Is.Zero);
            SampleFrozen(CommitAnimator(controller, root), (p, i) => { }, 8,
                () => { Assert.That(light.cookie, Is.SameAs(initial)); return 0; });
            Assert.That(initial.width, Is.EqualTo(2)); if (output != null) Assert.That(output.width, Is.EqualTo(4));
        }

        [Test]
        public void ConstantResourceReferences_IdenticalSkinReferenceKeepsNativeMorphOutput()
        {
            Mesh("Shape"); var settings = ObjectSettings(); var clip = ConstantResourceClip(renderer, "m_Mesh", renderer.sharedMesh);
            AnimationUtility.SetEditorCurve(clip, Binding("Shape"), AnimationCurve.Linear(0, 0, .3f, 100));
            var source = Controller(clip); var expected = SampleFrozen(source, (p, i) => { }, 8);
            var index = ConstantOutputIndex(source, out var controller);
            var snapshot = new ObjectAnalysisSnapshot(root, index.ControllerEntries, null, settings, null, null);
            Assert.That(snapshot.Rewrite(() => ConstantResourceReferences.Apply(snapshot, settings, _ => false, new List<string>())), Is.EqualTo(1));
            var actual = SampleFrozen(CommitAnimator(controller, root), (p, i) => { }, 8);
            Assert.That(actual, Is.EqualTo(expected).Within(1e-6));
        }

        [TestCase("ParticleReader")] [TestCase("Culling")] [TestCase("ObjectPreservation")]
        [TestCase("MeshPreservation")] [TestCase("NoPolicy")] [TestCase("MeshCollider")]
        public void ConstantResourceReferences_StaticMeshKeepsInitializationConsumers(string reason)
        {
            Mesh("Shape"); var settings = ObjectSettings();
            var target = PolicyChild("Static mesh"); var visible = target.AddComponent<MeshRenderer>();
            var filter = target.AddComponent<MeshFilter>(); filter.sharedMesh = renderer.sharedMesh;
            var replacement = Track(Object.Instantiate(filter.sharedMesh));
            var meshSettings = new MeshOptimizationState();
            if (reason == "ParticleReader")
            {
                var particles = PolicyChild("Particles").AddComponent<ParticleSystem>();
                var shape = particles.shape; shape.shapeType = ParticleSystemShapeType.MeshRenderer; shape.meshRenderer = visible;
            }
            if (reason == "Culling") root.AddComponent<Animator>().cullingMode = AnimatorCullingMode.CullCompletely;
            if (reason == "MeshCollider") target.AddComponent<MeshCollider>();
            if (reason == "ObjectPreservation") settings.ProtectedComponents.Add(ObjectRegistry.GetReference(visible));
            if (reason == "MeshPreservation") meshSettings.Preserved.Add(ObjectRegistry.GetReference(visible));
            var index = ConstantOutputIndex(Controller(ConstantResourceClip(filter, "m_Mesh", replacement)), out _);
            var snapshot = new ObjectAnalysisSnapshot(root, index.ControllerEntries, null, settings, null, null);
            Assert.That(ConstantResourceReferences.Apply(snapshot, settings, _ => reason != "NoPolicy", new List<string>(), meshSettings), Is.Zero);
            Assert.That(filter.sharedMesh, Is.SameAs(renderer.sharedMesh));
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantResourceReferences_ChangedSkinAndClothRequireSeparateInterfaceProof(bool cloth)
        {
            Mesh("Shape"); var settings = ObjectSettings();
            var replacement = cloth ? renderer.sharedMesh : Track(Object.Instantiate(renderer.sharedMesh));
            if (cloth) renderer.gameObject.AddComponent<Cloth>();
            var initial = renderer.sharedMesh;
            var index = ConstantOutputIndex(Controller(ConstantResourceClip(renderer, "m_Mesh", replacement)), out _);
            var snapshot = new ObjectAnalysisSnapshot(root, index.ControllerEntries, null, settings, null, null);
            Assert.That(ConstantResourceReferences.Apply(snapshot, settings, _ => true, new List<string>()), Is.Zero);
            Assert.That(renderer.sharedMesh, Is.SameAs(initial));
        }
    }
}
