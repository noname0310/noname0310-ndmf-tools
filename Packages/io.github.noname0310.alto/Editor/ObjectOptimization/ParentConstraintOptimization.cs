using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.ObjectOptimization
{
    // A parent constraint copies position/rotation, not scale. Parenting is equivalent
    // only when the intervening frames have fixed, positive uniform scales. A common
    // ancestor may still move, rotate and scale. Nonzero positional offsets are kept:
    // the SDK offset does not scale with the avatar as a Transform child offset does.
    internal static class ParentConstraintOptimization
    {
        internal static int Apply(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths, ObjectOptimizationState settings,
            IEnumerable<string> preservedParameters, ICollection<string> messages, ShaderPropertyUsage shaders = null,
            ObjectAnalysisSnapshot snapshot = null, bool commitNativePaths = false, OptimizationPolicy numericPolicy = null)
        {
            snapshot ??= new ObjectAnalysisSnapshot(avatar, controllers, paths, settings, preservedParameters, shaders);
            return snapshot.Rewrite(() => ApplyCore(avatar, controllers, paths, settings, preservedParameters, messages, shaders, snapshot, commitNativePaths, numericPolicy));
        }

        private static int ApplyCore(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths, ObjectOptimizationState settings,
            IEnumerable<string> preservedParameters, ICollection<string> messages, ShaderPropertyUsage shaders,
            ObjectAnalysisSnapshot snapshot, bool commitNativePaths, OptimizationPolicy numericPolicy)
        {
            using var cost = AnalysisCost.Measure("Constant output consumer/parent constraints");
            var index = snapshot.Index;
            var mutation = snapshot.Mutations;
            var analysis = index.Reachability;
            string reason = ObjectOptimizationSafety.UnsupportedRuntime(avatar) ?? index.UnsafeReason ??
                mutation.Uncertain ?? analysis.FallbackReason;
            if (reason != null) { messages.Add("Parent constraint simplification skipped: " + reason); return 0; }
            var graph = snapshot.Dependencies(false);
            var nativeVisibility = VisibilityMutationAnalysis.NativeWriters(avatar);
            var objectKeys = new HashSet<UnityEngine.Object>(index.Bindings.SelectMany(u =>
                u.ObjectKeys ?? Array.Empty<ObjectReferenceKeyframe>()).Select(k => k.value));
            InitialPoseReaders.Inputs roundingReaders = null;
            var roundingOutputs = new Dictionary<VRCParentConstraint, bool>();
            var proof = new InvariantComponentSettings(index, analysis, controllers, numericPolicy,
                NumericalOptimization.Constraints, (component, property) =>
                {
                    if (component is not VRCParentConstraint parent ||
                        !ConstantConstraintSettings.ContinuousConfigurationProperty(parent, property)) return false;
                    if (!roundingOutputs.TryGetValue(parent, out bool allowed))
                    {
                        roundingReaders ??= InitialPoseReaders.Analyze(avatar, animations: index);
                        roundingOutputs.Add(parent, allowed = CanRoundConfiguration(parent, avatar, settings,
                            graph, mutation, roundingReaders, numericPolicy));
                    }
                    return allowed;
                });
            int removed = 0;
            var appliedProofs = new AppliedProofReport();
            foreach (var constraint in avatar.GetComponentsInChildren<VRCParentConstraint>(true))
            {
                if (constraint.GetType() != typeof(VRCParentConstraint) || !settings.Scope.Contains(constraint.transform) ||
                    settings.Protected(constraint) || graph.ReferenceOwners.ContainsKey(constraint) || objectKeys.Contains(constraint) ||
                    constraint.GetComponents<Component>().Any(c => c != null && c != constraint && BuildFilterPass.Requires(c, constraint))) continue;
                var target = constraint.TargetTransform != null ? constraint.TargetTransform : constraint.transform;
                if (target == avatar.transform || !settings.Scope.Contains(target) || settings.Protected(target) ||
                    !constraint.Locked || constraint.FreezeToWorld || constraint.SmoothingFactor != 0 ||
                    mutation.WrittenByOther(target, constraint) ||
                    !proof.IsConstant(constraint, out var consumed)) continue;
                string path = AnimationUtility.CalculateTransformPath(constraint.transform, avatar.transform);
                var position = target.localPosition; var rotation = target.localRotation; var scale = target.localScale;
                bool allowRounding = StaticTransformProof.AllowsRounding(numericPolicy, NumericalOptimization.Constraints, constraint.transform, target.parent) &&
                    StaticTransformProof.AllowsSubtreeRounding(numericPolicy, target, NumericalOptimization.Constraints);
                if (constraint.GlobalWeight == 0 && RestMatches(constraint, target, allowRounding))
                {
                    bool roundedPose = allowRounding && !RestMatches(constraint, target, false);
                    var removeCurves = proof.PrepareConstantCurveRemoval(constraint);
                    InheritPolicy(numericPolicy, target, constraint.transform);
                    if (!settings.TryDestroy(constraint)) continue;
                    removeCurves();
                    target.localPosition = position; target.localRotation = rotation; target.localScale = scale;
                    messages.Add("Redundant parent constraint at rest: " + path + "; existing local pose preserved.");
                    appliedProofs.Record(consumed | (roundedPose ? NumericalOptimization.Constraints : NumericalOptimization.None));
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
                allowRounding &= StaticTransformProof.AllowsRounding(numericPolicy, NumericalOptimization.Constraints, parent);
                var common = CommonAncestor(parent, target.parent);
                if (common == null || !UniformScaleTo(parent, common, mutation, out float parentScale) ||
                    !UniformScaleTo(target.parent, common, mutation, out float previousScale) ||
                    !AlwaysActive(parent, common, index, nativeVisibility)) continue;
                var localRotation = Quaternion.Euler(source.ParentRotationOffset);
                var localScale = scale * (previousScale / parentScale);
                if (!allowRounding && (!StaticTransformProof.ExactChannels(source.ParentRotationOffset, Vector3.zero) ||
                    !NumericalEquivalence.Same(parentScale, 1, false) ||
                    !NumericalEquivalence.Same(previousScale, 1, false))) continue;
                var expected = RelativeMatrix(parent, common) * Matrix4x4.TRS(Vector3.zero, localRotation, localScale);
                if (!StaticTransformProof.Near(expected, RelativeMatrix(target, common), allowRounding)) continue;
                bool roundedParenting = allowRounding &&
                    (!StaticTransformProof.ExactChannels(source.ParentRotationOffset, Vector3.zero) ||
                     !NumericalEquivalence.Same(parentScale, 1, false) || !NumericalEquivalence.Same(previousScale, 1, false) ||
                     !StaticTransformProof.Near(expected, RelativeMatrix(target, common), false));
                Dictionary<string, string> relocatedPaths = null;
                if (commitNativePaths && !new HierarchyRelocation(index).CanReparent(target, parent, out relocatedPaths)) continue;
                // The animation service owns path remapping for the moved subtree.
                // At the SDK boundary the checked transaction above owns it instead.
                // Existing animation of the target itself was rejected above; child
                // animations and skinning references retain their local coordinates.
                var removeFollowingCurves = proof.PrepareConstantCurveRemoval(constraint);
                InheritPolicy(numericPolicy, target, constraint.transform, parent, target.parent);
                if (!settings.TryDestroy(constraint)) continue;
                removeFollowingCurves();
                target.SetParent(parent, false);
                target.localPosition = Vector3.zero; target.localRotation = localRotation; target.localScale = localScale;
                messages.Add("Parent constraint replaced with hierarchy parenting: " + path + " -> " +
                    AnimationUtility.CalculateTransformPath(parent, avatar.transform) + ".");
                appliedProofs.Record(consumed | (roundedParenting ? NumericalOptimization.Constraints : NumericalOptimization.None));
                removed++;
                if (commitNativePaths)
                {
                    HierarchyRelocation.CommitNativePaths(index, relocatedPaths);
                    // A second move must not reuse pre-move paths, writer roots or
                    // dependency facts. The outer worklist rebuilds the snapshot.
                    appliedProofs.AppendTo(messages, "parent constraints");
                    return removed;
                }
            }
            appliedProofs.AppendTo(messages, "parent constraints");
            return removed;
        }

        private static bool AllAxes(VRCParentConstraint c) => c.AffectsPositionX && c.AffectsPositionY && c.AffectsPositionZ &&
            c.AffectsRotationX && c.AffectsRotationY && c.AffectsRotationZ;

        private static bool CanRoundConfiguration(VRCParentConstraint constraint, GameObject avatar,
            ObjectOptimizationState settings, AvatarDependencyGraph graph, TransformMutationAnalysis mutations,
            InitialPoseReaders.Inputs readers, OptimizationPolicy policy)
        {
            var target = constraint.TargetTransform != null ? constraint.TargetTransform : constraint.transform;
            if (!constraint.enabled || !constraint.IsActive || !constraint.Locked || constraint.FreezeToWorld ||
                constraint.SmoothingFactor != 0 || constraint.SolveInLocalSpace || !AllAxes(constraint) ||
                graph.UnsafeReason != null || mutations.Uncertain != null || mutations.WrittenByOther(target, constraint) ||
                graph.ReferenceOwners.ContainsKey(constraint) || readers.CullingFrames.Contains(target) ||
                !StaticTransformProof.AllowsRounding(policy, NumericalOptimization.Constraints, constraint.transform, target.parent) ||
                !StaticTransformProof.AllowsSubtreeRounding(policy, target, NumericalOptimization.Constraints)) return false;
            foreach (var source in constraint.Sources)
                if (source.SourceTransform == null || !source.SourceTransform.IsChildOf(avatar.transform) ||
                    graph.DependsOn(source.SourceTransform, target) ||
                    !StaticTransformProof.AllowsRounding(policy, NumericalOptimization.Constraints, source.SourceTransform)) return false;
            // A numerical configuration proof is appropriate for a stateless visual
            // solve. The solver's own reference is expected; other solvers, sensors,
            // simulations and unknown native readers retain the Animator writes.
            // DependsOn also includes future animated object-reference targets.
            foreach (var component in graph.Components)
            {
                if (component == constraint || component is Transform || component is INDMFEditorOnly || component is IEditorOnly ||
                    ObjectOptimizationSafety.IsParentChangeDetector(component)) continue;
                bool preserved = settings.Protected(component) || !settings.Scope.Contains(component.transform);
                if (!preserved && (component is Renderer || component is MeshFilter || component is Light)) continue;
                if (graph.DependsOn(component, target)) return false;
            }
            return true;
        }

        private static void InheritPolicy(OptimizationPolicy policy, Transform target, params Transform[] sources)
        {
            if (policy == null) return;
            foreach (var output in target.GetComponentsInChildren<Transform>(true))
                policy.InheritNumericalConstraints(output, sources);
        }

        private static bool RestMatches(VRCParentConstraint c, Transform target, bool allowRounding)
        {
            // Weight zero selects At Rest; it is not equivalent to disabling the component.
            if (!AllAxes(c)) return false;
            if (!allowRounding) return StaticTransformProof.ExactChannels(c.PositionAtRest, target.localPosition) &&
                StaticTransformProof.ExactChannels(Quaternion.Euler(c.RotationAtRest), target.localRotation);
            return StaticTransformProof.Near(Matrix4x4.TRS(c.PositionAtRest, Quaternion.Euler(c.RotationAtRest), target.localScale),
                Matrix4x4.TRS(target.localPosition, target.localRotation, target.localScale), allowRounding);
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

    }
}
