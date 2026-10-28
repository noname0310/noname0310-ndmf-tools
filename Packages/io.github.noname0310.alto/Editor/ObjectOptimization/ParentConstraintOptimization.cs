using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Noname.AvatarTools.ObjectOptimization
{
    // A parent constraint copies position/rotation, not scale. Parenting is equivalent
    // only when the intervening frames have fixed, positive uniform scales. A common
    // ancestor may still move, rotate and scale. Nonzero positional offsets are kept:
    // the SDK offset does not scale with the avatar as a Transform child offset does.
    internal static class ParentConstraintOptimization
    {
        private const float ReconstructionTolerance = 0.000001f;

        internal static int Apply(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths, ObjectOptimizationState settings,
            IEnumerable<string> preservedParameters, ICollection<string> messages)
        {
            var index = AvatarAnimationIndex.Create(avatar, controllers, paths, preservedParameters);
            var mutation = TransformMutationAnalysis.Create(avatar, index);
            var analysis = AnimatorReachability.Analyze(avatar, index.Controllers, preservedParameters);
            string reason = ObjectOptimizationSafety.UnsupportedRuntime(avatar) ?? index.UnsafeReason ??
                mutation.Uncertain ?? analysis.FallbackReason;
            if (reason != null) { messages.Add("Parent constraint simplification skipped: " + reason); return 0; }
            var graph = AvatarDependencyGraph.Create(avatar, index, settings, collectObjects: false);
            var nativeVisibility = VisibilityMutationAnalysis.NativeWriters(avatar);
            var objectKeys = new HashSet<UnityEngine.Object>(index.Bindings.SelectMany(u =>
                u.ObjectKeys ?? Array.Empty<ObjectReferenceKeyframe>()).Select(k => k.value));
            var proof = new InvariantComponentSettings(index, analysis, controllers);
            int removed = 0;
            foreach (var constraint in avatar.GetComponentsInChildren<VRCParentConstraint>(true))
            {
                if (constraint.GetType() != typeof(VRCParentConstraint) || !settings.Scope.Contains(constraint.transform) ||
                    settings.Protected(constraint) || graph.ReferenceOwners.ContainsKey(constraint) || objectKeys.Contains(constraint) ||
                    constraint.GetComponents<Component>().Any(c => c != null && c != constraint && BuildFilterPass.Requires(c, constraint))) continue;
                var target = constraint.TargetTransform != null ? constraint.TargetTransform : constraint.transform;
                if (target == avatar.transform || !settings.Scope.Contains(target) || settings.Protected(target) ||
                    !constraint.Locked || constraint.FreezeToWorld ||
                    mutation.WrittenByOther(target, constraint) ||
                    !proof.IsConstant(constraint)) continue;
                string path = AnimationUtility.CalculateTransformPath(constraint.transform, avatar.transform);
                var position = target.localPosition; var rotation = target.localRotation; var scale = target.localScale;
                if (constraint.GlobalWeight == 0 && RestMatches(constraint, target))
                {
                    var removeCurves = proof.PrepareConstantCurveRemoval(constraint);
                    if (!settings.TryDestroy(constraint)) continue;
                    removeCurves();
                    target.localPosition = position; target.localRotation = rotation; target.localScale = scale;
                    messages.Add("Redundant parent constraint at rest: " + path + "; existing local pose preserved.");
                    removed++; continue;
                }
                if (!constraint.enabled || !constraint.IsActive || constraint.GlobalWeight != 1 || constraint.SolveInLocalSpace ||
                    constraint.Sources.Count != 1 || !AllAxes(constraint) ||
                    !AlwaysActive(constraint.transform, avatar.transform, index, nativeVisibility) ||
                    !AlwaysActive(target, avatar.transform, index, nativeVisibility)) continue;
                var source = constraint.Sources[0]; var parent = source.SourceTransform;
                if (parent == null || parent == target || parent.IsChildOf(target) || !parent.IsChildOf(avatar.transform) ||
                    graph.DependsOn(parent, target) ||
                    !settings.Scope.Contains(parent) || source.Weight != 1 || !source.ParentPositionOffset.Equals(Vector3.zero) ||
                    parent.Cast<Transform>().Any(t => t != target && t.name == target.name)) continue;
                var common = CommonAncestor(parent, target.parent);
                if (common == null || !UniformScaleTo(parent, common, mutation, out float parentScale) ||
                    !UniformScaleTo(target.parent, common, mutation, out float previousScale) ||
                    !AlwaysActive(parent, common, index, nativeVisibility)) continue;
                var localRotation = Quaternion.Euler(source.ParentRotationOffset);
                var localScale = scale * (previousScale / parentScale);
                var expected = RelativeMatrix(parent, common) * Matrix4x4.TRS(Vector3.zero, localRotation, localScale);
                if (!Near(expected, RelativeMatrix(target, common))) continue;
                // The animation service owns path remapping for the moved subtree.
                // Existing animation of the target itself was rejected above; child
                // animations and skinning references retain their local coordinates.
                var removeFollowingCurves = proof.PrepareConstantCurveRemoval(constraint);
                if (!settings.TryDestroy(constraint)) continue;
                removeFollowingCurves();
                target.SetParent(parent, false);
                target.localPosition = Vector3.zero; target.localRotation = localRotation; target.localScale = localScale;
                messages.Add("Parent constraint replaced with hierarchy parenting: " + path + " -> " +
                    AnimationUtility.CalculateTransformPath(parent, avatar.transform) + ".");
                removed++;
            }
            return removed;
        }

        private static bool AllAxes(VRCParentConstraint c) => c.AffectsPositionX && c.AffectsPositionY && c.AffectsPositionZ &&
            c.AffectsRotationX && c.AffectsRotationY && c.AffectsRotationZ;

        private static bool RestMatches(VRCParentConstraint c, Transform target)
        {
            // Weight zero selects At Rest; it is not equivalent to disabling the component.
            if (!AllAxes(c)) return false;
            return Near(Matrix4x4.TRS(c.PositionAtRest, Quaternion.Euler(c.RotationAtRest), target.localScale),
                Matrix4x4.TRS(target.localPosition, target.localRotation, target.localScale));
        }

        private static bool AlwaysActive(Transform target, Transform stop, AvatarAnimationIndex index,
            ISet<UnityEngine.Object> nativeVisibility)
        {
            for (var current = target; current != null && current != stop; current = current.parent)
                if (!current.gameObject.activeSelf || index.Writes(current.gameObject, "m_IsActive") ||
                    nativeVisibility.Contains(current.gameObject)) return false;
            return true;
        }

        private static Transform CommonAncestor(Transform a, Transform b)
        {
            var ancestors = new HashSet<Transform>();
            for (; a != null; a = a.parent) ancestors.Add(a);
            for (; b != null; b = b.parent) if (ancestors.Contains(b)) return b;
            return null;
        }

        private static Matrix4x4 RelativeMatrix(Transform node, Transform ancestor)
        {
            var matrix = Matrix4x4.identity;
            for (; node != ancestor; node = node.parent)
                matrix = Matrix4x4.TRS(node.localPosition, node.localRotation, node.localScale) * matrix;
            return matrix;
        }

        private static bool UniformScaleTo(Transform node, Transform ancestor, TransformMutationAnalysis mutation, out float product)
        {
            product = 1;
            for (; node != null && node != ancestor; node = node.parent)
            {
                var scale = node.localScale;
                if (mutation.MayChangeScale(node) || !MotionAnalysis.Finite(scale.x) || scale.x <= 0 ||
                    scale.x != scale.y || scale.y != scale.z) return false;
                product *= scale.x;
                if (!MotionAnalysis.Finite(product) || product <= 0) return false;
            }
            return node == ancestor;
        }

        private static bool Near(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (!MotionAnalysis.Finite(a[i]) || !MotionAnalysis.Finite(b[i]) ||
                    Math.Abs(a[i] - b[i]) > ReconstructionTolerance * Math.Max(1, Math.Max(Math.Abs(a[i]), Math.Abs(b[i])))) return false;
            return true;
        }

    }
}
