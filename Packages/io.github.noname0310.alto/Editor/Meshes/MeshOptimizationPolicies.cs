using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Meshes
{
    [Flags]
    internal enum MeshOptimizationPermissions
    {
        None = 0,
        CoordinateSpaceChanges = 1,
        RigidConversion = 2,
        MaterialSlotReordering = 4,
        All = CoordinateSpaceChanges | RigidConversion | MaterialSlotReordering
    }

    internal sealed class MeshOptimizationPolicies
    {
        private sealed class Rule
        {
            internal OptimizationBuildScope Scope;
            internal int Depth;
            internal MeshOptimizationPermissions Permissions;
        }

        private readonly List<Rule> rules = new();
        internal bool MayConvertRigid => rules.Any(r => (r.Permissions & MeshOptimizationPermissions.RigidConversion) != 0);

        internal static MeshOptimizationPermissions Permissions(bool coordinates, bool rigid, bool reorder) =>
            (coordinates ? MeshOptimizationPermissions.CoordinateSpaceChanges : 0) |
            (coordinates && rigid ? MeshOptimizationPermissions.RigidConversion : 0) |
            (reorder ? MeshOptimizationPermissions.MaterialSlotReordering : 0);

        internal void Capture(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOMeshOptimizationPolicy>(true);
            foreach (var component in all.Where(c => OptimizationBuildScope.Applies(c, context)))
            {
                var scope = OptimizationBuildScope.Capture(component, context.AvatarRootObject);
                int depth = 0;
                for (var current = scope.Root; current != context.AvatarRootTransform; current = current.parent) depth++;
                var permissions = Permissions(component.AllowCoordinateSpaceChanges, component.ConvertRigidMeshes, component.AllowMaterialSlotReordering);
                if (rules.Any(r => r.Scope.Root == scope.Root && r.Permissions != permissions))
                    throw new InvalidOperationException("Mesh Optimization Policies with the same scope and platform must use matching options.");
                rules.Add(new Rule { Scope = scope, Depth = depth, Permissions = permissions });
            }
            // Policies are configuration only. Keep their captured scopes through
            // generators without leaving authoring references for dependency analysis.
            foreach (var component in all) Object.DestroyImmediate(component);
        }

        internal MeshOptimizationPermissions Resolve(Transform target)
        {
            Rule selected = null;
            foreach (var rule in rules)
            {
                if (!rule.Scope.Contains(target) || selected != null && rule.Depth < selected.Depth) continue;
                if (selected != null && rule.Depth == selected.Depth && rule.Permissions != selected.Permissions)
                    throw new InvalidOperationException("Equally specific Mesh Optimization Policies overlap after hierarchy changes. Use matching options or non-overlapping scopes.");
                selected = rule;
            }
            // More-specific scopes can explicitly keep all relaxations disabled.
            return selected?.Permissions ?? MeshOptimizationPermissions.None;
        }
    }
}
