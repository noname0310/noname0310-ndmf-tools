using UnityEngine;

namespace Noname.AvatarTools
{
    // Only policies that deliberately change avatar behavior expose a platform selection.
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedPlatformOptimizationComponent")]
    public abstract class ALTOPlatformOptimizationComponent : ALTOOptimizationComponent
    {
        [Tooltip("Build targets on which this policy applies. Also respects platform filters on this object and its ancestors.")]
        public OptimizationPlatforms ApplyOnPlatforms = OptimizationPlatforms.Android;

        protected ALTOPlatformOptimizationComponent() { }

        // Set only the serialized field's initial value. Existing serialized
        // selections still override this default when Unity loads the component.
        protected ALTOPlatformOptimizationComponent(OptimizationPlatforms defaultPlatforms)
        {
            ApplyOnPlatforms = defaultPlatforms;
        }
    }
}
