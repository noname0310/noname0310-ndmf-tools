using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    [CustomEditor(typeof(ALTOPhysBoneFilter))]
    internal sealed class ALTOPhysBoneFilterEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            foreach (string field in new[] { "ScopeRoot", "ApplyOnPlatforms", "FilterMode" })
                EditorGUILayout.PropertyField(serializedObject.FindProperty(field), true);
            bool keepListed = serializedObject.FindProperty("FilterMode").intValue == (int)PhysBoneFilterMode.KeepListed;
            var physBones = serializedObject.FindProperty("PhysBones");
            var boneRoots = serializedObject.FindProperty("BoneRoots");
            EditorGUILayout.PropertyField(physBones, new GUIContent(keepListed ? "PhysBones to Keep" : "PhysBones to Remove", physBones.tooltip), true);
            EditorGUILayout.PropertyField(boneRoots, new GUIContent(keepListed ? "Bone Roots to Keep" : "Bone Roots to Remove", boneRoots.tooltip), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("RemoveUnreferencedColliders"));
            if (serializedObject.FindProperty("RemoveUnreferencedColliders").boolValue)
                using (new EditorGUI.IndentLevelScope())
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("PreserveColliders"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Budget"), true);
            var component = (ALTOPhysBoneFilter)target;
            if (component.FindDescriptor() == null)
                EditorGUILayout.HelpBox("Place this component inside an avatar with a VRC Avatar Descriptor.", MessageType.Error);
            if ((component.ApplyOnPlatforms & OptimizationBuildScope.Platform(EditorUserBuildSettings.activeBuildTarget)) == 0)
                EditorGUILayout.HelpBox("This filter is inactive for the current build target.", MessageType.Info);
            EditorGUILayout.HelpBox("Filtering runs after generators. Bone Roots selects controlled bones, including newly generated PhysBones; " +
                "it does not infer arbitrary generator ownership. " + (keepListed
                    ? "Keep Listed with an empty selection removes all scoped PhysBones."
                    : "Remove Listed with an empty selection removes no PhysBones."), MessageType.Info);
            EditorGUILayout.HelpBox("Bone transforms remain. Shared and global colliders are protected. Budget limits use final NDMF avatar-wide SDK counts; " +
                "-1 disables a limit. This component has no continuous preview.", MessageType.None);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
