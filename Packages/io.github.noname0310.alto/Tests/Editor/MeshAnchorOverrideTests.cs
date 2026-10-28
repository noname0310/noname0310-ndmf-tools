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
        private ALTOMeshRendererSettingsOverride AnchorSettings()
        {
            var component = root.AddComponent<ALTOMeshRendererSettingsOverride>();
            component.ApplyOnPlatforms = OptimizationPlatforms.All;
            component.OverrideProbeAnchor = true;
            return component;
        }

        [TestCase(false)] [TestCase(true)]
        public void MeshAnchorOverrideEnablesOrdinaryCompatibleMerging(bool explicitAnchor)
        {
            var pair = MergePair(); MaterialDescriptor();
            var firstAnchor = PolicyChild("Common anchor").transform;
            var otherAnchor = PolicyChild("Other anchor").transform; otherAnchor.localPosition = Vector3.one;
            pair.First.probeAnchor = firstAnchor; pair.Second.probeAnchor = otherAnchor;
            pair.First.lightProbeUsage = pair.Second.lightProbeUsage = LightProbeUsage.BlendProbes;
            var third = AssemblyPart("Third", pair.First.bones, Vector3.up);
            third.sharedMaterials = pair.First.sharedMaterials; third.probeAnchor = firstAnchor;
            third.lightProbeUsage = LightProbeUsage.BlendProbes; third.reflectionProbeUsage = ReflectionProbeUsage.Off;
            var parts = new[] { pair.First, pair.Second, third };
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(parts), true, MergePolicy()).Count, Is.EqualTo(2));
            var component = AnchorSettings();
            if (explicitAnchor) { component.AnchorSelection = MeshAnchorSelection.Explicit; component.AnchorOverride = otherAnchor; }
            var settings = root.AddComponent<ALTOTraceAndOptimizeMeshes>(); settings.RebindStaticBones = false;
            var context = new BuildContext(root, null);
            MeshRendererSettingsOverridePass.Capture(context); MeshOptimizationPass.Capture(context);
            MeshRendererSettingsOverridePass.Apply(context);
            var expected = explicitAnchor ? otherAnchor : firstAnchor;
            Assert.That(parts.All(p => p.probeAnchor == expected), Is.True);
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(parts), true, MergePolicy()).Count, Is.EqualTo(1));
            MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            var output = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToArray();
            Assert.That(output.Length, Is.EqualTo(1)); Track(output[0].sharedMesh);
            Assert.That(output[0].probeAnchor, Is.SameAs(expected));
            Assert.That(root.GetComponent<ALTOMeshRendererSettingsOverride>(), Is.Null);
            Assert.That(typeof(ALTOPlatformOptimizationComponent).IsAssignableFrom(typeof(ALTOMeshRendererSettingsOverride)), Is.True);
        }

        [Test]
        public void MeshAnchorOverrideGeneratesACommonBoundsCenterForImplicitAnchors()
        {
            var pair = MergePair(); MaterialDescriptor();
            var rigid = RigidPart(); rigid.probeAnchor = null;
            pair.Second.gameObject.SetActive(false);
            var parts = new Renderer[] { pair.First, pair.Second, rigid };
            var bounds = parts[0].bounds; foreach (var part in parts.Skip(1)) bounds.Encapsulate(part.bounds);
            var component = AnchorSettings();
            var context = new BuildContext(root, null); MeshRendererSettingsOverridePass.Capture(context); MeshRendererSettingsOverridePass.Apply(context);
            var anchor = pair.First.probeAnchor;
            Assert.That(anchor, Is.Not.Null); Assert.That(anchor.parent, Is.SameAs(root.transform));
            Assert.That(Vector3.Distance(anchor.position, bounds.center), Is.LessThan(0.000001f));
            Assert.That(parts.All(r => r.probeAnchor == anchor), Is.True);
            Assert.That(pair.Second.gameObject.activeSelf, Is.False);
            Assert.That(rigid.GetComponent<MeshRenderer>(), Is.SameAs(rigid), "Anchor assignment must not convert or merge a renderer.");
        }

        [Test]
        public void MeshAnchorSelectionIsDeterministicWhenCountsTie()
        {
            var pair = MergePair(); var first = PolicyChild("A anchor").transform; var second = PolicyChild("Z anchor").transform;
            pair.First.probeAnchor = second; pair.Second.probeAnchor = first;
            Assert.That(LightingAnchorSelection.Resolve(root, new Renderer[] { pair.First, pair.Second }, null), Is.SameAs(first));
            Assert.That(LightingAnchorSelection.Resolve(root, new Renderer[] { pair.Second, pair.First }, null), Is.SameAs(first));
        }

        [Test]
        public void MeshAnchorOverrideHonorsScopePreservationAndGeneratedInputs()
        {
            var pair = MergePair(); MaterialDescriptor(); var scope = PolicyChild("Scope");
            pair.First.transform.SetParent(scope.transform, true);
            var component = AnchorSettings(); component.ScopeRoot = scope.transform;
            component.AnchorSelection = MeshAnchorSelection.Explicit; component.AnchorOverride = root.transform; component.PreserveRenderers.Add(pair.First);
            var context = new BuildContext(root, null); MeshRendererSettingsOverridePass.Capture(context);
            var generated = AssemblyPart("Generated", pair.First.bones, Vector3.up); generated.transform.SetParent(scope.transform, true);
            MeshRendererSettingsOverridePass.Apply(context);
            Assert.That(pair.First.probeAnchor, Is.Null); Assert.That(pair.Second.probeAnchor, Is.Null);
            Assert.That(generated.probeAnchor, Is.SameAs(root.transform));
        }

        [TestCase("Disabled")] [TestCase("EditorOnly")] [TestCase("PlatformFilter")] [TestCase("BuildTarget")]
        public void MeshAnchorOverrideRespectsComponentExclusion(string exclusion)
        {
            var pair = MergePair(); MaterialDescriptor(); var holder = PolicyChild("Settings");
            var component = holder.AddComponent<ALTOMeshRendererSettingsOverride>(); component.ScopeRoot = root.transform; component.ApplyOnPlatforms = OptimizationPlatforms.All; component.OverrideProbeAnchor = true;
            component.AnchorSelection = MeshAnchorSelection.Explicit; component.AnchorOverride = root.transform;
            if (exclusion == "Disabled") component.enabled = false;
            if (exclusion == "BuildTarget") component.ApplyOnPlatforms = OptimizationPlatforms.None;
            if (exclusion == "EditorOnly") holder.tag = "EditorOnly";
            if (exclusion == "PlatformFilter") BuildPlatformFilters.Register("anchor test", (t, c) => t != holder.transform);
            try
            {
                var context = new BuildContext(root, null); MeshRendererSettingsOverridePass.Capture(context); MeshRendererSettingsOverridePass.Apply(context);
                Assert.That(pair.First.probeAnchor, Is.Null); Assert.That(pair.Second.probeAnchor, Is.Null);
            }
            finally { BuildPlatformFilters.Register("anchor test", (t, c) => true); }
        }

        [Test]
        public void MeshAnchorOverrideRejectsAnExternalReference()
        {
            MaterialDescriptor(); var other = Track(new GameObject("Other avatar"));
            var component = AnchorSettings(); component.AnchorSelection = MeshAnchorSelection.Explicit; component.AnchorOverride = other.transform;
            Assert.Throws<InvalidOperationException>(() => MeshRendererSettingsOverridePass.Capture(new BuildContext(root, null)));
        }

        [Test]
        public void MeshAnchorPlatformsUseTheSharedPolicyAndRetainSerializedChoices()
        {
            var component = AnchorSettings();
            Assert.That(component.ApplyOnPlatforms, Is.EqualTo(OptimizationPlatforms.All));
            Assert.That(OptimizationBuildScope.Applies(component, BuildTarget.StandaloneWindows64), Is.True);
            Assert.That(OptimizationBuildScope.Applies(component, BuildTarget.Android), Is.True);
            component.ApplyOnPlatforms = OptimizationPlatforms.Android;
            var serialized = JsonUtility.ToJson(component);
            var copy = PolicyChild("Copy").AddComponent<ALTOMeshRendererSettingsOverride>();
            JsonUtility.FromJsonOverwrite(serialized, copy);
            Assert.That(OptimizationBuildScope.Applies(copy, BuildTarget.StandaloneWindows64), Is.False);
            Assert.That(OptimizationBuildScope.Applies(copy, BuildTarget.Android), Is.True);
            Assert.That(root.AddComponent<ALTOPhysBoneFilter>().ApplyOnPlatforms, Is.EqualTo(OptimizationPlatforms.Android),
                "Adding an all-platform policy must not change other components' defaults.");
        }

        [Test]
        public void MeshAnchorOverrideOnABuildCloneLeavesAuthoringObjectsAndAssetsUnchanged()
        {
            var pair = MergePair(); MaterialDescriptor(); var anchor = PolicyChild("Selected anchor").transform;
            var component = AnchorSettings(); component.AnchorSelection = MeshAnchorSelection.Explicit; component.AnchorOverride = anchor;
            var original = root.GetComponentsInChildren<Renderer>(true);
            var serialized = original.Select(EditorJsonUtility.ToJson).ToArray();
            var firstMesh = pair.First.sharedMesh; var vertices = firstMesh.vertices;
            var clone = Track(UnityEngine.Object.Instantiate(root));
            var context = new BuildContext(clone, null); MeshRendererSettingsOverridePass.Capture(context); MeshRendererSettingsOverridePass.Apply(context);
            Assert.That(original.Select(EditorJsonUtility.ToJson).ToArray(), Is.EqualTo(serialized));
            Assert.That(firstMesh.vertices, Is.EqualTo(vertices));
            Assert.That(root.GetComponent<ALTOMeshRendererSettingsOverride>(), Is.Not.Null);
            Assert.That(clone.GetComponentsInChildren<Renderer>(true).All(r => r.probeAnchor == clone.transform.Find("Selected anchor")), Is.True);
        }

        [TestCase("ProbeMode")] [TestCase("Shadow")]
        public void MeshAnchorOverrideDoesNotRelaxOtherRendererSettings(string difference)
        {
            var pair = MergePair(); MaterialDescriptor();
            var component = AnchorSettings(); component.AnchorSelection = MeshAnchorSelection.Explicit; component.AnchorOverride = root.transform;
            if (difference == "ProbeMode") pair.Second.lightProbeUsage = LightProbeUsage.BlendProbes;
            else pair.Second.shadowCastingMode = ShadowCastingMode.Off;
            var originalMode = pair.Second.lightProbeUsage; var originalShadow = pair.Second.shadowCastingMode;
            var context = new BuildContext(root, null); MeshRendererSettingsOverridePass.Capture(context); MeshRendererSettingsOverridePass.Apply(context);
            Assert.That(pair.Second.lightProbeUsage, Is.EqualTo(originalMode)); Assert.That(pair.Second.shadowCastingMode, Is.EqualTo(originalShadow));
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }), true, MergePolicy()).Count, Is.EqualTo(2));
        }

        [TestCase(false)] [TestCase(true)]
        public void MeshAnchorEquivalenceRequiresACommonRuntimePosition(bool animated)
        {
            var pair = MergePair(); var a = PolicyChild("Anchor A").transform; var b = PolicyChild("Anchor B").transform;
            a.localPosition = b.localPosition = Vector3.up;
            a.localRotation = Quaternion.Euler(20, 0, 0); b.localScale = Vector3.one * 2;
            pair.First.probeAnchor = a; pair.Second.probeAnchor = b;
            pair.First.lightProbeUsage = pair.Second.lightProbeUsage = LightProbeUsage.BlendProbes;
            var clip = Clip();
            if (animated) AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Anchor B", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Linear(0, 0, 1, 2));
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }), true, MergePolicy(clip: clip)).Count,
                Is.EqualTo(animated ? 2 : 1));
        }

        [Test]
        public void MeshMergeIgnoresUnusedProbeAnchorDifferences()
        {
            var pair = MergePair(); pair.First.probeAnchor = root.transform; pair.Second.probeAnchor = PolicyChild("Unused").transform;
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }), true).Count, Is.EqualTo(1));
        }
    }
}
