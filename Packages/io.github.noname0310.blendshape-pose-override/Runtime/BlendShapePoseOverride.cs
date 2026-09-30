using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools
{
    public enum PoseAnimationPathMode
    {
        Relative,
        Absolute
    }

    [Serializable]
    public sealed class BlendShapePoseEntry
    {
        public string BlendShapeName;

        // Keep the first clip's serialized field so existing scene and prefab overrides remain valid.
        public AnimationClip Animation;
        public List<AnimationClip> AdditionalAnimations = new List<AnimationClip>();

        public IEnumerable<AnimationClip> GetAnimations()
        {
            yield return Animation;
            if (AdditionalAnimations != null)
                foreach (var clip in AdditionalAnimations) yield return clip;
        }
    }

    /// <summary>Replaces blendshapes with poses authored relative to the renderer's current weights.</summary>
    [AddComponentMenu("Noname/BlendShape Pose Override")]
    [DisallowMultipleComponent]
    public sealed class BlendShapePoseOverride : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("The mesh to modify. The descriptor's eyelid renderer is used only as an initial default.")]
        public SkinnedMeshRenderer TargetMesh;

        public List<BlendShapePoseEntry> Overrides = new List<BlendShapePoseEntry>();

        [Tooltip("Relative uses this object or Relative Path Root. Absolute uses the avatar descriptor's object.")]
        public PoseAnimationPathMode PathMode = PoseAnimationPathMode.Relative;

        [Tooltip("Optional animation root in Relative mode. Leave empty to use this component's object.")]
        public Transform RelativePathRoot;

        [HideInInspector] public int PreviewEntry;
        [HideInInspector, Range(0, 100)] public float PreviewWeight;

        public VRCAvatarDescriptor FindDescriptor()
        {
            for (var current = transform; current != null; current = current.parent)
            {
                var descriptor = current.GetComponent<VRCAvatarDescriptor>();
                if (descriptor != null) return descriptor;
            }
            return null;
        }

        private void Reset()
        {
            var descriptor = FindDescriptor();
            TargetMesh = descriptor != null ? descriptor.customEyeLookSettings.eyelidsSkinnedMesh : null;
            Overrides = new List<BlendShapePoseEntry>();
            PathMode = PoseAnimationPathMode.Relative;
            RelativePathRoot = null;
            PreviewEntry = 0;
            PreviewWeight = 0;
        }
    }
}
