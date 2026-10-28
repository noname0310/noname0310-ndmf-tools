using System;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private ALTOMeshRendererSettingsOverride ShadowSettings()
        {
            var component = root.AddComponent<ALTOMeshRendererSettingsOverride>();
            component.ApplyOnPlatforms = OptimizationPlatforms.All;
            component.OverrideShadowCasting = component.OverrideReceiveShadows = component.OverrideStaticShadowCaster = true;
            return component;
        }

        [TestCase("Casting")] [TestCase("Receiving")] [TestCase("Static")]
        public void ExplicitShadowPolicyEnablesCompatibleMeshMerging(string difference)
        {
            var pair = MergePair(); MaterialDescriptor();
            if (difference == "Casting") pair.Second.shadowCastingMode = ShadowCastingMode.Off;
            if (difference == "Receiving") pair.Second.receiveShadows = false;
            if (difference == "Static") pair.Second.staticShadowCaster = true;
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }), true).Count, Is.EqualTo(2));
            ShadowSettings(); var settings = root.AddComponent<ALTOTraceAndOptimizeMeshes>(); settings.RebindStaticBones = false;
            var context = new BuildContext(root, null);
            MeshRendererSettingsOverridePass.Capture(context); MeshOptimizationPass.Capture(context);
            MeshRendererSettingsOverridePass.Apply(context);
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }), true).Count, Is.EqualTo(1));
            MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            var output = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToArray();
            Assert.That(output.Length, Is.EqualTo(1)); Track(output[0].sharedMesh);
            Assert.That(output[0].shadowCastingMode, Is.EqualTo(ShadowCastingMode.On));
            Assert.That(output[0].receiveShadows, Is.True); Assert.That(output[0].staticShadowCaster, Is.False);
            Assert.That(root.GetComponent<ALTOMeshRendererSettingsOverride>(), Is.Null);
        }

        [Test]
        public void ShadowPolicyHonorsScopePreservationAndGeneratedInactiveInputs()
        {
            var pair = MergePair(); MaterialDescriptor(); var scope = PolicyChild("Scope");
            pair.First.transform.SetParent(scope.transform, true);
            pair.First.shadowCastingMode = pair.Second.shadowCastingMode = ShadowCastingMode.Off;
            var component = ShadowSettings(); component.ScopeRoot = scope.transform; component.PreserveRenderers.Add(pair.First);
            var protectedPart = AssemblyPart("Protected", pair.First.bones, Vector3.one);
            protectedPart.transform.SetParent(scope.transform, true); protectedPart.shadowCastingMode = ShadowCastingMode.Off;
            var objects = ObjectSettings(); objects.Component.PreserveComponents.Add(protectedPart);
            var context = new BuildContext(root, null); ObjectOptimizationPass.Capture(context); MeshRendererSettingsOverridePass.Capture(context);
            var generated = AssemblyPart("Generated", pair.First.bones, Vector3.up);
            generated.transform.SetParent(scope.transform, true); generated.gameObject.SetActive(false); generated.shadowCastingMode = ShadowCastingMode.Off;
            var rigid = Track(new GameObject("Generated rigid")).AddComponent<MeshRenderer>();
            rigid.transform.SetParent(scope.transform, false); rigid.shadowCastingMode = ShadowCastingMode.Off;
            MeshRendererSettingsOverridePass.Apply(context);
            Assert.That(pair.First.shadowCastingMode, Is.EqualTo(ShadowCastingMode.Off));
            Assert.That(pair.Second.shadowCastingMode, Is.EqualTo(ShadowCastingMode.Off));
            Assert.That(protectedPart.shadowCastingMode, Is.EqualTo(ShadowCastingMode.Off));
            Assert.That(generated.shadowCastingMode, Is.EqualTo(ShadowCastingMode.On));
            Assert.That(rigid.shadowCastingMode, Is.EqualTo(ShadowCastingMode.On));
            Assert.That(generated.gameObject.activeSelf, Is.False);
        }

        [TestCase("Disabled")] [TestCase("EditorOnly")] [TestCase("PlatformFilter")] [TestCase("BuildTarget")]
        public void ShadowPolicyHonorsComponentExclusion(string exclusion)
        {
            var pair = MergePair(); MaterialDescriptor(); var holder = PolicyChild("Settings");
            pair.First.shadowCastingMode = ShadowCastingMode.Off;
            var component = holder.AddComponent<ALTOMeshRendererSettingsOverride>(); component.ScopeRoot = root.transform;
            component.ApplyOnPlatforms = OptimizationPlatforms.All; component.OverrideShadowCasting = true;
            if (exclusion == "Disabled") component.enabled = false;
            if (exclusion == "BuildTarget") component.ApplyOnPlatforms = OptimizationPlatforms.None;
            if (exclusion == "EditorOnly") holder.tag = "EditorOnly";
            if (exclusion == "PlatformFilter") BuildPlatformFilters.Register("shadow test", (t, c) => t != holder.transform);
            try
            {
                var context = new BuildContext(root, null); MeshRendererSettingsOverridePass.Capture(context); MeshRendererSettingsOverridePass.Apply(context);
                Assert.That(pair.First.shadowCastingMode, Is.EqualTo(ShadowCastingMode.Off));
                Assert.That(context.GetState<MeshRendererSettingsOverrideState>().Rules, Is.Empty);
            }
            finally { BuildPlatformFilters.Register("shadow test", (t, c) => true); }
        }

        [TestCase(BuildTarget.Android, true)] [TestCase(BuildTarget.StandaloneWindows64, false)]
        public void ShadowPolicyDefaultsToAndroidAndRequiresSelectedFields(BuildTarget target, bool applies)
        {
            var pair = MergePair(); MaterialDescriptor(); pair.First.shadowCastingMode = ShadowCastingMode.Off;
            var component = root.AddComponent<ALTOMeshRendererSettingsOverride>();
            Assert.That(component.ApplyOnPlatforms, Is.EqualTo(OptimizationPlatforms.Android));
            var context = new BuildContext(root, null);
            typeof(OptimizationBuildEnvironment).GetField("Target", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(context.GetState<OptimizationBuildEnvironment>(), target);
            component.OverrideShadowCasting = true;
            MeshRendererSettingsOverridePass.Capture(context); MeshRendererSettingsOverridePass.Apply(context);
            Assert.That(pair.First.shadowCastingMode, Is.EqualTo(applies ? ShadowCastingMode.On : ShadowCastingMode.Off));
            Assert.That(pair.First.lightProbeUsage, Is.EqualTo(LightProbeUsage.Off));
        }

        [Test]
        public void ShadowPolicyOnlyChangesSelectedFieldsAndLeavesAuthoringCloneSourcesIntact()
        {
            var pair = MergePair(); MaterialDescriptor();
            pair.First.shadowCastingMode = ShadowCastingMode.Off; pair.First.receiveShadows = false; pair.First.staticShadowCaster = true;
            var component = root.AddComponent<ALTOMeshRendererSettingsOverride>(); component.ApplyOnPlatforms = OptimizationPlatforms.All;
            component.OverrideShadowCasting = true;
            var original = root.GetComponentsInChildren<Renderer>(true);
            var serialized = original.Select(EditorJsonUtility.ToJson).ToArray();
            var mesh = pair.First.sharedMesh; var vertices = mesh.vertices;
            var clone = Track(UnityEngine.Object.Instantiate(root));
            var context = new BuildContext(clone, null); MeshRendererSettingsOverridePass.Capture(context); MeshRendererSettingsOverridePass.Apply(context);
            Assert.That(original.Select(EditorJsonUtility.ToJson), Is.EqualTo(serialized));
            Assert.That(mesh.vertices, Is.EqualTo(vertices));
            var output = clone.transform.Find(pair.First.name).GetComponent<SkinnedMeshRenderer>();
            Assert.That(output.shadowCastingMode, Is.EqualTo(ShadowCastingMode.On));
            Assert.That(output.receiveShadows, Is.False); Assert.That(output.staticShadowCaster, Is.True);
        }

        [Test]
        public void ShadowPolicyRejectsReferencesOutsideTheAvatar()
        {
            MaterialDescriptor(); var external = Track(new GameObject("External")).AddComponent<MeshRenderer>();
            var component = ShadowSettings(); component.PreserveRenderers.Add(external);
            Assert.Throws<InvalidOperationException>(() => MeshRendererSettingsOverridePass.Capture(new BuildContext(root, null)));
        }
    }
}
