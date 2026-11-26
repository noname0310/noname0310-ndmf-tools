using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
using UnityEngine;

namespace Noname.AvatarTools.AnimatorOptimization
{
    internal static class ConstantMaterialSlots
    {
        internal static int Apply(AvatarAnimationIndex index, Func<Renderer, bool> allowInitialChanges,
            Func<Renderer, bool> preserved, AnimatorOptimizationReport report)
        {
            using var cost = AnalysisCost.Measure("Constant output consumer/material slots");
            if (index.UnsafeReason != null || ObjectOptimizationSafety.UnsupportedRuntime(index.Avatar) != null) return 0;
            var changes = new List<(Renderer Renderer, int Slot, ConstantPropertyProof Proof)>();
            foreach (var renderer in index.Avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer) || preserved?.Invoke(renderer) == true ||
                    renderer.HasPropertyBlock()) continue;
                var materials = renderer.sharedMaterials;
                var properties = new HashSet<string>(StringComparer.Ordinal);
                foreach (var use in index.ForObject(renderer))
                {
                    string property = use.Binding.propertyName;
                    if (!TrySlot(property, out int slot) || slot >= materials.Length || !properties.Add(property)) continue;
                    if (!index.ConstantOutputs.TryProve(renderer, property, ConstantOutputValue.Object(materials[slot]), out var proof, out _)) continue;
                    if (proof.Value.Reference != null && !(proof.Value.Reference is Material)) continue;
                    if (proof.ChangesInitialValue && (allowInitialChanges?.Invoke(renderer) != true || proof.Writers.Any(w => w.PreservedInput))) continue;
                    if (slot == 0 && proof.ChangesInitialValue &&
                        !MaterialSlotDefaultReaders.CanReplaceFirstMaterial(index, renderer, (Material)proof.Value.Reference)) continue;
                    changes.Add((renderer, slot, proof));
                }
            }
            // Plan against one immutable writer snapshot, then commit references and
            // all aliases together. The input material assets are never modified.
            foreach (var group in changes.GroupBy(c => c.Renderer))
            {
                var materials = group.Key.sharedMaterials;
                foreach (var change in group) materials[change.Slot] = (Material)change.Proof.Value.Reference;
                group.Key.sharedMaterials = materials;
            }
            var curves = changes.SelectMany(c => c.Proof.Writers).GroupBy(w => w.Clip);
            int count = 0;
            foreach (var group in curves)
            {
                var bindings = group.Select(w => w.Binding).Distinct().ToArray();
                MotionAnalysis.RemoveCurves(group.Key, bindings); count += bindings.Length;
            }
            report.MaterialSlotCurves += count;
            report.InitialOutputs += changes.Count(c => c.Proof.ChangesInitialValue);
            foreach (var change in changes) report.ConstantOutputProofs.Record(initializationChanged: change.Proof.ChangesInitialValue);
            return count;
        }

        internal static bool TrySlot(string property, out int slot)
        {
            const string prefix = "m_Materials.Array.data[";
            slot = -1;
            return property != null && property.StartsWith(prefix, StringComparison.Ordinal) && property.EndsWith("]", StringComparison.Ordinal) &&
                int.TryParse(property.Substring(prefix.Length, property.Length - prefix.Length - 1),
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out slot) && slot >= 0;
        }
    }
}
