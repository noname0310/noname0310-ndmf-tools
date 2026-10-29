using nadena.dev.ndmf;
using UnityEngine;

namespace Noname.AvatarTools
{
    [AddComponentMenu("Noname/ALTO Optimization Platform Filter")]
    [DisallowMultipleComponent]
    public sealed class ALTOOptimizationPlatformFilter : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("Run ALTO settings on this object and its descendants only on these platforms. Nested filters intersect. Does not remove objects or filter other packages' components.")]
        public OptimizationPlatforms ApplyOnPlatforms = OptimizationPlatforms.Android;
    }
}
