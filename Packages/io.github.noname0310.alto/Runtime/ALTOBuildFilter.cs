using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noname.AvatarTools
{
    // Retained for serialized setups from ALTO 0.0.4 and earlier. New setups use
    // Build Exclusions and Rendering Filter independently.
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedBuildFilter")]
    public sealed class ALTOBuildFilter : ALTOBuildExclusions
    {
        [Tooltip("Applied after generators and before texture atlasing and mesh merging.")]
        public List<MaterialExclusion> MaterialExclusions = new();

        [Tooltip("Explicitly remove BlendShape-defined geometry after generators, before freezing, atlas packing and reduction. Missing targets or morphs are reported as errors.")]
        public List<BlendShapeMeshExclusion> BlendShapeExclusions = new();

        [Tooltip("Remove material slots beyond a mesh's submesh count after generators run. Primary geometry is retained.")]
        public bool RemoveAdditionalMaterialPasses;

        [Tooltip("Explicitly discard these rendering features after generators run. Particle Systems also removes their simulation components when no retained consumer needs them. These types are not categorically unsupported on Android.")]
        public RendererFilterTypes ExcludedRendererTypes;

        [Tooltip("Exclude these renderers from automatic rendering rules. Explicit object, component and material exclusions still apply.")]
        public List<Renderer> PreserveAutomaticRenderers = new();
    }
}
