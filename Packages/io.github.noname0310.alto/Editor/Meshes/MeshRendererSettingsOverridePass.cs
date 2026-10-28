using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Meshes
{
    internal sealed class MeshRendererSettingsOverrideState
    {
        internal readonly List<MeshRendererSettingsRule> Rules = new();
    }

    internal sealed class MeshRendererSettingsRule
    {
        internal ALTOMeshRendererSettingsOverride Component;
        internal OptimizationBuildScope Scope;
        internal bool HadProxyVolume;
        internal readonly HashSet<ObjectReference> Preserved = new();
    }

    // The anchor option's behavior reference is PA Edit All Mesh Anchor Override.
    // Scope, automatic selection, conflict checks and build staging are independent implementations; see CREDITS.md.
    internal static class MeshRendererSettingsOverridePass
    {
        private sealed class Write
        {
            internal object Value;
            internal ALTOMeshRendererSettingsOverride Owner;
            internal Action Apply;
        }

        internal static void Capture(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOMeshRendererSettingsOverride>(true);
            var state = context.GetState<MeshRendererSettingsOverrideState>();
            foreach (var component in all)
            {
                if (!OptimizationBuildScope.Applies(component, context)) { Object.DestroyImmediate(component); continue; }
                Validate(component, context.AvatarRootTransform);
                var rule = new MeshRendererSettingsRule
                {
                    Component = component,
                    Scope = OptimizationBuildScope.Capture(component, context.AvatarRootObject),
                    HadProxyVolume = component.OverrideLightProbeProxyVolume && component.LightProbeProxyVolume != null
                };
                foreach (var renderer in (component.PreserveRenderers ?? new()).Where(r => r != null))
                {
                    if (!renderer.transform.IsChildOf(context.AvatarRootTransform))
                        throw new InvalidOperationException("Preserved renderers must belong to this avatar: " + renderer.name);
                    rule.Preserved.Add(ObjectRegistry.GetReference(renderer));
                }
                state.Rules.Add(rule);
            }
        }

        private static void Validate(ALTOMeshRendererSettingsOverride component, Transform avatar)
        {
            if (component == null) throw new InvalidOperationException("The mesh renderer override settings were removed during the build.");
            if (component.OverrideProbeAnchor)
            {
                if (!Enum.IsDefined(typeof(MeshAnchorSelection), component.AnchorSelection))
                    throw new InvalidOperationException("Choose a valid Anchor Selection mode.");
                if (component.AnchorSelection == MeshAnchorSelection.Explicit && (component.AnchorOverride == null ||
                    !component.AnchorOverride.IsChildOf(avatar) || ALTOPlugin.IsEditorOnly(component.AnchorOverride)))
                    throw new InvalidOperationException("Explicit anchor selection requires a retained transform inside this avatar.");
            }
            if (component.OverrideLightProbeUsage && !Enum.IsDefined(typeof(LightProbeUsage), component.LightProbes) ||
                component.OverrideReflectionProbeUsage && !Enum.IsDefined(typeof(ReflectionProbeUsage), component.ReflectionProbes) ||
                component.OverrideShadowCasting && !Enum.IsDefined(typeof(ShadowCastingMode), component.ShadowCasting))
                throw new InvalidOperationException("Choose valid mesh renderer probe and shadow settings.");
            if (component.OverrideLightProbeProxyVolume && component.LightProbeProxyVolume != null &&
                (!component.LightProbeProxyVolume.transform.IsChildOf(avatar) || ALTOPlugin.IsEditorOnly(component.LightProbeProxyVolume.transform)))
                throw new InvalidOperationException("The light probe proxy volume override must belong to this avatar and be retained.");
        }

        internal static void Apply(BuildContext context)
        {
            var generated = new List<Transform>();
            bool committed = false;
            try
            {
                var state = context.GetState<MeshRendererSettingsOverrideState>();
                if (state.Rules.Count == 0) return;
                var objects = context.GetState<ObjectOptimizationState>();
                var renderers = context.AvatarRootObject.GetComponentsInChildren<Renderer>(true)
                    .Where(r => (r is MeshRenderer || r is SkinnedMeshRenderer) &&
                        BuildPlatformFilters.Includes(r.transform, context) && !objects.Protected(r)).ToArray();
                var writes = new Dictionary<(Renderer, string), Write>();
                var automaticAnchors = new Dictionary<string, Transform>(StringComparer.Ordinal);
                foreach (var rule in state.Rules)
                {
                    var component = rule.Component;
                    Validate(component, context.AvatarRootTransform);
                    if (rule.HadProxyVolume && component.LightProbeProxyVolume == null)
                        throw new InvalidOperationException("The selected light probe proxy volume was removed during the build.");
                    var selected = renderers.Where(r => rule.Scope.Contains(r.transform) &&
                        !rule.Preserved.Contains(ObjectRegistry.GetReference(r))).ToArray();
                    if (selected.Length == 0) continue;
                    Transform anchor = null;
                    if (component.OverrideProbeAnchor)
                    {
                        if (component.AnchorSelection == MeshAnchorSelection.Explicit) anchor = component.AnchorOverride;
                        else
                        {
                            // Resolve against the original settings, never a preceding rule's writes.
                            // Identical automatic selections share one generated anchor.
                            string key = string.Join(",", selected.Select(r => r.GetInstanceID()).OrderBy(id => id));
                            if (!automaticAnchors.TryGetValue(key, out anchor))
                            {
                                anchor = LightingAnchorSelection.Resolve(context.AvatarRootObject, selected, null);
                                automaticAnchors.Add(key, anchor);
                                if (!selected.Any(r => r.probeAnchor == anchor)) generated.Add(anchor);
                            }
                        }
                    }
                    foreach (var renderer in selected)
                    {
                        if (component.OverrideProbeAnchor) Propose(renderer, "probeAnchor", anchor, v => renderer.probeAnchor = v, component);
                        if (component.OverrideLightProbeUsage) Propose(renderer, "lightProbeUsage", component.LightProbes, v => renderer.lightProbeUsage = v, component);
                        if (component.OverrideLightProbeProxyVolume) Propose(renderer, "lightProbeProxyVolumeOverride", component.LightProbeProxyVolume, v => renderer.lightProbeProxyVolumeOverride = v, component);
                        if (component.OverrideReflectionProbeUsage) Propose(renderer, "reflectionProbeUsage", component.ReflectionProbes, v => renderer.reflectionProbeUsage = v, component);
                        if (component.OverrideShadowCasting) Propose(renderer, "shadowCastingMode", component.ShadowCasting, v => renderer.shadowCastingMode = v, component);
                        if (component.OverrideReceiveShadows) Propose(renderer, "receiveShadows", component.ReceiveShadows, v => renderer.receiveShadows = v, component);
                        if (component.OverrideStaticShadowCaster) Propose(renderer, "staticShadowCaster", component.StaticShadowCaster, v => renderer.staticShadowCaster = v, component);
                    }
                }
                foreach (var write in writes.Values) write.Apply();
                committed = true;
                var report = context.GetState<OptimizationBuildReport>();
                report.Initialize(context.AvatarRootObject, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
                report.Messages.Add("Mesh renderer settings override: applied " + writes.Count + " selected properties to " +
                    writes.Keys.Select(k => k.Item1).Distinct().Count() + " renderers across " + state.Rules.Count +
                    " policies. Initial settings were overridden; existing animation remains active.");

                void Propose<T>(Renderer renderer, string property, T value, Action<T> setter, ALTOMeshRendererSettingsOverride owner)
                {
                    var key = (renderer, property);
                    if (writes.TryGetValue(key, out var previous))
                    {
                        if (!EqualityComparer<T>.Default.Equals((T)previous.Value, value))
                            throw new InvalidOperationException("Conflicting mesh renderer overrides for '" +
                                AnimationUtility.CalculateTransformPath(renderer.transform, context.AvatarRootTransform) + "." + property +
                                "' from '" + OwnerPath(previous.Owner) + "' and '" + OwnerPath(owner) + "'.");
                    }
                    else writes.Add(key, new Write { Value = value, Owner = owner, Apply = () => setter(value) });
                }
                string OwnerPath(ALTOMeshRendererSettingsOverride owner) =>
                    AnimationUtility.CalculateTransformPath(owner.transform, context.AvatarRootTransform) + " (component " +
                    (Array.IndexOf(owner.GetComponents<ALTOMeshRendererSettingsOverride>(), owner) + 1) + ")";
            }
            finally
            {
                if (!committed) foreach (var anchor in generated.Where(a => a != null)) Object.DestroyImmediate(anchor.gameObject);
                foreach (var component in context.AvatarRootObject.GetComponentsInChildren<ALTOMeshRendererSettingsOverride>(true))
                    Object.DestroyImmediate(component);
            }
        }
    }
}
