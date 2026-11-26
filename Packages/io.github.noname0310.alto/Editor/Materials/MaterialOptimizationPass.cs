using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
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
        internal bool Clean, Share, BakeLilToon, BakeShaderInputs;
        internal readonly HashSet<ObjectReference> Preserved = new();
        internal readonly List<(Renderer Original, bool Protected)> Inputs = new();
    }

    internal static class MaterialOptimizationPass
    {
        internal static void Capture(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeMaterials>(true);
            var enabled = all.Where(c => c.enabled && BuildPlatformFilters.IncludesSettings(c.transform, context)).ToArray();
            if (enabled.Length > 1) throw new InvalidOperationException("Use one enabled ALTO Trace And Optimize Materials per avatar.");
            if (enabled.Length == 1)
            {
                var state = context.GetState<MaterialOptimizationState>(); var component = state.Component = enabled[0];
                state.Scope = OptimizationBuildScope.Capture(component, context.AvatarRootObject);
                state.Clean = component.RemoveUnusedSavedProperties; state.Share = component.ShareEquivalentMaterials;
                state.BakeLilToon = component.BakeLilToonMainTextures;
                state.BakeShaderInputs = component.BakeConstantShaderInputs;
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
                CaptureMappedInputs(context.GetState<MaterialOptimizationState>());
                foreach (var component in context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeMaterials>(true)) Object.DestroyImmediate(component);
            }
        }

        private static void CaptureMappedInputs(MaterialOptimizationState state)
        {
            if (state.Component == null || state.Component.BuildInputRenderers.Length != state.Inputs.Count) return;
            // External generators can remap several source renderers to one
            // output. Keep every source protection after the marker is removed;
            // final shader specialization runs beyond that marker's lifetime.
            for (int i = 0; i < state.Inputs.Count; i++)
                state.Inputs[i] = (state.Component.BuildInputRenderers[i], state.Inputs[i].Protected);
        }

        internal static void BakeBeforeAtlas(BuildContext context) => ApplyCore(context, true);

        internal static void SpecializeFinal(BuildContext context)
        {
            // SDK builds have a later callback boundary containing late material
            // generators. Manual NDMF builds finish at PlatformFinish instead.
            if (AvatarOutputSdkFinalizer.HasSession(context)) return;
            using var lease = OptimizationSession.Acquire(context);
            var services = lease.Services;
            var entries = services.ControllerContext.Controllers.Where(e => !(e.Key is Object owner) || owner != null)
                .ToDictionary(e => e.Key, e => e.Value);
            int changes = SpecializeFinal(context, entries, ParameterPolicyPass.Resolve(context, entries),
                services.ObjectPathRemapper.GetVirtualToRealPathMap(), context.AssetSaver.SaveAsset);
            // Manual NDMF builds have no later SDK finalizer. New immutable
            // shader inputs can expose more mesh/consumer simplifications here
            // too, using the same controllers and current path remapper.
            if (changes != 0) OptimizationFixedPoint.Apply(context, includeMeshes: true);
        }

        internal static int SpecializeFinal(BuildContext context, IDictionary<object, VirtualAnimatorController> entries,
            ResolvedParameterPolicy policy, IReadOnlyDictionary<string, string> paths, Action<Object> save)
            => SpecializeFinalChanges(context, entries, policy, paths, save).Count;

        internal static OptimizationChanges SpecializeFinalChanges(BuildContext context, IDictionary<object, VirtualAnimatorController> entries,
            ResolvedParameterPolicy policy, IReadOnlyDictionary<string, string> paths, Action<Object> save)
        {
            var state = context.GetState<MaterialOptimizationState>();
            if (!state.BakeShaderInputs || state.Scope == null || policy.Uncertain) return default;
            var report = context.GetState<OptimizationBuildReport>();
            var objects = context.GetState<ObjectOptimizationState>();
            using var measurement = OptimizationMeasurement.Begin(report, "final material output", "shared shader constants");
            var analysis = context.GetState<OptimizationAnalysis>();
            AnalysisDomain domains = AnalysisDomain.None;
            var referenceOwners = new HashSet<Object>();
            try
            {
                int changes = SharedShaderConstants.Apply(context.AvatarRootObject, entries, paths,
                    context.GetState<OptimizationBuildEnvironment>().Target,
                    renderer => state.Scope.Contains(renderer.transform) && !objects.Protected(renderer) && !ProtectedInput(state, renderer),
                    material => state.Preserved.Contains(ObjectRegistry.GetReference(material)), save, report.Messages, policy.Preserved,
                    analysis.Shaders,
                    renderer => context.GetState<OptimizationPolicy>().AllowsInitialValueChanges(renderer.transform),
                    renderer => context.GetState<OptimizationPolicy>().AllowsShaderConstantFolding(renderer.transform),
                    analysis.Avatar(context.AvatarRootObject, entries, paths, policy.Preserved), changed => domains |= changed,
                    owner => referenceOwners.Add(owner));
                var mutation = new OptimizationChanges(changes, domains, referenceOwners);
                analysis.Invalidate(mutation);
                measurement?.Complete();
                return mutation;
            }
            catch { analysis.Invalidate(AnalysisDomain.Materials | AnalysisDomain.Animation); throw; }
        }

        private static void ApplyCore(BuildContext context, bool bakeOnly = false)
        {
            var state = context.GetState<MaterialOptimizationState>();
            if (state.Scope == null || (bakeOnly ? !state.BakeLilToon : !state.Clean && !state.Share && !state.BakeLilToon)) return;
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
            { Scope = new OptimizationBuildScope { Root = avatar.transform } }, false, context.GetState<OptimizationAnalysis>().Shaders);
            bool Protected(Material material) => state.Preserved.Contains(ObjectRegistry.GetReference(material)) || material.parent != null ||
                references.ReferenceOwners.TryGetValue(material, out var readers) && readers.Any(r => !(r is Renderer));
            var shaders = context.GetState<OptimizationAnalysis>().Shaders; var fallbacks = new VrcFallbackPropertyContract();
            var replacements = new Dictionary<Material, Material>(); var canonical = new List<Material>(); var created = new HashSet<Material>();
            var animated = state.BakeLilToon ? MaterialTextureBake.AnimatedProperties(animations, renderers, safeSwaps) :
                new Dictionary<Material, HashSet<string>>();
            int removed = 0, shared = 0, baked = 0;
            if (state.BakeLilToon && MaterialTextureBake.LilToon == null)
                report.Messages.Add("lilToon texture baking skipped: supported lilToon integration is not installed.");
            foreach (var source in pool)
            {
                if (Protected(source)) { replacements.Add(source, source); continue; }
                var unused = !bakeOnly && state.Clean ? SavedMaterialProperties.Unused(source, shaders, fallbacks) : new();
                var result = unused.Count > 0 ? SavedMaterialProperties.CleanCopy(source, unused) : source;
                if (result != source) { created.Add(result); removed += unused.Count; }
                if (state.BakeLilToon)
                {
                    var bake = MaterialTextureBake.LilToon?.Invoke(result, animated.TryGetValue(source, out var properties) ? properties : new());
                    if (bake != null)
                    {
                        report.Messages.Add(source.name + ": " + bake.Detail);
                        if (bake.Material != null)
                        {
                            if (created.Remove(result)) Object.DestroyImmediate(result);
                            result = bake.Material; created.Add(result); baked++;
                            context.AssetSaver.SaveAsset(bake.Texture);
                            ObjectRegistry.RegisterReplacedObject(source, result);
                        }
                    }
                }
                var existing = !bakeOnly && state.Share ? canonical.FirstOrDefault(m => SavedMaterialProperties.Equivalent(m, result)) : null;
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
                " equivalent materials shared; " + baked + " lilToon textures baked across " + renderers.Count + " renderers; " + watch.ElapsedMilliseconds + " ms. Source assets retained.");
        }
    }
}
