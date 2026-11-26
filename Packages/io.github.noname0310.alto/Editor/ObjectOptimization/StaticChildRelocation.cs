using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Noname.AvatarTools.ObjectOptimization
{
    internal static class StaticChildRelocation
    {
        internal static bool TryPlan(Transform frame, Transform child, AvatarAnimationIndex animations,
            TransformMutationAnalysis mutations, out Action apply, bool allowSmallDifferences = true)
        {
            apply = null;
            // Translation preserves the coordinate basis for arbitrary animated
            // rotations/scales. Position curves need only a constant value offset;
            // tangents, weights, timing and wrap modes remain exactly the same.
            // Translating keys moves addition across interpolation/blending.
            // A strict native arithmetic certificate is not available for it.
            if (!allowSmallDifferences || !SpatialFrameProof.TranslationOnly(frame, allowSmallDifferences)) return false;
            var uses = animations.ForObject(child).Where(u => u.Binding.type != null && typeof(Transform).IsAssignableFrom(u.Binding.type)).ToArray();
            if (uses.Any(u => !u.Complete || u.Immutable || u.Binding.isPPtrCurve || u.Targets.Length != 1 ||
                u.Objects().Count() != 1 || !IsLocalTrs(u.Binding.propertyName))) return false;
            var positionClips = uses.Where(u => IsPosition(u.Binding.propertyName)).Select(u => u.Clip).ToHashSet();
            if (positionClips.Any(c => c.AdditiveReferencePoseClip != null || c.Legacy || MotionAnalysis.Source(c)?.humanMotion == true)) return false;
            // Do not introduce bindings for implicit compound channels: that could
            // change Write Defaults' property coverage. Every translated axis must
            // already be explicit in each clip that writes the position.
            foreach (var clip in positionClips)
                for (int axis = 0; axis < 3; axis++)
                    if (frame.localPosition[axis] != 0 && !uses.Any(u => u.Clip == clip && IsPosition(u.Binding.propertyName) &&
                        u.Binding.propertyName.EndsWith("." + "xyz"[axis], StringComparison.Ordinal))) return false;
            foreach (var layer in positionClips.Count == 0 ? Enumerable.Empty<VirtualLayer>() : animations.Controllers.SelectMany(c => c.Layers))
            {
                var nodes = layer.AllReachableNodes().ToArray();
                if (!nodes.OfType<VirtualClip>().Any(positionClips.Contains)) continue;
                // A coordinate translation is affine, not linear. Adding an offset
                // to every input is invalid for additive layers or unnormalized
                // weighted sums. A normalized Direct tree can also have zero total
                // weight; keep it until its fallback is proven as an affine blend.
                if (layer.BlendingMode != AnimatorLayerBlendingMode.Override || layer.SyncedLayerIndex != -1 ||
                    nodes.OfType<VirtualBlendTree>().Any(t => t.BlendType == BlendTreeType.Direct)) return false;
            }
            var clips = uses.Select(u => MotionAnalysis.Source(u.Clip)).ToHashSet();
            // Rotation/scale constraints do not observe the changed local position.
            // PhysBones, humanoid writers and positional constraints keep their
            // original simulation/rest frames until a separate proof exists.
            if (mutations.WritersOf(child).Any(w => !clips.Contains(w) &&
                (w == null || w.GetType() != typeof(VRCRotationConstraint) && w.GetType() != typeof(VRCScaleConstraint)))) return false;
            var edits = new List<Action>();
            foreach (var use in uses)
            {
                string property = use.Binding.propertyName;
                if (!IsPosition(property)) continue;
                int axis = "xyz".IndexOf(property[property.Length - 1]);
                if (axis < 0 || use.Curve == null) return false;
                float offset = frame.localPosition[axis];
                var keys = use.Curve.keys;
                for (int i = 0; i < keys.Length; i++) { keys[i].value += offset; if (!StaticTransformProof.Finite(keys[i].value)) return false; }
                var curve = new AnimationCurve(keys) { preWrapMode = use.Curve.preWrapMode, postWrapMode = use.Curve.postWrapMode };
                edits.Add(() => use.Clip.SetFloatCurve(use.Binding, curve));
            }
            apply = () => { foreach (var edit in edits) edit(); };
            return true;
        }

        private static bool IsPosition(string property) => property.StartsWith("m_LocalPosition.", StringComparison.Ordinal) ||
            property.StartsWith("localPosition.", StringComparison.Ordinal);

        private static bool IsLocalTrs(string property) =>
            new[] { "m_LocalPosition.", "localPosition.", "m_LocalRotation.", "localRotation.", "localEulerAnglesRaw.",
                "localEulerAnglesBaked.", "localEulerAngles.", "m_LocalScale.", "localScale." }
            .Any(p => property.StartsWith(p, StringComparison.Ordinal) && property.Length == p.Length + 1 && "xyzw".Contains(property[property.Length - 1]));
    }
}
