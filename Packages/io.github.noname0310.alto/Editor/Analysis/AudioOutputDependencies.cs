using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.Analysis
{
    // Audio effects modify an AudioSource/AudioListener on the same GameObject:
    // https://docs.unity3d.com/2022.3/Documentation/Manual/class-AudioEffect.html
    // The SDK spatializers affect AudioSource only. ONSPAudioSource.Reset and
    // LateUpdate disable the component when that source is absent.
    // AAO ComponentInfos.VRCSDK.cs was reviewed; this output dependency model is
    // independently implemented rather than marking each spatializer as a root.
    internal static class AudioOutputDependencies
    {
        private static bool IsUnityFilter(Type type) =>
            type == typeof(AudioLowPassFilter) || type == typeof(AudioHighPassFilter) ||
            type == typeof(AudioEchoFilter) || type == typeof(AudioDistortionFilter) ||
            type == typeof(AudioReverbFilter) || type == typeof(AudioChorusFilter);

        internal static bool IsModifier(Component component)
        {
            var type = component.GetType();
            return IsUnityFilter(type) || type == typeof(VRCSpatialAudioSource) ||
                type.FullName == "ONSPAudioSource" && type.Assembly.GetName().Name == "VRC.SDKBase";
        }

        internal static IEnumerable<Component> Outputs(Component modifier)
        {
            foreach (var source in modifier.GetComponents<AudioSource>()) yield return source;
            if (IsUnityFilter(modifier.GetType()))
                foreach (var listener in modifier.GetComponents<AudioListener>()) yield return listener;
        }
    }
}
