using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Physics
{
    [CustomEditor(typeof(ALTOTraceAndOptimizePhysBones))]
    internal sealed class ALTOTraceAndOptimizePhysBonesEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update(); DrawPropertiesExcluding(serializedObject, "m_Script");
            var settings = (ALTOTraceAndOptimizePhysBones)target;
            var scope = settings.ScopeRoot != null ? settings.ScopeRoot : settings.transform;
            EditorGUILayout.HelpBox("Physics components in '" + scope.name + "', including generated components, are considered. " +
                "Analysis covers the whole avatar. Collider sharing preserves collision order and repeated collision applications. " +
                "Global collisions, animated components, external readers and protected consumers remain intact. " +
                "Use ALTO PhysBone Filter for intentionally removing simulations.", MessageType.Info);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
