using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.AnimatorOptimization
{
    internal static class MaterialConstants
    {
        private sealed class Use
        {
            internal object Owner;
            internal VirtualAnimatorController Controller;
            internal VirtualLayer Layer;
            internal VirtualClip Clip;
            internal EditorCurveBinding Binding;
            internal Renderer Renderer;
        }

        internal static void Optimize(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths, Action<Material> save, AnimatorOptimizationReport report,
            AnimatorReachability analysis = null)
        {
            analysis ??= AnimatorReachability.Analyze(avatar, controllers.Values);
            if (analysis.FallbackReason != null) return;
            var uses = new List<Use>();
            var swaps = new HashSet<Renderer>();
            var ambiguous = new HashSet<VirtualClip>();
            var roots = new Dictionary<VirtualClip, Transform>();
            var bindingCache = new Dictionary<VirtualMotion, HashSet<EditorCurveBinding>>();
            var stateCache = new Dictionary<VirtualLayer, VirtualState[]>();
            Renderer Resolve(Transform root, string path)
            {
                if (root == avatar.transform && paths != null && paths.TryGetValue(path, out var mapped)) path = mapped;
                var target = root;
                foreach (string part in (path ?? "").Split('/'))
                {
                    if (part.Length == 0) continue;
                    var children = target.Cast<Transform>().Where(t => t.name == part).ToArray();
                    if (children.Length != 1) return null;
                    target = children[0];
                }
                var renderers = target.GetComponents<Renderer>();
                return renderers.Length == 1 && (renderers[0] is SkinnedMeshRenderer || renderers[0] is MeshRenderer) ? renderers[0] : null;
            }
            foreach (var entry in controllers)
            foreach (var layer in entry.Value.Layers)
            foreach (var clip in layer.AllReachableNodes().OfType<VirtualClip>().Distinct())
            {
                var root = entry.Key is Animator animator ? animator.transform : avatar.transform;
                if (roots.TryGetValue(clip, out var previous) && previous != root) ambiguous.Add(clip); else roots[clip] = root;
                foreach (var binding in clip.GetObjectCurveBindings())
                    if (typeof(Renderer).IsAssignableFrom(binding.type) && binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal))
                    { var renderer = Resolve(root, binding.path); if (renderer != null) swaps.Add(renderer); }
                foreach (var binding in clip.GetFloatCurveBindings())
                {
                    if (!typeof(Renderer).IsAssignableFrom(binding.type) || !binding.propertyName.StartsWith("material.", StringComparison.Ordinal)) continue;
                    // Unity animates a Color/Vector as one property-block value. Deleting a constant
                    // channel while another channel remains animated makes the deleted channel zero,
                    // not the material's stored default. Keep component bindings until a whole-property
                    // proof accounts for every channel, including implicit values and other writers.
                    if (binding.propertyName.IndexOf('.', "material.".Length) >= 0) continue;
                    var renderer = Resolve(root, binding.path);
                    if (renderer != null) uses.Add(new Use { Owner = entry.Key, Controller = entry.Value, Layer = layer,
                        Clip = clip, Binding = binding, Renderer = renderer });
                }
            }
            var behaviours = controllers.Values.SelectMany(c => c.AllReachableNodes()).Distinct()
                .SelectMany(n => n is VirtualState s ? s.Behaviours : n is VirtualStateMachine m ? m.Behaviours : Enumerable.Empty<StateMachineBehaviour>()).ToArray();
            bool hasWeightControl = behaviours.Any(b => b is VRC_AnimatorLayerControl || b is VRC_PlayableLayerControl);
            var copies = new Dictionary<Renderer, Material[]>();
            var removals = new Dictionary<VirtualClip, HashSet<EditorCurveBinding>>();
            foreach (var group in uses.GroupBy(u => (u.Renderer, u.Binding.propertyName)))
            {
                var first = group.First(); var renderer = first.Renderer;
                if (swaps.Contains(renderer) || group.Any(u => ambiguous.Contains(u.Clip) || u.Clip.IsMarkerClip ||
                    u.Layer.BlendingMode != AnimatorLayerBlendingMode.Override || u.Layer.SyncedLayerIndex != -1 || u.Layer.AvatarMask != null ||
                    u.Owner is VRCAvatarDescriptor.AnimLayerType type && type == VRCAvatarDescriptor.AnimLayerType.Additive)) continue;
                if (group.Select(u => u.Binding).Distinct().Count() != 1) continue;
                if (!MotionAnalysis.Constant(first.Clip.GetFloatCurve(first.Binding), out float value) ||
                    group.Any(u => !MotionAnalysis.Constant(u.Clip.GetFloatCurve(u.Binding), out float other) || other != value)) continue;
                var materials = renderer.sharedMaterials;
                if (materials.Length == 0 || materials.Any(m => m == null)) continue;
                var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block);
                if (!block.isEmpty) continue;
                bool hasSlotBlock = false;
                for (int i = 0; i < materials.Length; i++) { renderer.GetPropertyBlock(block, i); if (!block.isEmpty) hasSlotBlock = true; }
                if (hasSlotBlock) continue;
                string property = first.Binding.propertyName.Substring("material.".Length);
                var defaults = new float[materials.Length];
                bool supported = true;
                for (int i = 0; i < materials.Length; i++)
                    if (!Read(materials[i], property, out defaults[i])) supported = false;
                if (!supported) continue;
                bool sameDefault = defaults.All(v => v == value);
                if (!sameDefault && (hasWeightControl || group.Any(u => !(u.Owner is VRCAvatarDescriptor.AnimLayerType type) ||
                    type != VRCAvatarDescriptor.AnimLayerType.FX))) continue;
                bool proven = true;
                var constants = new Dictionary<VirtualMotion, bool>();
                foreach (var context in group.GroupBy(u => (u.Controller, u.Layer)))
                {
                    var layer = context.Key.Layer;
                    if (!sameDefault && layer != context.Key.Controller.Layers.First() && layer.DefaultWeight != 1) { proven = false; break; }
                    if (!stateCache.TryGetValue(layer, out var states))
                        stateCache.Add(layer, states = layer.AllReachableNodes().OfType<VirtualState>().Where(analysis.IsReachable).ToArray());
                    if (states.Length == 0 || states.Any(s => !s.WriteDefaultValues ||
                        !ConstantOutput(s.Motion, first.Binding, value, sameDefault, analysis, new(), bindingCache, constants))) { proven = false; break; }
                }
                if (!proven) continue;
                if (!sameDefault)
                {
                    if (!copies.TryGetValue(renderer, out var changed))
                    {
                        changed = materials.Select(m => {
                            var copy = UnityEngine.Object.Instantiate(m); copy.name = m.name + " (optimized)";
                            save(copy); return copy;
                        }).ToArray();
                        copies.Add(renderer, changed); renderer.sharedMaterials = changed;
                    }
                    foreach (var material in changed) material.SetFloat(property, value);
                }
                foreach (var use in group)
                {
                    if (!removals.TryGetValue(use.Clip, out var bindings)) removals.Add(use.Clip, bindings = new());
                    bindings.Add(use.Binding);
                }
            }
            foreach (var pair in removals)
            { report.MaterialCurves += pair.Value.Count; MotionAnalysis.RemoveCurves(pair.Key, pair.Value); }
        }

        private static bool ConstantOutput(VirtualMotion motion, EditorCurveBinding binding, float value, bool defaultMatches,
            AnimatorReachability analysis, HashSet<VirtualMotion> stack,
            Dictionary<VirtualMotion, HashSet<EditorCurveBinding>> bindings, Dictionary<VirtualMotion, bool> constants)
        {
            if (motion == null) return defaultMatches;
            if (constants.TryGetValue(motion, out bool cached)) return cached;
            if (!stack.Add(motion)) return false;
            bool result = false;
            if (motion is VirtualClip clip)
            {
                var curve = clip.GetFloatCurve(binding);
                result = curve == null ? defaultMatches : MotionAnalysis.Constant(curve, out float sample) && sample == value;
            }
            else if (motion is VirtualBlendTree tree && tree.Children.Count > 0)
            {
                if (tree.BlendType != BlendTreeType.Direct)
                    result = tree.Children.All(c => ConstantOutput(c.Motion, binding, value, defaultMatches, analysis, stack, bindings, constants));
                else if (!tree.NormalizedBlendValues)
                {
                    var writers = tree.Children.Where(c => {
                        if (c.Motion == null) return false;
                        if (!bindings.TryGetValue(c.Motion, out var set)) bindings.Add(c.Motion, set = MotionAnalysis.Bindings(c.Motion));
                        return set.Contains(binding);
                    }).ToArray();
                    result = writers.Length == 0 ? defaultMatches : writers.Length == 1 &&
                        analysis.Values(writers[0].DirectBlendParameter).TryGetConstant(out float weight) && weight == 1 &&
                        ConstantOutput(writers[0].Motion, binding, value, defaultMatches, analysis, stack, bindings, constants);
                }
            }
            stack.Remove(motion); constants[motion] = result; return result;
        }

        private static bool Read(Material material, string property, out float value)
        {
            value = 0;
            if (material.shader == null || !material.HasProperty(property)) return false;
            int index = material.shader.FindPropertyIndex(property);
            if (index < 0) return false;
            var type = material.shader.GetPropertyType(index);
            if (type != UnityEngine.Rendering.ShaderPropertyType.Float && type != UnityEngine.Rendering.ShaderPropertyType.Range) return false;
            value = material.GetFloat(property);
            return MotionAnalysis.Finite(value);
        }
    }
}
