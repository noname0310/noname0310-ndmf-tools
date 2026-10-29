using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ParameterPolicy;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Installer
{
    internal sealed partial class ALTOInstallerWindow : EditorWindow
    {
        internal const string MobileMaterialPath = "Packages/io.github.noname0310.alto/Presets/Mobile Toon Standard.mat";
        private static readonly string[] Tabs = { "Setup", "Parameters", "Features", "Meshes", "Materials", "Review" };
        private static readonly int[] AtlasSizes = { 256, 512, 1024, 2048, 4096, 8192 };
        private static readonly string[] AtlasSizeLabels = AtlasSizes.Select(n => n + " x " + n).ToArray();
        [SerializeField] private InstallerDraft draft = new();
        [SerializeField] private int tab;
        [SerializeField] private Vector2 scroll;
        [SerializeField] private bool showHierarchy;
        [SerializeField] private bool advancedMode;
        [SerializeField] private int simpleTab;
        [SerializeField] private List<MeshRegionPresetMatch> meshPresetMatches = new();
        private int existingComponents;
        private List<string> validation;
        private string operationError;
        private bool summaryDirty;
        private GUIStyle titleStyle, frameStyle, headingStyle;

        private string CommonPlatforms => !draft.IncludeCommon ? "Disabled in Setup" :
            draft.CommonMobileOnly ? "Android + iOS" : "PC + Android + iOS";
        private string MobilePlatforms => draft.IncludeMobile ? "Android + iOS" : "Disabled in Setup";

        [MenuItem("Tools/Noname/ALTO/Installer")]
        internal static void Open()
        {
            var window = GetWindow<ALTOInstallerWindow>("ALTO Installer");
            var selected = Selection.activeGameObject;
            var avatar = selected != null ? selected.GetComponentInParent<VRCAvatarDescriptor>(true) : null;
            if (avatar != null && window.draft.Avatar != avatar) window.SetAvatar(avatar);
            window.Show();
        }

        private void OnEnable()
        {
            minSize = new Vector2(620, 560);
            draft ??= new InstallerDraft();
            validation = null;
            operationError = null;
            if (draft.ReferenceMaterial == null) draft.ReferenceMaterial = AssetDatabase.LoadAssetAtPath<Material>(MobileMaterialPath);
            RefreshAvatarSummary();
            if (draft.Avatar != null && (!draft.AfkScanComplete || draft.AfkScannedAvatar != draft.Avatar))
                RefreshAfkAnimations();
            EditorApplication.hierarchyChanged += Invalidate;
            EditorApplication.projectChanged += Invalidate;
            Undo.undoRedoPerformed += Invalidate;
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= Invalidate;
            EditorApplication.projectChanged -= Invalidate;
            Undo.undoRedoPerformed -= Invalidate;
        }

        private void Invalidate() { validation = null; summaryDirty = true; physBoneChoices = null; Repaint(); }
        private List<string> Validation() => validation ??= AvatarInstaller.Validate(draft);

        private void SetAvatar(VRCAvatarDescriptor avatar)
        {
            // Clear references tied to the previous avatar while retaining platform and quality choices.
            draft.Avatar = avatar;
            draft.PreserveParameters = new AnimatorParameterSelection();
            draft.FrozenValueOverrides.Clear();
            draft.ExcludedObjects.Clear(); draft.ExcludedComponents.Clear();
            draft.PhysBones.Clear(); draft.PhysBoneRoots.Clear(); draft.ContactRoots.Clear();
            draft.OcclusionTargets.Clear(); draft.BlendShapeExclusions.Clear();
            draft.AdditionalExcludedAnimations.Clear(); draft.PreserveRenderers.Clear();
            draft.AfkCandidates.Clear(); draft.AfkDiagnostics.Clear(); draft.AfkScanComplete = false; draft.AfkScannedAvatar = null;
            AddDescriptorRenderers();
            var body = BodyMeshDiscovery.Find(avatar);
            if (body != null) draft.OcclusionTargets.Add(new InstallerOcclusionTarget
            { Renderer = body, RegionSelection = InstallerRegionSelection.AllBlendShapes, BlendShapes = BlendShapeNames(body) });
            KnownFeatureDiscovery.AddSuggestions(draft);
            RefreshMeshPresets(true);
            RefreshPhysBoneChoices();
            validation = null; operationError = null;
            RefreshAvatarSummary();
            RefreshAfkAnimations();
        }

        private void RefreshAvatarSummary() => existingComponents = draft.Avatar == null ? 0 :
            AvatarInstaller.ExistingSettings(draft.Avatar).Length;

        private void AddDescriptorRenderers()
        {
            if (draft.Avatar == null) return;
            foreach (var renderer in new[] { draft.Avatar.VisemeSkinnedMesh, draft.Avatar.customEyeLookSettings.eyelidsSkinnedMesh })
                if (renderer != null && renderer.GetComponentInParent<VRCAvatarDescriptor>(true) == draft.Avatar &&
                    !BlendShapeOptimization.ALTOPlugin.IsEditorOnly(renderer.transform) && !draft.PreserveRenderers.Contains(renderer))
                    draft.PreserveRenderers.Add(renderer);
        }

        private void OnGUI()
        {
            if (summaryDirty) { summaryDirty = false; RefreshAvatarSummary(); }
            EditorGUI.BeginChangeCheck();
            titleStyle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 20 };
            using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(12, 12, 10, 10) }))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("ALTO Installer", titleStyle);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label("AVATAR SETUP", EditorStyles.miniLabel);
                }
                EditorGUILayout.LabelField("Avatar Link-Time Optimizer", EditorStyles.miniLabel);
                EditorGUILayout.HelpBox("Choose what to keep, then install from Review. Optimization runs on build copies of your avatar.", MessageType.Info);
                var avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField("Avatar", draft.Avatar, typeof(VRCAvatarDescriptor), true);
                if (avatar != draft.Avatar) SetAvatar(avatar);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Setup mode", GUILayout.Width(90));
                    var nextMode = GUILayout.Toolbar(advancedMode ? 1 : 0, new[] { "Simple", "Advanced" }, GUILayout.Width(220)) == 1;
                    if (nextMode != advancedMode) { advancedMode = nextMode; scroll = Vector2.zero; GUI.FocusControl(null); }
                    GUILayout.FlexibleSpace();
                }
                var tabs = advancedMode ? Tabs : SimpleTabs;
                int currentTab = Mathf.Clamp(advancedMode ? tab : simpleTab, 0, tabs.Length - 1);
                int nextTab = GUILayout.Toolbar(currentTab, tabs, GUILayout.Height(26));
                if (nextTab != currentTab) { currentTab = nextTab; scroll = Vector2.zero; GUI.FocusControl(null); }
                if (advancedMode) tab = currentTab; else simpleTab = currentTab;
                EditorGUILayout.Space(6);
                scroll = EditorGUILayout.BeginScrollView(scroll);
                float previousLabelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = Mathf.Clamp(position.width * 0.43f, 235, 330);
                try
                {
                    if (!advancedMode) DrawSimpleTab(currentTab);
                    else switch (currentTab)
                    {
                        case 0: DrawSetup(); break;
                        case 1: DrawParameters(); break;
                        case 2: DrawFilters(); break;
                        case 3: DrawGeometry(); break;
                        case 4: DrawMaterials(); break;
                        case 5: DrawReview(); break;
                    }
                }
                finally { EditorGUIUtility.labelWidth = previousLabelWidth; }
                EditorGUILayout.EndScrollView();
                if (!string.IsNullOrWhiteSpace(operationError)) EditorGUILayout.HelpBox(operationError, MessageType.Error);
                EditorGUILayout.Space(8);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(currentTab == 0))
                        if (GUILayout.Button("Back", GUILayout.Width(80))) { currentTab--; scroll = Vector2.zero; }
                    GUILayout.FlexibleSpace();
                    if (currentTab < tabs.Length - 1)
                    {
                        if (GUILayout.Button("Next", GUILayout.Width(100))) { currentTab++; scroll = Vector2.zero; }
                    }
                    else using (new EditorGUI.DisabledScope(Validation().Count != 0 || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling))
                        if (GUILayout.Button("Install on Avatar", GUILayout.Width(180)))
                        {
                            try { AvatarInstaller.Install(draft); operationError = null; Invalidate(); }
                            catch (Exception exception) { operationError = exception.Message; }
                        }
                }
                if (advancedMode) tab = currentTab; else simpleTab = currentTab;
            }
            if (EditorGUI.EndChangeCheck()) validation = null;
        }

        private void DrawSetup()
        {
            Frame("Common", "General avatar optimization", CommonPlatforms, true, () =>
            {
                draft.IncludeCommon = EditorGUILayout.ToggleLeft("Enable general optimization", draft.IncludeCommon, EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(!draft.IncludeCommon))
                {
                    draft.CommonMobileOnly = EditorGUILayout.Toggle(new GUIContent("Optimize Mobile Only",
                        "Apply general optimization to Android and iOS. When off, also optimize PC builds. Parameter synchronization is configured separately."), draft.CommonMobileOnly);
                    Text("Remove unused animation logic, mesh data, materials and physics calculations.");
                    draft.NormalizeProbeAnchors = EditorGUILayout.Toggle(new GUIContent("Use consistent lighting across meshes",
                        "Sample lighting from a shared probe anchor. This changes meshes that intentionally use different anchors."), draft.NormalizeProbeAnchors);
                    draft.PreserveMmdBlendShapes = EditorGUILayout.Toggle("Keep expressions used by MMD worlds", draft.PreserveMmdBlendShapes);
                    EditorGUILayout.Space(12);
                    Section("Expressions allowed to change", "Leave selected animations out of BlendShape optimization analysis to allow more shapes to be frozen. Their expressions may look different afterwards.");
                    draft.ExcludeAfkAnimations = EditorGUILayout.ToggleLeft(new GUIContent("Allow AFK expressions to change",
                        "Review the candidates detected when selecting the avatar. Discovery follows VRChat's built-in AFK parameter."), draft.ExcludeAfkAnimations);
                    if (draft.ExcludeAfkAnimations)
                        DrawAfkAnimations();
                    Property("AdditionalExcludedAnimations", "Other animations allowed to change");
                }
            });
            Frame("Mobile", "Additional simplification for mobile avatars", MobilePlatforms, true, () =>
            {
                draft.IncludeMobile = EditorGUILayout.ToggleLeft("Simplify the mobile avatar", draft.IncludeMobile, EditorStyles.boldLabel);
                Text("Choose which controls, effects and physics to keep, combine materials, remove hidden body regions and reduce triangles. These choices can change appearance and behavior.");
                if (draft.IncludeMobile && !draft.IncludeCommon)
                    EditorGUILayout.HelpBox("Enable general optimization above for the shared cleanup and mesh-merging passes. Use Optimize Mobile Only to keep these passes off on PC.", MessageType.Warning);
            });
            if (existingComponents > 0)
            {
                EditorGUILayout.HelpBox("This avatar already contains ALTO settings. Installation is disabled to avoid duplicates or overwriting your configuration.", MessageType.Info);
                if (GUILayout.Button("Select Existing Settings"))
                    Selection.activeGameObject = AvatarInstaller.ExistingSettings(draft.Avatar).FirstOrDefault()?.gameObject;
            }
        }

        private void RefreshAfkAnimations()
        {
            if (draft.Avatar == null || EditorApplication.isPlayingOrWillChangePlaymode) return;
            try
            {
                var result = AfkAnimationDiscovery.Find(draft.Avatar.gameObject);
                if (draft.AfkScannedAvatar == draft.Avatar)
                    foreach (var candidate in result.Candidates)
                    {
                        var previous = draft.AfkCandidates.FirstOrDefault(c => c.Clip == candidate.Clip);
                        if (previous != null && previous.SharedOutsideAfk == candidate.SharedOutsideAfk) candidate.Selected = previous.Selected;
                    }
                draft.AfkCandidates = result.Candidates; draft.AfkDiagnostics = result.Diagnostics;
                draft.AfkScannedAvatar = draft.Avatar; draft.AfkScanComplete = true;
                operationError = null; validation = null;
            }
            catch (Exception exception)
            {
                draft.AfkScanComplete = false; operationError = exception.Message; validation = null;
            }
        }

        private void DrawAfkAnimations()
        {
            Text("Candidates are detected when you select an avatar. Review which AFK expressions may change. Refresh after changing the avatar's animation setup.");
            using (new EditorGUI.DisabledScope(draft.Avatar == null || EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button("Refresh AFK Animations"))
                {
                    RefreshAfkAnimations();
                }
            foreach (var candidate in draft.AfkCandidates)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    candidate.Selected = EditorGUILayout.Toggle(candidate.Selected, GUILayout.Width(18));
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.ObjectField(new GUIContent(candidate.Clip != null ? candidate.Clip.name : "Missing clip", candidate.Evidence), candidate.Clip, typeof(AnimationClip), false);
                }
                if (candidate.SharedOutsideAfk) Text("Also used outside AFK. Selecting it allows those expressions to change too.");
            }
            if (draft.AfkScanComplete && draft.AfkCandidates.Count == 0)
                Text("No AFK-dependent clips with BlendShape tracks were identified.");
            foreach (var message in draft.AfkDiagnostics) EditorGUILayout.HelpBox(message, MessageType.Warning);
            Text("Scans referenced controllers, including Override Controllers. Controllers generated only during builds, indirect parameter calculations and unsupported Blend Trees may need manual clip selection.");
        }

        private void DrawParameters()
        {
            Frame("Cross-platform", "Keep parameter synchronization compatible", "PC + Android + iOS", true, () =>
            {
                Text("These options also apply to PC when Optimize Mobile Only is enabled. A PC upload records the parameter layout used by mobile uploads.");
                draft.ParameterLayout = EditorGUILayout.Toggle("Match mobile parameters to PC", draft.ParameterLayout);
                draft.ParameterCompression = EditorGUILayout.Toggle("Fit synced parameters within 256 bits", draft.ParameterCompression);
                if (draft.ParameterCompression && !draft.ParameterLayout)
                    EditorGUILayout.HelpBox("Enable matching mobile parameters to PC to use automatic compression.", MessageType.Warning);
                if (draft.ParameterCompression)
                    Text("Compress automatically when needed. Sharing network slots can make some values sync less frequently.");
            });
            Frame("Mobile", "Choose which controls remain adjustable", MobilePlatforms, draft.IncludeMobile, () =>
            {
                draft.FreezeMode = Choice("Mobile control behavior", draft.FreezeMode,
                    (AnimatorParameterFreezeMode.SelectedParameters, "Fix only the parameter values listed below"),
                    (AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved, "Fix all inputs except those kept below"));
                Text("Fixed inputs stop responding to menus, OSC and other input sources. Keep facial tracking and other controls you still want to use.");
                DrawParameterSources();
                Property("PreserveParameters.Names", "Also keep these parameter names");
                Property("PreserveParameters.Prefixes", "Also keep names starting with");
                Property("FrozenValueOverrides", draft.FreezeMode == AnimatorParameterFreezeMode.SelectedParameters ? "Parameter values to fix" : "Use these values instead of defaults");
                if (draft.FreezeMode == AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved &&
                    draft.PreserveParameters.Sources.All(s => s.Source == null) &&
                    draft.PreserveParameters.Names.Count == 0 && draft.PreserveParameters.Prefixes.Count == 0)
                    EditorGUILayout.HelpBox("No inputs are selected to keep working. Expression and feature controls will use their build defaults; VRChat's built-in inputs remain active.", MessageType.Warning);
            });
        }

        private void DrawParameterSources(bool details = true)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Controls to keep working", EditorStyles.boldLabel);
            Text("Add a feature object, Animator Controller or Expression Parameters asset to keep its parameters adjustable. An entire Expression Parameters asset may also keep clothing and feature toggles.");
            for (int i = 0; i < draft.PreserveParameters.Sources.Count; i++)
            {
                var source = draft.PreserveParameters.Sources[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        source.Source = EditorGUILayout.ObjectField(new GUIContent("Feature object or parameter asset",
                            "Accepts a GameObject, Animator, Animator Controller, component, or Expression Parameters asset."), source.Source, typeof(Object), true);
                        if (GUILayout.Button("Remove", GUILayout.Width(68))) { draft.PreserveParameters.Sources.RemoveAt(i); break; }
                    }
                    if (source.Source != null && !ParameterSourceResolver.IsSupported(source.Source))
                        EditorGUILayout.HelpBox("Choose a supported parameter source.", MessageType.Error);
                    if (details)
                    {
                        if (source.Source is GameObject) source.IncludeChildren = EditorGUILayout.Toggle("Include children", source.IncludeChildren);
                        if (source.Source is GameObject || source.Source is Component || source.Source is RuntimeAnimatorController)
                            source.SelectionMode = Choice("Parameters to keep", source.SelectionMode,
                                (AnimatorParameterSelectionMode.Declared, "All parameters declared by this source"),
                                (AnimatorParameterSelectionMode.Referenced, "Only parameters used by its logic"));
                        Property("PreserveParameters.Sources.Array.data[" + i + "].ExcludedNames", "Do not keep these parameter names");
                    }
                    else if (!source.IncludeChildren || source.SelectionMode != AnimatorParameterSelectionMode.Declared || source.ExcludedNames.Count > 0)
                        Text("Custom parameter selection is configured for this source in Advanced > Parameters.");
                }
            }
            if (GUILayout.Button("Add Feature or Parameter Asset")) draft.PreserveParameters.Sources.Add(new AnimatorParameterSource());
        }

        private void DrawFilters()
        {
            Frame("Mobile", "Choose which features to remove", MobilePlatforms, draft.IncludeMobile, () =>
            {
                Section("Optional avatar features", "Remove selected feature objects or components before they generate content for the avatar.");
                DrawFeatureExclusions();
                draft.RemoveAudioSources = EditorGUILayout.Toggle(new GUIContent("Remove existing avatar sounds",
                    "Remove existing Audio Sources and their unused audio effects. Sounds generated later by other tools are not included."), draft.RemoveAudioSources);
                EditorGUILayout.Space(12);
                Section("Visual effects", "Remove selected effects, including effects created during the build.");
                EditorGUILayout.LabelField("Effects to remove", EditorStyles.boldLabel);
                using (new EditorGUI.IndentLevelScope())
                {
                    RendererTypeToggle("Particle effects", RendererFilterTypes.ParticleSystems);
                    RendererTypeToggle("Line effects", RendererFilterTypes.Lines);
                    RendererTypeToggle("Trail effects", RendererFilterTypes.Trails);
                }
                draft.RemoveAdditionalMaterialPasses = EditorGUILayout.Toggle(new GUIContent("Remove extra material overlays",
                    "Remove extra material slots that draw an existing submesh again, such as additional overlay effects."), draft.RemoveAdditionalMaterialPasses);
                DrawMeshPresets();
                Property("BlendShapeExclusions", "Always remove these mesh regions");
                Text("Use BlendShapes to identify regions to delete even when visible, such as face overlays. Hidden-body removal is configured separately in Meshes.");
                EditorGUILayout.Space(12);
                Section("Bone physics (PhysBones)", "Choose which parts keep bone physics. Select existing PhysBones or a bone hierarchy to also match generated ones.");
                draft.PhysBoneFilterMode = Choice("Bone physics to keep", draft.PhysBoneFilterMode,
                    (PhysBoneFilterMode.KeepListed, "Keep only the selected PhysBones"),
                    (PhysBoneFilterMode.RemoveListed, "Keep all except the selected PhysBones"));
                Property("PhysBones", "Selected PhysBones"); Property("PhysBoneRoots", "Select by bone hierarchy");
                draft.RemoveUnreferencedColliders = EditorGUILayout.Toggle(new GUIContent("Remove unused collision shapes",
                    "Remove PhysBone Colliders only when no remaining consumer or preservation rule needs them."), draft.RemoveUnreferencedColliders);
                if (draft.PhysBoneFilterMode == PhysBoneFilterMode.KeepListed && draft.PhysBones.Count == 0 && draft.PhysBoneRoots.Count == 0)
                    EditorGUILayout.HelpBox("No PhysBones are selected to keep. All avatar PhysBones will be removed on mobile.", MessageType.Info);
                EditorGUILayout.Space(12);
                Section("Touch interactions (Contacts)", "Choose which objects keep Contact senders and receivers, including those created during the build.");
                draft.ContactFilterMode = Choice("Interactions to keep", draft.ContactFilterMode,
                    (ContactFilterMode.KeepListed, "Keep only Contacts on selected objects"),
                    (ContactFilterMode.RemoveListed, "Keep all except Contacts on selected objects"));
                Property("ContactRoots", "Selected interaction objects");
                if (draft.ContactFilterMode == ContactFilterMode.KeepListed && draft.ContactRoots.Count == 0)
                    Text("No interaction objects are selected to keep. All Contact senders and receivers will be removed on mobile.");
            });
        }

        private void DrawGeometry()
        {
            Frame("Mobile", "Reduce mesh complexity", MobilePlatforms, draft.IncludeMobile, () =>
            {
                draft.RemoveOccludedMesh = EditorGUILayout.ToggleLeft("Remove hidden body regions", draft.RemoveOccludedMesh, EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(!draft.RemoveOccludedMesh))
                {
                    Text("A body mesh is suggested when you select an avatar, using names, materials and BlendShapes. Review this list. Each BlendShape defines a candidate region; it is removed only when hidden in all tested views and poses. All BlendShapes are selected by default.");
                    DrawBodyMeshes();
                }
                EditorGUILayout.Space(12);
                draft.ReduceMeshes = EditorGUILayout.ToggleLeft("Reduce triangle count", draft.ReduceMeshes, EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(!draft.ReduceMeshes))
                {
                    draft.TriangleBudget = Mathf.Max(0, EditorGUILayout.IntField("Target total triangles", draft.TriangleBudget));
                    draft.PreserveBorderEdges = EditorGUILayout.Toggle(new GUIContent("Keep open mesh boundaries",
                        "Preserve border edges during reduction. This can limit how many triangles can be removed."), draft.PreserveBorderEdges);
                    Property("PreserveRenderers", "Meshes to keep at full detail");
                    Text("The avatar's lip-sync and eyelid meshes are added when you select an avatar. Edit this list to change which meshes keep their detail. If the face and body share one mesh, the whole mesh is kept at full detail. The triangle target includes all meshes, including those kept at full detail.");
                }
                EditorGUILayout.Space(12);
                Section("Combine more meshes", "Allow these changes to combine more meshes and reduce draw calls. Independently toggled meshes may still remain separate.");
                draft.NormalizeRendererSettings = EditorGUILayout.Toggle("Use consistent lighting and shadows", draft.NormalizeRendererSettings);
                draft.AllowCoordinateSpaceChanges = EditorGUILayout.Toggle(new GUIContent("Combine meshes with different origins",
                    "Allow local coordinate changes and conversion of rigid meshes to skinned meshes. Object-space shader effects may change."), draft.AllowCoordinateSpaceChanges);
                draft.AllowMaterialSlotReordering = EditorGUILayout.Toggle(new GUIContent("Reorder materials for fewer draw calls",
                    "Allow material slots to be reordered when merging compatible meshes. Effects that depend on draw order may change."), draft.AllowMaterialSlotReordering);
            });
        }

        private void DrawBodyMeshes()
        {
            for (int i = 0; i < draft.OcclusionTargets.Count; i++)
            {
                var target = draft.OcclusionTargets[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var renderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Body mesh", target.Renderer, typeof(SkinnedMeshRenderer), true);
                        if (renderer != target.Renderer)
                        {
                            target.Renderer = renderer;
                            target.BlendShapes = BlendShapeNames(renderer);
                        }
                        if (GUILayout.Button("Remove", GUILayout.Width(68))) { draft.OcclusionTargets.RemoveAt(i); break; }
                    }
                    target.RegionSelection = Choice("Regions to check", target.RegionSelection,
                        (InstallerRegionSelection.AllBlendShapes, "All BlendShape regions"),
                        (InstallerRegionSelection.SelectedBlendShapes, "Choose BlendShape regions"));
                    var mesh = target.Renderer != null ? target.Renderer.sharedMesh : null;
                    if (target.RegionSelection == InstallerRegionSelection.AllBlendShapes)
                        Text((mesh != null ? mesh.blendShapeCount : 0) + " BlendShape regions will be checked for visibility.");
                    else
                    {
                        Property("OcclusionTargets.Array.data[" + i + "].BlendShapes", "Selected BlendShapes");
                        using (new EditorGUI.DisabledScope(mesh == null))
                            if (GUILayout.Button("Choose Regions to Check..."))
                                PopupWindow.Show(GUILayoutUtility.GetLastRect(), new BlendShapeRegionSelector(this, "draft.OcclusionTargets.Array.data[" + i + "]"));
                    }
                    if (target.Renderer != null && draft.Avatar != null && !target.Renderer.transform.IsChildOf(draft.Avatar.transform))
                        EditorGUILayout.HelpBox("Choose a mesh inside the selected avatar.", MessageType.Error);
                }
            }
            if (GUILayout.Button("Add Body Mesh")) draft.OcclusionTargets.Add(new InstallerOcclusionTarget());
            if (draft.OcclusionTargets.Count == 0) Text("Choose a body mesh to use hidden-region removal.");
        }

        private static List<string> BlendShapeNames(SkinnedMeshRenderer renderer) => renderer == null || renderer.sharedMesh == null ? new List<string>() :
            Enumerable.Range(0, renderer.sharedMesh.blendShapeCount).Select(renderer.sharedMesh.GetBlendShapeName).ToList();

        private void DrawMaterials()
        {
            Frame("Common", "Combine lilToon colors and texture layers", CommonPlatforms, draft.IncludeCommon, () =>
            {
                draft.BakeLilToonMainTextures = EditorGUILayout.Toggle("Bake lilToon colors and layers", draft.BakeLilToonMainTextures);
                Text("Combine supported static color corrections and secondary textures into the main texture. Animated or unsupported settings are left for the shader. Texture filtering, mipmaps and Safety Fallback appearance may differ.");
            });
            Frame("Mobile", "Combine materials and textures", MobilePlatforms, draft.IncludeMobile, () =>
            {
                draft.CreateMaterialAtlas = EditorGUILayout.ToggleLeft("Merge materials into a texture atlas", draft.CreateMaterialAtlas, EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(!draft.CreateMaterialAtlas))
                {
                    draft.ReferenceMaterial = (Material)EditorGUILayout.ObjectField(new GUIContent("Output material preset",
                        "The merged material copies this material's shader and surface settings. Input materials contribute their main textures."), draft.ReferenceMaterial, typeof(Material), false);
                    if (GUILayout.Button("Use Mobile Toon Standard Preset"))
                        draft.ReferenceMaterial = AssetDatabase.LoadAssetAtPath<Material>(MobileMaterialPath);
                    draft.AtlasSize = EditorGUILayout.IntPopup("Combined texture resolution", draft.AtlasSize, AtlasSizeLabels, AtlasSizes);
                    draft.AtlasCompression = (AtlasCompression)EditorGUILayout.EnumPopup("Texture compression", draft.AtlasCompression);
                    draft.ExistingAtlases = Choice("Existing atlas setups", draft.ExistingAtlases,
                        (ExistingAtlasPolicy.Preserve, "Keep their existing settings and materials"),
                        (ExistingAtlasPolicy.ReplaceFullyCovered, "Replace setups fully covered by this atlas"),
                        (ExistingAtlasPolicy.Chain, "Repack their completed textures"));
                    Text("Merge main textures across all avatar meshes. Other surface settings come from the output material preset.");
                    Text("The bundled preset uses Mobile Toon Standard with white tint and both sides visible.");
                    if (draft.ExistingAtlases == ExistingAtlasPolicy.Chain)
                        Text("Repacking an existing atlas adds another texture sampling step, which can reduce image quality.");
                    if (draft.ReferenceMaterial == null) EditorGUILayout.HelpBox("Choose the material preset for the merged result.", MessageType.Warning);
                }
            });
        }

        private void DrawReview()
        {
            Frame("Common", "General optimization summary", CommonPlatforms, true, () =>
            {
                if (!draft.IncludeCommon) { Text("General optimization is not included."); return; }
                Text("Remove unused animation logic, mesh data, materials and physics calculations.");
                Text("Consistent lighting across meshes: " + YesNo(draft.NormalizeProbeAnchors));
                Text("Keep expressions used by MMD worlds: " + YesNo(draft.PreserveMmdBlendShapes));
                Text("Allow AFK expressions to change: " + YesNo(draft.ExcludeAfkAnimations));
                Text("Bake lilToon colors and layers: " + YesNo(draft.BakeLilToonMainTextures));
            });
            Frame("Mobile", "Mobile simplification summary", MobilePlatforms, true, () =>
            {
                if (!draft.IncludeMobile) { Text("Additional mobile simplification is not included."); return; }
                Text("Remove " + draft.ExcludedObjects.Count + " feature objects and " + draft.ExcludedComponents.Count + " individual components.");
                Text(draft.FreezeMode == AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved
                    ? "Fix input values except those selected to keep working."
                    : "Fix only the parameter values explicitly listed.");
                Text(draft.PhysBoneFilterMode == PhysBoneFilterMode.KeepListed ? "Keep only selected PhysBones." : "Keep all except selected PhysBones.");
                Text(draft.PhysBones.Count + " individual PhysBones and " + draft.PhysBoneRoots.Count + " bone hierarchies selected.");
                if (draft.PhysBoneFilterMode == PhysBoneFilterMode.KeepListed && draft.PhysBones.Count == 0 && draft.PhysBoneRoots.Count == 0)
                    EditorGUILayout.HelpBox("No PhysBones are selected. All PhysBones will be removed on mobile. Choose the parts you want to keep in " + (advancedMode ? "Features." : "Physics."), MessageType.Warning);
                Text(draft.ContactFilterMode == ContactFilterMode.KeepListed ? "Keep Contacts only on selected objects." : "Keep Contacts except on selected objects.");
                Text(draft.CreateMaterialAtlas ? "Combine main textures into a " + draft.AtlasSize + " x " + draft.AtlasSize + " atlas." : "Texture atlas creation: disabled.");
                Text(draft.RemoveOccludedMesh ? "Check " + draft.OcclusionTargets.Count + " body meshes for hidden regions." : "Keep hidden body regions.");
                Text(draft.ReduceMeshes ? "Target " + draft.TriangleBudget.ToString("N0") + " total triangles, including meshes kept at full detail." : "Triangle reduction: disabled.");
            });
            Frame("Cross-platform", "Parameter synchronization summary",
                draft.ParameterLayout || draft.ParameterCompression ? "PC + Android + iOS" : "Not included", true, () =>
            {
                Text("Match mobile parameters to PC: " + YesNo(draft.ParameterLayout));
                Text("Fit synced parameters within 256 bits: " + YesNo(draft.ParameterCompression));
                if (draft.CommonMobileOnly && (draft.ParameterLayout || draft.ParameterCompression))
                    Text("These options also apply to PC. PC uploads establish the parameter layout used by mobile avatars.");
            });
            Section("Before installation", "Install adds a prefab instance with your settings as overrides and supports Undo. Final appearance, resource counts and compatibility are evaluated during the build.");
            foreach (var message in Validation()) EditorGUILayout.HelpBox(message, MessageType.Warning);
            if (draft.IncludeCommon && draft.ExcludeAfkAnimations && draft.AfkScanComplete)
                Text(draft.AfkCandidates.Count(c => c.Selected) + " reviewed AFK clips will be excluded from BlendShape optimization analysis.");
            EditorGUILayout.Space(8);
            showHierarchy = EditorGUILayout.Foldout(showHierarchy, "Show planned settings objects", true);
            if (showHierarchy) DrawPlannedHierarchy();
        }

        private void DrawPlannedHierarchy()
        {
            Text("Settings will cover the selected avatar. Object names organize the settings; build order is handled automatically.");
            EditorGUILayout.LabelField("LinkTimeOptimizationSettings", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                if (draft.IncludeCommon)
                {
                    EditorGUILayout.LabelField("Common  [" + CommonPlatforms + "]", EditorStyles.boldLabel);
                    using (new EditorGUI.IndentLevelScope())
                    {
                        Text("Rendering / consistent lighting across meshes");
                        Text("Optimization / general avatar optimization");
                    }
                }
                if (draft.ParameterLayout || draft.ParameterCompression)
                {
                    EditorGUILayout.LabelField("Parameters  [Cross-platform]", EditorStyles.boldLabel);
                    using (new EditorGUI.IndentLevelScope())
                        Text("Match PC parameters: " + YesNo(draft.ParameterLayout) + "; automatic compression: " + YesNo(draft.ParameterCompression));
                }
                if (draft.IncludeMobile)
                {
                    EditorGUILayout.LabelField("Mobile  [Android + iOS]", EditorStyles.boldLabel);
                    using (new EditorGUI.IndentLevelScope())
                    {
                        Text("Exclusions / " + draft.ExcludedObjects.Count + " objects, " + draft.ExcludedComponents.Count + " components");
                        Text("Policies / control behavior and mesh combination");
                        Text("Filters / visual effects, bone physics and touch interactions");
                        if (draft.CreateMaterialAtlas) Text("Materials / " + draft.AtlasSize + " x " + draft.AtlasSize + " atlas");
                        if (draft.RemoveOccludedMesh || draft.ReduceMeshes)
                            Text("Geometry / " + (draft.RemoveOccludedMesh ? draft.OcclusionTargets.Count + " body meshes to check" : "keep hidden regions") +
                                (draft.ReduceMeshes ? "; target " + draft.TriangleBudget.ToString("N0") + " triangles" : "; keep triangle count"));
                    }
                }
            }
        }

        private void Property(string path, string label)
        {
            using var serialized = new SerializedObject(this);
            serialized.Update();
            EditorGUILayout.PropertyField(serialized.FindProperty("draft." + path), new GUIContent(label), true);
            serialized.ApplyModifiedProperties();
        }

        private static string YesNo(bool value) => value ? "enabled" : "disabled";
        private static T Choice<T>(string label, T value, params (T Value, string Label)[] options) where T : Enum
        {
            int current = Array.FindIndex(options, option => EqualityComparer<T>.Default.Equals(option.Value, value));
            int selected = EditorGUILayout.Popup(label, current, options.Select(option => option.Label).ToArray());
            return selected >= 0 && selected < options.Length ? options[selected].Value : value;
        }

        private void Frame(string group, string purpose, string platforms, bool enabled, Action draw)
        {
            frameStyle ??= new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 10, 10),
                margin = new RectOffset(0, 0, 0, 10)
            };
            headingStyle ??= new GUIStyle(EditorStyles.boldLabel) { wordWrap = true };
            using (new EditorGUILayout.VerticalScope(frameStyle))
            {
                EditorGUILayout.LabelField(group + " - " + purpose, headingStyle);
                EditorGUILayout.LabelField("Applies to: " + platforms, headingStyle);
                EditorGUILayout.Space(6);
                using (new EditorGUI.DisabledScope(!enabled)) draw();
            }
            EditorGUILayout.Space(6);
        }
        private void RendererTypeToggle(string label, RendererFilterTypes flag)
        {
            bool selected = (draft.ExcludedRendererTypes & flag) != 0;
            if (EditorGUILayout.Toggle(label, selected)) draft.ExcludedRendererTypes |= flag;
            else draft.ExcludedRendererTypes &= ~flag;
        }
        private static void Text(string text) => EditorGUILayout.LabelField(text, EditorStyles.wordWrappedLabel);
        private static void Section(string title, string description)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            Text(description);
            EditorGUILayout.Space(4);
        }
    }
}
