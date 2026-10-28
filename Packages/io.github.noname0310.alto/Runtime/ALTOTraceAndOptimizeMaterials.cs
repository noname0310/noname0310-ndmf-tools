using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noname.AvatarTools
{
    [AddComponentMenu("Noname/ALTO Trace And Optimize Materials")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedTraceAndOptimizeMaterials")]
    public sealed class ALTOTraceAndOptimizeMaterials : ALTOOptimizationComponent
    {
        [Tooltip("Remove saved properties and texture references only when no shader branch, dependency or VRChat fallback can use them. Source assets and Material Variants are retained.")]
        public bool RemoveUnusedSavedProperties = true;

        [Tooltip("Share materials only when their serialized rendering settings, shader, keywords, tags, properties and textures match exactly. Editor labels may differ.")]
        public bool ShareEquivalentMaterials = true;

        [Tooltip("Keep these materials unchanged by this component.")]
        public List<Material> PreserveMaterials = new();

        [NonSerialized] public Renderer[] BuildInputRenderers = Array.Empty<Renderer>();
    }
}
