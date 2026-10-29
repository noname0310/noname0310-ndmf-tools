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

}
