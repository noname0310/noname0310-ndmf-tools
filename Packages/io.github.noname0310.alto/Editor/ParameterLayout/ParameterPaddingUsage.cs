using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.ObjectOptimization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.ParameterLayout
{
    // Padding has no avatar consumer. Check native objects at the final SDK boundary,
    // including late controllers, undeclared bindings and unknown runtime observers.
    // This is independent of any external compressor's naming or private APIs.
    internal sealed class ParameterPaddingUsage
    {
        private readonly HashSet<string> observed = new(StringComparer.Ordinal);
        private readonly ExternalParameterAccess external = ExternalParameterAccess.Collect();
        private bool uncertain;

        internal ParameterPaddingUsage(GameObject avatar)
        {
            uncertain = external.Uncertain || ObjectOptimizationSafety.UnsupportedRuntime(avatar) != null;
            observed.UnionWith(AnimatorReachability.BuiltIns);
            observed.UnionWith(AvatarParameterInputs.Sensors(avatar).Select(p => p.Name));
            observed.UnionWith(ExpressionMenuOptimizer.Menus(avatar.GetComponent<VRCAvatarDescriptor>()?.expressionsMenu)
                .SelectMany(m => m.controls ?? new()).Where(c => c != null).SelectMany(ExpressionMenuOptimizer.Parameters));
            var controllers = VRChatPlatformAnimatorBindings.Instance.GetInnateControllers(avatar).Select(c => c.Item2)
                .Concat(avatar.GetComponentsInChildren<Animator>(true).Select(a => a.runtimeAnimatorController))
                .Where(c => c != null).Distinct().ToArray();
            var pending = new Queue<UnityEngine.Object>(controllers);
            var seen = new HashSet<UnityEngine.Object>();
            while (pending.Count != 0)
            {
                var asset = pending.Dequeue();
                if (asset == null || !seen.Add(asset)) continue;
                if (asset is AnimatorController controller) observed.UnionWith(controller.parameters.Select(p => p.name));
                if (asset is AnimationClip clip)
                {
                    if (clip.events.Length != 0) uncertain = true;
                    observed.UnionWith(AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip))
                        .Where(b => b.type == typeof(Animator)).Select(b => b.propertyName));
                }
                if (asset is StateMachineBehaviour behaviour &&
                    !(behaviour is VRC_AvatarParameterDriver) && !(behaviour is VRC_AnimatorLayerControl) &&
                    !(behaviour is VRC_PlayableLayerControl) && !(behaviour is VRC_AnimatorTrackingControl) &&
                    !(behaviour is VRC_AnimatorLocomotionControl) && !(behaviour is VRC_AnimatorTemporaryPoseSpace) &&
                    !(behaviour is VRC_AnimatorPlayAudio)) uncertain = true;
                // Include transition/tree/time parameter names even when no corresponding
                // declaration exists. Strings in behaviours can also name reads or writes.
                // CollectDependencies does not enumerate unsaved controller subobjects.
                // Follow serialized native references, including synced-layer overrides.
                using var serialized = new SerializedObject(asset);
                var property = serialized.GetIterator();
                bool strings = asset is AnimatorState || asset is AnimatorTransitionBase || asset is BlendTree || asset is StateMachineBehaviour;
                while (property.Next(true))
                {
                    if (strings && property.propertyType == SerializedPropertyType.String) observed.Add(property.stringValue);
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var reference = property.objectReferenceValue;
                    if (reference is RuntimeAnimatorController || reference is AnimatorStateMachine || reference is AnimatorState ||
                        reference is AnimatorTransitionBase || reference is Motion || reference is StateMachineBehaviour) pending.Enqueue(reference);
                }
            }
        }

        internal bool IsUnobserved(string name) => !uncertain && !observed.Contains(name) &&
            !external.IsRead(name) && !external.IsWritten(name);
    }
}
