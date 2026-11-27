using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ParameterPolicy;
using Noname.AvatarTools.Build;

namespace Noname.AvatarTools.AnimatorOptimization
{
    internal sealed class AnimatorOptimizationState
    {
        internal AnimatorOptimizationSettings Settings;
        internal readonly List<AnimatorOptimizationReport> Reports = new();
    }

    internal static class AnimatorOptimizationPass
    {
        internal static void Capture(BuildContext context)
        {
            var state = context.GetState<AnimatorOptimizationState>();
            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeAnimator>(true))
            {
                if (!component.enabled || !BuildPlatformFilters.IncludesSettings(component.transform, context)) continue;
                if (component.FindDescriptor()?.gameObject != context.AvatarRootObject)
                    throw new InvalidOperationException("ALTO Trace And Optimize Animator requires a VRC Avatar Descriptor ancestor.");
                var settings = AnimatorOptimizationSettings.From(component);
                if (state.Settings != null && !state.Settings.Same(settings))
                    throw new InvalidOperationException("Animator optimization components in the same avatar must use matching settings.");
                state.Settings = settings;
            }
            if (state.Settings != null && state.Settings.Enabled)
                context.GetState<OptimizationBuildReport>().Initialize(context.AvatarRootObject,
                    context.GetState<OptimizationBuildEnvironment>().Target.ToString());
        }

        internal static int ApplyPreparation(BuildContext context, bool inspectMaterials) => Apply(context, inspectMaterials);

        internal static int ApplyFinalAndCountChanges(BuildContext context)
            => Apply(context, true, true);

