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
            transform != null && !BlendShapeOptimization.ALTOPlugin.IsEditorOnly(transform) &&
            Filters.Values.All(predicate => predicate(transform, context));
    }
}
