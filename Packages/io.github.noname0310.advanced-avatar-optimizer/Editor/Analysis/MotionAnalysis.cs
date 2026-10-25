using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using nadena.dev.ndmf.animator;
using UnityEditor;
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

        internal static bool Constant(AnimationCurve curve, out float value)
        {
            value = 0;
            if (curve == null || curve.length == 0) return false;
            var keys = curve.keys;
            value = keys[0].value;
            if (!Finite(value)) return false;
            for (int i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                if (!Finite(key.time) || key.value != value || float.IsNaN(key.inTangent) || float.IsNaN(key.outTangent) ||
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

        internal static float EndTime(VirtualClip clip)
            => clip.GetFloatCurveBindings().SelectMany(b => clip.GetFloatCurve(b)?.keys ?? Array.Empty<Keyframe>()).Select(k => k.time)
                .Concat(clip.GetObjectCurveBindings().SelectMany(b => clip.GetObjectCurve(b) ?? Array.Empty<ObjectReferenceKeyframe>()).Select(k => k.time))
                .DefaultIfEmpty(0).Max();

        // Restrict structural rewrites to zero-duration clips. Changing a tree's duration changes
        // normalized state time, synchronized children and exit-time transitions even for constant poses.
        internal static bool StaticClip(VirtualClip clip)
            => !clip.IsMarkerClip && !clip.Legacy && clip.AdditiveReferencePoseClip == null &&
               Source(clip) is AnimationClip source && !source.humanMotion && source.events.Length == 0 &&
               clip.Settings.stopTime == 0 && EndTime(clip) == 0 &&
               !clip.GetObjectCurveBindings().Any() && clip.GetFloatCurveBindings().All(b =>
                   !b.isDiscreteCurve && Constant(clip.GetFloatCurve(b), out _));

        internal static void RemoveCurves(VirtualClip clip, IEnumerable<EditorCurveBinding> bindings)
        {
            float end = EndTime(clip);
            foreach (var binding in bindings.ToArray()) clip.SetFloatCurve(binding, null);
            if (EndTime(clip) < end)
                clip.SetFloatCurve(EditorCurveBinding.FloatCurve("__advanced_optimizer_duration_" + Guid.NewGuid().ToString("N"),
                    typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, end, 0));
        }
    }
}
