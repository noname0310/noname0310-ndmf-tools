using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    internal static class MotionAnalysis
    {
        // NDMF does not expose clip event/humanoid metadata on VirtualClip. An unavailable
        // source disables structural rewrites instead of assuming that a clip is generic.
        private static readonly PropertyInfo Original = typeof(VirtualNode).GetProperty("OriginalObject",
            BindingFlags.Instance | BindingFlags.NonPublic);
        internal static AnimationClip Source(VirtualClip clip) => Original?.GetValue(clip) as AnimationClip;

        internal static IEnumerable<VirtualClip> Clips(VirtualMotion motion)
            => motion == null ? Enumerable.Empty<VirtualClip>() : motion.AllReachableNodes().OfType<VirtualClip>().Distinct();

        internal static HashSet<EditorCurveBinding> Bindings(VirtualMotion motion)
            => new(Clips(motion).SelectMany(c => c.GetFloatCurveBindings().Concat(c.GetObjectCurveBindings())));

        internal static bool Constant(AnimationCurve curve, out float value, bool preserveZeroSign = true)
        {
            value = 0;
            if (curve == null || curve.length == 0) return false;
            var keys = curve.keys;
            value = keys[0].value;
            if (!Finite(value)) return false;
            for (int i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                if (!Finite(key.time) || (preserveZeroSign ? !Same(key.value, value) : key.value != value) ||
                    float.IsNaN(key.inTangent) || float.IsNaN(key.outTangent) ||
                    !Finite(key.inWeight) || !Finite(key.outWeight) || key.inWeight < 0 || key.inWeight > 1 ||
                    key.outWeight < 0 || key.outWeight > 1) return false;
                if (i == 0) continue;
                var previous = keys[i - 1];
                if (previous.time >= key.time) return false;
                if (float.IsInfinity(previous.outTangent) || float.IsInfinity(key.inTangent)) continue;
                if (previous.outTangent != 0 || key.inTangent != 0) return false;
            }
            return true;
        }

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // Material uniforms can observe the sign of zero. A constant-output
        // proof preserves the stored value, not only numeric comparison results.
        internal static bool Same(float left, float right) =>
            BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);
        internal static bool NegativeZero(float value) => BitConverter.SingleToInt32Bits(value) == int.MinValue;

        // A fixed 1D input selects an endpoint, an exact threshold, or its two
        // adjacent motions. This proves zero influence without evaluating a
        // weighted pose or changing clip duration and binding coverage.
        // https://docs.unity3d.com/2022.3/Documentation/Manual/BlendTree-1DBlending.html
        internal static VirtualBlendTree.VirtualChildMotion[] Constant1DChildren(VirtualBlendTree tree,
            AnimatorReachability analysis)
        {
            if (tree.BlendType != BlendTreeType.Simple1D || tree.Children.Count == 0 ||
                !analysis.Values(tree.BlendParameter).TryGetConstant(out float position) || !Finite(position)) return null;
            var children = tree.Children.OrderBy(c => c.Threshold).ToArray();
            if (children.Any(c => c.Motion == null || c.Mirror || !Finite(c.Threshold) ||
                !Finite(c.TimeScale) || !Finite(c.CycleOffset)) ||
                children.Select(c => c.Threshold).Distinct().Count() != children.Length) return null;
            if (position <= children[0].Threshold) return new[] { children[0] };
            if (position >= children[children.Length - 1].Threshold) return new[] { children[children.Length - 1] };
            int upper = Array.FindIndex(children, c => c.Threshold > position);
            return children[upper - 1].Threshold == position
                ? new[] { children[upper - 1] }
                : new[] { children[upper - 1], children[upper] };
        }

        internal static float EndTime(VirtualClip clip)
            => clip.GetFloatCurveBindings().SelectMany(b => clip.GetFloatCurve(b)?.keys ?? Array.Empty<Keyframe>()).Select(k => k.time)
                // Object-reference keys occupy one complete frame, unlike scalar keys.
                // Retain that final frame when the last PPtr track is removed.
                .Concat(clip.GetObjectCurveBindings().SelectMany(b => clip.GetObjectCurve(b) ?? Array.Empty<ObjectReferenceKeyframe>())
                    .Select(k => k.time + 1f / clip.FrameRate))
                .DefaultIfEmpty(0).Max();

        // Restrict structural rewrites to zero-duration clips. Changing a tree's duration changes
        // normalized state time, synchronized children and exit-time transitions even for constant poses.
        internal static bool StaticClip(VirtualClip clip)
            => ConstantPoseClip(clip) && clip.Settings.stopTime == 0 && EndTime(clip) == 0;

        // Constancy of the pose is distinct from constancy of the motion's duration.
        // A caller must separately prove that changing duration cannot be observed.
        internal static bool ConstantPoseClip(VirtualClip clip)
            => ConstantPoseClip(clip, true);

        // Ignoring zero sign is a consumer-owned numerical contract. The ordinary
        // motion/parameter queries continue to use the exact overload above.
        internal static bool ConstantPoseClip(VirtualClip clip, bool preserveZeroSign)
            => !clip.IsMarkerClip && !clip.Legacy && clip.AdditiveReferencePoseClip == null &&
               Source(clip) is AnimationClip source && !source.humanMotion && source.events.Length == 0 &&
               !clip.GetObjectCurveBindings().Any() && clip.GetFloatCurveBindings().All(b =>
                   !b.isDiscreteCurve && Constant(clip.GetFloatCurve(b), out _, preserveZeroSign));

        internal static void RemoveCurves(VirtualClip clip, IEnumerable<EditorCurveBinding> bindings)
        {
            float end = EndTime(clip);
            foreach (var binding in bindings.ToArray())
                if (binding.isPPtrCurve) clip.SetObjectCurve(binding, null);
                else clip.SetFloatCurve(binding, null);
            PreserveDuration(clip, end);
        }

        internal static void PreserveDuration(VirtualClip clip, float end)
        {
            if (EndTime(clip) < end || !clip.GetFloatCurveBindings().Any() && !clip.GetObjectCurveBindings().Any())
                clip.SetFloatCurve(EditorCurveBinding.FloatCurve("__advanced_optimizer_duration_" + Guid.NewGuid().ToString("N"),
                    typeof(GameObject), "m_IsActive"), end == 0 ? new AnimationCurve(new Keyframe(0, 0)) : AnimationCurve.Constant(0, end, 0));
        }
    }
}
