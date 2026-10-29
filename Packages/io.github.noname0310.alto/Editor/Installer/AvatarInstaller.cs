using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.MaterialAtlas;
using Noname.AvatarTools.MeshReduction;
using Noname.AvatarTools.ParameterPolicy;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Installer
{
    internal static class AvatarInstaller
    {
        internal const string TemplatePath = "Packages/io.github.noname0310.alto/Editor/Installer/Templates/LinkTimeOptimizationSettings.prefab";
        internal const string RootName = "LinkTimeOptimizationSettings";
        internal const OptimizationPlatforms Mobile = OptimizationPlatforms.Android | OptimizationPlatforms.IOS;
        private static readonly Type[] SettingsTypes = {
            typeof(ALTOOptimizationComponent), typeof(ALTOTraceAndOptimizeAnimator), typeof(ALTOTraceAndOptimizeBlendShapes),
            typeof(ALTOTraceAndOptimizeExpressions), typeof(ALTOParameterLayout), typeof(ALTOParameterCompression),
            typeof(ALTOOptimizationPlatformFilter)
        };

        internal static Component[] ExistingSettings(VRCAvatarDescriptor avatar) => avatar == null ? Array.Empty<Component>() :
            avatar.GetComponentsInChildren<Component>(true).Where(c => c != null &&
                c.GetComponentInParent<VRCAvatarDescriptor>(true) == avatar && SettingsTypes.Any(t => t.IsInstanceOfType(c))).ToArray();

        internal static List<string> Validate(InstallerDraft draft, bool checkDependencies = true)
        {
            var errors = new List<string>();
            var avatar = draft.Avatar;
            if (avatar == null) { errors.Add("Choose an avatar."); return errors; }
            if (EditorUtility.IsPersistent(avatar) || !avatar.gameObject.scene.IsValid())
                errors.Add("Choose an avatar instance in a scene or Prefab Mode.");
            if (ExistingSettings(avatar).Length != 0)
                errors.Add("This avatar already has ALTO settings. Edit the existing setup; the installer will not add duplicates or overwrite it.");
            if (avatar.GetComponentsInChildren<VRCAvatarDescriptor>(true).Any(d => d != avatar))
                errors.Add("The avatar contains another Avatar Descriptor. Resolve the nested avatar before installing whole-avatar optimization.");
            if (avatar.transform.Find(RootName) != null)
                errors.Add("An object named " + RootName + " already exists. Rename or remove it before installing.");
            if (!draft.IncludeCommon && draft.IncludeMobile)
                errors.Add("Mobile simplification requires general optimization. Enable it in Setup; choose Optimize Mobile Only to leave PC optimization off.");
            if (draft.ParameterCompression && !draft.ParameterLayout)
                errors.Add("Automatic compression requires matching mobile parameters to PC. Enable both synchronization options or disable compression.");
            if (!draft.IncludeCommon && !draft.IncludeMobile && !draft.ParameterLayout && !draft.ParameterCompression)
                errors.Add("Select at least one optimization or synchronization option.");

            void Owned(Object value, string label, bool optional = false)
            {
                if (value == null) { if (!optional) errors.Add(label + " contains an empty reference. Assign it or remove the row."); return; }
                var transform = value is GameObject go ? go.transform : (value as Component)?.transform;
                if (transform != null && (!transform.IsChildOf(avatar.transform) ||
                    transform.GetComponentInParent<VRCAvatarDescriptor>(true) != avatar))
                    errors.Add(label + " must belong to the selected avatar: " + value.name);
            }
            void Morphs(SkinnedMeshRenderer renderer, IEnumerable<string> names, string label)
            {
                Owned(renderer, label);
                if (renderer == null) return;
                if (renderer.sharedMesh == null) { errors.Add(label + " has no mesh: " + renderer.name); return; }
                foreach (var name in names ?? Enumerable.Empty<string>())
                    if (string.IsNullOrEmpty(name) || renderer.sharedMesh.GetBlendShapeIndex(name) < 0)
                        errors.Add(label + " contains a missing BlendShape on " + renderer.name + ": " + name);
            }
            if (draft.IncludeCommon)
            {
                if (draft.AdditionalExcludedAnimations.Any(c => c == null)) errors.Add("Assign or remove empty animation exclusions.");
                if (draft.ExcludeAfkAnimations && (!draft.AfkScanComplete || draft.AfkScannedAvatar != avatar))
                    errors.Add("Refresh and review AFK animations in Setup before installing.");
            }
            if (!draft.IncludeMobile) return errors;
            foreach (var value in draft.ExcludedObjects)
            {
                Owned(value, "Feature objects");
                if (value == avatar.gameObject) errors.Add("The avatar root cannot be removed.");
            }
            foreach (var value in draft.ExcludedComponents)
            {
                Owned(value, "Excluded components");
                if (value is Transform || value is VRCAvatarDescriptor || value is Animator animator && animator.gameObject == avatar.gameObject)
                    errors.Add("Cannot exclude the avatar's required component: " + value.GetType().Name);
            }
            foreach (var value in draft.PreserveParameters.Sources)
            {
                if (value?.Source == null || !ParameterSourceResolver.IsSupported(value.Source)) errors.Add("Choose a supported source for every preserved feature.");
                else Owned(value.Source, "Preserved features");
            }
            foreach (var value in draft.FrozenValueOverrides)
            {
                if (value == null || string.IsNullOrWhiteSpace(value.Name)) { errors.Add("Each fixed parameter needs a name."); continue; }
                Owned(value.Source, "Fixed parameter sources", true);
                if (value.Source != null && !ParameterSourceResolver.IsSupported(value.Source)) errors.Add("Unsupported fixed parameter source: " + value.Source.name);
                if (!value.UseDefaultValue && (float.IsNaN(value.Value) || float.IsInfinity(value.Value))) errors.Add("Fixed parameter values must be finite.");
            }
            foreach (var value in draft.PhysBones) Owned(value, "Selected PhysBones");
            foreach (var value in draft.PhysBoneRoots) Owned(value?.Root, "PhysBone roots");
            foreach (var value in draft.ContactRoots) Owned(value?.Root, "Contact roots");
            foreach (var value in draft.BlendShapeExclusions)
            {
                if (value == null) { errors.Add("Remove the empty mesh deletion row."); continue; }
                Morphs(value.Renderer, value.BlendShapes, "Mesh region deletion");
                if (float.IsNaN(value.DisplacementThreshold) || float.IsInfinity(value.DisplacementThreshold) || value.DisplacementThreshold < 0)
                    errors.Add("Mesh deletion thresholds must be finite and nonnegative.");
            }
            if (draft.RemoveOccludedMesh)
                foreach (var value in draft.OcclusionTargets)
                {
                    if (value == null) { errors.Add("Remove the empty body mesh row."); continue; }
                    Morphs(value.Renderer, value.RegionSelection == InstallerRegionSelection.SelectedBlendShapes ? value.BlendShapes : Array.Empty<string>(), "Hidden body regions");
                }
            if (draft.ReduceMeshes)
            {
                foreach (var value in draft.PreserveRenderers) Owned(value, "Meshes kept at full detail");
                if (draft.TriangleBudget < 0) errors.Add("The triangle target cannot be negative.");
                if (checkDependencies && MeshReductionBackend.Simplify == null) errors.Add("Install the supported Meshia integration to reduce triangle count, or turn triangle reduction off.");
            }
            if (draft.CreateMaterialAtlas)
            {
                try { MaterialAtlasPass.ValidateGroups(new[] { AtlasGroup(draft) }); }
                catch (InvalidOperationException exception) { errors.Add(exception.Message); }
                if (checkDependencies && MaterialAtlasBackend.Capture == null) errors.Add("Install the supported TexTransTool integration to combine materials, or turn atlas creation off.");
            }
            return errors.Distinct().ToList();
        }

        internal static GameObject Install(InstallerDraft draft)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("Install outside Play Mode after compilation finishes.");
            var errors = Validate(draft);
            if (errors.Count != 0) throw new InvalidOperationException(string.Join("\n", errors));
            if (draft.IncludeCommon && draft.ExcludeAfkAnimations)
            {
                var current = AfkAnimationDiscovery.Find(draft.Avatar.gameObject);
                if (!AfkAnimationDiscovery.SameCandidates(current.Candidates, draft.AfkCandidates))
                    throw new InvalidOperationException("AFK candidates changed. Refresh and review the animations again before installing.");
            }
            var template = AssetDatabase.LoadAssetAtPath<GameObject>(TemplatePath);
            if (template == null) throw new InvalidOperationException("The ALTO settings template is missing. Reinstall the package.");
            var preview = EditorSceneManager.NewPreviewScene();
            GameObject instance = null;
            int undoGroup = -1;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(template, preview);
                Configure(instance, draft);
                Undo.IncrementCurrentGroup(); undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Install ALTO");
                SceneManager.MoveGameObjectToScene(instance, draft.Avatar.gameObject.scene);
                instance.transform.SetParent(draft.Avatar.transform, false);
                // Record after moving to the avatar's scene so avatar-local references are
                // serialized as instance overrides, while the package template stays reusable.
                foreach (var component in instance.GetComponentsInChildren<Component>(true))
                    if (component != null) PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                Undo.RegisterCreatedObjectUndo(instance, "Install ALTO");
                Undo.CollapseUndoOperations(undoGroup);
                EditorSceneManager.MarkSceneDirty(draft.Avatar.gameObject.scene);
                Selection.activeGameObject = instance;
                return instance;
            }
            catch
            {
                if (undoGroup >= 0) Undo.RevertAllDownToGroup(undoGroup);
                if (instance != null) Object.DestroyImmediate(instance);
                throw;
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }

        internal static MaterialAtlasGroup AtlasGroup(InstallerDraft draft) => new()
        { ReferenceMaterial = draft.ReferenceMaterial, AtlasSize = draft.AtlasSize, Compression = draft.AtlasCompression };

        internal static void Configure(GameObject settings, InstallerDraft draft)
        {
            T Get<T>() where T : Component => settings.GetComponentInChildren<T>(true) ??
                throw new InvalidOperationException("The settings template is missing " + typeof(T).Name + ".");
            void Remove(Object value) { if (value != null) Object.DestroyImmediate(value); }
            var root = draft.Avatar.transform;
            foreach (var component in settings.GetComponentsInChildren<ALTOOptimizationComponent>(true)) component.ScopeRoot = root;
            Get<ALTOTraceAndOptimizeBlendShapes>().ScopeRoot = root;
            foreach (var component in settings.GetComponentsInChildren<ALTOPlatformOptimizationComponent>(true)) component.ApplyOnPlatforms = Mobile;
            var anchors = settings.transform.Find("Common/Rendering").GetComponent<ALTOMeshRendererSettingsOverride>();
            anchors.ApplyOnPlatforms = OptimizationPlatforms.All;
            anchors.OverrideProbeAnchor = draft.NormalizeProbeAnchors;
            if (!draft.NormalizeProbeAnchors) Remove(anchors);
            var shapes = Get<ALTOTraceAndOptimizeBlendShapes>();
            shapes.PreserveMmdBlendShapes = draft.PreserveMmdBlendShapes;
            shapes.ExcludedAnimations = draft.AdditionalExcludedAnimations.Concat(draft.ExcludeAfkAnimations
                ? draft.AfkCandidates.Where(c => c.Selected).Select(c => c.Clip) : Enumerable.Empty<AnimationClip>()).Where(c => c != null).Distinct().ToList();
            Get<ALTOTraceAndOptimizeMaterials>().BakeLilToonMainTextures = draft.BakeLilToonMainTextures;
            var platformFilter = Get<ALTOOptimizationPlatformFilter>();
            platformFilter.enabled = draft.CommonMobileOnly;
            if (!draft.ParameterCompression) Remove(Get<ALTOParameterCompression>());
            else Get<ALTOParameterCompression>().TargetBitBudget = 256;
            if (!draft.ParameterLayout) Remove(Get<ALTOParameterLayout>());
            if (!draft.ParameterLayout && !draft.ParameterCompression) Remove(settings.transform.Find("Parameters").gameObject);
            if (!draft.IncludeCommon) Remove(settings.transform.Find("Common").gameObject);
            if (!draft.IncludeMobile) { Remove(settings.transform.Find("Mobile").gameObject); return; }

            var exclusions = Get<ALTOBuildExclusions>();
            exclusions.ExcludedObjects = draft.ExcludedObjects.Distinct().ToList();
            exclusions.ExcludedComponents = draft.ExcludedComponents.Distinct().ToList();
            exclusions.RemoveAudioSources = draft.RemoveAudioSources;
            var policy = Get<ALTOAnimatorParameterPolicy>();
            policy.FreezeMode = draft.FreezeMode;
            policy.BakeInitialVisibility = true;
            policy.PreserveParameters = Copy(draft.PreserveParameters);
            policy.FreezeParameters = draft.FrozenValueOverrides.Select(CopyConstant).ToList();
            var rendering = Get<ALTORenderingFilter>();
            rendering.ExcludedRendererTypes = draft.ExcludedRendererTypes;
            rendering.RemoveAdditionalMaterialPasses = draft.RemoveAdditionalMaterialPasses;
            rendering.BlendShapeExclusions = draft.BlendShapeExclusions.Select(v => new BlendShapeMeshExclusion
            { Renderer = v.Renderer, BlendShapes = new List<string>(v.BlendShapes), DisplacementThreshold = v.DisplacementThreshold }).ToList();
            var physics = Get<ALTOPhysBoneFilter>();
            physics.FilterMode = draft.PhysBoneFilterMode; physics.PhysBones = draft.PhysBones.Distinct().ToList();
            physics.BoneRoots = draft.PhysBoneRoots.Select(v => new PhysBoneRootSelection { Root = v.Root, IncludeChildren = v.IncludeChildren }).ToList();
            physics.RemoveUnreferencedColliders = draft.RemoveUnreferencedColliders;
            var contacts = Get<ALTOContactFilter>(); contacts.FilterMode = draft.ContactFilterMode;
            contacts.ObjectRoots = draft.ContactRoots.Select(v => new ContactObjectSelection { Root = v.Root, IncludeChildren = v.IncludeChildren }).ToList();
            var rendererSettings = settings.transform.Find("Mobile/Policies").GetComponent<ALTOMeshRendererSettingsOverride>();
            if (!draft.NormalizeRendererSettings) Remove(rendererSettings);
            var meshes = Get<ALTOMeshOptimizationPolicy>();
            meshes.AllowCoordinateSpaceChanges = draft.AllowCoordinateSpaceChanges;
            meshes.ConvertRigidMeshes = true;
            meshes.AllowMaterialSlotReordering = draft.AllowMaterialSlotReordering;
            var atlas = Get<ALTOMaterialAtlas>();
            if (!draft.CreateMaterialAtlas) Remove(atlas);
            else { atlas.MergeGroups = new List<MaterialAtlasGroup> { AtlasGroup(draft) }; atlas.ExistingAtlases = draft.ExistingAtlases; }
            var occlusion = Get<ALTORemoveOccludedMesh>();
            if (!draft.RemoveOccludedMesh) Remove(occlusion);
            else occlusion.Targets = draft.OcclusionTargets.Select(v => new OcclusionTarget
            { Renderer = v.Renderer, BlendShapes = v.RegionSelection == InstallerRegionSelection.AllBlendShapes
                ? Enumerable.Range(0, v.Renderer.sharedMesh.blendShapeCount).Select(v.Renderer.sharedMesh.GetBlendShapeName).ToList()
                : new List<string>(v.BlendShapes) }).ToList();
            var reduction = Get<ALTOMeshReduction>();
            if (!draft.ReduceMeshes) Remove(reduction);
            else
            {
                reduction.TriangleBudget = draft.TriangleBudget; reduction.PreserveBorderEdges = draft.PreserveBorderEdges;
                reduction.PreserveRenderers = draft.PreserveRenderers.Distinct().ToList();
            }
        }

        private static AnimatorParameterSelection Copy(AnimatorParameterSelection source) => new()
        {
            Names = new List<string>(source.Names), Prefixes = new List<string>(source.Prefixes),
            Sources = source.Sources.Select(s => new AnimatorParameterSource { Source = s.Source, IncludeChildren = s.IncludeChildren,
                SelectionMode = s.SelectionMode, ExcludedNames = new List<string>(s.ExcludedNames) }).ToList()
        };
        private static AnimatorParameterConstant CopyConstant(AnimatorParameterConstant source) => new()
        { Source = source.Source, IncludeChildren = source.IncludeChildren, Name = source.Name, UseDefaultValue = source.UseDefaultValue, Value = source.Value };

        // Used to author the bundled asset and by isolated tests. Runtime installation loads the asset above.
        internal static GameObject CreateTemplate()
        {
            var root = new GameObject(RootName);
            GameObject Child(string path)
            {
                var parent = root.transform;
                foreach (var name in path.Split('/'))
                {
                    var next = parent.Find(name);
                    if (next == null) { next = new GameObject(name).transform; next.SetParent(parent, false); }
                    parent = next;
                }
                return parent.gameObject;
            }
            var common = Child("Common");
            var platformFilter = common.AddComponent<ALTOOptimizationPlatformFilter>();
            platformFilter.ApplyOnPlatforms = Mobile; platformFilter.enabled = false;
            var anchors = Child("Common/Rendering").AddComponent<ALTOMeshRendererSettingsOverride>();
            anchors.OverrideProbeAnchor = true; anchors.ApplyOnPlatforms = OptimizationPlatforms.All;
            var optimization = Child("Common/Optimization");
            optimization.AddComponent<ALTOTraceAndOptimizeAnimator>(); optimization.AddComponent<ALTOTraceAndOptimizeObjects>();
            optimization.AddComponent<ALTOTraceAndOptimizeBlendShapes>(); optimization.AddComponent<ALTOTraceAndOptimizeMaterials>();
            optimization.AddComponent<ALTOTraceAndOptimizePhysBones>(); optimization.AddComponent<ALTOTraceAndOptimizeMeshes>();
            optimization.AddComponent<ALTOTraceAndOptimizeExpressions>();
            var compression = Child("Parameters").AddComponent<ALTOParameterCompression>();
            compression.TransmissionMode = ParameterTransmissionMode.LowLatency;
            compression.PacketHoldSeconds = .1f;
            Child("Parameters").AddComponent<ALTOParameterLayout>();
            Child("Mobile/Exclusions").AddComponent<ALTOBuildExclusions>().RemoveAudioSources = true;
            var parameterPolicy = Child("Mobile/Policies").AddComponent<ALTOAnimatorParameterPolicy>();
            parameterPolicy.FreezeMode = AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved;
            parameterPolicy.BakeInitialVisibility = true;
            var rendering = Child("Mobile/Policies").AddComponent<ALTOMeshRendererSettingsOverride>();
            rendering.OverrideLightProbeUsage = rendering.OverrideReflectionProbeUsage = true;
            rendering.OverrideShadowCasting = rendering.OverrideReceiveShadows = rendering.OverrideStaticShadowCaster = true;
            var meshPolicy = Child("Mobile/Policies").AddComponent<ALTOMeshOptimizationPolicy>();
            meshPolicy.AllowCoordinateSpaceChanges = meshPolicy.AllowMaterialSlotReordering = true;
            var filter = Child("Mobile/Filters").AddComponent<ALTORenderingFilter>();
            filter.RemoveAdditionalMaterialPasses = true;
            filter.ExcludedRendererTypes = RendererFilterTypes.ParticleSystems | RendererFilterTypes.Lines | RendererFilterTypes.Trails;
            Child("Mobile/Filters").AddComponent<ALTOPhysBoneFilter>(); Child("Mobile/Filters").AddComponent<ALTOContactFilter>();
            var atlas = Child("Mobile/Materials").AddComponent<ALTOMaterialAtlas>();
            atlas.ExistingAtlases = ExistingAtlasPolicy.ReplaceFullyCovered;
            atlas.MergeGroups[0].ReferenceMaterial = AssetDatabase.LoadAssetAtPath<Material>(ALTOInstallerWindow.MobileMaterialPath);
            Child("Mobile/Geometry").AddComponent<ALTORemoveOccludedMesh>();
            var reduction = Child("Mobile/Geometry").AddComponent<ALTOMeshReduction>();
            reduction.TriangleBudget = 60000; reduction.PreserveBorderEdges = false;
            foreach (var component in root.GetComponentsInChildren<ALTOPlatformOptimizationComponent>(true))
                component.ApplyOnPlatforms = component == anchors ? OptimizationPlatforms.All : Mobile;
            return root;
        }
    }
}
