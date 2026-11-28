using System;
using Noname.AvatarTools.Analysis;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.ParameterPolicy;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.MaterialAtlas;
using Noname.AvatarTools.Materials;
using Noname.AvatarTools.Physics;
using Noname.AvatarTools.MeshReduction;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.Occlusion;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(Noname.AvatarTools.BlendShapeOptimization.ALTOPlugin))]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.ALTO.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.ALTO.ModularAvatar")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.ALTO.ModularAvatar.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.ALTO.AvatarOptimizer")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.ALTO.TexTransTool")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.ALTO.TexTransTool.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.ALTO.Meshia")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.ALTO.Meshia.ModularAvatar")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.ALTO.Meshia.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.ALTO.LilToon")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.ALTO.LilToon.Tests")]

namespace Noname.AvatarTools.BlendShapeOptimization
{
    internal sealed class ScopeSettings
    {
        internal Transform Root;
        internal HashSet<SkinnedMeshRenderer> OriginalRenderers;
        internal bool Mmd, ZeroReset, IgnoreUnreachableStates, RemoveIneffective, FreezeConstants;
        internal HashSet<AnimationClip> ExcludedAnimations;
        internal int Depth;
    }

    internal sealed class OptimizationState
    {
        internal readonly List<ScopeSettings> Scopes = new();
        internal readonly BlendShapeProvenance Provenance = new();
    }

    [RunsOnAllPlatforms]
    public sealed class ALTOPlugin : Plugin<ALTOPlugin>
    {
        public override string QualifiedName => "noname.alto";
        public override string DisplayName => "ALTO";

        protected override void Configure()
        {
            InPhase(BuildPhase.FirstChance).Run("Apply explicit build exclusions", BuildFilterPass.RemoveEarly);
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture parameter policy", context =>
                {
                    ParameterPolicyPass.Capture(context);
                    ParameterPolicyPass.RemoveMarkers(context);
                });
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture optimization policy", OptimizationPolicy.Capture)
                .Then.Run("Capture BlendShape optimization scopes", Capture);
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture Animator optimization settings", AnimatorOptimizationPass.Capture);
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture PhysBone filter settings", PhysBoneFilterPass.Capture);
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture Contact filter settings", ContactFilterPass.Capture);
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture object optimization settings", ObjectOptimizationPass.Capture);
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture material optimization settings", MaterialOptimizationPass.Capture);
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture material atlas settings", MaterialAtlasPass.Capture);
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture mesh reduction settings", MeshReductionPass.Capture);
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture mesh optimization settings", MeshOptimizationPass.Capture);
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture mesh renderer settings", MeshRendererSettingsOverridePass.Capture);
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture PhysBone optimization settings", PhysBoneOptimizationPass.Capture);
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture expression optimization settings", ExpressionOptimizationPass.Capture);
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture parameter layout settings", ParameterLayout.ParameterLayoutPass.Capture);
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture occlusion removal settings", OcclusionPass.Capture);
            InPhase(BuildPhase.Optimizing)
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .BeforePlugin("net.rs64.tex-trans-tool")
                .BeforePlugin("Meshia.MeshSimplification.Ndmf.Editor.NdmfPlugin")
                .Run("Remove optimization platform markers", context =>
                {
                    foreach (var filter in context.AvatarRootObject.GetComponentsInChildren<ALTOOptimizationPlatformFilter>(true))
                        UnityEngine.Object.DestroyImmediate(filter);
                })
                .Then.Run("Optimize avatar inputs before material conversion", OptimizationPipeline.BeforeAtlas);
            InPhase(BuildPhase.Optimizing).AfterPlugin("net.rs64.tex-trans-tool")
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .BeforePlugin("Meshia.MeshSimplification.Ndmf.Editor.NdmfPlugin")
                .Run("Optimize converted material outputs", OptimizationPipeline.AfterAtlas);
            InPhase(BuildPhase.Optimizing).AfterPlugin("com.anatawa12.avatar-optimizer")
                .AfterPlugin("Meshia.MeshSimplification.Ndmf.Editor.NdmfPlugin")
                .Run("Optimize outputs after external optimizers", OptimizationPipeline.AfterExternalOptimizers)
                .Then.Run("Remove BlendShape optimization markers", context =>
            {
                foreach (var marker in context.AvatarRootObject.GetComponentsInChildren<BlendShapeOptimizationExclude>(true))
                    Object.DestroyImmediate(marker);
                foreach (var component in context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeBlendShapes>(true))
                    Object.DestroyImmediate(component);
            })
                .Then.Run("Record provisional PhysBone resources", context => PhysBoneFilterPass.CheckBudget(context, final: false))
                .Then.Run("Reduce surviving meshes to the triangle budget", context =>
                    OptimizationSession.Step(context, "mesh reduction", AnalysisDomain.Meshes, MeshReductionPass.Apply))
                .Then.Run("Merge compatible surviving meshes", context =>
                    OptimizationSession.Step(context, "mesh assembly", AnalysisDomain.Hierarchy | AnalysisDomain.Meshes |
                        AnalysisDomain.Animation | AnalysisDomain.Materials, MeshOptimizationPass.Apply))
                .Then.Run("Clean up dependencies after mesh changes", OptimizationPipeline.AfterMeshes)
                .Then.Run("Remove object optimization markers", ObjectOptimizationPass.RemoveMarkers)
                .Then.Run("Record optimization analysis", AvatarBuildAnalysis.PublishReport);
            InPhase(BuildPhase.PlatformFinish).Run("Pack final atlas inputs for manual builds", MaterialAtlasPass.FinishManualBuild)
                .Then.Run("Finalize parameter layout", ParameterLayout.ParameterLayoutPass.Apply)
                .Then.Run("Fold shared immutable shader inputs", MaterialOptimizationPass.SpecializeFinal);
        }

