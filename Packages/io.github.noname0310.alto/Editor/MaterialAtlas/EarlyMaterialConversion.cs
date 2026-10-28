using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.util;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.MaterialAtlas
{
    // Reference conversion and texture packing are separate operations. Generators
    // see the requested shader; packing still consumes their final mesh/UV output.
    internal sealed class EarlyMaterialConversion
    {
        private const string ProvenanceTag = "NonameAtlasConvertedInput";
        internal sealed class Input
        {
            internal Material Source;
            internal int Group;
        }
        private readonly Dictionary<string, Input> inputs = new(StringComparer.Ordinal);
        private readonly Dictionary<Object, Input> origins = new();
        private readonly Dictionary<Renderer, Input[]> preparedRenderers = new();
        private Transform avatarRoot;

        internal void RegisterCopies(GameObject avatar)
        {
            foreach (var material in avatar.ReferencedAssets(traverseSaved: true).OfType<Material>().Distinct())
                if (inputs.TryGetValue(material.GetTag(ProvenanceTag, false, ""), out var input))
                    ObjectRegistry.TryRegisterReplacedObject(ObjectRegistry.GetReference(input.Source), material);
        }

        internal void ValidateSurvivingInputs(IEnumerable<Renderer> atlasRenderers)
        {
            HashSet<Input> survivingInputs = null;
            foreach (var prepared in preparedRenderers)
            {
                var renderer = prepared.Key;
                if (renderer == null || renderer.sharedMaterials.Any(m => m != null)) continue;
                if (!HasGeometry(renderer)) continue;
                // Generators can move rendering to copies while keeping the original
                // renderer as an animation source. Validate input availability across
                // selected outputs, not the continued ownership of a material slot.
                survivingInputs ??= new HashSet<Input>(atlasRenderers.Where(r => r != null && HasGeometry(r))
                    .SelectMany(r => r.sharedMaterials).Select(Find).Where(input => input != null));
                if (prepared.Value.All(survivingInputs.Contains)) continue;
                throw new InvalidOperationException("All material references were removed after early conversion on " +
                    AnimationUtility.CalculateTransformPath(renderer.transform, avatarRoot) +
                    ", and its converted inputs do not survive on the selected atlas renderers. " +
                    "Check the Reference Material, renderer selection and generator diagnostics; atlas packing cannot recover discarded inputs.");
            }
        }

        private static bool HasGeometry(Renderer renderer)
        {
            var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh :
                renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
            return mesh != null && Enumerable.Range(0, mesh.subMeshCount).Any(s => mesh.GetIndexCount(s) != 0);
        }

        internal Input Find(Material material)
        {
            if (material == null) return null;
            // Material copies preserve override tags even when a generator does
            // not participate in NDMF's object registry. Tokens belong to this build.
            if (inputs.TryGetValue(material.GetTag(ProvenanceTag, false, ""), out var tagged)) return tagged;
            return origins.TryGetValue(ObjectRegistry.GetReference(material).Object, out var source) ? source : null;
        }

        internal static EarlyMaterialConversion Apply(BuildContext context, MaterialAtlasState state)
        {
            var result = new EarlyMaterialConversion { avatarRoot = context.AvatarRootTransform };
            var selected = new HashSet<Renderer>(MaterialAtlasPass.SelectRenderers(context.AvatarRootObject, state));
            var candidates = selected.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct()
                .Select(m => (Material: m, Group: state.Group(m)))
                .Where(p => p.Group >= 0).ToDictionary(p => p.Material, p => p.Group);

            // Follow material references through clips, Animator graphs and asset
            // settings. Clone only ancestors of replaced materials. This includes
            // material-swap keys without changing the persistent source clip.
            var components = context.AvatarRootObject.GetComponentsInChildren<Component>(true)
                .Where(c => c != null && !(c is Transform) && !(c is ALTOOptimizationComponent) &&
                    !(c is ALTOTraceAndOptimizeBlendShapes) && !(c is BlendShapeOptimizationExclude)).ToArray();
            var parents = new Dictionary<Object, HashSet<Object>>();
            var visited = new HashSet<Object>();
            void Visit(Object asset)
            {
                if (!visited.Add(asset) || asset is Material) return;
                using var serialized = new SerializedObject(asset);
                foreach (var property in serialized.ObjectProperties())
                {
                    var child = property.objectReferenceValue;
                    if (child == null || child is GameObject || child is Component || child is Shader || child is Texture || child is MonoScript) continue;
                    if (!parents.TryGetValue(child, out var uses)) parents.Add(child, uses = new());
                    uses.Add(asset); Visit(child);
                }
            }
            foreach (var component in components) Visit(component);
            bool wholeAvatar = state.Scope.Root == context.AvatarRootTransform &&
                state.Settings.TargetSelection == RendererSelectionMode.AllRenderersInScope;
            var materialCurves = visited.OfType<AnimationClip>().SelectMany(clip =>
                AnimationUtility.GetObjectReferenceCurveBindings(clip).Where(b => typeof(Renderer).IsAssignableFrom(b.type) &&
                    b.propertyName.StartsWith(MaterialFilterPass.SlotPrefix, StringComparison.Ordinal)).Select(binding =>
                    (Clip: clip, Binding: binding, Materials: AnimationUtility.GetObjectReferenceCurve(clip, binding)
                        .Select(k => k.value).OfType<Material>().Distinct().ToArray()))).ToArray();
            if (wholeAvatar)
                foreach (var curve in materialCurves.Where(c => c.Binding.type == typeof(MeshRenderer) || c.Binding.type == typeof(SkinnedMeshRenderer)))
                foreach (var material in curve.Materials)
                {
                    int group = state.Group(material);
                    if (group >= 0) candidates[material] = group;
                }
            foreach (var curve in materialCurves)
                if (curve.Materials.Any(candidates.ContainsKey) && (!wholeAvatar ||
                    curve.Binding.type != typeof(MeshRenderer) && curve.Binding.type != typeof(SkinnedMeshRenderer)))
                    throw new InvalidOperationException("Before Generators conversion cannot isolate this material animation: " +
                        curve.Clip.name + "/" + curve.Binding.path + ". Use the whole avatar with All Renderers In Scope, separate the shared material, or choose After Generators.");
            if (candidates.Count == 0) return result;
            var affected = new HashSet<Object>(candidates.Keys.Cast<Object>());
            var pending = new Queue<Object>(affected);
            while (pending.Count != 0)
            {
                var asset = pending.Dequeue();
                if (!parents.TryGetValue(asset, out var uses)) continue;
                foreach (var user in uses) if (affected.Add(user)) pending.Enqueue(user);
            }
            foreach (var consumer in affected.OfType<Component>())
                if (!state.Scope.Contains(consumer.transform) || consumer is Renderer renderer && !selected.Contains(renderer))
                    throw new InvalidOperationException("Before Generators material conversion shares an input with an unselected or out-of-scope consumer: " +
                        AnimationUtility.CalculateTransformPath(consumer.transform, context.AvatarRootTransform) +
                        ". Include that consumer, separate the shared asset, or choose After Generators.");
            foreach (var candidate in candidates)
                foreach (var mapping in state.Settings.MergeGroups[candidate.Value].TextureMappings)
                {
                    var source = candidate.Key;
                    if (!source.HasTexture(mapping.SourceProperty)) continue;
                    var texture = source.GetTexture(mapping.SourceProperty);
                    if (texture != null && !(texture is Texture2D))
                        throw new InvalidOperationException("Before Generators conversion requires static Texture2D inputs: " + source.name);
                    var scale = source.GetTextureScale(mapping.SourceProperty); var offset = source.GetTextureOffset(mapping.SourceProperty);
                    if (!MotionAnalysis.Finite(scale.x) || !MotionAnalysis.Finite(scale.y) || !MotionAnalysis.Finite(offset.x) || !MotionAnalysis.Finite(offset.y))
                        throw new InvalidOperationException("Before Generators conversion found non-finite texture coordinates: " + source.name);
                }

            var replacements = new Dictionary<Object, Object>();
            var owned = new List<Object>();
            try
            {
                foreach (var candidate in candidates)
                {
                    var group = state.Settings.MergeGroups[candidate.Value];
                    var source = candidate.Key;
                    var material = new Material(group.ReferenceMaterial) { name = source.name + " (Reference converted)" };
                    owned.Add(material);
                    foreach (var mapping in group.TextureMappings)
                    {
                        var texture = source.HasTexture(mapping.SourceProperty) ? source.GetTexture(mapping.SourceProperty) : null;
                        if (texture == null)
                        {
                            var solid = new Texture2D(4, 4, TextureFormat.RGBA32, false, mapping.Linear) { name = "Atlas missing texture" };
                            owned.Add(solid); solid.SetPixels(Enumerable.Repeat(mapping.MissingTextureColor, 16).ToArray()); solid.Apply(); texture = solid;
                            material.SetTextureScale(mapping.DestinationProperty, Vector2.one);
                            material.SetTextureOffset(mapping.DestinationProperty, Vector2.zero);
                        }
                        else
                        {
                            material.SetTextureScale(mapping.DestinationProperty, source.GetTextureScale(mapping.SourceProperty));
                            material.SetTextureOffset(mapping.DestinationProperty, source.GetTextureOffset(mapping.SourceProperty));
                        }
                        material.SetTexture(mapping.DestinationProperty, texture);
                    }
                    string token = Guid.NewGuid().ToString("N");
                    material.SetOverrideTag(ProvenanceTag, token);
                    var input = new Input { Source = source, Group = candidate.Value };
                    result.inputs.Add(token, input);
                    result.origins[ObjectRegistry.GetReference(source).Object] = input;
                    replacements.Add(source, material);
                }
                foreach (var asset in affected.Where(a => !(a is Component) && !(a is Material)))
                {
                    Object copy = asset switch
                    {
                        AnimatorController => new AnimatorController(),
                        AnimatorStateMachine => new AnimatorStateMachine(),
                        AnimatorState => new AnimatorState(),
                        AnimatorStateTransition => new AnimatorStateTransition(),
                        AnimatorTransition => new AnimatorTransition(),
                        BlendTree => new BlendTree(),
                        _ => null
                    };
                    if (copy != null) EditorUtility.CopySerialized(asset, copy);
                    else copy = Object.Instantiate(asset);
                    copy.name = asset.name; owned.Add(copy); replacements.Add(asset, copy);
                }
                // Resolve cycles and shared graphs only after every copy exists.
                // Register before exposing copies to component validation callbacks.
                foreach (var pair in replacements) ObjectRegistry.RegisterReplacedObject(pair.Key, pair.Value);
                foreach (var target in affected.OfType<Component>().Cast<Object>().Concat(replacements.Values.Where(o => !(o is Material))))
                {
                    using var serialized = new SerializedObject(target);
                    bool changed = false;
                    foreach (var property in serialized.ObjectProperties())
                        if (property.objectReferenceValue != null && replacements.TryGetValue(property.objectReferenceValue, out var replacement))
                        { property.objectReferenceValue = replacement; changed = true; }
                    if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                foreach (var pair in replacements)
                {
                    // These native animation caches require their public setters;
                    // changing serialized PPtr data alone can leave stale curves.
                    if (pair.Key is AnimationClip original && pair.Value is AnimationClip clip)
                    {
                        foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(original))
                        {
                            var keys = AnimationUtility.GetObjectReferenceCurve(original, binding);
                            bool changed = false;
                            for (int i = 0; i < keys.Length; i++)
                                if (keys[i].value != null && replacements.TryGetValue(keys[i].value, out var replacement))
                                { keys[i].value = replacement; changed = true; }
                            if (changed) AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);
                        }
                        var events = AnimationUtility.GetAnimationEvents(original);
                        foreach (var animationEvent in events)
                            if (animationEvent.objectReferenceParameter != null && replacements.TryGetValue(animationEvent.objectReferenceParameter, out var replacement))
                                animationEvent.objectReferenceParameter = replacement;
                        AnimationUtility.SetAnimationEvents(clip, events);
                    }
                    if (pair.Key is AnimatorOverrideController originalOverride && pair.Value is AnimatorOverrideController outputOverride)
                    {
                        var baseController = originalOverride.runtimeAnimatorController;
                        outputOverride.runtimeAnimatorController = replacements.TryGetValue(baseController, out var controllerCopy)
                            ? (RuntimeAnimatorController)controllerCopy : baseController;
                        var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>(); originalOverride.GetOverrides(overrides);
                        outputOverride.ApplyOverrides(overrides.Select(p => new KeyValuePair<AnimationClip, AnimationClip>(
                            replacements.TryGetValue(p.Key, out var key) ? (AnimationClip)key : p.Key,
                            p.Value != null && replacements.TryGetValue(p.Value, out var value) ? (AnimationClip)value : p.Value)).ToList());
                    }
                }
                foreach (var asset in owned) context.AssetSaver.SaveAsset(asset);
                foreach (var renderer in selected)
                {
                    var rendererInputs = renderer.sharedMaterials.Select(result.Find).Where(input => input != null).Distinct().ToArray();
                    if (rendererInputs.Length != 0) result.preparedRenderers.Add(renderer, rendererInputs);
                }
                context.GetState<OptimizationBuildReport>().Messages.Add("Converted " + candidates.Count +
                    " material references before generators; cloned " + (replacements.Count - candidates.Count) +
                    " dependent assets. Final texture packing remains after generators.");
                return result;
            }
            catch
            {
                foreach (var asset in owned) if (asset != null && !EditorUtility.IsPersistent(asset)) Object.DestroyImmediate(asset);
                throw;
            }
        }
    }
}
