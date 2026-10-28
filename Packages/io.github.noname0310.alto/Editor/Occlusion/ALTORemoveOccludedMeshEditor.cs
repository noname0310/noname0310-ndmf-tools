using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Occlusion
{
    [CustomEditor(typeof(ALTORemoveOccludedMesh))]
    internal sealed class ALTORemoveOccludedMeshEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ScopeRoot"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ApplyOnPlatforms"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Targets"), true);
            var selection = serializedObject.FindProperty("OccluderSelection");
            EditorGUILayout.PropertyField(selection);
            bool allInScope = selection.intValue == (int)RendererSelectionMode.AllRenderersInScope;
            var occluders = serializedObject.FindProperty("Occluders");
            EditorGUILayout.PropertyField(occluders, new GUIContent(
                allInScope ? "Occluder Overrides (Optional)" : "Selected Occluders", occluders.tooltip), true);
            EditorGUILayout.HelpBox(allInScope
                ? "Eligible mesh renderers throughout Scope Root are selected automatically, including generated renderers. " +
                  "This optional list only overrides Assume Opaque and Culling for individual renderers; it does not restrict selection."
                : "Only listed renderers are considered as occluders. An empty list selects none. " +
                  "Each entry can also override Assume Opaque and Culling.", MessageType.Info);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("ExcludedOccluders"), true);
            DrawPropertiesExcluding(serializedObject, "m_Script", "ScopeRoot", "ApplyOnPlatforms", "Targets",
                "OccluderSelection", "Occluders", "ExcludedOccluders");
            serializedObject.ApplyModifiedProperties();
            var settings = (ALTORemoveOccludedMesh)target;
            var avatar = settings.FindDescriptor();
            EditorGUILayout.HelpBox("Targets are explicit SkinnedMeshRenderers. Select BlendShapes to define removable regions; their current weights do not select regions. " +
                "All Renderers In Scope includes eligible MeshRenderers and SkinnedMeshRenderers, including build-generated objects. Targets never occlude one another.", MessageType.Info);
            EditorGUILayout.HelpBox("This is a sampled, lossy optimization. Candidate morph weights are reset to zero in temporary pose copies. " +
                "Every region must be covered and hidden in every sampled pose and view. " +
                "Automatic occluders require opaque ShaderLab metadata and unambiguous culling/stencil state; shader displacement and arbitrary alpha programs are not simulated. " +
                "Additional poses reduce motion-related holes but cannot prove visibility for all runtime motion.", MessageType.Warning);
            EditorGUILayout.HelpBox("An empty Reference Pose uses the current build pose. Pose clip paths are relative to the avatar root. " +
                "Current Setup Preview does not run generators or evaluate Animator state machines. Analyze Build runs an isolated build for the active platform and writes the final report.", MessageType.None);
            if (avatar == null) EditorGUILayout.HelpBox("Place this component inside an avatar with a VRC Avatar Descriptor.", MessageType.Error);
            using (new EditorGUI.DisabledScope(avatar == null || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling))
            {
                if (GUILayout.Button("Analyze / Preview Current Setup"))
                    OcclusionPreviewWindow.Open(settings);
                if (GUILayout.Button("Analyze Build (Active Platform)"))
                {
                    var obj = avatar.gameObject;
                    EditorApplication.delayCall += () => { if (obj != null) AvatarBuildAnalysis.Analyze(obj); };
                }
            }
        }
    }

    [CustomPropertyDrawer(typeof(OcclusionPose))]
    internal sealed class OcclusionPoseDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            int rows = property.isExpanded ? (HasClip(property) ? 3 : 2) : 1;
            return EditorGUIUtility.singleLineHeight * rows + EditorGUIUtility.standardVerticalSpacing * (rows - 1);
        }

        private static bool HasClip(SerializedProperty property)
        {
            var clip = property.FindPropertyRelative("Clip");
            return clip.hasMultipleDifferentValues || clip.objectReferenceValue != null;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            using (new EditorGUI.PropertyScope(position, label, property))
            {
                position.height = EditorGUIUtility.singleLineHeight;
                property.isExpanded = EditorGUI.Foldout(position, property.isExpanded, label, true);
                if (!property.isExpanded) return;
                using (new EditorGUI.IndentLevelScope())
                {
                    position.y += position.height + EditorGUIUtility.standardVerticalSpacing;
                    EditorGUI.PropertyField(position, property.FindPropertyRelative("Clip"));
                    if (HasClip(property))
                    {
                        position.y += position.height + EditorGUIUtility.standardVerticalSpacing;
                        EditorGUI.PropertyField(position, property.FindPropertyRelative("Time"));
                    }
                }
            }
        }
    }

    [CustomPropertyDrawer(typeof(OcclusionTarget))]
    internal sealed class OcclusionTargetDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            EditorGUIUtility.singleLineHeight * 2 + EditorGUIUtility.standardVerticalSpacing;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var row = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            var renderer = property.FindPropertyRelative("Renderer");
            EditorGUI.PropertyField(row, renderer, label);
            row.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            var shapes = property.FindPropertyRelative("BlendShapes");
            using (new EditorGUI.DisabledScope((renderer.objectReferenceValue as SkinnedMeshRenderer)?.sharedMesh == null))
                if (GUI.Button(row, "Select BlendShape Regions (" + shapes.arraySize + ")"))
                    PopupWindow.Show(row, new BlendShapeRegionSelector(property.serializedObject.targetObject, property.propertyPath));
            EditorGUI.EndProperty();
        }
    }

}
