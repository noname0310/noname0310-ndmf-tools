using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools
{
    public enum MeshAnchorSelection { Automatic, Explicit }

    [AddComponentMenu("Noname/ALTO Mesh Renderer Settings Override")]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedMeshRendererSettingsOverride")]
    public sealed class ALTOMeshRendererSettingsOverride : ALTOPlatformOptimizationComponent
    {
        public bool OverrideProbeAnchor;
        public MeshAnchorSelection AnchorSelection = MeshAnchorSelection.Automatic;

        [Tooltip("Required in Explicit mode. Automatic mode selects the most common retained anchor, or creates one at the combined bounds center when none exists.")]
        public Transform AnchorOverride;

        public bool OverrideLightProbeUsage;
        public LightProbeUsage LightProbes = LightProbeUsage.BlendProbes;

        public bool OverrideLightProbeProxyVolume;
        [Tooltip("Set this proxy volume override, or explicitly clear it by leaving the reference empty. Automatic proxy-volume lookup may still prevent mesh merging.")]
        public GameObject LightProbeProxyVolume;

        public bool OverrideReflectionProbeUsage;
        public ReflectionProbeUsage ReflectionProbes = ReflectionProbeUsage.BlendProbes;

        public bool OverrideShadowCasting;
        public ShadowCastingMode ShadowCasting = ShadowCastingMode.On;
        public bool OverrideReceiveShadows;
        public bool ReceiveShadows = true;
        public bool OverrideStaticShadowCaster;
        public bool StaticShadowCaster;

        [Tooltip("Leave these renderers unchanged by this component. Empty preserves none. Inactive and build-generated MeshRenderers and SkinnedMeshRenderers in scope are otherwise included. Only selected settings are overridden; existing animations remain active.")]
        public List<Renderer> PreserveRenderers = new();
    }
}