        private static int Apply(BuildContext context, bool inspectMaterials, bool finalHierarchy = false)
        {
            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeAnimator>(true))
                UnityEngine.Object.DestroyImmediate(component);
            var state = context.GetState<AnimatorOptimizationState>();
            if (state.Settings == null || !state.Settings.Enabled) return 0;
            var watch = Stopwatch.StartNew();
            var animationLease = Noname.AvatarTools.Build.OptimizationSession.Acquire(context);
            var services = animationLease.Services;
            try
            {
                var entries = services.ControllerContext.Controllers.Where(e => !(e.Key is UnityEngine.Object owner) || owner != null).ToArray();
                var controllers = entries.Select(e => e.Value).Distinct().ToArray();
                ObjectOptimization.ObjectOptimizationPass.CaptureConsumers(context, entries);
                var additive = new HashSet<VirtualAnimatorController>(entries.Where(e => e.Key is VRCAvatarDescriptor.AnimLayerType type &&
                    type == VRCAvatarDescriptor.AnimLayerType.Additive).Select(e => e.Value));
                var ownership = entries.ToDictionary(e => e.Key, e => e.Value);
                var policy = ParameterPolicyPass.Resolve(context, ownership);
                if (policy.Uncertain) return 0;
                var analysis = AnimatorReachability.Analyze(context.AvatarRootObject, controllers, policy.Preserved);
                Func<VirtualClip, UnityEditor.EditorCurveBinding, bool> observable = null;
                MaterialOutputOptimizationReport materialReport = null;
                if (state.Settings.MaterialOutputs && inspectMaterials)
                {
                    var outputReport = new MaterialOutputOptimizationReport
                    {
                        SkippedReason = analysis.FallbackReason ?? ObjectOptimization.ObjectOptimizationSafety.UnsupportedRuntime(context.AvatarRootObject)
                    };
                    materialReport = outputReport;
                    var buildReport = context.GetState<OptimizationBuildReport>();
                    buildReport.MaterialOutputs.Add(outputReport);
                    if (outputReport.SkippedReason == null)
                    {
                        var materialWatch = Stopwatch.StartNew();
                        var paths = services.ObjectPathRemapper.GetVirtualToRealPathMap();
                        var usage = new MaterialBindingUsage(context.AvatarRootObject, entries.ToDictionary(e => e.Key, e => e.Value), paths,
                            context.GetState<OptimizationAnalysis>().Shaders);
                        MaterialOutputPruning.Apply(controllers, usage, outputReport);
                        var hierarchy = new Expressions.ExpressionOutputUsage(context.AvatarRootObject, entries.ToDictionary(e => e.Key, e => e.Value), paths);
                        // Retained curves remain observable, including outputs protected by the
                        // parameter policy. Only our missing duration placeholders are ignored.
                        observable = (clip, binding) => !(binding.path ?? "").StartsWith("__advanced_optimizer_duration_", StringComparison.Ordinal) ||
                            hierarchy.IsObservable(clip, binding);
                        outputReport.Milliseconds = materialWatch.ElapsedMilliseconds;
                    }
                    buildReport.Messages.Add(outputReport.SkippedReason != null ? "Unused material output analysis skipped: " + outputReport.SkippedReason :
                        $"Removed {outputReport.RemovedCurves} unused material-property curves ({outputReport.ShaderSourcesRead} shader source files); {outputReport.Milliseconds} ms.");
                }
                if (state.Settings.StateLogic && analysis.FallbackReason == null)
                {
                    var hierarchy = new Expressions.ExpressionOutputUsage(context.AvatarRootObject, entries.ToDictionary(e => e.Key, e => e.Value),
                        services.ObjectPathRemapper.GetVirtualToRealPathMap());
                    // General missing bindings are only unobservable after all hierarchy builders
                    // have finished. Earlier passes ignore only our own duration placeholders.
                    observable = (clip, binding) => !finalHierarchy &&
                        !(binding.path ?? "").StartsWith("__advanced_optimizer_duration_", StringComparison.Ordinal) || hierarchy.IsObservable(clip, binding);
                }
                var rounding = new AnimationOutputRounding(context.AvatarRootObject, ownership,
                    services.ObjectPathRemapper.GetVirtualToRealPathMap(), context.GetState<OptimizationPolicy>(), policy.Preserved);
                var report = AnimatorOptimizer.Optimize(context.AvatarRootObject, controllers, state.Settings, additive, analysis, policy.Preserved,
                    observable, policy.Roots, ownership, allowNumericalDifferences: rounding.Allows);
                if (materialReport != null && materialReport.SkippedReason == null)
                {
                    materialReport.RemovedParameterCurves = report.ParameterCurves;
                    materialReport.RemovedDriverOperations = report.DriverOperations;
                    materialReport.RemovedBranches = report.Branches;
                }
                BakeMaterialOutputs(context.AvatarRootObject, ownership, services.ObjectPathRemapper.GetVirtualToRealPathMap(),
                    policy.Preserved, state.Settings, report, context.GetState<OptimizationPolicy>(),
                    context.GetState<ObjectOptimization.ObjectOptimizationState>(), context.GetState<Materials.MaterialOptimizationState>(),
                    material => context.AssetSaver.SaveAsset(material));
                state.Reports.Add(report);
                context.GetState<OptimizationBuildReport>().Messages.Add("Animator graph optimization: " + report + "; " + watch.ElapsedMilliseconds + " ms.");
                UnityEngine.Debug.Log($"[ALTO] Animator: {report}; {watch.ElapsedMilliseconds} ms.", context.AvatarRootObject);
                return report.Changes + (materialReport?.RemovedCurves ?? 0);
            }
            finally
            {
                animationLease.Dispose();
            }
        }

        internal static void BakeMaterialOutputs(GameObject avatar, IDictionary<object, VirtualAnimatorController> entries,
            IReadOnlyDictionary<string, string> paths, IEnumerable<string> preservedInputs,
            AnimatorOptimizationSettings settings, AnimatorOptimizationReport report, OptimizationPolicy initialization,
            ObjectOptimization.ObjectOptimizationState objects, Materials.MaterialOptimizationState materials,
            Action<Material> save)
        {
            if (report.Fallback != null) return;
            bool Allow(Renderer renderer) => initialization?.AllowsInitialValueChanges(renderer.transform) == true;
            bool Preserved(Renderer renderer) => objects?.Protected(renderer) == true;
            if (settings.MaterialSlots)
            {
                var index = AvatarAnimationIndex.Create(avatar, entries, paths, preservedInputs);
                ConstantMaterialSlots.Apply(index, Allow, Preserved, report);
            }
            // The graph and material set may have changed above or in AnimatorOptimizer.
            // MaterialConstants takes a fresh snapshot instead of reusing stale reachability.
            if (settings.Materials)
                MaterialConstants.Optimize(avatar, entries, paths, save, report, null, Allow, Preserved,
                    m => materials?.Preserved.Contains(ObjectRegistry.GetReference(m)) == true, preservedInputs);
        }
    }
}
