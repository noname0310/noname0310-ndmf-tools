using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Noname.AvatarTools
{
    [AddComponentMenu("Noname/BlendShape Optimization Exclude")]
    [DisallowMultipleComponent]
    public sealed class BlendShapeOptimizationExclude : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("BlendShape names to preserve on this renderer. Exclusions apply to freezing, zero-reset grouping and ineffective shape removal.")]
        public List<string> BlendShapes = new List<string>();
    }
}
