using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools
{
    [AddComponentMenu("Noname/ALTO Trace And Optimize BlendShapes")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedTraceAndOptimizeBlendShapes")]
    public sealed class ALTOTraceAndOptimizeBlendShapes : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("Optional replacement for this object's subtree. Must belong to the same avatar.")]
        public Transform ScopeRoot;

        [Tooltip("Preserve recognized Japanese and English MMD morph names in every scoped renderer.")]
        public bool PreserveMmdBlendShapes = true;

        [Tooltip("Remove BlendShapes whose position, normal and tangent deltas are exactly zero on every vertex used by any submesh, in every frame. Includes empty separators and shapes left ineffective by geometry removal. Protected shapes are retained.")]
        public bool RemoveIneffectiveBlendShapes = true;

        [Tooltip("Combine shapes whose animation curves are always zero and whose animation usage is identical. Current weights are baked into a group shape with a default weight of 100.")]
        public bool OptimizeZeroResetAnimations = true;

        [Tooltip("Ignore states proven unreachable by transition and parameter analysis. Runtime inputs and uncertain paths are preserved. This does not remove states from the controller.")]
        public bool IgnoreUnreachableStates = true;

        [Tooltip("Ignore these clips when deciding which BlendShapes to freeze or merge in this scope. Bindings to optimized shapes are removed from their build copies. Other animation tracks are retained.")]
        public List<AnimationClip> ExcludedAnimations = new();

        public VRCAvatarDescriptor FindDescriptor()
        {
            for (var current = transform; current != null; current = current.parent)
            {
                var descriptor = current.GetComponent<VRCAvatarDescriptor>();
                if (descriptor != null) return descriptor;
            }
            return null;
        }
    }
}
