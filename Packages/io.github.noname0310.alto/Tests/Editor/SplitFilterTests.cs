using System;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private ALTORenderingFilter RenderingFilter()
        {
            if (root.GetComponent<VRCAvatarDescriptor>() == null) root.AddComponent<VRCAvatarDescriptor>();
            var filter = root.AddComponent<ALTORenderingFilter>();
            filter.ApplyOnPlatforms = OptimizationPlatforms.All;
            return filter;
        }

        [Test]
        public void SeparateFiltersRetainIndependentScopesAndSkipExplicitlyRemovedTargets()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var mobile = PolicyChild("Mobile");
            var exclusions = PolicyChild("Exclusions", mobile.transform).AddComponent<ALTOBuildExclusions>();
            exclusions.ScopeRoot = root.transform; exclusions.ApplyOnPlatforms = OptimizationPlatforms.All;
            var target = PolicyChild("Rendering target");
            var removed = PolicyChild("Removed feature", target.transform).AddComponent<SkinnedMeshRenderer>();
            var retained = PolicyChild("Retained feature", target.transform).AddComponent<SkinnedMeshRenderer>();
            exclusions.ExcludedObjects.Add(removed.gameObject);
            var rendering = PolicyChild("Rendering", mobile.transform).AddComponent<ALTORenderingFilter>();
            rendering.ScopeRoot = target.transform; rendering.ApplyOnPlatforms = OptimizationPlatforms.All;
            rendering.MaterialExclusions.Add(new MaterialExclusion { Renderer = removed, Slot = 0 });
            rendering.MaterialExclusions.Add(new MaterialExclusion { Renderer = retained, Slot = 0 });
            rendering.BlendShapeExclusions.Add(new BlendShapeMeshExclusion { Renderer = removed, BlendShapes = new() { "Region" } });
            var context = new BuildContext(root, null);
            BuildFilterPass.RemoveEarly(context);
            var state = context.GetState<RenderingFilterState>();
            Assert.That(removed == null && retained != null, Is.True);
            Assert.That(state.Scope.Root, Is.SameAs(target.transform));
            Assert.That(state.Materials.Single().Renderer, Is.SameAs(retained));
            Assert.That(state.SelectedRenderers.Values.Single(), Is.EqualTo("Rendering target/Retained feature"));
            Assert.That(state.BlendShapes, Is.Empty);
            Assert.That(root.GetComponentsInChildren<ALTOBuildExclusions>(true), Is.Empty);
            Assert.That(root.GetComponentsInChildren<ALTORenderingFilter>(true), Is.Empty);
        }

        [Test]
        public void InvalidRenderingRulesFailBeforeEarlyExclusionsMutateTheAvatar()
        {
            var exclusions = Filter();
            var feature = PolicyChild("Feature"); exclusions.ExcludedObjects.Add(feature);
            var rendering = RenderingFilter();
            rendering.BlendShapeExclusions.Add(new BlendShapeMeshExclusion { BlendShapes = new() { "Missing target" } });
            Assert.Throws<InvalidOperationException>(() => BuildFilterPass.RemoveEarly(new BuildContext(root, null)));
            Assert.That(feature != null && exclusions != null && rendering != null, Is.True);
        }

        [Test]
        public void InactiveRenderingPolicyDoesNotBlockIndependentEarlyExclusions()
        {
            var exclusions = Filter();
            var feature = PolicyChild("Feature"); exclusions.ExcludedObjects.Add(feature);
            var rendering = RenderingFilter(); rendering.ApplyOnPlatforms = OptimizationPlatforms.None;
            rendering.BlendShapeExclusions.Add(new BlendShapeMeshExclusion { BlendShapes = new() { "Missing target" } });
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            Assert.That(feature == null, Is.True);
            Assert.That(context.GetState<RenderingFilterState>().Scope, Is.Null);
        }

        [Test]
        public void RenderingPolicyInsideExcludedSubtreeDoesNotAffectSurvivors()
        {
            var exclusions = Filter(); var feature = PolicyChild("Excluded settings");
            exclusions.ExcludedObjects.Add(feature);
            var rendering = feature.AddComponent<ALTORenderingFilter>();
            rendering.ScopeRoot = root.transform; rendering.ApplyOnPlatforms = OptimizationPlatforms.All;
            rendering.ExcludedRendererTypes = RendererFilterTypes.Lines;
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            Assert.That(feature == null, Is.True);
            Assert.That(context.GetState<RenderingFilterState>().Scope, Is.Null);
        }

        [Test]
        public void LegacyCombinedFilterStillCapturesBothRolesAfterSerialization()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var legacy = root.AddComponent<ALTOBuildFilter>();
            legacy.ApplyOnPlatforms = OptimizationPlatforms.All;
            var feature = PolicyChild("Excluded feature"); legacy.ExcludedObjects.Add(feature);
            legacy.RemoveAudioSources = true;
            legacy.RemoveAdditionalMaterialPasses = true;
            legacy.ExcludedRendererTypes = RendererFilterTypes.Lines;
            legacy.MaterialExclusions.Add(new MaterialExclusion { Renderer = renderer, Slot = 1 });
            string json = JsonUtility.ToJson(legacy);
            legacy.ExcludedObjects.Clear(); legacy.RemoveAudioSources = false; legacy.MaterialExclusions.Clear();
            JsonUtility.FromJsonOverwrite(json, legacy);
            Assert.That(legacy.RemoveAudioSources, Is.True);
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            var state = context.GetState<RenderingFilterState>();
            Assert.That(feature == null, Is.True);
            Assert.That(state.Materials.Single().Renderer, Is.SameAs(renderer));
            Assert.That(state.ExcludedRendererTypes, Is.EqualTo(RendererFilterTypes.Lines));
            Assert.That(state.RemoveAdditionalMaterialPasses, Is.True);
        }

        [Test]
        public void LegacyAndNewRenderingPoliciesCannotSilentlyOverrideEachOther()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var legacy = root.AddComponent<ALTOBuildFilter>(); legacy.ApplyOnPlatforms = OptimizationPlatforms.All;
            var feature = PolicyChild("Feature"); legacy.ExcludedObjects.Add(feature);
            RenderingFilter();
            Assert.Throws<InvalidOperationException>(() => BuildFilterPass.RemoveEarly(new BuildContext(root, null)));
            Assert.That(feature != null, Is.True);
        }

        [TestCase(BuildTarget.Android, true)]
        [TestCase(BuildTarget.iOS, true)]
        [TestCase(BuildTarget.StandaloneWindows64, false)]
        public void MobilePoliciesApplyToAndroidAndIosButNotPc(BuildTarget target, bool expected)
        {
            var exclusions = Filter(); var rendering = RenderingFilter();
            exclusions.ApplyOnPlatforms = rendering.ApplyOnPlatforms = OptimizationPlatforms.Android | OptimizationPlatforms.IOS;
            Assert.That(OptimizationBuildScope.Applies(exclusions, target), Is.EqualTo(expected));
            Assert.That(OptimizationBuildScope.Applies(rendering, target), Is.EqualTo(expected));
        }
    }
}
