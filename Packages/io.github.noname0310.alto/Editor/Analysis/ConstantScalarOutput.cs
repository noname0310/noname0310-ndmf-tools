using nadena.dev.ndmf.animator;
using UnityEditor;

namespace Noname.AvatarTools.Analysis
{
    // Continuous scalar evaluation only. Discrete/reference values and quaternion
    // components require their own semantics in the common motion analyzer.
    internal static class ConstantScalarOutput
    {
        internal static bool TryEvaluate(VirtualMotion motion, EditorCurveBinding binding, float? defaultValue,
            AnimatorReachability analysis, out float value, bool writeDefaults = false)
            => ConstantMotionOutput.TryEvaluateScalar(motion, binding, defaultValue, analysis, out value, writeDefaults);
    }
}
