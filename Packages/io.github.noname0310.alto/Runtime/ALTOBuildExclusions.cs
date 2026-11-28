using System.Collections.Generic;
using UnityEngine;

namespace Noname.AvatarTools
{
    [AddComponentMenu("Noname/ALTO Build Exclusions")]
    [DisallowMultipleComponent]
    public class ALTOBuildExclusions : ALTOPlatformOptimizationComponent
    {
        [Tooltip("Remove these objects and their children before avatar generators run.")]
        public List<GameObject> ExcludedObjects = new();

        [Tooltip("Remove only these components. Their GameObjects remain available to other features.")]
        public List<Component> ExcludedComponents = new();

        [Tooltip("Remove AudioSources, including sources created during the build, together with their unneeded Unity audio filters and SDK spatializers. Remaining references are validated before removal.")]
        public bool RemoveAudioSources;

        [Tooltip("Keep these sources when Remove Audio Sources is enabled. Explicit object and component exclusions still apply.")]
        public List<AudioSource> PreserveAudioSources = new();
    }
}
