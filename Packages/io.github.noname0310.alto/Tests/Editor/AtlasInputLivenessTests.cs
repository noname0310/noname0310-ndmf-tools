using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.MaterialAtlas;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private AvatarAnimationIndex AtlasIndex(AnimationClip clip, out Dictionary<object, VirtualAnimatorController> entries)
        {
            entries = new() { [VRCAvatarDescriptor.AnimLayerType.FX] = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip)) };
            return AvatarAnimationIndex.Create(root, entries, null);
        }

        [TestCase("Material")]
        [TestCase("Mesh")]
        [TestCase("StaticMesh")]
        public void AtlasOmitsDeadResourceWritersAndCollectionRemovesTheirObjectCurves(string resource)
        {
            var settings = ObjectSettings(); settings.RemoveUnusedObjects = true;
            var target = PolicyChild("Hidden"); target.SetActive(false);
            Renderer draw; UnityEngine.Object value; Type type;
            if (resource == "StaticMesh")
            {
                draw = target.AddComponent<MeshRenderer>(); target.AddComponent<MeshFilter>();
                type = typeof(MeshFilter); value = Mesh();
            }
            else
            {
                draw = target.AddComponent<SkinnedMeshRenderer>(); type = typeof(SkinnedMeshRenderer);
                value = resource == "Material" ? Track(new Material(Shader.Find("Unlit/Texture"))) : Mesh();
            }
            var binding = EditorCurveBinding.PPtrCurve(target.name, type,
                resource == "Material" ? "m_Materials.Array.data[0]" : "m_Mesh");
            var source = Track(new AnimationClip());
            AnimationUtility.SetObjectReferenceCurve(source, binding, new[] {
                new ObjectReferenceKeyframe { time = 0, value = value }, new ObjectReferenceKeyframe { time = .5f, value = null } });
            var index = AtlasIndex(source, out var entries);
            var output = entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single();
            float duration = MotionAnalysis.EndTime(output);
            var liveness = new AtlasInputLiveness();
            Assert.That(liveness.Select(new[] { renderer, draw }, settings, index, null, new List<string>()), Is.EqualTo(new[] { renderer }));
            Assert.That(target != null && draw != null, Is.True, "Planning must retain generator references until collection.");
            Assert.That(output.GetObjectCurve(binding), Is.Not.Null);
            CollectObjects(settings, entries: entries);
            Assert.That(draw == null && target == null, Is.True);
            Assert.That(output.GetObjectCurve(binding), Is.Null);
            Assert.That(MotionAnalysis.EndTime(output), Is.EqualTo(duration));
            Assert.That(AnimationUtility.GetObjectReferenceCurve(source, binding), Has.Length.EqualTo(2));
            Assert.DoesNotThrow(() => liveness.Validate(settings, AvatarAnimationIndex.Create(root, entries, null), null));
        }

        [TestCase("ParentToggle")]
        [TestCase("EnabledToggle")]
        [TestCase("Preserved")]
        [TestCase("OutsideScope")]
        [TestCase("AuthoringReference")]
        [TestCase("ParticleShape")]
        [TestCase("UnknownRetargeting")]
        [TestCase("Immutable")]
        [TestCase("Ambiguous")]
        [TestCase("OptimizationDisabled")]
        [TestCase("UnknownRuntime")]
        public void AtlasKeepsInactiveInputsWithPossibleObservers(string reason)
        {
            var settings = ObjectSettings(reason == "OutsideScope" ? PolicyChild("Scope") : root);
            settings.RemoveUnusedObjects = reason != "OptimizationDisabled";
            var parent = PolicyChild("Parent"); var target = PolicyChild("Hidden", parent.transform);
            var draw = target.AddComponent<SkinnedMeshRenderer>(); draw.sharedMesh = Mesh();
            if (reason == "EnabledToggle") draw.enabled = false; else parent.SetActive(false);
            var clip = Clip();
            if (reason == "ParentToggle" || reason == "EnabledToggle")
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(reason == "ParentToggle" ? "Parent" : "Parent/Hidden",
                    reason == "ParentToggle" ? typeof(GameObject) : typeof(SkinnedMeshRenderer), reason == "ParentToggle" ? "m_IsActive" : "m_Enabled"),
                    AnimationCurve.Constant(0, 1, 1));
            if (reason == "Preserved") settings.ProtectedComponents.Add(ObjectRegistry.GetReference(draw));
            if (reason == "AuthoringReference") root.AddComponent<DependencyAuthoringFixture>().Target = draw;
            if (reason == "UnknownRuntime") root.AddComponent<ParameterPolicySourceFixture>();
            if (reason == "ParticleShape")
            {
                var particles = PolicyChild("Consumer").AddComponent<ParticleSystem>();
                var shape = particles.shape; shape.enabled = true; shape.shapeType = ParticleSystemShapeType.SkinnedMeshRenderer;
                shape.skinnedMeshRenderer = draw;
            }
            if (reason == "UnknownRetargeting" || reason == "Immutable" || reason == "Ambiguous")
                AnimationUtility.SetObjectReferenceCurve(clip,
                    EditorCurveBinding.PPtrCurve("Parent/Hidden", typeof(SkinnedMeshRenderer),
                        reason == "UnknownRetargeting" ? "m_RootBone" : "m_Materials.Array.data[0]"),
                    new[] { new ObjectReferenceKeyframe { value = reason == "UnknownRetargeting" ? parent.transform : null } });
            if (reason == "Ambiguous") PolicyChild("Hidden", parent.transform).AddComponent<SkinnedMeshRenderer>();
            var index = AtlasIndex(clip, out var entries);
            if (reason == "Immutable")
            {
                entries.Values.Single().Layers.First().StateMachine.DefaultState.Motion = VirtualClip.FromMarker(clip);
                index = AvatarAnimationIndex.Create(root, entries, null);
            }
            Assert.That(new AtlasInputLiveness().Select(new Renderer[] { draw }, settings, index, null, new List<string>()),
                Is.EqualTo(new Renderer[] { draw }));
        }

        [TestCase("Activation")]
        [TestCase("Animation")]
        [TestCase("Reference")]
        [TestCase("Event")]
        public void AtlasOmissionsAreRevalidatedAfterLaterBuildMutations(string mutation)
        {
            var settings = ObjectSettings(); settings.RemoveUnusedObjects = true;
            var target = PolicyChild("Hidden"); target.SetActive(false); var draw = target.AddComponent<MeshRenderer>();
            var clip = Clip(); var liveness = new AtlasInputLiveness();
            Assert.That(liveness.Select(new Renderer[] { draw }, settings, AtlasIndex(clip, out _), null, new List<string>()), Is.Empty);
            if (mutation == "Activation") target.SetActive(true);
            if (mutation == "Animation") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("Hidden", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, 1));
            if (mutation == "Reference") root.AddComponent<DependencyAuthoringFixture>().Target = draw;
            if (mutation == "Event") AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { functionName = "Observe" } });
            var error = Assert.Throws<InvalidOperationException>(() => liveness.Validate(settings, AtlasIndex(clip, out _), null));
            Assert.That(error.Message, Does.Contain("liveness changed"));
        }

        [Test]
        public void AtlasPreparationOmitsDeadAnimatedSlotsBeforeValidationAndPacking()
        {
            var target = PolicyChild("Hidden"); target.SetActive(false);
            var draw = target.AddComponent<SkinnedMeshRenderer>(); draw.sharedMesh = Mesh();
            var material = Track(new Material(Shader.Find("Unlit/Texture")));
            renderer.sharedMaterial = draw.sharedMaterial = material;
            var clip = Track(new AnimationClip());
            AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("Hidden", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { value = material } });
            MaterialDescriptor(Controller(clip)); ObjectSettings();
            var context = new BuildContext(root, null); ObjectOptimizationPass.Capture(context);
            var state = EarlyAtlas(context, AtlasGroup());
            state.Settings.UnsupportedInputs = UnsupportedAtlasInput.Error;
            // Replacing an existing atlas does not make an unobservable renderer live.
            state.RequiredRenderers.Add(ObjectRegistry.GetReference(draw));
            var prepare = MaterialAtlasBackend.Prepare; var validate = MaterialAtlasBackend.ValidateRenderer;
            bool packed = false;
            try
            {
                MaterialAtlasBackend.ValidateRenderer = (_, _) => null;
                MaterialAtlasBackend.Prepare = (_, s) =>
                {
                    packed = true;
                    Assert.That(s.Plans.Select(p => p.Renderer), Is.EqualTo(new[] { renderer }));
                };
                MaterialAtlasPass.Prepare(context);
            }
            finally { MaterialAtlasBackend.Prepare = prepare; MaterialAtlasBackend.ValidateRenderer = validate; TrackEarlyAtlasAssets(); }
            Assert.That(packed && state.Prepared, Is.True);
            Assert.That(draw != null, Is.True);
            Assert.DoesNotThrow(() => MaterialAtlasPass.ValidateOmittedInputs(context));
        }

        [Test]
        public void ResourceCurveSharedByLiveAndDeadAnimationRootsSurvivesForItsLiveOwner()
        {
            var settings = ObjectSettings();
            var first = PolicyChild("First"); var second = PolicyChild("Second");
            var dead = PolicyChild("Draw", first.transform).AddComponent<MeshRenderer>(); dead.enabled = false;
            var live = PolicyChild("Draw", second.transform).AddComponent<MeshRenderer>();
            var binding = EditorCurveBinding.PPtrCurve("Draw", typeof(MeshRenderer), "m_Materials.Array.data[0]");
            var source = Track(new AnimationClip());
            AnimationUtility.SetObjectReferenceCurve(source, binding, new[] { new ObjectReferenceKeyframe { value = null } });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(source));
            var entries = new Dictionary<object, VirtualAnimatorController>
                { [first.AddComponent<Animator>()] = controller, [second.AddComponent<Animator>()] = controller };
            CollectObjects(settings, entries: entries);
            Assert.That(dead == null && live != null, Is.True);
            Assert.That(controller.AllReachableNodes().OfType<VirtualClip>().Single().GetObjectCurve(binding), Is.Not.Null);
        }
    }
}
