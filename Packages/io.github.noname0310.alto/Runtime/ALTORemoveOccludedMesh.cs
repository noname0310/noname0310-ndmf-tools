using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noname.AvatarTools
{
    public enum OcclusionCulling { Automatic = -1, Off = 0, Front = 1, Back = 2 }

    [Serializable]
    public sealed class OcclusionTarget
    {
        public SkinnedMeshRenderer Renderer;
        public List<string> BlendShapes = new();
    }

    [Serializable]
    public sealed class OcclusionOccluder
    {
        public Renderer Renderer;
        [Tooltip("Explicitly treat this geometry as opaque, ignoring shader transparency, alpha clipping and stencil. With explicit Culling, also ignore animated shader values and property blocks. Mesh/material swaps and visibility animation remain protected.")]
        public bool AssumeOpaque;
        public OcclusionCulling Culling = OcclusionCulling.Automatic;
    }

    [Serializable]
    public sealed class OcclusionPose
    {
        [Tooltip("Animation paths are relative to the avatar root. Empty uses the current build pose.")]
        public AnimationClip Clip;
        [Min(0)] public float Time;
    }

    [AddComponentMenu("Noname/ALTO Remove Occluded Mesh")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedRemoveOccludedMesh")]
    public sealed class ALTORemoveOccludedMesh : ALTOPlatformOptimizationComponent
    {
        public List<OcclusionTarget> Targets = new();
        public RendererSelectionMode OccluderSelection = RendererSelectionMode.AllRenderersInScope;
        [Tooltip("In All Renderers mode these entries override individual occluder settings. In Selected Renderers mode they are the complete selection.")]
        public List<OcclusionOccluder> Occluders = new();
        public List<Renderer> ExcludedOccluders = new();
        public OcclusionPose ReferencePose = new();
        public List<OcclusionPose> AdditionalPoses = new();
        [Range(6, 1024)] public int ViewCount = 64;
        [Range(16, 4096)] public int RenderResolution = 512;
        [Range(0, 32)] public int BoundaryMarginPixels = 2;
        [Min(0)] public float DepthTolerance = 0.0001f;
        [Min(0)] public float DisplacementThreshold = 0.00001f;
    }
}
