using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.ObjectOptimization
{
    internal static class ConstantTransformOutputs
    {
        private static readonly string[] Groups = { "m_LocalPosition", "m_LocalScale", "m_LocalRotation" };

        internal static int Apply(AvatarAnimationIndex index, ObjectOptimizationState settings,
            Func<Transform, bool> allowInitialChanges, ICollection<string> messages, OptimizationPolicy numericPolicy = null,
            ObjectAnalysisSnapshot snapshot = null)
        {
            using var cost = AnalysisCost.Measure("Constant output consumer/local poses");
            if (index.UnsafeReason != null || ObjectOptimizationSafety.UnsupportedRuntime(index.Avatar) != null) return 0;
            var readers = snapshot?.InitialReaders ?? InitialPoseReaders.Analyze(index.Avatar, animations: index);
            var changes = new List<(Transform Target, string Group, Vector4 Value, bool ChangesInitialValue, ConstantPropertyProof[] Proofs, NumericalOptimization Consumed)>();
            foreach (var target in index.Avatar.GetComponentsInChildren<Transform>(true))
            {
                if (target is RectTransform || readers.Frames.Contains(target) || !settings.Scope.Contains(target) ||
                    settings.Protected(target)) continue;
                bool allowRounding = numericPolicy?.AllowsSmallNumericalDifferences(target, NumericalOptimization.AnimationOutputs) == true &&
                    StaticTransformProof.AllowsSubtreeRounding(numericPolicy, target, NumericalOptimization.AnimationOutputs) &&
                    !readers.CullingFrames.Contains(target);
                foreach (string group in Groups)
                {
                    bool rotation = group == "m_LocalRotation";
                    Vector4 initial = rotation ? Components(target.localRotation) :
                        group == "m_LocalPosition" ? (Vector4)target.localPosition : (Vector4)target.localScale;
                    int channels = rotation ? 4 : 3;
                    var queries = new ConstantPropertyQuery[channels]; bool any = false;
                    for (int i = 0; i < channels; i++)
                    {
                        string name = group + "." + "xyzw"[i]; any |= index.Writes(target, name);
                        queries[i] = new ConstantPropertyQuery(name, ConstantOutputValue.Scalar(initial[i]));
                    }
                    if (!any || HasAlternateWriter(index, target, group, queries) ||
                        !index.ConstantOutputs.TryProveGroup(target, queries, out var proofs, out _, allowRounding)) continue;
                    var value = new Vector4(proofs[0].Value.Number, proofs[1].Value.Number, proofs[2].Value.Number,
                        rotation ? proofs[3].Value.Number : 0);
                    var originalValue = value;
                    if (rotation && !ConstantQuaternion(proofs, value, allowRounding, out value)) continue;
                    if (rotation && index.ConstantOutputs.MayBlendStates(proofs[0]))
                    {
                        // Native transition interpolation can change the normalized
                        // quaternion by an ULP even when both states hold identical
                        // float-unit components. Clip/1D equality is not that proof.
                        if (!allowRounding || !index.ConstantOutputs.HasFiniteTransitions(proofs[0]))
                        {
                            messages.Add("Retained constant quaternion: strict output proof cannot remove transition normalization.");
                            continue;
                        }
                    }
                    bool changedInitial = rotation ? !SameRotation(initial, originalValue) : proofs.Any(p => p.ChangesInitialValue);
                    if (changedInitial && (readers.CullingFrames.Contains(target) || allowInitialChanges?.Invoke(target) != true ||
                        proofs.Any(p => p.Writers.Any(w => w.PreservedInput)))) continue;
                    bool numerical = index.ConstantOutputs.RequiresNumericalPermission(target, queries, proofs);
                    if (rotation && allowRounding)
                    {
                        numerical |= index.ConstantOutputs.MayBlendStates(proofs[0]) ||
                            !ConstantQuaternion(proofs, originalValue, false, out _) ||
                            changedInitial && Enumerable.Range(0, 4).Any(i => !MotionAnalysis.Same(originalValue[i], value[i]));
                    }
                    changes.Add((target, group, value, changedInitial, proofs,
                        numerical ? NumericalOptimization.AnimationOutputs : NumericalOptimization.None));
                }
            }
            var removals = new Dictionary<nadena.dev.ndmf.animator.VirtualClip, HashSet<EditorCurveBinding>>();
            var appliedProofs = new AppliedProofReport();
            foreach (var change in changes)
            {
                // A sign-negated quaternion is the same rotation. Keep authored
                // storage when the physical initial pose has not changed.
                if (change.ChangesInitialValue)
                {
                    if (change.Group == "m_LocalPosition") change.Target.localPosition = change.Value;
                    else if (change.Group == "m_LocalScale") change.Target.localScale = change.Value;
                    else change.Target.localRotation = Rotation(change.Value);
                }
                foreach (var writer in change.Proofs.SelectMany(p => p.Writers))
                {
                    if (!removals.TryGetValue(writer.Clip, out var bindings)) removals.Add(writer.Clip, bindings = new());
                    bindings.Add(writer.Binding);
                }
                appliedProofs.Record(change.Consumed, change.ChangesInitialValue);
            }
            int count = 0;
            foreach (var removal in removals)
            { MotionAnalysis.RemoveCurves(removal.Key, removal.Value); count += removal.Value.Count; }
            if (count != 0) messages.Add("Baked " + count + " constant local-pose curves (" +
                changes.Count(c => c.ChangesInitialValue) + " changed initial transform groups). " +
                "Simulation/sensor pose readers and Animator culling inputs were preserved.");
            appliedProofs.AppendTo(messages, "constant local poses");
            return count;
        }

        private static Vector4 Components(Quaternion value) => new(value.x, value.y, value.z, value.w);
        private static Quaternion Rotation(Vector4 value) => new(value.x, value.y, value.z, value.w);

        // Initial-value permission is independent of roundoff permission. Only
        // exact physical sign equivalence preserves the authored initial pose.
        private static bool SameRotation(Vector4 first, Vector4 second) => first.Equals(second) || first.Equals(-second);

        private static bool HasAlternateWriter(AvatarAnimationIndex index, Transform target, string group,
            ConstantPropertyQuery[] queries)
        {
            string member = group.Substring("m_Local".Length);
            foreach (var use in index.ForObject(target))
            {
                string property = use.Binding.propertyName;
                if ((property.IndexOf(member, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    group == "m_LocalRotation" && property.IndexOf("Euler", StringComparison.OrdinalIgnoreCase) >= 0) &&
                    !queries.Any(q => q.Name == property)) return true;
            }
            return false;
        }

        private static bool ConstantQuaternion(ConstantPropertyProof[] proofs, Vector4 value, bool allowRounding, out Vector4 output)
        {
            output = value;
            // Strict mode requires a float-unit quaternion. The numerical policy
            // additionally admits the small normalization error of one identical
            // finite pose, with a separate matrix tolerance check below.
            float squared = Vector4.Dot(value, value);
            if (!MotionAnalysis.Finite(squared) || squared <= 0 ||
                (allowRounding ? !NumericalEquivalence.Same(squared, 1) : squared != 1)) return false;
            // Scalar interpolation is not a quaternion interpolation proof. All
            // complete writer groups must hold the same quaternion for supported
            // clip/1D evaluation. State-transition normalization is checked separately.
            foreach (var proof in proofs)
            foreach (var writer in proof.Writers)
                if (!MotionAnalysis.Constant(writer.Curve, out float number, preserveZeroSign: !allowRounding) ||
                    !ConstantOutputValue.Scalar(number).Equals(proof.Value) && !(allowRounding && number == proof.Value.Number)) return false;
            if (allowRounding)
            {
                // Only normalization of the identical complete pose is covered.
                // This does not equate arbitrary nearby quaternion trajectories.
                var normalized = Rotation(value).normalized;
                if (!NumericalEquivalence.Same(Matrix4x4.Rotate(Rotation(value)), Matrix4x4.Rotate(normalized))) return false;
                output = Components(normalized);
            }
            return true;
        }
    }
}
