using nadena.dev.ndmf;
using UnityEngine;

namespace Noname.AvatarTools
{
    /// <summary>Build-time clothing displacement using another renderer's surface normals.</summary>
    [AddComponentMenu("Noname/Normal Field Offset")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SkinnedMeshRenderer))]
    public sealed class NormalFieldOffset : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("The body renderer that supplies the normal field. Its surface is sampled before MA mesh deletion.")]
        public SkinnedMeshRenderer TargetBody;

        [Tooltip("Displacement in world-space meters in the setup pose. Positive = along the body's outward normals; negative = inward.")]
        public float Offset = 0.002f;

        [Min(0)]
        [Tooltip("Only offset vertices this close to the body (meters). 0 = unlimited. The last 20% fades smoothly to zero.")]
        public float MaxDistance;
    }
}
