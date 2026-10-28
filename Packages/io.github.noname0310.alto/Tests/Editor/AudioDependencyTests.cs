using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static Type SdkSpatializerType() => AppDomain.CurrentDomain.GetAssemblies()
            .Single(a => a.GetName().Name == "VRC.SDKBase").GetType("ONSPAudioSource", throwOnError: true);

        [TestCase(false, false)] [TestCase(false, true)]
        [TestCase(true, false)] [TestCase(true, true)]
        public void AudioDependencyCollectsOrphanEffectsAndTheirConstraintTrees(bool active, bool animated)
        {
            var settings = ObjectSettings(); var branch = PolicyChild("Sound placement");
            var source = PolicyChild("Sound anchor"); var owner = PolicyChild("Sound effect", branch.transform);
            var effect = owner.AddComponent(SdkSpatializerType());
            ((Behaviour)effect).enabled = true; owner.SetActive(active);
            var constraint = branch.AddComponent<VRCParentConstraint>();
            constraint.Sources.Add(new VRC.Dynamics.VRCConstraintSource(source.transform, 1));
            constraint.IsActive = true;
            var clip = animated ? InitialTransformClip(owner.transform) : Clip();
            if (animated) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(owner.transform, root.transform), effect.GetType(), "gain"),
                AnimationCurve.Linear(0, 1, .3f, 7));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            UnusedTransformOutputs.Apply(root, entries, null, settings, null, new List<string>());
            Assert.That(constraint == null, Is.True, "An orphan effect cannot make its placement constraint observable.");
            var messages = CollectObjects(settings, controller);
            Assert.That(owner == null && branch == null && source == null && effect == null && constraint == null, Is.True);
            Assert.That(messages.Any(m => m.EndsWith("/ ONSPAudioSource")), Is.True);
            if (animated)
            {
                Assert.That(MotionAnalysis.EndTime(controller.AllReachableNodes().OfType<VirtualClip>().Single()), Is.EqualTo(.3f));
                Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(4));
            }
            Assert.That(CollectObjects(settings, controller).Last(), Does.StartWith("Collected 0 unused components, 0 objects"));
        }

        [TestCase(typeof(AudioLowPassFilter))] [TestCase(typeof(AudioHighPassFilter))]
        [TestCase(typeof(AudioEchoFilter))] [TestCase(typeof(AudioDistortionFilter))]
        [TestCase(typeof(AudioReverbFilter))] [TestCase(typeof(AudioChorusFilter))]
        public void AudioDependencyRetainsSourceAndListenerFilterChains(Type filterType)
        {
            var settings = ObjectSettings();
            foreach (string outputKind in new[] { "Source", "Listener", "Both" })
            {
                var owner = PolicyChild(outputKind);
                if (outputKind != "Listener") owner.AddComponent<AudioSource>();
                if (outputKind != "Source") owner.AddComponent<AudioListener>();
                var effect = owner.AddComponent(filterType);
                var other = owner.AddComponent(filterType == typeof(AudioChorusFilter) ? typeof(AudioEchoFilter) : typeof(AudioChorusFilter));
                var before = owner.GetComponents<Component>().Select(c => c.GetInstanceID()).ToArray();
                var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip()));
                var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
                var graph = AvatarDependencyGraph.Create(root, AvatarAnimationIndex.Create(root, entries, null), settings, true);
                foreach (var output in owner.GetComponents<AudioBehaviour>())
                { Assert.That(graph.DependsOn(output, effect), Is.True); Assert.That(graph.DependsOn(output, other), Is.True); }
                CollectObjects(settings, controller);
                Assert.That(owner.GetComponents<Component>().Select(c => c.GetInstanceID()), Is.EqualTo(before), "Filter order must not change.");
            }
        }

        [TestCase(false, false)] [TestCase(false, true)]
        [TestCase(true, false)] [TestCase(true, true)]
        public void AudioDependencyRetainsSpatializersWithAnyAudioSource(bool sdkComponent, bool enabled)
        {
            var settings = ObjectSettings(); var owner = PolicyChild("Sound");
            var first = owner.AddComponent<AudioSource>(); var second = owner.AddComponent<AudioSource>();
            first.enabled = enabled; second.enabled = false; owner.SetActive(enabled);
            var effect = owner.AddComponent(sdkComponent ? typeof(VRCSpatialAudioSource) : SdkSpatializerType());
            ((Behaviour)effect).enabled = enabled;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip()));
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var graph = AvatarDependencyGraph.Create(root, AvatarAnimationIndex.Create(root, entries, null), settings, true);
            Assert.That(graph.DependsOn(first, effect), Is.True); Assert.That(graph.DependsOn(second, effect), Is.True);
            CollectObjects(settings, controller);
            Assert.That(first != null && second != null && effect != null, Is.True);
        }

        [TestCase("Reference")] [TestCase("ComponentPreservation")] [TestCase("SubtreePreservation")]
        [TestCase("OutsideScope")] [TestCase("ObjectCurve")] [TestCase("PreservedInput")]
        [TestCase("AnimationEvent")] [TestCase("UnknownRuntime")]
        public void AudioDependencyRetainsObservedOrUncertainOrphanEffects(string reason)
        {
            var settings = ObjectSettings(reason == "OutsideScope" ? PolicyChild("Scope") : root);
            var owner = PolicyChild("Orphan effect"); var effect = owner.AddComponent(SdkSpatializerType());
            var clip = Clip();
            if (reason == "Reference") root.AddComponent<DependencyAuthoringFixture>().Target = effect;
            if (reason == "ComponentPreservation") settings.ProtectedComponents.Add(ObjectRegistry.GetReference(effect));
            if (reason == "SubtreePreservation") settings.ProtectedSubtrees.Add(new OptimizationBuildScope { Root = owner.transform });
            if (reason == "ObjectCurve") AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("", typeof(DependencyAuthoringFixture), "Target"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = effect } });
            if (reason == "PreservedInput") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(owner.name, effect.GetType(), "gain"), AnimationCurve.Constant(0, .3f, 3));
            if (reason == "AnimationEvent") AnimationUtility.SetAnimationEvents(clip,
                new[] { new AnimationEvent { functionName = "ObserveAudioEffect" } });
            if (reason == "UnknownRuntime") root.AddComponent<ParameterPolicySourceFixture>();
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(
                reason == "PreservedInput" ? TreeController(("Preserved", clip)) : Controller(clip));
            CollectObjects(settings, controller, reason == "PreservedInput" ? new[] { "Preserved" } : null);
            Assert.That(effect != null && owner != null, Is.EqualTo(reason != "PreservedInput"), reason);
            if (reason == "PreservedInput") Assert.That(controller.Parameters.ContainsKey("Preserved"), Is.True);
        }

        [Test]
        public void AudioDependencyDoesNotConfuseListenerOrSkinningWithAnAudioSource()
        {
            var settings = ObjectSettings(); var owner = PolicyChild("Listener bone");
            var listener = owner.AddComponent<AudioListener>(); var effect = owner.AddComponent(SdkSpatializerType());
            renderer.bones = new[] { owner.transform };
            CollectObjects(settings);
            Assert.That(owner != null && listener != null, Is.True); Assert.That(effect == null, Is.True);
            Assert.That(renderer.bones.Single(), Is.EqualTo(owner.transform));
        }

        [Test]
        public void AudioDependencyReleasesExpressionInputsAfterGeneratorRemovesTheSource()
        {
            var settings = ObjectSettings(); var owner = PolicyChild("Generated sound");
            var audio = owner.AddComponent<AudioSource>(); var effect = owner.AddComponent(SdkSpatializerType());
            var clip = StaticCurve(EditorCurveBinding.FloatCurve(owner.name, effect.GetType(), "gain"), 2);
            var native = TreeController(("Sound control", clip));
            var menu = ExpressionMenu(ExpressionControl("Sound", "Sound control"));
            Assert.That(ExpressionLive(native), Does.Contain("Sound control"));
            UnityEngine.Object.DestroyImmediate(audio);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(native);
            CollectObjects(settings, controller);
            Assert.That(effect == null && owner == null, Is.True);
            var built = CommitAnimator(controller, root); var live = ExpressionLive(built);
            Assert.That(live, Does.Not.Contain("Sound control"));
            var report = new ExpressionOptimizationReport();
            var optimizedMenu = PruneMenu(menu, AllExpressionOptions(), report, live);
            Assert.That(optimizedMenu == null || optimizedMenu.controls.Count == 0, Is.True);
            Assert.That(menu.controls.Count, Is.EqualTo(1)); Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(1));
        }

        [Test]
        public void AudioDependencyRetainsIndependentReverbZones()
        {
            var settings = ObjectSettings(); var owner = PolicyChild("Acoustic region");
            var zone = owner.AddComponent<AudioReverbZone>();
            CollectObjects(settings);
            Assert.That(zone != null && owner != null, Is.True);
        }
    }
}
