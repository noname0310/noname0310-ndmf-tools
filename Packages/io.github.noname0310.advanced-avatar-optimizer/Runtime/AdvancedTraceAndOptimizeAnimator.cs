using nadena.dev.ndmf;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools
{
    [AddComponentMenu("Noname/Advanced Trace And Optimize Animator")]
    [DisallowMultipleComponent]
    public sealed class AdvancedTraceAndOptimizeAnimator : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("Remove unreachable states and transitions when controller binding coverage is preserved.")]
        public bool RemoveUnreachableStates = true;

        [Tooltip("Remove Animator parameter curves whose results cannot reach an observable output, including unused feedback loops.")]
        public bool RemoveUnusedParameterCurves = true;

        [Tooltip("Simplify constant-weight, static Blend Trees without changing their binding coverage.")]
        public bool SimplifyConstantBlendTrees = true;

        [Tooltip("Merge disjoint static clips with identical weights in non-normalized Direct Blend Trees.")]
        public bool MergeSameWeightClips = true;

        [Tooltip("Bake provably invariant Float/Range material properties into build copies. Color and Vector channels remain animated to preserve Unity's property-block output.")]
        public bool BakeConstantMaterialProperties = true;

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
