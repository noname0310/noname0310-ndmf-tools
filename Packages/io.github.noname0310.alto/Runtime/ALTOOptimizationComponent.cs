using System;
using nadena.dev.ndmf;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools
{
    [Flags]
    public enum OptimizationPlatforms
    {
        None = 0,
        PC = 1,
        Android = 2,
        IOS = 4,
        All = PC | Android | IOS
    }

    public enum RendererSelectionMode
    {
        AllRenderersInScope,
        SelectedRenderers
    }

    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedOptimizationComponent")]

    public abstract class ALTOOptimizationComponent : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("An empty scope uses this object's subtree. Analysis still covers the entire avatar.")]
        public Transform ScopeRoot;

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
