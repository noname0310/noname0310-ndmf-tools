using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Materials
{
    internal static partial class SharedShaderConstants
    {
        private const int MaximumUniformDomainValues = 32;
        private sealed class UseSet
        {
            internal readonly Dictionary<Material, HashSet<Renderer>> Materials = new();
            internal readonly Dictionary<Shader, string> Blocked = new();
            internal readonly HashSet<Shader> ReferencedShaders = new();
            internal readonly List<AvatarAnimationIndex.BindingUse> Swaps = new();
            internal string Issue;
            internal void Block(Material material, string reason) { if (material != null) Block(material.shader, reason); }
            internal void Block(Shader shader, string reason)
            { if (shader != null) { ReferencedShaders.Add(shader); if (!Blocked.ContainsKey(shader)) Blocked.Add(shader, reason); } }
            internal void Add(Renderer renderer, Material material)
            {
                if (material == null) return;
                if (material.shader != null) ReferencedShaders.Add(material.shader);
                if (!Materials.TryGetValue(material, out var renderers)) Materials.Add(material, renderers = new());
                renderers.Add(renderer);
            }
        }

        internal static int Apply(GameObject avatar, IDictionary<object, VirtualAnimatorController> entries,
            IReadOnlyDictionary<string, string> paths, BuildTarget target, Func<Renderer, bool> eligible,
            Func<Material, bool> preservedMaterial, Action<Object> save, IList<string> diagnostics,
            IEnumerable<string> preservedInputs = null, ShaderPropertyUsage shaders = null,
            Func<Renderer, bool> allowInitialChanges = null, Func<Renderer, bool> allowCompilerConstantFolding = null,
            AvatarAnalysisSnapshot facts = null, Action<AnalysisDomain> mutated = null, Action<Object> referenceChanged = null)
        {
            if (!SharedShaderConstantSource.SupportedTarget(target))
            { diagnostics?.Add("Shared shader constants retained: platform shader allowlists or graphics backend prohibit generated shaders."); return 0; }
            var index = facts?.Index ?? AvatarAnimationIndex.Create(avatar, entries, paths, preservedInputs);
            string issue = ObjectOptimizationSafety.UnsupportedRuntime(avatar) ?? index.UnsafeReason;
            if (issue != null) { diagnostics?.Add("Shared shader constants retained: " + issue); return 0; }
            // Avatar assumptions and file snapshots are fresh at this final build
            // boundary. Only immutable source/compiler artifacts cross boundaries.
            var sources = shaders == null ? new ShaderPropertyUsage() : new ShaderPropertyUsage(shaders.Artifacts);
            var references = facts?.References ?? new SerializedReferenceSnapshot();
            var uses = Collect(index, eligible, preservedMaterial, references);
            if (uses.Issue != null) { diagnostics?.Add("Shared shader constants retained: " + uses.Issue); return 0; }
            var shaderGroups = uses.Materials.Keys.Where(m => m.shader != null).GroupBy(m => m.shader).ToArray();
            var replacements = new Dictionary<Material, Material>(); var generated = new List<Object>();
            var replacedShaders = new HashSet<Shader>(); bool saved = false;
            try
            {
                var shaderReplacements = PlanDependencies(index, uses, target, sources, allowInitialChanges, diagnostics, allowCompilerConstantFolding);
                foreach (var pair in shaderReplacements) generated.Add(pair.Value);
                foreach (var group in shaderGroups)
                {
                    if (!shaderReplacements.TryGetValue(group.Key, out var shader)) continue;
                    replacedShaders.Add(group.Key);
                    foreach (var original in group)
                    {
                        var copy = new Material(original) { name = original.name + " (shared shader constants)", shader = shader };
                        copy.shaderKeywords = original.shaderKeywords;
                        replacements.Add(original, copy); generated.Add(copy);
                    }
                }
                if (replacements.Count == 0) return 0;
                foreach (var asset in generated) save(asset);
                saved = true;
                // Saving can invoke editor callbacks. Exact renderer writes below
                // do not describe those callbacks, so end the live fact epoch
                // before checking the resulting ownership graph.
                references.Invalidate(AnalysisDomain.All);
                facts?.Invalidate(externalMutation: true);
                foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
                {
                    var materials = renderer.sharedMaterials; bool changed = false;
                    for (int slot = 0; slot < materials.Length; slot++)
                        if (materials[slot] != null && replacements.TryGetValue(materials[slot], out var replacement))
                        { materials[slot] = replacement; changed = true; }
                    if (changed) { renderer.sharedMaterials = materials; references.Invalidate(renderer); referenceChanged?.Invoke(renderer); }
                }
                // Rewrite each curve once after all shader groups are planned.
                // A curve can swap between multiple shaders; group-by-group
                // writes from the old snapshot could restore an earlier shader.
                bool animationChanged = false;
                foreach (var use in uses.Swaps)
                {
                    var keys = use.ObjectKeys.ToArray(); bool changed = false;
                    for (int i = 0; i < keys.Length; i++)
                        if (keys[i].value is Material material && replacements.TryGetValue(material, out var replacement))
                        { keys[i].value = replacement; changed = true; }
                    if (changed) { use.Clip.SetObjectCurve(use.Binding, keys); animationChanged = true; }
                }
                foreach (var replacement in replacements) ObjectRegistry.RegisterReplacedObject(replacement.Key, replacement.Value);
                var final = Collect(AvatarAnimationIndex.Create(avatar, entries, paths, preservedInputs), eligible, preservedMaterial, references);
                var after = final.Materials.Keys.Where(m => m.shader != null).Select(m => m.shader).ToHashSet();
                if (final.Issue != null || after.Overlaps(replacedShaders) || after.Count > shaderGroups.Length)
                    throw new InvalidOperationException("Shared shader replacement did not preserve complete one-for-one shader ownership.");
                mutated?.Invoke(AnalysisDomain.Materials | (animationChanged ? AnalysisDomain.Animation : AnalysisDomain.None));
                return replacedShaders.Count;
            }
            finally
            {
                if (!saved) foreach (var asset in generated.AsEnumerable().Reverse()) if (asset != null) Object.DestroyImmediate(asset);
            }
        }

        private static UseSet Collect(AvatarAnimationIndex index, Func<Renderer, bool> eligible, Func<Material, bool> preservedMaterial,
            SerializedReferenceSnapshot snapshot = null)
        {
            var result = new UseSet(); var renderers = index.Avatar.GetComponentsInChildren<Renderer>(true).ToHashSet();
            foreach (var renderer in renderers) foreach (var material in renderer.sharedMaterials) result.Add(renderer, material);
            foreach (var use in index.Bindings.Where(u => u.Binding.isPPtrCurve))
            {
                var targets = use.Objects().ToArray();
                bool safe = use.Complete && !use.Protected && ConstantMaterialSlots.TrySlot(use.Binding.propertyName, out int slot) &&
                    targets.Length != 0 && targets.All(o => o is Renderer r && slot < r.sharedMaterials.Length) &&
                    use.ObjectKeys != null && use.ObjectKeys.All(k => k.value == null || k.value is Material);
                if (safe)
                {
                    result.Swaps.Add(use);
                    foreach (var renderer in targets.Cast<Renderer>())
                    foreach (var material in use.ObjectKeys.Select(k => k.value).OfType<Material>()) result.Add(renderer, material);
                }
                else
                {
                    foreach (var material in (use.ObjectKeys ?? Array.Empty<ObjectReferenceKeyframe>()).Select(k => k.value).OfType<Material>())
                        result.Block(material, "unsupported or protected animation reference");
                    foreach (var shader in (use.ObjectKeys ?? Array.Empty<ObjectReferenceKeyframe>()).Select(k => k.value).OfType<Shader>())
                        result.Block(shader, "an animation directly references the shader");
                    if (use.Binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal))
                        foreach (var renderer in targets.OfType<Renderer>()) foreach (var material in renderer.sharedMaterials)
                            result.Block(material, "incomplete material-slot animation");
                }
            }
            foreach (var pair in result.Materials)
            {
                if (preservedMaterial?.Invoke(pair.Key) == true || pair.Key.parent != null)
                    result.Block(pair.Key, "preserved material or Material Variant");
                foreach (var renderer in pair.Value)
                    if (!(renderer is MeshRenderer || renderer is SkinnedMeshRenderer) || eligible?.Invoke(renderer) == false || renderer.HasPropertyBlock())
                        result.Block(pair.Key, "preserved/unsupported renderer or MaterialPropertyBlock");
            }
            snapshot ??= new SerializedReferenceSnapshot(); var visited = new HashSet<Object>();
            void Inspect(Object owner)
            {
                if (owner == null || !visited.Add(owner)) return;
                if (visited.Count > 4096) throw new InvalidOperationException("material reference traversal budget exceeded");
                foreach (var entry in snapshot.Get(owner))
                {
                    if (owner is Renderer renderer && renderers.Contains(renderer) && ConstantMaterialSlots.TrySlot(entry.Path, out _)) continue;
                    if (owner is Material owned && result.Materials.ContainsKey(owned) && entry.Path == "m_Shader") continue;
                    if (entry.Value is Material material)
                    {
                        result.Block(material, "material reference outside a remappable renderer slot");
                        Inspect(material);
                    }
                    else if (entry.Value is Shader shader) result.Block(shader, "direct serialized shader reference");
                    else if (entry.Value is RuntimeAnimatorController && OwnedControllerReference(index, owner, entry.Path)) continue;
                    // Texture assets can contain native material references, for
                    // example CustomRenderTexture's update/initialization materials.
                    // Only the exact controller fields represented by the virtual
                    // index are remapped later; arbitrary clip/controller references
                    // must retain every material they can introduce.
                    else if (!(entry.Value is Transform || entry.Value is Mesh || entry.Value is AudioClip ||
                        entry.Value is MonoScript)) Inspect(entry.Value);
                }
            }
            try
            {
                foreach (var material in result.Materials.Keys) Inspect(material);
                foreach (var use in index.Bindings.Where(u => u.Binding.isPPtrCurve))
                foreach (var key in use.ObjectKeys ?? Array.Empty<ObjectReferenceKeyframe>())
                    if (!(key.value is Material || key.value is Shader)) Inspect(key.value);
                foreach (var component in index.Avatar.GetComponentsInChildren<Component>(true)) Inspect(component);
                foreach (var controller in index.Controllers)
                foreach (var node in controller.AllReachableNodes())
                {
                    if (node is VirtualState state) foreach (var behavior in state.Behaviours) Inspect(behavior);
                    if (node is VirtualStateMachine machine) foreach (var behavior in machine.Behaviours) Inspect(behavior);
                }
            }
            catch (Exception exception) { result.Issue = "serialized shader/material references are incomplete: " + exception.Message; }
            return result;
        }

        private static bool OwnedControllerReference(AvatarAnimationIndex index, Object owner, string path)
        {
            if (owner is Animator animator && path == "m_Controller") return index.ControllerEntries.ContainsKey(animator);
            if (!(owner is VRCAvatarDescriptor descriptor) || descriptor.gameObject != index.Avatar) return false;
            bool Layer(VRCAvatarDescriptor.CustomAnimLayer[] layers, string field)
            {
                for (int i = 0; i < (layers?.Length ?? 0); i++)
                    if (path == field + ".Array.data[" + i + "].animatorController")
                        return index.ControllerEntries.ContainsKey(layers[i].type);
                return false;
            }
            return Layer(descriptor.baseAnimationLayers, "baseAnimationLayers") ||
                Layer(descriptor.specialAnimationLayers, "specialAnimationLayers");
        }

    }
}
