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
        internal readonly HashSet<Renderer> Evaluated = new();
    }

    internal sealed class MeshRendererSettingsRule
    {
        internal string Owner;
        internal OptimizationBuildScope Scope;
        internal bool OverrideAnchor, ExplicitAnchor, HadProxyVolume;
        internal Transform Anchor;
        internal readonly Dictionary<string, object> Values = new(StringComparer.Ordinal);
        internal readonly HashSet<ObjectReference> Preserved = new();
        internal readonly HashSet<ObjectReference> OutsideScope = new();
    }

    // The anchor option's behavior reference is PA Edit All Mesh Anchor Override.
    // Scope, automatic selection, conflict checks and build staging are independent implementations; see CREDITS.md.
    internal static class MeshRendererSettingsOverridePass
    {
        private sealed class Write
        {
            internal object Value;
            internal string Owner;
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
                    Owner = AnimationUtility.CalculateTransformPath(component.transform, context.AvatarRootTransform) +
                        " (component " + (Array.IndexOf(component.GetComponents<ALTOMeshRendererSettingsOverride>(), component) + 1) + ")",
                    Scope = OptimizationBuildScope.Capture(component, context.AvatarRootObject),
                    HadProxyVolume = component.OverrideLightProbeProxyVolume && component.LightProbeProxyVolume != null,
                    OverrideAnchor = component.OverrideProbeAnchor,
                    ExplicitAnchor = component.AnchorSelection == MeshAnchorSelection.Explicit,
                    Anchor = component.AnchorSelection == MeshAnchorSelection.Explicit ? component.AnchorOverride : null
                };
                if (component.OverrideLightProbeUsage) rule.Values.Add("lightProbeUsage", component.LightProbes);
                if (component.OverrideLightProbeProxyVolume) rule.Values.Add("lightProbeProxyVolumeOverride", component.LightProbeProxyVolume);
                if (component.OverrideReflectionProbeUsage) rule.Values.Add("reflectionProbeUsage", component.ReflectionProbes);
                if (component.OverrideShadowCasting) rule.Values.Add("shadowCastingMode", component.ShadowCasting);
                if (component.OverrideReceiveShadows) rule.Values.Add("receiveShadows", component.ReceiveShadows);
                if (component.OverrideStaticShadowCaster) rule.Values.Add("staticShadowCaster", component.StaticShadowCaster);
                foreach (var renderer in (component.PreserveRenderers ?? new()).Where(r => r != null))
                {
                    if (!renderer.transform.IsChildOf(context.AvatarRootTransform))
                        throw new InvalidOperationException("Preserved renderers must belong to this avatar: " + renderer.name);
                    rule.Preserved.Add(ObjectRegistry.GetReference(renderer));
                }
                foreach (var renderer in context.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
                    if (!rule.Scope.Contains(renderer.transform)) rule.OutsideScope.Add(ObjectRegistry.GetReference(renderer));
                state.Rules.Add(rule);
            }
        }

        internal static bool HasPendingOutputs(BuildContext context)
        {
            var state = context.GetState<MeshRendererSettingsOverrideState>();
            return state.Rules.Count != 0 && context.AvatarRootObject.GetComponentsInChildren<Renderer>(true)
                .Any(r => (r is MeshRenderer || r is SkinnedMeshRenderer) && !state.Evaluated.Contains(r) &&
                    BuildPlatformFilters.Includes(r.transform, context));
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
                var allRenderers = context.AvatarRootObject.GetComponentsInChildren<Renderer>(true);
                var renderers = allRenderers
                    .Where(r => (r is MeshRenderer || r is SkinnedMeshRenderer) &&
                        BuildPlatformFilters.Includes(r.transform, context) && !objects.Protected(r)).ToArray();
                var writes = new Dictionary<(Renderer, string), Write>();
                var automaticAnchors = new Dictionary<string, Transform>(StringComparer.Ordinal);
                foreach (var rule in state.Rules)
                {
                    if (rule.OverrideAnchor && rule.ExplicitAnchor && (rule.Anchor == null ||
                        !rule.Anchor.IsChildOf(context.AvatarRootTransform) || ALTOPlugin.IsEditorOnly(rule.Anchor)))
                        throw new InvalidOperationException("The selected lighting anchor was removed during the build: " + rule.Owner);
                    if (rule.HadProxyVolume && (GameObject)rule.Values["lightProbeProxyVolumeOverride"] == null)
                        throw new InvalidOperationException("The selected light probe proxy volume was removed during the build.");
                    var selected = renderers.Where(r => rule.Scope.Contains(r.transform) &&
                        !rule.Preserved.Contains(ObjectRegistry.GetReference(r)) &&
                        !rule.OutsideScope.Contains(ObjectRegistry.GetReference(r))).ToArray();
                    if (!selected.Any(r => !state.Evaluated.Contains(r))) continue;
                    Transform anchor = rule.Anchor;
                    if (rule.OverrideAnchor && anchor == null)
                    {
                        // Resolve once for this policy, then reuse the same anchor for
                        // later outputs rather than creating a new lighting boundary.
                        string key = string.Join(",", selected.Select(r => r.GetInstanceID()).OrderBy(id => id));
                        if (!automaticAnchors.TryGetValue(key, out anchor))
                        {
                            anchor = LightingAnchorSelection.Resolve(context.AvatarRootObject, selected, null);
                            automaticAnchors.Add(key, anchor);
                            if (!selected.Any(r => r.probeAnchor == anchor)) generated.Add(anchor);
                        }
                        rule.Anchor = anchor;
                    }
                    foreach (var renderer in selected)
                    {
                        if (state.Evaluated.Contains(renderer)) continue;
                        if (rule.OverrideAnchor) Propose(renderer, "probeAnchor", anchor, rule.Owner);
                        foreach (var value in rule.Values) Propose(renderer, value.Key, value.Value, rule.Owner);
                    }
                }
                foreach (var write in writes.Values) write.Apply();
                // An override is an initial value policy. Never overwrite values
                // that constant animation baking subsequently established.
                state.Evaluated.UnionWith(allRenderers);
                committed = true;
                if (writes.Count == 0) return;
                var report = context.GetState<OptimizationBuildReport>();
                report.Initialize(context.AvatarRootObject, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
                report.Messages.Add("Mesh renderer settings override: applied " + writes.Count + " selected properties to " +
                    writes.Keys.Select(k => k.Item1).Distinct().Count() + " renderers across " + state.Rules.Count +
                    " policies. Initial settings were overridden; existing animation remains active.");

                void Propose(Renderer renderer, string property, object value, string owner)
                {
                    var key = (renderer, property);
                    if (writes.TryGetValue(key, out var previous))
                    {
                        if (!Equals(previous.Value, value))
                            throw new InvalidOperationException("Conflicting mesh renderer overrides for '" +
                                AnimationUtility.CalculateTransformPath(renderer.transform, context.AvatarRootTransform) + "." + property +
                                "' from '" + previous.Owner + "' and '" + owner + "'.");
                    }
                    else writes.Add(key, new Write { Value = value, Owner = owner, Apply = () => Assign(renderer, property, value) });
                }
            }
            finally
            {
                if (!committed) foreach (var anchor in generated.Where(a => a != null)) Object.DestroyImmediate(anchor.gameObject);
                foreach (var component in context.AvatarRootObject.GetComponentsInChildren<ALTOMeshRendererSettingsOverride>(true))
                    Object.DestroyImmediate(component);
            }
        }

        private static void Assign(Renderer renderer, string property, object value)
        {
            switch (property)
            {
                case "probeAnchor": renderer.probeAnchor = (Transform)value; break;
                case "lightProbeUsage": renderer.lightProbeUsage = (LightProbeUsage)value; break;
                case "lightProbeProxyVolumeOverride": renderer.lightProbeProxyVolumeOverride = (GameObject)value; break;
                case "reflectionProbeUsage": renderer.reflectionProbeUsage = (ReflectionProbeUsage)value; break;
                case "shadowCastingMode": renderer.shadowCastingMode = (ShadowCastingMode)value; break;
                case "receiveShadows": renderer.receiveShadows = (bool)value; break;
                case "staticShadowCaster": renderer.staticShadowCaster = (bool)value; break;
                default: throw new InvalidOperationException("Unknown renderer setting: " + property);
            }
        }
    }
}
