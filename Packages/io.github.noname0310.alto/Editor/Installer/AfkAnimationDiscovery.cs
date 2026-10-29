using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ParameterPolicy;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Noname.AvatarTools.Installer
{
    [Serializable]
    internal sealed class AfkClipChoice
    {
        public AnimationClip Clip;
        public bool Selected;
        public bool SharedOutsideAfk;
        public string Evidence;
    }

    internal sealed class AfkDiscoveryResult
    {
        internal readonly List<AfkClipChoice> Candidates = new();
        internal readonly List<string> Diagnostics = new();
        internal int ControllerCount;
    }

    // A read-only authoring graph. No controller, clip, state or behaviour is cloned or modified.
    // Compare reachability with AFK variable vs AFK always false; follow the whole AFK sequence,
    // including exit clips, while other parameter values conservatively remain unknown.
    internal static class AfkAnimationDiscovery
    {
        private sealed class Node { internal readonly List<Edge> Edges = new(); }
        private sealed class Edge { internal Node Target; internal AnimatorCondition[] Conditions; }
        private sealed class Machine
        {
            internal AnimatorStateMachine Source;
            internal Machine Parent;
            internal readonly Node Entry = new(), Active = new(), Exit = new();
        }
        private sealed class Graph
        {
            internal readonly Dictionary<AnimatorState, Node> States = new();
            private readonly Dictionary<AnimatorStateMachine, Machine> machines = new();
            private readonly Dictionary<AnimatorState, Machine> owners = new();
            private Machine root;

            internal Graph(AnimatorStateMachine source)
            {
                root = Index(source, null);
                foreach (var machine in machines.Values)
                {
                    Link(machine.Entry, machine.Active);
                    if (machine.Parent != null) Link(machine.Active, machine.Parent.Active);
                    if (machine.Source.defaultState != null && States.TryGetValue(machine.Source.defaultState, out var initial))
                        Link(machine.Entry, initial);
                    else
                    {
                        foreach (var state in machine.Source.states) if (state.state != null) Link(machine.Entry, States[state.state]);
                        foreach (var child in machine.Source.stateMachines) if (child.stateMachine != null) Link(machine.Entry, machines[child.stateMachine].Entry);
                    }
                    Connect(machine.Entry, machine.Source.entryTransitions, machine);
                    Connect(machine.Active, machine.Source.anyStateTransitions, machine);
                    if (machine.Parent == null) Link(machine.Exit, machine.Entry);
                    else
                    {
                        Connect(machine.Exit, machine.Parent.Source.GetStateMachineTransitions(machine.Source), machine.Parent);
                        // Include Unity's enclosing fallback/re-entry possibilities rather than claiming exclusivity.
                        Link(machine.Exit, machine.Parent.Entry); Link(machine.Exit, machine.Parent.Exit);
                    }
                }
                foreach (var pair in States)
                {
                    Link(pair.Value, owners[pair.Key].Active);
                    Connect(pair.Value, pair.Key.transitions, owners[pair.Key]);
                }
            }

            private Machine Index(AnimatorStateMachine source, Machine parent)
            {
                if (source == null || machines.ContainsKey(source)) throw new InvalidOperationException("Missing, shared or cyclic state machine.");
                var machine = new Machine { Source = source, Parent = parent }; machines.Add(source, machine);
                foreach (var item in source.states)
                    if (item.state != null)
                    {
                        if (States.ContainsKey(item.state)) throw new InvalidOperationException("A state has multiple owners.");
                        States.Add(item.state, new Node()); owners.Add(item.state, machine);
                    }
                foreach (var item in source.stateMachines) if (item.stateMachine != null) Index(item.stateMachine, machine);
                return machine;
            }

            private static void Link(Node from, Node to, AnimatorCondition[] conditions = null) =>
                from.Edges.Add(new Edge { Target = to, Conditions = conditions ?? Array.Empty<AnimatorCondition>() });

            private void Connect(Node from, IEnumerable<AnimatorTransitionBase> transitions, Machine owner)
            {
                var values = transitions.Where(t => t != null).ToArray(); bool solo = values.Any(t => t.solo);
                foreach (var transition in values.Where(t => !t.mute && (!solo || t.solo)))
                {
                    Node target;
                    if (transition.isExit) target = owner.Exit;
                    else if (transition.destinationState != null && States.TryGetValue(transition.destinationState, out var state)) target = state;
                    else if (transition.destinationStateMachine != null && machines.TryGetValue(transition.destinationStateMachine, out var machine)) target = machine.Entry;
                    else throw new InvalidOperationException("A transition has an unsupported destination.");
                    Link(from, target, transition.conditions);
                }
            }

            internal HashSet<AnimatorState> Reachable(bool fixedOff)
            {
                var visited = new HashSet<Node>(); var pending = new Queue<Node>(); pending.Enqueue(root.Entry);
                var afk = fixedOff ? ParameterValues.Constant(0) : ParameterValues.Constant(0).Union(ParameterValues.Constant(1));
                while (pending.Count > 0)
                {
                    var node = pending.Dequeue(); if (!visited.Add(node)) continue;
                    foreach (var edge in node.Edges)
                        if (edge.Conditions.GroupBy(c => c.parameter).All(g => (g.Key == "AFK" ? afk : ParameterValues.Unknown).CanSatisfy(g)))
                            pending.Enqueue(edge.Target);
                }
                return new HashSet<AnimatorState>(States.Where(p => visited.Contains(p.Value)).Select(p => p.Key));
            }
        }

        internal static AfkDiscoveryResult Find(GameObject avatar) => Find(ParameterSourceResolver.AvatarControllers(avatar));

        internal static AfkDiscoveryResult Find(IEnumerable<RuntimeAnimatorController> sources)
        {
            var result = new AfkDiscoveryResult();
            var evidence = new Dictionary<AnimationClip, HashSet<string>>();
            var normalClips = new HashSet<AnimationClip>();
            foreach (var source in sources.Where(c => c != null).Distinct())
            {
                result.ControllerCount++;
                var overrides = new List<Dictionary<AnimationClip, AnimationClip>>();
                var visited = new HashSet<RuntimeAnimatorController>();
                var current = source;
                while (current is AnimatorOverrideController wrapper && visited.Add(current))
                {
                    var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>(); wrapper.GetOverrides(pairs);
                    overrides.Add(pairs.Where(p => p.Key != null && p.Value != null).ToDictionary(p => p.Key, p => p.Value));
                    current = wrapper.runtimeAnimatorController;
                }
                AnimationClip Resolve(AnimationClip clip)
                {
                    for (int i = overrides.Count - 1; i >= 0; i--)
                        if (overrides[i].TryGetValue(clip, out var replacement)) clip = replacement;
                    return clip;
                }
                if (!(current is AnimatorController controller))
                {
                    result.Diagnostics.Add("Unsupported controller: " + source.name);
                    normalClips.UnionWith(source.animationClips.Where(c => c != null)); continue;
                }
                var layers = controller.layers;
                foreach (var layer in layers)
                {
                    try
                    {
                        var machine = layer.syncedLayerIndex < 0 ? layer.stateMachine :
                            layer.syncedLayerIndex < layers.Length ? layers[layer.syncedLayerIndex].stateMachine : null;
                        if (machine == null) throw new InvalidOperationException("The layer has no usable state machine.");
                        var graph = new Graph(machine);
                        var normal = graph.Reachable(true); var active = graph.Reachable(false);
                        foreach (var state in graph.States.Keys)
                        {
                            var motion = layer.syncedLayerIndex < 0 ? state.motion : layer.GetOverrideMotion(state) ?? state.motion;
                            var baseline = normal.Contains(state) ? Clips(motion, true, Resolve) : new HashSet<AnimationClip>();
                            normalClips.UnionWith(baseline);
                            if (!active.Contains(state)) continue;
                            foreach (var clip in Clips(motion, false, Resolve).Except(baseline))
                            {
                                if (!evidence.TryGetValue(clip, out var paths)) evidence.Add(clip, paths = new HashSet<string>());
                                paths.Add(source.name + " / " + layer.name + " / " + state.name);
                            }
                        }
                    }
                    catch (InvalidOperationException error)
                    {
                        result.Diagnostics.Add(source.name + " / " + layer.name + ": " + error.Message);
                        // An uncertain source may share clips with an identified AFK path.
                        normalClips.UnionWith(source.animationClips.Where(c => c != null));
                    }
                }
            }
            foreach (var pair in evidence.OrderBy(p => p.Key.name, StringComparer.Ordinal).ThenBy(p => AssetDatabase.GetAssetPath(p.Key), StringComparer.Ordinal))
            {
                if (!AnimationUtility.GetCurveBindings(pair.Key).Any(b => b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))) continue;
                bool shared = normalClips.Contains(pair.Key);
                result.Candidates.Add(new AfkClipChoice
                { Clip = pair.Key, Selected = !shared, SharedOutsideAfk = shared, Evidence = string.Join("\n", pair.Value.OrderBy(v => v, StringComparer.Ordinal)) });
            }
            return result;
        }

        private static HashSet<AnimationClip> Clips(Motion root, bool fixedOff, Func<AnimationClip, AnimationClip> resolve)
        {
            var result = new HashSet<AnimationClip>(); var visited = new HashSet<Motion>(); var pending = new Stack<Motion>();
            if (root != null) pending.Push(root);
            while (pending.Count > 0)
            {
                var motion = pending.Pop(); if (motion == null || !visited.Add(motion)) continue;
                if (motion is AnimationClip clip) { result.Add(resolve(clip)); continue; }
                if (!(motion is BlendTree tree)) continue;
                IEnumerable<ChildMotion> selected = tree.children;
                if (fixedOff && tree.blendType == BlendTreeType.Direct)
                    selected = selected.Where(c => c.directBlendParameter != "AFK");
                if (tree.blendType == BlendTreeType.Simple1D && tree.blendParameter == "AFK")
                {
                    var sorted = tree.children.OrderBy(c => c.threshold).ToArray();
                    if (sorted.Length > 0 && sorted.All(c => !float.IsNaN(c.threshold) && !float.IsInfinity(c.threshold)) &&
                        sorted.Select(c => c.threshold).Distinct().Count() == sorted.Length)
                    {
                        IEnumerable<ChildMotion> At(float value)
                        {
                            if (value <= sorted[0].threshold) return sorted.Take(1);
                            if (value >= sorted[sorted.Length - 1].threshold) return sorted.Skip(sorted.Length - 1);
                            int upper = Array.FindIndex(sorted, c => c.threshold > value);
                            return sorted[upper - 1].threshold == value ? sorted.Skip(upper - 1).Take(1) : sorted.Skip(upper - 1).Take(2);
                        }
                        // The SDK's AFK input is Boolean even if a controller declares it as Float.
                        selected = fixedOff ? At(0) : At(0).Concat(At(1));
                    }
                }
                foreach (var child in selected) if (child.motion != null) pending.Push(child.motion);
            }
            return result;
        }

        internal static bool SameCandidates(IReadOnlyList<AfkClipChoice> first, IReadOnlyList<AfkClipChoice> second) =>
            first.Count == second.Count && first.All(a => second.Any(b => a.Clip == b.Clip && a.SharedOutsideAfk == b.SharedOutsideAfk));
    }
}
