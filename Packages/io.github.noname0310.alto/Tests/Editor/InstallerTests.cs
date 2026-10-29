using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Installer;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ParameterLayout;
using Noname.AvatarTools.ParameterPolicy;
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
        private InstallerDraft InstallerChoices(bool mobile = false) => new()
        {
            Avatar = root.AddComponent<VRCAvatarDescriptor>(), IncludeMobile = mobile,
            ParameterCompression = false, ParameterLayout = false,
            ReferenceMaterial = AssetDatabase.LoadAssetAtPath<Material>(ALTOInstallerWindow.MobileMaterialPath)
        };

        private GameObject InstallerSettings(InstallerDraft draft)
        {
            var settings = Track(AvatarInstaller.CreateTemplate());
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(settings, scene);
            AvatarInstaller.Configure(settings, draft);
            settings.transform.SetParent(root.transform, false);
            return settings;
        }

        [Test, Category("Installer")]
        public void InstallerTemplateContainsOnlyReusableSettingsAndBundledMaterial()
        {
            var template = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarInstaller.TemplatePath);
            Assert.That(template, Is.Not.Null);
            Assert.That(template.GetComponentsInChildren<Component>(true).Any(c => c == null), Is.False);
            Assert.That(template.GetComponentsInChildren<VRCAvatarDescriptor>(true), Is.Empty);
            Assert.That(template.GetComponentsInChildren<ALTOOptimizationComponent>(true).All(c => c.ScopeRoot == null), Is.True);
            Assert.That(template.GetComponentInChildren<ALTOTraceAndOptimizeBlendShapes>().ScopeRoot, Is.Null);
            Assert.That(template.GetComponentInChildren<ALTOOptimizationPlatformFilter>().enabled, Is.False);
            Assert.That(template.GetComponentInChildren<ALTOMaterialAtlas>().MergeGroups.Single().ReferenceMaterial,
                Is.EqualTo(AssetDatabase.LoadAssetAtPath<Material>(ALTOInstallerWindow.MobileMaterialPath)));
            Assert.That(AssetDatabase.GetDependencies(AvatarInstaller.TemplatePath).Any(p => p.StartsWith("Assets/", StringComparison.Ordinal)), Is.False);
        }

        [Test, Category("Installer")]
        public void InstallerCopiesWholeAvatarScopesAndIndependentChoices()
        {
            Mesh("region A", "region B");
            var draft = InstallerChoices(true);
            draft.PreserveParameters.Names.Add("Control");
            draft.PreserveParameters.Sources.Add(new AnimatorParameterSource { Source = root, ExcludedNames = new() { "Unused" } });
            draft.FrozenValueOverrides.Add(new AnimatorParameterConstant { Name = "Feature", Value = 0 });
            draft.BlendShapeExclusions.Add(new BlendShapeMeshExclusion { Renderer = renderer, BlendShapes = new() { "region A" } });
            draft.OcclusionTargets.Add(new InstallerOcclusionTarget { Renderer = renderer });
            draft.PreserveRenderers.Add(renderer);
            draft.AtlasSize = 4096;
            var clip = Clip("region B"); draft.AdditionalExcludedAnimations.Add(clip);
            Assert.That(AvatarInstaller.Validate(draft, false), Is.Empty);
            var settings = InstallerSettings(draft);
            Assert.That(settings.GetComponentsInChildren<ALTOOptimizationComponent>(true).All(c => c.ScopeRoot == root.transform), Is.True);
            Assert.That(settings.GetComponentInChildren<ALTOTraceAndOptimizeBlendShapes>().ScopeRoot, Is.EqualTo(root.transform));
            Assert.That(settings.GetComponentInChildren<ALTOTraceAndOptimizeBlendShapes>().ExcludedAnimations, Is.EqualTo(new[] { clip }));
            Assert.That(settings.GetComponentInChildren<ALTOMaterialAtlas>().MergeGroups.Single().AtlasSize, Is.EqualTo(4096));
            Assert.That(settings.GetComponentInChildren<ALTOMeshReduction>().PreserveRenderers, Is.EqualTo(new[] { renderer }));
            Assert.That(settings.GetComponentInChildren<ALTOPhysBoneFilter>().FilterMode, Is.EqualTo(PhysBoneFilterMode.KeepListed));
            Assert.That(settings.GetComponentInChildren<ALTOContactFilter>().FilterMode, Is.EqualTo(ContactFilterMode.KeepListed));
            var policy = settings.GetComponentInChildren<ALTOAnimatorParameterPolicy>();
            Assert.That(policy.BakeInitialVisibility, Is.True);
            Assert.That(policy.FreezeMode, Is.EqualTo(AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved));
            draft.PreserveParameters.Names.Clear(); draft.PreserveParameters.Sources[0].ExcludedNames.Clear();
            draft.FrozenValueOverrides[0].Value = 1; draft.BlendShapeExclusions[0].BlendShapes.Clear();
            Assert.That(policy.PreserveParameters.Names, Is.EqualTo(new[] { "Control" }));
            Assert.That(policy.PreserveParameters.Sources.Single().ExcludedNames, Is.EqualTo(new[] { "Unused" }));
            Assert.That(policy.FreezeParameters.Single().Value, Is.Zero);
            Assert.That(settings.GetComponentInChildren<ALTORenderingFilter>().BlendShapeExclusions.Single().BlendShapes, Is.EqualTo(new[] { "region A" }));
        }

        [TestCase(0, 2), Category("Installer")]
        [TestCase(1, 1)]
        public void InstallerOcclusionSelectionResolvesAllShapesOnlyWhenChosen(int selection, int count)
        {
            Mesh("first", "new region");
            var draft = InstallerChoices(true);
            draft.OcclusionTargets.Add(new InstallerOcclusionTarget { Renderer = renderer, RegionSelection = (InstallerRegionSelection)selection, BlendShapes = new() { "first" } });
            var result = InstallerSettings(draft).GetComponentInChildren<ALTORemoveOccludedMesh>().Targets.Single();
            Assert.That(result.BlendShapes.Count, Is.EqualTo(count));
            Assert.That(result.Renderer, Is.EqualTo(renderer));
        }

        [Test, Category("Installer")]
        public void InstallerDisabledFeaturesProduceNoUnusedComponents()
        {
            var draft = InstallerChoices(true);
            draft.NormalizeProbeAnchors = draft.NormalizeRendererSettings = false;
            draft.CreateMaterialAtlas = draft.RemoveOccludedMesh = draft.ReduceMeshes = false;
            var settings = InstallerSettings(draft);
            Assert.That(settings.GetComponentsInChildren<ALTOMeshRendererSettingsOverride>(), Is.Empty);
            Assert.That(settings.GetComponentsInChildren<ALTOMaterialAtlas>(), Is.Empty);
            Assert.That(settings.GetComponentsInChildren<ALTORemoveOccludedMesh>(), Is.Empty);
            Assert.That(settings.GetComponentsInChildren<ALTOMeshReduction>(), Is.Empty);
            Assert.That(settings.transform.Find("Parameters"), Is.Null);
            Assert.That(settings.GetComponentsInChildren<ALTOOptimizationPlatformFilter>().Any(f => f.enabled), Is.False);
        }

        [Test, Category("Installer")]
        public void InstallerRejectsForeignReferencesAndMissingRequiredChoicesBeforeChangingAvatar()
        {
            var draft = InstallerChoices(true);
            draft.ExcludedObjects.Add(Track(new GameObject("Another avatar")));
            draft.ExcludedComponents.Add(root.transform);
            draft.BlendShapeExclusions.Add(new BlendShapeMeshExclusion { Renderer = renderer, BlendShapes = new() { "missing" } });
            draft.ExcludeAfkAnimations = true;
            var errors = AvatarInstaller.Validate(draft, false);
            Assert.That(errors.Any(e => e.Contains("must belong")), Is.True);
            Assert.That(errors.Any(e => e.Contains("required component")), Is.True);
            Assert.That(errors.Any(e => e.Contains("no mesh")), Is.True);
            Assert.That(errors.Any(e => e.Contains("review AFK")), Is.True);
            Assert.Throws<InvalidOperationException>(() => AvatarInstaller.Install(draft));
            Assert.That(root.transform.childCount, Is.Zero);
        }

        [Test, Category("Installer")]
        public void InstallerDependenciesAreExplicitRatherThanSilentlyEnablingOptions()
        {
            var draft = InstallerChoices(true); draft.IncludeCommon = false;
            draft.ParameterCompression = true;
            var errors = AvatarInstaller.Validate(draft, false);
            Assert.That(errors.Any(e => e.Contains("general optimization")), Is.True);
            Assert.That(errors.Any(e => e.Contains("requires matching")), Is.True);
        }

        [Test, Category("Installer")]
        public void InstallerIsOneUndoableAdditionWithoutChangingSourceAssetsOrExistingSettings()
        {
            Mesh("custom face"); renderer.SetBlendShapeWeight(0, 43);
            var mesh = renderer.sharedMesh;
            var draft = InstallerChoices();
            var oldSelection = Selection.objects;
            int group = -1;
            try
            {
                var settings = AvatarInstaller.Install(draft); group = Undo.GetCurrentGroup();
                Assert.That(settings.transform.parent, Is.EqualTo(root.transform));
                var template = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarInstaller.TemplatePath);
                Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(settings), Is.SameAs(template));
                Assert.That(PrefabUtility.GetPrefabInstanceStatus(settings), Is.EqualTo(PrefabInstanceStatus.Connected));
                Assert.That(settings.transform.Find("Mobile"), Is.Null);
                Assert.That(PrefabUtility.GetRemovedGameObjects(settings).Any(r => r.assetGameObject.name == "Mobile"), Is.True);
                Assert.That(renderer.sharedMesh, Is.SameAs(mesh));
                Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(43));
                Assert.That(AvatarInstaller.Validate(draft).Any(e => e.Contains("already has ALTO")), Is.True);
                Assert.Throws<InvalidOperationException>(() => AvatarInstaller.Install(draft));
                Assert.That(root.transform.childCount, Is.EqualTo(1));
                Undo.PerformUndo();
                Assert.That(root.transform.Find(AvatarInstaller.RootName), Is.Null);
                Undo.PerformRedo();
                settings = root.transform.Find(AvatarInstaller.RootName).gameObject;
                Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(settings), Is.SameAs(template));
                Assert.That(PrefabUtility.GetPrefabInstanceStatus(settings), Is.EqualTo(PrefabInstanceStatus.Connected));
                Assert.That(settings.transform.Find("Mobile"), Is.Null);
                Assert.That(settings.GetComponentInChildren<ALTOTraceAndOptimizeBlendShapes>().ScopeRoot, Is.EqualTo(root.transform));
                Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(43));
            }
            finally
            {
                if (group >= 0) Undo.RevertAllDownToGroup(group);
                Selection.objects = oldSelection;
            }
        }

        [Test, Category("Installer")]
        public void InstallerPrefabOverridesSurviveSavingInsideAnAvatarPrefab()
        {
            var draft = InstallerChoices(true);
            draft.CommonMobileOnly = true;
            draft.CreateMaterialAtlas = draft.RemoveOccludedMesh = draft.ReduceMeshes = false;
            draft.PreserveMmdBlendShapes = false;
            draft.NormalizeProbeAnchors = false;
            draft.PreserveParameters.Names.Add("TrackingInput");
            var feature = Track(new GameObject("Optional feature")); feature.transform.SetParent(root.transform, false);
            draft.ExcludedObjects.Add(feature);
            var bone = Track(new GameObject("Preserved bone")); bone.transform.SetParent(root.transform, false);
            draft.PhysBones.Add(bone.AddComponent<VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone>());
            var previousSelection = Selection.objects;
            var template = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarInstaller.TemplatePath);
            Func<string> templateState = () => string.Join("\n", template.GetComponentsInChildren<Component>(true).Select(c => EditorJsonUtility.ToJson(c)));
            string before = templateState();
            string path = "Assets/__ALTO_Installer_Prefab_Test_" + Guid.NewGuid().ToString("N") + ".prefab";
            GameObject reloaded = null;
            int group = -1;
            try
            {
                AvatarInstaller.Install(draft); group = Undo.GetCurrentGroup();
                var savedAvatar = PrefabUtility.SaveAsPrefabAsset(root, path);
                Assert.That(savedAvatar, Is.Not.Null);
                reloaded = (GameObject)PrefabUtility.InstantiatePrefab(savedAvatar, scene);
                var settings = reloaded.transform.Find(AvatarInstaller.RootName).gameObject;
                Assert.That(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(settings), Is.EqualTo(AvatarInstaller.TemplatePath));
                Assert.That(settings.GetComponentInChildren<ALTOTraceAndOptimizeBlendShapes>().ScopeRoot, Is.EqualTo(reloaded.transform));
                Assert.That(settings.GetComponentInChildren<ALTOTraceAndOptimizeBlendShapes>().PreserveMmdBlendShapes, Is.False);
                Assert.That(settings.GetComponentInChildren<ALTOOptimizationPlatformFilter>().enabled, Is.True);
                Assert.That(settings.GetComponentInChildren<ALTOAnimatorParameterPolicy>().PreserveParameters.Names, Is.EqualTo(new[] { "TrackingInput" }));
                Assert.That(settings.GetComponentInChildren<ALTOBuildExclusions>().ExcludedObjects.Single(), Is.EqualTo(reloaded.transform.Find("Optional feature").gameObject));
                Assert.That(settings.GetComponentInChildren<ALTOPhysBoneFilter>().PhysBones.Single().transform, Is.EqualTo(reloaded.transform.Find("Preserved bone")));
                Assert.That(settings.transform.Find("Parameters"), Is.Null);
                Assert.That(settings.GetComponentInChildren<ALTOMaterialAtlas>(), Is.Null);
                Assert.That(settings.transform.Find("Common/Rendering").GetComponent<ALTOMeshRendererSettingsOverride>(), Is.Null);
                Assert.That(templateState(), Is.EqualTo(before), "Installation must not change the package defaults.");
            }
            finally
            {
                if (reloaded != null) Object.DestroyImmediate(reloaded);
                if (group >= 0) Undo.RevertAllDownToGroup(group);
                AssetDatabase.DeleteAsset(path);
                Selection.objects = previousSelection;
            }
        }

        [TestCase(BuildTarget.StandaloneWindows64, true, false), Category("Installer")]
        [TestCase(BuildTarget.Android, true, true)]
        [TestCase(BuildTarget.iOS, true, true)]
        [TestCase(BuildTarget.StandaloneWindows64, false, true)]
        [TestCase(BuildTarget.StandaloneLinux64, false, true)]
        public void InstallerCommonPlatformSelectionKeepsCrossPlatformSynchronization(BuildTarget target, bool mobileOnly, bool common)
        {
            var draft = InstallerChoices(); draft.CommonMobileOnly = mobileOnly;
            draft.ParameterCompression = draft.ParameterLayout = true;
            InstallerSettings(draft);
            var context = new BuildContext(root, null);
            typeof(OptimizationBuildEnvironment).GetField("Target", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(context.GetState<OptimizationBuildEnvironment>(), target);
            ALTOPlugin.Capture(context); AnimatorOptimizationPass.Capture(context); MeshOptimizationPass.Capture(context);
            ParameterLayoutPass.Capture(context);
            Assert.That(context.GetState<OptimizationState>().Scopes.Count > 0, Is.EqualTo(common));
            Assert.That(context.GetState<AnimatorOptimizationState>().Settings != null, Is.EqualTo(common));
            Assert.That(context.GetState<MeshOptimizationState>().Enabled, Is.EqualTo(common));
            Assert.That(context.GetState<ParameterLayoutState>().Settings, Is.Not.Null);
            Assert.That(context.GetState<ParameterLayoutState>().Settings.Compression.Budget, Is.EqualTo(256));
        }

        [Test, Category("Installer")]
        public void InstallerPlatformFiltersIntersectButNeverFilterObjectsOrForeignComponents()
        {
            var draft = InstallerChoices(); draft.CommonMobileOnly = true;
            var settings = InstallerSettings(draft);
            var context = new BuildContext(root, null);
            typeof(OptimizationBuildEnvironment).GetField("Target", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(context.GetState<OptimizationBuildEnvironment>(), BuildTarget.Android);
            var optimization = settings.transform.Find("Common/Optimization");
            var inner = optimization.gameObject.AddComponent<ALTOOptimizationPlatformFilter>(); inner.ApplyOnPlatforms = OptimizationPlatforms.PC;
            var foreign = optimization.gameObject.AddComponent<AudioSource>();
            Assert.That(BuildPlatformFilters.IncludesSettings(optimization, context), Is.False);
            Assert.That(BuildPlatformFilters.Includes(foreign.transform, context), Is.True);
            inner.enabled = false;
            Assert.That(BuildPlatformFilters.IncludesSettings(optimization, context), Is.True);
            Assert.That(foreign != null, Is.True);
        }

        [TestCase(OptimizationPlatforms.Android | OptimizationPlatforms.IOS, true), Category("Installer")]
        [TestCase(OptimizationPlatforms.PC, false)]
        [TestCase(OptimizationPlatforms.None, false)]
        public void InstallerMobilePreservationStillProtectsPcSynchronization(OptimizationPlatforms platforms, bool preserved)
        {
            var draft = InstallerChoices(true); draft.ParameterCompression = draft.ParameterLayout = true;
            draft.PreserveParameters.Names.Add("External input");
            var settings = InstallerSettings(draft);
            settings.transform.Find("Mobile").gameObject.AddComponent<ALTOOptimizationPlatformFilter>().ApplyOnPlatforms = platforms;
            var context = new BuildContext(root, null);
            typeof(OptimizationBuildEnvironment).GetField("Target", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(context.GetState<OptimizationBuildEnvironment>(), BuildTarget.StandaloneWindows64);
            ParameterCompressionPass.CapturePolicyPreservation(context); ParameterPolicyPass.Capture(context);
            Assert.That(context.GetState<TransportPolicyPreservation>().Names.Contains("External input"), Is.EqualTo(preserved));
            Assert.That(context.GetState<ParameterPolicyState>().Enabled, Is.False);
        }

        private AnimatorStateTransition InstallerEdge(AnimatorState from, AnimatorState to, AnimatorConditionMode? condition = null)
        {
            var edge = Track(from.AddTransition(to)); edge.hasExitTime = false;
            if (condition.HasValue) edge.AddCondition(condition.Value, 0, "AFK");
            return edge;
        }

        private (AnimatorController Controller, AnimatorState[] States, AnimationClip[] Clips) InstallerAfkSequence()
        {
            var clips = Enumerable.Range(0, 4).Select(i => { var c = Clip("shape"); c.name = "Unrelated label " + i; return c; }).ToArray();
            var controller = Controller(clips); controller.AddParameter("AFK", AnimatorControllerParameterType.Bool);
            var states = controller.layers[0].stateMachine.states.Select(s => s.state).ToArray();
            InstallerEdge(states[0], states[1], AnimatorConditionMode.If);
            InstallerEdge(states[1], states[2]); InstallerEdge(states[2], states[3], AnimatorConditionMode.IfNot);
            InstallerEdge(states[3], states[0]);
            return (controller, states, clips);
        }

        [Test, Category("Installer")]
        public void InstallerAfkFindsIntroLoopAndExitWithoutNamesOrSourceMutation()
        {
            var fixture = InstallerAfkSequence();
            var before = fixture.Clips.Select(EditorJsonUtility.ToJson).ToArray();
            var result = AfkAnimationDiscovery.Find(new[] { fixture.Controller });
            Assert.That(result.Candidates.Select(c => c.Clip), Is.EquivalentTo(fixture.Clips.Skip(1)));
            Assert.That(result.Candidates.All(c => c.Selected && !c.SharedOutsideAfk), Is.True);
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(fixture.Clips.Select(EditorJsonUtility.ToJson), Is.EqualTo(before));
            Assert.That(fixture.States[0].transitions.Single().conditions.Single().parameter, Is.EqualTo("AFK"));
        }

        [Test, Category("Installer")]
        public void InstallerAfkSharedClipsRequireExplicitSelection()
        {
            var fixture = InstallerAfkSequence();
            var shared = Controller(fixture.Clips[1]);
            var result = AfkAnimationDiscovery.Find(new[] { fixture.Controller, shared });
            var candidate = result.Candidates.Single(c => c.Clip == fixture.Clips[1]);
            Assert.That(candidate.SharedOutsideAfk, Is.True); Assert.That(candidate.Selected, Is.False);
            Assert.That(result.Candidates.Where(c => c.Clip != fixture.Clips[1]).All(c => c.Selected), Is.True);
        }

        [TestCase(true), Category("Installer")]
        [TestCase(false)]
        public void InstallerAfkUnavailablePathsAreNotSuggested(bool muted)
        {
            var fixture = InstallerAfkSequence();
            var edge = fixture.States[0].transitions.Single();
            if (muted) edge.mute = true;
            else edge.AddCondition(AnimatorConditionMode.IfNot, 0, "AFK");
            Assert.That(AfkAnimationDiscovery.Find(new[] { fixture.Controller }).Candidates, Is.Empty);
        }

        [Test, Category("Installer")]
        public void InstallerAfkOtherParametersRemainDynamic()
        {
            var fixture = InstallerAfkSequence();
            fixture.Controller.AddParameter("Arbitrary control", AnimatorControllerParameterType.Float);
            fixture.States[0].transitions.Single().AddCondition(AnimatorConditionMode.Greater, 0.5f, "Arbitrary control");
            Assert.That(AfkAnimationDiscovery.Find(new[] { fixture.Controller }).Candidates.Count, Is.EqualTo(3));
        }

        [Test, Category("Installer")]
        public void InstallerAfkUsesEffectiveOverrideClipsAndBlendShapeTracksOnly()
        {
            var fixture = InstallerAfkSequence();
            var replacement = Clip("replacement");
            var noFace = Track(new AnimationClip());
            var wrapper = Track(new AnimatorOverrideController(fixture.Controller));
            wrapper.ApplyOverrides(new List<KeyValuePair<AnimationClip, AnimationClip>>
            { new(fixture.Clips[1], replacement), new(fixture.Clips[2], noFace) });
            var result = AfkAnimationDiscovery.Find(new[] { wrapper });
            Assert.That(result.Candidates.Select(c => c.Clip), Is.EquivalentTo(new[] { replacement, fixture.Clips[3] }));
        }

        [TestCase(BlendTreeType.Simple1D), Category("Installer")]
        [TestCase(BlendTreeType.Direct)]
        public void InstallerAfkDetectsParameterDrivenBlendTreeBranches(BlendTreeType type)
        {
            var normal = Clip("normal"); var afk = Clip("afk face");
            var controller = Controller(normal);
            var tree = Track(new BlendTree { blendType = type, blendParameter = "AFK", useAutomaticThresholds = false });
            tree.children = new[] {
                new ChildMotion { motion = normal, threshold = 0, directBlendParameter = "Always", timeScale = 1 },
                new ChildMotion { motion = afk, threshold = 1, directBlendParameter = "AFK", timeScale = 1 }
            };
            controller.layers[0].stateMachine.defaultState.motion = tree;
            Assert.That(AfkAnimationDiscovery.Find(new[] { controller }).Candidates.Select(c => c.Clip), Is.EqualTo(new[] { afk }));
        }

        [Test, Category("Installer")]
        public void InstallerAfkDoesNotInventBooleanValuesOutsideZeroAndOne()
        {
            var normal = Clip("normal"); var afk = Clip("afk face"); var impossible = Clip("unreachable face");
            var controller = Controller(normal);
            var tree = Track(new BlendTree { blendType = BlendTreeType.Simple1D, blendParameter = "AFK", useAutomaticThresholds = false });
            tree.children = new[] {
                new ChildMotion { motion = normal, threshold = 0, timeScale = 1 },
                new ChildMotion { motion = afk, threshold = 1, timeScale = 1 },
                new ChildMotion { motion = impossible, threshold = 2, timeScale = 1 }
            };
            controller.layers[0].stateMachine.defaultState.motion = tree;
            Assert.That(AfkAnimationDiscovery.Find(new[] { controller }).Candidates.Select(c => c.Clip), Is.EqualTo(new[] { afk }));
        }

        [Test, Category("Installer")]
        public void InstallerAfkFollowsNestedMachinesAndExitTransitions()
        {
            var idle = Clip("idle"); var inside = Clip("inside"); var exit = Clip("exit");
            var controller = Controller(idle, exit); controller.AddParameter("AFK", AnimatorControllerParameterType.Bool);
            var machine = controller.layers[0].stateMachine;
            var nested = Track(machine.AddStateMachine("Nested"));
            var state = Track(nested.AddState("Nested state")); state.motion = inside;
            var entry = Track(machine.defaultState.AddTransition(nested)); entry.AddCondition(AnimatorConditionMode.If, 0, "AFK");
            Track(state.AddExitTransition()).AddCondition(AnimatorConditionMode.IfNot, 0, "AFK");
            Track(machine.AddStateMachineTransition(nested, machine.states[1].state));
            var result = AfkAnimationDiscovery.Find(new[] { controller });
            Assert.That(result.Candidates.Select(c => c.Clip), Is.EquivalentTo(new[] { inside, exit }));
        }

        [Test, Category("Installer")]
        public void InstallerAfkIncludesSyncedLayerMotionOverrides()
        {
            var fixture = InstallerAfkSequence(); var replacement = Clip("override face");
            var layer = new AnimatorControllerLayer { name = "Synced", syncedLayerIndex = 0, defaultWeight = 1 };
            layer.SetOverrideMotion(fixture.States[1], replacement);
            fixture.Controller.layers = new[] { fixture.Controller.layers[0], layer };
            var result = AfkAnimationDiscovery.Find(new[] { fixture.Controller });
            Assert.That(result.Candidates.Select(c => c.Clip), Is.EquivalentTo(fixture.Clips.Skip(1).Append(replacement)));
        }

        [Test, Category("Installer")]
        public void InstallerAfkSoloTransitionsExcludeOtherCandidates()
        {
            var fixture = InstallerAfkSequence();
            var idle = fixture.States[0];
            InstallerEdge(idle, idle).solo = true;
            Assert.That(AfkAnimationDiscovery.Find(new[] { fixture.Controller }).Candidates, Is.Empty);
        }

        [Test, Category("Installer")]
        public void InstallerAfkUsesDescriptorAndChildControllersButNotNestedAvatarsOrEditorOnly()
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>(); descriptor.customizeAnimationLayers = true;
            var fixture = InstallerAfkSequence();
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer
            { type = VRCAvatarDescriptor.AnimLayerType.FX, isDefault = false, animatorController = fixture.Controller } };
            var extra = Track(new GameObject("Feature")); extra.transform.SetParent(root.transform);
            var extraController = Controller(fixture.Clips[1]); extra.AddComponent<Animator>().runtimeAnimatorController = extraController;
            var nested = Track(new GameObject("Nested avatar")); nested.transform.SetParent(root.transform);
            nested.AddComponent<VRCAvatarDescriptor>(); nested.AddComponent<Animator>().runtimeAnimatorController = Controller(fixture.Clips[2]);
            var ignored = Track(new GameObject("Editor only feature")); ignored.transform.SetParent(root.transform); ignored.tag = "EditorOnly";
            ignored.AddComponent<Animator>().runtimeAnimatorController = Controller(fixture.Clips[3]);
            Assert.That(ParameterSourceResolver.AvatarControllers(root), Is.EquivalentTo(new[] { fixture.Controller, extraController }));
            var result = AfkAnimationDiscovery.Find(root);
            Assert.That(result.Candidates.Single(c => c.Clip == fixture.Clips[1]).SharedOutsideAfk, Is.True);
            Assert.That(result.Candidates.Single(c => c.Clip == fixture.Clips[2]).SharedOutsideAfk, Is.False);
        }

        [Test, Category("Installer")]
        public void InstallerRejectsAfkChangesAfterReview()
        {
            var draft = InstallerChoices(); var fixture = InstallerAfkSequence();
            root.AddComponent<Animator>().runtimeAnimatorController = fixture.Controller;
            draft.ExcludeAfkAnimations = draft.AfkScanComplete = true; draft.AfkScannedAvatar = draft.Avatar;
            draft.AfkCandidates = AfkAnimationDiscovery.Find(root).Candidates;
            fixture.States[0].transitions.Single().mute = true;
            var error = Assert.Throws<InvalidOperationException>(() => AvatarInstaller.Install(draft));
            Assert.That(error.Message, Does.Contain("AFK candidates changed"));
            Assert.That(root.transform.Find(AvatarInstaller.RootName), Is.Null);
        }

        private static object InstallerWindowCall(ALTOInstallerWindow window, string method, params object[] arguments) =>
            typeof(ALTOInstallerWindow).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(window, arguments);

        private static InstallerDraft InstallerWindowDraft(ALTOInstallerWindow window) =>
            (InstallerDraft)typeof(ALTOInstallerWindow).GetField("draft", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(window);

        [Test, Category("Installer")]
        public void InstallerAvatarSelectionAutomaticallyFindsAfkAndInitializesFaceMeshes()
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.VisemeSkinnedMesh = renderer;
            var eyes = descriptor.customEyeLookSettings; eyes.eyelidsSkinnedMesh = renderer; descriptor.customEyeLookSettings = eyes;
            var fixture = InstallerAfkSequence(); root.AddComponent<Animator>().runtimeAnimatorController = fixture.Controller;
            var window = Track(ScriptableObject.CreateInstance<ALTOInstallerWindow>());
            InstallerWindowCall(window, "SetAvatar", descriptor);
            var draft = InstallerWindowDraft(window);
            Assert.That(draft.ExcludeAfkAnimations, Is.False, "Discovery does not opt into expression changes.");
            Assert.That(draft.AfkScanComplete, Is.True);
            Assert.That(draft.AfkScannedAvatar, Is.EqualTo(descriptor));
            Assert.That(draft.AfkCandidates.Select(c => c.Clip), Is.EquivalentTo(fixture.Clips.Skip(1)));
            Assert.That(draft.PreserveRenderers, Is.EqualTo(new[] { renderer }), "Shared lip-sync and eyelid references are deduplicated.");
        }

        [Test, Category("Installer")]
        public void InstallerAfkRefreshPreservesManualChoicesAndDoesNotRestoreRemovedFaceSuggestions()
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>(); descriptor.VisemeSkinnedMesh = renderer;
            var fixture = InstallerAfkSequence(); root.AddComponent<Animator>().runtimeAnimatorController = fixture.Controller;
            var window = Track(ScriptableObject.CreateInstance<ALTOInstallerWindow>());
            InstallerWindowCall(window, "SetAvatar", descriptor);
            var draft = InstallerWindowDraft(window);
            var deselected = draft.AfkCandidates.First().Clip;
            draft.AfkCandidates.First().Selected = false;
            draft.PreserveRenderers.Clear();
            InstallerWindowCall(window, "RefreshAfkAnimations");
            Assert.That(draft.AfkCandidates.Single(c => c.Clip == deselected).Selected, Is.False);
            Assert.That(draft.PreserveRenderers, Is.Empty);
        }

        [Test, Category("Installer")]
        public void InstallerSwitchingOrClearingAvatarReplacesAutomaticResults()
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>(); descriptor.VisemeSkinnedMesh = renderer;
            root.AddComponent<Animator>().runtimeAnimatorController = InstallerAfkSequence().Controller;
            var other = Track(new GameObject("Another avatar"));
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(other, scene);
            var otherDescriptor = other.AddComponent<VRCAvatarDescriptor>();
            var otherFace = other.AddComponent<SkinnedMeshRenderer>(); otherDescriptor.VisemeSkinnedMesh = otherFace;
            var window = Track(ScriptableObject.CreateInstance<ALTOInstallerWindow>());
            InstallerWindowCall(window, "SetAvatar", descriptor);
            InstallerWindowCall(window, "SetAvatar", otherDescriptor);
            var draft = InstallerWindowDraft(window);
            Assert.That(draft.AfkScanComplete, Is.True, "An avatar without AFK clips still has a completed evaluation.");
            Assert.That(draft.AfkScannedAvatar, Is.EqualTo(otherDescriptor));
            Assert.That(draft.AfkCandidates, Is.Empty);
            Assert.That(draft.PreserveRenderers, Is.EqualTo(new[] { otherFace }));
            InstallerWindowCall(window, "SetAvatar", new object[] { null });
            Assert.That(draft.AfkScanComplete, Is.False);
            Assert.That(draft.AfkScannedAvatar, Is.Null);
            Assert.That(draft.PreserveRenderers, Is.Empty);
        }

        private SkinnedMeshRenderer InstallerBodyRenderer(string name, params string[] shapes)
        {
            var owner = Track(new GameObject(name)); owner.transform.SetParent(root.transform, false);
            var result = owner.AddComponent<SkinnedMeshRenderer>();
            var mesh = Track(new Mesh { name = name + " mesh", vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } });
            foreach (var shape in shapes) Frame(mesh, shape, 100, Vector3.right);
            result.sharedMesh = mesh;
            return result;
        }

        [Test, Category("Installer")]
        public void InstallerBodySuggestionPopulatesAllMorphsAndDoesNotOverwriteManualEdits()
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            var face = InstallerBodyRenderer("Face", "vrc.v_aa", "blink"); descriptor.VisemeSkinnedMesh = face;
            var body = InstallerBodyRenderer("Body", "bust_size", "waist_size", "leg_hide");
            var clothing = InstallerBodyRenderer("Bodysuit", "bust_size"); clothing.sharedMesh = body.sharedMesh;
            Assert.That(BodyMeshDiscovery.Find(descriptor), Is.EqualTo(body));
            var window = Track(ScriptableObject.CreateInstance<ALTOInstallerWindow>());
            InstallerWindowCall(window, "SetAvatar", descriptor);
            var draft = InstallerWindowDraft(window);
            var row = draft.OcclusionTargets.Single();
            Assert.That(row.Renderer, Is.EqualTo(body));
            Assert.That(row.RegionSelection, Is.EqualTo(InstallerRegionSelection.AllBlendShapes));
            Assert.That(row.BlendShapes, Is.EqualTo(new[] { "bust_size", "waist_size", "leg_hide" }));
            Assert.That(draft.PreserveRenderers, Is.EqualTo(new[] { face }));
            draft.OcclusionTargets.Clear();
            InstallerWindowCall(window, "RefreshAfkAnimations");
            Assert.That(draft.OcclusionTargets, Is.Empty);
        }

        [Test, Category("Installer")]
        public void InstallerBodyDiscoveryIgnoresNestedAvatarsEditorOnlyAndMeshesWithoutMorphs()
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            var body = InstallerBodyRenderer("Body", "bust");
            InstallerBodyRenderer("Body without morphs");
            InstallerBodyRenderer("Body editor only", "bust").gameObject.tag = "EditorOnly";
            InstallerBodyRenderer("Body nested avatar", "bust").gameObject.AddComponent<VRCAvatarDescriptor>();
            Assert.That(BodyMeshDiscovery.Rank(descriptor).Select(c => c.Renderer), Is.EqualTo(new[] { body }));
        }

        [Test, Category("Installer")]
        public void InstallerBodyDiscoveryLeavesAmbiguousCandidatesUnselected()
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            var body = InstallerBodyRenderer("Body A", "bust");
            InstallerBodyRenderer("Body B", "bust");
            Assert.That(BodyMeshDiscovery.Find(descriptor), Is.Null);
            var window = Track(ScriptableObject.CreateInstance<ALTOInstallerWindow>());
            InstallerWindowCall(window, "SetAvatar", descriptor);
            Assert.That(InstallerWindowDraft(window).OcclusionTargets, Is.Empty);
            Object.DestroyImmediate(body.gameObject);
            Assert.That(BodyMeshDiscovery.Find(descriptor), Is.Not.Null);
        }

        [Test, Category("Installer")]
        public void InstallerBodyDiscoveryAllowsUnifiedFaceAndBodyWithBodyEvidence()
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            var body = InstallerBodyRenderer("Body", "bust_size", "waist_size", "vrc.v_aa", "blink");
            descriptor.VisemeSkinnedMesh = body;
            body.sharedMaterials = new[] { Track(new Material(Shader.Find("Standard")) { name = "Skin" }) };
            Assert.That(BodyMeshDiscovery.Find(descriptor), Is.EqualTo(body));
        }

        private MeshRegionPreset InstallerRegionPreset(Mesh mesh, params string[] regions)
        {
            var preset = Track(ScriptableObject.CreateInstance<MeshRegionPreset>());
            preset.DisplayName = "Synthetic region preset";
            preset.BlendShapes.AddRange(regions);
            preset.Fingerprints.Add(MeshRegionPresetDiscovery.Fingerprint(mesh));
            return preset;
        }

        [Test, Category("Installer")]
        public void InstallerFingerprintsIgnoreAddedMorphsBonesVertexOrderingAndSeamDuplicates()
        {
            var mesh = Mesh("overlay");
            var reference = MeshRegionPresetDiscovery.Fingerprint(mesh);
            var changed = Track(Object.Instantiate(mesh));
            Frame(changed, "new tracking morph", 100, Vector3.up);
            changed.bindposes = new[] { Matrix4x4.identity, Matrix4x4.Translate(Vector3.up) };
            changed.vertices = new[] { Vector3.up, Vector3.right, Vector3.zero, Vector3.right, Vector3.up };
            var fingerprint = MeshRegionPresetDiscovery.Fingerprint(changed);
            Assert.That(fingerprint.PositionsSha256, Is.EqualTo(reference.PositionsSha256));
            Assert.That(fingerprint.DistributionSignature, Is.EqualTo(reference.DistributionSignature));
            changed.ClearBlendShapes();
            changed.vertices = new[] { Vector3.up, Vector3.right * 1.01f, Vector3.zero };
            Assert.That(MeshRegionPresetDiscovery.Fingerprint(changed).PositionsSha256, Is.Not.EqualTo(reference.PositionsSha256));
            Assert.That(mesh.vertexCount, Is.EqualTo(3), "Fingerprinting must not change the source mesh.");
        }

        [Test, Category("Installer")]
        public void InstallerExactRegionPresetInitializesOnlyKnownNamesAndAllowsManualRemoval()
        {
            var mesh = Mesh("overlay", "keep face");
            var draft = InstallerChoices(true);
            var preset = InstallerRegionPreset(mesh, "overlay");
            var matches = MeshRegionPresetDiscovery.Find(draft.Avatar, new[] { preset });
            Assert.That(matches.Single().Exact, Is.True);
            MeshRegionPresetDiscovery.ApplyInitialExactMatches(draft, matches);
            Assert.That(draft.BlendShapeExclusions.Single().BlendShapes, Is.EqualTo(new[] { "overlay" }));
            MeshRegionPresetDiscovery.Apply(draft, matches.Single());
            Assert.That(draft.BlendShapeExclusions, Has.Count.EqualTo(1));
            Assert.That(draft.BlendShapeExclusions.Single().BlendShapes, Has.Count.EqualTo(1));
            draft.BlendShapeExclusions.Clear();
            MeshRegionPresetDiscovery.Find(draft.Avatar, new[] { preset });
            Assert.That(draft.BlendShapeExclusions, Is.Empty, "Discovery must not silently restore a removed preset.");
        }

        [Test, Category("Installer")]
        public void InstallerApproximateRegionPresetRequiresSelectionAndNeverMeansVertexTolerance()
        {
            var mesh = Mesh("overlay");
            var draft = InstallerChoices(true);
            var preset = InstallerRegionPreset(mesh, "overlay");
            mesh.vertices = mesh.vertices.Select(v => v * 1.1f + Vector3.one).ToArray();
            var match = MeshRegionPresetDiscovery.Find(draft.Avatar, new[] { preset }).Single();
            Assert.That(match.Exact, Is.False);
            Assert.That(match.Similarity, Is.GreaterThanOrEqualTo(MeshRegionPresetDiscovery.SuggestionThreshold));
            MeshRegionPresetDiscovery.ApplyInitialExactMatches(draft, new[] { match });
            Assert.That(draft.BlendShapeExclusions, Is.Empty);
            MeshRegionPresetDiscovery.Apply(draft, match);
            Assert.That(draft.BlendShapeExclusions.Single().BlendShapes, Is.EqualTo(new[] { "overlay" }));
        }

        [Test, Category("Installer")]
        public void InstallerAmbiguousPresetsAndMissingRegionNamesAreNotAppliedAutomatically()
        {
            var mesh = Mesh("overlay", "another region");
            var draft = InstallerChoices(true);
            var a = InstallerRegionPreset(mesh, "overlay");
            var b = InstallerRegionPreset(mesh, "another region");
            var matches = MeshRegionPresetDiscovery.Find(draft.Avatar, new[] { a, b });
            Assert.That(matches, Has.Count.EqualTo(2));
            MeshRegionPresetDiscovery.ApplyInitialExactMatches(draft, matches);
            Assert.That(draft.BlendShapeExclusions, Is.Empty);
            a.BlendShapes.Add("missing");
            Assert.That(MeshRegionPresetDiscovery.Find(draft.Avatar, new[] { a }), Is.Empty);
            MeshRegionPresetDiscovery.Apply(draft, matches.Single(m => m.Preset == a));
            Assert.That(draft.BlendShapeExclusions, Is.Empty);
        }

        [Test, Category("Installer")]
        public void InstallerRegionDiscoveryIgnoresNestedAvatarsAndEditorOnlyMeshes()
        {
            var mesh = Mesh("overlay");
            var draft = InstallerChoices(true);
            var preset = InstallerRegionPreset(mesh, "overlay");
            var excluded = InstallerBodyRenderer("Editor mesh", "overlay"); excluded.sharedMesh = mesh;
            excluded.gameObject.tag = "EditorOnly";
            var nested = InstallerBodyRenderer("Other avatar", "overlay"); nested.sharedMesh = mesh;
            nested.gameObject.AddComponent<VRCAvatarDescriptor>();
            Assert.That(MeshRegionPresetDiscovery.Find(draft.Avatar, new[] { preset }).Select(m => m.Renderer), Is.EqualTo(new[] { renderer }));
        }

        [Test, Category("Installer")]
        public void InstallerBundledShinanoPresetContainsOnlyNamesAndFingerprints()
        {
            const string path = "Packages/io.github.noname0310.alto/Presets/Shinano Face Overlays.asset";
            var preset = AssetDatabase.LoadAssetAtPath<MeshRegionPreset>(path);
            Assert.That(preset, Is.Not.Null);
            Assert.That(preset.BlendShapes, Has.Count.EqualTo(9));
            Assert.That(preset.Fingerprints, Has.Count.EqualTo(1), "Only the original face is used as the reference.");
            Assert.That(AssetDatabase.GetDependencies(path).All(p => p.StartsWith("Packages/io.github.noname0310.alto/", StringComparison.Ordinal)), Is.True);
            Assert.That(MeshRegionPresetDiscovery.LoadPresets(), Does.Contain(preset));
            using var serialized = new SerializedObject(preset);
            var property = serialized.GetIterator();
            while (property.Next(true))
                if (property.propertyType == SerializedPropertyType.ObjectReference && property.name != "m_Script")
                    Assert.That(property.objectReferenceValue, Is.Null, "Presets must not retain paid avatar assets.");
        }

        [Test, Category("Installer")]
        public void InstallerSimplePhysicsHidesInactiveEntriesWithoutChangingManualSelections()
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            Func<string, VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone> add = name =>
            {
                var owner = Track(new GameObject(name)); owner.transform.SetParent(root.transform, false);
                return owner.AddComponent<VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone>();
            };
            var active = add("Active hair");
            var disabled = add("Disabled ears"); disabled.enabled = false;
            var inactive = add("Inactive accessory"); inactive.gameObject.SetActive(false);
            var hiddenRoot = add("Hidden root"); hiddenRoot.rootTransform = inactive.transform;
            var window = Track(ScriptableObject.CreateInstance<ALTOInstallerWindow>());
            InstallerWindowCall(window, "SetAvatar", descriptor);
            var draft = InstallerWindowDraft(window);
            Assert.That(draft.PhysBones, Is.Empty, "The Installer must not choose physics priorities.");
            Assert.That(draft.ExcludeAfkAnimations, Is.False);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            Assert.That(typeof(ALTOInstallerWindow).GetField("advancedMode", flags).GetValue(window), Is.False);
            Assert.That(InstallerWindowCall(window, "VisiblePhysBones"), Is.EqualTo(new[] { active }));
            draft.PhysBones.Add(inactive);
            InstallerWindowCall(window, "VisiblePhysBones");
            Assert.That(draft.PhysBones, Is.EqualTo(new[] { inactive }));
            typeof(ALTOInstallerWindow).GetField("hideInactivePhysBones", flags).SetValue(window, false);
            Assert.That((VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone[])InstallerWindowCall(window, "VisiblePhysBones"),
                Is.EquivalentTo(new[] { active, disabled, inactive, hiddenRoot }));
            typeof(ALTOInstallerWindow).GetField("physBoneSearch", flags).SetValue(window, "ears");
            Assert.That(InstallerWindowCall(window, "VisiblePhysBones"), Is.EqualTo(new[] { disabled }));
            Assert.That(draft.PhysBones, Is.EqualTo(new[] { inactive }));
        }
    }
}
