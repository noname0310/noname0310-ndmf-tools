using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noname.AvatarTools
{
    public enum AnimatorParameterSelectionMode { Declared, Referenced }
    public enum AnimatorParameterFreezeMode { SelectedParameters, AllExternalInputsExceptPreserved }

    [Serializable]
    public sealed class AnimatorParameterSource
    {
        [Tooltip("An Animator Controller, Override Controller, Animator, GameObject, component with controller references, or Expression Parameters asset.")]
        public UnityEngine.Object Source;

        public bool IncludeChildren = true;
        public AnimatorParameterSelectionMode SelectionMode = AnimatorParameterSelectionMode.Declared;

        [Tooltip("Names excluded from this source. New parameters are included automatically.")]
        public List<string> ExcludedNames = new();
    }

    [Serializable]
    public sealed class AnimatorParameterSelection
    {
        public List<AnimatorParameterSource> Sources = new();

        [Tooltip("Additional parameter names in the final avatar's namespace.")]
        public List<string> Names = new();

        [Tooltip("Include final avatar parameter names starting with these prefixes. Empty prefixes are ignored.")]
        public List<string> Prefixes = new();
    }

    [Serializable]
    public sealed class AnimatorParameterConstant
    {
        [Tooltip("Optional source object or controller. With a source, Name uses its original parameter namespace; otherwise it uses the final avatar namespace.")]
        public UnityEngine.Object Source;
        public bool IncludeChildren = true;
        public string Name;

        [Tooltip("Use the final Expression Parameters default after generators run, or a consistent controller default when no expression declaration exists.")]
        public bool UseDefaultValue;

        [Tooltip("The value read by the built avatar. Bool requires 0 or 1; Int requires a whole number. Trigger parameters are unsupported.")]
        public float Value;
    }

    [AddComponentMenu("Noname/ALTO Animator Parameter Policy")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedAnimatorParameterPolicy")]
    public sealed class ALTOAnimatorParameterPolicy : ALTOPlatformOptimizationComponent
    {
        [Tooltip("Preserve these dynamic parameters and the computations that supply them.")]
        public AnimatorParameterSelection PreserveParameters = new();

        public AnimatorParameterFreezeMode FreezeMode = AnimatorParameterFreezeMode.SelectedParameters;

        [Tooltip("Disconnect selected Animator inputs from menus, networking, Contacts, PhysBones, Drivers and parameter curves. Preservation takes priority: conflicting settings stop the build.")]
        public List<AnimatorParameterConstant> FreezeParameters = new();

        [Tooltip("Initialize Active/Enabled properties to their proven constant FX output after parameter freezing. This can change initialization callbacks and visibility before the first Animator evaluation. Dynamic, ambiguous and explicitly preserved outputs remain unchanged.")]
        public bool BakeInitialVisibility;
    }
}
