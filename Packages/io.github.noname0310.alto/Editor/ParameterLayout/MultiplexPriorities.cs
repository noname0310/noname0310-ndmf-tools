using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Expressions;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;

namespace Noname.AvatarTools.ParameterLayout
{
    internal enum MultiplexPriority { PersistentMenu, InternalComputed, RuntimeDriven, ExternalInput }

    internal sealed class MultiplexPriorities
    {
        internal readonly Dictionary<string, MultiplexPriority> Candidates = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, string> Protected = new(StringComparer.Ordinal);

        // These priorities infer the likely input source, not the user's OSC configuration.
        // Menu inputs can also be driven externally. Explicit preservation always wins.
        internal static MultiplexPriorities Analyze(GameObject avatar, VirtualAnimatorController[] controllers,
            ISet<string> preserved, IDictionary<object, VirtualAnimatorController> entries = null)
        {
            var result = new MultiplexPriorities();
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            var declared = ParameterLayoutModel.Read(descriptor.expressionParameters?.parameters);
            var menuInputs = new HashSet<string>(StringComparer.Ordinal);
            var momentary = new HashSet<string>(StringComparer.Ordinal);
            var puppets = new HashSet<string>(StringComparer.Ordinal);
            void Add(ISet<string> names, string name) { if (!string.IsNullOrEmpty(name)) names.Add(name); }
            foreach (var menu in ExpressionMenuOptimizer.Menus(descriptor.expressionsMenu))
            foreach (var control in menu.controls ?? new())
            {
                if (control == null) continue;
                if (control.type == ControlType.Toggle) Add(menuInputs, control.parameter?.name);
                else if (control.type == ControlType.RadialPuppet)
                {
                    Add(menuInputs, control.subParameters?.FirstOrDefault()?.name);
                    Add(momentary, control.parameter?.name);
                }
                else if (control.type is ControlType.TwoAxisPuppet or ControlType.FourAxisPuppet)
                {
                    foreach (var parameter in control.subParameters ?? Array.Empty<VRCExpressionsMenu.Control.Parameter>())
                        Add(puppets, parameter?.name);
                    Add(momentary, control.parameter?.name);
                }
                else foreach (string name in ExpressionMenuOptimizer.Parameters(control)) Add(momentary, name);
            }
            var graph = AnimatorParameterDependencies.Collect(avatar, controllers, controllerEntries: entries);
            var nodes = controllers.SelectMany(c => c.AllReachableNodes()).Distinct().ToArray();
            var clips = nodes.OfType<VirtualClip>().ToArray();
            var drivers = nodes.OfType<VirtualState>().SelectMany(s => s.Behaviours)
                .Concat(nodes.OfType<VirtualStateMachine>().SelectMany(m => m.Behaviours)).OfType<VRC_AvatarParameterDriver>().ToArray();
            IEnumerable<string> ClipWrites(VirtualClip clip) => clip.GetFloatCurveBindings()
                .Where(b => graph.IsParameter(clip, b)).Select(b => b.propertyName);
            IEnumerable<string> DynamicWrites(StateMachineBehaviour behaviour) => behaviour is VRC_AvatarParameterDriver driver
                ? (driver.parameters ?? new()).Where(p => p != null && p.type is VRC_AvatarParameterDriver.ChangeType.Add or
                    VRC_AvatarParameterDriver.ChangeType.Random).Select(p => p.name) : Array.Empty<string>();
            var computed = new HashSet<string>(clips.SelectMany(ClipWrites), StringComparer.Ordinal);
            var dataWriters = new HashSet<string>(computed, StringComparer.Ordinal);
            foreach (var operation in drivers.SelectMany(d => d.parameters ?? new()).Where(p => p != null))
            {
                Add(computed, operation.name);
                if (operation.type != VRC_AvatarParameterDriver.ChangeType.Set) Add(dataWriters, operation.name);
            }
            var sensors = AvatarParameterInputs.Sensors(avatar).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            var runtimeSeeds = sensors.Concat(puppets).Concat(AnimatorReachability.BuiltIns.Where(n => n != "IsLocal"));
            var runtime = graph.AffectedByWriters(runtimeSeeds, ClipWrites, DynamicWrites);
            var external = ExternalParameterAccess.Collect();
            var analysis = AnimatorReachability.Analyze(avatar, controllers);
            var publicInputs = declared.Select(p => p.name).ToHashSet(StringComparer.Ordinal);
            var knownControls = menuInputs.Concat(puppets).Concat(momentary).Concat(sensors)
                .Concat(AnimatorReachability.BuiltIns.Where(n => n != "IsLocal")).ToHashSet(StringComparer.Ordinal);
            var externalSeeds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in declared.Select(p => p.name).Concat(controllers.SelectMany(c => c.Parameters.Keys)).Distinct())
            {
                if (external.IsRead(name) || external.IsWritten(name)) externalSeeds.Add(name);
                // A proven private constant (for example a Direct tree's unit weight)
                // must not classify every dependent output as an unexplained input.
                // Public inputs and registered external access remain possible sources.
                if (!publicInputs.Contains(name) && analysis.Values(name).TryGetConstant(out _)) continue;
                // An initialization-only Set is not evidence that an input is internally
                // produced. Keep unexplained public inputs (including OSC candidates) last.
                if (!AnimatorReachability.BuiltIns.Contains(name) && !knownControls.Contains(name) && !dataWriters.Contains(name) &&
                    (!computed.Contains(name) || !graph.Expand(new[] { name }).Overlaps(knownControls))) externalSeeds.Add(name);
            }
            var externallyDriven = graph.AffectedBy(externalSeeds);
            var pulseDriven = graph.AffectedBy(momentary);
            string uncertain = external.Uncertain ? string.Join("; ", external.Diagnostics) : analysis.FallbackReason;
            if (clips.Any(c => MotionAnalysis.Source(c) == null || MotionAnalysis.Source(c).events.Length != 0))
                uncertain ??= "animation events or unknown clips may access inputs";
            foreach (var parameter in declared)
            {
                string name = parameter.name;
                if (preserved.Contains(name)) result.Protected[name] = "Preserved";
                else if (AnimatorReachability.BuiltIns.Contains(name)) result.Protected[name] = "BuiltIn";
                else if (pulseDriven.Contains(name)) result.Protected[name] = "MomentaryDependency";
                else if (uncertain != null) result.Protected[name] = "Uncertain input access: " + uncertain;
                else result.Candidates[name] = externallyDriven.Contains(name) ? MultiplexPriority.ExternalInput :
                    runtime.Contains(name) ? MultiplexPriority.RuntimeDriven : menuInputs.Contains(name) ? MultiplexPriority.PersistentMenu :
                    computed.Contains(name) ? MultiplexPriority.InternalComputed : MultiplexPriority.ExternalInput;
            }
            return result;
        }

