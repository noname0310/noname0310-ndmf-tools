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
                EditorGUI.LabelField(right, "Override Animation");
            };
            entries.elementHeight = EditorGUIUtility.singleLineHeight + 6;
            entries.drawElementCallback = DrawEntry;
            entries.onAddCallback = list =>
            {
                int index = list.serializedProperty.arraySize++;
                var row = list.serializedProperty.GetArrayElementAtIndex(index);
                row.FindPropertyRelative(nameof(BlendShapePoseEntry.BlendShapeName)).stringValue = "";
                row.FindPropertyRelative(nameof(BlendShapePoseEntry.Animation)).objectReferenceValue = null;
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
                "Each animation defines the pose at weight 100. The current Target Mesh blendshape values are the basis at weight 0. " +
                "Unkeyed values keep their current settings. All entries use the same original basis.", MessageType.Info);
            if (component.Overrides != null)
                for (int i = 0; i < component.Overrides.Count; i++)
                    if (PoseOverrideConfiguration.HasMultipleFrames(component.Overrides[i]?.Animation))
                        EditorGUILayout.HelpBox($"Entry {i + 1}: {PoseOverrideConfiguration.MultiFrameWarning}", MessageType.Warning);

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
                    if (GUILayout.Button(new GUIContent("Add VRC Blink", hasVrcBlink ? $"Add the descriptor's Blink shape: {vrcBlink}. Find its pose animation using v2 eye parameters." : vrcError)))
                        AddPreset(component, new Dictionary<string, EyePose> { { vrcBlink, EyePose.Closed } }, "Add VRC Blink Override");
                using (new EditorGUI.DisabledScope(mmdNames.Length == 0))
                    if (GUILayout.Button(new GUIContent("Add MMD Eye Morphs", $"Add {mmdNames.Length} of 7 named MMD eye morphs. Find their individual pose animations using v2 eye parameters.")))
                        AddPreset(component, PoseOverridePresets.GetMmdRequests(mesh), "Add MMD Eye Overrides");
            }
        }

        private void AddPreset(BlendShapePoseOverride component, IReadOnlyDictionary<string, EyePose> requests, string undoName)
        {
            Undo.RecordObject(component, undoName);
            int added = PoseOverridePresets.AddMissingEntries(component, requests.Keys);
            var report = EyePresetAutoAssignment.FillMissing(component, requests);
            presetMessage = $"Added {added} entries. Assigned {report.Assigned} animations. Kept {report.Preserved} existing assignments.";
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
            EditorGUI.PropertyField(right, row.FindPropertyRelative(nameof(BlendShapePoseEntry.Animation)), GUIContent.none);
        }
    }
}
