using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using UnityEngine;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.ParameterLayout
{
    internal sealed class PlannedParameterTransport
    {
        internal Parameter[] Parameters;
        internal MultiplexEncoding Multiplexing;
        internal List<ParameterLayoutEntry> AvatarDriven = new();
        internal readonly List<string> Messages = new(), Warnings = new();
    }

    internal static class ParameterTransportPlanning
    {
        internal static PlannedParameterTransport Choose(GameObject avatar, VirtualAnimatorController[] controllers,
            IDictionary<object, VirtualAnimatorController> entries, ParameterCompressionSettings settings, Parameter[] input,
            ISet<string> protectedNames, IReadOnlyDictionary<string, int> keep, ISet<string> minimumPrecision,
            ISet<string> occupied, List<ParameterLayoutEntry> automatic, OptimizationBuildReport report)
        {
            MultiplexPriorities priorities = null;
            var messages = new List<string>();
            if (settings.Multiplexing == ParameterMultiplexingMode.AutomaticByPriority)
                priorities = MultiplexPriorities.Analyze(avatar, controllers, protectedNames, entries);
            var selection = settings.Multiplexing == ParameterMultiplexingMode.AutomaticMenuInputs
                ? MultiplexSelection.Analyze(avatar, controllers, protectedNames, messages, entries) : null;
            var eligible = selection?.Eligible ?? (settings.Multiplexing == ParameterMultiplexingMode.AllSynchronizedInputs
                ? input.Where(p => p.networkSynced).Select(p => p.name).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(settings.MultiplexNames, StringComparer.Ordinal));
            if (settings.Multiplexing == ParameterMultiplexingMode.SelectedInputs)
                eligible.UnionWith(input.Where(p => settings.MultiplexPrefixes.Any(prefix => p.name.StartsWith(prefix, StringComparison.Ordinal))).Select(p => p.name));
            eligible.ExceptWith(protectedNames);
            if (priorities == null && keep.Count > 0 && settings.InputAssumption == ParameterInputAssumption.ExternalWithCompression)
            {
                priorities = new MultiplexPriorities();
                foreach (string name in eligible) priorities.Candidates[name] = MultiplexPriority.PersistentMenu;
            }
            if (priorities != null) foreach (var pair in keep) priorities.KeepPriorities[pair.Key] = pair.Value;

            // Unsigned conversion can create a separate representation family.
            // Compare the original plan with Float, Int, and combined reductions
            // rather than assuming each saved declaration bit improves the mux.
            var groups = automatic.GroupBy(p => p.Type).Select(g => g.ToList()).ToArray();
            PlannedParameterTransport best = null; (int Keep, int Kind, int Batches, int Cost) bestScore = default;
            InvalidOperationException failure = null;
            for (int mask = 0; mask < 1 << groups.Length; mask++)
            {
                var changed = groups.Where((g, i) => (mask & (1 << i)) != 0).SelectMany(g => g).ToList();
                var plan = new PlannedParameterTransport { Parameters = AvatarDrivenEncodingAnalysis.Apply(input, changed), AvatarDriven = changed };
                var minimum = new HashSet<string>(minimumPrecision, StringComparer.Ordinal);
                minimum.UnionWith(changed.Where(p => p.Type == ValueType.Float).Select(p => p.Name));
                try
                {
                    if (settings.Multiplexing != ParameterMultiplexingMode.Disabled)
                        plan.Multiplexing = priorities != null
                            ? priorities.Plan(plan.Parameters, settings.Budget, settings.HoldSeconds, new HashSet<string>(occupied), plan.Messages, plan.Warnings, minimum)
                            : MultiplexEncoding.Plan(plan.Parameters, eligible, settings.Budget, settings.HoldSeconds, new HashSet<string>(occupied), minimum);
                }
                catch (InvalidOperationException e) { failure ??= e; continue; }
                int cost = MultiplexEncoding.Cost(plan.Parameters), lastKeep = -1, lastKind = -1;
                if (plan.Multiplexing != null)
                {
                    cost += plan.Multiplexing.WireCost - plan.Multiplexing.Inputs.Sum(p => p.Cost);
                    foreach (var member in plan.Multiplexing.Inputs)
                    {
                        int memberKeep = keep.TryGetValue(member.Name, out int value) ? value : 0;
                        int kind = priorities != null && priorities.Candidates.TryGetValue(member.Name, out var priority) ? (int)priority : 0;
                        if (memberKeep > lastKeep) { lastKeep = memberKeep; lastKind = kind; }
                        else if (memberKeep == lastKeep) lastKind = Math.Max(lastKind, kind);
                    }
                }
                var score = (lastKeep, lastKind, plan.Multiplexing?.Batches ?? 0, cost);
                if (best == null || score.CompareTo(bestScore) < 0) { best = plan; bestScore = score; }
            }
            report.Messages.AddRange(messages);
            if (best == null) throw new InvalidOperationException((failure?.Message ?? "Cannot plan parameter transport.") +
                (selection != null ? " Selection: " + selection.Summary(input) : ""));
            if (automatic.Count != 0)
                best.Messages.Add("Avatar-driven unsigned reduction: selected " + best.AvatarDriven.Count + " of " + automatic.Count +
                    " proven candidates after comparing original and reduced representation families. Priority and cycle length precede bit cost.");
            if (settings.Multiplexing == ParameterMultiplexingMode.AllSynchronizedInputs)
                best.Messages.Add("All Synchronized Inputs explicitly permits snapshot delay for continuous and momentary inputs. " +
                    "Input assumptions and Keep rules still apply; brief changes between snapshots can be missed.");
            return best;
        }
    }
}
