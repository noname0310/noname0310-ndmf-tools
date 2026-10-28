using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor.Animations;

namespace Noname.AvatarTools.Analysis
{
    // A state can change another layer through Write Defaults even without an observable curve.
    // Implicit defaults are supplied at the state boundary, not multiplied by the
    // weights of ordinary generic motions. Explicit curves still need their own
    // output/dependency analysis; this is not permission to merge weighted poses.
    internal sealed class DefaultWriteAnalysis
    {
        private readonly Dictionary<VirtualMotion, bool> cache = new();
        private readonly HashSet<VirtualMotion> visiting = new();

        internal bool Uniform(IEnumerable<VirtualState> states) =>
            states.All(s => s.WriteDefaultValues && Ordinary(s.Motion));

        private bool Ordinary(VirtualMotion motion)
        {
            if (motion == null) return false;
            if (cache.TryGetValue(motion, out bool result)) return result;
            if (!visiting.Add(motion)) return false;
            if (motion is VirtualClip clip)
            {
                var source = MotionAnalysis.Source(clip);
                result = !clip.IsMarkerClip && !clip.Legacy && clip.AdditiveReferencePoseClip == null &&
                    source != null && !source.humanMotion && source.events.Length == 0;
            }
            else if (motion is VirtualBlendTree tree && tree.Children.Count > 0 &&
                tree.Children.All(c => c.Motion != null && !c.Mirror &&
                    MotionAnalysis.Finite(c.TimeScale) && MotionAnalysis.Finite(c.CycleOffset)))
            {
                bool supported = tree.BlendType == BlendTreeType.Direct ||
                    tree.BlendType == BlendTreeType.Simple1D &&
                    tree.Children.All(c => MotionAnalysis.Finite(c.Threshold)) &&
                    tree.Children.Select(c => c.Threshold).Distinct().Count() == tree.Children.Count;
                result = supported && tree.Children.All(c => Ordinary(c.Motion));
            }
            visiting.Remove(motion);
            cache[motion] = result;
            return result;
        }
    }
}
