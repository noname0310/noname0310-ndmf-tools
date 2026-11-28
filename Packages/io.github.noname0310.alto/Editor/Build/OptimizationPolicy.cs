using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    internal sealed class OptimizationPolicy
    {
        private readonly List<(OptimizationBuildScope Scope, int Depth, bool AllowInitial, NumericalOptimization Rounding, bool ShaderFolding)> scopes = new();
        private readonly Dictionary<Transform, NumericalOptimization> inheritedPermissions = new();
        private readonly Dictionary<Transform, (bool AllowInitial, NumericalOptimization Rounding, bool ShaderFolding)> authoredPermissions = new();
        private readonly Dictionary<Transform, (bool AllowInitial, NumericalOptimization Rounding, bool ShaderFolding)> scopeRoots = new();
        private readonly HashSet<Transform> inheritedStrictShaderFolding = new();
        private NumericalOptimization strictFeatures;
        internal bool HasStrictNumericalConstraints => strictFeatures != NumericalOptimization.None;
        internal bool HasStrictNumericalConstraintsFor(NumericalOptimization feature) => (strictFeatures & feature) != 0;

        internal static void Capture(BuildContext context)
        {
            var state = context.GetState<OptimizationPolicy>();
            foreach (var component in context.AvatarRootObject.GetComponentsInChildren<ALTOOptimizationPolicy>(true))
            {
                if (OptimizationBuildScope.Applies(component, context))
                {
                    var scope = OptimizationBuildScope.Capture(component, context.AvatarRootObject);
                    int depth = 0;
                    for (var current = scope.Root; current != null; current = current.parent) depth++;
                    foreach (var previous in state.scopes)
                        if (previous.Scope.Root == scope.Root && (previous.AllowInitial != component.AllowInitialValueChanges ||
                            previous.Rounding != component.AllowedNumericalDifferences || previous.ShaderFolding != component.AllowShaderConstantFolding))
                            throw BuildDiagnostics.Validation("Optimization Policies with the same scope must agree.");
                    state.scopes.Add((scope, depth, component.AllowInitialValueChanges, component.AllowedNumericalDifferences, component.AllowShaderConstantFolding));
                    state.scopeRoots[scope.Root] = (component.AllowInitialValueChanges, component.AllowedNumericalDifferences, component.AllowShaderConstantFolding);
                    state.strictFeatures |= NumericalOptimization.All & ~component.AllowedNumericalDifferences;
                }
                UnityEngine.Object.DestroyImmediate(component);
            }
            foreach (var transform in context.AvatarRootObject.GetComponentsInChildren<Transform>(true))
                if (!state.authoredPermissions.ContainsKey(transform))
                    state.authoredPermissions.Add(transform, state.ResolveAuthoredScope(transform));
        }

        private (bool AllowInitial, NumericalOptimization Rounding, bool ShaderFolding) ResolveAuthoredScope(Transform target)
        {
            int depth = -1; bool initial = false, shaderFolding = true; var rounding = NumericalOptimization.All;
            foreach (var scope in scopes)
                if (scope.Depth > depth && scope.Scope.OriginalTransforms.Contains(target))
                { depth = scope.Depth; initial = scope.AllowInitial; rounding = scope.Rounding; shaderFolding = scope.ShaderFolding; }
            return (initial, rounding, shaderFolding);
        }

        private (bool AllowInitial, NumericalOptimization Rounding, bool ShaderFolding) ResolveCurrentScope(Transform target)
        {
            // New outputs use actual live ancestry: captured depths are no
            // longer comparable after a generator relocates a scope root.
            for (var current = target; current != null; current = current.parent)
                if (scopeRoots.TryGetValue(current, out var permission)) return permission;
            return (false, NumericalOptimization.All, true);
        }

        private (bool AllowInitial, NumericalOptimization Rounding, bool ShaderFolding) Permissions(Transform target) =>
            target != null && authoredPermissions.TryGetValue(target, out var original)
                ? original : ResolveCurrentScope(target);

        internal bool AllowsInitialValueChanges(Transform target) => Permissions(target).AllowInitial;

        internal bool AllowsShaderConstantFolding(Transform target) => Permissions(target).ShaderFolding &&
            (target == null || !inheritedStrictShaderFolding.Contains(target));

        internal NumericalOptimization NumericalPermissionsFor(Transform target)
        {
            // Rounding equivalence is the baseline ALTO contract, including
            // existing avatars which do not contain an explicit policy.
            var allowed = Permissions(target).Rounding & NumericalOptimization.All;
            return target != null && inheritedPermissions.TryGetValue(target, out var inherited) ? allowed & inherited : allowed;
        }

        internal bool AllowsSmallNumericalDifferences(Transform target, NumericalOptimization feature) =>
            (NumericalPermissionsFor(target) & feature) == feature;

        // Call before destroying contributors. A shared output cannot discard a
        // stricter source contract merely because its surviving owner is relaxed.
        internal void InheritNumericalConstraints(Transform output, IEnumerable<Transform> contributors)
        {
            if (output == null) return;
            var allowed = NumericalPermissionsFor(output);
            bool shaderFolding = AllowsShaderConstantFolding(output);
            foreach (var contributor in contributors)
            {
                allowed &= contributor == null ? NumericalOptimization.None : NumericalPermissionsFor(contributor);
                shaderFolding &= contributor != null && AllowsShaderConstantFolding(contributor);
            }
            if (!shaderFolding) inheritedStrictShaderFolding.Add(output);
            if (allowed != NumericalOptimization.All)
            {
                inheritedPermissions[output] = allowed;
                strictFeatures |= NumericalOptimization.All & ~allowed;
            }
        }
    }
}
