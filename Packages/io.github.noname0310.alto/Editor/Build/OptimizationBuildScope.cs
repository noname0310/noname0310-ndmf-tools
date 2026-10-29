using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Noname.AvatarTools.BlendShapeOptimization;

namespace Noname.AvatarTools.Build
{
    internal sealed class OptimizationBuildEnvironment
    {
        internal readonly BuildTarget Target = EditorUserBuildSettings.activeBuildTarget;
    }

    internal sealed class OptimizationBuildScope
    {
        internal Transform Root;
        internal readonly HashSet<Transform> OriginalTransforms = new();

        internal bool Contains(Transform value) => value != null &&
            (OriginalTransforms.Contains(value) || (Root != null && value.IsChildOf(Root)));

        internal static OptimizationPlatforms Platform(BuildTarget target) => target switch
        {
            BuildTarget.StandaloneWindows64 => OptimizationPlatforms.PC,
            BuildTarget.StandaloneWindows => OptimizationPlatforms.PC,
            BuildTarget.Android => OptimizationPlatforms.Android,
            BuildTarget.iOS => OptimizationPlatforms.IOS,
            _ => OptimizationPlatforms.None
        };

        internal static bool Applies(ALTOPlatformOptimizationComponent component, BuildTarget target) =>
            component.enabled && !ALTOPlugin.IsEditorOnly(component.transform) &&
            (component.ApplyOnPlatforms & Platform(target)) != 0;

        internal static OptimizationBuildScope Capture(ALTOOptimizationComponent component, GameObject avatar)
        {
            var root = component.ScopeRoot != null ? component.ScopeRoot : component.transform;
            if (component.FindDescriptor()?.gameObject != avatar || !root.IsChildOf(avatar.transform))
                throw new InvalidOperationException(component.GetType().Name + " requires a scope inside its own avatar.");
            for (var current = root; current != avatar.transform; current = current.parent)
                if (current.GetComponent<VRCAvatarDescriptor>() != null)
                    throw new InvalidOperationException("Scope Root cannot belong to a nested avatar.");
            var result = new OptimizationBuildScope { Root = root };
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                if (ALTOPlugin.IsEditorOnly(child)) continue;
                var descriptor = child.GetComponent<VRCAvatarDescriptor>();
                if (descriptor != null && descriptor.gameObject != avatar)
                    throw new InvalidOperationException("Optimization scopes cannot contain another avatar.");
                result.OriginalTransforms.Add(child);
            }
            return result;
        }

        internal static bool Applies(ALTOPlatformOptimizationComponent component, BuildContext context) =>
            Applies(component, context.GetState<OptimizationBuildEnvironment>().Target) &&
            BuildPlatformFilters.IncludesSettings(component.transform, context);
    }
}
