using System;
using System.Collections.Generic;
using System.Linq;
using VRC.SDK3.Avatars.ScriptableObjects;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.ParameterLayout
{
    [Serializable]
    internal sealed class MultiplexInput
    {
        public string Name;
        public ValueType Type;
        public int Batch, Slot;
    }

    // Typed reusable payloads, an atomic Int tag, and complete snapshot buffering.
    // Protocol 1 guards independently delayed fields; protocol 2 assumes coherent
    // packet delivery and adds periodic slow recovery sweeps. See CREDITS.md for
    // the VRCFury behavior reference; this protocol and implementation are independent.
    [Serializable]
    internal sealed class MultiplexEncoding
    {
        public int Protocol = 1;
        public string Tag;
        public int Batches;
        public float HoldSeconds;
        public List<ParameterLayoutEntry> Slots = new();
        public List<MultiplexInput> Inputs = new();
        internal int WireCost => 8 + Slots.Sum(p => Cost(p.Type));
        // Leave a publication visible for at least one 5 FPS observer frame, plus
        // the sender's explicit frame step. This is not a network delivery guarantee.
        internal float PublicationHoldSeconds => Math.Max(.2f, HoldSeconds);
        // Tolerate slow phase transitions, but expire before the minimum two-epoch
        // wrap interval (four batches with invalidation, preparation and publication).
        internal int EpochCount => Protocol == 1 ? 2 : Batches <= 31 ? 8 : Batches <= 63 ? 4 : 2;
        internal float FastPublicationSeconds => HoldSeconds;
        internal float RecoveryPublicationSeconds => Math.Max(.25f, FastPublicationSeconds);
        internal bool IsRecoveryEpoch(int epoch) => (epoch + 1) % Math.Min(4, EpochCount) == 0;
        internal float PublicationSeconds(int epoch) => IsRecoveryEpoch(epoch)
            ? RecoveryPublicationSeconds : FastPublicationSeconds;
        internal float ReceiveTimeoutSeconds => Protocol == 1 ? Math.Max(1f, 6 * HoldSeconds)
            : Math.Max(1.2f, 2 * RecoveryPublicationSeconds);
        internal float NominalCycleSeconds => Protocol == 1 ? Batches * (2 * HoldSeconds + PublicationHoldSeconds)
            : Batches * Enumerable.Range(0, EpochCount).Average(PublicationSeconds);
        internal int Code(int epoch, int batch) => 1 + epoch * Batches + batch;
        internal static int Cost(ValueType type) => type == ValueType.Bool ? 1 : 8;
        internal static int Cost(IEnumerable<Parameter> parameters) => parameters.Where(p => p.networkSynced).Sum(p => Cost(p.valueType));

        internal static string Allocate(ISet<string> occupied, string stem)
        {
            string name = stem;
            for (int suffix = 1; !occupied.Add(name); suffix++) name = stem + "_" + suffix;
            return name;
        }

        // Cost decreases monotonically with the batch count. The largest supported
        // count answers feasibility without allocating wire names or Animator assets.
        internal static bool CanFit(Parameter[] all, ISet<string> eligible, int budget, int maxBatches = 127)
        {
            if (budget < 16 || budget > 256)
                throw new InvalidOperationException("Choose a network budget from 16 to 256.");
            if (maxBatches < 2 || maxBatches > 127)
                throw new ArgumentOutOfRangeException(nameof(maxBatches));
            int before = Cost(all);
            if (before <= budget) return true;
            var counts = new Dictionary<ValueType, int>();
            int selected = 0;
            foreach (var parameter in all.Where(p => p.networkSynced && eligible.Contains(p.name)))
            {
                counts.TryGetValue(parameter.valueType, out int count);
                counts[parameter.valueType] = count + 1;
                selected += Cost(parameter.valueType);
            }
            int minimum = before - selected + 8 + counts.Sum(g => ((g.Value + maxBatches - 1) / maxBatches) * Cost(g.Key));
            return counts.Count != 0 && minimum <= budget && minimum < before;
        }

        internal static MultiplexEncoding Plan(Parameter[] all, ISet<string> eligible, int budget, float hold, ISet<string> occupied)
        {
            if (budget < 16 || budget > 256 || !float.IsFinite(hold) || hold < .1f || hold > 1)
                throw new InvalidOperationException("Choose a network budget from 16 to 256 and a packet hold from 0.1 to 1 second.");
            int before = Cost(all);
            if (before <= budget) return null;
            var groups = all.Where(p => p.networkSynced && eligible.Contains(p.name))
                .OrderBy(p => p.name, StringComparer.Ordinal).GroupBy(p => p.valueType).OrderBy(g => g.Key).ToArray();
            int native = before - groups.Sum(g => g.Count() * Cost(g.Key));
            // Searching cycle length directly gives the shortest schedule within the budget.
            // Separate typed slots preserve the platform's native numeric quantization.
            for (int batches = 2; batches <= 127; batches++)
            {
                int cost = native + 8 + groups.Sum(g => ((g.Count() + batches - 1) / batches) * Cost(g.Key));
                if (groups.Length == 0 || cost > budget || cost >= before) continue;
                var result = new MultiplexEncoding
                { Batches = batches, HoldSeconds = hold, Tag = Allocate(occupied, "__AdvancedMux/Tag") };
                foreach (var group in groups)
                {
                    int width = (group.Count() + batches - 1) / batches, start = result.Slots.Count;
                    for (int i = 0; i < width; i++) result.Slots.Add(new ParameterLayoutEntry
                    { Name = Allocate(occupied, "__AdvancedMux/" + group.Key + "/" + i), Type = group.Key });
                    int index = 0;
                    foreach (var parameter in group)
                    {
                        result.Inputs.Add(new MultiplexInput
                        { Name = parameter.name, Type = parameter.valueType, Batch = index / width, Slot = start + index % width });
                        index++;
                    }
                }
                result.Batches = result.Inputs.Max(p => p.Batch) + 1;
                result.Validate();
                return result;
            }
            throw new InvalidOperationException("Cannot fit the synchronized parameter budget: " + before + "/" + budget +
                " bits, " + native + " bits outside the eligible selection. Preserve/Keep Native and momentary-input exclusions are not overridden.");
        }

        internal void Validate()
        {
            if ((Protocol != 1 && Protocol != 2) || string.IsNullOrWhiteSpace(Tag) || Batches < 2 || Batches > 127 ||
                !float.IsFinite(HoldSeconds) || HoldSeconds < .1f || HoldSeconds > 1 || Slots == null || Inputs == null ||
                Slots.Count == 0 || Inputs.Count < 2 || Slots.Any(s => s == null || string.IsNullOrWhiteSpace(s.Name) ||
                    !Enum.IsDefined(typeof(ValueType), s.Type)))
                throw new InvalidOperationException("Invalid time multiplexing contract.");
            var wire = new HashSet<string>(Slots.Select(s => s.Name), StringComparer.Ordinal);
            if (wire.Count != Slots.Count || !wire.Add(Tag)) throw new InvalidOperationException("Duplicate multiplexing wire names.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var assignments = new HashSet<(int, int)>();
            foreach (var input in Inputs)
                if (input == null || string.IsNullOrWhiteSpace(input.Name) || !names.Add(input.Name) || wire.Contains(input.Name) ||
                    input.Batch < 0 || input.Batch >= Batches || input.Slot < 0 || input.Slot >= Slots.Count ||
                    input.Type != Slots[input.Slot].Type || !assignments.Add((input.Batch, input.Slot)))
                    throw new InvalidOperationException("Invalid multiplexed input mapping.");
            if (Enumerable.Range(0, Batches).Any(b => Inputs.All(p => p.Batch != b)) ||
                Enumerable.Range(0, Slots.Count).Any(s => Inputs.All(p => p.Slot != s)) || WireCost >= Inputs.Sum(p => Cost(p.Type)))
                throw new InvalidOperationException("Incomplete or non-saving multiplexing contract.");
        }

        internal Parameter[] WireDeclarations() => new[]
        { new Parameter { name = Tag, valueType = ValueType.Int, networkSynced = true, saved = false } }
            .Concat(Slots.Select(s => new Parameter { name = s.Name, valueType = s.Type, networkSynced = true, saved = false })).ToArray();
    }
}
