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
        internal bool RemoveHiddenGeometry;
        internal bool ExpandBounds;
        internal bool PreserveVertexIds;
        internal int VertexIdUniformThreshold = 3;
        internal bool PreserveParticleRandomSequence;
        internal readonly MeshOptimizationPolicies Policies = new();
        internal bool MayConvertRigid => MergeRenderers && Policies.MayConvertRigid;
        internal bool Changed;
        internal readonly HashSet<ObjectReference> Preserved = new();
        internal readonly List<(Renderer Original, bool InScope, bool Preserved)> Inputs = new();
        internal readonly List<MeshOptimizationPermissions> InputPermissions = new();
        internal readonly List<ObjectReference> InputReferences = new();
        private readonly Dictionary<ObjectReference, MeshOptimizationPermissions> rewrittenPermissions = new();
        internal bool Enabled => Scope != null && (MergeRenderers || MergeSlots || RemoveUnusedData || RebindStaticBones || RemoveHiddenGeometry);

        internal Renderer MappedInput(int index) => Component != null && Component.BuildInputRenderers.Length == Inputs.Count
            ? Component.BuildInputRenderers[index] : Inputs[index].Original;

        internal MeshOptimizationPermissions PermissionsFor(Renderer renderer)
        {
            var reference = ObjectRegistry.GetReference(renderer);
            bool found = rewrittenPermissions.TryGetValue(reference, out var permissions);
            if (!found) permissions = MeshOptimizationPermissions.All;
            for (int i = 0; i < Inputs.Count; i++)
            {
                if (MappedInput(i) != renderer && (i >= InputReferences.Count || !Equals(InputReferences[i], reference))) continue;
                // A merged output inherits only permissions granted to every input.
                // Missing provenance cannot grant a visual relaxation.
                permissions &= i < InputPermissions.Count ? InputPermissions[i] : MeshOptimizationPermissions.None;
                found = true;
            }
            if (!found) permissions = Policies.Resolve(renderer.transform);
            if (!MergeRenderers) permissions &= ~MeshOptimizationPermissions.RigidConversion;
            if (!MergeSlots) permissions &= ~MeshOptimizationPermissions.MaterialSlotReordering;
            return permissions;
        }

        internal void RecordPermissions(Renderer renderer, MeshOptimizationPermissions permissions) =>
            rewrittenPermissions[ObjectRegistry.GetReference(renderer)] = permissions;
    }

    internal static class MeshOptimizationPass
    {
        internal static void Capture(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeMeshes>(true);
            var enabled = all.Where(c => c.enabled && BuildPlatformFilters.IncludesSettings(c.transform, context)).ToArray();
            if (enabled.Length > 1) throw new InvalidOperationException("Use one enabled ALTO Trace And Optimize Meshes per avatar.");
            var state = context.GetState<MeshOptimizationState>();
            state.Policies.Capture(context);
            if (enabled.Length == 1)
            {
                var component = enabled[0];
                state.Component = component;
                state.Scope = OptimizationBuildScope.Capture(component, context.AvatarRootObject);
                state.MergeRenderers = component.MergeCompatibleSkinnedMeshes; state.MergeSlots = component.MergeIdenticalMaterialSlots;
                state.RemoveUnusedData = component.RemoveUnusedMeshData;
                state.RemoveHiddenGeometry = component.RemovePermanentlyHiddenGeometry;
                state.RebindStaticBones = component.RebindStaticBones;
                state.ExpandBounds = component.ExpandBounds;
                state.PreserveVertexIds = component.PreserveVertexIds;
                state.VertexIdUniformThreshold = Math.Max(2, component.VertexIdUniformThreshold);
                state.PreserveParticleRandomSequence = component.PreserveParticleRandomSequence;
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
            {
                state.Inputs.Add((renderer, state.Scope.Contains(renderer.transform),
                    state.Preserved.Contains(ObjectRegistry.GetReference(renderer)) || objectSettings.Protected(renderer)));
                state.InputPermissions.Add(state.Policies.Resolve(renderer.transform));
                state.InputReferences.Add(ObjectRegistry.GetReference(renderer));
            }
            if (state.RebindStaticBones)
                state.Component.BuildSkinningDependencies = SkinningBoundaryDependencies.Collect(context.AvatarRootTransform,
                    state.Inputs.Where(i => i.InScope && !i.Preserved).Select(i => i.Original).OfType<SkinnedMeshRenderer>());
        }

        internal static bool InputProtected(MeshOptimizationState settings, Renderer renderer, out string reason)
        {
            reason = null;
            for (int i = 0; i < settings.Inputs.Count; i++)
            {
                var mapped = settings.MappedInput(i);
                if (mapped != renderer) continue;
                if (!settings.Inputs[i].InScope) { reason = "an external optimizer merged an out-of-scope input"; return true; }
                if (settings.Inputs[i].Preserved) { reason = "an external optimizer merged a preserved input"; return true; }
            }
            return false;
        }

        internal static void Apply(BuildContext context)
            => ApplyAndCountChanges(context);

        internal static int ApplyAndCountChanges(BuildContext context)
        {
            try { return ApplyCore(context); }
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

        private static int ApplyCore(BuildContext context)
        {
            if (!context.GetState<MeshOptimizationState>().Enabled) return 0;
            using var lease = OptimizationSession.Acquire(context);
            var services = lease.Services;
            var entries = services.ControllerContext.Controllers.Where(e => !(e.Key is Object owner) || owner != null).ToDictionary(e => e.Key, e => e.Value);
            var policy = ParameterPolicyPass.Resolve(context, entries);
            return ApplyCore(context, entries, policy, services.ObjectPathRemapper.GetVirtualToRealPathMap(),
                services.ObjectPathRemapper.GetVirtualPathForObject, mesh => context.AssetSaver.SaveAsset(mesh), false, context.AssetSaver.SaveAsset);
        }

        internal static int ApplyFinal(BuildContext context, IDictionary<object, VirtualAnimatorController> entries,
            ResolvedParameterPolicy policy, Action<Object> save)
            => ApplyCore(context, entries, policy, null,
                target => AnimationUtility.CalculateTransformPath(target, context.AvatarRootTransform), mesh => save(mesh), true, save);

        private static int ApplyCore(BuildContext context, IDictionary<object, VirtualAnimatorController> entries,
            ResolvedParameterPolicy policy, IReadOnlyDictionary<string, string> paths, Func<Transform, string> pathFor,
            Action<Mesh> save, bool runtimeDependenciesOnly, Action<Object> saveOther)
        {
            var settings = context.GetState<MeshOptimizationState>();
            if (!settings.Enabled) return 0;
            var avatar = context.AvatarRootObject; var report = context.GetState<OptimizationBuildReport>();
            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => settings.Scope.Contains(r.transform)).ToArray();
            if (renderers.Length == 0 && (!settings.MayConvertRigid || !avatar.GetComponentsInChildren<MeshRenderer>(true).Any(r => settings.Scope.Contains(r.transform)))) return 0;
            report.Initialize(avatar, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
            var watch = Stopwatch.StartNew();
            var phaseWatch = Stopwatch.StartNew();
            var shaderSources = context.GetState<OptimizationAnalysis>().Shaders;
            var shaderInputs = shaderSources.MeshInputs;
            int shaderDiagnosticsBefore = shaderInputs.UnitDiagnostics.Count;
            double preprocessingBefore = shaderSources.PreprocessorGraphMilliseconds;
            double nativePreprocessBefore = shaderInputs.NativePreprocessMilliseconds;
            int nativePreprocessHitsBefore = shaderInputs.NativePreprocessCacheHits;
            double nativeCompileBefore = shaderInputs.NativeCompileMilliseconds;
            double nativeWaitBefore = shaderInputs.NativeCompileWaitMilliseconds;
            double slicesBefore = shaderInputs.UseSliceMilliseconds;
            double specializationBefore = shaderInputs.SpecializationMilliseconds;
            double keywordsBefore = shaderInputs.KeywordPlanMilliseconds;
            double inspectedProgramsBefore = shaderInputs.PreprocessedInspectionMilliseconds;
            var sharedFacts = context.GetState<OptimizationAnalysis>().Avatar(avatar, entries, paths, policy.Preserved);
            var animations = sharedFacts.Index;
            double indexMilliseconds = phaseWatch.Elapsed.TotalMilliseconds;
            phaseWatch.Restart();
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
            var records = sharedFacts.References;
            var references = AvatarDependencyGraph.Create(avatar, animations, referenceSettings, collectObjects: false,
                shaders: context.GetState<OptimizationAnalysis>().Shaders, references: records);
            int converted = 0, weighted = 0;
            bool pendingReduction = context.GetState<MeshReduction.MeshReductionState>().Pending ||
                context.GetState<MaterialAtlas.MaterialAtlasState>().Pending;
            if (settings.MayConvertRigid && !pendingReduction)
            {
                foreach (var renderer in avatar.GetComponentsInChildren<MeshRenderer>(true).Where(r => settings.Scope.Contains(r.transform)))
                {
                    var permissions = settings.PermissionsFor(renderer);
                    if ((permissions & MeshOptimizationPermissions.RigidConversion) == 0) continue;
                    if (settings.Preserved.Contains(ObjectRegistry.GetReference(renderer)) || objectSettings.Protected(renderer) || InputProtected(settings, renderer, out _)) continue;
                    string issue = RigidMeshConversion.ValidateExplicitConversion(renderer, animations, references,
                        context.GetState<OptimizationAnalysis>().Shaders, true,
                        (permissions & MeshOptimizationPermissions.TransparentSortingChanges) != 0,
                        context.GetState<OptimizationBuildEnvironment>().Target,
                        (permissions & MeshOptimizationPermissions.ShaderIndexChanges) != 0,
                        context.GetState<OptimizationPolicy>().AllowsSmallNumericalDifferences(renderer.transform, NumericalOptimization.Skinning));
                    if (issue != null) { report.Messages.Add("Rigid renderer retained '" + renderer.name + "': " + issue + "."); continue; }
                    var convertedRenderer = RigidMeshConversion.Convert(renderer, animations, save);
                    settings.RecordPermissions(convertedRenderer, permissions); converted++;
                }
                if (converted > 0)
                {
                    settings.Changed = true;
                    animations = AvatarAnimationIndex.Create(avatar, entries, paths, policy.Preserved);
                    records = new SerializedReferenceSnapshot();
                    references = AvatarDependencyGraph.Create(avatar, animations, referenceSettings, collectObjects: false,
                        shaders: context.GetState<OptimizationAnalysis>().Shaders, references: records);
                    renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => settings.Scope.Contains(r.transform)).ToArray();
                }
            }
            var staticProof = new StaticTransformProof(avatar, animations, objectSettings, records, context.GetState<OptimizationPolicy>());
            var mutations = staticProof.Mutations;
            var shapeProvenance = context.GetState<BlendShapeOptimization.OptimizationState>().Provenance;
            // SDK callbacks can still add vertex deformation after NDMF. Manual
            // bakes use the completed NDMF graph; SDK builds wait for its finalizer.
            bool finalGeometry = runtimeDependenciesOnly || !AvatarOutputSdkFinalizer.HasSession(context);
            int hiddenTriangles = finalGeometry ? PermanentlyHiddenGeometry.Apply(avatar, renderers, settings,
                objectSettings, animations, references, shaderSources, context.GetState<OptimizationBuildEnvironment>().Target,
                save, report.Messages, shapeProvenance, context.GetState<OptimizationPolicy>().AllowsInitialValueChanges) : 0;
            if (hiddenTriangles != 0) settings.Changed = true;
            // Keep renderer-level reduction policy boundaries intact until all
            // visibility/geometry changes have converged and the budget is spent.
            if (pendingReduction) return hiddenTriangles;
            double dependencyMilliseconds = phaseWatch.Elapsed.TotalMilliseconds;
            phaseWatch.Restart();
            int independentlyRebound = StaticSkinningOptimization.Apply(avatar, settings, objectSettings,
                animations, references, staticProof, save, shapeProvenance);
            double skinningMilliseconds = phaseWatch.Elapsed.TotalMilliseconds;
            phaseWatch.Restart();
            var permissionsByRenderer = renderers.ToDictionary(r => (Renderer)r, settings.PermissionsFor);
            var mergePolicy = new MeshMergePolicy { ExpandBounds = settings.ExpandBounds,
                NumericalPolicy = context.GetState<OptimizationPolicy>(),
                Target = context.GetState<OptimizationBuildEnvironment>().Target,
                PreserveVertexIds = settings.PreserveVertexIds && runtimeDependenciesOnly,
                PreserveParticleRandomSequence = settings.PreserveParticleRandomSequence,
                PermissionsFor = renderer => permissionsByRenderer[renderer], Mutations = mutations };
            var candidates = new List<MeshMergeCandidate>();
            var invariants = animations.Bindings.Any(u => u.Binding.propertyName == MeshMergeAnalysis.UpdateWhenOffscreenProperty) ?
                new InvariantComponentSettings(animations, animations.Reachability, entries) : null;
            foreach (var renderer in renderers)
            {
                if (settings.Preserved.Contains(ObjectRegistry.GetReference(renderer)) || objectSettings.Protected(renderer))
                { report.Messages.Add("Mesh retained '" + renderer.name + "': explicit preservation."); continue; }
                if (InputProtected(settings, renderer, out var inputReason))
                { report.Messages.Add("Mesh retained '" + renderer.name + "': " + inputReason + "."); continue; }
                var candidate = MeshMergeAnalysis.Inspect(renderer, avatar, animations, references, context.GetState<OptimizationAnalysis>().Shaders, out string reason, mergePolicy, invariants);
                if (candidate != null)
                {
                    if (mergePolicy.ConvertsRigid(renderer) && RigidMeshConversion.Unweighted(renderer.sharedMesh))
                    { RigidMeshConversion.WeightUnweighted(renderer, save, shapeProvenance); weighted++; settings.Changed = true; }
                    candidates.Add(candidate);
                    if (candidate.Pinned) report.Messages.Add("Mesh interface retained '" + renderer.name + "': " +
                        string.Join("; ", candidate.PinReasons.Take(5)) + ".");
                }
                else report.Messages.Add("Mesh retained '" + renderer.name + "': " + reason + ".");
            }
            double inspectionMilliseconds = phaseWatch.Elapsed.TotalMilliseconds;
            phaseWatch.Restart();
            var skinning = new Dictionary<SkinnedMeshRenderer, StaticSkinningPlan>();
            int removed = 0, slots = 0, groups = 0, vertices = 0, palette = 0, rebound = independentlyRebound, equivalentVisibility = 0;
            var planning = new MeshMergePlanning(mergePolicy);
            var partitions = MeshMergeAnalysis.Group(candidates, settings.MergeRenderers, mergePolicy, planning);
            foreach (var partition in partitions.Skip(1))
            {
                var conflict = partitions[0].SelectMany(first => partition.Select(second => new
                    { First = first, Second = second, Reason = planning.Incompatibility(first, second) }))
                    .FirstOrDefault(pair => pair.Reason != null);
                if (conflict != null) report.Messages.Add("Mesh group '" + partition[0].Renderer.name + "' (" + partition.Count +
                    " inputs) remains separate from '" + partitions[0][0].Renderer.name + "': " + conflict.Reason +
                    " ['" + conflict.First.Renderer.name + "', '" + conflict.Second.Renderer.name + "'].");
            }
            double groupingMilliseconds = phaseWatch.Elapsed.TotalMilliseconds;
            phaseWatch.Restart();
            foreach (var group in partitions)
            {
                var permissions = group.Aggregate(MeshOptimizationPermissions.All,
                    (allowed, candidate) => allowed & permissionsByRenderer[candidate.Renderer]);
                bool reorderSlots = (permissions & MeshOptimizationPermissions.MaterialSlotReordering) != 0;
                int before = group.Sum(c => c.Renderer.sharedMaterials.Length);
                bool sameMaterials = MeshMergeAnalysis.HasMergeableMaterialSlots(group, reorderSlots);
                bool unusedData = settings.RemoveUnusedData && group.Any(c => SkinnedMeshAssembler.HasUnusedData(c.Renderer));
                int reboundCount = group.Sum(c => skinning.TryGetValue(c.Renderer, out var plan) ? plan.ReboundEntries : 0);
                if (group.Count == 1 && (!settings.MergeSlots || !sameMaterials) && !unusedData && reboundCount == 0)
                {
                    report.Messages.Add("Mesh retained '" + group[0].Renderer.name + "': no compatible renderer under the selected bounds/coordinate policy and matching visibility, masks, lighting and external interface.");
                    continue;
                }
                var destination = group[0].Renderer;
                var materialAnimations = planning.Plan(group);
                if (materialAnimations.Issue != null)
                { report.Messages.Add("Mesh group retained: " + materialAnimations.Issue); continue; }
                foreach (var change in materialAnimations.DefaultChanges)
                    report.Messages.Add("Animated material default changed by explicit mesh policy: '" + change.Source.Renderer.name + "' " +
                        change.Property + " from " + change.Previous.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " to " +
                        change.Retained.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " (retained renderer '" + destination.name + "').");
                if (group.Skip(1).Any(c => !MeshMergeAnalysis.SameVisibility(group[0], c, semantic: false))) equivalentVisibility++;
                int oldVertices = group.Sum(c => c.Renderer.sharedMesh.vertexCount), oldPalette = group.Sum(c => c.Renderer.bones.Length);
                VertexIdMergePlan vertexIds = null;
                try
                {
                    if (group.Any(c => c.VertexIdMaterials.Count != 0))
                        vertexIds = VertexIdMergePlan.Create(group, settings.RemoveUnusedData, settings.VertexIdUniformThreshold, mergePolicy.Target);
                }
                catch (InvalidOperationException exception)
                { report.Messages.Add("Mesh group retained: " + exception.Message); continue; }
                using (vertexIds)
                {
                    if (vertexIds != null && group.Count == 1 && (!settings.MergeSlots || !sameMaterials) && reboundCount == 0 &&
                        (!settings.RemoveUnusedData || !SkinnedMeshAssembler.HasUnusedData(destination, !vertexIds.KeepVertices.Contains(destination))))
                    {
                        report.Messages.Add("Mesh retained '" + destination.name + "': original vertex numbering costs less than remapping.");
                        continue;
                    }
                    string firstVertexInput = vertexIds?.VertexOrder[0].name;
                    Merge(group, pathFor(destination.transform), settings.MergeSlots,
                        save, settings.RemoveUnusedData, skinning, settings.ExpandBounds, reorderSlots, vertexIds, saveOther, materialAnimations, shapeProvenance,
                        context.GetState<OptimizationPolicy>());
                    planning.Clear();
                    if (vertexIds != null) report.Messages.Add("Vertex-ID preservation for '" + destination.name + "': vertex buffer starts with '" +
                        firstVertexInput + "'; " + vertexIds.KeepVertices.Count + " original vertex layouts retained; " +
                        vertexIds.AttributeInputs.Count + " inputs use original-ID attributes; " + vertexIds.LiteralShaders + " literal and " +
                        vertexIds.UniformShaders + " uniform shader adapters.");
                }
                settings.RecordPermissions(destination, permissions);
                removed += group.Count - 1; slots += before - destination.sharedMaterials.Length; groups++;
                vertices += oldVertices - destination.sharedMesh.vertexCount; palette += oldPalette - destination.bones.Length;
                rebound += reboundCount;
                settings.Changed = true;
            }
            report.Messages.Add("Mesh analysis timings (ms): animation index=" + indexMilliseconds.ToString("F1") +
                ", dependencies=" + dependencyMilliseconds.ToString("F1") + ", static skinning=" + skinningMilliseconds.ToString("F1") +
                ", candidate inspection=" + inspectionMilliseconds.ToString("F1") + ", grouping=" + groupingMilliseconds.ToString("F1") +
                ", assembly=" + phaseWatch.Elapsed.TotalMilliseconds.ToString("F1") +
                "; included shader work: preprocessor=" + (shaderSources.PreprocessorGraphMilliseconds - preprocessingBefore).ToString("F1") +
                ", native preprocessing=" + (shaderInputs.NativePreprocessMilliseconds - nativePreprocessBefore).ToString("F1") +
                ", native compilation=" + (shaderInputs.NativeCompileMilliseconds - nativeCompileBefore).ToString("F1") +
                ", compilation wait=" + (shaderInputs.NativeCompileWaitMilliseconds - nativeWaitBefore).ToString("F1") +
                ", input slices=" + (shaderInputs.UseSliceMilliseconds - slicesBefore).ToString("F1") +
                ", specialization=" + (shaderInputs.SpecializationMilliseconds - specializationBefore).ToString("F1") +
                ", keyword planning=" + (shaderInputs.KeywordPlanMilliseconds - keywordsBefore).ToString("F1") +
                ", program inspection=" + (shaderInputs.PreprocessedInspectionMilliseconds - inspectedProgramsBefore).ToString("F1") + ".");
            report.Messages.Add("Shader preprocessing artifact hits: " + (shaderInputs.NativePreprocessCacheHits - nativePreprocessHitsBefore) +
                "; " + shaderSources.Artifacts.NativeProgramCount + " programs for " + shaderSources.Artifacts.NativeVariantCount +
                " variants, " + shaderSources.Artifacts.NativeProgramBytes + " source bytes retained.");
            for (int i = shaderDiagnosticsBefore; i < shaderInputs.UnitDiagnostics.Count; i++)
                report.Messages.Add("Shader input proof: " + shaderInputs.UnitDiagnostics[i]);
            report.Messages.Add((runtimeDependenciesOnly ? "Final runtime mesh optimization: " : "Mesh optimization: ") + removed + " renderers and " + slots + " material slots merged in " + groups + " groups; " +
                vertices + " unused vertices and " + palette + " unused or duplicate bone-palette entries removed; " +
                rebound + " static skin-bone entries rebound; " + converted + " rigid renderers converted and " + weighted +
                " unweighted meshes assigned rigid skinning; material-slot reordering permitted for " +
                permissionsByRenderer.Values.Count(p => (p & MeshOptimizationPermissions.MaterialSlotReordering) != 0) + " inspected renderers" +
                "; " + equivalentVisibility + " groups use equivalent Animator visibility outputs; " + watch.ElapsedMilliseconds + " ms.");
            context.GetState<OptimizationAnalysis>().Invalidate(AnalysisDomain.Animation | AnalysisDomain.Hierarchy | AnalysisDomain.Meshes);
            return groups + converted + weighted + independentlyRebound + hiddenTriangles;
        }

        internal static void Merge(IReadOnlyList<MeshMergeCandidate> group, string destinationPath, bool mergeSlots, Action<Mesh> save, bool removeUnusedData = false,
            IReadOnlyDictionary<SkinnedMeshRenderer, StaticSkinningPlan> skinning = null, bool expandBounds = false, bool reorderMaterialSlots = false,
            VertexIdMergePlan vertexIds = null, Action<Object> saveOther = null, MeshMaterialAnimationPlan materialAnimations = null,
            BlendShapeOptimization.BlendShapeProvenance shapeProvenance = null, OptimizationPolicy numericalPolicy = null)
        {
            var sources = group.Select(c => c.Renderer).ToArray(); var destination = sources[0];
            materialAnimations ??= MeshMaterialAnimationPlan.Create(group, validateDefaults: false);
            if (materialAnimations.Issue != null) throw new InvalidOperationException(materialAnimations.Issue);
            var coordinateRoot = MeshMergePolicy.DestinationCoordinateRoot(group);
            var bounds = expandBounds ? MeshMergePolicy.UnionBounds(group, coordinateRoot) : group[0].Bounds;
            using var assembly = SkinnedMeshAssembler.Create(sources, mergeSlots, group[0].ReservedShapeNames, removeUnusedData, skinning,
                MeshMergePolicy.ReversedInputs(group, coordinateRoot), reorderMaterialSlots,
                group.Where(c => c.NativeFaceInterface).ToDictionary(c => c.Renderer, c => c.NativeFaceShapes), vertexIds,
                group.ToDictionary(c => c.Renderer, c => c.IsolatedMaterialSlots));
            var rewrites = new List<(AvatarAnimationIndex.BindingUse Use, EditorCurveBinding? Replacement)>();
            var materialRewrites = new List<(AvatarAnimationIndex.BindingUse Use, EditorCurveBinding Replacement, ObjectReferenceKeyframe[] Keys)>();
            foreach (var candidate in group)
                foreach (var use in candidate.Bindings)
                {
                    if (candidate.MaterialSwaps.TryGetValue(use, out int slot))
                    {
                        var binding = use.Binding; binding.path = destinationPath; binding.type = typeof(SkinnedMeshRenderer);
                        binding.propertyName = MaterialFilterPass.SlotPrefix + assembly.MaterialSlots[candidate.Renderer][slot] + "]";
                        var keys = use.ObjectKeys.Select(k => new ObjectReferenceKeyframe { time = k.time,
                            value = vertexIds != null ? vertexIds.RemapMaterial(candidate.Renderer, k.value as Material) : k.value }).ToArray();
                        materialRewrites.Add((use, binding, keys));
                        continue;
                    }
                    if (use.Binding.propertyName.StartsWith("material.", StringComparison.Ordinal))
                    {
                        EditorCurveBinding? materialOutput = null;
                        if (materialAnimations.Retained.Contains(use))
                        {
                            var binding = use.Binding; binding.path = destinationPath; binding.type = typeof(SkinnedMeshRenderer);
                            materialOutput = binding;
                        }
                        rewrites.Add((use, materialOutput));
                        continue;
                    }
                    if (candidate == group[0])
                    {
                        if (!use.Binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) continue;
                        string originalName = use.Binding.propertyName.Substring("blendShape.".Length);
                        if (assembly.ShapeNames[candidate.Renderer].TryGetValue(originalName, out var firstName))
                        { if (originalName == firstName) continue; }
                        else if (assembly.Mesh.GetBlendShapeIndex(originalName) < 0) continue;
                        // A native morph name arriving from another renderer can make
                        // an originally missing destination binding active. Remove that
                        // binding before adding the shared runtime interface.
                    }
                    EditorCurveBinding? replacement = null;
                    if (use.Binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal) &&
                        assembly.ShapeNames[candidate.Renderer].TryGetValue(use.Binding.propertyName.Substring("blendShape.".Length), out var shapeName))
                    {
                        var binding = use.Binding; binding.path = destinationPath; binding.type = typeof(SkinnedMeshRenderer);
                        binding.propertyName = "blendShape." + shapeName;
                        var existing = use.Clip.GetFloatCurve(binding);
                        bool movedAway = rewrites.Any(r => r.Use.Clip == use.Clip && r.Use.Binding.Equals(binding) &&
                            (!r.Replacement.HasValue || !r.Replacement.Value.Equals(binding)));
                        bool shared = candidate.NativeFaceShapes.Contains(shapeName) && MeshAnimationSignal.SameCurve(existing, use.Curve);
                        if (existing != null && !movedAway && !shared)
                            throw new InvalidOperationException("A merged morph would overwrite an existing animation binding: " + binding.propertyName);
                        replacement = binding;
                    }
                    // Missing morph tracks have no output on the fixed source mesh.
                    // Visibility and changing offscreen-update writers already
                    // exist on the destination with matching evaluation. Default-valued
                    // offscreen-update tracks are redundant. Object activation tracks remain.
                    rewrites.Add((use, replacement));
                }
            // Validate the complete relocation before changing either the mesh
            // or shared clips. Clear all old bindings before installing new slots.
            foreach (var edits in rewrites.Where(r => r.Use.Binding.propertyName.StartsWith("material.", StringComparison.Ordinal))
                .GroupBy(r => r.Use.Clip))
            {
                var originals = edits.Select(r => r.Use.Binding).ToHashSet();
                foreach (var target in edits.Where(r => r.Replacement.HasValue).GroupBy(r => r.Replacement.Value))
                {
                    if (target.Skip(1).Any(r => !MeshAnimationSignal.SameCurve(target.First().Use.Curve, r.Use.Curve)))
                        throw new InvalidOperationException("Independent material outputs would share one merged binding: " + target.Key.propertyName);
                    if (!originals.Contains(target.Key) && edits.Key.GetFloatCurve(target.Key) != null)
                        throw new InvalidOperationException("A merged material output would overwrite an existing animation binding: " + target.Key.propertyName);
                }
            }
            foreach (var edits in materialRewrites.GroupBy(r => r.Use.Clip))
            {
                if (edits.Select(r => r.Replacement).Distinct().Count() != edits.Count())
                    throw new InvalidOperationException("Independent material swaps would share one merged slot.");
                var originals = edits.Select(r => r.Use.Binding).ToHashSet();
                foreach (var edit in edits)
                    if (!originals.Contains(edit.Replacement) && edits.Key.GetObjectCurve(edit.Replacement) != null)
                        throw new InvalidOperationException("A merged material slot would overwrite an existing animation binding: " + edit.Replacement.propertyName);
            }
            var lengths = rewrites.Select(r => r.Use.Clip).Concat(materialRewrites.Select(r => r.Use.Clip)).Distinct()
                .ToDictionary(clip => clip, MotionAnalysis.EndTime);
            // Source assets remain immutable; only the build clone receives this mesh.
            ObjectRegistry.RegisterReplacedObject(destination.sharedMesh, assembly.Mesh);
            save?.Invoke(assembly.Mesh);
            vertexIds?.Save(saveOther);
            shapeProvenance?.RecordMerge(destination, assembly.Mesh, assembly.ShapeNames);
            // Release the old native skinning layout before changing vertex channels
            // and palette size. Editor cameras can have rendered the input this frame.
            destination.sharedMesh = null;
            if (coordinateRoot != MeshMergePolicy.CoordinateRoot(destination)) destination.rootBone = coordinateRoot;
            destination.sharedMesh = assembly.Mesh; destination.bones = assembly.Bones; destination.sharedMaterials = assembly.Materials;
            bool offscreen = destination.updateWhenOffscreen;
            destination.updateWhenOffscreen = false; destination.localBounds = bounds; destination.updateWhenOffscreen = offscreen;
            for (int i = 0; i < assembly.ShapeWeights.Length; i++) destination.SetBlendShapeWeight(i, assembly.ShapeWeights[i]);
            foreach (var clips in rewrites.GroupBy(r => r.Use.Clip))
            {
                foreach (var rewrite in clips) clips.Key.SetFloatCurve(rewrite.Use.Binding, null);
                foreach (var rewrite in clips)
                    if (rewrite.Replacement.HasValue) clips.Key.SetFloatCurve(rewrite.Replacement.Value, rewrite.Use.Curve);
            }
            foreach (var rewrite in materialRewrites) rewrite.Use.Clip.SetObjectCurve(rewrite.Use.Binding, null);
            foreach (var rewrite in materialRewrites) rewrite.Use.Clip.SetObjectCurve(rewrite.Replacement, rewrite.Keys);
            foreach (var length in lengths) MotionAnalysis.PreserveDuration(length.Key, length.Value);
            // Keep source objects, transforms and unrelated components. They may still be
            // bones or activation/reference targets. The object collector owns their removal.
            if (group[0].Avatar != null) ContactOutputs.Remap(group[0].Avatar, sources, destination);
            foreach (var candidate in group)
                foreach (var binding in candidate.ParticleBindings) binding.Apply(destination, assembly.MaterialSlots[candidate.Renderer]);
            if (group[0].Avatar != null)
            {
                var descriptor = group[0].Avatar.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
                if (descriptor != null && descriptor.faceTrackingMeshes != null)
                    descriptor.faceTrackingMeshes = descriptor.faceTrackingMeshes.Select(r => r != null && sources.Contains(r) ? destination : r).Distinct().ToList();
            }
            numericalPolicy?.InheritNumericalConstraints(destination.transform, sources.Select(source => source.transform));
            foreach (var source in sources.Skip(1)) Object.DestroyImmediate(source);
            assembly.Mesh = null;
        }
    }
}
