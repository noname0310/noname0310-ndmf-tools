using System;
using System.Linq;
using nadena.dev.modular_avatar.core;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.ModularAvatarIntegration
{
    [InitializeOnLoad]
    internal static class PlatformFilterIntegration
    {
        static PlatformFilterIntegration() => BuildPlatformFilters.Register("nadena.dev.modular-avatar", Includes);

        private static bool Includes(Transform transform, BuildContext context)
        {
            for (var current = transform; current != null; current = current.parent)
            {
                var filters = current.GetComponents<ModularAvatarPlatformFilter>();
                bool includes = filters.Any(f => !f.ExcludePlatform);
                if (includes && filters.Any(f => f.ExcludePlatform))
                    throw new InvalidOperationException("MA Platform Filter cannot mix Include and Exclude on '" + current.name + "'.");
                string platform = context.PlatformProvider.QualifiedName;
                if (includes && !filters.Any(f => !f.ExcludePlatform && f.Platform == platform)) return false;
                if (filters.Any(f => f.ExcludePlatform && f.Platform == platform)) return false;
                if (current == context.AvatarRootTransform) break;
            }
            return true;
        }
    }
}
