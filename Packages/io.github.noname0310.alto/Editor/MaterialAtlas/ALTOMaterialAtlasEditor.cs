using UnityEditor;
using UnityEngine;
using Noname.AvatarTools.Build;

namespace Noname.AvatarTools.MaterialAtlas
{
    [CustomEditor(typeof(ALTOMaterialAtlas))]
    internal sealed class ALTOMaterialAtlasEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            RendererSelectionInspector.Draw(serializedObject, "TargetRenderers", false);
            var existing = serializedObject.FindProperty("ExistingAtlases");
            EditorGUILayout.PropertyField(existing);
            using (new EditorGUI.DisabledScope(existing.enumValueIndex == (int)ExistingAtlasPolicy.Chain))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("MaterialConversion"));
            DrawPropertiesExcluding(serializedObject, "m_Script", "ScopeRoot", "ApplyOnPlatforms", "TargetSelection", "TargetRenderers", "ExistingAtlases", "MaterialConversion");
            EditorGUILayout.HelpBox("Existing Atlases: Preserve keeps overlapping TTT atlases and their material consumers outside this conversion. " +
                "Replace Fully Covered intentionally replaces their settings with these groups, only when all material consumers are covered and the existing atlas uses UV0; partial overlaps are preserved. " +
                "Chain waits for TTT to finish, then packs its completed textures and UVs into the final atlas. Chain overrides Material Conversion timing, can resample textures twice, and may use atlas space less efficiently.", MessageType.Info);
            EditorGUILayout.HelpBox("Material Conversion explicitly chooses Before Generators or After Generators. Before Generators prepares Reference Materials and copies dependent animation assets before other tools can discard input shaders. " +
                "This changes shader-dependent generator behavior. Shared out-of-scope consumers and incompatible final animations stop the build. Texture packing always runs after generators; generated inputs are checked again then.", MessageType.Info);
            EditorGUILayout.HelpBox("Empty Scope Root uses this object's subtree. " +
                "Each group's Material Selection explicitly chooses all unassigned materials or only listed materials. An empty Selected Materials list selects nothing. " +
                "Only mapped textures are transferred. Other source shader settings are replaced by the Reference Material. " +
                "Compatible numeric shader animations are retained. Texture swaps, animated atlas coordinates and lost preserved outputs retain their renderers or cause an error. " +
                "Atlas generation runs only during builds. ALTO Trace And Optimize Meshes can merge compatible renderers and material slots afterwards. " +
                "An installed AAO optimizer can also apply its own compatible merges.", MessageType.Info);
            if (MaterialAtlasBackend.Capture == null)
                EditorGUILayout.HelpBox("Install TexTransTool 1.0.2 to use this component.", MessageType.Warning);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
