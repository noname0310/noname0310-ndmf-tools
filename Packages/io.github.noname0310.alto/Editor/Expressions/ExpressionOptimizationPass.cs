using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Expressions
{
    internal sealed class ExpressionOptimizationState
    {
        internal ExpressionSettings Settings;
    }

    internal static class ExpressionOptimizationPass
    {
        internal static void Capture(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOTraceAndOptimizeExpressions>(true);
            var enabled = all.Where(c => c.enabled && !ALTOPlugin.IsEditorOnly(c.transform) &&
                BuildPlatformFilters.IncludesSettings(c.transform, context)).ToArray();
            if (enabled.Length > 1) throw new InvalidOperationException("Use one enabled ALTO Trace And Optimize Expressions per avatar.");
            if (enabled.Length == 1)
            {
                if (enabled[0].FindDescriptor()?.gameObject != context.AvatarRootObject)
                    throw new InvalidOperationException("ALTO Trace And Optimize Expressions requires a VRC Avatar Descriptor ancestor.");
                context.GetState<ExpressionOptimizationState>().Settings = ExpressionSettings.Capture(enabled[0]);
                context.GetState<OptimizationBuildReport>().Initialize(context.AvatarRootObject,
                    context.GetState<OptimizationBuildEnvironment>().Target.ToString());
            }
            foreach (var component in all) Object.DestroyImmediate(component);
        }

        internal static void Apply(BuildContext context)
        {
            var settings = context.GetState<ExpressionOptimizationState>().Settings;
            if (settings == null) return;
            var buildReport = context.GetState<OptimizationBuildReport>();
            var animationLease = Noname.AvatarTools.Build.OptimizationSession.Acquire(context);
            var services = animationLease.Services;
            try
            {
                var entries = services.ControllerContext.Controllers.Where(e => !(e.Key is Object owner) || owner != null).ToDictionary(e => e.Key, e => e.Value);
                var policy = ParameterPolicyPass.Resolve(context, entries);
                var frozen = new HashSet<string>(context.GetState<ParameterPolicyState>().Constants.Keys, StringComparer.Ordinal);
                // SDK preprocessors such as parameter compressors inspect menus after NDMF. Leave
                // both assets intact until they finish, then analyze their final controllers too.
                if (AvatarOutputSdkFinalizer.Defer(context, settings, frozen, buildReport)) return;
                ApplyToAvatar(context.AvatarRootObject, settings, policy, frozen, entries,
                    services.ObjectPathRemapper.GetVirtualToRealPathMap(), buildReport, context.AssetSaver.SaveAsset, null,
                    context.GetState<ObjectOptimizationState>(), context.GetState<AnimatorOptimizationState>().Settings);
            }
            finally
            {
                animationLease.Dispose();
            }
        }

        internal static int ApplyToAvatar(GameObject avatar, ExpressionSettings settings, ResolvedParameterPolicy policy,
            ISet<string> frozen, IDictionary<object, VirtualAnimatorController> entries, IReadOnlyDictionary<string, string> paths,
            OptimizationBuildReport buildReport, Action<Object> save, ISet<string> localCandidates = null,
            ObjectOptimizationState objects = null, AnimatorOptimizationSettings animator = null)
        {
            var reports = new List<ExpressionOptimizationReport>();
            int changes, iterations = 0, total = 0;
            do
            {
                iterations++;
                changes = ApplyIteration(avatar, settings, policy, frozen, entries, paths, buildReport, save,
                    localCandidates, objects, animator);
                reports.Add(buildReport.Expressions);
                total += changes;
            } while (changes != 0);
            MergeReports(reports, buildReport);
            if (iterations > 1)
                buildReport.Messages.Add("Expression and sensor dependency analysis converged after " + iterations + " iterations.");
            return total;
        }

        internal static void MergeReports(IReadOnlyList<ExpressionOptimizationReport> reports, OptimizationBuildReport buildReport)
        {
            if (reports.Count <= 1) return;
            var result = reports.Last();
            result.InputMenus = reports[0].InputMenus;
            result.InputControls = reports[0].InputControls;
            result.InputParameters = reports[0].InputParameters;
            result.Milliseconds = reports.Sum(r => r.Milliseconds);
            result.RemovedLocalParameters = reports.SelectMany(r => r.RemovedLocalParameters).Distinct().ToList();
            // Retained decisions come from the final graph; keep earlier removal reasons.
            result.Controls = reports.Take(reports.Count - 1).SelectMany(r => r.Controls).Where(c => c.Removed)
                .Concat(result.Controls).ToList();
        }

        private static int ApplyIteration(GameObject avatar, ExpressionSettings settings, ResolvedParameterPolicy policy,
            ISet<string> frozen, IDictionary<object, VirtualAnimatorController> entries, IReadOnlyDictionary<string, string> paths,
            OptimizationBuildReport buildReport, Action<Object> save, ISet<string> localCandidates,
            ObjectOptimizationState objects, AnimatorOptimizationSettings animator)
        {
            var watch = Stopwatch.StartNew();
            var report = new ExpressionOptimizationReport(); buildReport.Expressions = report;
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            var menus = ExpressionMenuOptimizer.Menus(descriptor.expressionsMenu).ToArray();
            report.InputMenus = report.OutputMenus = menus.Length;
            report.InputControls = report.OutputControls = menus.Sum(m => m.controls?.Count ?? 0);
            report.InputParameters = report.OutputParameters = descriptor.expressionParameters?.parameters?.Length ?? 0;
            report.SyncedParameters = descriptor.expressionParameters?.parameters?.Count(p => p != null && p.networkSynced) ?? 0;
            if (!settings.Enabled) { report.SkippedReason = "all cleanup options are disabled"; return 0; }
            try
            {
                var controllers = entries.Values.Distinct().ToArray();
                var external = ExternalParameterAccess.Collect();
                var controls = menus.SelectMany(m => m.controls ?? new()).Where(c => c != null).ToArray();
                var present = new HashSet<(string, VRCExpressionsMenu.Control.ControlType)>(controls.Select(ExpressionSettings.Key));
                string reason = settings.InvalidProtection ?? ObjectOptimizationSafety.UnsupportedRuntime(avatar);
                if (reason == null && policy.Uncertain) reason = "parameter preservation mapping is uncertain";
                if (reason == null && external.Uncertain) reason = string.Join("; ", external.Diagnostics);
                if (reason == null && settings.Protected.Any(k => !present.Contains(k)))
                    reason = "a preserved source control could not be matched after menu generation";
                var analysis = reason == null ? AnimatorReachability.Analyze(avatar, controllers, policy.Preserved) : null;
                if (analysis != null) reason = analysis.FallbackReason;
                if (reason == null && controllers.SelectMany(c => c.AllReachableNodes()).OfType<VirtualClip>()
                    .Any(c => MotionAnalysis.Source(c)?.events.Length > 0)) reason = "animation events may consume parameters outside the indexed graph";
                if (reason != null)
                {
                    report.SkippedReason = reason;
                    buildReport.Messages.Add("Expression cleanup skipped: " + reason);
                    return 0;
                }

                var outputs = new ExpressionOutputUsage(avatar, entries, paths);
                var graph = AnimatorParameterDependencies.Collect(avatar, controllers, true, outputs.IsObservable, analysis, entries);
                var roots = new HashSet<string>(StringComparer.Ordinal);
                roots.UnionWith(policy.Roots);
                roots.UnionWith(controls.Where(c => settings.Protected.Contains(ExpressionSettings.Key(c))).SelectMany(ExpressionMenuOptimizer.Parameters));
                var names = controllers.SelectMany(c => c.Parameters.Keys).Concat(controls.SelectMany(ExpressionMenuOptimizer.Parameters))
                    .Concat((descriptor.expressionParameters?.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>()).Where(p => p != null).Select(p => p.name))
                    .Where(n => !string.IsNullOrEmpty(n)).Distinct().ToArray();
                roots.UnionWith(names.Where(n => external.IsRead(n) || external.IsWritten(n)));
                var live = graph.ExpandObservable(roots);
                descriptor.expressionsMenu = ExpressionMenuOptimizer.Optimize(descriptor.expressionsMenu, settings, live, frozen, report,
                    save, (source, copy) => ObjectRegistry.RegisterReplacedObject(source, copy));

                int changes = 0;
                if (settings.LocalParameters && descriptor.expressionParameters != null)
                {
                    var keep = new HashSet<string>(live, StringComparer.Ordinal);
                    keep.UnionWith(ExpressionMenuOptimizer.Menus(descriptor.expressionsMenu).SelectMany(m => m.controls ?? new()).SelectMany(ExpressionMenuOptimizer.Parameters));
                    if (objects?.Scope != null && objects.PruneUnusedSensorParameters)
                    {
                        var locals = new HashSet<string>((descriptor.expressionParameters.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>())
                            .Where(p => p != null && !p.networkSynced && !keep.Contains(p.name) &&
                                (localCandidates == null || localCandidates.Contains(p.name))).Select(p => p.name), StringComparer.Ordinal);
                        changes += SensorParameterPruning.Apply(avatar, controllers, objects, policy.Preserved, external,
                            buildReport.Messages, locals, frozen);
                        if (objects.RemoveUnusedTransforms)
                            changes += UnusedTransformOutputs.Apply(avatar, entries, paths, objects, policy.Preserved, buildReport.Messages);
                        if (objects.RemoveUnusedObjects)
                            changes += UnusedObjectCollection.Apply(avatar, entries, paths, objects, policy.Preserved, buildReport.Messages);
                        if (animator?.Enabled == true)
                        {
                            var additive = new HashSet<VirtualAnimatorController>(entries.Where(e =>
                                e.Key is VRCAvatarDescriptor.AnimLayerType type && type == VRCAvatarDescriptor.AnimLayerType.Additive).Select(e => e.Value));
                            var hierarchy = new ExpressionOutputUsage(avatar, entries, paths);
                            var optimized = AnimatorOptimizer.Optimize(avatar, controllers, animator, additive,
                                AnimatorReachability.Analyze(avatar, controllers, policy.Preserved), policy.Preserved,
                                animator.StateLogic ? hierarchy.IsObservable : null, policy.Roots, entries);
                            changes += optimized.Changes;
                            buildReport.Messages.Add("Animator after expression cleanup: " + optimized);
                        }
                    }
                    // A retained sensor is still observable, including its result-transform
                    // consumers, protected components and declarations exposed by late builders.
                    keep.UnionWith(AvatarParameterInputs.Sensors(avatar).Select(s => s.Name));
                    PruneLocalParameters(descriptor, keep, report, save, localCandidates);
                }
                report.Applied = true;
                // Menu and declaration removal can release consumers in the next graph.
                // Include these edits so callers can rely on the completed fixed point.
                return changes + report.InputControls - report.OutputControls +
                    report.InputMenus - report.OutputMenus + report.RemovedLocalParameters.Count;
            }
            finally
            {
                report.Milliseconds = watch.ElapsedMilliseconds;
                buildReport.Messages.Add($"Expressions: controls {report.InputControls} -> {report.OutputControls}, menus {report.InputMenus} -> {report.OutputMenus}; " +
                    $"removed {report.RemovedLocalParameters.Count} local parameters; {report.SyncedParameters} synchronized declarations retained; {report.Milliseconds} ms.");
            }
        }

        internal static void PruneLocalParameters(VRCAvatarDescriptor descriptor, ISet<string> keep,
            ExpressionOptimizationReport report, Action<Object> save, ISet<string> candidates = null)
        {
            var source = descriptor.expressionParameters;
            if (source == null || source.parameters == null) return;
            bool Retain(VRCExpressionParameters.Parameter p) => p == null || string.IsNullOrEmpty(p.name) || p.networkSynced ||
                keep.Contains(p.name) || candidates != null && !candidates.Contains(p.name);
            var removed = source.parameters.Where(p => !Retain(p)).ToArray();
            if (removed.Length == 0) return;
            var copy = Object.Instantiate(source); copy.name = source.name;
            copy.parameters = copy.parameters.Where(Retain).ToArray();
            save(copy); ObjectRegistry.RegisterReplacedObject(source, copy); descriptor.expressionParameters = copy;
            report.OutputParameters = copy.parameters.Length;
            report.RemovedLocalParameters.AddRange(removed.Select(p => p.name));
        }
    }
}
