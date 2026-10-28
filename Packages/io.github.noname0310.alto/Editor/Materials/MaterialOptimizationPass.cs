using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Materials
{
    internal sealed class MaterialOptimizationState
    {
        internal ALTOTraceAndOptimizeMaterials Component;
        internal OptimizationBuildScope Scope;
        internal bool Clean, Share;
        internal readonly HashSet<ObjectReference> Preserved = new();
        internal readonly List<(Renderer Original, bool Protected)> Inputs = new();
    }

    internal static class MaterialOptimizationPass
    {
        internal static void Capture(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeMaterials>(true);
            var enabled = all.Where(c => c.enabled && BuildPlatformFilters.Includes(c.transform, context)).ToArray();
            if (enabled.Length > 1) throw new InvalidOperationException("Use one enabled ALTO Trace And Optimize Materials per avatar.");
            if (enabled.Length == 1)
            {
                var state = context.GetState<MaterialOptimizationState>(); var component = state.Component = enabled[0];
                state.Scope = OptimizationBuildScope.Capture(component, context.AvatarRootObject);
                state.Clean = component.RemoveUnusedSavedProperties; state.Share = component.ShareEquivalentMaterials;
                foreach (var material in (component.PreserveMaterials ?? new()).Where(m => m != null)) state.Preserved.Add(ObjectRegistry.GetReference(material));
            }
            foreach (var component in all.Except(enabled)) Object.DestroyImmediate(component);
        }

        internal static void Prepare(BuildContext context)
        {
            var state = context.GetState<MaterialOptimizationState>();
            if (state.Component == null) return;
            state.Component.BuildInputRenderers = context.AvatarRootObject.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in state.Component.BuildInputRenderers)
                state.Inputs.Add((renderer, !state.Scope.Contains(renderer.transform) || context.GetState<ObjectOptimizationState>().Protected(renderer)));
        }

        internal static bool ProtectedInput(MaterialOptimizationState state, Renderer renderer)
        {
            for (int i = 0; i < state.Inputs.Count; i++)
            {
                var mapped = state.Component != null && state.Component.BuildInputRenderers.Length == state.Inputs.Count ?
                    state.Component.BuildInputRenderers[i] : state.Inputs[i].Original;
                if (mapped == renderer && state.Inputs[i].Protected) return true;
            }
            return false;
        }

        internal static void Apply(BuildContext context)
        {
            try { ApplyCore(context); }
            finally
            {
                foreach (var component in context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeMaterials>(true)) Object.DestroyImmediate(component);
            }
        }

        private static void ApplyCore(BuildContext context)
        {
            var state = context.GetState<MaterialOptimizationState>();
            if (state.Scope == null || !state.Clean && !state.Share) return;
            var avatar = context.AvatarRootObject; var report = context.GetState<OptimizationBuildReport>();
            report.Initialize(avatar, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
            var watch = Stopwatch.StartNew();
            using var lease = OptimizationSession.Acquire(context);
            var services = lease.Services;
            var entries = services.ControllerContext.Controllers.Where(e => !(e.Key is Object owner) || owner != null).ToDictionary(e => e.Key, e => e.Value);
                var policy = ParameterPolicyPass.Resolve(context, entries);
            var animations = AvatarAnimationIndex.Create(avatar, entries, services.ObjectPathRemapper.GetVirtualToRealPathMap(), policy.Preserved);
            string issue = ObjectOptimizationSafety.UnsupportedRuntime(avatar) ?? animations.UnsafeReason;
            if (policy.Uncertain || issue != null)
            { report.Messages.Add("Material optimization retained inputs: " + (issue ?? "unverified Preserve mapping")); return; }
            var objectState = context.GetState<ObjectOptimizationState>();
            var renderers = avatar.GetComponentsInChildren<Renderer>(true).Where(r => (r is SkinnedMeshRenderer || r is MeshRenderer) &&
                state.Scope.Contains(r.transform) && !objectState.Protected(r) && !ProtectedInput(state, r)).ToHashSet();
            var swaps = animations.Bindings.Where(u => u.Binding.isPPtrCurve && u.Binding.propertyName.StartsWith("m_Materials", StringComparison.Ordinal)).ToArray();
            bool Safe(AvatarAnimationIndex.BindingUse use) => use.Complete && !use.Protected &&
                use.Binding.propertyName.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal) &&
                use.Objects().Any() && use.Objects().All(o => o is Renderer renderer && renderers.Contains(renderer)) &&
                use.ObjectKeys != null && use.ObjectKeys.All(k => k.value == null || k.value is Material);
            // Removing one renderer may make a shared clip unsafe for another. Compute the
            // closed selection before rewriting any asset references.
            bool changed;
            do
            {
                changed = false;
                foreach (var use in swaps.Where(u => !Safe(u)))
                    foreach (var renderer in use.Objects().OfType<Renderer>()) changed |= renderers.Remove(renderer);
            } while (changed);
            var safeSwaps = swaps.Where(Safe).ToArray();
            var pool = renderers.SelectMany(r => r.sharedMaterials).Concat(safeSwaps.SelectMany(u => u.ObjectKeys.Select(k => k.value).OfType<Material>()))
                .Where(m => m != null).Distinct().ToArray();
            var references = AvatarDependencyGraph.Create(avatar, animations, new ObjectOptimizationState
            { Scope = new OptimizationBuildScope { Root = avatar.transform } }, false);
            bool Protected(Material material) => state.Preserved.Contains(ObjectRegistry.GetReference(material)) || material.parent != null ||
                references.ReferenceOwners.TryGetValue(material, out var readers) && readers.Any(r => !(r is Renderer));
            var shaders = context.GetState<OptimizationAnalysis>().Shaders; var fallbacks = new VrcFallbackPropertyContract();
            var replacements = new Dictionary<Material, Material>(); var canonical = new List<Material>(); var created = new HashSet<Material>();
            int removed = 0, shared = 0;
            foreach (var source in pool)
            {
                if (Protected(source)) { replacements.Add(source, source); continue; }
                var unused = state.Clean ? SavedMaterialProperties.Unused(source, shaders, fallbacks) : new();
                var result = unused.Count > 0 ? SavedMaterialProperties.CleanCopy(source, unused) : source;
                if (result != source) { created.Add(result); removed += unused.Count; }
                var existing = state.Share ? canonical.FirstOrDefault(m => SavedMaterialProperties.Equivalent(m, result)) : null;
                if (existing != null)
                {
                    if (created.Remove(result)) Object.DestroyImmediate(result);
                    result = existing; shared++;
                }
                else canonical.Add(result);
                replacements.Add(source, result);
            }
            foreach (var material in created) context.AssetSaver.SaveAsset(material);
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++) if (materials[i] != null && replacements.TryGetValue(materials[i], out var replacement)) materials[i] = replacement;
                renderer.sharedMaterials = materials;
            }
            foreach (var use in safeSwaps)
            {
                var keys = use.ObjectKeys.ToArray();
                for (int i = 0; i < keys.Length; i++)
                    if (keys[i].value is Material material && replacements.TryGetValue(material, out var replacement)) keys[i].value = replacement;
                use.Clip.SetObjectCurve(use.Binding, keys);
            }
            report.Messages.Add("Material optimization: " + removed + " unused saved properties removed; " + shared +
                " equivalent materials shared across " + renderers.Count + " renderers; " + watch.ElapsedMilliseconds + " ms. Source assets and fallback inputs retained.");
        }
    }
}
