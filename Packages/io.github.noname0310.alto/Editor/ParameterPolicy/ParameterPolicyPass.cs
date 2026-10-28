using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.ParameterPolicy
{
    internal sealed class CapturedParameter
    {
        internal string OriginalName, FinalName, SourcePath;
        internal bool HasOwner, Referenced;
    }

    internal sealed class ParameterPolicyState
    {
        internal bool Enabled;
        internal bool BakeInitialVisibility;
        internal OptimizationBuildScope Scope;
        internal AnimatorParameterFreezeMode FreezeMode;
        internal bool FreezeResolved;
        internal readonly HashSet<string> DefaultFrozen = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, float> Frozen = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, ParameterConstant> Constants = new(StringComparer.Ordinal);
        internal readonly List<CapturedParameter> Preserved = new();
        internal readonly HashSet<string> Names = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Prefixes = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Diagnostics = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Reported = new(StringComparer.Ordinal);
        internal bool UnresolvedSource;
    }

    internal sealed class ResolvedParameterPolicy
    {
        internal readonly HashSet<string> Preserved = new(StringComparer.Ordinal);
        internal HashSet<string> PreservationRoots;
        internal IEnumerable<string> Roots => PreservationRoots ?? Preserved;
        internal bool Uncertain;
    }

    internal static class ParameterPolicyPass
    {
        internal static void Capture(BuildContext context)
        {
            var state = context.GetState<ParameterPolicyState>();
            var components = context.AvatarRootObject.GetComponentsInChildren<ALTOAnimatorParameterPolicy>(true)
                .Where(c => OptimizationBuildScope.Applies(c, context)).ToArray();
            if (components.Length > 1)
                throw new InvalidOperationException("Use one enabled ALTO Animator Parameter Policy per avatar and platform.");
            foreach (var component in components)
            {
                if (!Enum.IsDefined(typeof(AnimatorParameterFreezeMode), component.FreezeMode))
                    throw new InvalidOperationException("Choose a valid parameter Freeze Mode.");
                state.Scope = OptimizationBuildScope.Capture(component, context.AvatarRootObject);
                state.FreezeMode = component.FreezeMode;
                state.BakeInitialVisibility = component.BakeInitialVisibility;
                state.Enabled = true;
                context.GetState<OptimizationBuildReport>().Initialize(context.AvatarRootObject,
                    context.GetState<OptimizationBuildEnvironment>().Target.ToString());
                var selection = component.PreserveParameters ?? new AnimatorParameterSelection();
                state.Names.UnionWith((selection.Names ?? new()).Where(n => !string.IsNullOrEmpty(n)));
                state.Prefixes.UnionWith((selection.Prefixes ?? new()).Where(n => !string.IsNullOrEmpty(n)));
                foreach (var source in selection.Sources ?? new())
                {
                    if (source == null || source.Source == null) continue;
                    var result = ParameterSourceResolver.Resolve(source, context.AvatarRootObject, context);
                    var excluded = new HashSet<string>(source.ExcludedNames ?? new(), StringComparer.Ordinal);
                    foreach (var diagnostic in result.Diagnostics)
                        state.Diagnostics.Add(source.Source.name + ": " + diagnostic);
                    foreach (var parameter in result.Parameters.Where(p => !excluded.Contains(p.Name)))
                    {
                        var name = ResolveName(context, parameter);
                        state.Preserved.Add(new CapturedParameter
                        {
                            OriginalName = parameter.Name, FinalName = name, HasOwner = parameter.Owner != null,
                            Referenced = parameter.Referenced,
                            SourcePath = parameter.Owner != null
                                ? UnityEditor.AnimationUtility.CalculateTransformPath(parameter.Owner.transform, context.AvatarRootTransform)
                                : source.Source.name
                        });
                        if (parameter.Owner == null) state.UnresolvedSource = true;
                    }
                    if (result.Diagnostics.Count > 0 && result.Parameters.Count == 0)
                        state.UnresolvedSource = true;
                }
                foreach (var rule in component.FreezeParameters ?? new())
                {
                    if (rule == null || string.IsNullOrWhiteSpace(rule.Name))
                        throw new InvalidOperationException("Each frozen parameter needs a name. Remove unused rows.");
                    if (!rule.UseDefaultValue && !MotionAnalysis.Finite(rule.Value))
                        throw new InvalidOperationException("Frozen parameter values must be finite: " + rule.Name);
                    var name = rule.Name;
                    if (!ReferenceEquals(rule.Source, null) && rule.Source == null)
                    {
                        state.Diagnostics.Add("Skipped frozen parameter from a removed source: " + name);
                        continue;
                    }
                    if (rule.Source != null)
                    {
                        var resolved = ParameterSourceResolver.Resolve(new AnimatorParameterSource
                        { Source = rule.Source, IncludeChildren = rule.IncludeChildren }, context.AvatarRootObject, context);
                        var candidates = resolved.Parameters.Where(p => p.Name == name).ToArray();
                        var names = candidates.Select(p => ResolveName(context, p)).Distinct().ToArray();
                        if (names.Length != 1 || candidates.Any(p => p.Owner == null))
                            throw new InvalidOperationException("Cannot uniquely resolve frozen parameter '" + name +
                                "' from '" + rule.Source.name + "'. Choose its owning object or use its final name without a source.");
                        name = names[0];
                    }
                    if ((rule.UseDefaultValue && state.Frozen.ContainsKey(name)) || (!rule.UseDefaultValue && state.DefaultFrozen.Contains(name)))
                        throw new InvalidOperationException("Conflicting default/explicit freeze settings for parameter: " + name);
                    if (rule.UseDefaultValue) { state.DefaultFrozen.Add(name); continue; }
                    if (state.Frozen.TryGetValue(name, out var previous) && previous != rule.Value)
                        throw new InvalidOperationException("Conflicting constant values for parameter: " + name);
                    state.Frozen[name] = rule.Value;
                }
            }
        }

        internal static string ResolveName(BuildContext context, SourceParameter parameter)
        {
            var name = parameter.Name;
            if (parameter.Owner == null) return name;
            var mappings = nadena.dev.ndmf.ParameterInfo.ForContext(context).GetParameterRemappingsAt(parameter.Owner.gameObject);
            return mappings.TryGetValue((parameter.MappingNamespace, parameter.MappingName ?? name), out var mapping)
                ? mapping.ParameterName + parameter.MappingSuffix : name;
        }

        internal static ResolvedParameterPolicy Resolve(BuildContext context, IDictionary<object, VirtualAnimatorController> entries)
            => Resolve(context, entries.Values.Distinct().ToArray(), entries);

        internal static ResolvedParameterPolicy Resolve(BuildContext context, VirtualAnimatorController[] controllers,
            IDictionary<object, VirtualAnimatorController> entries = null)
        {
            var state = context.GetState<ParameterPolicyState>();
            var result = new ResolvedParameterPolicy();
            if (!state.Enabled) return result;
            var names = new HashSet<string>(controllers.SelectMany(c => c.Parameters.Keys), StringComparer.Ordinal);
            var descriptor = context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            if (descriptor?.expressionParameters != null)
                names.UnionWith((descriptor.expressionParameters.parameters ?? Array.Empty<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter>())
                    .Where(p => p != null).Select(p => p.name));
            result.Preserved.UnionWith(state.Names.Where(names.Contains));
            result.Preserved.UnionWith(names.Where(n => n != null && state.Prefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal))));
            result.Uncertain = state.UnresolvedSource;
            foreach (var parameter in state.Preserved)
            {
                if (names.Contains(parameter.FinalName)) result.Preserved.Add(parameter.FinalName);
                else if (parameter.Referenced)
                {
                    result.Uncertain = true;
                    state.Diagnostics.Add("Could not verify preserved parameter '" + parameter.OriginalName +
                        "' from '" + parameter.SourcePath + "' as '" + parameter.FinalName +
                        "'. Parameter-dependent optimization is skipped for this build.");
                }
            }
            foreach (var name in state.Names.Where(n => !names.Contains(n)))
            {
                result.Uncertain = true;
                state.Diagnostics.Add("Preserved parameter '" + name + "' is absent from the analyzed controllers.");
            }
            result.PreservationRoots = new(result.Preserved, StringComparer.Ordinal);
            if (result.Preserved.Count > 0)
                result.Preserved.UnionWith(AnimatorParameterDependencies.Collect(context.AvatarRootObject, controllers,
                    controllerEntries: entries).Expand(result.Preserved));
            int newDiagnostics = 0;
            foreach (var diagnostic in state.Diagnostics.OrderBy(d => d, StringComparer.Ordinal))
                if (state.Reported.Add(diagnostic))
                {
                    context.GetState<OptimizationBuildReport>().Messages.Add(diagnostic);
                    if (++newDiagnostics <= 5)
                        Debug.LogWarning("[ALTO] " + diagnostic, context.AvatarRootObject);
                }
            if (newDiagnostics > 5)
                Debug.LogWarning("[ALTO] " + (newDiagnostics - 5) +
                    " additional parameter-policy diagnostics are stored in the build report.", context.AvatarRootObject);
            var summary = result.Preserved.Count + " preserved parameters (including dependencies); " +
                (result.Uncertain ? "unresolved source mapping." : "source mapping verified.");
            var buildReport = context.GetState<OptimizationBuildReport>();
            buildReport.PreservedParameters.Clear();
            buildReport.PreservedParameters.AddRange(result.Preserved.OrderBy(n => n, StringComparer.Ordinal));
            buildReport.ParameterMappings.Clear();
            buildReport.ParameterMappings.AddRange(state.Preserved.Select(p =>
                p.SourcePath + ": " + p.OriginalName + " -> " + p.FinalName).Distinct().OrderBy(n => n, StringComparer.Ordinal));
            if (state.Reported.Add(summary))
            {
                context.GetState<OptimizationBuildReport>().Messages.Add(summary);
                Debug.Log("[ALTO] Parameter policy: " + summary, context.AvatarRootObject);
            }
            return result;
        }

        internal static void RemoveMarkers(BuildContext context)
        {
            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<ALTOAnimatorParameterPolicy>(true))
                UnityEngine.Object.DestroyImmediate(component);
        }
    }
}
