using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Materials
{
    internal static partial class SharedShaderConstants
    {
        private sealed class ShaderUsers
        {
            internal readonly HashSet<Material> Materials = new();
            internal Dictionary<string, float> Constants = new(StringComparer.Ordinal);
            internal Dictionary<string, float[]> Domains = new(StringComparer.Ordinal);
            internal HashSet<string> CandidateNames;
            internal bool CompilerFolding;
            internal int InitialChanges;
        }

        private sealed class ConstantDemand
        {
            internal bool All;
            internal readonly HashSet<string> Names = new(StringComparer.Ordinal);
            internal void Include(HashSet<string> names)
            {
                if (names == null) All = true;
                else if (!All) Names.UnionWith(names);
            }
            internal bool Contains(string name) => All || Names.Contains(name);
        }

        private static Dictionary<Shader, Shader> PlanDependencies(AvatarAnimationIndex index, UseSet uses,
            BuildTarget target, ShaderPropertyUsage sources, Func<Renderer, bool> allowInitialChanges, IList<string> diagnostics,
            Func<Renderer, bool> allowCompilerConstantFolding)
        {
            var closure = new Dictionary<Shader, HashSet<Shader>>();
            var dependencies = new Dictionary<Shader, Shader[]>();
            var users = new Dictionary<Shader, ShaderUsers>();
            var blocked = new Dictionary<Shader, string>(uses.Blocked);
            var order = new List<Shader>();
            try
            {
                var pending = new Queue<Shader>(uses.ReferencedShaders);
                while (pending.Count != 0)
                {
                    var shader = pending.Dequeue(); if (closure.ContainsKey(shader)) continue;
                    var native = ShaderBuildSource.Dependencies(shader); closure.Add(shader, native);
                    foreach (var child in native) if (child != shader) pending.Enqueue(child);
                }
                foreach (var pair in closure)
                {
                    try
                    {
                        if (!ShaderBuildSource.TryRead(pair.Key, out string source, out _))
                            throw new InvalidOperationException("editable ShaderLab source is unavailable");
                        var layout = ShaderBuildSource.Layout(pair.Key, source);
                        var children = layout.Dependencies.Select(Shader.Find).Distinct().ToArray();
                        if (children.Any(s => s == null || !pair.Value.Contains(s)))
                            throw new InvalidOperationException("source and native shader dependencies disagree");
                        var declared = new HashSet<Shader> { pair.Key };
                        foreach (var child in children) declared.UnionWith(closure[child]);
                        if (!declared.SetEquals(pair.Value))
                            throw new InvalidOperationException("native shader dependencies lack a relocatable source reference");
                        dependencies.Add(pair.Key, children);
                    }
                    catch (Exception exception) { blocked.TryAdd(pair.Key, exception.Message); }
                }
                foreach (var pair in uses.Materials)
                    if (pair.Key.shader != null)
                        foreach (var shader in closure[pair.Key.shader])
                        {
                            if (!users.TryGetValue(shader, out var set)) users.Add(shader, set = new());
                            set.Materials.Add(pair.Key);
                        }
                // A preserved or uneditable parent keeps every original shader
                // it imports. Do not create a second specialized copy of those
                // dependencies; other disjoint shader graphs remain eligible.
                foreach (var pair in blocked.ToArray())
                    foreach (var shader in closure[pair.Key]) blocked.TryAdd(shader, "a retained shader depends on the original: " + pair.Key.name);
                var visited = new HashSet<Shader>(); var active = new HashSet<Shader>();
                void Visit(Shader shader)
                {
                    if (visited.Contains(shader)) return;
                    if (!active.Add(shader)) throw new InvalidOperationException("cyclic shader dependency graph");
                    if (dependencies.TryGetValue(shader, out var children)) foreach (var child in children) Visit(child);
                    active.Remove(shader); visited.Add(shader); order.Add(shader);
                }
                foreach (var shader in uses.ReferencedShaders) Visit(shader);
                // Gather every consumer's demand before collecting shared material
                // values. A dependency can need inputs absent from its wrappers;
                // caching only the first consumer's subset would lose that proof.
                var demands = new Dictionary<Material, ConstantDemand>();
                foreach (var pair in users)
                {
                    if (blocked.ContainsKey(pair.Key)) continue;
                    var set = pair.Value;
                    set.CompilerFolding = allowCompilerConstantFolding != null && set.Materials.All(m =>
                        uses.Materials[m].All(allowCompilerConstantFolding));
                    // Strict native equivalence may need other uniform domains
                    // as premises. Preserve its complete input collection.
                    if (set.CompilerFolding && ShaderBuildSource.TryRemainingConstants(pair.Key, out var names))
                        set.CandidateNames = new HashSet<string>(names, StringComparer.Ordinal);
                    foreach (var material in set.Materials)
                    {
                        if (!demands.TryGetValue(material, out var demand)) demands.Add(material, demand = new());
                        demand.Include(set.CandidateNames);
                    }
                }
                var values = new Dictionary<(Renderer, Material), (Dictionary<string, float> Constants, HashSet<string> Initial)>();
                var inputs = new InputCollection(index, allowInitialChanges);
                foreach (var pair in users)
                    if (!blocked.ContainsKey(pair.Key) && pair.Value.CandidateNames?.Count != 0)
                        ComputeSharedValues(inputs, uses, pair.Value, values, demands);
            }
            catch (Exception exception)
            { diagnostics?.Add("Shared shader constants retained: incomplete dependency ownership: " + exception.Message); return new(); }

            var replacements = new Dictionary<Shader, Shader>();
            try
            {
                // If a caller cannot be rewritten, retain its dependencies and
                // retry the remaining graph. The blocked set grows monotonically;
                // successful immutable source proofs are reused from the cache.
                while (true)
                {
                    bool retry = false; var messages = new List<string>();
                    foreach (var shader in order)
                    {
                        if (blocked.TryGetValue(shader, out string block))
                        { if (users.ContainsKey(shader)) messages.Add(shader.name + ": shared shader constants retained: " + block); continue; }
                        if (!users.TryGetValue(shader, out var set)) continue;
                        var imported = dependencies[shader].Where(replacements.ContainsKey).ToDictionary(s => s, s => replacements[s]);
                        if (set.Constants.Count == 0 && imported.Count == 0) continue;
                        var statistics = new SharedShaderConstantSource.Statistics();
                        if (!SharedShaderConstantSource.TryCreate(shader, set.Constants, target, sources,
                            out var output, out _, out int declarations, out string reason, statistics, set.Domains, imported,
                            allowCompilerConstantFolding: set.CompilerFolding))
                        {
                            messages.Add(shader.name + ": shared shader constants retained: " + reason + "; " + statistics.Detail + ".");
                            foreach (var dependency in imported.Keys)
                                foreach (var child in closure[dependency])
                                    if (blocked.TryAdd(child, "caller cannot preserve dependency replacement: " + shader.name + ": " + reason)) retry = true;
                            if (retry) diagnostics?.Add(shader.name + ": dependency relocation retained: " + reason + "; " + statistics.Detail + ".");
                            if (retry) break;
                            continue;
                        }
                        replacements.Add(shader, output);
                        messages.Add(shader.name + ": one shared shader replacement; " + declarations +
                            " constant declarations across " + set.Materials.Count + " materials; " + imported.Count +
                            " relocated dependencies; initial-value policy used for " + set.InitialChanges +
                            " renderer properties (literal or value-domain proof); " + statistics.Detail + ".");
                    }
                    if (!retry)
                    {
                        var finalClosure = new HashSet<Shader>();
                        foreach (var original in uses.ReferencedShaders)
                            finalClosure.UnionWith(ShaderBuildSource.Dependencies(
                                replacements.TryGetValue(original, out var output) ? output : original));
                        foreach (var original in replacements.Keys)
                            if (finalClosure.Contains(original))
                                foreach (var shader in closure[original])
                                    if (blocked.TryAdd(shader, "an imported shader still retains the original")) retry = true;
                        if (!retry)
                        {
                            if (finalClosure.Count > closure.Count) throw new InvalidOperationException("shared shader dependency count increased");
                            foreach (string message in messages) diagnostics?.Add(message);
                            return replacements;
                        }
                    }
                    foreach (var shader in replacements.Values.Reverse()) UnityEngine.Object.DestroyImmediate(shader);
                    replacements.Clear();
                }
            }
            catch (Exception exception)
            {
                foreach (var shader in replacements.Values.Reverse()) if (shader != null) UnityEngine.Object.DestroyImmediate(shader);
                diagnostics?.Add("Shared shader constants retained: dependency replacement validation failed: " + exception.Message);
                return new();
            }
        }

        private static void ComputeSharedValues(InputCollection inputs, UseSet uses, ShaderUsers set,
            Dictionary<(Renderer, Material), (Dictionary<string, float> Constants, HashSet<string> Initial)> captured,
            Dictionary<Material, ConstantDemand> demands)
        {
            Dictionary<string, float> intersection = null;
            Dictionary<string, Dictionary<int, float>> domains = null;
            var relaxed = new HashSet<(Renderer Renderer, string Property)>();
            foreach (var material in set.Materials)
            foreach (var renderer in uses.Materials[material])
            {
                if (!captured.TryGetValue((renderer, material), out var facts))
                {
                    var found = inputs.Read(renderer, material, demands[material], out var changed);
                    captured.Add((renderer, material), facts = (found, changed));
                }
                var values = facts.Constants;
                var initialChanges = facts.Initial;
                // Different immutable values remain uniforms. Complete exact
                // value sets can still support the native arithmetic proof.
                if (domains == null) domains = values.Where(p => MotionAnalysis.Finite(p.Value)).ToDictionary(p => p.Key,
                    p => new Dictionary<int, float> { [BitConverter.SingleToInt32Bits(p.Value)] = p.Value }, StringComparer.Ordinal);
                else foreach (string name in domains.Keys.ToArray())
                {
                    if (!values.TryGetValue(name, out float value) || !MotionAnalysis.Finite(value)) { domains.Remove(name); continue; }
                    int bits = BitConverter.SingleToInt32Bits(value); var domain = domains[name];
                    if (!domain.ContainsKey(bits) && domain.Count >= MaximumUniformDomainValues) domains.Remove(name);
                    else domain[bits] = value;
                }
                foreach (string name in initialChanges) relaxed.Add((renderer, name));
                if (intersection == null) intersection = new Dictionary<string, float>(values, StringComparer.Ordinal);
                else foreach (string name in intersection.Keys.ToArray())
                    if (!values.TryGetValue(name, out float value) || BitConverter.SingleToInt32Bits(value) != BitConverter.SingleToInt32Bits(intersection[name]))
                        intersection.Remove(name);
            }
            set.Constants = intersection ?? new(StringComparer.Ordinal);
            if (set.CandidateNames != null)
                foreach (string name in set.Constants.Keys.ToArray()) if (!set.CandidateNames.Contains(name)) set.Constants.Remove(name);
            set.Domains = domains?.ToDictionary(p => p.Key, p => p.Value.OrderBy(v => v.Key).Select(v => v.Value).ToArray(), StringComparer.Ordinal)
                ?? new(StringComparer.Ordinal);
            set.InitialChanges = relaxed.Count(p => set.Constants.ContainsKey(p.Property) || set.Domains.ContainsKey(p.Property));
        }
    }
}
