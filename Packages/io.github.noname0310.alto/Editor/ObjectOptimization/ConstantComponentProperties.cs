using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.ObjectOptimization
{
    internal static class ConstantComponentProperties
    {
        // Explicit native contracts, not a reflection-based authorization. Spatial
        // sensor/solver fields permit invariant-curve removal only: a changed
        // initialization can produce lasting contacts or simulation state.
        private static readonly string[][] RendererFields = {
            new[] { "m_CastShadows" }, new[] { "m_ReceiveShadows" }, new[] { "m_LightProbeUsage" },
            new[] { "m_ReflectionProbeUsage" }, new[] { "m_SortingOrder" }, new[] { "m_SortingLayerID" },
            new[] { "m_MotionVectors" }, new[] { "m_DynamicOccludee" }
        };
        private static readonly string[][] SkinnedFields = { new[] { "m_UpdateWhenOffscreen" } };
        private static readonly string[][] LightFields = {
            new[] { "m_Intensity" }, new[] { "m_Range" }, new[] { "m_SpotAngle" },
            new[] { "m_Color.r", "m_Color.g", "m_Color.b", "m_Color.a" }, new[] { "m_Shadows.m_Strength" }
        };
        private static readonly string[][] SpatialFields = {
            new[] { "radius" }, new[] { "height" },
            new[] { "position.x", "position.y", "position.z" },
            new[] { "rotation.x", "rotation.y", "rotation.z", "rotation.w" }
        };
        // Stateful components keep their authored configuration. Removing an
        // invariant write is useful, but a changed value can alter playback,
        // emitted particles, collision callbacks or later replay state.
        private static readonly string[][] AudioFields = {
            new[] { "m_Volume" }, new[] { "m_Pitch" }, new[] { "Loop" }, new[] { "Mute" },
            new[] { "Priority" }, new[] { "Pan2D" }, new[] { "DopplerLevel" },
            new[] { "MinDistance" }, new[] { "MaxDistance" }, new[] { "rolloffMode" },
            new[] { "BypassEffects" }, new[] { "BypassListenerEffects" }, new[] { "BypassReverbZones" }
        };
        private static readonly string[][] ParticleFields = {
            new[] { "InitialModule.startLifetime.scalar" }, new[] { "InitialModule.startLifetime.minScalar" },
            new[] { "InitialModule.startSpeed.scalar" }, new[] { "InitialModule.startSpeed.minScalar" },
            new[] { "InitialModule.startSize.scalar" }, new[] { "InitialModule.startSize.minScalar" },
            new[] { "InitialModule.startSizeY.scalar" }, new[] { "InitialModule.startSizeZ.scalar" },
            new[] { "InitialModule.gravityModifier.scalar" }, new[] { "InitialModule.gravityModifier.minScalar" },
            new[] { "EmissionModule.rateOverTime.scalar" }, new[] { "EmissionModule.rateOverTime.minScalar" },
            new[] { "EmissionModule.rateOverDistance.scalar" }, new[] { "EmissionModule.rateOverDistance.minScalar" }
        };

        internal static int Apply(AvatarAnimationIndex index, ObjectOptimizationState settings,
            Func<Transform, bool> allowInitialChanges, ICollection<string> messages, OptimizationPolicy numericPolicy = null,
            ObjectAnalysisSnapshot snapshot = null)
        {
            using var cost = AnalysisCost.Measure("Constant output consumer/component properties");
            if (index.UnsafeReason != null || ObjectOptimizationSafety.UnsupportedRuntime(index.Avatar) != null) return 0;
            var changes = new List<(Component Target, ConstantPropertyQuery[] Queries, ConstantPropertyProof[] Proofs, NumericalOptimization Consumed)>();
            InitialPoseReaders.Inputs poseReaders = null;
            Dictionary<Transform, HashSet<string>> audioWriters = null;
            var nativeBindings = new NativeAnimationBindings(index.Avatar);
            foreach (var component in index.Avatar.GetComponentsInChildren<Component>(true))
            {
                if (component == null || !settings.Scope.Contains(component.transform) || settings.Protected(component)) continue;
                var type = component.GetType(); bool visual;
                IEnumerable<string[]> groups;
                if (type == typeof(SkinnedMeshRenderer) || type == typeof(MeshRenderer))
                {
                    if (component.GetComponent<Cloth>() != null) continue;
                    visual = true; groups = type == typeof(SkinnedMeshRenderer) ? RendererFields.Concat(SkinnedFields) : RendererFields;
                }
                else if (type == typeof(Light)) { visual = true; groups = LightFields; }
                else if (type == typeof(VRCContactReceiver) || type == typeof(VRCContactSender) || type == typeof(VRCPhysBoneCollider))
                { visual = false; groups = SpatialFields; }
                else if (type == typeof(AudioSource)) { visual = false; groups = AudioFields; }
                else if (type == typeof(ParticleSystem)) { visual = false; groups = ParticleFields; }
                else continue;
                if (index.ForObject(component).Count == 0) continue;
                if (component is AudioSource || component is ParticleSystem)
                {
                    var supportedFields = groups.SelectMany(g => g).ToHashSet();
                    var unsupported = index.ForObject(component).Select(u => u.Binding.propertyName).Distinct()
                        .Where(n => !supportedFields.Contains(n)).ToArray();
                    if (unsupported.Length != 0) messages.Add("Retained " + type.Name + " fields without a lifecycle/output adapter: " +
                        string.Join(", ", unsupported) + ".");
                }
                using var serialized = new SerializedObject(component);
                foreach (var fields in groups)
                {
                    if (component is AudioSource && fields.Any(f => index.Writes(component, f)))
                    {
                        audioWriters ??= AudioNativeWriters(index);
                        if (audioWriters.TryGetValue(component.transform, out var native) && fields.Any(native.Contains))
                        { messages.Add("Retained AudioSource." + fields[0] + ": a native audio behaviour or spatializer can write it."); continue; }
                    }
                    var queries = new ConstantPropertyQuery[fields.Length]; bool supported = true;
                    for (int i = 0; i < fields.Length; i++)
                    {
                        if (!index.Writes(component, fields[i]) || !ConstantOutputValue.TryRead(serialized.FindProperty(fields[i]), out var initial))
                        { supported = false; break; }
                        queries[i] = new ConstantPropertyQuery(fields[i], initial);
                    }
                    bool allowRounding = component is Light &&
                        numericPolicy?.AllowsSmallNumericalDifferences(component.transform, NumericalOptimization.AnimationOutputs) == true;
                    if (allowRounding)
                    {
                        poseReaders ??= snapshot?.InitialReaders ?? InitialPoseReaders.Analyze(index.Avatar, animations: index);
                        allowRounding = !poseReaders.Components.Contains(component) && !poseReaders.CullingComponents.Contains(component);
                    }
                    if (!supported || !index.ConstantOutputs.TryProveGroup(component, queries, out var proofs, out _, allowRounding)) continue;
                    if (!visual && proofs.Any(p => p.ChangesInitialValue) && (component is AudioSource || component is ParticleSystem))
                    { messages.Add("Retained " + type.Name + "." + fields[0] + ": changed initialization requires a playback/simulation lifecycle proof."); continue; }
                    if (proofs.Any(p => p.ChangesInitialValue) && (!visual || allowInitialChanges?.Invoke(component.transform) != true ||
                        proofs.Any(p => p.Writers.Any(w => w.PreservedInput)))) continue;
                    if (proofs.Any(p => p.ChangesInitialValue) && (component is Light ||
                        fields[0] == "m_UpdateWhenOffscreen" || fields[0] == "m_CastShadows" ||
                        fields[0] == "m_DynamicOccludee"))
                    {
                        // Visibility (including shadows) can start or stop Animator
                        // evaluation. Offscreen skins can also feed simulations.
                        // Neither lasting effect is a visual initialization change.
                        poseReaders ??= snapshot?.InitialReaders ?? InitialPoseReaders.Analyze(index.Avatar, animations: index);
                        if (poseReaders.CullingComponents.Contains(component) ||
                            fields[0] == "m_UpdateWhenOffscreen" && poseReaders.Components.Contains(component)) continue;
                    }
                    if (proofs.Any(p => p.ChangesInitialValue) &&
                        !nativeBindings.Supports(component, proofs.SelectMany(p => p.Writers), out var nativeReason))
                    { messages.Add("Retained component property: " + nativeReason + "."); continue; }
                    for (int i = 0; i < fields.Length; i++)
                        if (!Representable(serialized.FindProperty(fields[i]), proofs[i].Value.Number)) supported = false;
                    if (supported) changes.Add((component, queries, proofs,
                        index.ConstantOutputs.RequiresNumericalPermission(component, queries, proofs)
                            ? NumericalOptimization.AnimationOutputs : NumericalOptimization.None));
                }
            }
            var removals = new Dictionary<nadena.dev.ndmf.animator.VirtualClip, HashSet<EditorCurveBinding>>();
            int changedInitial = 0;
            var appliedProofs = new AppliedProofReport();
            foreach (var group in changes.GroupBy(c => c.Target))
            {
                using var serialized = new SerializedObject(group.Key);
                foreach (var change in group)
                for (int i = 0; i < change.Queries.Length; i++)
                {
                    var proof = change.Proofs[i];
                    if (proof.ChangesInitialValue)
                    { Write(serialized.FindProperty(change.Queries[i].Name), proof.Value.Number); changedInitial++; }
                    foreach (var writer in proof.Writers)
                    {
                        if (!removals.TryGetValue(writer.Clip, out var bindings)) removals.Add(writer.Clip, bindings = new());
                        bindings.Add(writer.Binding);
                    }
                }
                serialized.ApplyModifiedPropertiesWithoutUndo();
                foreach (var change in group) appliedProofs.Record(change.Consumed, change.Proofs.Any(p => p.ChangesInitialValue));
            }
            int count = 0;
            foreach (var removal in removals)
            { MotionAnalysis.RemoveCurves(removal.Key, removal.Value); count += removal.Value.Count; }
            if (count != 0) messages.Add("Baked " + count + " constant component-property curves (" + changedInitial + " changed initial values).");
            appliedProofs.AppendTo(messages, "constant component properties");
            return count;
        }

        private static Dictionary<Transform, HashSet<string>> AudioNativeWriters(AvatarAnimationIndex index)
        {
            var result = new Dictionary<Transform, HashSet<string>>();
            void Add(Transform target, string property)
            {
                if (!result.TryGetValue(target, out var fields)) result.Add(target, fields = new());
                fields.Add(property);
            }
            foreach (var audio in Noname.AvatarTools.ParameterPolicy.ParameterSpecialization.Behaviours(
                index.Controllers.SelectMany(c => c.AllReachableNodes())).OfType<VRC_AnimatorPlayAudio>().Distinct())
                foreach (var target in AnimationUsage.Resolve(index.Avatar.transform, index.Avatar.transform, audio.SourcePath ?? "", index.Paths))
                {
                    if (audio.VolumeApplySettings != VRC_AnimatorPlayAudio.ApplySettings.NeverApply) Add(target, "m_Volume");
                    if (audio.PitchApplySettings != VRC_AnimatorPlayAudio.ApplySettings.NeverApply) Add(target, "m_Pitch");
                    if (audio.LoopApplySettings != VRC_AnimatorPlayAudio.ApplySettings.NeverApply) Add(target, "Loop");
                }
            foreach (var source in index.Avatar.GetComponentsInChildren<AudioSource>(true))
                if (source.GetComponents<Component>().Any(c => c != null && AudioOutputDependencies.IsModifier(c)))
                    foreach (string property in new[] { "MinDistance", "MaxDistance", "DopplerLevel", "rolloffMode" }) Add(source.transform, property);
            return result;
        }

        private static bool Representable(SerializedProperty property, float value)
        {
            if (!MotionAnalysis.Finite(value)) return false;
            // Animator narrows this native field to a signed 16-bit integer,
            // whereas SerializedProperty clamps it. Retain overflow tracks.
            if (property.propertyPath == "m_SortingOrder" && (value < short.MinValue || value > short.MaxValue)) return false;
            if (property.propertyType == SerializedPropertyType.Float) return true;
            if (property.propertyType == SerializedPropertyType.Boolean) return value == 0 || value == 1;
            if (property.propertyType == SerializedPropertyType.Enum && (value < 0 || value >= property.enumNames.Length)) return false;
            return value >= int.MinValue && (double)value <= int.MaxValue && value == Math.Truncate(value);
        }

        private static void Write(SerializedProperty property, float value)
        {
            if (property.propertyType == SerializedPropertyType.Float) property.floatValue = value;
            else if (property.propertyType == SerializedPropertyType.Boolean) property.boolValue = value != 0;
            else property.intValue = (int)value;
        }
    }
}
