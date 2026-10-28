using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.BlendShapeOptimization;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    internal static class MaterialFilterPass
    {
        internal const string SlotPrefix = "m_Materials.Array.data[";

        internal static void Apply(BuildContext context)
        {
            var state = context.GetState<BuildFilterState>();
            if (state.Scope == null || state.Materials.Count == 0 && !state.RemoveAdditionalMaterialPasses && state.ExcludedRendererTypes == RendererFilterTypes.None) return;
            foreach (var selection in state.SelectedRenderers)
                if (selection.Key.Renderer == null)
                    throw new InvalidOperationException("The renderer selected for material exclusion was removed or merged before filtering: " +
                        selection.Value + ". Select the generated destination renderer instead.");
            var animationLease = Noname.AvatarTools.Build.OptimizationSession.Acquire(context);
            var services = animationLease.Services;
            try
            {
                var entries = services.ControllerContext.Controllers
                    .Where(e => !(e.Key is UnityEngine.Object owner) || owner != null).ToDictionary(e => e.Key, e => e.Value);
                var report = context.GetState<OptimizationBuildReport>();
                report.Initialize(context.AvatarRootObject, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
                AutomaticRenderingRules.RemoveRenderers(context.AvatarRootObject, state, entries.Values, report.Messages);
                var renderers = context.AvatarRootObject.GetComponentsInChildren<Renderer>(true)
                    .Where(r => state.Scope.Contains(r.transform) && !ALTOPlugin.IsEditorOnly(r.transform)).ToArray();
                var rules = AutomaticRenderingRules.MaterialRules(state, renderers);
                var allPlans = renderers.Select(r => MaterialSlotPlan.Create(r, rules)).ToArray();
                var plans = allPlans.Where(p => p.HasChanges).ToDictionary(p => p.Renderer);
                int curves = RewriteAnimations(context.AvatarRootObject, entries, plans, rules,
                    new HashSet<Renderer>(renderers), services.ObjectPathRemapper.GetVirtualToRealPathMap());
                foreach (var plan in plans.Values)
                {
                    int removedGeometry = plan.SourceMesh != null ? plan.SourceMesh.subMeshCount - plan.KeptSubMeshes.Length : 0;
                    plan.Apply(context);
                    report.Messages.Add("Filtered materials on " + AnimationUtility.CalculateTransformPath(plan.Renderer.transform, context.AvatarRootTransform) +
                        ": removed " + plan.SlotMap.Count(i => i < 0) + " slots and " + removedGeometry + " submeshes.");
                }
                for (int i = 0; i < state.Materials.Count; i++)
                    if (!allPlans.Any(p => p.MatchedRules.Contains(state.Materials[i])))
                    {
                        string message = "Material exclusion " + (i + 1) + " matched no material slots after generators ran.";
                        report.Messages.Add(message);
                        Debug.LogWarning("[ALTO] " + message, context.AvatarRootObject);
                    }
                report.Messages.Add("Material exclusions remapped or removed " + curves + " material-slot animation bindings.");
            }
            finally
            {
                animationLease.Dispose();
            }
        }

        internal static int RewriteAnimations(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<Renderer, MaterialSlotPlan> plans, IReadOnlyList<MaterialExclusion> rules,
            ISet<Renderer> scope, IReadOnlyDictionary<string, string> paths = null)
        {
            var destinations = new Dictionary<(VirtualClip clip, EditorCurveBinding binding), HashSet<int>>();
            foreach (var entry in controllers)
            {
                var root = entry.Key is Animator animator ? animator.transform : avatar.transform;
                foreach (var clip in entry.Value.AllReachableNodes().OfType<VirtualClip>().Distinct())
                    foreach (var binding in clip.GetObjectCurveBindings())
                    {
                        var targets = AnimationUsage.Resolve(avatar.transform, root, binding.path, paths);
                        if (binding.propertyName == "m_Mesh" &&
                            (binding.type == typeof(MeshFilter) || binding.type == typeof(SkinnedMeshRenderer)))
                        {
                            if (targets.SelectMany(t => t.GetComponents<Renderer>()).Any(plans.ContainsKey))
                                throw new InvalidOperationException("Material exclusion cannot safely remap an animated mesh reference: " + binding.path);
                            continue;
                        }
                        if (!typeof(Renderer).IsAssignableFrom(binding.type) || !TrySlot(binding.propertyName, out int slot)) continue;
                        foreach (var target in targets)
                        foreach (var renderer in target.GetComponents(binding.type).OfType<Renderer>())
                        {
                            if (scope.Contains(renderer)) ValidateAnimatedMaterialSelection(renderer, slot, clip, binding, rules);
                            int destination = plans.TryGetValue(renderer, out var plan) && slot < plan.SlotMap.Length ? plan.SlotMap[slot] : slot;
                            var key = (clip, binding);
                            if (!destinations.TryGetValue(key, out var values)) destinations.Add(key, values = new());
                            values.Add(destination);
                        }
                    }
            }
            var changes = new List<(VirtualClip clip, EditorCurveBinding from, int slot, ObjectReferenceKeyframe[] keys)>();
            foreach (var destination in destinations)
            {
                TrySlot(destination.Key.binding.propertyName, out int previous);
                if (destination.Value.Count != 1)
                    throw new InvalidOperationException("A material-slot animation is shared by animation roots requiring different slot mappings: " +
                        destination.Key.clip.Name + " / " + destination.Key.binding.path + ". Use separate clips or matching exclusion rules.");
                int next = destination.Value.Single();
                if (next == previous) continue;
                if (destination.Key.clip.IsMarkerClip)
                    throw new InvalidOperationException("A built-in marker animation cannot be remapped for material exclusion: " + destination.Key.clip.Name);
                changes.Add((destination.Key.clip, destination.Key.binding, next, destination.Key.clip.GetObjectCurve(destination.Key.binding)));
            }
            // Validate all consumers before writing shared virtual clips. Clear old slots first to avoid
            // overwriting an existing slot curve when a higher slot moves to its index.
            var lengths = changes.Select(c => c.clip).Distinct().ToDictionary(c => c, MotionAnalysis.EndTime);
            foreach (var change in changes) change.clip.SetObjectCurve(change.from, null);
            foreach (var change in changes)
                if (change.slot >= 0)
                {
                    var binding = change.from;
                    binding.propertyName = SlotPrefix + change.slot + "]";
                    change.clip.SetObjectCurve(binding, change.keys);
                }
            foreach (var length in lengths) MotionAnalysis.PreserveDuration(length.Key, length.Value);
            return changes.Count;
        }

        private static bool TrySlot(string property, out int slot)
        {
            slot = -1;
            return property.StartsWith(SlotPrefix, StringComparison.Ordinal) && property.EndsWith("]", StringComparison.Ordinal) &&
                int.TryParse(property.Substring(SlotPrefix.Length, property.Length - SlotPrefix.Length - 1), out slot) && slot >= 0;
        }

        private static void ValidateAnimatedMaterialSelection(Renderer renderer, int slot, VirtualClip clip,
            EditorCurveBinding binding, IReadOnlyList<MaterialExclusion> rules)
        {
            var materials = renderer.sharedMaterials;
            foreach (var rule in rules)
            {
                if (rule.Material == null || rule.Slot >= 0 || (rule.Renderer != null && rule.Renderer != renderer)) continue;
                bool initial = slot < materials.Length && MaterialSlotPlan.SameMaterial(materials[slot], rule.Material);
                var keys = clip.GetObjectCurve(binding) ?? Array.Empty<ObjectReferenceKeyframe>();
                bool selected = keys.Any(k => k.value is Material material && MaterialSlotPlan.SameMaterial(material, rule.Material));
                bool other = keys.Any(k => k.value is Material material && !MaterialSlotPlan.SameMaterial(material, rule.Material));
                if ((!initial && selected) || (initial && other))
                    throw new InvalidOperationException("Material-selected slot has animated swaps to different materials: " + renderer.name +
                        " slot " + slot + ". Specify Renderer and Slot explicitly to remove that slot in every state.");
            }
        }
    }
}
