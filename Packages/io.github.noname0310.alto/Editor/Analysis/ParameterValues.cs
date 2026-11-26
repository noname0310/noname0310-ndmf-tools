using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Animations;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // A bounded finite set, or an enclosing interval. Unknown contains every possible value.
    // Values only grow during analysis; the solver widens repeatedly changing cells to Unknown.
    internal sealed class ParameterValues
    {
        internal readonly double Min, Max;
        private readonly double[] values;
        internal static readonly ParameterValues Unknown = new(double.NegativeInfinity, double.PositiveInfinity);
        private static readonly ParameterValues Zero = new(0, 0, new double[] { 0 });
        private static readonly ParameterValues One = new(1, 1, new double[] { 1 });
        private static readonly ParameterValues Boolean = new(0, 1, new double[] { 0, 1 });
        internal bool TryGetConstant(out float value)
        {
            value = (float)Min;
            return Min == Max && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private ParameterValues(double min, double max, double[] finite = null)
        { Min = min; Max = max; values = finite; }

        internal static ParameterValues Constant(double value)
            => double.IsNaN(value) || double.IsInfinity(value) ? Unknown : value == 1 ? One :
                value == 0 && !double.IsNegativeInfinity(1 / value) ? Zero : new(value, value, new[] { value });

        internal static ParameterValues Range(double min, double max)
            => double.IsNaN(min) || double.IsNaN(max) || min > max ? Unknown :
                min == max ? Constant(min) : new(min, max);

        internal ParameterValues Union(ParameterValues other)
        {
            if (Same(other)) return this;
            if (values == null && Min <= other.Min && Max >= other.Max) return this;
            if (other.values == null && other.Min <= Min && other.Max >= Max) return other;
            if (values != null && other.values != null)
            {
                // Finite domains are sorted, unique and bounded to eight values.
                // Merge them directly; neither a hash set nor a sort is needed.
                Span<double> merged = stackalloc double[8];
                int left = 0, right = 0, count = 0;
                while (left < values.Length || right < other.values.Length)
                {
                    double next;
                    if (right == other.values.Length || left < values.Length && values[left] <= other.values[right])
                    {
                        next = values[left++];
                        if (right < other.values.Length && other.values[right] == next) right++;
                    }
                    else next = other.values[right++];
                    if (count == merged.Length) return Range(Math.Min(Min, other.Min), Math.Max(Max, other.Max));
                    merged[count++] = next;
                }
                var set = new double[count]; merged.Slice(0, count).CopyTo(set);
                return new(set[0], set[set.Length - 1], set);
            }
            return Range(Math.Min(Min, other.Min), Math.Max(Max, other.Max));
        }

        internal bool Same(ParameterValues other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (Min != other.Min || Max != other.Max) return false;
            if (values == null) return other.values == null;
            if (other.values == null || values.Length != other.values.Length) return false;
            for (int i = 0; i < values.Length; i++) if (values[i] != other.values[i]) return false;
            return true;
        }

        internal ParameterValues ConvertTo(AnimatorControllerParameterType type)
        {
            if (type == AnimatorControllerParameterType.Bool || type == AnimatorControllerParameterType.Trigger)
            {
                if (Min == 0 && Max == 0) return Zero;
                if (Min > 0 || Max < 0) return One;
                return Boolean;
            }
            if (type == AnimatorControllerParameterType.Float)
            {
                // Integer defaults and Driver sources enter the lattice exactly.
                // A Float destination rounds them to its actual storage precision.
                bool exact = Min == (double)(float)Min && Max == (double)(float)Max;
                if (exact && values != null)
                    foreach (double value in values) exact &= value == (double)(float)value;
                if (exact) return this;
                if (values == null) return Range((float)Min, (float)Max);
                ParameterValues rounded = null;
                foreach (double value in values)
                {
                    var converted = Constant((float)value);
                    rounded = rounded == null ? converted : rounded.Union(converted);
                }
                return rounded;
            }
            if (type != AnimatorControllerParameterType.Int) return this;
            if (Min < int.MinValue || Max > int.MaxValue) return Unknown;
            // Cover both rounding and truncation, including SDK conversions between parameter types.
            if (values == null)
            {
                double first = Math.Floor(Min), last = Math.Ceiling(Max);
                if (last - first < 8)
                {
                    var set = new double[(int)(last - first) + 1];
                    for (int i = 0; i < set.Length; i++) set[i] = first + i;
                    return new(first, last, set);
                }
                return Range(first, last);
            }
            ParameterValues result = null;
            foreach (double value in values)
            {
                var converted = Constant(Math.Floor(value)).Union(Constant(Math.Ceiling(value)));
                result = result == null ? converted : result.Union(converted);
            }
            return result;
        }

        internal bool CanSatisfy(IEnumerable<AnimatorCondition> conditions)
        {
            var tests = conditions as IReadOnlyList<AnimatorCondition> ?? conditions.ToArray();
            for (int i = 0; i < tests.Count; i++)
                if (float.IsNaN(tests[i].threshold) || float.IsInfinity(tests[i].threshold)) return true;
            if (values != null)
            {
                foreach (double value in values)
                {
                    bool matches = true;
                    for (int i = 0; i < tests.Count; i++)
                        if (!Matches(value, tests[i])) { matches = false; break; }
                    if (matches) return true;
                }
                return false;
            }
            double low = Min, high = Max;
            bool lowOpen = false, highOpen = false;
            HashSet<double> forbidden = null;
            for (int i = 0; i < tests.Count; i++)
            {
                var condition = tests[i];
                double threshold = condition.threshold;
                switch (condition.mode)
                {
                    case AnimatorConditionMode.If: (forbidden ??= new()).Add(0); break;
                    case AnimatorConditionMode.IfNot: threshold = 0; goto case AnimatorConditionMode.Equals;
                    case AnimatorConditionMode.Equals:
                        if (threshold < low || threshold > high || threshold == low && lowOpen || threshold == high && highOpen)
                            return false;
                        low = high = threshold; lowOpen = highOpen = false; break;
                    case AnimatorConditionMode.NotEqual: (forbidden ??= new()).Add(threshold); break;
                    case AnimatorConditionMode.Greater:
                        if (threshold >= low) { low = threshold; lowOpen = true; }
                        break;
                    case AnimatorConditionMode.Less:
                        if (threshold <= high) { high = threshold; highOpen = true; }
                        break;
                }
            }
            return low < high || low == high && !lowOpen && !highOpen && (forbidden == null || !forbidden.Contains(low));
        }

        private static bool Matches(double value, AnimatorCondition condition)
        {
            switch (condition.mode)
            {
                case AnimatorConditionMode.If: return value != 0;
                case AnimatorConditionMode.IfNot: return value == 0;
                case AnimatorConditionMode.Equals: return value == condition.threshold;
                case AnimatorConditionMode.NotEqual: return value != condition.threshold;
                case AnimatorConditionMode.Greater: return value > condition.threshold;
                case AnimatorConditionMode.Less: return value < condition.threshold;
                default: return true;
            }
        }
    }
}