        internal static bool IsEditorOnly(Transform transform)
        {
            for (; transform != null; transform = transform.parent)
                if (transform.CompareTag("EditorOnly")) return true;
            return false;
        }

        internal static void Capture(BuildContext context)
        {
            var state = context.GetState<OptimizationState>();
            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeBlendShapes>(true))
            {
                if (!component.enabled || !BuildPlatformFilters.IncludesSettings(component.transform, context)) continue;
                var descriptor = component.FindDescriptor();
                var root = component.ScopeRoot != null ? component.ScopeRoot : component.transform;
                if (descriptor == null || descriptor.gameObject != context.AvatarRootObject ||
                    !root.IsChildOf(context.AvatarRootTransform))
                    throw new InvalidOperationException("ALTO Trace And Optimize BlendShapes requires a scope inside its avatar.");
                int depth = 0;
                for (var parent = root; parent != context.AvatarRootTransform; parent = parent.parent) depth++;
                state.Scopes.Add(new ScopeSettings { Root = root, Depth = depth,
                    OriginalRenderers = new HashSet<SkinnedMeshRenderer>(root.GetComponentsInChildren<SkinnedMeshRenderer>(true)),
                    ExcludedAnimations = new HashSet<AnimationClip>((component.ExcludedAnimations ?? new List<AnimationClip>()).Where(c => c != null)),
                    Mmd = component.PreserveMmdBlendShapes, ZeroReset = component.OptimizeZeroResetAnimations,
                    IgnoreUnreachableStates = component.IgnoreUnreachableStates, RemoveIneffective = component.RemoveIneffectiveBlendShapes,
                    FreezeConstants = component.FreezeConstantAnimatedBlendShapes });
            }
            // An equally specific overlap has no meaningful priority. Require matching settings instead of build-order dependence.
            foreach (var group in state.Scopes.GroupBy(s => s.Root))
                if (group.Select(s => (s.Mmd, s.ZeroReset, s.IgnoreUnreachableStates, s.RemoveIneffective, s.FreezeConstants)).Distinct().Count() > 1 ||
                    group.Any(s => !s.ExcludedAnimations.SetEquals(group.First().ExcludedAnimations)))
                    throw new InvalidOperationException("BlendShape optimization components with the same scope must use matching settings.");
            if (state.Scopes.Count > 0)
            {
                var report = context.GetState<OptimizationBuildReport>();
                report.Initialize(context.AvatarRootObject, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
                using var measurement = OptimizationMeasurement.Begin(report, "capture", "BlendShape provenance");
                state.Provenance.Capture(context.AvatarRootObject, state.Scopes);
                // Capture before generators can remove or move an empty policy
                // region. Whole-avatar output permission includes those regions.
                state.Provenance.CaptureInitialPermissions(context.GetState<OptimizationPolicy>().AllowsInitialValueChanges);
                state.Provenance.CaptureNumericalPermissions(context.GetState<OptimizationPolicy>().AllowsSmallNumericalDifferences);
                measurement?.Complete();
            }
        }

