using System;
using UnityEditor;
using UnityEngine;
using VRC.Core;

namespace Noname.AvatarTools.ParameterLayout
{
    [CustomEditor(typeof(ALTOParameterLayout))]
    internal sealed class ALTOParameterLayoutEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update(); DrawPropertiesExcluding(serializedObject, "m_Script"); serializedObject.ApplyModifiedProperties();
            EditorGUILayout.HelpBox("This component controls the entire avatar's network layout, wherever it is attached. " +
                "Successful uploads on the Reference Platform record synchronized parameter names, types, order and any ALTO Parameter Compression encoding. " +
                "Other platforms fill missing slots with unused padding. Build analysis, manual baking and failed uploads never replace the confirmed record. " +
                "Uploading the reference platform without this component invalidates its previous record.", MessageType.Info);
            EditorGUILayout.HelpBox("Records are project files in ProjectSettings/ALTO/ParameterLayouts, keyed by Blueprint ID. " +
                "Keep them with the project when switching machines. This feature has no MA, AAO or VRCFury dependency. " +
                "External parameter compression/layout rewriting after NDMF is rejected; arbitrary encoded transport protocols are not inferred.", MessageType.Info);
            var descriptor = ((ALTOParameterLayout)target).FindDescriptor();
            var id = descriptor != null ? descriptor.GetComponent<PipelineManager>()?.blueprintId : null;
            if (!ParameterLayoutStore.ValidId(id))
            { EditorGUILayout.HelpBox("The reference-platform upload must assign a Blueprint ID before other platforms can use its record.", MessageType.Info); return; }
            try
            {
                var record = ParameterLayoutStore.Project.Read(id);
                if (record == null) { EditorGUILayout.HelpBox("No confirmed reference upload has been recorded for this avatar.", MessageType.Info); return; }
                EditorGUILayout.LabelField("Recorded Platform", record.ReferencePlatform.ToString());
                EditorGUILayout.LabelField("Synchronized Slots", record.Parameters.Count.ToString());
                EditorGUILayout.LabelField("Uploaded (UTC)", record.UploadedUtc);
                if (GUILayout.Button("Show Recorded Layout")) EditorUtility.RevealInFinder(ParameterLayoutStore.Project.PathFor(id));
            }
            catch (Exception exception) { EditorGUILayout.HelpBox(exception.Message, MessageType.Error); }
        }
    }
}
