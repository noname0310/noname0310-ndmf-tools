using System;
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
        internal bool Mmd, ZeroReset, IgnoreUnreachableStates;
        internal HashSet<AnimationClip> ExcludedAnimations;
        internal int Depth;
    }

    internal sealed class OptimizationState
    {
        internal readonly List<ScopeSettings> Scopes = new();
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
                    ParameterLayout.ParameterCompressionPass.CapturePolicyPreservation(context);
                    ParameterPolicyPass.Capture(context);
                    ParameterPolicyPass.RemoveMarkers(context);
                });
            InPhase(BuildPhase.Resolving).AfterPlugin("nadena.dev.modular-avatar")
                .Run("Capture BlendShape optimization scopes", Capture);
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
                .Then.Run("Optimize avatar inputs and prepare atlases", OptimizationPipeline.BeforeAtlas);
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
                .Then.Run("Check PhysBone resource budgets", PhysBoneFilterPass.CheckBudget)
                .Then.Run("Reduce surviving meshes to the triangle budget", context =>
                    OptimizationSession.Step(context, "mesh reduction", AnalysisDomain.Meshes, MeshReductionPass.Apply))
                .Then.Run("Merge compatible surviving meshes", context =>
                    OptimizationSession.Step(context, "mesh assembly", AnalysisDomain.Hierarchy | AnalysisDomain.Meshes |
                        AnalysisDomain.Animation | AnalysisDomain.Materials, MeshOptimizationPass.Apply))
                .Then.Run("Clean up dependencies after mesh changes", OptimizationPipeline.AfterMeshes)
                .Then.Run("Remove object optimization markers", ObjectOptimizationPass.RemoveMarkers)
                .Then.Run("Record optimization analysis", AvatarBuildAnalysis.PublishReport);
            InPhase(BuildPhase.PlatformFinish).Run("Finalize parameter layout", ParameterLayout.ParameterLayoutPass.Apply);
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
                    IgnoreUnreachableStates = component.IgnoreUnreachableStates });
            }
            // An equally specific overlap has no meaningful priority. Require matching settings instead of build-order dependence.
            foreach (var group in state.Scopes.GroupBy(s => s.Root))
                if (group.Select(s => (s.Mmd, s.ZeroReset, s.IgnoreUnreachableStates)).Distinct().Count() > 1 ||
                    group.Any(s => !s.ExcludedAnimations.SetEquals(group.First().ExcludedAnimations)))
                    throw new InvalidOperationException("BlendShape optimization components with the same scope must use matching settings.");
            if (state.Scopes.Count > 0)
                context.GetState<OptimizationBuildReport>().Initialize(context.AvatarRootObject,
                    context.GetState<OptimizationBuildEnvironment>().Target.ToString());
        }

        internal static void Apply(BuildContext context)
        {
            var scopes = context.GetState<OptimizationState>().Scopes;
            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeBlendShapes>(true))
                Object.DestroyImmediate(component);
            if (scopes.Count == 0) return;
            var watch = Stopwatch.StartNew();
            // Activate lazily: avatars without this component must not pay for cloning/indexing controllers.
            var animationLease = Noname.AvatarTools.Build.OptimizationSession.Acquire(context);
            var services = animationLease.Services;
            try
            {
                var descriptor = context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
                var policy = ParameterPolicyPass.Resolve(context, services.ControllerContext.Controllers
                    .Where(e => !(e.Key is UnityEngine.Object owner) || owner != null).ToDictionary(e => e.Key, e => e.Value));
                if (policy.Uncertain) return;
                var usage = AnimationUsage.Collect(context.AvatarRootObject, services.ControllerContext,
                    services.ObjectPathRemapper.GetVirtualToRealPathMap(), scopes.Any(s => s.IgnoreUnreachableStates), policy.Preserved);
                var exclusions = scopes.ToDictionary(s => s, s => new ExcludedAnimationMatcher(s.ExcludedAnimations));
                var plans = new List<OptimizationPlan>();
                foreach (var renderer in context.AvatarRootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (renderer.sharedMesh == null || IsEditorOnly(renderer.transform)) continue;
                    var scope = scopes.Where(s => s.OriginalRenderers.Contains(renderer) ||
                        (s.Root != null && renderer.transform.IsChildOf(s.Root))).OrderByDescending(s => s.Depth).FirstOrDefault();
                    if (scope == null) continue;
                    var preserve = ProtectedNames(renderer, descriptor, scope.Mmd);
                    PreserveForOtherOptimizers(renderer, preserve);
                    plans.Add(OptimizationPlan.Create(renderer, usage, scope.ZeroReset, preserve, exclusions[scope].Matches,
                        scope.IgnoreUnreachableStates));
                }
                var eyelidNames = CaptureEyelids(descriptor);
                var outputs = new List<(OptimizationPlan plan, Mesh mesh)>();
                try
                {
                    foreach (var plan in plans.Where(p => p.HasChanges))
                        outputs.Add((plan, MeshOptimization.Generate(plan, MeshOptimization.CreateRecipe(plan))));
                }
                catch
                {
                    foreach (var output in outputs) Object.DestroyImmediate(output.mesh);
                    throw;
                }
                foreach (var (plan, mesh) in outputs)
                {
                    ObjectRegistry.RegisterReplacedObject(plan.Source, mesh);
                    context.AssetSaver.SaveAsset(mesh);
                    MeshOptimization.Apply(plan, mesh);
                    plan.RewriteCurves();
                }
                if (descriptor != null && outputs.Any(o => o.plan.Renderer == descriptor.customEyeLookSettings.eyelidsSkinnedMesh))
                    RestoreEyelids(descriptor, eyelidNames);
                UnityEngine.Debug.Log($"[Noname BlendShapes] {outputs.Count} meshes, {usage.ClipCount} clips: " +
                    $"froze {plans.Sum(p => p.Frozen.Count)} shapes; merged {plans.Sum(p => p.Groups.Sum(g => g.Inputs.Count))} " +
                    $"zero-reset shapes into {plans.Sum(p => p.Groups.Count)}; " +
                    $"{watch.ElapsedMilliseconds} ms.", context.AvatarRootObject);
                if (usage.Reachability != null)
                    UnityEngine.Debug.Log($"[Noname BlendShapes] State analysis: {usage.Reachability.ReachableStateCount}/{usage.Reachability.StateCount} states reachable." +
                        (usage.Reachability.FallbackReason == null ? "" : " Preserved all states: " + usage.Reachability.FallbackReason), context.AvatarRootObject);
                foreach (var plan in plans.Where(p => p.Skipped.Count > 0))
                    UnityEngine.Debug.Log($"[Noname BlendShapes] Preserved {plan.Skipped.Count} unsupported inputs on '{plan.Renderer.name}': " +
                        string.Join("; ", plan.Skipped.Take(5)), plan.Renderer);
                foreach (var clip in exclusions.Values.SelectMany(e => e.Unmatched).Distinct())
                    UnityEngine.Debug.LogWarning($"[Noname BlendShapes] Excluded animation '{clip.name}' did not match any analyzed bindings. " +
                        "It may be unused in this scope or changed by another build tool; unmatched curves are kept.", clip);
            }
            finally
            {
                animationLease.Dispose();
            }
        }

        internal static HashSet<string> ProtectedNames(SkinnedMeshRenderer renderer, VRCAvatarDescriptor descriptor, bool mmd)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var marker = renderer.GetComponent<BlendShapeOptimizationExclude>();
            if (marker != null && marker.enabled) names.UnionWith(marker.BlendShapes);
            if (mmd) names.UnionWith(MmdMorphNames.All);
            if (descriptor != null)
            {
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

        private static void PreserveForOtherOptimizers(SkinnedMeshRenderer renderer, HashSet<string> names)
        {
            var existing = names.Where(n => renderer.sharedMesh.GetBlendShapeIndex(n) >= 0).ToList();
            if (existing.Count == 0) return;
            var marker = renderer.GetComponent<BlendShapeOptimizationExclude>();
            if (marker == null) marker = renderer.gameObject.AddComponent<BlendShapeOptimizationExclude>();
            marker.enabled = true;
            marker.BlendShapes = existing;
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
