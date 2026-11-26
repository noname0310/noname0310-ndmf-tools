using System.Linq;
using System.Collections.Generic;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // A fixed local frame is a shared fact, not permission to delete its object.
    // Skinning, collision geometry and hierarchy each prove their own consumers.
    internal sealed class StaticTransformProof
    {
        internal readonly TransformMutationAnalysis Mutations;
        internal readonly OptimizationPolicy NumericalPolicy;
        private readonly Transform avatar;
        private readonly ObjectOptimizationState objects;
        private readonly AvatarAnimationIndex animations;
        private readonly SerializedReferenceSnapshot references;
        private HashSet<Transform> statefulFrames;
        internal StaticTransformProof(GameObject avatar, AvatarAnimationIndex animations, ObjectOptimizationState objects,
            SerializedReferenceSnapshot references = null, OptimizationPolicy numericalPolicy = null)
        {
            this.avatar = avatar.transform; this.objects = objects; NumericalPolicy = numericalPolicy;
            this.animations = animations; this.references = references;
            Mutations = TransformMutationAnalysis.Create(avatar, animations, objects.AssumeFixedHumanoidBoneScale, references);
        }

        internal bool CanSubstitute(Transform target, OptimizationBuildScope scope) =>
            Mutations.Uncertain == null && target != null && target != avatar && target.parent != null &&
            target.IsChildOf(avatar) && scope != null && scope.Contains(target) && !(target is RectTransform) &&
            !Mutations.Mutable.Contains(target) && !objects.Protected(target) && Finite(Local(target));

        internal static Matrix4x4 Local(Transform target) => Matrix4x4.TRS(target.localPosition, target.localRotation, target.localScale);
        internal static bool ExactChannels(Vector3 first, Vector3 second) =>
            NumericalEquivalence.Same(first.x, second.x, false) && NumericalEquivalence.Same(first.y, second.y, false) &&
            NumericalEquivalence.Same(first.z, second.z, false);
        internal static bool ExactChannels(Quaternion first, Quaternion second) =>
            NumericalEquivalence.Same(first.x, second.x, false) && NumericalEquivalence.Same(first.y, second.y, false) &&
            NumericalEquivalence.Same(first.z, second.z, false) && NumericalEquivalence.Same(first.w, second.w, false);

        // A matrix alone loses quaternion sign and signed-zero channel encodings.
        // Strict structural removal keeps those observable Transform properties.
        internal static bool ExactLocalIdentity(Transform target) => ExactChannels(target.localPosition, Vector3.zero) &&
            ExactChannels(target.localRotation, Quaternion.identity) && ExactChannels(target.localScale, Vector3.one);
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        internal static bool Finite(Quaternion value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        internal static bool Finite(Matrix4x4 value)
        {
            for (int i = 0; i < 16; i++) if (!Finite(value[i])) return false;
            return true;
        }

        internal bool AllowsRounding(NumericalOptimization feature, params Transform[] targets) => AllowsRounding(NumericalPolicy, feature, targets);

        // Near identity is not an identity for a Contact boundary, a simulation
        // rest frame, or another persistent reader. Reuse the full reader closure,
        // including indirect references and animated root selections.
        internal bool HasStatefulReaders(Transform frame) =>
            (statefulFrames ??= InitialPoseReaders.Analyze(avatar.gameObject, references, animations).Frames).Contains(frame);

        internal static bool AllowsRounding(OptimizationPolicy policy, NumericalOptimization feature, params Transform[] targets) =>
            policy == null || targets.All(t => t == null || policy.AllowsSmallNumericalDifferences(t, feature));

        // Moving a frame changes every descendant's output, including a nested
        // strict scope that is more restrictive than the frame's own policy.
        internal bool AllowsSubtreeRounding(Transform target, NumericalOptimization feature) => AllowsSubtreeRounding(NumericalPolicy, target, feature);
        internal static bool AllowsSubtreeRounding(OptimizationPolicy policy, Transform target, NumericalOptimization feature) =>
            policy == null || !policy.HasStrictNumericalConstraintsFor(feature) || target == null || target.GetComponentsInChildren<Transform>(true)
                .All(t => policy.AllowsSmallNumericalDifferences(t, feature));

        // Structural checks remain separate. This only selects the permitted
        // reconstruction error after the caller proves the frame is fixed.
        internal static bool Near(Matrix4x4 a, Matrix4x4 b, bool allowSmallDifferences = true) =>
            NumericalEquivalence.Same(a, b, allowSmallDifferences);

        internal static bool TryCompose(Transform parentFrame, Transform child, out Vector3 position, out Quaternion rotation, out Vector3 scale,
            bool allowSmallDifferences = true)
        {
            if (!allowSmallDifferences)
            {
                // Reassociating nonidentity TRS products has no exact native
                // arithmetic proof. An identity frame needs no recomputation.
                position = child.localPosition; rotation = child.localRotation; scale = child.localScale;
                return ExactLocalIdentity(parentFrame);
            }
            var matrix = Local(parentFrame) * Local(child);
            position = matrix.GetColumn(3);
            rotation = parentFrame.localRotation * child.localRotation;
            var basis = Matrix4x4.Rotate(rotation).transpose * matrix;
            scale = new Vector3(basis.m00, basis.m11, basis.m22);
            return Near(matrix, Matrix4x4.TRS(position, rotation, scale), allowSmallDifferences);
        }
    }
}
