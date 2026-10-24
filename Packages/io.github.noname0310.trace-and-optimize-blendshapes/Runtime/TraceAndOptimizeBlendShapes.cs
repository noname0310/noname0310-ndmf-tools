using nadena.dev.ndmf;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools
{
    [AddComponentMenu("Noname/Trace And Optimize BlendShapes")]
    [DisallowMultipleComponent]
    public sealed class TraceAndOptimizeBlendShapes : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("Optional replacement for this object's subtree. Must belong to the same avatar.")]
        public Transform ScopeRoot;

        [Tooltip("Preserve recognized Japanese and English MMD morph names in every scoped renderer.")]
        public bool PreserveMmdBlendShapes = true;

        [Tooltip("Combine shapes whose animation curves are always zero and whose animation usage is identical. Current weights are baked into a group shape with a default weight of 100.")]
        public bool OptimizeZeroResetAnimations = true;

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
