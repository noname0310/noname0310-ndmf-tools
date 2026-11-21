using System.Linq;
using System.Collections.Generic;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.ParameterLayout
{
    [CustomEditor(typeof(ALTOParameterCompression))]
    internal sealed class ALTOParameterCompressionEditor : Editor
    {
        private readonly HashSet<int> expandedPolicies = new();

        private void DrawSharedPolicies(ALTOParameterCompression component, bool avatarDriven)
        {
            var avatar = component.GetComponentInParent<VRCAvatarDescriptor>(true);
            if (avatar == null) return;
            var policies = avatar.GetComponentsInChildren<ALTOAnimatorParameterPolicy>(true).Where(p => p.UsePreserveForCompression).ToArray();
            EditorGUILayout.LabelField("Shared Policy Inputs", EditorStyles.boldLabel);
            if (policies.Length == 0) { EditorGUILayout.LabelField("No Animator Parameter Policy shares its Preserve list."); return; }
            foreach (var policy in policies)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        int id = policy.GetInstanceID();
                        bool expanded = EditorGUILayout.Foldout(expandedPolicies.Contains(id), policy.gameObject.name, true);
                        if (expanded) expandedPolicies.Add(id); else expandedPolicies.Remove(id);
                        if (GUILayout.Button("Select Policy", GUILayout.Width(100)))
                        { Selection.activeObject = policy; EditorGUIUtility.PingObject(policy); }
                    }
                    bool included = false; string pending = null;
                    try { included = policy.enabled && BuildPlatformFilters.PreviewIncludesSettings(policy.transform, avatar.transform,
                        EditorUserBuildSettings.activeBuildTarget, out pending); }
                    catch (System.Exception error) { pending = error.Message; }
                    EditorGUILayout.LabelField(included
                        ? avatarDriven ? "External input protection" : "Keep priority: " + policy.CompressionPriority + " (higher is compressed later)"
                        : "Excluded for the current build target or disabled", EditorStyles.wordWrappedMiniLabel);
                    if (pending != null) EditorGUILayout.HelpBox(pending, MessageType.Info);
                    if (!expandedPolicies.Contains(policy.GetInstanceID())) continue;
                    EditorGUILayout.LabelField(AnimationUtility.CalculateTransformPath(policy.transform, avatar.transform), EditorStyles.wordWrappedMiniLabel);
                    using var serialized = new SerializedObject(policy);
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.PropertyField(serialized.FindProperty("UsePreserveForCompression"));
                        EditorGUILayout.PropertyField(serialized.FindProperty("CompressionPriority"));
                        EditorGUILayout.PropertyField(serialized.FindProperty("ApplyOnPlatforms"), new GUIContent("Freezing platforms only"));
                        EditorGUILayout.PropertyField(serialized.FindProperty("FreezeMode"));
                        var preserve = serialized.FindProperty("PreserveParameters"); preserve.isExpanded = true;
                        var nested = preserve.Copy(); var end = preserve.GetEndProperty();
                        while (nested.NextVisible(true) && !SerializedProperty.EqualContents(nested, end))
                            if (nested.hasVisibleChildren) nested.isExpanded = true;
                        EditorGUILayout.PropertyField(preserve, true);
                    }
                }
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var assumption = serializedObject.FindProperty("InputAssumption");
            assumption.enumValueIndex = EditorGUILayout.Popup("Input Assumption", assumption.enumValueIndex, new[] {
                "Treat all as external; allow compression", "Assume avatar-driven except listed inputs" });
            bool avatarDriven = assumption.enumValueIndex == (int)ParameterInputAssumption.AvatarDrivenExceptListed;
            EditorGUILayout.HelpBox(avatarDriven
                ? "Unlisted inputs are assumed to have no OSC or unregistered external writer. List every external input below: listed inputs and their upstream dependencies are never automatically compressed. This assumption can change behavior if external inputs are omitted."
                : "Every input may be externally controlled. Multiplexing is still allowed when needed. Higher Keep priorities are compressed later, not excluded. No automatic range narrowing relies on a missing OSC declaration.",
                avatarDriven ? MessageType.Warning : MessageType.Info);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("KeepParameters"), true);
            DrawSharedPolicies((ALTOParameterCompression)target, avatarDriven);
            EditorGUILayout.HelpBox("Animator Parameter Policy can share its own Preserve list here with Use Preserve for Compression. " +
                "Shared selections use this component's input assumption and their configured priority, on PC and mobile.", MessageType.Info);
            using (new EditorGUI.DisabledScope(!avatarDriven))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("AllowUnsignedBitReduction"));
            var mode = serializedObject.FindProperty("TimeMultiplexing");
            EditorGUILayout.PropertyField(mode);
            if (mode.enumValueIndex != (int)ParameterMultiplexingMode.Disabled)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("TargetBitBudget"));
                var transmission = serializedObject.FindProperty("TransmissionMode");
                EditorGUILayout.PropertyField(transmission);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("PacketHoldSeconds"));
                if (transmission.enumValueIndex == (int)ParameterTransmissionMode.LowLatency)
                    EditorGUILayout.HelpBox("Low Latency assumes the fields from one network packet are observed together. " +
                        "A 0.1-second packet hold gives the shortest ordinary schedule; periodic longer sweeps help slow observers recover. " +
                        "Use Guarded to tolerate independently delayed fields. Other platforms use the reference upload's recorded mode and timing.", MessageType.Info);
                if (mode.enumValueIndex == (int)ParameterMultiplexingMode.SelectedInputs)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("MultiplexedParameters"), true);
                if (mode.enumValueIndex == (int)ParameterMultiplexingMode.AutomaticByPriority)
                    EditorGUILayout.HelpBox("Compress persistent menu inputs first, then internal computations, runtime-driven values, and inferred external inputs last. " +
                        "Keep priority is applied before these inferred scheduling groups. Stop when the budget fits. Known momentary paths remain native. " +
                        "Scheduling hints do not prove the absence of OSC writers. Policy selections affect transport only when Use Preserve for Compression is enabled.", MessageType.Info);
                if (mode.enumValueIndex == (int)ParameterMultiplexingMode.AllSynchronizedInputs)
                    EditorGUILayout.HelpBox("All Synchronized Inputs includes non-menu, sensor-driven and momentary inputs unless preserved. " +
                        "Continuous values update at snapshot rate, and short changes may be missed. Input Assumption and Keep priorities still apply.", MessageType.Warning);
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("RemoveUnusedSynchronization"));
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Precision Overrides", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Unselected inputs retain the artist's bit width and signed/unsigned representation. " +
                "Source Overrides configure Int ranges and Float precision separately. Parameter Overrides show settings for one input's type and take priority. Keep Existing restores the artist's settings. " +
                "Explicit precision overrides can change listed inputs too; their transport priority or protection remains in effect.", MessageType.Info);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("PrecisionSourceOverrides"), new GUIContent("Source Overrides"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("PrecisionOverrides"), new GUIContent("Parameter Overrides"), true);
            serializedObject.ApplyModifiedProperties();
            var component = (ALTOParameterCompression)target;
            if ((component.PrecisionSourceOverrides ?? new()).Any(r => r?.Floats?.Precision == FloatPrecisionMode.Exact) ||
                (component.PrecisionOverrides ?? new()).Any(r => r?.Floats?.Precision == FloatPrecisionMode.Exact && r.Type != ParameterPrecisionType.Int))
                EditorGUILayout.HelpBox("Exact precision can require separate payload slots. Lowering one input's bit width may increase total transport cost or update delay. " +
                    "Minimum permits wider slots when sharing them is cheaper; the build report shows the selected width.", MessageType.Warning);
            var descriptor = component.GetComponentInParent<VRCAvatarDescriptor>(true);
            if (descriptor == null || !descriptor.GetComponentsInChildren<ALTOParameterLayout>(true).Any(c => c.enabled))
                EditorGUILayout.HelpBox("Add ALTO Parameter Layout to this avatar. Compression changes its network contract on every platform.", MessageType.Error);
            EditorGUILayout.HelpBox("Automatic By Priority is the default. Keep is a scheduling priority in external mode and strict protection in avatar-driven mode. " +
                "Automatic Menu Inputs restricts automatic selection to persistent menus. Both run on the reference platform only when the budget is exceeded. " +
                "Automatic Menu Inputs " +
                "selects persistent toggles and radial sliders, excludes momentary/puppet activation, sensor-dependent and preserved inputs, " +
                "and chooses the shortest schedule that fits. Selected Inputs uses only its explicit sources/names/prefixes. Empty selection selects nothing. " +
                "All Synchronized Inputs explicitly selects all synchronized user inputs except preserved inputs; it is never enabled automatically to resolve a budget error.", MessageType.Info);
            EditorGUILayout.HelpBox("Time multiplexing captures a full snapshot, sends typed batches and applies complete received snapshots together. " +
                "Missed or out-of-order batches restart reception. Timing includes network and Animator delays; this is not reliable event delivery. " +
                "Use higher Keep priorities for latency-sensitive inputs. Parameter names and Saved values remain local and unchanged.", MessageType.Info);
            EditorGUILayout.HelpBox("The reference-platform upload records both integer and time-multiplexing contracts. Other platforms reuse those exact mappings. " +
                "Each row explicitly limits transmitted values to Minimum..Maximum. Out-of-range values are clamped remotely; local OSC values are unchanged. " +
                "Menu values alone do not prove an OSC input's range.", MessageType.Info);
            EditorGUILayout.HelpBox("Integer encoding is automatic. Full native ranges need no Animator codec. Other ranges use a remapped native Int; " +
                "two-value ranges use one Bool because SDK numeric inputs require at least two bits. Existing uploaded Bool contracts remain supported. " +
                "Integer encoding is separate from time multiplexing. " +
                "The original local name, type, default and Saved setting remain intact.", MessageType.Info);
            EditorGUILayout.HelpBox("Minimum precision shares wider compatible slots or creates narrower slots according to total bit cost. " +
                "The shortest schedule within the budget is chosen first. Exact and unmodified artist settings retain their original quantization. " +
                "The reference upload records precision overrides for all other platforms.", MessageType.Info);
        }
    }
}
