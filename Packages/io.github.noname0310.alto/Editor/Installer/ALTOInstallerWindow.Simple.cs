using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Meshes;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.Installer
{
    internal sealed partial class ALTOInstallerWindow
    {
        private static readonly string[] SimpleTabs = { "Setup", "Features", "Physics", "Meshes", "Review" };
        [SerializeField] private string physBoneSearch = "";
        [SerializeField] private Vector2 physBoneScroll;
        [SerializeField] private bool hideInactivePhysBones = true;
        private VRCPhysBone[] physBoneChoices;

        private void DrawSimpleTab(int index)
        {
            switch (index)
            {
                case 0: DrawSimpleSetup(); break;
                case 1: DrawSimpleFeatures(); break;
                case 2: DrawSimplePhysics(); break;
                case 3: DrawSimpleMeshes(); break;
                case 4: DrawReview(); break;
            }
        }

        private void DrawSimpleSetup()
        {
            Frame("Common", "Optimization and appearance", CommonPlatforms, true, () =>
            {
                draft.IncludeCommon = EditorGUILayout.ToggleLeft("Enable general optimization", draft.IncludeCommon, EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(!draft.IncludeCommon))
                {
                    draft.CommonMobileOnly = EditorGUILayout.Toggle("Optimize Mobile Only", draft.CommonMobileOnly);
                    Text("Remove unused animation logic, mesh data, materials and physics calculations.");
                    EditorGUILayout.Space(10);
                    Section("AFK expressions", "Preserve AFK expressions, or allow them to change so more facial BlendShapes can be optimized. This is your choice; detection alone does not enable it.");
                    draft.ExcludeAfkAnimations = EditorGUILayout.ToggleLeft("Allow AFK expressions to change", draft.ExcludeAfkAnimations);
                    if (draft.ExcludeAfkAnimations) DrawAfkAnimations();
                    else Text("AFK expressions remain part of optimization analysis.");
                    EditorGUILayout.Space(10);
                    draft.BakeLilToonMainTextures = EditorGUILayout.Toggle("Bake lilToon colors and layers", draft.BakeLilToonMainTextures);
                    Text("Combine supported static color corrections and secondary textures. Texture filtering, mipmaps and Safety Fallback appearance may differ.");
                }
            });
            Frame("Mobile", "Simplify the mobile avatar", MobilePlatforms, true, () =>
            {
                draft.IncludeMobile = EditorGUILayout.ToggleLeft("Simplify the mobile avatar", draft.IncludeMobile, EditorStyles.boldLabel);
                Text("Choose the controls, optional features, bone physics and meshes to keep in the next steps. Materials and geometry are simplified during the build.");
                if (draft.IncludeMobile && !draft.IncludeCommon)
                    EditorGUILayout.HelpBox("Enable general optimization above. Choose Optimize Mobile Only if PC should be left out.", MessageType.Warning);
            });
            Frame("Cross-platform", "Parameter synchronization", "PC + Android + iOS", true, () =>
            {
                Text("Match mobile parameters to PC: " + YesNo(draft.ParameterLayout) + ". Automatic compression: " + YesNo(draft.ParameterCompression) + ".");
                Text("PC uploads establish the parameter layout used by mobile uploads. Compression can make some values sync less frequently. Change these options in Advanced > Parameters.");
            });
            DrawExistingSettingsNotice();
        }

        private void DrawSimpleFeatures()
        {
            Frame("Mobile", "Controls to keep working", MobilePlatforms, draft.IncludeMobile, () =>
            {
                Text(draft.FreezeMode == AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved
                    ? "Other menu and external inputs use their build defaults. Add facial tracking or other features that must remain adjustable. VRChat's built-in inputs remain active."
                    : "Only the specific parameter values configured in Advanced are fixed. Other inputs remain adjustable.");
                DrawParameterSources(false);
                if (draft.PreserveParameters.Names.Count + draft.PreserveParameters.Prefixes.Count > 0)
                    Text("Additional preserved names and prefixes are configured in Advanced > Parameters.");
                if (draft.FreezeMode == AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved &&
                    draft.PreserveParameters.Sources.All(s => s?.Source == null) &&
                    draft.PreserveParameters.Names.Count == 0 && draft.PreserveParameters.Prefixes.Count == 0)
                    EditorGUILayout.HelpBox("No feature inputs are selected to keep working. Add your facial tracking setup here if it should remain active on mobile.", MessageType.Warning);
            });
            Frame("Mobile", "Optional features to remove", MobilePlatforms, draft.IncludeMobile, () =>
            {
                Text("Choose gimmicks you do not need on mobile. Explicit exclusions can remove more content and reduce build analysis work. You can leave unrecognized features for automatic optimization.");
                DrawFeatureExclusions();
            });
        }

        private void DrawFeatureExclusions()
        {
            Text("Known Light Limit Changer, marshmallow PB, Pudding and RealSweat setup components are initially selected by component type. Remove any entry you want to keep.");
            ObjectList("Feature objects to remove", draft.ExcludedObjects, "Add Feature Object");
            ObjectList("Individual components to remove", draft.ExcludedComponents, "Add Component");
            Text("Removing a setup component prevents its build processing. Already-installed objects can also be removed explicitly; remaining effects are handled by mobile filtering and optimization.");
            using (new EditorGUI.DisabledScope(draft.Avatar == null))
                if (GUILayout.Button("Add Detected Setup Components")) KnownFeatureDiscovery.AddSuggestions(draft);
        }

        private void RefreshPhysBoneChoices()
        {
            physBoneChoices = draft.Avatar == null ? Array.Empty<VRCPhysBone>() :
                draft.Avatar.GetComponentsInChildren<VRCPhysBone>(true).Where(p =>
                    p.GetComponentInParent<VRCAvatarDescriptor>(true) == draft.Avatar && !ALTOPlugin.IsEditorOnly(p.transform))
                    .OrderBy(p => AnimationUtility.CalculateTransformPath(p.transform, draft.Avatar.transform), StringComparer.Ordinal).ToArray();
        }

        private void DrawSimplePhysics()
        {
            Frame("Mobile", "Choose the bone physics you value", MobilePlatforms, draft.IncludeMobile, () =>
            {
                Text("Choose which parts should keep moving, such as hair, ears or clothing. No PhysBones are selected automatically. Most avatars need fewer PhysBones for mobile; prioritize the parts that matter to you.");
                draft.PhysBoneFilterMode = Choice("Bone physics to keep", draft.PhysBoneFilterMode,
                    (PhysBoneFilterMode.KeepListed, "Keep only the selected PhysBones"),
                    (PhysBoneFilterMode.RemoveListed, "Keep all except the selected PhysBones"));
                bool keep = draft.PhysBoneFilterMode == PhysBoneFilterMode.KeepListed;
                if (physBoneChoices == null) RefreshPhysBoneChoices();
                physBoneSearch = EditorGUILayout.TextField("Search PhysBones", physBoneSearch);
                hideInactivePhysBones = EditorGUILayout.ToggleLeft(new GUIContent("Hide Inactive PhysBones",
                    "Hide disabled components and inactive objects or controlled bone roots in the current Editor hierarchy. This only filters the list; existing selections are kept."), hideInactivePhysBones);
                Text("Check to " + (keep ? "keep" : "remove") + ". " + draft.PhysBones.Count(p => p != null) + " individually selected; " + physBoneChoices.Length + " existing PhysBones found.");
                physBoneScroll = EditorGUILayout.BeginScrollView(physBoneScroll, EditorStyles.helpBox, GUILayout.Height(210));
                var visible = VisiblePhysBones();
                foreach (var bone in visible)
                {
                    string path = AnimationUtility.CalculateTransformPath(bone.transform, draft.Avatar.transform);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool selected = draft.PhysBones.Contains(bone);
                        string controlled = bone.rootTransform == null ? path : AnimationUtility.CalculateTransformPath(bone.rootTransform, draft.Avatar.transform);
                        bool next = EditorGUILayout.ToggleLeft(new GUIContent(path, "Controlled bone: " + controlled), selected);
                        if (next != selected)
                        {
                            if (next) draft.PhysBones.Add(bone); else draft.PhysBones.RemoveAll(p => p == bone);
                        }
                        if (GUILayout.Button("Show", GUILayout.Width(44))) EditorGUIUtility.PingObject(bone);
                    }
                }
                if (visible.Length == 0) Text("No PhysBones match the current list filters.");
                EditorGUILayout.EndScrollView();
                Text(visible.Length + " shown. List filters do not change your selections; animation may change activity during play.");
                Property("PhysBones", "Selected PhysBones");
                Property("PhysBoneRoots", "Also select by bone hierarchy");
                Text("Bone hierarchies can match PhysBones generated during the build. The list above shows existing components; final counts are evaluated during the build.");
                if (keep && draft.PhysBones.Count == 0 && draft.PhysBoneRoots.Count == 0)
                    EditorGUILayout.HelpBox("Nothing is selected. All PhysBones will be removed from the mobile avatar.", MessageType.Warning);
                Text("Unused collision shapes: " + (draft.RemoveUnreferencedColliders ? "remove" : "keep") + ". Change collision and touch-interaction options in Advanced > Features.");
            });
        }

        private VRCPhysBone[] VisiblePhysBones()
        {
            if (physBoneChoices == null) RefreshPhysBoneChoices();
            return physBoneChoices.Where(p => p != null && (!hideInactivePhysBones || p.isActiveAndEnabled &&
                    (p.rootTransform == null || p.rootTransform.gameObject.activeInHierarchy)) &&
                (string.IsNullOrWhiteSpace(physBoneSearch) || AnimationUtility.CalculateTransformPath(p.transform, draft.Avatar.transform)
                    .IndexOf(physBoneSearch, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
        }

        private void DrawSimpleMeshes()
        {
            Frame("Mobile", "Body and face meshes", MobilePlatforms, draft.IncludeMobile, () =>
            {
                Section("Hidden body regions", "Review the suggested body mesh and add other body meshes if needed. Every BlendShape is a candidate by default; only regions hidden in all tested views and poses are removed.");
                draft.RemoveOccludedMesh = EditorGUILayout.ToggleLeft("Remove hidden body regions", draft.RemoveOccludedMesh);
                using (new EditorGUI.DisabledScope(!draft.RemoveOccludedMesh)) DrawBodyMeshes();
                EditorGUILayout.Space(10);
                Section("Meshes to keep at full detail", "Lip-sync and eyelid meshes are added from the Avatar Descriptor. Correct this list for your avatar; a combined face/body mesh is preserved as a whole.");
                ObjectList("Keep these meshes at full detail", draft.PreserveRenderers, "Add Mesh to Preserve");
                Text(draft.ReduceMeshes ? "Other meshes are reduced towards " + draft.TriangleBudget.ToString("N0") + " total avatar triangles. Change the target in Advanced > Meshes." : "Triangle reduction is disabled in Advanced > Meshes.");
                EditorGUILayout.Space(10);
                Section("Always remove these mesh regions", "Select overlays that should disappear even when visible, such as face sweat, tears or cheek effects. For an unknown avatar, choose its mesh and BlendShapes manually.");
                DrawMeshPresets();
                DrawMeshRegionExclusions();
            });
            Frame("Mobile", "Materials", MobilePlatforms, draft.IncludeMobile, () =>
            {
                Text(draft.CreateMaterialAtlas ? "Combine main textures using " + (draft.ReferenceMaterial != null ? draft.ReferenceMaterial.name : "a missing material preset") +
                    " at " + draft.AtlasSize + " x " + draft.AtlasSize + "." : "Material atlasing is disabled.");
                Text("Change the output material and texture quality in Advanced > Materials.");
            });
        }

        private void DrawExistingSettingsNotice()
        {
            if (existingComponents == 0) return;
            EditorGUILayout.HelpBox("This avatar already contains ALTO settings. Edit those settings or choose an avatar without ALTO to install a new setup.", MessageType.Info);
            if (GUILayout.Button("Select Existing Settings"))
                Selection.activeGameObject = AvatarInstaller.ExistingSettings(draft.Avatar).FirstOrDefault()?.gameObject;
        }

        private static void ObjectList<T>(string title, List<T> items, string addLabel) where T : UnityEngine.Object
        {
            EditorGUILayout.LabelField(title + " (" + items.Count + ")", EditorStyles.boldLabel);
            for (int i = 0; i < items.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string label = items[i] is Component component ? KnownFeatureDiscovery.Label(component) : null;
                    items[i] = (T)EditorGUILayout.ObjectField(new GUIContent(label ?? ""), items[i], typeof(T), true);
                    if (GUILayout.Button("Remove", GUILayout.Width(68))) { items.RemoveAt(i); break; }
                }
            }
            if (GUILayout.Button(addLabel)) items.Add(null);
        }

        private void RefreshMeshPresets(bool applyExact)
        {
            meshPresetMatches = MeshRegionPresetDiscovery.Find(draft.Avatar, MeshRegionPresetDiscovery.LoadPresets());
            if (applyExact) MeshRegionPresetDiscovery.ApplyInitialExactMatches(draft, meshPresetMatches);
        }

        private void DrawMeshPresets()
        {
            foreach (var match in meshPresetMatches ?? Enumerable.Empty<MeshRegionPresetMatch>())
            {
                if (match.Renderer == null || match.Preset == null) continue;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    string name = string.IsNullOrEmpty(match.Preset.DisplayName) ? match.Preset.name : match.Preset.DisplayName;
                    EditorGUILayout.LabelField(name, EditorStyles.boldLabel);
                    using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField("Mesh", match.Renderer, typeof(SkinnedMeshRenderer), true);
                    Text(match.Exact ? "Base-position fingerprint matched. The listed regions were suggested when selecting this avatar."
                        : "Similar geometry: " + match.Similarity.ToString("P0") + " signature agreement. This is a suggestion, not a vertex-position tolerance. Review before applying.");
                    Text("Regions: " + string.Join(", ", match.Preset.BlendShapes));
                    bool applied = match.Preset.BlendShapes.All(n => draft.BlendShapeExclusions.Any(r => r != null && r.Renderer == match.Renderer && r.BlendShapes.Contains(n)));
                    using (new EditorGUI.DisabledScope(applied))
                        if (GUILayout.Button(applied ? "Preset Regions Selected" : "Use These Preset Regions")) MeshRegionPresetDiscovery.Apply(draft, match);
                }
            }
            if (draft.Avatar != null && (meshPresetMatches == null || meshPresetMatches.Count == 0))
                Text("No matching region preset was found. Add a mesh and choose its regions below.");
            using (new EditorGUI.DisabledScope(draft.Avatar == null))
                if (GUILayout.Button("Find Region Presets")) RefreshMeshPresets(false);
        }

        private void DrawMeshRegionExclusions()
        {
            for (int i = 0; i < draft.BlendShapeExclusions.Count; i++)
            {
                var region = draft.BlendShapeExclusions[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var renderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Mesh", region.Renderer, typeof(SkinnedMeshRenderer), true);
                        if (renderer != region.Renderer) { region.Renderer = renderer; region.BlendShapes.Clear(); }
                        if (GUILayout.Button("Remove", GUILayout.Width(68))) { draft.BlendShapeExclusions.RemoveAt(i); break; }
                    }
                    using (new EditorGUI.DisabledScope(region.Renderer == null || region.Renderer.sharedMesh == null))
                        if (GUILayout.Button("Choose Regions to Remove (" + region.BlendShapes.Count + ")"))
                            PopupWindow.Show(GUILayoutUtility.GetLastRect(), new BlendShapeRegionSelector(this, "draft.BlendShapeExclusions.Array.data[" + i + "]"));
                    if (region.BlendShapes.Count > 0) Text(string.Join(", ", region.BlendShapes));
                }
            }
            if (GUILayout.Button("Add Mesh Regions to Remove")) draft.BlendShapeExclusions.Add(new BlendShapeMeshExclusion());
        }
    }
}
