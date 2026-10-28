using System;
using System.Linq;
using Noname.AvatarTools.MeshReduction;
using NUnit.Framework;

namespace Noname.AvatarTools.Tests
{
    public sealed class TriangleAllocationFuzzTests
    {
        [TestCase(1937)] [TestCase(41381)] [TestCase(20261029)]
        public void SeededAllocationConservesBudgetAndAuthoredLimits(int seed)
        {
            var random = new Random(seed);
            for (int iteration = 0; iteration < 2048; iteration++)
            {
                var inputs = Enumerable.Range(0, random.Next(0, 65)).Select(_ =>
                {
                    int count = random.Next(8) == 0 ? int.MaxValue : random.Next(0, 100001);
                    int floor = random.Next(6) == 0 ? count : random.Next(0, Math.Max(1, count));
                    // All generated weights are representable positive float settings.
                    double weight = random.Next(3) == 0 ? 1 : (float)Math.Pow(10, random.Next(-38, 39));
                    return new TriangleAllocationInput(count, floor, weight);
                }).ToArray();
                long minimum = inputs.Sum(input => (long)input.Minimum);
                long maximum = inputs.Sum(input => (long)input.Count);
                var budgets = new[] { -1L, 0L, minimum - 1, minimum, maximum, maximum + 1,
                    (long)(random.NextDouble() * maximum) };
                foreach (long budget in budgets)
                {
                    var result = TriangleBudgetAllocator.Allocate(inputs, budget);
                    string trace = "Seed " + seed + ", case " + iteration + ", budget " + budget + ", inputs: " +
                        string.Join(";", inputs.Select(i => i.Count + "/" + i.Minimum + "/" + i.Weight.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                    Assert.That(result.Length, Is.EqualTo(inputs.Length), trace);
                    // Conservation is a specification-level oracle independent of water filling.
                    Assert.That(result.Sum(value => (long)value), Is.EqualTo(Math.Min(maximum, Math.Max(minimum, budget))), trace);
                    for (int i = 0; i < result.Length; i++)
                        Assert.That(result[i], Is.InRange(inputs[i].Minimum, inputs[i].Count), trace + ", renderer " + i);
                }
            }
        }
    }
}
