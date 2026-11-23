using System;
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
        private readonly Transform avatar;
        private readonly ObjectOptimizationState objects;
        internal StaticTransformProof(GameObject avatar, AvatarAnimationIndex animations, ObjectOptimizationState objects,
            SerializedReferenceSnapshot references = null)
        {
            this.avatar = avatar.transform; this.objects = objects;
            Mutations = TransformMutationAnalysis.Create(avatar, animations, objects.AssumeFixedHumanoidBoneScale, references);
        }

        internal bool CanSubstitute(Transform target, OptimizationBuildScope scope) =>
            Mutations.Uncertain == null && target != null && target != avatar && target.parent != null &&
            target.IsChildOf(avatar) && scope != null && scope.Contains(target) && !(target is RectTransform) &&
            !Mutations.Mutable.Contains(target) && !objects.Protected(target) && Finite(Local(target));

        internal static Matrix4x4 Local(Transform target) => Matrix4x4.TRS(target.localPosition, target.localRotation, target.localScale);
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        internal static bool Finite(Quaternion value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        internal static bool Finite(Matrix4x4 value)
        {
            for (int i = 0; i < 16; i++) if (!Finite(value[i])) return false;
            return true;
        }

        // Reconstruction tolerance covers floating-point TRS roundoff, not a user
        // permission to discard shear or animated relative motion.
        internal static bool Near(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (!Finite(a[i]) || !Finite(b[i]) || Math.Abs(a[i] - b[i]) > 1e-6f * Math.Max(1, Math.Max(Math.Abs(a[i]), Math.Abs(b[i])))) return false;
            return true;
        }

        internal static bool TryCompose(Transform parentFrame, Transform child, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            var matrix = Local(parentFrame) * Local(child);
            position = matrix.GetColumn(3);
            rotation = parentFrame.localRotation * child.localRotation;
            var basis = Matrix4x4.Rotate(rotation).transpose * matrix;
            scale = new Vector3(basis.m00, basis.m11, basis.m22);
            return Near(matrix, Matrix4x4.TRS(position, rotation, scale));
        }
    }
}
