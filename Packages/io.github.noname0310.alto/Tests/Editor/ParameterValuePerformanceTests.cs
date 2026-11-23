using NUnit.Framework;
using Noname.AvatarTools.Analysis;
// The pre-optimization implementation is retained as a differential oracle.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Animations;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    // A bounded finite set, or an enclosing interval. Unknown contains every possible value.
    // Values only grow during analysis; the solver widens repeatedly changing cells to Unknown.
    internal sealed class ReferenceParameterValues
    {
        internal readonly double Min, Max;
        private readonly double[] values;
        internal static readonly ReferenceParameterValues Unknown = new(double.NegativeInfinity, double.PositiveInfinity);
        internal bool TryGetConstant(out float value)
        {
            value = (float)Min;
            return Min == Max && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private ReferenceParameterValues(double min, double max, double[] finite = null)
        { Min = min; Max = max; values = finite; }

        internal static ReferenceParameterValues Constant(double value)
            => double.IsNaN(value) || double.IsInfinity(value) ? Unknown : new(value, value, new[] { value });

        internal static ReferenceParameterValues Range(double min, double max)
            => double.IsNaN(min) || double.IsNaN(max) || min > max ? Unknown :
                min == max ? Constant(min) : new(min, max);

        internal ReferenceParameterValues Union(ReferenceParameterValues other)
        {
            if (Same(other)) return this;
            if (values != null && other.values != null)
            {
                var set = values.Concat(other.values).Distinct().OrderBy(v => v).ToArray();
                if (set.Length <= 8) return new(set[0], set[set.Length - 1], set);
            }
            return Range(Math.Min(Min, other.Min), Math.Max(Max, other.Max));
        }

        internal bool Same(ReferenceParameterValues other)
            => Min == other.Min && Max == other.Max && (values == null ? other.values == null :
                other.values != null && values.SequenceEqual(other.values));

        internal ReferenceParameterValues ConvertTo(AnimatorControllerParameterType type)
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
            ReferenceParameterValues result = null;
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


namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void ParameterValueAllocationOptimizationsPreserveAbstractDomain()
        {
            var actualField = typeof(ParameterValues).GetField("values", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var expectedField = typeof(ReferenceParameterValues).GetField("values", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var random = new System.Random(4927);
            double[] seeds = { -0.0, 0, 1, -1, 2, 0.25, -0.5, 3.75, -8.25, 20, double.NaN, double.PositiveInfinity,
                double.NegativeInfinity, int.MaxValue + 1d, int.MinValue - 1d };
            for (int scenario = 0; scenario < 1500; scenario++)
            {
                double seed = seeds[random.Next(seeds.Length)];
                var actual = ParameterValues.Constant(seed);
                var expected = ReferenceParameterValues.Constant(seed);
                for (int step = 0; step < 20; step++)
                {
                    double a = random.Next(3) == 0 ? seeds[random.Next(seeds.Length)] : random.Next(-12, 13) * 0.25;
                    double b = random.Next(-12, 13) * 0.25;
                    switch (random.Next(4))
                    {
                        case 0:
                        case 1: actual = actual.Union(ParameterValues.Constant(a)); expected = expected.Union(ReferenceParameterValues.Constant(a)); break;
                        case 2: actual = actual.Union(ParameterValues.Range(a, b)); expected = expected.Union(ReferenceParameterValues.Range(a, b)); break;
                        default:
                            var type = new[] { AnimatorControllerParameterType.Float, AnimatorControllerParameterType.Int, AnimatorControllerParameterType.Bool, AnimatorControllerParameterType.Trigger }[random.Next(4)];
                            actual = actual.ConvertTo(type); expected = expected.ConvertTo(type); break;
                    }
                    Assert.That(actual.Min, Is.EqualTo(expected.Min));
                    Assert.That(actual.Max, Is.EqualTo(expected.Max));
                    Assert.That((double[])actualField.GetValue(actual), Is.EqualTo((double[])expectedField.GetValue(expected)));
                    var conditions = new AnimatorCondition[random.Next(5)];
                    for (int i = 0; i < conditions.Length; i++)
                        conditions[i] = new AnimatorCondition { parameter = "value", mode = (AnimatorConditionMode)random.Next(9), threshold = (float)seeds[random.Next(seeds.Length)] };
                    Assert.That(actual.CanSatisfy(conditions), Is.EqualTo(expected.CanSatisfy(conditions)));
                }
            }
            Assert.That(ParameterValues.Constant(-0.0).TryGetConstant(out float negativeZero), Is.True);
            Assert.That(float.IsNegativeInfinity(1 / negativeZero), Is.True);
        }
    }
}
