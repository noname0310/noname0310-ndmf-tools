using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.AnimatorOptimization
{
    internal static class MaterialConstants
    {
        private enum Storage { Scalar, Vector, Color, TextureTransform }

        private sealed class Change
        {
            internal Renderer Renderer;
            internal string Property;
            internal Storage Storage;
            internal Vector4 Value;
            internal bool ChangesInitial;
            internal AvatarAnimationIndex.BindingUse[] Writers;
        }

        internal static void Optimize(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths, Action<Material> save, AnimatorOptimizationReport report,
            AnimatorReachability analysis = null, Func<Renderer, bool> allowInitialChanges = null,
            Func<Renderer, bool> preservedRenderer = null, Func<Material, bool> preservedMaterial = null,
            IEnumerable<string> preservedInputs = null)
        {
            using var cost = AnalysisCost.Measure("Constant output consumer/material properties");
            if (ObjectOptimizationSafety.UnsupportedRuntime(avatar) != null) return;
            var index = AvatarAnimationIndex.Create(avatar, controllers, paths, preservedInputs);
            if (index.UnsafeReason != null) return;
            var constants = new ConstantPropertyAnalysis(index, analysis);
            var changes = new List<Change>();
            // Native material writers remain authoritative even when their output is
            // absent from every animation clip. Resolve them once per snapshot.
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is MeshRenderer || renderer is SkinnedMeshRenderer) || renderer.HasPropertyBlock() ||
                    preservedRenderer?.Invoke(renderer) == true) continue;
                var materials = renderer.sharedMaterials;
                if (materials.Length == 0 || materials.Any(m => m == null || preservedMaterial?.Invoke(m) == true)) continue;
                var uses = index.ForObject(renderer);
                var native = ContactOutputs.Writers(avatar, renderer, index).SelectMany(ContactOutputs.Properties).ToHashSet(StringComparer.Ordinal);
                // Dynamic resource changes need their own whole-use relocation proof.
                // Constant swaps have already been resolved by ConstantMaterialSlots.
                if (uses.Any(u => u.Binding.isPPtrCurve && u.Binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal))) continue;
                foreach (var group in uses.Where(u => u.Binding.propertyName.StartsWith("material.", StringComparison.Ordinal))
                    .GroupBy(u => MaterialAnimationInputs.PropertyName(u.Binding.propertyName)))
                {
                    string name = group.Key;
                    if (name == null || native.Contains(name)) continue;
                    if (!Read(materials[0], name, out var storage, out var initial)) continue;
                    var defaults = new Vector4[materials.Length]; bool supported = true;
                    for (int i = 0; i < materials.Length; i++)
                        if (!Read(materials[i], name, out var other, out defaults[i]) || other != storage) supported = false;
                    if (!supported) continue;
                    string channels = storage == Storage.Scalar ? "" : storage == Storage.Color ? "rgba" : "xyzw";
                    var fields = group.GroupBy(u => u.Binding.propertyName).ToDictionary(g => g.Key, g => g.ToArray());
                    if (fields.Count != (channels.Length == 0 ? 1 : 4)) continue;
                    var value = Vector4.zero; HashSet<VirtualClip> footprint = null;
                    for (int channel = 0; channel < Math.Max(1, channels.Length); channel++)
                    {
                        string binding = "material." + name + (channels.Length == 0 ? "" : "." + channels[channel]);
                        if (!fields.TryGetValue(binding, out var writers)) { supported = false; break; }
                        var clips = new HashSet<VirtualClip>(writers.Select(w => w.Clip));
                        // Unity writes vectors/colors as whole property-block entries.
                        // A sibling written in another clip can overwrite this channel
                        // implicitly. Require identical complete channel coverage.
                        if (footprint != null && !footprint.SetEquals(clips)) { supported = false; break; }
                        footprint = clips;
                        if (constants.TryProve(renderer, binding, ConstantOutputValue.Scalar(initial[channel]), out var proof, out _))
                            value[channel] = proof.Value.Number;
                        else if (defaults.All(d => ConstantOutputValue.Scalar(d[channel]).Equals(ConstantOutputValue.Scalar(initial[channel]))) &&
                            constants.MatchesInitialValue(renderer, writers, initial[channel], allowInterpolation: true))
                            value[channel] = initial[channel];
                        else { supported = false; break; }
                    }
                    if (!supported) continue;
                    bool changed = defaults.Any(d => Enumerable.Range(0, 4).Any(channel =>
                        !ConstantOutputValue.Scalar(d[channel]).Equals(ConstantOutputValue.Scalar(value[channel]))));
                    // The optional callback retains the established direct-call
                    // contract; the build pass always supplies captured policy.
                    if (changed && (allowInitialChanges?.Invoke(renderer) == false || group.Any(u => u.PreservedInput))) continue;
                    changes.Add(new Change { Renderer = renderer, Property = name, Storage = storage, Value = value,
                        ChangesInitial = changed, Writers = group.ToArray() });
                }
            }
            var removals = new Dictionary<VirtualClip, HashSet<EditorCurveBinding>>();
            foreach (var group in changes.GroupBy(c => c.Renderer))
            {
                if (group.Any(c => c.ChangesInitial))
                {
                    var copies = new Dictionary<Material, Material>();
                    var materials = group.Key.sharedMaterials;
                    for (int i = 0; i < materials.Length; i++)
                    {
                        if (!copies.TryGetValue(materials[i], out var copy))
                        {
                            copy = UnityEngine.Object.Instantiate(materials[i]); copy.name = materials[i].name + " (constant outputs)";
                            copies.Add(materials[i], copy);
                            foreach (var change in group) Write(copy, change);
                            save(copy);
                        }
                        materials[i] = copy;
                    }
                    group.Key.sharedMaterials = materials;
                }
                foreach (var change in group)
                {
                    if (change.ChangesInitial) report.InitialOutputs++;
                    foreach (var writer in change.Writers)
                    {
                        if (!removals.TryGetValue(writer.Clip, out var bindings)) removals.Add(writer.Clip, bindings = new());
                        bindings.Add(writer.Binding);
                    }
                }
            }
            foreach (var pair in removals)
            { report.MaterialCurves += pair.Value.Count; MotionAnalysis.RemoveCurves(pair.Key, pair.Value); }
            foreach (var change in changes) report.ConstantOutputProofs.Record(initializationChanged: change.ChangesInitial);
        }

        private static bool Read(Material material, string name, out Storage storage, out Vector4 value)
        {
            storage = default; value = default;
            if (material.shader == null) return false;
            int property = material.shader.FindPropertyIndex(name);
            if (property < 0)
            {
                if (!name.EndsWith("_ST", StringComparison.Ordinal)) return false;
                string texture = name.Substring(0, name.Length - 3);
                property = material.shader.FindPropertyIndex(texture);
                if (property < 0 || material.shader.GetPropertyType(property) != ShaderPropertyType.Texture) return false;
                if ((material.shader.GetPropertyFlags(property) & ShaderPropertyFlags.PerRendererData) != 0) return false;
                var scale = material.GetTextureScale(texture); var offset = material.GetTextureOffset(texture);
                storage = Storage.TextureTransform; value = new Vector4(scale.x, scale.y, offset.x, offset.y);
            }
            else
            {
                var flags = material.shader.GetPropertyFlags(property);
                if ((flags & (ShaderPropertyFlags.PerRendererData | ShaderPropertyFlags.Gamma)) != 0) return false;
                switch (material.shader.GetPropertyType(property))
                {
                    case ShaderPropertyType.Float: case ShaderPropertyType.Range:
                        storage = Storage.Scalar; value.x = material.GetFloat(name); break;
                    case ShaderPropertyType.Vector: storage = Storage.Vector; value = material.GetVector(name); break;
                    case ShaderPropertyType.Color: storage = Storage.Color; value = material.GetColor(name); break;
                    default: return false;
                }
            }
            for (int i = 0; i < 4; i++) if (!MotionAnalysis.Finite(value[i])) return false;
            return true;
        }

        private static void Write(Material material, Change change)
        {
            switch (change.Storage)
            {
                case Storage.Scalar: material.SetFloat(change.Property, change.Value.x); break;
                case Storage.Vector: material.SetVector(change.Property, change.Value); break;
                case Storage.Color: material.SetColor(change.Property, change.Value); break;
                case Storage.TextureTransform:
                    string texture = change.Property.Substring(0, change.Property.Length - 3);
                    material.SetTextureScale(texture, new Vector2(change.Value.x, change.Value.y));
                    material.SetTextureOffset(texture, new Vector2(change.Value.z, change.Value.w)); break;
            }
        }
    }
}
