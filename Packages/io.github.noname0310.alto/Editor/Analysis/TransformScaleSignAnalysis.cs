using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.Analysis
{
    internal static class TransformScaleSignAnalysis
    {
        // Head Chop applies scale factors to hide bones in first-person views.
        // A proven nonnegative multiplier can collapse a bone without reversing
        // its winding. It still writes scale and must not make that bone static.
        // https://creators.vrchat.com/avatars/avatar-components/vrc-headchop/
        internal static IEnumerable<Object> NonReversingNativeWriters(GameObject avatar, AvatarAnimationIndex index)
        {
            var candidates = avatar.GetComponentsInChildren<VRCHeadChop>(true)
                .Where(c => c.GetType() == typeof(VRCHeadChop) && Nonnegative(c.globalScaleFactor) &&
                    c.targetBones != null && c.targetBones.All(b => b != null && Nonnegative(b.scaleFactor) &&
                        (b.transform == null || Positive(b.transform.localScale)))).ToArray();
            if (candidates.Length == 0) yield break;
            var analysis = AnimatorReachability.Analyze(avatar, index.Controllers, index.PreservedParameters);
            if (analysis.FallbackReason != null) yield break;
            var proof = new InvariantComponentSettings(index, analysis, index.ControllerEntries);
            foreach (var candidate in candidates)
                if (proof.IsConstant(candidate)) yield return candidate;
        }

        private static bool Nonnegative(float value) => MotionAnalysis.Finite(value) && value >= 0;
        private static bool Positive(Vector3 value) =>
            Nonnegative(value.x) && Nonnegative(value.y) && Nonnegative(value.z) && value.x > 0 && value.y > 0 && value.z > 0;
    }
}
