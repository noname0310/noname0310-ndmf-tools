using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Noname.AvatarTools.ObjectOptimization
{
    internal static class StaticConstraintSources
    {
        internal static int Apply(GameObject avatar, ObjectOptimizationState settings, AvatarAnimationIndex animations,
            AvatarDependencyGraph references, StaticTransformProof proof, ICollection<string> messages)
        {
            if (!settings.FoldStaticTransformsIntoComponents || settings.Scope == null || proof.Mutations.Uncertain != null || references.UnsafeReason != null) return 0;
            int changes = 0;
            foreach (var constraint in avatar.GetComponentsInChildren<VRCConstraintBase>(true))
            {
                bool parentConstraint = constraint.GetType() == typeof(VRCParentConstraint);
                bool rotationConstraint = constraint.GetType() == typeof(VRCRotationConstraint);
                if ((!parentConstraint && !rotationConstraint) || !settings.Scope.Contains(constraint.transform) || settings.Protected(constraint) ||
                    !constraint.Locked || constraint.FreezeToWorld || constraint.SolveInLocalSpace || constraint.SmoothingFactor != 0 ||
                    references.ReferenceOwners.ContainsKey(constraint)) continue;
                var target = constraint.TargetTransform != null ? constraint.TargetTransform : constraint.transform;
                if (!settings.Scope.Contains(target) || settings.Protected(target)) continue;
                // No source-count, offset, solve-mode or source-weight animation. In
                // particular, retain the SDK's zero/negative/tiny-weight special cases.
                if (animations.ForObject(constraint).Any(u => !u.Complete || u.Binding.isPPtrCurve ||
                    (u.Binding.propertyName != "m_Enabled" && u.Binding.propertyName != "GlobalWeight"))) continue;
                var sources = constraint.Sources.ToArray();
                if (sources.Length == 0 || sources.Any(s => s.SourceTransform == null || !StaticTransformProof.Finite(s.Weight) || s.Weight < 0) ||
                    sources.Length == 1 && sources[0].Weight < 1e-6f || rotationConstraint && sources.Length != 1) continue;
                bool changed = false;
                for (int i = 0; i < sources.Length; i++)
                {
                    var source = sources[i]; var frame = source.SourceTransform;
                    if (!proof.CanSubstitute(frame, settings.Scope) || !settings.Scope.Contains(frame.parent) || settings.Protected(frame.parent) ||
                        frame == target || frame.IsChildOf(target)) continue;
                    // ParentConstraint offsets ignore source scale. A translated
                    // helper cannot be folded into a constant offset when avatar
                    // scaling may change; zero translation has no such dependency.
                    if (parentConstraint && !frame.localPosition.Equals(Vector3.zero)) continue;
                    bool allowRounding = proof.AllowsRounding(NumericalOptimization.Constraints, constraint.transform, frame, frame.parent) &&
                        proof.AllowsSubtreeRounding(target, NumericalOptimization.Constraints);
                    if (!allowRounding)
                    {
                        if (!StaticTransformProof.ExactLocalIdentity(frame)) continue;
                        // Preserve the authored Euler/offset values: converting
                        // even an identity quaternion through Euler can round.
                        source.SourceTransform = frame.parent;
                        sources[i] = source; changed = true; changes++;
                        continue;
                    }
                    var offset = frame.localRotation * source.ParentPositionOffset;
                    var rotation = frame.localRotation * Quaternion.Euler(parentConstraint ? source.ParentRotationOffset : ((VRCRotationConstraint)constraint).RotationOffset);
                    if (!StaticTransformProof.Finite(offset) || !StaticTransformProof.Finite(rotation)) continue;
                    source.SourceTransform = frame.parent;
                    if (parentConstraint) { source.ParentPositionOffset = offset; source.ParentRotationOffset = rotation.eulerAngles; }
                    else ((VRCRotationConstraint)constraint).RotationOffset = rotation.eulerAngles;
                    sources[i] = source; changed = true; changes++;
                }
                if (!changed) continue;
                var inputFrames = constraint.Sources.Select(s => s.SourceTransform).Append(constraint.transform).ToArray();
                foreach (var output in target.GetComponentsInChildren<Transform>(true))
                    proof.NumericalPolicy?.InheritNumericalConstraints(output, inputFrames);
                proof.NumericalPolicy?.InheritNumericalConstraints(constraint.transform, inputFrames);
                // Preserve slots, weights, normalization and their order.
                constraint.Sources = new VRCConstraintSourceKeyableList(sources);
                constraint.ApplyConfigurationChanges();
            }
            if (changes != 0) messages.Add("Rebased " + changes + " static constraint sources into component offsets; source weights and ordering retained.");
            return changes;
        }
    }
}
