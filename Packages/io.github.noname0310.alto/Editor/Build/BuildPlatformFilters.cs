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
        private static readonly Dictionary<string, Func<Transform, Transform, bool>> Previews = new();

        // Optional integrations use their package's public component API, without a mandatory dependency.
        internal static void Register(string name, Func<Transform, BuildContext, bool> predicate, Func<Transform, Transform, bool> preview = null)
        {
            Filters[name] = predicate;
            if (preview != null) Previews[name] = preview; else Previews.Remove(name);
        }

        internal static bool Includes(Transform transform, BuildContext context) =>
            transform != null &&
            !BlendShapeOptimization.ALTOPlugin.IsEditorOnly(transform) &&
            Filters.Values.All(predicate => predicate(transform, context));

        // Settings selection is separate from object availability. An ALTO platform filter never
        // makes renderers, parameter sources or components from another package disappear.
        internal static bool IncludesSettings(Transform transform, BuildContext context)
        {
            if (!Includes(transform, context)) return false;
            var platforms = SettingsPlatforms(transform, context.AvatarRootTransform, out bool filtered);
            return !filtered || (platforms & OptimizationBuildScope.Platform(context.GetState<OptimizationBuildEnvironment>().Target)) != 0;
        }

        internal static bool IncludesSettingsOnAnyPlatform(Transform transform, BuildContext context, OptimizationPlatforms platforms) =>
            Includes(transform, context) && (SettingsPlatforms(transform, context.AvatarRootTransform, out _) & platforms) != 0;

        // Inspector-only evaluation must not create BuildContext: its constructor
        // modifies the avatar and emits build events. Unpreviewable providers stay
        // explicitly uncertain until the real build evaluates them.
        internal static bool PreviewIncludesSettings(Transform transform, Transform avatar, UnityEditor.BuildTarget target, out string pending)
        {
            pending = null;
            if (transform == null || BlendShapeOptimization.ALTOPlugin.IsEditorOnly(transform)) return false;
            if ((SettingsPlatforms(transform, avatar, out _) & OptimizationBuildScope.Platform(target)) == 0) return false;
            foreach (var filter in Filters.Keys)
                if (Previews.TryGetValue(filter, out var preview)) { if (!preview(transform, avatar)) return false; }
                else pending = "Additional build filters are evaluated during the build.";
            return true;
        }

        private static OptimizationPlatforms SettingsPlatforms(Transform transform, Transform avatar, out bool filtered)
        {
            var platforms = OptimizationPlatforms.All;
            filtered = false;
            for (var current = transform; current != null; current = current.parent)
            {
                var filter = current.GetComponent<ALTOOptimizationPlatformFilter>();
                if (filter != null && filter.enabled) { platforms &= filter.ApplyOnPlatforms; filtered = true; }
                if (current == avatar) break;
            }
            return platforms;
        }
    }
}
