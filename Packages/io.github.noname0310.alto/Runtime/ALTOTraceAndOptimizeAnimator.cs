using nadena.dev.ndmf;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools
{
    [AddComponentMenu("Noname/ALTO Trace And Optimize Animator")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedTraceAndOptimizeAnimator")]
    public sealed class ALTOTraceAndOptimizeAnimator : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("Remove unreachable states and transitions when controller binding coverage is preserved.")]
        public bool RemoveUnreachableStates = true;

        [Tooltip("Remove Animator parameter curves whose results cannot reach an observable output, including unused feedback loops.")]
        public bool RemoveUnusedParameterCurves = true;

        [Tooltip("After SDK generators finish, remove internal Animator parameter declarations with no remaining references in any controller. Expression parameters, menu controls, platform inputs, sensors and explicitly preserved names remain intact.")]
        public bool RemoveUnusedParameterDeclarations = true;

        [Tooltip("Remove unused internal calculations and collapse state machines with no observable output. After SDK generation, remove proven empty override layers while preserving layer controls, network outputs, behaviours and implicit Write Defaults effects.")]
        public bool SimplifyUnusedStateLogic = true;

        [Tooltip("Simplify constant-weight, static Blend Trees without changing their binding coverage.")]
        public bool SimplifyConstantBlendTrees = true;

        [Tooltip("Merge disjoint static clips with identical weights in non-normalized Direct Blend Trees.")]
        public bool MergeSameWeightClips = true;

        [Tooltip("Bake provably invariant Float/Range material properties into build copies. Color and Vector channels remain animated to preserve Unity's property-block output.")]
        public bool BakeConstantMaterialProperties = true;

        [Tooltip("Remove whole material-property tracks proven unused by every possible shader, then simplify their unused calculations. Unknown shader sources and preserved dynamic inputs remain intact.")]
        public bool RemoveUnusedMaterialProperties = true;

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
