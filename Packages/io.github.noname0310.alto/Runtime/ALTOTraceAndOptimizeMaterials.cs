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
        [Tooltip("Bake supported static lilToon main color adjustments and secondary textures into a generated main texture on PC and mobile. Animated or unsupported settings are retained. Uses lilToon's texture baker; filtering, mipmaps and shader fallback appearance can differ from runtime composition.")]
        public bool BakeLilToonMainTextures;

        [Tooltip("Remove saved properties and texture references only when no shader branch, dependency or VRChat fallback can use them. Source assets and Material Variants are retained.")]
        public bool RemoveUnusedSavedProperties = true;

        [Tooltip("Share materials only when their serialized rendering settings, shader, keywords, tags, properties and textures match exactly. Editor labels may differ.")]
        public bool ShareEquivalentMaterials = true;

        [Tooltip("Keep these materials unchanged by this component.")]
        public List<Material> PreserveMaterials = new();

        [NonSerialized] public Renderer[] BuildInputRenderers = Array.Empty<Renderer>();
    }
}
