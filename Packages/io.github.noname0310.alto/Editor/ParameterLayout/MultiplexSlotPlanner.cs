using System;
using System.Collections.Generic;
using System.Linq;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.ParameterLayout
{
    // At a fixed batch count, each slot holds at most that many inputs. Exact
    // widths force their own slots; minimum widths can use any wider free slot
    // of the same type and representation. Visit widths from high to low:
    // reserve required exact slots, use spare capacity, then open the cheapest
    // slots capable of holding the remaining inputs. A more expensive new slot
    // cannot help a future (less demanding) input. This minimizes payload cost
    // without enumerating subsets or converting between quantization grids.
    internal sealed class MultiplexSlotPlanner
    {
        internal sealed class Slot
        {
            internal ParameterLayoutEntry Encoding;
            internal readonly List<Parameter> Inputs = new();
        }

        private sealed class Family
        {
            internal ValueType Type;
            internal bool Flipped;
            internal readonly List<Parameter>[] Exact = Enumerable.Range(0, 9).Select(_ => new List<Parameter>()).ToArray();
            internal readonly List<Parameter>[] Minimum = Enumerable.Range(0, 9).Select(_ => new List<Parameter>()).ToArray();
        }

        private readonly Family[] families;
        internal readonly int InputCost;

        internal MultiplexSlotPlanner(IEnumerable<Parameter> parameters, ISet<string> minimumPrecision = null)
        {
            var input = parameters.OrderBy(p => p.name, StringComparer.Ordinal).ToArray();
            InputCost = input.Sum(MultiplexEncoding.Cost);
            families = input.GroupBy(p => (p.valueType, Flipped: p.valueType != ValueType.Bool && p.flippedRepresentation))
                .OrderBy(g => g.Key.valueType).ThenBy(g => g.Key.Flipped).Select(g =>
                {
                    var family = new Family { Type = g.Key.valueType, Flipped = g.Key.Flipped };
                    foreach (var parameter in g)
                    {
                        var entry = ParameterLayoutEntry.Capture(parameter);
                        if (!entry.Valid) throw new InvalidOperationException("Invalid parameter wire encoding: " + parameter.name);
                        var buckets = parameter.valueType == ValueType.Float && minimumPrecision?.Contains(parameter.name) == true
                            ? family.Minimum : family.Exact;
                        buckets[entry.Cost].Add(parameter);
                    }
                    return family;
                }).ToArray();
        }

        internal int Cost(int batches)
        {
            if (batches < 1) throw new ArgumentOutOfRangeException(nameof(batches));
            int cost = 0;
            foreach (var family in families)
            {
                int capacity = 0;
                for (int bits = 8; bits >= 1; bits--)
                {
                    int exact = family.Exact[bits].Count, flexible = family.Minimum[bits].Count;
                    int slots = (exact + batches - 1) / batches;
                    cost += slots * bits; capacity += slots * batches - exact;
                    slots = (Math.Max(0, flexible - capacity) + batches - 1) / batches;
                    cost += slots * bits; capacity += slots * batches - flexible;
                }
            }
            return cost;
        }

        internal List<Slot> Allocate(int batches)
        {
            if (batches < 1) throw new ArgumentOutOfRangeException(nameof(batches));
            var result = new List<Slot>();
            foreach (var family in families)
            {
                var slots = new List<Slot>();
                Slot New(int bits)
                {
                    var slot = new Slot { Encoding = new ParameterLayoutEntry { Type = family.Type,
                        BitWidth = bits, FlippedRepresentation = family.Flipped } };
                    slots.Add(slot); return slot;
                }
                for (int bits = 8; bits >= 1; bits--)
                {
                    Slot exact = null;
                    foreach (var parameter in family.Exact[bits])
                    {
                        if (exact == null || exact.Inputs.Count == batches) exact = New(bits);
                        exact.Inputs.Add(parameter);
                    }
                    foreach (var parameter in family.Minimum[bits])
                    {
                        var slot = slots.Where(s => s.Inputs.Count < batches).OrderBy(s => s.Encoding.Cost).FirstOrDefault() ?? New(bits);
                        slot.Inputs.Add(parameter);
                    }
                }
                result.AddRange(slots);
            }
            return result;
        }
    }
}
