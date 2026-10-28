using System;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

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
    }
}
