using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.AnimatorOptimization
{
    [Serializable]
    public sealed class MaterialOutputOptimizationReport
    {
        public int RemovedCurves, ShaderSourcesRead;
        public int RemovedParameterCurves, RemovedDriverOperations, RemovedBranches;
        public long Milliseconds;
        public string SkippedReason;
        public List<string> RemovedProperties = new();
        public List<string> RetainedShaders = new();
    }

    internal sealed class MaterialBindingUsage
    {
        private readonly Transform avatar;
        private readonly IReadOnlyDictionary<string, string> paths;
        private readonly Dictionary<VirtualClip, HashSet<Transform>> roots = new();
        private readonly Dictionary<(VirtualClip, EditorCurveBinding, bool), Renderer[]> targets = new();
        private readonly Dictionary<Renderer, HashSet<Material>> swaps = new();
        private readonly HashSet<Renderer> unknownSwaps = new();
        private readonly Dictionary<(Renderer, string), bool> unusedProperties = new();
        internal readonly ShaderPropertyUsage Shaders;
        private readonly VrcFallbackPropertyContract fallbacks = new();

        internal MaterialBindingUsage(GameObject avatar, IDictionary<object, VirtualAnimatorController> entries,
            IReadOnlyDictionary<string, string> paths, ShaderPropertyUsage shaders = null)
        {
            Shaders = shaders ?? new ShaderPropertyUsage();
            this.avatar = avatar.transform; this.paths = paths;
            foreach (var entry in entries)
                foreach (var clip in entry.Value.AllReachableNodes().OfType<VirtualClip>())
                {
                    if (!roots.TryGetValue(clip, out var owners)) roots.Add(clip, owners = new());
                    owners.Add(entry.Key is Animator animator ? animator.transform : avatar.transform);
                }
            foreach (var clip in roots.Keys)
                foreach (var binding in clip.GetObjectCurveBindings().Where(b => b.type != null && typeof(Renderer).IsAssignableFrom(b.type) &&
                    (b.propertyName ?? "").StartsWith("m_Materials", StringComparison.Ordinal)))
                {
                    // A shared swap clip may have a valid target under one owner and no target
                    // under another. Keep all valid consumers instead of dropping the whole swap.
                    var renderers = Resolve(clip, binding, requireEveryOwner: false);
                    if (renderers == null) continue;
                    var keys = clip.GetObjectCurve(binding);
                    foreach (var renderer in renderers)
                    {
                        if (keys == null || !binding.propertyName.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal) ||
                            keys.Any(k => k.value != null && !(k.value is Material))) { unknownSwaps.Add(renderer); continue; }
                        if (!swaps.TryGetValue(renderer, out var values)) swaps.Add(renderer, values = new());
                        values.UnionWith(keys.Select(k => k.value).OfType<Material>());
                    }
                }
        }

        private Renderer[] Resolve(VirtualClip clip, EditorCurveBinding binding, bool requireEveryOwner = true)
        {
            var key = (clip, binding, requireEveryOwner);
            if (targets.TryGetValue(key, out var cached)) return cached;
            if (!roots.TryGetValue(clip, out var owners)) return null;
            var renderers = new HashSet<Renderer>();
            foreach (var owner in owners)
            {
                if (owner == null) { if (requireEveryOwner) return targets[key] = null; continue; }
                var resolved = AnimationUsage.Resolve(avatar, owner, binding.path, paths).ToArray();
                if (resolved.Length == 0 && requireEveryOwner) return targets[key] = null;
                foreach (var target in resolved)
                {
                    var values = target.GetComponents(binding.type).OfType<Renderer>().ToArray();
                    if (requireEveryOwner && (values.Length == 0 || values.Any(r => !(r is SkinnedMeshRenderer) && !(r is MeshRenderer))))
                        return targets[key] = null;
                    renderers.UnionWith(values);
                }
            }
            return targets[key] = renderers.ToArray();
        }

        internal static string Property(EditorCurveBinding binding)
        {
            if (binding.type == null || !typeof(Renderer).IsAssignableFrom(binding.type) || binding.isDiscreteCurve ||
                !(binding.propertyName ?? "").StartsWith("material.", StringComparison.Ordinal)) return null;
            string property = binding.propertyName.Substring("material.".Length);
            int dot = property.IndexOf('.');
            if (dot >= 0)
            {
                if (dot != property.Length - 2 || "rgbaxyzw".IndexOf(property[property.Length - 1]) < 0) return null;
                property = property.Substring(0, dot);
            }
            return property;
        }

        internal bool Unused(VirtualClip clip, EditorCurveBinding binding, MaterialOutputOptimizationReport report = null)
        {
            string property = Property(binding);
            if (string.IsNullOrEmpty(property)) return false;
            var renderers = Resolve(clip, binding);
            // Missing early-build targets could still be generated by a later plugin.
            if (renderers == null || renderers.Length == 0) return false;
            foreach (var renderer in renderers)
                if (!Unused(renderer, property, report)) return false;
            return true;
        }

        private bool Unused(Renderer renderer, string property, MaterialOutputOptimizationReport report)
        {
            if (unusedProperties.TryGetValue((renderer, property), out bool cached)) return cached;
            bool Check()
            {
                if (unknownSwaps.Contains(renderer)) return false;
                var materials = renderer.sharedMaterials.Where(m => m != null)
                    .Concat(swaps.TryGetValue(renderer, out var other) ? other : Enumerable.Empty<Material>()).Distinct().ToArray();
                if (materials.Length == 0) return false;
                foreach (var material in materials)
                {
                    if (fallbacks.MayUse(material, property)) return false;
                    var proof = Shaders.Get(material.shader);
                    if (proof.Uncertain != null && report != null)
                    {
                        string reason = (material.shader != null ? material.shader.name : "Missing shader") + ": " + proof.Uncertain;
                        if (!report.RetainedShaders.Contains(reason)) report.RetainedShaders.Add(reason);
                    }
                    if (proof.MayUse(property)) return false;
                }
                return true;
            }
            return unusedProperties[(renderer, property)] = Check();
        }
    }

    internal static class MaterialOutputPruning
    {
        internal static void Apply(VirtualAnimatorController[] controllers, MaterialBindingUsage usage,
            MaterialOutputOptimizationReport report)
        {
            var watch = Stopwatch.StartNew();
            foreach (var clip in controllers.SelectMany(c => c.AllReachableNodes()).OfType<VirtualClip>().Distinct())
            {
                var source = MotionAnalysis.Source(clip);
                if (clip.IsMarkerClip || source == null || source.humanMotion || source.events.Length > 0) continue;
                // Preserved inputs stay dynamic, but cannot make a shader consume
                // an absent property. Prove each output independently and retain
                // the clip's other bindings, computation inputs and timing.
                // Group by the complete property. Keep Color/Vector channels together even when
                // only one channel is present; never bake the omitted components to zero.
                var removed = new List<EditorCurveBinding>();
                foreach (var group in clip.GetFloatCurveBindings().Where(b => MaterialBindingUsage.Property(b) != null)
                    .GroupBy(b => (b.path, b.type, Property: MaterialBindingUsage.Property(b))))
                    if (group.All(b => usage.Unused(clip, b, report)))
                    {
                        removed.AddRange(group);
                        report.RemovedProperties.Add(clip.Name + " | " + group.Key.path + " | " + group.Key.Property);
                    }
                report.RemovedCurves += removed.Count;
                if (removed.Count > 0) MotionAnalysis.RemoveCurves(clip, removed);
            }
            report.ShaderSourcesRead = usage.Shaders.FilesRead;
            report.Milliseconds = watch.ElapsedMilliseconds;
        }
    }
}
