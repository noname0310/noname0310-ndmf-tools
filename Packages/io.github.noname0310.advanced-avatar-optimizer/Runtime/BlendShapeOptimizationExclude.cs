using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Noname.AvatarTools
{
    [AddComponentMenu("Noname/BlendShape Optimization Exclude")]
    [DisallowMultipleComponent]
    public sealed class BlendShapeOptimizationExclude : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("BlendShape names to preserve on this renderer. Exclusions apply to both freezing and zero-reset grouping.")]
        public List<string> BlendShapes = new List<string>();
    }
}
