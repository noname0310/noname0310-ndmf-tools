using System;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("Light")] [TestCase("Reflection")] [TestCase("ProxyVolume")]
        public void SelectedRendererProbeSettingsAllowOrdinaryCompatibleMerging(string field)
        {
            var pair = MergePair(); MaterialDescriptor();
            pair.First.probeAnchor = pair.Second.probeAnchor = root.transform;
            var proxy = PolicyChild("Proxy");
            if (field == "Light") pair.Second.lightProbeUsage = LightProbeUsage.BlendProbes;
            if (field == "Reflection") pair.Second.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            if (field == "ProxyVolume")
            {
                pair.First.lightProbeUsage = pair.Second.lightProbeUsage = LightProbeUsage.UseProxyVolume;
                pair.First.lightProbeProxyVolumeOverride = proxy;
                pair.Second.lightProbeProxyVolumeOverride = PolicyChild("Other proxy");
            }
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }), true).Count, Is.EqualTo(2));
            var policy = root.AddComponent<ALTOMeshRendererSettingsOverride>(); policy.ApplyOnPlatforms = OptimizationPlatforms.All;
            policy.OverrideLightProbeUsage = field == "Light";
            policy.OverrideReflectionProbeUsage = field == "Reflection";
            policy.OverrideLightProbeProxyVolume = field == "ProxyVolume"; policy.LightProbeProxyVolume = proxy;
            var context = new BuildContext(root, null);
            MeshRendererSettingsOverridePass.Capture(context); MeshRendererSettingsOverridePass.Apply(context);
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }), true).Count, Is.EqualTo(1));
        }

        [TestCase(BuildTarget.Android, true)] [TestCase(BuildTarget.StandaloneWindows64, false)]
        public void AnchorAndMobileSettingsCanUseIndependentPlatformPoliciesOnTheSameObject(BuildTarget target, bool mobile)
        {
            var pair = MergePair(); MaterialDescriptor(); var anchor = AnchorSettings();
            anchor.AnchorSelection = MeshAnchorSelection.Explicit; anchor.AnchorOverride = root.transform;
            pair.First.shadowCastingMode = ShadowCastingMode.Off;
            var lighting = root.AddComponent<ALTOMeshRendererSettingsOverride>();
            Assert.That(lighting.ApplyOnPlatforms, Is.EqualTo(OptimizationPlatforms.Android));
            lighting.OverrideShadowCasting = lighting.OverrideLightProbeUsage = lighting.OverrideReflectionProbeUsage = true;
            var context = new BuildContext(root, null);
            typeof(OptimizationBuildEnvironment).GetField("Target", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(context.GetState<OptimizationBuildEnvironment>(), target);
            MeshRendererSettingsOverridePass.Capture(context); MeshRendererSettingsOverridePass.Apply(context);
            Assert.That(pair.First.probeAnchor, Is.SameAs(root.transform));
            Assert.That(pair.First.shadowCastingMode, Is.EqualTo(mobile ? ShadowCastingMode.On : ShadowCastingMode.Off));
            Assert.That(pair.First.lightProbeUsage, Is.EqualTo(mobile ? LightProbeUsage.BlendProbes : LightProbeUsage.Off));
            Assert.That(pair.First.reflectionProbeUsage, Is.EqualTo(mobile ? ReflectionProbeUsage.BlendProbes : ReflectionProbeUsage.Off));
        }

        [Test]
        public void OverlappingAutomaticAnchorPoliciesReuseTheSameGeneratedAnchor()
        {
            var pair = MergePair(); MaterialDescriptor(); AnchorSettings(); AnchorSettings();
            int before = root.transform.childCount;
            var context = new BuildContext(root, null);
            MeshRendererSettingsOverridePass.Capture(context); MeshRendererSettingsOverridePass.Apply(context);
            Assert.That(pair.First.probeAnchor, Is.Not.Null);
            Assert.That(pair.Second.probeAnchor, Is.SameAs(pair.First.probeAnchor));
            Assert.That(root.transform.childCount, Is.EqualTo(before + 1));
        }

        [TestCase(false)] [TestCase(true)]
        public void ConflictingRendererSettingsFailBeforeAnyRendererWrites(bool reverse)
        {
            var pair = MergePair(); MaterialDescriptor();
            foreach (var part in root.GetComponentsInChildren<Renderer>(true)) _ = part.bounds; // Resolve Unity's lazy bounds cache before snapshotting.
            var before = root.GetComponentsInChildren<Renderer>(true).Select(EditorJsonUtility.ToJson).ToArray();
            int childCount = root.transform.childCount;
            var first = AnchorSettings(); first.OverrideReceiveShadows = true; first.ReceiveShadows = reverse;
            var second = AnchorSettings(); second.OverrideReceiveShadows = true; second.ReceiveShadows = !reverse;
            var context = new BuildContext(root, null); MeshRendererSettingsOverridePass.Capture(context);
            Assert.That(Assert.Throws<InvalidOperationException>(() => MeshRendererSettingsOverridePass.Apply(context)).Message, Does.Contain("receiveShadows"));
            Assert.That(root.GetComponentsInChildren<Renderer>(true).Select(EditorJsonUtility.ToJson), Is.EqualTo(before));
            Assert.That(root.transform.childCount, Is.EqualTo(childCount), "Rejected automatic anchors must be cleaned up.");
        }

        [Test]
        public void ExplicitAnchorSelectionNeverFallsBackWhenTheReferenceIsMissing()
        {
            MaterialDescriptor(); var settings = AnchorSettings(); settings.AnchorSelection = MeshAnchorSelection.Explicit;
            Assert.Throws<InvalidOperationException>(() => MeshRendererSettingsOverridePass.Capture(new BuildContext(root, null)));
            var anchor = PolicyChild("Temporary anchor"); settings.AnchorOverride = anchor.transform;
            var context = new BuildContext(root, null); MeshRendererSettingsOverridePass.Capture(context);
            UnityEngine.Object.DestroyImmediate(anchor);
            Assert.Throws<InvalidOperationException>(() => MeshRendererSettingsOverridePass.Apply(context));
        }

        [Test]
        public void EmptyPreservationAndUnselectedOverridesDoNotChangeAnyRendererSettings()
        {
            var pair = MergePair(); MaterialDescriptor(); var policy = root.AddComponent<ALTOMeshRendererSettingsOverride>();
            policy.ApplyOnPlatforms = OptimizationPlatforms.All;
            policy.AnchorSelection = MeshAnchorSelection.Explicit; // A disabled field is not a request.
            policy.ShadowCasting = (ShadowCastingMode)123;
            var before = root.GetComponentsInChildren<Renderer>(true).Select(EditorJsonUtility.ToJson).ToArray();
            var context = new BuildContext(root, null);
            MeshRendererSettingsOverridePass.Capture(context); MeshRendererSettingsOverridePass.Apply(context);
            Assert.That(root.GetComponentsInChildren<Renderer>(true).Select(EditorJsonUtility.ToJson), Is.EqualTo(before));
        }
    }
}
