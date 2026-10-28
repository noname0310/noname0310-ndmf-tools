using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noname.AvatarTools
{
    [AddComponentMenu("Noname/ALTO Trace And Optimize Meshes")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedTraceAndOptimizeMeshes")]
    public sealed class ALTOTraceAndOptimizeMeshes : ALTOOptimizationComponent
    {
        [Tooltip("Merge skinned renderers with compatible visibility, animations, skinning, culling and lighting settings after mesh reduction. Unsupported or externally observed renderers remain separate.")]
        public bool MergeCompatibleSkinnedMeshes = true;

        [Tooltip("Expand the destination bounds to contain every input culling volume. Disable to merge only matching bounds. Lighting anchors and visibility still have to be compatible.")]
        public bool ExpandBounds = true;

        [Tooltip("Allow compatible renderers with different object coordinate systems to merge. World geometry and independent toggles are retained, but object-space shader effects can change. This does not override lighting, shadow, animation or external-reference checks.")]
        public bool AllowCoordinateSpaceChanges;

        [Tooltip("Platforms where the coordinate-space relaxation applies. Equivalent optimizations still run on every platform, subject to ancestor platform filters.")]
        public OptimizationPlatforms CoordinateSpaceChangesOnPlatforms = OptimizationPlatforms.Android;

        [Tooltip("When coordinate-space changes and renderer merging are enabled, convert eligible MeshRenderers and unweighted skinned meshes to rigid skinning before grouping. Original transforms, geometry and independent toggles remain.")]
        public bool ConvertRigidMeshes = true;

        [Tooltip("Combine compatible submeshes using the same material. Animated material slots and shaders that depend on vertex or primitive indices are preserved.")]
        public bool MergeIdenticalMaterialSlots = true;

        [Tooltip("Merge equal material slots even when other materials occur between them. The first material stays first, but overlapping, stencil and depth effects can change when draw order changes. Animated material slots remain protected.")]
        public bool AllowMaterialSlotReordering = true;

        [Tooltip("Platforms where material-slot reordering is allowed. Choose All to enable it for PC as well. Order-preserving slot merging still runs on other platforms.")]
        public OptimizationPlatforms MaterialSlotReorderingOnPlatforms = OptimizationPlatforms.Android;

        [Tooltip("Remove vertices unused by every submesh, and unused or duplicate bone-palette entries. Drawn geometry, morph frames and all skinning influences are retained. Index-dependent shaders remain protected.")]
        public bool RemoveUnusedMeshData = true;

        [Tooltip("Rebind static skin bones to their nearest retained parent by absorbing the fixed local transform into bind poses. Animated, humanoid, physics-driven, constrained and explicitly preserved bones remain. Original transforms are left for object dependency cleanup.")]
        public bool RebindStaticBones = true;

        [Tooltip("Keep these renderers and their material slots unchanged by this component.")]
        public List<Renderer> PreserveRenderers = new();

        // Optional external optimizer mapping; never serialized into authoring assets.
        [NonSerialized] public Renderer[] BuildInputRenderers = Array.Empty<Renderer>();
        [NonSerialized] public Transform[] BuildSkinningDependencies = Array.Empty<Transform>();
    }
}
