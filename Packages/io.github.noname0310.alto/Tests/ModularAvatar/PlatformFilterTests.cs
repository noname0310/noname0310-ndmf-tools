using System;
using nadena.dev.modular_avatar.core;
using nadena.dev.ndmf;
using nadena.dev.ndmf.platform;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ParameterPolicy;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.ModularAvatarIntegration.Tests
{
    public sealed class PlatformFilterTests
    {
        private GameObject avatar, settings, feature;
        private BuildContext context;
        private const string Platform = "nadena.dev.ndmf.vrchat.avatar3";

        [SetUp]
        public void SetUp()
        {
            avatar = new GameObject("Platform filter fixture");
            avatar.AddComponent<VRCAvatarDescriptor>();
            settings = new GameObject("Empty settings object"); settings.transform.SetParent(avatar.transform);
            feature = new GameObject("Feature outside the settings subtree"); feature.transform.SetParent(avatar.transform);
            context = new BuildContext(avatar, null, PlatformRegistry.PlatformProviders[Platform]);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(avatar);

        private ModularAvatarPlatformFilter AddFilter(GameObject target, bool exclude, string platform)
        {
            var filter = target.AddComponent<ModularAvatarPlatformFilter>();
            filter.ExcludePlatform = exclude; filter.Platform = platform;
            return filter;
        }

        [TestCase(true, true, false)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(false, false, false)]
        public void AllBuildTargetsStillRespectMaIncludeAndExclude(bool exclude, bool matches, bool expected)
        {
            AddFilter(settings, exclude, matches ? Platform : "other.vrsns");
            var filter = settings.AddComponent<ALTOBuildFilter>();
            filter.ApplyOnPlatforms = OptimizationPlatforms.All;
            filter.ScopeRoot = avatar.transform;
            filter.ExcludedObjects.Add(feature);
            BuildFilterPass.RemoveEarly(context);
            Assert.That(feature == null, Is.EqualTo(expected));
            Assert.That(context.GetState<BuildFilterState>().Scope != null, Is.EqualTo(expected));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AncestorFilterControlsAllOptimizationCaptures(bool parentActive)
        {
            var parent = new GameObject("Filtered parent"); parent.transform.SetParent(avatar.transform);
            settings.transform.SetParent(parent.transform);
            parent.SetActive(parentActive);
            AddFilter(parent, true, Platform);
            var filter = settings.AddComponent<ALTOBuildFilter>();
            filter.ApplyOnPlatforms = OptimizationPlatforms.All; filter.ScopeRoot = avatar.transform;
            filter.ExcludedObjects.Add(feature);
            var policy = settings.AddComponent<ALTOAnimatorParameterPolicy>(); policy.ApplyOnPlatforms = OptimizationPlatforms.All;
            settings.AddComponent<ALTOTraceAndOptimizeBlendShapes>().ScopeRoot = avatar.transform;
            settings.AddComponent<ALTOTraceAndOptimizeAnimator>();
            settings.AddComponent<ALTOMeshRendererSettingsOverride>().ScopeRoot = avatar.transform;
            var shadows = settings.AddComponent<ALTOMeshRendererSettingsOverride>(); shadows.ScopeRoot = avatar.transform;
            shadows.ApplyOnPlatforms = OptimizationPlatforms.All; shadows.OverrideShadowCasting = true;
            var physics = settings.AddComponent<ALTOPhysBoneFilter>(); physics.ApplyOnPlatforms = OptimizationPlatforms.All;
            physics.FilterMode = PhysBoneFilterMode.KeepListed;
            BuildFilterPass.RemoveEarly(context);
            ParameterPolicyPass.Capture(context);
            ALTOPlugin.Capture(context);
            AnimatorOptimizationPass.Capture(context);
            PhysBoneFilterPass.Capture(context);
            MeshRendererSettingsOverridePass.Capture(context);
            Assert.That(feature != null, Is.True);
            Assert.That(context.GetState<ParameterPolicyState>().Enabled, Is.False);
            Assert.That(context.GetState<OptimizationState>().Scopes, Is.Empty);
            Assert.That(context.GetState<AnimatorOptimizationState>().Settings, Is.Null);
            Assert.That(context.GetState<PhysBoneFilterState>().Scope, Is.Null);
            Assert.That(context.GetState<MeshRendererSettingsOverrideState>().Rules, Is.Empty);
        }

        [Test]
        public void MultipleIncludesAreUnionedButAncestorRulesAreIntersected()
        {
            AddFilter(settings, false, "other.vrsns");
            AddFilter(settings, false, Platform);
            Assert.That(BuildPlatformFilters.Includes(settings.transform, context), Is.True);
            AddFilter(avatar, true, Platform);
            Assert.That(BuildPlatformFilters.Includes(settings.transform, context), Is.False);
        }

        [Test]
        public void MixedIncludeAndExcludeFailsBeforeApplyingExclusions()
        {
            AddFilter(settings, false, Platform); AddFilter(settings, true, "other.vrsns");
            var filter = settings.AddComponent<ALTOBuildFilter>();
            filter.ApplyOnPlatforms = OptimizationPlatforms.All; filter.ScopeRoot = avatar.transform;
            filter.ExcludedObjects.Add(feature);
            Assert.Throws<InvalidOperationException>(() => BuildFilterPass.RemoveEarly(context));
            Assert.That(feature != null, Is.True);
        }

        [Test]
        public void DesktopAndAndroidSelectionIsIndependentOfMaVrsnsSelection()
        {
            AddFilter(settings, false, Platform);
            var filter = settings.AddComponent<ALTOBuildFilter>();
            Assert.That(filter.ApplyOnPlatforms, Is.EqualTo(OptimizationPlatforms.Android));
            Assert.That(BuildPlatformFilters.Includes(settings.transform, context), Is.True);
            Assert.That(OptimizationBuildScope.Applies(filter, BuildTarget.Android), Is.True);
            Assert.That(OptimizationBuildScope.Applies(filter, BuildTarget.StandaloneWindows64), Is.False);
            Assert.That(typeof(ALTOOptimizationComponent).GetField("ApplyOnPlatforms"), Is.Null);
            Assert.That(typeof(ALTOTraceAndOptimizeBlendShapes).GetField("ApplyOnPlatforms"), Is.Null);
            Assert.That(typeof(ALTOTraceAndOptimizeAnimator).GetField("ApplyOnPlatforms"), Is.Null);
            var anchor = settings.AddComponent<ALTOMeshRendererSettingsOverride>();
            anchor.ApplyOnPlatforms = OptimizationPlatforms.All;
            Assert.That(anchor.ApplyOnPlatforms, Is.EqualTo(OptimizationPlatforms.All));
            Assert.That(OptimizationBuildScope.Applies(anchor, context), Is.True);
        }
    }
}
