using System;
using System.Collections.Generic;
using System.Linq;

namespace Noname.AvatarTools.MeshReduction
{
    internal readonly struct TriangleAllocationInput
    {
        internal readonly int Count, Minimum;
        internal readonly double Weight;
        internal TriangleAllocationInput(int count, int minimum, double weight = 1)
        {
            if (count < 0 || minimum < 0 || double.IsNaN(weight) || double.IsInfinity(weight) || weight <= 0)
                throw new ArgumentException("Triangle allocation requires nonnegative counts and positive finite weights.");
            Count = count; Minimum = Math.Min(count, minimum); Weight = weight;
        }
    }

    internal static class TriangleBudgetAllocator
    {
        // Weighted water filling with per-renderer floors and caps. Ties use input order.
        internal static int[] Allocate(IReadOnlyList<TriangleAllocationInput> inputs, long available)
        {
            var result = inputs.Select(i => i.Minimum).ToArray();
            long left = Math.Max(0, available - result.Sum(v => (long)v));
            var pending = Enumerable.Range(0, inputs.Count).Where(i => result[i] < inputs[i].Count).ToList();
            while (left > 0 && pending.Count > 0)
            {
                double total = pending.Sum(i => inputs[i].Weight * (inputs[i].Count - inputs[i].Minimum));
                var capped = pending.Where(i => left * (inputs[i].Weight * (inputs[i].Count - inputs[i].Minimum) / total)
                    >= inputs[i].Count - result[i]).ToArray();
                if (capped.Length > 0)
                {
                    foreach (int i in capped)
                    {
                        left -= inputs[i].Count - result[i]; result[i] = inputs[i].Count; pending.Remove(i);
                    }
                    continue;
                }
                long allocation = left;
                var fractions = new List<(int index, double remainder)>();
                foreach (int i in pending)
                {
                    double share = allocation * (inputs[i].Weight * (inputs[i].Count - inputs[i].Minimum) / total);
                    int whole = (int)Math.Floor(share);
                    result[i] += whole; left -= whole; fractions.Add((i, share - whole));
                }
                foreach (var item in fractions.OrderByDescending(v => v.remainder).ThenBy(v => v.index))
                {
                    if (left == 0) break;
                    if (result[item.index] < inputs[item.index].Count) { result[item.index]++; left--; }
                }
                break;
            }
            return result;
        }
    }
}
