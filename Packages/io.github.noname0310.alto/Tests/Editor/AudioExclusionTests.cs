using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(true)] [TestCase(false)]
        public void AudioExclusionRemovesSpatializersBeforeAllSources(bool active)
        {
            var filter = Filter(); filter.RemoveAudioSources = true;
            var owner = PolicyChild("Audio"); owner.SetActive(active);
            var source = owner.AddComponent<AudioSource>();
            var second = owner.AddComponent<AudioSource>();
            var spatial = owner.AddComponent<VRCSpatialAudioSource>();
            var duplicate = owner.AddComponent<VRCSpatialAudioSource>();
            var legacy = owner.AddComponent(SdkSpatializerType());
            var effect = owner.AddComponent<AudioLowPassFilter>();
            BuildFilterPass.RemoveEarly(new BuildContext(root, null));
            Assert.That(source == null && second == null && spatial == null && duplicate == null && legacy == null && effect == null, Is.True);
            Assert.That(owner != null && owner.activeSelf == active, Is.True);
        }

        [TestCase("Default")] [TestCase("Disabled")] [TestCase("Platform")]
        [TestCase("Scope")] [TestCase("EditorOnly")] [TestCase("Preserved")]
        public void AudioExclusionKeepsSourcesOutsideTheExplicitSelection(string reason)
        {
            var filter = Filter(); filter.RemoveAudioSources = reason != "Default";
            var owner = PolicyChild("Audio"); var audio = owner.AddComponent<AudioSource>();
            var spatial = owner.AddComponent<VRCSpatialAudioSource>();
            if (reason == "Disabled") filter.enabled = false;
            if (reason == "Platform") filter.ApplyOnPlatforms = OptimizationPlatforms.None;
            if (reason == "Scope") filter.ScopeRoot = PolicyChild("Scope").transform;
            if (reason == "EditorOnly") owner.tag = "EditorOnly";
            if (reason == "Preserved") filter.PreserveAudioSources.Add(audio);
            BuildFilterPass.RemoveEarly(new BuildContext(root, null));
            Assert.That(audio != null && spatial != null, Is.True);
        }

        [Test]
        public void AudioExclusionRejectsRemovalRequiredByARetainedListenerEffect()
        {
            var filter = Filter(); filter.RemoveAudioSources = true;
            var owner = PolicyChild("Mixed audio outputs");
            var audio = owner.AddComponent<AudioSource>(); var listener = owner.AddComponent<AudioListener>();
            var effect = owner.AddComponent<AudioLowPassFilter>();
            var zone = owner.AddComponent<AudioReverbZone>();
            var spatial = owner.AddComponent<VRCSpatialAudioSource>();
            // Unity refuses to remove the source even with another AudioBehaviour
            // satisfying the retained filter's RequireComponent attribute.
            Assert.Throws<InvalidOperationException>(() => BuildFilterPass.RemoveEarly(new BuildContext(root, null)));
            Assert.That(audio != null && spatial != null, Is.True);
            Assert.That(listener != null && effect != null && zone != null, Is.True);
        }

        [Test]
        public void AudioExclusionDoesNotPartiallyRemoveARequiredSourceGroup()
        {
            var filter = Filter(); filter.RemoveAudioSources = true;
            var owner = PolicyChild("Multiple sources");
            var first = owner.AddComponent<AudioSource>(); var second = owner.AddComponent<AudioSource>();
            var spatial = owner.AddComponent<VRCSpatialAudioSource>(); filter.PreserveAudioSources.Add(first);
            // Unity's required-component removal policy cannot select which AudioSource
            // the retained spatializer requires. Reject the conflict before deletion.
            Assert.Throws<InvalidOperationException>(() => BuildFilterPass.RemoveEarly(new BuildContext(root, null)));
            Assert.That(first != null && second != null && spatial != null, Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void AudioExclusionValidatesRetainedReadersBeforeDeletingAnything(bool readsEffect)
        {
            var filter = Filter(); filter.RemoveAudioSources = true;
            var owner = PolicyChild("Audio"); var audio = owner.AddComponent<AudioSource>();
            var spatial = owner.AddComponent<VRCSpatialAudioSource>();
            root.AddComponent<DependencyAuthoringFixture>().Target = readsEffect ? (Component)spatial : audio;
            Assert.Throws<InvalidOperationException>(() => BuildFilterPass.RemoveEarly(new BuildContext(root, null)));
            Assert.That(audio != null && spatial != null, Is.True);
        }

        [Test]
        public void AudioExclusionPreservationDoesNotOverrideExplicitComponentExclusion()
        {
            var filter = Filter(); filter.RemoveAudioSources = true;
            var audio = PolicyChild("Explicit source").AddComponent<AudioSource>();
            filter.PreserveAudioSources.Add(audio); filter.ExcludedComponents.Add(audio);
            BuildFilterPass.RemoveEarly(new BuildContext(root, null));
            Assert.That(audio == null, Is.True);
        }

        [TestCase("Generators")] [TestCase("ExternalOptimizers")] [TestCase("SdkPreparation")] [TestCase("SdkFinal")]
        public void AudioExclusionRechecksGeneratedSourcesAtLaterBoundaries(string boundary)
        {
            Filter().RemoveAudioSources = true;
            var context = ExpressionBuild(null, Controller(Clip()));
            BuildFilterPass.RemoveEarly(context);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            var owner = PolicyChild("Generated audio"); owner.SetActive(false);
            var audio = owner.AddComponent<AudioSource>();
            var spatial = owner.AddComponent<VRCSpatialAudioSource>();
            var duplicate = owner.AddComponent<VRCSpatialAudioSource>();
            var effect = owner.AddComponent<AudioLowPassFilter>();
            if (boundary == "Generators") AudioOutputFilterPass.Apply(context);
            if (boundary == "ExternalOptimizers") OptimizationPipeline.AfterExternalOptimizers(context);
            if (boundary == "SdkPreparation") AvatarOutputSdkFinalizer.PrepareTransport(root, o => Track(o));
            if (boundary == "SdkFinal") AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            Assert.That(audio == null && spatial == null && duplicate == null && effect == null, Is.True);
            Assert.That(owner != null && !owner.activeSelf, Is.True);
            Assert.That(AudioOutputFilterPass.HasPendingRemovals(context), Is.False);
            AudioOutputFilterPass.Apply(context);
            TrackControllerAssets((AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController);
        }

        [TestCase("Default")] [TestCase("Disabled")] [TestCase("Platform")] [TestCase("Scope")] [TestCase("EditorOnly")]
        public void AudioExclusionDoesNotExpandItsPolicyForGeneratedSources(string reason)
        {
            var filter = Filter(); filter.RemoveAudioSources = reason != "Default";
            if (reason == "Disabled") filter.enabled = false;
            if (reason == "Platform") filter.ApplyOnPlatforms = OptimizationPlatforms.None;
            if (reason == "Scope") filter.ScopeRoot = PolicyChild("Scope").transform;
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            var owner = PolicyChild("Late audio"); if (reason == "EditorOnly") owner.tag = "EditorOnly";
            var audio = owner.AddComponent<AudioSource>(); var spatial = owner.AddComponent<VRCSpatialAudioSource>();
            AudioOutputFilterPass.Apply(context);
            Assert.That(audio != null && spatial != null, Is.True);
        }

        [Test]
        public void AudioExclusionPreservesRegisteredReplacementsWithoutRewritingControllers()
        {
            var filter = Filter(); filter.RemoveAudioSources = true;
            var original = PolicyChild("Preserved").AddComponent<AudioSource>(); filter.PreserveAudioSources.Add(original);
            var source = Controller(Clip()); var context = ExpressionBuild(null, source);
            AudioSource replacement;
            using (new ObjectRegistryScope(new ObjectRegistry(root.transform)))
            {
                BuildFilterPass.RemoveEarly(context);
                AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
                replacement = PolicyChild("Replacement").AddComponent<AudioSource>();
                ObjectRegistry.RegisterReplacedObject(original, replacement); Object.DestroyImmediate(original);
            }
            AvatarOutputSdkFinalizer.Complete(root, _ => Assert.Fail("A preserved replacement must not trigger controller rewriting."));
            Assert.That(replacement != null, Is.True);
            Assert.That(root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController, Is.SameAs(source));
        }

        [TestCase(false)] [TestCase(true)]
        public void AudioExclusionRejectsGeneratedReferenceConflictsBeforeMutation(bool listener)
        {
            Filter().RemoveAudioSources = true;
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            var owner = PolicyChild("Late audio"); var audio = owner.AddComponent<AudioSource>();
            var spatial = owner.AddComponent<VRCSpatialAudioSource>();
            if (listener) { owner.AddComponent<AudioListener>(); owner.AddComponent<AudioLowPassFilter>(); }
            else root.AddComponent<DependencyAuthoringFixture>().Target = spatial;
            Assert.Throws<InvalidOperationException>(() => AudioOutputFilterPass.Apply(context));
            Assert.That(audio != null && spatial != null, Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void AudioExclusionRemovesOnlyExclusivelyOrphanedGeneratedCurves(bool shared)
        {
            var filter = Filter(); filter.RemoveAudioSources = true;
            var left = PolicyChild("Left"); var right = PolicyChild("Right"); filter.ScopeRoot = left.transform;
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            var removed = PolicyChild("Audio", left.transform).AddComponent<AudioSource>();
            var kept = PolicyChild("Audio", right.transform).AddComponent<AudioSource>();
            var source = Clip(); var binding = EditorCurveBinding.FloatCurve("Audio", typeof(AudioSource), "m_Volume");
            AnimationUtility.SetEditorCurve(source, binding, AnimationCurve.Constant(0, 3, .5f));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(source));
            var entries = new Dictionary<object, VirtualAnimatorController> { [left.AddComponent<Animator>()] = controller };
            if (shared) entries.Add(right.AddComponent<Animator>(), controller);
            AudioOutputFilterPass.Apply(context, entries);
            var clip = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(removed == null && kept != null, Is.True);
            Assert.That(clip.GetFloatCurveBindings().Contains(binding), Is.EqualTo(shared));
            Assert.That(MotionAnalysis.EndTime(clip), Is.EqualTo(3));
            Assert.That(AnimationUtility.GetCurveBindings(source), Does.Contain(binding));
        }

        [TestCase(false)] [TestCase(true)]
        public void AudioExclusionValidatesGeneratedObjectReferenceCurves(bool orphaned)
        {
            Filter().RemoveAudioSources = true;
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            var audio = PolicyChild("Audio").AddComponent<AudioSource>();
            PolicyChild("Consumer").AddComponent<ParameterPolicySourceFixture>();
            var source = Clip(); var binding = orphaned ? EditorCurveBinding.PPtrCurve("Audio", typeof(AudioSource), "m_audioClip") :
                EditorCurveBinding.PPtrCurve("Consumer", typeof(ParameterPolicySourceFixture), "ReferencedObject");
            AnimationUtility.SetObjectReferenceCurve(source, binding, new[] { new ObjectReferenceKeyframe { time = 2, value = orphaned ? null : audio } });
            float duration = source.length;
            var entries = ConstraintControllers(source);
            if (orphaned)
            {
                AudioOutputFilterPass.Apply(context, entries);
                Assert.That(audio == null, Is.True);
                var clip = entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single();
                Assert.That(clip.GetObjectCurveBindings(), Is.Empty);
                Assert.That(MotionAnalysis.EndTime(clip), Is.EqualTo(duration));
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => AudioOutputFilterPass.Apply(context, entries));
                Assert.That(audio != null, Is.True);
            }
        }

        [Test]
        public void AudioExclusionDropsPlaybackOnlyWhenAllItsSourcesAreRemoved()
        {
            var filter = Filter(); filter.RemoveAudioSources = true;
            var scope = PolicyChild("Scope"); filter.ScopeRoot = scope.transform;
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            PolicyChild("Audio", scope.transform).AddComponent<AudioSource>();
            PolicyChild("Kept").AddComponent<AudioSource>();
            var source = Controller(Clip()); var state = source.layers[0].stateMachine.states[0].state;
            state.AddStateMachineBehaviour<VRCAnimatorPlayAudio>().SourcePath = "Scope/Audio";
            state.AddStateMachineBehaviour<VRCAnimatorPlayAudio>().SourcePath = "Kept";
            state.AddStateMachineBehaviour<VRCAvatarParameterDriver>().parameters.Add(
                new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Unrelated", value = 1 });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            AudioOutputFilterPass.Apply(context, new Dictionary<object, VirtualAnimatorController> { [root.AddComponent<Animator>()] = controller });
            var behaviours = controller.AllReachableNodes().OfType<VirtualState>().Single().Behaviours;
            Assert.That(behaviours.OfType<VRCAnimatorPlayAudio>().Single().SourcePath, Is.EqualTo("Kept"));
            Assert.That(behaviours.OfType<VRCAvatarParameterDriver>().Count(), Is.EqualTo(1));
            Assert.That(state.behaviours.OfType<VRCAnimatorPlayAudio>().Count(), Is.EqualTo(2));
        }

        [Test]
        public void AudioExclusionDeclinedRemovalDoesNotEraseAnimation()
        {
            Filter().RemoveAudioSources = true;
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            var audio = PolicyChild("Audio").AddComponent<AudioSource>();
            var binding = EditorCurveBinding.FloatCurve("Audio", typeof(AudioSource), "m_Volume");
            var entries = ConstraintControllers(StaticCurve(binding, 1));
            context.GetState<AudioOutputFilterState>().DestroyObject = _ => { };
            Assert.Throws<InvalidOperationException>(() => AudioOutputFilterPass.Apply(context, entries));
            Assert.That(audio != null, Is.True);
            Assert.That(entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurveBindings(), Does.Contain(binding));
        }
    }
}
