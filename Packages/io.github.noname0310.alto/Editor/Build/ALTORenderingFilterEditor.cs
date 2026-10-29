using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    [CustomEditor(typeof(ALTORenderingFilter))]
    internal sealed class ALTORenderingFilterEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var component = (ALTORenderingFilter)target;
            FilterEditorGUI.Header(serializedObject, component);
            FilterEditorGUI.Rendering(serializedObject);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
