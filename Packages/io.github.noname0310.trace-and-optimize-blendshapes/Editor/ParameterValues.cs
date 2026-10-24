using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Animations;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization
{
    // A bounded finite set, or an enclosing interval. Unknown contains every possible value.
    // Values only grow during analysis; the solver widens repeatedly changing cells to Unknown.
    internal sealed class ParameterValues
    {
        internal readonly double Min, Max;
        private readonly double[] values;
        internal static readonly ParameterValues Unknown = new(double.NegativeInfinity, double.PositiveInfinity);

        private ParameterValues(double min, double max, double[] finite = null)
        { Min = min; Max = max; values = finite; }

        internal static ParameterValues Constant(double value)
            => double.IsNaN(value) || double.IsInfinity(value) ? Unknown : new(value, value, new[] { value });

        internal static ParameterValues Range(double min, double max)
            => double.IsNaN(min) || double.IsNaN(max) || min > max ? Unknown :
                min == max ? Constant(min) : new(min, max);

        internal ParameterValues Union(ParameterValues other)
        {
            if (Same(other)) return this;
            if (values != null && other.values != null)
            {
                var set = values.Concat(other.values).Distinct().OrderBy(v => v).ToArray();
                if (set.Length <= 8) return new(set[0], set[set.Length - 1], set);
            }
            return Range(Math.Min(Min, other.Min), Math.Max(Max, other.Max));
        }

        internal bool Same(ParameterValues other)
            => Min == other.Min && Max == other.Max && (values == null ? other.values == null :
                other.values != null && values.SequenceEqual(other.values));

        internal ParameterValues ConvertTo(AnimatorControllerParameterType type)
        {
            if (type == AnimatorControllerParameterType.Bool || type == AnimatorControllerParameterType.Trigger)
            {
                if (Min == 0 && Max == 0) return Constant(0);
                if (Min > 0 || Max < 0) return Constant(1);
                return Constant(0).Union(Constant(1));
            }
            if (type != AnimatorControllerParameterType.Int) return this;
            if (Min < int.MinValue || Max > int.MaxValue) return Unknown;
            // Cover both rounding and truncation, including SDK conversions between parameter types.
            if (values == null)
            {
                double first = Math.Floor(Min), last = Math.Ceiling(Max);
                if (last - first < 8)
                    return new(first, last, Enumerable.Range(0, (int)(last - first) + 1).Select(i => first + i).ToArray());
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
            var tests = conditions.ToArray();
            if (tests.Any(c => float.IsNaN(c.threshold) || float.IsInfinity(c.threshold))) return true;
            if (values != null) return values.Any(v => tests.All(c => Matches(v, c)));
            double low = Min, high = Max;
            bool lowOpen = false, highOpen = false;
            var forbidden = new HashSet<double>();
            foreach (var condition in tests)
            {
                double threshold = condition.threshold;
                switch (condition.mode)
                {
                    case AnimatorConditionMode.If: forbidden.Add(0); break;
                    case AnimatorConditionMode.IfNot: threshold = 0; goto case AnimatorConditionMode.Equals;
                    case AnimatorConditionMode.Equals:
                        if (threshold < low || threshold > high || threshold == low && lowOpen || threshold == high && highOpen)
                            return false;
                        low = high = threshold; lowOpen = highOpen = false; break;
                    case AnimatorConditionMode.NotEqual: forbidden.Add(threshold); break;
                    case AnimatorConditionMode.Greater:
                        if (threshold >= low) { low = threshold; lowOpen = true; }
                        break;
                    case AnimatorConditionMode.Less:
                        if (threshold <= high) { high = threshold; highOpen = true; }
                        break;
                }
            }
            return low < high || low == high && !lowOpen && !highOpen && !forbidden.Contains(low);
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
