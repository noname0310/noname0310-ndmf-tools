using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noname.AvatarTools
{
    public enum MaterialRemovalMode { RemoveMaterialPass, RemoveSubMeshGeometry }

    [Flags]
    public enum RendererFilterTypes { None = 0, ParticleSystems = 1, Lines = 2, Trails = 4 }

    [Serializable]
    public sealed class BlendShapeMeshExclusion
    {
        public SkinnedMeshRenderer Renderer;
        [Tooltip("Remove triangles touched by any selected BlendShape frame, regardless of its current weight. An empty list removes nothing.")]
        public List<string> BlendShapes = new();
        [Min(0), Tooltip("Minimum vertex displacement in mesh-local units. Normal and tangent deltas alone do not select geometry.")]
        public float DisplacementThreshold = 0.00001f;
    }

    [Serializable]
    public sealed class MaterialExclusion
    {
        [Tooltip("Limit this rule to one renderer. Leave empty to search the entire scope.")]
        public Renderer Renderer;
        [Tooltip("Match this material's source asset. Leave empty to select by renderer and slot.")]
        public Material Material;
        [Tooltip("A slot index, or -1 to match all slots selected by Renderer and Material.")]
        public int Slot = -1;
        [Tooltip("Material Pass removes extra passes beyond the mesh's submesh count. Geometry removes the submesh and all passes drawing it.")]
        public MaterialRemovalMode Mode;
    }

    [AddComponentMenu("Noname/ALTO Build Filter")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedBuildFilter")]
    public sealed class ALTOBuildFilter : ALTOPlatformOptimizationComponent
    {
        [Tooltip("Remove these objects and their children before avatar generators run.")]
        public List<GameObject> ExcludedObjects = new();

        [Tooltip("Remove only these components. Their GameObjects remain available to other features.")]
        public List<Component> ExcludedComponents = new();

        [Tooltip("Remove AudioSources present before generators run, together with their unneeded Unity audio filters and SDK spatializers. Remaining references are validated before removal.")]
        public bool RemoveAudioSources;

        [Tooltip("Keep these sources when Remove Audio Sources is enabled. Explicit object and component exclusions still apply.")]
        public List<AudioSource> PreserveAudioSources = new();

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
