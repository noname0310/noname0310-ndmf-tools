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

        [Tooltip("Allow equal material slots to merge across intervening materials. Overlapping, stencil and depth effects can change. Requires Merge Identical Material Slots in Trace And Optimize Meshes.")]
        public bool AllowMaterialSlotReordering;
    }
}
