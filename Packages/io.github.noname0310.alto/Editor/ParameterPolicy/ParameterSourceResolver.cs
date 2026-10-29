using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.BlendShapeOptimization;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.ParameterPolicy
{
    internal sealed class SourceParameter
    {
        internal string Name;
        internal Component Owner;
        internal Object Asset;
        internal bool Referenced;
        internal ParameterNamespace MappingNamespace;
        internal string MappingName, MappingSuffix;
    }

    internal sealed class ParameterSourceResult
    {
        internal readonly List<SourceParameter> Parameters = new();
        internal readonly List<string> Diagnostics = new();
    }

    // Source references are evaluated again at build time. The Inspector only shows authoring inputs.
    internal static class ParameterSourceResolver
    {
        internal static bool IsSupported(Object source) => source is RuntimeAnimatorController ||
            source is VRCExpressionParameters || source is GameObject || source is Component;

        internal static RuntimeAnimatorController[] AvatarControllers(GameObject avatar) =>
            Owners(avatar, true, avatar).SelectMany(Assets).OfType<RuntimeAnimatorController>().Distinct().ToArray();

        internal static ParameterSourceResult Resolve(AnimatorParameterSource selection, GameObject avatar, BuildContext context = null)
        {
            var result = new ParameterSourceResult();
            if (selection?.Source == null) return result;
            var source = selection.Source;
            var referenced = new Dictionary<RuntimeAnimatorController, HashSet<string>>();
            if (!IsSupported(source))
            {
                result.Diagnostics.Add("Choose a controller, Animator, GameObject, component, or Expression Parameters asset.");
                return result;
            }
            void Add(Object asset, Component owner)
            {
                HashSet<string> used = null;
                if (asset is RuntimeAnimatorController runtime)
                {
                    if (!referenced.TryGetValue(runtime, out used))
                        referenced[runtime] = used = ControllerParameters(runtime, AnimatorParameterSelectionMode.Referenced);
                }
                IEnumerable<string> names = asset switch
                {
                    RuntimeAnimatorController controller => ControllerParameters(controller, selection.SelectionMode),
                    VRCExpressionParameters expressions => (expressions.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>())
                        .Where(p => p != null).Select(p => p.name),
                    _ => Array.Empty<string>()
                };
                foreach (string name in names.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal))
                    result.Parameters.Add(new SourceParameter { Name = name, Owner = owner, Asset = asset, Referenced = used == null || used.Contains(name) });
            }
            if (source is GameObject || source is Component)
            {
                var root = source is Component component ? component.gameObject : (GameObject)source;
                if (avatar == null || !root.transform.IsChildOf(avatar.transform) || BelongsToOtherAvatar(root.transform, avatar))
                {
                    result.Diagnostics.Add("Scene parameter sources must belong to this avatar.");
                    return result;
                }
                if (source is Animator animator)
                {
                    if (!ALTOPlugin.IsEditorOnly(animator.transform)) Add(animator.runtimeAnimatorController, animator);
                }
                else if (source is Component single)
                {
                    if (!ALTOPlugin.IsEditorOnly(single.transform))
                        foreach (var asset in Assets(single)) Add(asset, single);
                }
                else
                {
                    foreach (var owner in Owners(root, selection.IncludeChildren, avatar))
                        foreach (var asset in Assets(owner)) Add(asset, owner);
                }

                // Parameter providers cover components which declare inputs without a controller asset,
                // such as MA Parameters, generated menu controls, Contacts and PhysBone prefixes.
                var info = context != null ? nadena.dev.ndmf.ParameterInfo.ForContext(context) : nadena.dev.ndmf.ParameterInfo.ForUI;
                foreach (var provided in info.GetParametersForObject(root))
                {
                    if (string.IsNullOrEmpty(provided.OriginalName)) continue;
                    var owner = provided.Source;
                    if (owner == null || owner is VRCAvatarDescriptor ||
                        ALTOPlugin.IsEditorOnly(owner.transform) || BelongsToOtherAvatar(owner.transform, avatar)) continue;
                    if (source is Component selected && owner != selected) continue;
                    if (source is GameObject && !selection.IncludeChildren && owner.gameObject != root) continue;
                    foreach (var parameter in provided.SubParameters())
                    {
                        if (string.IsNullOrEmpty(parameter.OriginalName) ||
                            result.Parameters.Any(p => p.Owner == owner && p.Name == parameter.OriginalName)) continue;
                        result.Parameters.Add(new SourceParameter
                        {
                            Name = parameter.OriginalName, Owner = owner, Asset = owner, Referenced = true,
                            MappingNamespace = provided.Namespace, MappingName = provided.OriginalName,
                            MappingSuffix = parameter.OriginalName.Substring(provided.OriginalName.Length)
                        });
                    }
                }
                if (result.Parameters.Count == 0)
                    result.Diagnostics.Add("No controller parameters or NDMF parameter declarations were found. Parameters generated only by an unsupported build tool cannot be verified from this source.");
            }
            else
            {
                var owners = avatar == null ? Array.Empty<Component>() : Owners(avatar, true, avatar).ToArray();
                bool found = false;
                foreach (var owner in owners)
                    foreach (var asset in Assets(owner))
                        if (SameSource(asset, source) || (source is RuntimeAnimatorController requested &&
                            asset is RuntimeAnimatorController installed && UsesController(installed, requested)))
                        { Add(asset, owner); found = true; }
                if (!found)
                {
                    Add(source, null);
                    result.Diagnostics.Add("This asset has no matching controller/parameter source in the avatar. Its final build names cannot yet be verified.");
                }
            }
            return result;
        }

        private static bool BelongsToOtherAvatar(Transform root, GameObject avatar)
        {
            for (var current = root; current != null && current != avatar.transform; current = current.parent)
                if (current.GetComponent<VRCAvatarDescriptor>() != null) return true;
            return false;
        }

        private static IEnumerable<Component> Owners(GameObject root, bool children, GameObject avatar)
        {
            var transforms = children ? root.GetComponentsInChildren<Transform>(true) : new[] { root.transform };
            foreach (var transform in transforms)
            {
                if (ALTOPlugin.IsEditorOnly(transform) || BelongsToOtherAvatar(transform, avatar)) continue;
                foreach (var component in transform.GetComponents<Component>())
                    if (component != null && !(component is Transform) && !(component is ALTOOptimizationComponent))
                        yield return component;
            }
        }

        // Serialized controller references cover Merge Animator and other generator components without
        // taking a hard dependency on those packages or guessing an artist's parameter naming scheme.
        private static IEnumerable<Object> Assets(Component component)
        {
            if (component is Animator animator)
            {
                if (animator.runtimeAnimatorController != null) yield return animator.runtimeAnimatorController;
                yield break;
            }
            if (component is VRCAvatarDescriptor descriptor)
            {
                if (descriptor.customizeAnimationLayers)
                    foreach (var layer in (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                        .Concat(descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>()))
                        if (!layer.isDefault && layer.animatorController != null) yield return layer.animatorController;
                if (descriptor.customExpressions && descriptor.expressionParameters != null) yield return descriptor.expressionParameters;
                yield break;
            }
            if (!(component is MonoBehaviour)) yield break;
            using var serialized = new SerializedObject(component);
            var property = serialized.GetIterator();
            var seen = new HashSet<Object>();
            while (property.Next(true))
                if (property.propertyType == SerializedPropertyType.ObjectReference &&
                    (property.objectReferenceValue is RuntimeAnimatorController || property.objectReferenceValue is VRCExpressionParameters) &&
                    seen.Add(property.objectReferenceValue))
                    yield return property.objectReferenceValue;
        }

        private static bool UsesController(RuntimeAnimatorController installed, RuntimeAnimatorController source)
        {
            var seen = new HashSet<RuntimeAnimatorController>();
            while (installed != null && seen.Add(installed))
            {
                if (SameSource(installed, source)) return true;
                installed = (installed as AnimatorOverrideController)?.runtimeAnimatorController;
            }
            return false;
        }

        private static bool SameSource(Object current, Object original) =>
            current != null && original != null && (current == original ||
                ObjectRegistry.GetReference(current).Object == ObjectRegistry.GetReference(original).Object);

        internal static HashSet<string> ControllerParameters(RuntimeAnimatorController source, AnimatorParameterSelectionMode mode)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var overrides = new List<Dictionary<AnimationClip, AnimationClip>>();
            var seenControllers = new HashSet<RuntimeAnimatorController>();
            while (source is AnimatorOverrideController wrapper && seenControllers.Add(source))
            {
                var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                wrapper.GetOverrides(pairs);
                overrides.Add(pairs.Where(p => p.Key != null && p.Value != null).ToDictionary(p => p.Key, p => p.Value));
                source = wrapper.runtimeAnimatorController;
            }
            if (!(source is AnimatorController controller)) return names;
            if (mode == AnimatorParameterSelectionMode.Declared)
            {
                names.UnionWith(controller.parameters.Select(p => p.name).Where(n => !string.IsNullOrEmpty(n)));
                return names;
            }
            var declared = new HashSet<string>(controller.parameters.Select(p => p.name), StringComparer.Ordinal);
            void Name(string value) { if (!string.IsNullOrEmpty(value)) names.Add(value); }
            void Behaviours(StateMachineBehaviour[] behaviours)
            {
                foreach (var behaviour in behaviours)
                {
                    if (behaviour == null) continue;
                    if (behaviour is VRC_AvatarParameterDriver driver)
                    {
                        foreach (var operation in driver.parameters ?? new())
                        {
                            if (operation == null) continue;
                            Name(operation.name);
                            if (operation.type == VRC_AvatarParameterDriver.ChangeType.Copy) Name(operation.source);
                        }
                    }
                    else
                    {
                        using var serialized = new SerializedObject(behaviour);
                        var property = serialized.GetIterator();
                        while (property.Next(true))
                            if (property.propertyType == SerializedPropertyType.String && declared.Contains(property.stringValue))
                                Name(property.stringValue);
                    }
                }
            }
            void Transitions(IEnumerable<AnimatorTransitionBase> transitions)
            {
                foreach (var transition in transitions)
                    if (transition != null)
                        foreach (var condition in transition.conditions) Name(condition.parameter);
            }
            var seenMotions = new HashSet<Motion>();
            void Motion(Motion motion)
            {
                if (motion == null || !seenMotions.Add(motion)) return;
                if (motion is BlendTree tree)
                {
                    if (tree.blendType == BlendTreeType.Direct)
                        foreach (var child in tree.children) Name(child.directBlendParameter);
                    else
                    {
                        Name(tree.blendParameter);
                        if (tree.blendType != BlendTreeType.Simple1D) Name(tree.blendParameterY);
                    }
                    foreach (var child in tree.children) Motion(child.motion);
                }
                else if (motion is AnimationClip clip)
                {
                    for (int i = overrides.Count - 1; i >= 0; i--)
                        if (overrides[i].TryGetValue(clip, out var replacement)) clip = replacement;
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                        if (binding.type == typeof(Animator) && binding.path == "" && declared.Contains(binding.propertyName))
                            Name(binding.propertyName);
                }
            }
            var seenMachines = new HashSet<AnimatorStateMachine>();
            void Machine(AnimatorStateMachine machine, AnimatorControllerLayer layer)
            {
                if (machine == null || !seenMachines.Add(machine)) return;
                Behaviours(machine.behaviours);
                Transitions(machine.anyStateTransitions); Transitions(machine.entryTransitions);
                foreach (var child in machine.states)
                {
                    var state = child.state;
                    if (state == null) continue;
                    if (state.timeParameterActive) Name(state.timeParameter);
                    if (state.speedParameterActive) Name(state.speedParameter);
                    if (state.cycleOffsetParameterActive) Name(state.cycleOffsetParameter);
                    if (state.mirrorParameterActive) Name(state.mirrorParameter);
                    Behaviours(state.behaviours);
                    Transitions(state.transitions);
                    Motion(state.motion);
                    if (layer.syncedLayerIndex >= 0) Motion(layer.GetOverrideMotion(state));
                }
                foreach (var child in machine.stateMachines)
                {
                    Transitions(machine.GetStateMachineTransitions(child.stateMachine));
                    Machine(child.stateMachine, layer);
                }
            }
            foreach (var layer in controller.layers)
            {
                seenMachines.Clear();
                var machine = layer.syncedLayerIndex >= 0 && layer.syncedLayerIndex < controller.layers.Length
                    ? controller.layers[layer.syncedLayerIndex].stateMachine : layer.stateMachine;
                Machine(machine, layer);
            }
            return names;
        }
    }
}
