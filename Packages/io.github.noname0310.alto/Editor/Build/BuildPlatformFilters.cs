using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    internal static class BuildPlatformFilters
    {
        private static readonly Dictionary<string, Func<Transform, BuildContext, bool>> Filters = new();

        // Optional integrations use their package's public component API, without a mandatory dependency.
        internal static void Register(string name, Func<Transform, BuildContext, bool> predicate) => Filters[name] = predicate;

        internal static bool Includes(Transform transform, BuildContext context) =>
            transform != null &&
            !BlendShapeOptimization.ALTOPlugin.IsEditorOnly(transform) &&
            Filters.Values.All(predicate => predicate(transform, context));

        // Settings selection is separate from object availability. An ALTO platform filter never
        // makes renderers, parameter sources or components from another package disappear.
        internal static bool IncludesSettings(Transform transform, BuildContext context)
        {
            if (!Includes(transform, context)) return false;
            var platforms = SettingsPlatforms(transform, context, out bool filtered);
            return !filtered || (platforms & OptimizationBuildScope.Platform(context.GetState<OptimizationBuildEnvironment>().Target)) != 0;
        }

        internal static bool IncludesSettingsOnAnyPlatform(Transform transform, BuildContext context, OptimizationPlatforms platforms) =>
            Includes(transform, context) && (SettingsPlatforms(transform, context, out _) & platforms) != 0;

        private static OptimizationPlatforms SettingsPlatforms(Transform transform, BuildContext context, out bool filtered)
        {
            var platforms = OptimizationPlatforms.All;
            filtered = false;
            for (var current = transform; current != null; current = current.parent)
            {
                var filter = current.GetComponent<ALTOOptimizationPlatformFilter>();
                if (filter != null && filter.enabled) { platforms &= filter.ApplyOnPlatforms; filtered = true; }
                if (current == context.AvatarRootTransform) break;
            }
            return platforms;
        }
    }
}
