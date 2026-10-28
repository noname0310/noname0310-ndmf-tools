using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noname.AvatarTools
{
    [Serializable]
    public sealed class MeshReductionPriority
    {
        public Renderer Renderer;
        [Tooltip("Higher values retain a larger share of triangles. One gives equal relative quality.")]
        [Min(0.01f)] public float Weight = 1;
        [Tooltip("Minimum triangles to retain on this renderer, capped at its input count.")]
        [Min(0)] public int MinimumTriangles;
    }

    [AddComponentMenu("Noname/ALTO Mesh Reduction")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedMeshReduction")]
    public sealed class ALTOMeshReduction : ALTOPlatformOptimizationComponent
    {
        [Tooltip("Total triangle budget inside Scope Root, including preserved and unselected renderers. This is not a platform upload limit.")]
        [Min(0)] public int TriangleBudget = 40000;
        [Tooltip("Select every mesh renderer in Scope Root at build time, or only the explicitly listed renderers. Preserve Renderers still takes precedence.")]
        public RendererSelectionMode TargetSelection = RendererSelectionMode.AllRenderersInScope;
        [Tooltip("Used only in Selected Renderers mode. An empty list selects no renderers. Unselected meshes still count toward the budget.")]
        public List<Renderer> ReductionTargets = new();
        [Tooltip("Keep these renderers' geometry unchanged by this component. They still count toward the budget.")]
        public List<Renderer> PreserveRenderers = new();
        public List<MeshReductionPriority> ReductionPriorities = new();
        public bool PreserveBorderEdges = true;
        public BudgetExceededAction OnBudgetUnreachable = BudgetExceededAction.Warning;

        // Used by the optional AAO mapping integration, never serialized into authoring assets.
        [NonSerialized] public Renderer[] BuildInputRenderers = Array.Empty<Renderer>();

        private void Reset() => AddDescriptorRenderers();

        public void AddDescriptorRenderers()
        {
            var descriptor = FindDescriptor();
            if (descriptor == null) return;
            var scope = ScopeRoot != null ? ScopeRoot : transform;
            foreach (var renderer in new[] { descriptor.VisemeSkinnedMesh, descriptor.customEyeLookSettings.eyelidsSkinnedMesh })
                if (renderer != null && renderer.transform.IsChildOf(scope) && !PreserveRenderers.Contains(renderer))
                    PreserveRenderers.Add(renderer);
        }
    }
}
