using System;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.AnimatorOptimization
{
    internal static class MissingAnimationBindings
    {
        // Only the final runtime hierarchy can prove absence. Preserve clip timing
        // even when its last real curve disappears; parameter curves have a separate
        // dataflow analysis and SDK proxy/humanoid clips remain untouched.
        internal static void Apply(VirtualAnimatorController[] controllers,
            Func<VirtualClip, EditorCurveBinding, bool> observable, AnimatorOptimizationReport report)
        {
            if (observable == null) return;
            foreach (var clip in controllers.SelectMany(c => c.AllReachableNodes()).OfType<VirtualClip>().Distinct())
            {
                var source = MotionAnalysis.Source(clip);
                if (clip.IsMarkerClip || clip.Legacy || clip.AdditiveReferencePoseClip != null ||
                    source == null || source.humanMotion || source.events.Length != 0) continue;
                bool Missing(EditorCurveBinding binding) => binding.type != typeof(Animator) &&
                    !(binding.path ?? "").StartsWith("__advanced_optimizer_duration_", StringComparison.Ordinal) &&
                    !observable(clip, binding);
                var floats = clip.GetFloatCurveBindings().Where(Missing).ToArray();
                var objects = clip.GetObjectCurveBindings().Where(Missing).ToArray();
                if (floats.Length + objects.Length == 0) continue;
                float end = MotionAnalysis.EndTime(clip);
                foreach (var binding in floats) clip.SetFloatCurve(binding, null);
                foreach (var binding in objects) clip.SetObjectCurve(binding, null);
                MotionAnalysis.PreserveDuration(clip, end);
                report.MissingBindings += floats.Length + objects.Length;
            }
        }
    }
}
