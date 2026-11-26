using System.Collections.Generic;
using nadena.dev.ndmf.animator;
using UnityEditor;

namespace Noname.AvatarTools.Analysis
{
    // Compatibility entry point for goal-matching consumers. Missing tracks may
    // match only the caller's explicitly established default-write contract.
    internal static class ConstantPropertyOutput
    {
        internal static bool Matches(VirtualMotion motion, EditorCurveBinding binding, float value, bool defaultMatches,
            AnimatorReachability analysis, Dictionary<VirtualMotion, HashSet<EditorCurveBinding>> bindings,
            Dictionary<VirtualMotion, bool> cache, HashSet<VirtualMotion> stack, bool allowInterpolation = false, bool writeDefaults = false,
            ConstantOutputKind kind = ConstantOutputKind.Scalar, bool allowSmallNumericalDifferences = false)
            => ConstantMotionOutput.Matches(motion, binding, value, defaultMatches, analysis, bindings, cache, stack, allowInterpolation, writeDefaults, kind,
                allowSmallNumericalDifferences);
    }
}
