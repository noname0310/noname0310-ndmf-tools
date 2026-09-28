using nadena.dev.ndmf.runtime;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Editor
{
    [CustomEditor(typeof(NormalFieldOffset)), CanEditMultipleObjects]
    internal sealed class NormalFieldOffsetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NormalFieldOffset.TargetBody)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NormalFieldOffset.Offset)), new GUIContent("Offset (m)"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(NormalFieldOffset.MaxDistance)), new GUIContent("Max Distance (m)"));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.HelpBox(
                "Moves this clothing mesh along the surface normals of Target Body. " +
                "Positive values move outward, negative values move inward. 0.002 m = 2 mm.\n" +
                "Enable NDMF Preview to see the result in the Scene view. The offset is applied on Play/build while preserving the original mesh.",
                MessageType.Info);

            foreach (NormalFieldOffset component in targets)
            {
                var request = new OffsetRequest(component.GetComponent<SkinnedMeshRenderer>(), component.TargetBody, component.Offset, component.MaxDistance);
                var error = NormalFieldProcessor.Validate(request);
                if (error != null)
                {
                    EditorGUILayout.HelpBox(error, MessageType.Warning);
                    continue;
                }
                var avatar = RuntimeUtil.FindAvatarInParents(component.transform);
                if (avatar != null && !component.TargetBody.transform.IsChildOf(avatar))
                    EditorGUILayout.HelpBox("Target Body must be a renderer within the same avatar.", MessageType.Error);
                if (NormalFieldOffsetPlugin.IsEditorOnly(component.TargetBody.transform))
                    EditorGUILayout.HelpBox("Target Body is on or under an EditorOnly object.", MessageType.Error);
            }
        }
    }
}
