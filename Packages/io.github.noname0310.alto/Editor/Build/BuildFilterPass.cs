using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.util;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.ParameterPolicy;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Build
{
    internal sealed class BuildFilterState
    {
        internal OptimizationBuildScope Scope;
        internal readonly List<MaterialExclusion> Materials = new();
        internal readonly Dictionary<MaterialExclusion, string> SelectedRenderers = new();
        internal readonly List<BlendShapeMeshExclusion> BlendShapes = new();
        internal readonly Dictionary<SkinnedMeshRenderer, string> BlendShapeTargets = new();
        internal bool RemoveAdditionalMaterialPasses;
        internal RendererFilterTypes ExcludedRendererTypes;
        internal readonly HashSet<ObjectReference> PreserveAutomaticRenderers = new();
    }

    internal static class BuildFilterPass
    {
        internal static void RemoveEarly(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOBuildFilter>(true);
            var enabled = all.Where(c => OptimizationBuildScope.Applies(c, context)).ToArray();
            if (enabled.Length > 1)
                throw new InvalidOperationException("Use one enabled ALTO Build Filter per avatar and platform.");
            foreach (var filter in enabled) ApplyEarly(context, filter);
            foreach (var filter in all) if (filter != null) Object.DestroyImmediate(filter);
        }

        internal static void ApplyEarly(BuildContext context, ALTOBuildFilter filter)
        {
            var avatar = context.AvatarRootObject;
            var scope = OptimizationBuildScope.Capture(filter, avatar);
            var objects = new HashSet<GameObject>((filter.ExcludedObjects ?? new()).Where(o => o != null));
            var components = new HashSet<Component>((filter.ExcludedComponents ?? new()).Where(c => c != null));
            if (filter.RemoveAudioSources)
            {
                var preserved = new HashSet<AudioSource>((filter.PreserveAudioSources ?? new()).Where(a => a != null));
                foreach (var source in preserved) ValidateScope(scope, source.transform);
                var sources = avatar.GetComponentsInChildren<AudioSource>(true)
                    .Where(a => scope.Contains(a.transform) && !ALTOPlugin.IsEditorOnly(a.transform) && !preserved.Contains(a)).ToArray();
                components.UnionWith(sources);
                foreach (var modifier in sources.SelectMany(a => a.GetComponents<Component>()).Where(c => c != null).Distinct()
                    .Where(AudioOutputDependencies.IsModifier))
                    if (AudioOutputDependencies.Outputs(modifier).All(components.Contains)) components.Add(modifier);
            }
            foreach (var value in objects)
            {
                ValidateScope(scope, value.transform);
                if (value == avatar || value.GetComponent<VRCAvatarDescriptor>() != null)
                    throw new InvalidOperationException("ALTO Build Filter cannot remove an avatar root.");
            }
            foreach (var component in components)
            {
                ValidateScope(scope, component.transform);
                if (component is Transform || component is VRCAvatarDescriptor ||
                    (component is Animator && component.gameObject == avatar))
                    throw new InvalidOperationException("ALTO Build Filter cannot remove " + component.GetType().Name +
                        " here. Exclude a feature's child object to remove its entire hierarchy.");
            }
            var removed = new HashSet<Object>(components);
            foreach (var value in objects)
                foreach (var child in value.GetComponentsInChildren<Transform>(true))
                {
                    removed.Add(child.gameObject);
                    foreach (var component in child.GetComponents<Component>()) if (component != null) removed.Add(component);
                }
            var state = context.GetState<BuildFilterState>();
            state.Scope = scope;
            if ((filter.ExcludedRendererTypes & ~(RendererFilterTypes.ParticleSystems | RendererFilterTypes.Lines | RendererFilterTypes.Trails)) != 0)
                throw new InvalidOperationException("Choose valid renderer exclusion types.");
            state.RemoveAdditionalMaterialPasses = filter.RemoveAdditionalMaterialPasses;
            state.ExcludedRendererTypes = filter.ExcludedRendererTypes;
            foreach (var renderer in (filter.PreserveAutomaticRenderers ?? new()).Where(r => r != null))
            {
                ValidateScope(scope, renderer.transform);
                state.PreserveAutomaticRenderers.Add(ObjectRegistry.GetReference(renderer));
            }
            foreach (var rule in filter.MaterialExclusions ?? new())
            {
                if (rule == null || (rule.Renderer == null && rule.Material == null && rule.Slot == -1)) continue;
                if (rule.Slot < -1) throw new InvalidOperationException("Material exclusion Slot must be -1 or a nonnegative index.");
                if (rule.Renderer != null)
                {
                    ValidateScope(scope, rule.Renderer.transform);
                    if (removed.Contains(rule.Renderer)) continue;
                    state.SelectedRenderers.Add(rule, AnimationUtility.CalculateTransformPath(rule.Renderer.transform, avatar.transform));
                }
                state.Materials.Add(rule);
            }
            foreach (var rule in filter.BlendShapeExclusions ?? new())
            {
                if (rule == null || rule.BlendShapes == null || rule.BlendShapes.Count == 0) continue;
                if (rule.Renderer == null) throw new InvalidOperationException("BlendShape exclusion requires a Skinned Mesh Renderer.");
                ValidateScope(scope, rule.Renderer.transform);
                if (removed.Contains(rule.Renderer)) continue;
                if (!Analysis.MotionAnalysis.Finite(rule.DisplacementThreshold) || rule.DisplacementThreshold < 0 || rule.BlendShapes.Any(string.IsNullOrEmpty))
                    throw new InvalidOperationException("BlendShape exclusion requires valid names and a finite nonnegative displacement threshold.");
                state.BlendShapes.Add(new BlendShapeMeshExclusion
                {
                    Renderer = rule.Renderer, DisplacementThreshold = rule.DisplacementThreshold,
                    BlendShapes = new List<string>(rule.BlendShapes)
                });
                state.BlendShapeTargets[rule.Renderer] = AnimationUtility.CalculateTransformPath(rule.Renderer.transform, avatar.transform);
            }
            if (state.Materials.Count > 0)
                context.GetState<OptimizationBuildReport>().Initialize(avatar, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
            if (removed.Count == 0) return;
            ValidatePreservation(context, removed);
            ValidateIncomingReferences(context, filter, removed);
            var order = new List<Component>();
            var remaining = new HashSet<Component>(components.Where(c =>
                !objects.Any(o => c.transform.IsChildOf(o.transform))));
            while (remaining.Count > 0)
            {
                var next = remaining.FirstOrDefault(candidate => !remaining.Any(other =>
                    other != candidate && other.gameObject == candidate.gameObject && Requires(other, candidate)));
                if (next == null)
                    throw new InvalidOperationException("Excluded components form a RequireComponent cycle. Exclude their GameObject instead.");
                remaining.Remove(next);
                order.Add(next);
            }
            var report = context.GetState<OptimizationBuildReport>();
            report.Initialize(avatar, context.GetState<OptimizationBuildEnvironment>().Target.ToString());
            // Complete validation before deleting anything. Deletion order must not hide a conflict.
            var roots = objects.Where(o => !objects.Any(other => other != o && o.transform.IsChildOf(other.transform))).ToArray();
            foreach (var value in roots)
            {
                report.Messages.Add("Excluded object: " + AnimationUtility.CalculateTransformPath(value.transform, avatar.transform));
                Object.DestroyImmediate(value);
            }
            // Remove dependants before required components to satisfy Unity's RequireComponent rules.
            foreach (var next in order)
            {
                report.Messages.Add("Excluded component: " + AnimationUtility.CalculateTransformPath(next.transform, avatar.transform) +
                    " (" + next.GetType().Name + ")");
                Object.DestroyImmediate(next);
            }
        }

        private static void ValidateScope(OptimizationBuildScope scope, Transform target)
        {
            if (!scope.Contains(target))
                throw new InvalidOperationException("Excluded objects and components must belong to the Build Filter's Scope Root: " + target.name);
        }

        internal static bool Requires(Component dependant, Component dependency) =>
            dependant.GetType().GetCustomAttributes(typeof(RequireComponent), true).Cast<RequireComponent>()
                .SelectMany(a => new[] { a.m_Type0, a.m_Type1, a.m_Type2 })
                .Any(type => type != null && type.IsInstanceOfType(dependency));

        private static void ValidateIncomingReferences(BuildContext context, ALTOBuildFilter filter, HashSet<Object> removed)
        {
            var avatar = context.AvatarRootObject;
            var removedByObject = removed.OfType<Component>().GroupBy(c => c.gameObject).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var owner in avatar.GetComponentsInChildren<Component>(true))
            {
                if (owner == null || owner == filter || removed.Contains(owner) || owner is Transform ||
                    !BuildPlatformFilters.Includes(owner.transform, context)) continue;
                if (owner is ALTOPlatformOptimizationComponent policy && !OptimizationBuildScope.Applies(policy, context)) continue;
                if (removedByObject.TryGetValue(owner.gameObject, out var dependencies))
                    foreach (var dependency in dependencies.Where(d => Requires(owner, d)))
                        throw new InvalidOperationException(owner.GetType().Name + " requires excluded component " + dependency.GetType().Name +
                            ". Exclude both components or their GameObject.");
                using var serialized = new SerializedObject(owner);
                foreach (var property in serialized.ObjectProperties())
                {
                    // Selected source references have already been checked against their exclusions.
                    if (owner is ALTOAnimatorParameterPolicy &&
                        property.propertyPath.StartsWith("PreserveParameters.Sources.", StringComparison.Ordinal)) continue;
                    if (property.objectReferenceValue != null && removed.Contains(property.objectReferenceValue))
                        throw new InvalidOperationException("Cannot exclude a referenced object/component. " +
                            AnimationUtility.CalculateTransformPath(owner.transform, avatar.transform) + " (" + owner.GetType().Name +
                            ")." + property.propertyPath + " still refers to " + property.objectReferenceValue.name + ".");
                }
            }
        }

        private static void ValidatePreservation(BuildContext context, HashSet<Object> removed)
        {
            foreach (var policy in context.AvatarRootObject.GetComponentsInChildren<ALTOAnimatorParameterPolicy>(true)
                .Where(c => OptimizationBuildScope.Applies(c, context)))
            {
                if (removed.Contains(policy))
                    throw new InvalidOperationException("ALTO Build Filter cannot remove an active parameter preservation policy.");
                var names = new HashSet<string>(policy.PreserveParameters?.Names ?? new(), StringComparer.Ordinal);
                var prefixes = (policy.PreserveParameters?.Prefixes ?? new()).Where(p => !string.IsNullOrEmpty(p)).ToArray();
                if (names.Count > 0 || prefixes.Length > 0)
                {
                    var roots = removed.OfType<GameObject>().Where(o => o.transform.parent == null || !removed.Contains(o.transform.parent.gameObject)).Cast<Object>();
                    var singles = removed.OfType<Component>().Where(c => !removed.Contains(c.gameObject)).Cast<Object>();
                    foreach (var source in roots.Concat(singles))
                        foreach (var parameter in ParameterSourceResolver.Resolve(new AnimatorParameterSource { Source = source }, context.AvatarRootObject, context).Parameters)
                        {
                            if (parameter.Owner != null && !BuildPlatformFilters.Includes(parameter.Owner.transform, context)) continue;
                            string name = ParameterPolicyPass.ResolveName(context, parameter);
                            if (names.Contains(name) || prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
                                throw new InvalidOperationException("Build exclusion removes a source of preserved parameter '" + name + "'.");
                        }
                }
                foreach (var selection in policy.PreserveParameters?.Sources ?? new())
                {
                    if (selection?.Source == null) continue;
                    var result = ParameterSourceResolver.Resolve(selection, context.AvatarRootObject, context);
                    var exclusions = new HashSet<string>(selection.ExcludedNames ?? new(), StringComparer.Ordinal);
                    var parameters = result.Parameters.Where(p => !exclusions.Contains(p.Name) &&
                        (p.Owner == null || BuildPlatformFilters.Includes(p.Owner.transform, context))).ToArray();
                    if (parameters.Length == 0 && result.Diagnostics.Count == 0) continue;
                    bool conflict = parameters.Any(p => p.Owner != null && removed.Contains(p.Owner));
                    if (selection.Source is GameObject source)
                        conflict |= removed.Any(value => value is Component c &&
                            (c.gameObject == source || (selection.IncludeChildren && c.transform.IsChildOf(source.transform))));
                    else if (selection.Source is Component component) conflict |= removed.Contains(component);
                    if (conflict)
                        throw new InvalidOperationException("Build exclusion conflicts with preserved parameter source '" +
                            selection.Source.name + "'. Adjust the exclusion or the preservation source.");
                }
            }
        }
    }
}
