using System;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // Numerical output equivalence, not a general replacement for equality.
    // Callers establish a continuous observer and bound all contributing error.
    // Never use this for branches, indices, object identity, cache identity,
    // validity checks, parameter domains or an unknown shader operation.
    internal static class NumericalEquivalence
    {
        internal const double RelativeTolerance = 1e-6;

        internal static double Tolerance(float first, float second) =>
            RelativeTolerance * Math.Max(1d, Math.Max(Math.Abs((double)first), Math.Abs((double)second)));

        internal static bool Same(float first, float second, bool allowSmallDifferences = true)
        {
            if (!MotionAnalysis.Finite(first) || !MotionAnalysis.Finite(second)) return false;
            return allowSmallDifferences
                ? Math.Abs((double)first - second) <= Tolerance(first, second)
                : BitConverter.SingleToInt32Bits(first) == BitConverter.SingleToInt32Bits(second);
        }

        internal static bool Same(Matrix4x4 first, Matrix4x4 second, bool allowSmallDifferences = true)
        {
            for (int i = 0; i < 16; i++)
                if (!Same(first[i], second[i], allowSmallDifferences)) return false;
            return true;
        }

        internal static bool AcceptsError(float reference, double absoluteError, bool allowSmallDifferences = true) =>
            MotionAnalysis.Finite(reference) && !double.IsNaN(absoluteError) && !double.IsInfinity(absoluteError) &&
            absoluteError >= 0 && (allowSmallDifferences
                ? absoluteError <= Tolerance(reference, reference) : absoluteError == 0);
    }
}
