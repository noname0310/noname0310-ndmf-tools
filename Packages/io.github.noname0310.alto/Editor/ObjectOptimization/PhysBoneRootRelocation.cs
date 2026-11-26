using System.Linq;
using Noname.AvatarTools.Analysis;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.ObjectOptimization
{
    // A PhysBone chain can absorb a fixed origin-preserving similarity transform.
    // The SDK interpolates the simulation parent's position and rotation at low
    // frame rates. A translated parent does not commute with that interpolation:
    // matching only the world-space rest pose would change intermediate steps.
    internal static class PhysBoneRootRelocation
    {
        internal static bool CanRebase(Transform frame, Transform child, AvatarAnimationIndex animations,
            TransformMutationAnalysis mutations, bool allowSmallDifferences = true)
        {
            var offset = frame.localPosition;
            if (!allowSmallDifferences || !SpatialFrameProof.UniformPositiveScale(frame.localScale, allowSmallDifferences) ||
                offset.x != 0 || offset.y != 0 || offset.z != 0) return false;
            var writers = mutations.WritersOf(child).ToArray();
            if (writers.Length == 0 || writers.Any(w => w == null || w.GetType() != typeof(VRCPhysBone))) return false;
            foreach (var writer in writers)
            {
                var bone = (VRCPhysBone)writer;
                // Internal chain members retain their parent chain frame. Unknown
                // versions, moving roots and animated physics settings are kept.
                if (bone.GetRootTransform() != child || !PhysBoneHierarchy.FixedExclusions(bone, animations) ||
                    bone.version is not (VRCPhysBoneBase.Version.Version_1_0 or VRCPhysBoneBase.Version.Version_1_1) ||
                    // Polar limit evaluation can amplify changes in coordinate
                    // roundoff at degenerate limits. Retain its original frame.
                    bone.limitType is not (VRCPhysBoneBase.LimitType.None or VRCPhysBoneBase.LimitType.Angle or VRCPhysBoneBase.LimitType.Hinge) ||
                    animations.ForObject(bone).Any(u => !u.Complete || u.Binding.propertyName != "m_Enabled")) return false;
            }
            return true;
        }
    }
}
