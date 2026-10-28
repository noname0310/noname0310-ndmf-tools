using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.ParameterPolicy
{
    [CustomEditor(typeof(ALTOAnimatorParameterPolicy))]
    internal sealed class ALTOAnimatorParameterPolicyEditor : UnityEditor.Editor
    {
        private readonly Dictionary<int, ParameterSourceResult> previews = new();
        private readonly Dictionary<int, int> pages = new();
        private readonly Dictionary<int, Dictionary<string, string>> origins = new();
        private string search = "";
        private bool scopeValidated;
        private string scopeError;
        private const int PageSize = 30;

        private void OnEnable()
        {
            EditorApplication.projectChanged += Invalidate;
            EditorApplication.hierarchyChanged += Invalidate;
            Undo.undoRedoPerformed += Invalidate;
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= Invalidate;
            EditorApplication.hierarchyChanged -= Invalidate;
            Undo.undoRedoPerformed -= Invalidate;
        }

        private void Invalidate() { previews.Clear(); origins.Clear(); scopeValidated = false; Repaint(); }

        public override void OnInspectorGUI()
        {
            var component = (ALTOAnimatorParameterPolicy)target;
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ScopeRoot"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ApplyOnPlatforms"));
            var descriptor = component.FindDescriptor();
            if (descriptor == null)
                EditorGUILayout.HelpBox("Place this component inside an avatar with a VRC Avatar Descriptor.", MessageType.Error);
            else
            {
                if (!scopeValidated)
                {
                    scopeError = null;
                    try { OptimizationBuildScope.Capture(component, descriptor.gameObject); }
                    catch (InvalidOperationException error) { scopeError = error.Message; }
                    scopeValidated = true;
                }
                if (scopeError != null) EditorGUILayout.HelpBox(scopeError, MessageType.Error);
            }
            if ((component.ApplyOnPlatforms & OptimizationBuildScope.Platform(EditorUserBuildSettings.activeBuildTarget)) == 0)
                EditorGUILayout.HelpBox("This policy is inactive for the current build target: " + EditorUserBuildSettings.activeBuildTarget + ".", MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Preserve Parameters", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Choose source objects or controllers to preserve their parameters and required computations. " +
                "Source lists update during builds. Preserve also protects upstream dependencies from freezing.", MessageType.Info);
            var selection = serializedObject.FindProperty("PreserveParameters");
            var sources = selection.FindPropertyRelative("Sources");
            for (int i = 0; i < sources.arraySize; i++)
            {
                var item = sources.GetArrayElementAtIndex(i);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Source " + (i + 1), EditorStyles.boldLabel);
                        if (GUILayout.Button("Remove", GUILayout.Width(70)))
                        {
                            sources.DeleteArrayElementAtIndex(i);
                            Invalidate();
                            break;
                        }
                    }
                    EditorGUILayout.PropertyField(item.FindPropertyRelative("Source"));
                    var source = item.FindPropertyRelative("Source").objectReferenceValue;
                    if (source is GameObject)
                        EditorGUILayout.PropertyField(item.FindPropertyRelative("IncludeChildren"));
                    if (source is RuntimeAnimatorController || source is GameObject || source is Component)
                        EditorGUILayout.PropertyField(item.FindPropertyRelative("SelectionMode"));
                    if (source != null && !ParameterSourceResolver.IsSupported(source))
                        EditorGUILayout.HelpBox("Unsupported source type.", MessageType.Error);
                    item.isExpanded = EditorGUILayout.Foldout(item.isExpanded, "Source Parameters", true);
                    if (!item.isExpanded || source == null || i >= component.PreserveParameters.Sources.Count) continue;
                    if (!previews.TryGetValue(i, out var result))
                    {
                        previews[i] = result = ParameterSourceResolver.Resolve(component.PreserveParameters.Sources[i], descriptor?.gameObject);
                        origins[i] = result.Parameters.GroupBy(p => p.Name).ToDictionary(g => g.Key, g =>
                            string.Join("\n", g.Select(p => p.Owner != null && descriptor != null
                                ? AnimationUtility.CalculateTransformPath(p.Owner.transform, descriptor.transform) + " (" + p.Owner.GetType().Name + ")"
                                : AssetDatabase.GetAssetPath(p.Asset)).Distinct().OrderBy(p => p, StringComparer.Ordinal)));
                    }
                    foreach (var diagnostic in result.Diagnostics) EditorGUILayout.HelpBox(diagnostic, MessageType.Warning);
                    DrawParameters(i, item.FindPropertyRelative("ExcludedNames"), result);
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Source"))
                {
                    int index = sources.arraySize++;
                    var source = sources.GetArrayElementAtIndex(index);
                    source.FindPropertyRelative("Source").objectReferenceValue = null;
                    source.FindPropertyRelative("IncludeChildren").boolValue = true;
                    source.FindPropertyRelative("SelectionMode").enumValueIndex = 0;
                    source.FindPropertyRelative("ExcludedNames").ClearArray();
                    source.isExpanded = true;
                }
                if (GUILayout.Button("Refresh Source Lists")) Invalidate();
            }
            EditorGUILayout.PropertyField(selection.FindPropertyRelative("Names"), new GUIContent("Additional Names"), true);
            EditorGUILayout.PropertyField(selection.FindPropertyRelative("Prefixes"), new GUIContent("Additional Prefixes"), true);
            EditorGUILayout.HelpBox("Source Parameters show original names. Build reports verify final names after generator remapping. " +
                "Names and prefixes above use the final avatar namespace. Analysis covers the whole avatar; preservation may keep dependencies outside Scope Root.", MessageType.None);
            DrawConstants(component);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("BakeInitialVisibility"));
            if (serializedObject.FindProperty("BakeInitialVisibility").boolValue)
                EditorGUILayout.HelpBox("Initialize objects and renderers to their proven constant FX visibility. " +
                    "This can change activation callbacks and visibility before the first Animator evaluation. " +
                    "Dynamic outputs and explicit preservation remain protected. Scope Root and Apply On Platforms apply.", MessageType.Info);
            if (serializedObject.ApplyModifiedProperties()) Invalidate();
        }

        private void DrawConstants(ALTOAnimatorParameterPolicy component)
        {
            EditorGUILayout.Space();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("FreezeMode"));
            bool automatic = serializedObject.FindProperty("FreezeMode").enumValueIndex == (int)AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved;
            EditorGUILayout.LabelField(automatic ? "Frozen Value Overrides (Optional)" : "Frozen Parameters", EditorStyles.boldLabel);
            if (automatic)
                EditorGUILayout.HelpBox("Freeze menu controls, Contacts, PhysBone outputs and external Expression Parameters at their build defaults, except preserved inputs. " +
                    "Expression Parameters written by Drivers or parameter curves are included unless Preserve protects them. " +
                    "Internal-only calculations remain dynamic until their consumers disappear. Rows below can override the selected build defaults.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Only the listed parameters are frozen. An empty list freezes nothing; it never enables automatic selection.", MessageType.Info);
            EditorGUILayout.HelpBox("Animator readers use the specified value even when the original input changes. " +
                "This changes avatar behavior. Menu controls and network parameter slots are retained for now. " +
                "Bool values must be 0 or 1; Int values must be whole numbers. Built-ins, preserved dependencies and Trigger parameters cannot be frozen.", MessageType.Info);
            var rules = serializedObject.FindProperty("FreezeParameters");
            for (int i = 0; i < rules.arraySize; i++)
            {
                var item = rules.GetArrayElementAtIndex(i);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Constant " + (i + 1), EditorStyles.boldLabel);
                        if (GUILayout.Button("Remove", GUILayout.Width(70))) { rules.DeleteArrayElementAtIndex(i); break; }
                    }
                    EditorGUILayout.PropertyField(item.FindPropertyRelative("Source"));
                    var selectedSource = item.FindPropertyRelative("Source").objectReferenceValue;
                    if (selectedSource is GameObject)
                        EditorGUILayout.PropertyField(item.FindPropertyRelative("IncludeChildren"));
                    else if (selectedSource == null)
                        EditorGUILayout.PropertyField(item.FindPropertyRelative("IncludeChildren"),
                            new GUIContent("Include Children in Picker", "Search the avatar root and its descendants when using Choose without a source."));
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PropertyField(item.FindPropertyRelative("Name"));
                        if (GUILayout.Button("Choose...", GUILayout.Width(80)))
                        {
                            serializedObject.ApplyModifiedProperties();
                            var source = item.FindPropertyRelative("Source").objectReferenceValue;
                            var selection = new AnimatorParameterSource
                            {
                                Source = source != null ? source : component.FindDescriptor()?.gameObject,
                                IncludeChildren = item.FindPropertyRelative("IncludeChildren").boolValue
                            };
                            var result = ParameterSourceResolver.Resolve(selection, component.FindDescriptor()?.gameObject);
                            var menu = new GenericMenu();
                            var propertyPath = item.FindPropertyRelative("Name").propertyPath;
                            var sourcePath = item.FindPropertyRelative("Source").propertyPath;
                            var inferredSource = source == null ? selection.Source : null;
                            foreach (var name in result.Parameters.Select(p => p.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal))
                            {
                                var captured = name;
                                menu.AddItem(new GUIContent(name), name == item.FindPropertyRelative("Name").stringValue, () =>
                                {
                                    if (target == null) return;
                                    serializedObject.Update();
                                    var property = serializedObject.FindProperty(propertyPath);
                                    if (property == null) return;
                                    property.stringValue = captured;
                                    if (inferredSource != null) serializedObject.FindProperty(sourcePath).objectReferenceValue = inferredSource;
                                    serializedObject.ApplyModifiedProperties();
                                });
                            }
                            if (menu.GetItemCount() == 0) menu.AddDisabledItem(new GUIContent("No source parameters found"));
                            menu.ShowAsContext();
                        }
                    }
                    EditorGUILayout.PropertyField(item.FindPropertyRelative("UseDefaultValue"));
                    if (!item.FindPropertyRelative("UseDefaultValue").boolValue)
                        EditorGUILayout.PropertyField(item.FindPropertyRelative("Value"));
                }
            }
            if (GUILayout.Button(automatic ? "Add Value Override" : "Add Frozen Parameter"))
            {
                var item = rules.GetArrayElementAtIndex(rules.arraySize++);
                item.FindPropertyRelative("Source").objectReferenceValue = null;
                item.FindPropertyRelative("IncludeChildren").boolValue = true;
                item.FindPropertyRelative("Name").stringValue = "";
                item.FindPropertyRelative("UseDefaultValue").boolValue = false;
                item.FindPropertyRelative("Value").floatValue = 0;
            }
            EditorGUILayout.HelpBox("Source is optional. With a source, use its original parameter name; without one, use the final avatar name. " +
                "Choose only lists authoring-time names. Build and Analyze verifies generated names, Preserve conflicts and Scope safety. " +
                "Subtree scopes require local renderer outputs; unproven bone, physics or behaviour effects require the avatar root scope.", MessageType.None);
        }

        private void DrawParameters(int index, SerializedProperty excluded, ParameterSourceResult result)
        {
            var excludedNames = new HashSet<string>(Enumerable.Range(0, excluded.arraySize)
                .Select(i => excluded.GetArrayElementAtIndex(i).stringValue), StringComparer.Ordinal);
            var all = result.Parameters.Select(p => p.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
            search = EditorGUILayout.TextField("Search", search);
            EditorGUILayout.LabelField((all.Length - all.Count(excludedNames.Contains)) + " / " + all.Length + " included");
            bool changed = false;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Include All")) { excludedNames.Clear(); changed = true; }
                if (GUILayout.Button("Exclude Listed")) { excludedNames.UnionWith(all); changed = true; }
            }
            var names = all.Where(n => n.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            int maxPage = Math.Max(0, (names.Length - 1) / PageSize);
            int page = Math.Min(pages.TryGetValue(index, out var previous) ? previous : 0, maxPage);
            if (maxPage > 0)
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(page == 0)) if (GUILayout.Button("Previous")) page--;
                    GUILayout.Label((page + 1) + " / " + (maxPage + 1));
                    using (new EditorGUI.DisabledScope(page == maxPage)) if (GUILayout.Button("Next")) page++;
                }
            pages[index] = page;
            foreach (string name in names.Skip(page * PageSize).Take(PageSize))
            {
                bool included = !excludedNames.Contains(name);
                origins.TryGetValue(index, out var sourceOrigins);
                string origin = sourceOrigins != null && sourceOrigins.TryGetValue(name, out var text) ? text : "";
                bool selected = EditorGUILayout.ToggleLeft(new GUIContent(name, origin), included);
                if (included == selected) continue;
                if (selected) excludedNames.Remove(name); else excludedNames.Add(name);
                changed = true;
            }
            if (!changed) return;
            var values = excludedNames.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            excluded.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++) excluded.GetArrayElementAtIndex(i).stringValue = values[i];
        }
    }
}
