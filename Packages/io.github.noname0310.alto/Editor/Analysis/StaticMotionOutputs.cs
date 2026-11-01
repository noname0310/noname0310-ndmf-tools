using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;

namespace Noname.AvatarTools.Analysis
{
    // The caller must prove that removing a child does not renormalize its siblings
    // or change state timing/default writes. This analysis proves only that the
    // entire child motion has no observable output or events and satisfies the
    // caller's clip proof (zero duration by default, or proven unobserved timing).
    internal sealed class StaticMotionOutputs
    {
        private readonly Func<VirtualClip, EditorCurveBinding, bool> observable;
        private readonly Func<VirtualClip, bool> safeClip;
        private readonly Dictionary<VirtualMotion, bool> cache = new();
        private readonly HashSet<VirtualMotion> visiting = new();

        internal StaticMotionOutputs(Func<VirtualClip, EditorCurveBinding, bool> observable,
            Func<VirtualClip, bool> safeClip = null)
        {
            this.observable = observable;
            this.safeClip = safeClip ?? MotionAnalysis.StaticClip;
        }

        internal bool IsUnobserved(VirtualMotion motion)
        {
            if (motion == null) return false;
            if (cache.TryGetValue(motion, out bool result)) return result;
            if (!visiting.Add(motion)) return false;
            result = motion switch
            {
                VirtualClip clip => safeClip(clip) &&
                    !clip.GetFloatCurveBindings().Concat(clip.GetObjectCurveBindings())
                        .Any(binding => observable == null || observable(clip, binding)),
                VirtualBlendTree tree => tree.Children.All(child => child.TimeScale == 1 &&
                    child.CycleOffset == 0 && !child.Mirror && IsUnobserved(child.Motion)),
                _ => false
            };
            visiting.Remove(motion);
            cache[motion] = result;
            return result;
        }
    }
}
