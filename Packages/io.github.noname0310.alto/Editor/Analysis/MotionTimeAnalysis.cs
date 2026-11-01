using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;

namespace Noname.AvatarTools.Analysis
{
    // Duration can affect state transitions, synchronized layers, behaviours and
    // the sampling time of sibling motions. A constant pose alone is insufficient.
    internal sealed class MotionTimeAnalysis
    {
        private readonly HashSet<VirtualMotion> observed = new();
        private readonly Dictionary<VirtualMotion, bool> constant = new();
        private readonly HashSet<VirtualMotion> visiting = new();

        internal MotionTimeAnalysis(IEnumerable<VirtualAnimatorController> controllers)
        {
            foreach (var controller in controllers)
            {
                var layers = controller.Layers.ToArray();
                var synchronizedSources = new HashSet<int>(layers.Where(l => l.SyncedLayerIndex >= 0)
                    .Select(l => l.SyncedLayerIndex));
                foreach (var layer in layers)
                {
                    var nodes = layer.AllReachableNodes().ToArray();
                    bool timeObserved = layer.SyncedLayerIndex >= 0 || synchronizedSources.Contains(layer.VirtualLayerIndex) ||
                        nodes.OfType<VirtualStateMachine>().Any(m => m.Behaviours.Count != 0) ||
                        nodes.OfType<VirtualState>().Any(s => s.Behaviours.Count != 0) ||
                        nodes.OfType<VirtualStateTransition>().Any(t => t.ExitTime.HasValue ||
                            !MotionAnalysis.Finite(t.Duration) || t.Duration < 0 ||
                            !t.HasFixedDuration && t.Duration != 0);
                    foreach (var state in nodes.OfType<VirtualState>())
                        if (state.Motion != null && (timeObserved || !ConstantPose(state.Motion)))
                            observed.UnionWith(state.Motion.AllReachableNodes().OfType<VirtualMotion>());
                }
            }
        }

        internal bool CanChangeDuration(VirtualMotion motion)
            => motion != null && !observed.Contains(motion) && ConstantPose(motion);

        private bool ConstantPose(VirtualMotion motion)
        {
            if (motion == null) return false;
            if (constant.TryGetValue(motion, out bool result)) return result;
            if (!visiting.Add(motion)) return false;
            result = motion switch
            {
                VirtualClip clip => MotionAnalysis.ConstantPoseClip(clip),
                VirtualBlendTree tree => tree.Children.All(c => c.TimeScale == 1 && c.CycleOffset == 0 &&
                    !c.Mirror && ConstantPose(c.Motion)),
                _ => false
            };
            visiting.Remove(motion);
            constant[motion] = result;
            return result;
        }
    }
}
