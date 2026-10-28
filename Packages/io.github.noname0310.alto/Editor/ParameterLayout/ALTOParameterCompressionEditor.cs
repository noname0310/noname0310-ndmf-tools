using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.ParameterLayout
{
    [CustomEditor(typeof(ALTOParameterCompression))]
    internal sealed class ALTOParameterCompressionEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var mode = serializedObject.FindProperty("TimeMultiplexing");
            EditorGUILayout.PropertyField(mode);
            if (mode.enumValueIndex != (int)ParameterMultiplexingMode.Disabled)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("TargetBitBudget"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("PacketHoldSeconds"));
                if (mode.enumValueIndex == (int)ParameterMultiplexingMode.SelectedInputs)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("MultiplexedParameters"), true);
                if (mode.enumValueIndex == (int)ParameterMultiplexingMode.AutomaticByPriority)
                    EditorGUILayout.HelpBox("Compress persistent menu inputs first, then internal computations, runtime-driven values, and inferred external inputs last. " +
                        "Stop when the budget fits. External dependencies share the last priority; known momentary paths and explicit preservation remain native. " +
                        "Input sources are inferred from the final build, not from facial asset names. OSC can also write ordinary menu inputs. " +
                        "Visible Parameter Policy preservation also applies to the shared network contract when its freezing targets another platform. " +
                        "The build report lists each decision and warns if inferred external inputs must be compressed.", MessageType.Info);
                if (mode.enumValueIndex == (int)ParameterMultiplexingMode.AllSynchronizedInputs)
                    EditorGUILayout.HelpBox("All Synchronized Inputs includes non-menu, sensor-driven and momentary inputs unless preserved. " +
                        "Continuous values update at snapshot rate, and short changes may be missed. Keep inputs that need immediate response or event delivery in Keep Native Parameters.", MessageType.Warning);
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("RemoveUnusedSynchronization"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("KeepNativeParameters"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Integers"), true);
            serializedObject.ApplyModifiedProperties();
            var component = (ALTOParameterCompression)target;
            var descriptor = component.GetComponentInParent<VRCAvatarDescriptor>(true);
            if (descriptor == null || !descriptor.GetComponentsInChildren<ALTOParameterLayout>(true).Any(c => c.enabled))
                EditorGUILayout.HelpBox("Add ALTO Parameter Layout to this avatar. Compression changes its network contract on every platform.", MessageType.Error);
            EditorGUILayout.HelpBox("Automatic By Priority is the default; Keep Native Parameters is an optional override for inputs that must never be multiplexed. " +
                "Automatic Menu Inputs restricts automatic selection to persistent menus. Both run on the reference platform only when the budget is exceeded. " +
                "Automatic Menu Inputs " +
                "selects persistent toggles and radial sliders, excludes momentary/puppet activation, sensor-dependent and preserved inputs, " +
                "and chooses the shortest schedule that fits. Selected Inputs uses only its explicit sources/names/prefixes. Empty selection selects nothing. " +
                "All Synchronized Inputs explicitly selects all synchronized user inputs except preserved inputs; it is never enabled automatically to resolve a budget error.", MessageType.Info);
            EditorGUILayout.HelpBox("Time multiplexing captures a full snapshot, sends typed batches and applies complete received snapshots together. " +
                "Missed or out-of-order batches restart reception. Timing includes network and Animator delays; this is not reliable event delivery. " +
                "Keep latency-sensitive inputs in Keep Native Parameters. Parameter names and Saved values remain local and unchanged.", MessageType.Info);
            EditorGUILayout.HelpBox("The reference-platform upload records both integer and time-multiplexing contracts. Other platforms reuse those exact mappings. " +
                "Each row explicitly limits transmitted values to Minimum..Maximum. Out-of-range values are clamped remotely; local OSC values are unchanged. " +
                "Menu values alone do not prove an OSC input's range.", MessageType.Info);
            EditorGUILayout.HelpBox("N-bit Ints save 8-N bits and add an Animator transport layer. Independent Bool delivery may briefly decode an intermediate value. " +
                "Integer bit encoding is separate from time multiplexing. Keep latency-sensitive inputs in Keep Native Parameters. " +
                "The original local name, type, default and Saved setting remain intact.", MessageType.Info);
            foreach (var rule in component.Integers ?? new())
            {
                if (rule == null) continue;
                try
                {
                    int bits = IntegerEncoding.Width(rule.Minimum, rule.Maximum);
                    EditorGUILayout.LabelField(rule.Name ?? "Unnamed", bits + " Bool slots; saves " + (8 - bits) + " bits");
                }
                catch (System.Exception exception) { EditorGUILayout.HelpBox(exception.Message, MessageType.Error); }
            }
        }
    }
}
