using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.Installer
{
    // Optional installer suggestions, not build-time special cases or package dependencies.
    // Full type names identify authoring components even when users rename their objects.
    internal static class KnownFeatureDiscovery
    {
        private static readonly Dictionary<string, string> Features = new(StringComparer.Ordinal)
        {
            ["io.github.azukimochi.LightLimitChangerSettings"] = "Light Limit Changer",
            ["wataameya.marshmallow_PB.ndmf.marshmallow_PB_MA"] = "marshmallow PB",
            ["EmissionGlow.ProjectPudding.PuddingComponent"] = "Pudding",
            ["CookiTools.RealSweatInstaller"] = "RealSweat Installer",
            ["CookiTools.RealSweatTuning"] = "RealSweat Tuning"
        };

        internal static string Label(Component component) => component != null &&
            Features.TryGetValue(component.GetType().FullName ?? "", out var name) ? name : null;

        internal static Component[] Find(VRCAvatarDescriptor avatar) => avatar == null ? Array.Empty<Component>() :
            avatar.GetComponentsInChildren<Component>(true).Where(c => c != null && Label(c) != null &&
                c.GetComponentInParent<VRCAvatarDescriptor>(true) == avatar && !ALTOPlugin.IsEditorOnly(c.transform)).ToArray();

        internal static void AddSuggestions(InstallerDraft draft)
        {
            foreach (var component in Find(draft.Avatar))
                if (!draft.ExcludedComponents.Contains(component) && !draft.ExcludedObjects.Any(o =>
                    o != null && component.transform.IsChildOf(o.transform))) draft.ExcludedComponents.Add(component);
        }
    }
}
