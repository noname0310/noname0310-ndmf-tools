using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.ObjectOptimization;
using UnityEngine;

namespace Noname.AvatarTools.MaterialAtlas
{
    internal static class AtlasAnimationSafety
    {
        internal static Dictionary<Renderer, string> Find(GameObject avatar,
            IDictionary<object, VirtualAnimatorController> controllers, ISet<string> preserved,
            IReadOnlyDictionary<string, string> paths, IReadOnlyList<MaterialAtlasGroup> groups,
            Func<Material, Material> original = null, Func<Material, int> selectGroup = null)
        {
            var result = new Dictionary<Renderer, string>();
            var unique = controllers.Values.Distinct().ToArray();
            // Reference conversion deliberately replaces static shader settings. A shared constant
            // tree weight must not make every sibling output a protected dynamic feature.
            // Analyze actual writers here; Preserve still prevents parameter freezing elsewhere.
            var analysis = AnimatorReachability.Analyze(avatar, unique);
            string unsupported = ObjectOptimizationSafety.UnsupportedRuntime(avatar) ?? analysis.FallbackReason;
            if (unsupported != null)
            {
                foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true)) result[renderer] = unsupported;
                return result;
            }
            var dependencies = AnimatorParameterDependencies.Collect(avatar, unique, controllerEntries: controllers);
            var affected = dependencies.AffectedBy(preserved, n => !analysis.Values(n).TryGetConstant(out _));
            foreach (var entry in controllers)
            {
                var root = entry.Key is Animator animator ? animator.transform : avatar.transform;
                foreach (var clip in entry.Value.AllReachableNodes().OfType<VirtualClip>().Distinct())
                {
                    bool protectedOutput = dependencies.MotionDependsOn(clip, affected);
                    string protection = protectedOutput ? "preserved parameter dependencies [" +
                        string.Join(", ", dependencies.MotionInputs(clip).Where(affected.Contains).OrderBy(n => n, StringComparer.Ordinal)) + "] control " : "animated material property ";
                    foreach (var binding in clip.GetObjectCurveBindings())
                    {
                        string reason = typeof(Renderer).IsAssignableFrom(binding.type) &&
                            binding.propertyName.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal) ? "animated material slot" :
                            binding.propertyName == "m_Mesh" && (binding.type == typeof(SkinnedMeshRenderer) || binding.type == typeof(MeshFilter)) ? "animated mesh reference" : null;
                        if (reason == null) continue;
                        foreach (var target in AnimationUsage.Resolve(avatar.transform, root, binding.path, paths))
                            foreach (var renderer in target.GetComponents<Renderer>()) result[renderer] = reason + " in " + clip.Name;
                    }
                    foreach (var binding in clip.GetFloatCurveBindings())
                    {
                        if (!typeof(Renderer).IsAssignableFrom(binding.type) || !binding.propertyName.StartsWith("material.", StringComparison.Ordinal)) continue;
                        string property = binding.propertyName.Substring("material.".Length).Split('.')[0];
                        foreach (var target in AnimationUsage.Resolve(avatar.transform, root, binding.path, paths))
                        foreach (var renderer in target.GetComponents(binding.type).OfType<Renderer>())
                        foreach (var current in renderer.sharedMaterials.Where(m => m != null))
                        {
                            var source = original?.Invoke(current) ?? current;
                            int index = selectGroup?.Invoke(current) ?? MaterialAtlasPass.SelectGroup(current, groups);
                            if (index < 0 || !HasProperty(source, property) && !HasProperty(current, property)) continue;
                            var group = groups[index];
                            if (group.TextureMappings.Any(m => property == m.SourceProperty || property == m.SourceProperty + "_ST" ||
                                property == m.DestinationProperty || property == m.DestinationProperty + "_ST"))
                                result[renderer] = "animated atlas texture property " + property + " in " + clip.Name;
                            else if (HasProperty(group.ReferenceMaterial, property))
                            {
                                // Numeric bindings stay on the same renderer; Unity's per-renderer
                                // property block and AAO's animation-aware merge preserve their values.
                                // Keep every Color/Vector channel together by leaving these curves intact.
                                if (!CompatibleNumericProperty(HasProperty(source, property) ? source : current, group.ReferenceMaterial, property))
                                    result[renderer] = "incompatible animated property " + property + " in " + clip.Name;
                            }
                            else if (protectedOutput) result[renderer] = protection + property + " in " + clip.Name;
                        }
                    }
                }
            }
            return result;
        }

        private static bool HasProperty(Material material, string name) => material.HasProperty(name) ||
            name.EndsWith("_ST", StringComparison.Ordinal) && material.HasTexture(name.Substring(0, name.Length - 3));

        private static bool CompatibleNumericProperty(Material source, Material destination, string name)
        {
            if (source.shader == null || destination.shader == null) return false;
            int a = source.shader.FindPropertyIndex(name), b = destination.shader.FindPropertyIndex(name);
            if (a < 0 || b < 0) return false;
            var first = source.shader.GetPropertyType(a); var second = destination.shader.GetPropertyType(b);
            if (first == UnityEngine.Rendering.ShaderPropertyType.Texture || second == UnityEngine.Rendering.ShaderPropertyType.Texture) return false;
            if (first == UnityEngine.Rendering.ShaderPropertyType.Range) first = UnityEngine.Rendering.ShaderPropertyType.Float;
            if (second == UnityEngine.Rendering.ShaderPropertyType.Range) second = UnityEngine.Rendering.ShaderPropertyType.Float;
            return first == second;
        }
    }
}