        internal MultiplexEncoding Plan(Parameter[] parameters, int budget, float hold, ISet<string> occupied,
            ICollection<string> messages, ICollection<string> warnings)
        {
            if (MultiplexEncoding.Cost(parameters) <= budget)
                return MultiplexEncoding.Plan(parameters, new HashSet<string>(), budget, hold, occupied);
            var selected = new HashSet<string>(StringComparer.Ordinal);
            var ranked = parameters.Where(p => p.networkSynced && Candidates.ContainsKey(p.name))
                .GroupBy(p => Candidates[p.name]).OrderBy(g => g.Key).ToArray();
            foreach (var group in ranked)
            {
                selected.UnionWith(group.Select(p => p.name));
                if (!MultiplexEncoding.CanFit(parameters, selected, budget)) continue;
                int shortest = 2;
                while (!MultiplexEncoding.CanFit(parameters, selected, budget, shortest)) shortest++;
                // Trim the boundary tier without selecting any later priority tier.
                // First preserve its shortest feasible cycle: minimizing only the
                // number of selected inputs can leave one slot and a very long cycle.
                // Cheap inputs are removed first to retain more native logical inputs.
                // The subset is inclusion-minimal at this cycle length, not globally.
                foreach (var parameter in group.OrderBy(p => MultiplexEncoding.Cost(p.valueType)).ThenBy(p => p.name, StringComparer.Ordinal))
                {
                    selected.Remove(parameter.name);
                    if (!MultiplexEncoding.CanFit(parameters, selected, budget, shortest)) selected.Add(parameter.name);
                }
                var plan = MultiplexEncoding.Plan(parameters, selected, budget, hold, occupied);
                messages.Add("Priority multiplexing reached " + group.Key + "; selected " + plan.Inputs.Count +
                    " inputs. Later priorities stay native. Native means no additional time-multiplexing delay.");
                foreach (var parameter in parameters.Where(p => p.networkSynced).OrderBy(p => p.name, StringComparer.Ordinal))
                    messages.Add("Priority multiplexing " + (selected.Contains(parameter.name) ? "selected: " : "kept native: ") + parameter.name +
                        " (" + (Protected.TryGetValue(parameter.name, out var reason) ? reason :
                            Candidates.TryGetValue(parameter.name, out var priority) ? priority.ToString() : "Recorded or explicitly encoded wire") + ")");
                int externalCount = plan.Inputs.Count(p => Candidates[p.Name] == MultiplexPriority.ExternalInput);
                if (externalCount != 0) warnings.Add("Priority multiplexing includes " + externalCount +
                    " inferred external inputs to meet the budget. Their remote updates now wait for complete snapshots; inspect the selected-input report.");
                return plan;
            }
            string summary = string.Join("; ", parameters.Where(p => p.networkSynced).GroupBy(p =>
                    Protected.TryGetValue(p.name, out var reason) ? reason : Candidates.TryGetValue(p.name, out var priority) ? priority.ToString() : "Native wire")
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key + "=" + g.Sum(p => MultiplexEncoding.Cost(p.valueType)) + " bits"));
            messages.Add("Priority multiplexing cannot meet the budget: " + summary);
            throw new InvalidOperationException("Cannot fit the synchronized parameter budget without overriding protected or momentary inputs. " + summary);
        }
    }
}
