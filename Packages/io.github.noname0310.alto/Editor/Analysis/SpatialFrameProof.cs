using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // Shared geometry proof for SDK shapes. Their radius uses the largest absolute
    // scale component in the rotation-aligned world matrix, not a column length.
    internal static class SpatialFrameProof
    {
        internal static bool UniformShapeScale(Transform frame, Transform avatar,
            TransformMutationAnalysis mutations, out float scale)
        {
            var s = frame.localScale; scale = s.x;
            if (!UniformPositiveScale(s)) return false;
            if (StaticTransformProof.Near(Matrix4x4.Rotate(frame.localRotation), Matrix4x4.identity)) return true;
            for (var parent = frame.parent; parent != null; parent = parent.parent)
            {
                var p = parent.localScale;
                if (mutations.MayChangeScale(parent) || !UniformPositiveScale(p)) return false;
                if (parent == avatar) break;
            }
            return true;
        }

        internal static bool UniformPositiveScale(Vector3 scale) =>
            StaticTransformProof.Finite(scale) && scale.x > 0 && scale.y > 0 && scale.z > 0 &&
            // Normalize before comparing: an absolute matrix tolerance would
            // accept arbitrarily large anisotropy at small coordinate scales.
            StaticTransformProof.Near(Matrix4x4.Scale(scale / scale.x), Matrix4x4.identity);

        internal static bool TranslationOnly(Transform frame) => StaticTransformProof.Near(
            StaticTransformProof.Local(frame), Matrix4x4.Translate(frame.localPosition));
    }
}
