using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.Build
{
    [Serializable]
    public sealed class FrozenParameterReport
    {
        public string Name, InternalName, Type;
        public float Value;
        public bool FromBuildDefault;
    }

    [Serializable]
    internal sealed class OptimizationResourceCounts
    {
        public int SkinnedRenderers, MeshRenderers, MaterialSlots, PhysBones, PhysBoneColliders;
        public long Triangles;

        internal static OptimizationResourceCounts Capture(GameObject avatar)
        {
            var counts = new OptimizationResourceCounts();
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = null;
                if (renderer is SkinnedMeshRenderer skin) { counts.SkinnedRenderers++; mesh = skin.sharedMesh; }
                else if (renderer is MeshRenderer) { counts.MeshRenderers++; mesh = renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null; }
                else continue;
                counts.MaterialSlots += renderer.sharedMaterials.Length;
                if (mesh == null) continue;
                for (int i = 0; i < mesh.subMeshCount; i++)
                    if (mesh.GetTopology(i) == MeshTopology.Triangles) counts.Triangles += mesh.GetIndexCount(i) / 3;
            }
            counts.PhysBones = avatar.GetComponentsInChildren<VRCPhysBone>(true).Length;
            counts.PhysBoneColliders = avatar.GetComponentsInChildren<VRCPhysBoneCollider>(true).Length;
            return counts;
        }
    }

    [Serializable]
    internal sealed class OptimizationBuildReport
    {
        public string Avatar, Platform, StartedUtc;
        public OptimizationResourceCounts Input;
        public PhysBoneResourceCounts Physics;
        public MeshReduction.MeshReductionReport MeshReduction;
        public Expressions.ExpressionOptimizationReport Expressions;
        public List<AnimatorOptimization.MaterialOutputOptimizationReport> MaterialOutputs = new();
        public readonly List<string> Messages = new();
        public readonly List<string> PreservedParameters = new();
        public readonly List<string> ParameterMappings = new();
        public readonly List<FrozenParameterReport> FrozenParameters = new();
        public readonly List<OptimizationStageMeasurement> Measurements = new();
        internal OptimizationMeasurement.Timeline Timeline;
        internal void Initialize(GameObject avatar, string platform)
        {
            if (Input != null) return;
            Avatar = avatar.name;
            Platform = platform;
            StartedUtc = DateTime.UtcNow.ToString("O");
            Input = OptimizationResourceCounts.Capture(avatar);
            // Analysis retains this report's measurement list even if a later
            // pass fails before the normal end-of-build publication.
            AvatarBuildAnalysis.PublishReport(avatar, this);
        }
    }
}
