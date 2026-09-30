using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Noname.AvatarTools.Editor
{
    [CustomEditor(typeof(BlendShapePoseOverride))]
    internal sealed class BlendShapePoseOverrideEditor : UnityEditor.Editor
    {
        private ReorderableList entries;
        private string[] shapeNames = new string[0];
        private string presetMessage;
        private MessageType presetMessageType;

        private void OnEnable()
        {
            entries = new ReorderableList(serializedObject, serializedObject.FindProperty(nameof(BlendShapePoseOverride.Overrides)), true, true, true, true);
            entries.drawHeaderCallback = rect =>
            {
                Split(rect, out var left, out var right);
                EditorGUI.LabelField(left, "Blendshape");
                EditorGUI.LabelField(right, "Override Animations");
            };
            entries.elementHeightCallback = index =>
                (entries.serializedProperty.GetArrayElementAtIndex(index)
                    .FindPropertyRelative(nameof(BlendShapePoseEntry.AdditionalAnimations)).arraySize + 2)
                * (EditorGUIUtility.singleLineHeight + 2) + 4;
            entries.drawElementCallback = DrawEntry;
            entries.onAddCallback = list =>
            {
                int index = list.serializedProperty.arraySize++;
                var row = list.serializedProperty.GetArrayElementAtIndex(index);
                row.FindPropertyRelative(nameof(BlendShapePoseEntry.BlendShapeName)).stringValue = "";
                row.FindPropertyRelative(nameof(BlendShapePoseEntry.Animation)).objectReferenceValue = null;
                row.FindPropertyRelative(nameof(BlendShapePoseEntry.AdditionalAnimations)).arraySize = 0;
                list.index = index;
            };
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlendShapePoseOverride.TargetMesh)), new GUIContent("Target Mesh"));
            var renderer = serializedObject.FindProperty(nameof(BlendShapePoseOverride.TargetMesh)).objectReferenceValue as SkinnedMeshRenderer;
            var mesh = renderer != null ? renderer.sharedMesh : null;
            shapeNames = mesh != null
                ? Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray()
                : new string[0];
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlendShapePoseOverride.PathMode)));
            if (serializedObject.FindProperty(nameof(BlendShapePoseOverride.PathMode)).enumValueIndex == (int)PoseAnimationPathMode.Relative)
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BlendShapePoseOverride.RelativePathRoot)));

            if (serializedObject.ApplyModifiedProperties()) SceneView.RepaintAll();
            var component = (BlendShapePoseOverride)target;
            DrawPresets(component, mesh);
            if (!string.IsNullOrEmpty(presetMessage)) EditorGUILayout.HelpBox(presetMessage, presetMessageType);

            EditorGUILayout.Space();
            entries.DoLayoutList();
            if (serializedObject.ApplyModifiedProperties()) SceneView.RepaintAll();

            EditorGUILayout.HelpBox(
                "Each row combines the first-frame blendshape values from its animations into the pose at weight 100. " +
                "Overlapping keys must have the same value. Unkeyed values keep their current settings. " +
                "All rows use the current Target Mesh values as the same original basis at weight 0.", MessageType.Info);
            if (component.Overrides != null)
                for (int i = 0; i < component.Overrides.Count; i++)
                {
                    var clips = component.Overrides[i]?.GetAnimations().ToArray();
                    if (clips == null) continue;
                    for (int j = 0; j < clips.Length; j++)
                        if (PoseOverrideConfiguration.HasMultipleFrames(clips[j]))
                            EditorGUILayout.HelpBox($"Entry {i + 1}, animation {j + 1}: {PoseOverrideConfiguration.MultiFrameWarning}", MessageType.Warning);
                }

            bool valid = PoseOverrideConfiguration.TryResolve(component, out var plan, out var error);
            if (!valid) EditorGUILayout.HelpBox(error, MessageType.Warning);
            else foreach (var warning in plan.Warnings) EditorGUILayout.HelpBox(warning, MessageType.Warning);
            if (valid && plan.Poses.Count == 0)
                EditorGUILayout.HelpBox("Add entries manually with + or use an eye preset. An empty list leaves the mesh unchanged.", MessageType.Info);

            using (new EditorGUI.DisabledScope(!valid || plan.Poses.Count == 0))
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Scene Preview", EditorStyles.boldLabel);
                var labels = component.Overrides?.Select((e, i) => $"{i + 1}: {e?.BlendShapeName}").ToArray() ?? new string[0];
                var selected = serializedObject.FindProperty(nameof(BlendShapePoseOverride.PreviewEntry));
                selected.intValue = EditorGUILayout.Popup("Preview Entry", Mathf.Clamp(selected.intValue, 0, Mathf.Max(0, labels.Length - 1)), labels);
                EditorGUILayout.Slider(serializedObject.FindProperty(nameof(BlendShapePoseOverride.PreviewWeight)), 0, 100, new GUIContent("Preview Weight"));
                EditorGUILayout.HelpBox("Enable NDMF Preview to see the result. Preview controls do not change build defaults or the source mesh.", MessageType.Info);
            }
            if (serializedObject.ApplyModifiedProperties()) SceneView.RepaintAll();
        }

        private void DrawPresets(BlendShapePoseOverride component, Mesh mesh)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Eye Presets (Optional)", EditorStyles.boldLabel);
            bool hasVrcBlink = PoseOverridePresets.TryGetVrcBlink(component, out var vrcBlink, out var vrcError);
            var mmdNames = PoseOverridePresets.GetAvailableMmdShapes(mesh);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!hasVrcBlink))
                    if (GUILayout.Button(new GUIContent("Add VRC Blink", hasVrcBlink ? $"Add the descriptor's Blink shape: {vrcBlink}. Find its pose animations using eye parameters." : vrcError)))
                        AddPreset(component, new Dictionary<string, EyePose> { { vrcBlink, EyePose.Closed } }, "Add VRC Blink Override");
                using (new EditorGUI.DisabledScope(mmdNames.Length == 0))
                    if (GUILayout.Button(new GUIContent("Add MMD Eye Morphs", $"Add {mmdNames.Length} of 7 named MMD eye morphs. Find their individual pose animations using eye parameters.")))
                        AddPreset(component, PoseOverridePresets.GetMmdRequests(mesh), "Add MMD Eye Overrides");
            }
        }

        private void AddPreset(BlendShapePoseOverride component, IReadOnlyDictionary<string, EyePose> requests, string undoName)
        {
            Undo.RecordObject(component, undoName);
            int added = PoseOverridePresets.AddMissingEntries(component, requests.Keys);
            var report = EyePresetAutoAssignment.FillMissing(component, requests);
            presetMessage = $"Added {added} entries. Mapped {report.Assigned} entries. Kept {report.Preserved} existing assignments.";
            if (report.Warnings.Count > 0) presetMessage += "\n" + string.Join("\n", report.Warnings);
            presetMessageType = report.Warnings.Count > 0 ? MessageType.Warning : MessageType.Info;
            if (added > 0 || report.Assigned > 0)
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                EditorUtility.SetDirty(component);
                SceneView.RepaintAll();
            }
            serializedObject.Update();
        }

        private static void Split(Rect rect, out Rect left, out Rect right)
        {
            left = new Rect(rect.x, rect.y, rect.width * 0.45f - 4, EditorGUIUtility.singleLineHeight);
            right = new Rect(rect.x + rect.width * 0.45f, rect.y, rect.width * 0.55f, EditorGUIUtility.singleLineHeight);
        }

        private void DrawEntry(Rect rect, int index, bool active, bool focused)
        {
            rect.y += 2;
            Split(rect, out var left, out var right);
            var row = entries.serializedProperty.GetArrayElementAtIndex(index);
            var name = row.FindPropertyRelative(nameof(BlendShapePoseEntry.BlendShapeName));
            int current = System.Array.IndexOf(shapeNames, name.stringValue) + 1;
            var options = new List<string> { current > 0 || string.IsNullOrEmpty(name.stringValue) ? "Select a blendshape" : $"Missing: {name.stringValue}" };
            options.AddRange(shapeNames);
            EditorGUI.BeginChangeCheck();
            int selected = EditorGUI.Popup(left, current, options.ToArray());
            if (EditorGUI.EndChangeCheck()) name.stringValue = selected > 0 ? shapeNames[selected - 1] : "";
            var first = row.FindPropertyRelative(nameof(BlendShapePoseEntry.Animation));
            var additional = row.FindPropertyRelative(nameof(BlendShapePoseEntry.AdditionalAnimations));
            int count = additional.arraySize + 1;
            for (int i = 0; i < count; i++)
            {
                var clipRect = new Rect(right.x, right.y + i * (right.height + 2), right.width - 24, right.height);
                var clip = i == 0 ? first : additional.GetArrayElementAtIndex(i - 1);
                EditorGUI.PropertyField(clipRect, clip, GUIContent.none);
                using (new EditorGUI.DisabledScope(count == 1))
                    if (GUI.Button(new Rect(clipRect.xMax + 2, clipRect.y, 22, clipRect.height), new GUIContent("-", "Remove this animation")))
                    {
                        if (i == 0) first.objectReferenceValue = additional.GetArrayElementAtIndex(0).objectReferenceValue;
                        int removed = Mathf.Max(0, i - 1);
                        additional.GetArrayElementAtIndex(removed).objectReferenceValue = null;
                        additional.DeleteArrayElementAtIndex(removed);
                        break;
                    }
            }
            if (GUI.Button(new Rect(right.x, right.y + count * (right.height + 2), right.width, right.height), "+ Add Animation"))
            {
                int added = additional.arraySize++;
                additional.GetArrayElementAtIndex(added).objectReferenceValue = null;
            }
        }
    }
}
