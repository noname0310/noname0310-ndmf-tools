using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    internal static class RendererSelectionInspector
    {
        internal static void Draw(SerializedObject serialized, string targetsProperty, bool reduction)
        {
            EditorGUILayout.PropertyField(serialized.FindProperty("ScopeRoot"));
            EditorGUILayout.PropertyField(serialized.FindProperty("ApplyOnPlatforms"));
            var mode = serialized.FindProperty("TargetSelection");
            EditorGUILayout.PropertyField(mode);
            var scope = serialized.FindProperty("ScopeRoot").objectReferenceValue as Transform;
            if (scope == null) scope = ((Component)serialized.targetObject).transform;
            if (mode.intValue == (int)RendererSelectionMode.AllRenderersInScope)
                EditorGUILayout.HelpBox("Target range: " + scope.name + " and all descendants.\n" +
                    "Includes MeshRenderer and SkinnedMeshRenderer components on inactive objects and renderers generated before this build pass. " +
                    (reduction ? "Preserve Renderers and unsupported-input checks still apply; only meshes surviving cleanup are reduced." :
                        "Material groups and unsupported-input checks still apply."), MessageType.Info);
            else
            {
                var targets = serialized.FindProperty(targetsProperty);
                EditorGUILayout.PropertyField(targets, true);
                if (targets.arraySize == 0)
                    EditorGUILayout.HelpBox("No renderers selected. " + (reduction ? "Scoped meshes still count toward the triangle budget." :
                        "This component will not atlas any renderers."), MessageType.Info);
            }
        }
    }
}
