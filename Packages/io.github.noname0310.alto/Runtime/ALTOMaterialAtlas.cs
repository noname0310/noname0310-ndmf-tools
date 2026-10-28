using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noname.AvatarTools
{
    public enum UnsupportedAtlasInput { PreserveRenderer, Error }
    public enum AtlasCompression { None, Low, Normal, High }
    public enum AtlasMaterialSelectionMode { AllUnassignedMaterials, SelectedMaterials }
    public enum AtlasMaterialConversionTiming { BeforeGenerators, AfterGenerators }
    public enum ExistingAtlasPolicy { Preserve, ReplaceFullyCovered, Chain }

    [Serializable]
    public sealed class AtlasTextureMapping
    {
        public string SourceProperty = "_MainTex";
        public string DestinationProperty = "_MainTex";
        public Color MissingTextureColor = Color.white;
        [Tooltip("Use linear sampling for data textures. Normal-map import encodings are not converted.")]
        public bool Linear;
    }

    [Serializable]
    public sealed class AtlasResolutionPriority
    {
        public Material Material;
        [Range(0.125f, 1f)] public float ResolutionScale = 1f;
    }

    [Serializable]
    public sealed class MaterialAtlasGroup
    {
        public string Name = "Atlas";
        public AtlasMaterialSelectionMode MaterialSelection = AtlasMaterialSelectionMode.AllUnassignedMaterials;
        [Tooltip("Used only in Selected Materials mode. An empty list selects no materials.")]
        public List<Material> Materials = new();
        [Tooltip("The output copies this material's shader and static settings. Only mapped source textures are transferred; compatible numeric shader animations remain active.")]
        public Material ReferenceMaterial;
        public List<AtlasTextureMapping> TextureMappings = new() { new() };
        [Min(32)] public int AtlasSize = 2048;
        [Min(1)] public int PaddingPixels = 8;
        public AtlasCompression Compression = AtlasCompression.Normal;
        public bool GenerateMipMaps = true;
        public List<AtlasResolutionPriority> ResolutionPriorities = new();
    }

    [AddComponentMenu("Noname/ALTO Material Atlas")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedMaterialAtlas")]
    public sealed class ALTOMaterialAtlas : ALTOPlatformOptimizationComponent
    {
        [Tooltip("Convert existing material references before avatar generators can discard incompatible shaders. Texture packing still runs after generators. After Generators leaves input shaders available to generators but cannot recover references they discard.")]
        public AtlasMaterialConversionTiming MaterialConversion = AtlasMaterialConversionTiming.BeforeGenerators;
        [Tooltip("Preserve leaves overlapping existing atlases and their materials intact. Replace Fully Covered takes over an existing UV0 atlas only when all its material consumers are selected. Chain processes existing atlases first, then repacks their completed textures and UVs; conversion is deferred until then.")]
        public ExistingAtlasPolicy ExistingAtlases = ExistingAtlasPolicy.Preserve;
        [Tooltip("Select every mesh renderer in Scope Root at build time, or only the explicitly listed renderers.")]
        public RendererSelectionMode TargetSelection = RendererSelectionMode.AllRenderersInScope;
        [Tooltip("Used only in Selected Renderers mode. An empty list selects no renderers.")]
        public List<Renderer> TargetRenderers = new();
        public List<MaterialAtlasGroup> MergeGroups = new() { new() };
        public UnsupportedAtlasInput UnsupportedInputs = UnsupportedAtlasInput.PreserveRenderer;
    }
}