        internal static void Apply(BuildContext context)
        {
            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeBlendShapes>(true))
                Object.DestroyImmediate(component);
            ApplyWithCurrentServices(context, late: false);
        }

        internal static void PrepareInputs(BuildContext context)
        {
            var state = context.GetState<OptimizationState>();
            if (state.Scopes.Count == 0) return;
            state.Provenance.CaptureInitialPermissions(context.GetState<OptimizationPolicy>().AllowsInitialValueChanges);
            state.Provenance.CaptureNumericalPermissions(context.GetState<OptimizationPolicy>().AllowsSmallNumericalDifferences);
            state.Provenance.PrepareEarly();
            state.Provenance.ReportSignatures(context.GetState<OptimizationBuildReport>().Messages);
        }

        internal static int ApplyLate(BuildContext context) => ApplyWithCurrentServices(context, late: true);

        private static int ApplyWithCurrentServices(BuildContext context, bool late)
        {
            if (!context.GetState<OptimizationState>().Provenance.HasScopedShapes) return 0;
            // Activate lazily: avatars without this component must not pay for cloning/indexing controllers.
            using var animationLease = OptimizationSession.Acquire(context);
            var services = animationLease.Services;
            var entries = services.ControllerContext.Controllers.Where(e => !(e.Key is Object owner) || owner != null)
                .ToDictionary(e => e.Key, e => e.Value);
            var policy = ParameterPolicyPass.Resolve(context, entries);
            using var boundary = context.GetState<OptimizationState>().Provenance.BeginBoundary();
            return ApplyCurrent(context.AvatarRootObject, entries, services.ObjectPathRemapper.GetVirtualToRealPathMap(),
                policy, context.GetState<OptimizationState>(), context.GetState<OptimizationPolicy>(),
                context.GetState<OptimizationBuildReport>(), context.AssetSaver.SaveAsset, late,
                context.GetState<ObjectOptimizationState>(), context.GetState<MeshOptimizationState>());
        }

        internal static int ApplyFinal(BuildContext context, IDictionary<object, VirtualAnimatorController> entries,
            ResolvedParameterPolicy policy, Action<Object> save)
            => ApplyCurrent(context.AvatarRootObject, entries, null, policy, context.GetState<OptimizationState>(),
                context.GetState<OptimizationPolicy>(), context.GetState<OptimizationBuildReport>(), save, true,
                context.GetState<ObjectOptimizationState>(), context.GetState<MeshOptimizationState>());

        internal static int ApplyCurrent(GameObject avatar, IDictionary<object, VirtualAnimatorController> entries,
            IReadOnlyDictionary<string, string> paths, ResolvedParameterPolicy policy, OptimizationState state,
            OptimizationPolicy initialization, OptimizationBuildReport report, Action<Object> save, bool late,
            ObjectOptimizationState objects = null, MeshOptimizationState meshes = null)
        {
            if (state.Scopes.Count == 0 || !state.Provenance.Captured) return 0;
            state.Provenance.CaptureInitialPermissions(t => initialization?.AllowsInitialValueChanges(t) == true);
            state.Provenance.CaptureNumericalPermissions((t, feature) => initialization?.AllowsSmallNumericalDifferences(t, feature) == true);
            if (!late) { state.Provenance.PrepareEarly(); state.Provenance.LockEarly(); }
            if (policy.Uncertain || !avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Any(r => r.sharedMesh != null && r.sharedMesh.blendShapeCount != 0))
            { state.Provenance.ReportSignatures(report.Messages); return 0; }
            var watch = Stopwatch.StartNew();
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            var eligible = new List<(SkinnedMeshRenderer Renderer, IReadOnlyDictionary<string, BlendShapeProvenance.Rule> Rules)>();
            foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null || IsEditorOnly(renderer.transform) || objects?.Protected(renderer) == true ||
                    meshes != null && (meshes.Preserved.Contains(ObjectRegistry.GetReference(renderer)) ||
                        MeshOptimizationPass.InputProtected(meshes, renderer, out _))) continue;
                if (state.Provenance.TryGet(renderer, out var rules) && rules.Values.Any(r => r != null)) eligible.Add((renderer, rules));
            }
            if (eligible.Count == 0) { state.Provenance.ReportSignatures(report.Messages); return 0; }
            string runtimeIssue = ObjectOptimizationSafety.UnsupportedRuntime(avatar);
            if (runtimeIssue != null)
            {
                // A runtime script can find skins without serialized references,
                // write their weights, or observe even empty shape interfaces.
                // The shared runtime contract excludes build-only components.
                report.Messages.Add("BlendShape optimization retained inputs: " + runtimeIssue + ".");
                return 0;
            }
            var usage = AnimationUsage.Collect(avatar, entries, paths, state.Scopes.Any(s => s.IgnoreUnreachableStates), policy.Preserved);
            var exclusions = state.Scopes.ToDictionary(s => s, s => new ExcludedAnimationMatcher(s.ExcludedAnimations));
            // Static/default morph baking also changes arithmetic consumed by
            // native full-vertex readers, even when animated freezing is off.
            var index = AvatarAnimationIndex.Create(avatar, entries, paths, policy.Preserved);
            var vertexReaderReasons = new Dictionary<SkinnedMeshRenderer, string>();
            var fullVertexReaders = index != null
                ? BlendShapeEffectAnalysis.FindFullVertexReaders(avatar, index, vertexReaderReasons) : new HashSet<SkinnedMeshRenderer>();
            var constants = index?.ConstantOutputs;
            var poseReaders = index != null ? InitialPoseReaders.Analyze(avatar, animations: index) : null;
            var plans = new List<OptimizationPlan>();
            var cancellationGroups = new Dictionary<OptimizationPlan, HashSet<ShapeGroup>>();
            foreach (var (renderer, rules) in eligible)
            {
                var names = Enumerable.Range(0, renderer.sharedMesh.blendShapeCount).Select(renderer.sharedMesh.GetBlendShapeName).ToArray();
                bool observeAll = fullVertexReaders.Contains(renderer);
                if (observeAll)
                    report.Messages.Add("BlendShape arithmetic retains native vertex inputs on '" + renderer.name + "': " + vertexReaderReasons[renderer] + ".");
                else if (poseReaders?.DeformedCullingBounds.Contains(renderer) == true)
                    report.Messages.Add("BlendShape arithmetic retains dynamic culling bounds on '" + renderer.name + "'.");
                var combined = new OptimizationPlan { Renderer = renderer, Source = renderer.sharedMesh };
                var effects = renderer.sharedMesh.isReadable && rules.Values.Any(r => r?.RemoveIneffective == true)
                    ? new BlendShapeEffectAnalysis(renderer.sharedMesh, observeAll) : null;
                var removableGroups = new HashSet<ShapeGroup>();
                cancellationGroups.Add(combined, removableGroups);
                var exported = new HashSet<string>(StringComparer.Ordinal);
                var extraProtections = state.Provenance.CaptureAdditionalProtections(renderer).ToHashSet(StringComparer.Ordinal);
                foreach (var group in rules.Where(p => p.Value != null).GroupBy(p => p.Value))
                {
                    var rule = group.Key;
                    // Runtime interfaces refer to current names/indices on their
                    // current renderer. They are not restrictions on unrelated
                    // geometry copied from an originally observed shape.
                    var preserve = ProtectedNames(renderer, descriptor, rule.PreserveMmdNames, rule.RemoveIneffective,
                        observeAll, includeMarker: false, effects: effects);
                    preserve.UnionWith(extraProtections);
                    if (rule.AuthoredProtected) preserve.UnionWith(group.Select(p => p.Key));
                    exported.UnionWith(preserve);
                    var selected = group.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
                    preserve.UnionWith(names.Where(n => !selected.Contains(n)));
                    var part = OptimizationPlan.Create(renderer, usage, rule.ZeroReset, preserve,
                        clip => rule.Scopes.All(s => exclusions[s].Matches(clip)), rule.IgnoreUnreachable, rule.RemoveIneffective,
                        observeAll, rule.FreezeConstants ? constants : null,
                        rule.AllowInitialChanges && initialization?.AllowsInitialValueChanges(renderer.transform) == true &&
                        poseReaders?.DeformedCullingBounds.Contains(renderer) != true, effects,
                        rule.AllowsNumericalDifferences(NumericalOptimization.BlendShapes) &&
                        initialization?.AllowsSmallNumericalDifferences(renderer.transform, NumericalOptimization.BlendShapes) == true &&
                        !observeAll && poseReaders?.DeformedCullingBounds.Contains(renderer) != true,
                        rule.AllowsNumericalDifferences(NumericalOptimization.AnimationOutputs) &&
                        initialization?.AllowsSmallNumericalDifferences(renderer.transform, NumericalOptimization.AnimationOutputs) == true &&
                        !observeAll && poseReaders?.DeformedCullingBounds.Contains(renderer) != true);
                    // A singleton rename does not simplify the graph and would
                    // produce a different generated name on every late iteration.
                    if (late) part.Groups.RemoveAll(g => g.Inputs.Count < 2);
                    combined.Frozen.AddRange(part.Frozen); combined.Ineffective.AddRange(part.Ineffective);
                    combined.Skipped.AddRange(part.Skipped); combined.Effects ??= part.Effects;
                    foreach (var shapeGroup in part.Groups)
                    {
                        while (combined.Groups.Any(g => g.Name == shapeGroup.Name)) shapeGroup.Name += "_";
                        combined.Groups.Add(shapeGroup);
                        if (rule.RemoveIneffective) removableGroups.Add(shapeGroup);
                    }
                }
                if (!late)
                {
                    var writtenMarker = PreserveForOtherOptimizers(renderer, exported);
                    state.Provenance.ExportProtections(renderer, writtenMarker);
                }
                plans.Add(combined);
            }
            var eyelidNames = CaptureEyelids(descriptor);
            var outputs = new List<(OptimizationPlan plan, Mesh mesh)>();
            try
            {
                foreach (var plan in plans.Where(p => p.HasChanges))
                    outputs.Add((plan, MeshOptimization.Generate(plan,
                        MeshOptimization.CreateRecipe(plan, cancellationGroups[plan].Contains))));
            }
            catch
            {
                foreach (var output in outputs) Object.DestroyImmediate(output.mesh);
                throw;
            }
            int changes = 0;
            var appliedProofs = new AppliedProofReport();
            foreach (var (plan, mesh) in outputs)
            {
                ObjectRegistry.RegisterReplacedObject(plan.Source, mesh);
                save?.Invoke(mesh);
                state.Provenance.RecordOptimization(plan, mesh);
                MeshOptimization.Apply(plan, mesh); plan.RewriteCurves();
                foreach (var shape in plan.Removed)
                    appliedProofs.Record(shape.ConsumedNumericalPermission, shape.ChangedInitialValue);
                changes += plan.Removed.Count() - plan.Groups.Count;
                if (plan.Frozen.Any(s => s.Uses.Count != 0))
                    report.Messages.Add("Baked " + plan.Frozen.Count(s => s.Uses.Count != 0) +
                        " constant animated BlendShapes on " + plan.Renderer.name + " (" +
                        plan.Frozen.Count(s => s.ChangedInitialValue) + " changed initial weights).");
                if (plan.Ineffective.Count > 0)
                    report.Messages.Add("Removed " + plan.Ineffective.Count + " ineffective BlendShapes from " +
                        UnityEditor.AnimationUtility.CalculateTransformPath(plan.Renderer.transform, avatar.transform) +
                        ": " + string.Join(", ", plan.Ineffective.Select(s => s.Name)) + ".");
            }
            if (descriptor != null && outputs.Any(o => o.plan.Renderer == descriptor.customEyeLookSettings.eyelidsSkinnedMesh))
                RestoreEyelids(descriptor, eyelidNames);
            appliedProofs.AppendTo(report.Messages, "BlendShape outputs");
            new NativeFaceTracking(descriptor).ClearMissingShapes(new HashSet<string>(outputs.SelectMany(o => o.plan.Ineffective.Select(s => s.Name))));
            if (!late || outputs.Count != 0)
                UnityEngine.Debug.Log($"[Noname BlendShapes] {outputs.Count} meshes, {usage.ClipCount} clips: " +
                    $"removed {plans.Sum(p => p.Ineffective.Count)} ineffective shapes; froze {plans.Sum(p => p.Frozen.Count)} shapes; merged {plans.Sum(p => p.Groups.Sum(g => g.Inputs.Count))} " +
                    $"zero-reset shapes into {plans.Sum(p => p.Groups.Count)}; {watch.ElapsedMilliseconds} ms.", avatar);
            if (!late)
            {
                if (usage.Reachability != null)
                    UnityEngine.Debug.Log($"[Noname BlendShapes] State analysis: {usage.Reachability.ReachableStateCount}/{usage.Reachability.StateCount} states reachable." +
                        (usage.Reachability.FallbackReason == null ? "" : " Preserved all states: " + usage.Reachability.FallbackReason), avatar);
                foreach (var plan in plans.Where(p => p.Skipped.Count > 0))
                    UnityEngine.Debug.Log($"[Noname BlendShapes] Preserved {plan.Skipped.Count} unsupported inputs on '{plan.Renderer.name}': " +
                        string.Join("; ", plan.Skipped.Take(5)), plan.Renderer);
                foreach (var clip in exclusions.Values.SelectMany(e => e.Unmatched).Distinct())
                    UnityEngine.Debug.LogWarning($"[Noname BlendShapes] Excluded animation '{clip.name}' did not match any analyzed bindings. " +
                        "It may be unused in this scope or changed by another build tool; unmatched curves are kept.", clip);
            }
            state.Provenance.ReportSignatures(report.Messages);
            return changes;
        }

        internal static HashSet<string> ProtectedNames(SkinnedMeshRenderer renderer, VRCAvatarDescriptor descriptor, bool mmd,
            bool removeIneffective = false, bool observeAllVertices = false, bool includeMarker = true,
            BlendShapeEffectAnalysis effects = null)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var marker = renderer.GetComponent<BlendShapeOptimizationExclude>();
            if (includeMarker && marker != null && marker.enabled) names.UnionWith(marker.BlendShapes);
            if (mmd) names.UnionWith(MmdMorphNames.All);
            if (descriptor != null)
            {
                var native = new NativeFaceTracking(descriptor);
                if (native.Meshes.Contains(renderer))
                {
                    if (!removeIneffective) effects = null;
                    else if (renderer.sharedMesh != null && renderer.sharedMesh.isReadable)
                        effects ??= new BlendShapeEffectAnalysis(renderer.sharedMesh, observeAllVertices);
                    foreach (string name in native.ShapeNames())
                    {
                        int shape = renderer.sharedMesh != null ? renderer.sharedMesh.GetBlendShapeIndex(name) : -1;
                        if (shape < 0 || effects == null || effects.HasEffect(shape)) names.Add(name);
                    }
                }
                if (descriptor.VisemeSkinnedMesh == renderer)
                {
                    if (descriptor.VisemeBlendShapes != null) names.UnionWith(descriptor.VisemeBlendShapes.Where(n => n != null));
                    if (!string.IsNullOrEmpty(descriptor.MouthOpenBlendShapeName)) names.Add(descriptor.MouthOpenBlendShapeName);
                }
                if (descriptor.customEyeLookSettings.eyelidsSkinnedMesh == renderer)
                    names.UnionWith(CaptureEyelids(descriptor).Where(n => n != null));
            }
            return names;
        }

        private static BlendShapeOptimizationExclude PreserveForOtherOptimizers(SkinnedMeshRenderer renderer, HashSet<string> names)
        {
            var existing = names.Where(n => renderer.sharedMesh.GetBlendShapeIndex(n) >= 0).ToList();
            if (existing.Count == 0) return null;
            var marker = renderer.GetComponent<BlendShapeOptimizationExclude>();
            if (marker == null) marker = renderer.gameObject.AddComponent<BlendShapeOptimizationExclude>();
            marker.enabled = true;
            marker.BlendShapes = existing;
            return marker;
        }

        internal static string[] CaptureEyelids(VRCAvatarDescriptor descriptor)
        {
            if (descriptor == null) return Array.Empty<string>();
            var settings = descriptor.customEyeLookSettings;
            var mesh = settings.eyelidsSkinnedMesh != null ? settings.eyelidsSkinnedMesh.sharedMesh : null;
            return (settings.eyelidsBlendshapes ?? Array.Empty<int>()).Select(i =>
                mesh != null && i >= 0 && i < mesh.blendShapeCount ? mesh.GetBlendShapeName(i) : null).ToArray();
        }

        internal static void RestoreEyelids(VRCAvatarDescriptor descriptor, string[] names)
        {
            if (descriptor == null) return;
            var settings = descriptor.customEyeLookSettings;
            if (settings.eyelidsSkinnedMesh == null || settings.eyelidsSkinnedMesh.sharedMesh == null) return;
            var mesh = settings.eyelidsSkinnedMesh.sharedMesh;
            settings.eyelidsBlendshapes = names.Select(n => n == null ? -1 : mesh.GetBlendShapeIndex(n)).ToArray();
            descriptor.customEyeLookSettings = settings;
        }
    }
}
