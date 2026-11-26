using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // This classifies possible script dispatch, not every observable property of
    // an authored event. Empty names may still produce native diagnostics, and
    // events still belong to duration, structural and object-reference checks.
    internal static class AnimationEventEffects
    {
        internal static bool MayInvokeCallback(AnimationEvent animationEvent)
            => animationEvent == null || !string.IsNullOrEmpty(animationEvent.functionName);

        internal static bool MayInvokeCallback(AnimationClip clip)
        {
            if (clip == null) return true;
            foreach (var animationEvent in clip.events)
                if (MayInvokeCallback(animationEvent)) return true;
            return false;
        }
    }
}
