using UnityEngine;

namespace Noname.AvatarTools
{
    [AddComponentMenu("Noname/ALTO Mesh Optimization Policy")]
    public sealed class ALTOMeshOptimizationPolicy : ALTOPlatformOptimizationComponent
    {
        [Tooltip("Allow different object coordinate systems during mesh optimization. World geometry and independent toggles remain protected, but object-space shader effects can change.")]
        public bool AllowCoordinateSpaceChanges;

        [Tooltip("Convert eligible MeshRenderers and unweighted skinned meshes to rigid skinning. Requires coordinate-space changes and Merge Compatible Skinned Meshes in Trace And Optimize Meshes.")]
        public bool ConvertRigidMeshes = true;

        [Tooltip("Allow transparent renderers to merge even though they lose independent camera-distance sorting. Transparency, overlapping surfaces and their ordering relative to other renderers can change. Other compatibility checks still apply.")]
        public bool AllowTransparentSortingChanges;

        [Tooltip("Allow mesh optimization without proving that shaders are independent of mesh IDs and skinning inputs. Vertex/primitive IDs, bone indices and skinning weights may change, affecting index-based masks, deformation and other effects. Also permits shaders whose input usage could not be verified. Other compatibility checks still apply.")]
        public bool AllowShaderIndexChanges;

        [Tooltip("Allow renderers driven by the same material animation to merge despite different animated starting values. The retained renderer supplies the starting values, including during partial-weight blending and default restoration. Unanimated properties and channels must remain unchanged.")]
        public bool AllowAnimatedMaterialDefaultChanges;

        [Tooltip("Allow equal material slots to merge across intervening materials. Overlapping, stencil and depth effects can change. Requires Merge Identical Material Slots in Trace And Optimize Meshes.")]
        public bool AllowMaterialSlotReordering;
    }
}
