using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    [CustomEditor(typeof(ALTOContactFilter))]
    internal sealed class ALTOContactFilterEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            foreach (string field in new[] { "ScopeRoot", "ApplyOnPlatforms", "FilterMode" })
                EditorGUILayout.PropertyField(serializedObject.FindProperty(field), true);
            bool keep = serializedObject.FindProperty("FilterMode").intValue == (int)ContactFilterMode.KeepListed;
            foreach (string field in new[] { "Senders", "Receivers", "ObjectRoots" })
            {
                var property = serializedObject.FindProperty(field);
                string label = field == "ObjectRoots" ? "Object Roots" : field;
                EditorGUILayout.PropertyField(property, new GUIContent(label + (keep ? " to Keep" : " to Remove"), property.tooltip), true);
            }
            var component = (ALTOContactFilter)target;
            if (component.FindDescriptor() == null)
                EditorGUILayout.HelpBox("Place this component inside an avatar with a VRC Avatar Descriptor.", MessageType.Error);
            if ((component.ApplyOnPlatforms & OptimizationBuildScope.Platform(EditorUserBuildSettings.activeBuildTarget)) == 0)
                EditorGUILayout.HelpBox("This filter is inactive for the current build target.", MessageType.Info);
            EditorGUILayout.HelpBox(keep
                ? "Keep Listed with empty selections removes every scoped Contact Sender and Receiver."
                : "Remove Listed with empty selections removes no Contacts.", MessageType.Info);
            EditorGUILayout.HelpBox("Filtering runs after generators and is rechecked after late SDK processors. Object Roots selects the objects holding Contacts, " +
                "not their Root Transform anchors. Selections follow registered replacements and reparented objects. " +
                "Transforms remain for subsequent dependency cleanup. Preserved Receiver inputs and component-reference conflicts stop the build.", MessageType.None);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
