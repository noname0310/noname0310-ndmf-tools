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
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Meshes
{
    internal sealed class MeshOptimizationState
    {
        internal ALTOTraceAndOptimizeMeshes Component;
        internal OptimizationBuildScope Scope;
        internal bool MergeRenderers, MergeSlots, RemoveUnusedData, RebindStaticBones;
        internal bool ExpandBounds, AllowCoordinateSpaceChanges;
        internal bool ConvertRigidMeshes, AllowMaterialSlotReordering;
        internal bool Changed;
        internal readonly HashSet<ObjectReference> Preserved = new();
        internal readonly List<(Renderer Original, bool InScope, bool Preserved)> Inputs = new();
        internal bool Enabled => Scope != null && (MergeRenderers || MergeSlots || RemoveUnusedData || RebindStaticBones);
    }

    internal static class MeshOptimizationPass
    {
        internal static void Capture(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeMeshes>(true);
            var enabled = all.Where(c => c.enabled && BuildPlatformFilters.Includes(c.transform, context)).ToArray();
            if (enabled.Length > 1) throw new InvalidOperationException("Use one enabled ALTO Trace And Optimize Meshes per avatar.");
            if (enabled.Length == 1)
            {
                var component = enabled[0]; var state = context.GetState<MeshOptimizationState>();
                state.Component = component;
                state.Scope = OptimizationBuildScope.Capture(component, context.AvatarRootObject);
                state.MergeRenderers = component.MergeCompatibleSkinnedMeshes; state.MergeSlots = component.MergeIdenticalMaterialSlots;
                state.RemoveUnusedData = component.RemoveUnusedMeshData;
                state.RebindStaticBones = component.RebindStaticBones;
                state.ExpandBounds = component.ExpandBounds;
                state.AllowCoordinateSpaceChanges = component.AllowCoordinateSpaceChanges &&
                    (component.CoordinateSpaceChangesOnPlatforms & OptimizationBuildScope.Platform(context.GetState<OptimizationBuildEnvironment>().Target)) != 0;
                state.ConvertRigidMeshes = state.AllowCoordinateSpaceChanges && state.MergeRenderers && component.ConvertRigidMeshes;
                state.AllowMaterialSlotReordering = state.MergeSlots && component.AllowMaterialSlotReordering &&
                    (component.MaterialSlotReorderingOnPlatforms & OptimizationBuildScope.Platform(context.GetState<OptimizationBuildEnvironment>().Target)) != 0;
                foreach (var renderer in (component.PreserveRenderers ?? new()).Where(r => r != null))
                {
                    if (!renderer.transform.IsChildOf(context.AvatarRootTransform))
                        throw new InvalidOperationException("Preserved renderers must belong to this avatar: " + renderer.name);
                    state.Preserved.Add(ObjectRegistry.GetReference(renderer));
                }
            }
            // Keep enabled metadata for optional external property/renderer mapping.
            foreach (var component in all.Except(enabled)) Object.DestroyImmediate(component);
        }

        internal static void Prepare(BuildContext context)
        {
            var state = context.GetState<MeshOptimizationState>();
            if (state.Component == null) return;
            var objectSettings = context.GetState<ObjectOptimizationState>();
            state.Component.BuildInputRenderers = context.AvatarRootObject.GetComponentsInChildren<Renderer>(true)
                .Where(r => r is SkinnedMeshRenderer || r is MeshRenderer).ToArray();
            foreach (var renderer in state.Component.BuildInputRenderers)
                state.Inputs.Add((renderer, state.Scope.Contains(renderer.transform),
                    state.Preserved.Contains(ObjectRegistry.GetReference(renderer)) || objectSettings.Protected(renderer)));
            if (state.RebindStaticBones)
                state.Component.BuildSkinningDependencies = SkinningBoundaryDependencies.Collect(context.AvatarRootTransform,
                    state.Inputs.Where(i => i.InScope && !i.Preserved).Select(i => i.Original).OfType<SkinnedMeshRenderer>());
        }

        internal static bool InputProtected(MeshOptimizationState settings, Renderer renderer, out string reason)
        {
            reason = null;
            for (int i = 0; i < settings.Inputs.Count; i++)
            {
                var mapped = settings.Component != null && settings.Component.BuildInputRenderers.Length == settings.Inputs.Count ?
                    settings.Component.BuildInputRenderers[i] : settings.Inputs[i].Original;
                if (mapped != renderer) continue;
                if (!settings.Inputs[i].InScope) { reason = "an external optimizer merged an out-of-scope input"; return true; }
                if (settings.Inputs[i].Preserved) { reason = "an external optimizer merged a preserved input"; return true; }
            }
            return false;
        }

        internal static void Apply(BuildContext context)
        {
            try { ApplyCore(context); }
            finally
            {
                CaptureMappedInputs(context.GetState<MeshOptimizationState>());
                foreach (var component in context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeMeshes>(true)) Object.DestroyImmediate(component);
            }
        }

        internal static void CaptureMappedInputs(MeshOptimizationState settings)
        {
            if (settings.Component == null || settings.Component.BuildInputRenderers.Length != settings.Inputs.Count) return;
            for (int i = 0; i < settings.Inputs.Count; i++)
                settings.Inputs[i] = (settings.Component.BuildInputRenderers[i], settings.Inputs[i].InScope, settings.Inputs[i].Preserved);
        }

        private static void ApplyCore(BuildContext context)
        {
            if (!context.GetState<MeshOptimizationState>().Enabled) return;
            using var lease = OptimizationSession.Acquire(context);
            var services = lease.Services;
            var entries = services.ControllerContext.Controllers.Where(e => !(e.Key is Object owner) || owner != null).ToDictionary(e => e.Key, e => e.Value);
            var policy = ParameterPolicyPass.Resolve(context, entries);
            ApplyCore(context, entries, policy, services.ObjectPathRemapper.GetVirtualToRealPathMap(),
                services.ObjectPathRemapper.GetVirtualPathForObject, mesh => context.AssetSaver.SaveAsset(mesh), false);
        }

        internal static int ApplyFinal(BuildContext context, IDictionary<object, VirtualAnimatorController> entries,
            ResolvedParameterPolicy policy, Action<Object> save)
            => ApplyCore(context, entries, policy, null,
                target => AnimationUtility.CalculateTransformPath(target, context.AvatarRootTransform), mesh => save(mesh), true);

        private static int ApplyCore(BuildContext context, IDictionary<object, VirtualAnimatorController> entries,
            ResolvedParameterPolicy policy, IReadOnlyDictionary<string, string> paths, Func<Transform, string> pathFor,
            Action<Mesh> save, bool runtimeDependenciesOnly)
        {
            var settings = context.GetState<MeshOptimizationState>();
            if (!settings.Enabled) return 0;
            var avatar = context.AvatarRootObject; var report = context.GetState<OptimizationBuildReport>();
            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => settings.Scope.Contains(r.transform)).ToArray();
            if (renderers.Length == 0 && (!settings.ConvertRigidMeshes || !avatar.GetComponentsInChildren<MeshRenderer>(true).Any(r => settings.Scope.Contains(r.transform)))) return 0;
            report.Initialize(avatar, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
            var watch = Stopwatch.StartNew();
            var animations = AvatarAnimationIndex.Create(avatar, entries, paths, policy.Preserved);
            string unsupported = ObjectOptimizationSafety.UnsupportedRuntime(avatar) ?? animations.UnsafeReason;
            if (policy.Uncertain || unsupported != null)
            {
                report.Messages.Add("Mesh optimization retained all renderers: " + (unsupported ?? "unverified Preserve mapping")); return 0;
            }
            var objectSettings = context.GetState<ObjectOptimizationState>();
            var referenceSettings = new ObjectOptimizationState
            { Scope = new OptimizationBuildScope { Root = avatar.transform }, RuntimeDependenciesOnly = runtimeDependenciesOnly };
            referenceSettings.ProtectedComponents.UnionWith(objectSettings.ProtectedComponents);
            referenceSettings.ProtectedSubtrees.AddRange(objectSettings.ProtectedSubtrees);
            var references = AvatarDependencyGraph.Create(avatar, animations, referenceSettings, collectObjects: false);
            int converted = 0, weighted = 0;
            if (settings.ConvertRigidMeshes)
            {
                foreach (var renderer in avatar.GetComponentsInChildren<MeshRenderer>(true).Where(r => settings.Scope.Contains(r.transform)))
                {
                    if (settings.Preserved.Contains(ObjectRegistry.GetReference(renderer)) || objectSettings.Protected(renderer) || InputProtected(settings, renderer, out _)) continue;
                    string issue = RigidMeshConversion.ValidateExplicitConversion(renderer, animations, references,
                        context.GetState<OptimizationAnalysis>().Shaders, true);
                    if (issue != null) { report.Messages.Add("Rigid renderer retained '" + renderer.name + "': " + issue + "."); continue; }
                    RigidMeshConversion.Convert(renderer, animations, save); converted++;
                }
                if (converted > 0)
                {
                    settings.Changed = true;
                    animations = AvatarAnimationIndex.Create(avatar, entries, paths, policy.Preserved);
                    references = AvatarDependencyGraph.Create(avatar, animations, referenceSettings, collectObjects: false);
                    renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => settings.Scope.Contains(r.transform)).ToArray();
                }
            }
            var mutations = TransformMutationAnalysis.Create(avatar, animations);
            var mergePolicy = new MeshMergePolicy { ExpandBounds = settings.ExpandBounds,
                AllowCoordinateSpaceChanges = settings.AllowCoordinateSpaceChanges, ConvertRigidMeshes = settings.ConvertRigidMeshes,
                AllowMaterialSlotReordering = settings.AllowMaterialSlotReordering, Mutations = mutations };
            var candidates = new List<MeshMergeCandidate>();
            var invariants = animations.Bindings.Any(u => u.Binding.propertyName == MeshMergeAnalysis.UpdateWhenOffscreenProperty) ?
                new InvariantComponentSettings(animations, AnimatorReachability.Analyze(avatar, animations.Controllers, policy.Preserved), entries) : null;
            foreach (var renderer in renderers)
            {
                if (settings.Preserved.Contains(ObjectRegistry.GetReference(renderer)) || objectSettings.Protected(renderer))
                { report.Messages.Add("Mesh retained '" + renderer.name + "': explicit preservation."); continue; }
                if (InputProtected(settings, renderer, out var inputReason))
                { report.Messages.Add("Mesh retained '" + renderer.name + "': " + inputReason + "."); continue; }
                var candidate = MeshMergeAnalysis.Inspect(renderer, avatar, animations, references, context.GetState<OptimizationAnalysis>().Shaders, out string reason, mergePolicy, invariants);
                if (candidate != null)
                {
                    if (settings.ConvertRigidMeshes && RigidMeshConversion.Unweighted(renderer.sharedMesh))
                    { RigidMeshConversion.WeightUnweighted(renderer, save); weighted++; settings.Changed = true; }
                    candidates.Add(candidate);
                    if (candidate.Pinned) report.Messages.Add("Mesh interface retained '" + renderer.name + "': " +
                        string.Join("; ", candidate.PinReasons.Take(5)) + ".");
                }
                else report.Messages.Add("Mesh retained '" + renderer.name + "': " + reason + ".");
            }
            var skinning = new Dictionary<SkinnedMeshRenderer, StaticSkinningPlan>();
            if (settings.RebindStaticBones)
            {
                if (mutations.Uncertain != null) report.Messages.Add("Static skin bones retained: " + mutations.Uncertain + ".");
                else foreach (var candidate in candidates)
                    skinning.Add(candidate.Renderer, StaticSkinningPlan.Create(candidate.Renderer, bone =>
                        bone != avatar.transform && bone.IsChildOf(avatar.transform) && settings.Scope.Contains(bone) &&
                        !(bone is RectTransform) && !mutations.Mutable.Contains(bone) && !objectSettings.Protected(bone)));
            }
            int removed = 0, slots = 0, groups = 0, vertices = 0, palette = 0, rebound = 0;
            var partitions = MeshMergeAnalysis.Group(candidates, settings.MergeRenderers, mergePolicy);
            foreach (var partition in partitions.Skip(1))
            {
                var conflict = partitions[0].SelectMany(first => partition.Select(second => new
                    { First = first, Second = second, Reason = MeshMergeAnalysis.Incompatibility(first, second, mergePolicy) }))
                    .FirstOrDefault(pair => pair.Reason != null);
                if (conflict != null) report.Messages.Add("Mesh group '" + partition[0].Renderer.name + "' (" + partition.Count +
                    " inputs) remains separate from '" + partitions[0][0].Renderer.name + "': " + conflict.Reason +
                    " ['" + conflict.First.Renderer.name + "', '" + conflict.Second.Renderer.name + "'].");
            }
            foreach (var group in partitions)
            {
                int before = group.Sum(c => c.Renderer.sharedMaterials.Length);
                var materials = group.SelectMany(c => c.Renderer.sharedMaterials).ToArray();
                bool sameMaterials = settings.AllowMaterialSlotReordering ? materials.Distinct().Count() < materials.Length :
                    materials.Zip(materials.Skip(1), (a, b) => a != null && a == b).Any(same => same);
                bool unusedData = settings.RemoveUnusedData && group.Any(c => SkinnedMeshAssembler.HasUnusedData(c.Renderer));
                int reboundCount = group.Sum(c => skinning.TryGetValue(c.Renderer, out var plan) ? plan.ReboundEntries : 0);
                if (group.Count == 1 && (!settings.MergeSlots || !sameMaterials) && !unusedData && reboundCount == 0)
                {
                    report.Messages.Add("Mesh retained '" + group[0].Renderer.name + "': no compatible renderer under the selected bounds/coordinate policy and matching visibility, masks, lighting and external interface.");
                    continue;
                }
                var destination = group[0].Renderer;
                int oldVertices = group.Sum(c => c.Renderer.sharedMesh.vertexCount), oldPalette = group.Sum(c => c.Renderer.bones.Length);
                Merge(group, pathFor(destination.transform), settings.MergeSlots,
                    save, settings.RemoveUnusedData, skinning, settings.ExpandBounds, settings.AllowMaterialSlotReordering);
                removed += group.Count - 1; slots += before - destination.sharedMaterials.Length; groups++;
                vertices += oldVertices - destination.sharedMesh.vertexCount; palette += oldPalette - destination.bones.Length;
                rebound += reboundCount;
                settings.Changed = true;
            }
            report.Messages.Add((runtimeDependenciesOnly ? "Final runtime mesh optimization: " : "Mesh optimization: ") + removed + " renderers and " + slots + " material slots merged in " + groups + " groups; " +
                vertices + " unused vertices and " + palette + " unused or duplicate bone-palette entries removed; " +
                rebound + " static skin-bone entries rebound; " + converted + " rigid renderers converted and " + weighted +
                " unweighted meshes assigned rigid skinning; material-slot reordering " + (settings.AllowMaterialSlotReordering ? "enabled" : "disabled") +
                "; " + watch.ElapsedMilliseconds + " ms.");
            context.GetState<OptimizationAnalysis>().Invalidate(AnalysisDomain.Animation | AnalysisDomain.Hierarchy | AnalysisDomain.Meshes);
            return groups + converted + weighted;
        }

        internal static void Merge(IReadOnlyList<MeshMergeCandidate> group, string destinationPath, bool mergeSlots, Action<Mesh> save, bool removeUnusedData = false,
            IReadOnlyDictionary<SkinnedMeshRenderer, StaticSkinningPlan> skinning = null, bool expandBounds = false, bool reorderMaterialSlots = false)
        {
            var sources = group.Select(c => c.Renderer).ToArray(); var destination = sources[0];
            var bounds = expandBounds ? MeshMergePolicy.UnionBounds(group) : group[0].Bounds;
            using var assembly = SkinnedMeshAssembler.Create(sources, mergeSlots, group[0].ReservedShapeNames, removeUnusedData, skinning,
                MeshMergePolicy.ReversedInputs(group), reorderMaterialSlots);
            var rewrites = new List<(AvatarAnimationIndex.BindingUse Use, EditorCurveBinding? Replacement)>();
            foreach (var candidate in group.Skip(1))
                foreach (var use in candidate.Bindings)
                {
                    EditorCurveBinding? replacement = null;
                    if (use.Binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal) &&
                        assembly.ShapeNames[candidate.Renderer].TryGetValue(use.Binding.propertyName.Substring("blendShape.".Length), out var shapeName))
                    {
                        var binding = use.Binding; binding.path = destinationPath; binding.type = typeof(SkinnedMeshRenderer);
                        binding.propertyName = "blendShape." + shapeName;
                        if (use.Clip.GetFloatCurve(binding) != null)
                            throw new InvalidOperationException("A merged morph would overwrite an existing animation binding: " + binding.propertyName);
                        replacement = binding;
                    }
                    // Missing morph tracks have no output on the fixed source mesh.
                    // Visibility, material and changing offscreen-update writers already
                    // exist on the destination with matching evaluation. Default-valued
                    // offscreen-update tracks are redundant. Object activation tracks remain.
                    rewrites.Add((use, replacement));
                }
            // Source assets remain immutable; only the build clone receives this mesh.
            ObjectRegistry.RegisterReplacedObject(destination.sharedMesh, assembly.Mesh);
            save?.Invoke(assembly.Mesh);
            // Release the old native skinning layout before changing vertex channels
            // and palette size. Editor cameras can have rendered the input this frame.
            destination.sharedMesh = null;
            destination.sharedMesh = assembly.Mesh; destination.bones = assembly.Bones; destination.sharedMaterials = assembly.Materials;
            bool offscreen = destination.updateWhenOffscreen;
            destination.updateWhenOffscreen = false; destination.localBounds = bounds; destination.updateWhenOffscreen = offscreen;
            for (int i = 0; i < assembly.ShapeWeights.Length; i++) destination.SetBlendShapeWeight(i, assembly.ShapeWeights[i]);
            foreach (var clips in rewrites.GroupBy(r => r.Use.Clip))
            {
                float end = MotionAnalysis.EndTime(clips.Key);
                foreach (var rewrite in clips) clips.Key.SetFloatCurve(rewrite.Use.Binding, null);
                foreach (var rewrite in clips)
                    if (rewrite.Replacement.HasValue) clips.Key.SetFloatCurve(rewrite.Replacement.Value, rewrite.Use.Curve);
                MotionAnalysis.PreserveDuration(clips.Key, end);
            }
            // Keep source objects, transforms and unrelated components. They may still be
            // bones or activation/reference targets. The object collector owns their removal.
            foreach (var source in sources.Skip(1)) Object.DestroyImmediate(source);
            assembly.Mesh = null;
        }
    }
}
