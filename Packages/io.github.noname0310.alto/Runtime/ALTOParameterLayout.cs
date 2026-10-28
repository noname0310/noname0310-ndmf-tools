using nadena.dev.ndmf;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools
{
    public enum ParameterLayoutPlatform { PC, Android, IOS }
    public enum AdditionalSyncedParameterPolicy { Error, KeepLocal }

    [AddComponentMenu("Noname/ALTO Parameter Layout")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedParameterLayout")]
    public sealed class ALTOParameterLayout : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("Successful uploads on this platform record the avatar's synchronized parameter layout. Other platforms use that record.")]
        public ParameterLayoutPlatform ReferencePlatform = ParameterLayoutPlatform.PC;
        [Tooltip("Choose what happens when another platform adds synchronized parameters absent from the reference upload. Keep Local explicitly disables their network synchronization.")]
        public AdditionalSyncedParameterPolicy AdditionalParameters = AdditionalSyncedParameterPolicy.Error;

        public VRCAvatarDescriptor FindDescriptor()
        {
            for (var current = transform; current != null; current = current.parent)
                if (current.TryGetComponent<VRCAvatarDescriptor>(out var descriptor)) return descriptor;
            return null;
        }
    }
}
