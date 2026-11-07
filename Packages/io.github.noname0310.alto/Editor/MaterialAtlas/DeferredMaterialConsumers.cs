using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.util;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.MaterialAtlas
{
    // Keep excluded consumers on their original shader while generators run.
    // A successful later exclusion must discharge every use of these copies,
    // including uses introduced by generators. Never infer removal from a flag.
    internal sealed class DeferredMaterialConsumers
    {
        private const string ProvenanceTag = "NonameAtlasDeferredConsumer";
        private readonly Dictionary<string, string> tokens = new(StringComparer.Ordinal);
        private readonly Dictionary<ObjectReference, string> origins = new();

        internal void Isolate(Renderer renderer, Transform avatarRoot, ISet<Material> sources, ICollection<Object> owned)
        {
            string path = AnimationUtility.CalculateTransformPath(renderer.transform, avatarRoot);
            var copies = new Dictionary<Material, Material>();
            using var serialized = new SerializedObject(renderer);
            foreach (var property in serialized.ObjectProperties())
            {
                if (!(property.objectReferenceValue is Material source) || !sources.Contains(source)) continue;
                if (!copies.TryGetValue(source, out var copy))
                {
                    copy = new Material(source) { name = source.name + " (Pending renderer exclusion)" };
                    owned.Add(copy); copies.Add(source, copy);
                    string token = Guid.NewGuid().ToString("N");
                    copy.SetOverrideTag(ProvenanceTag, token);
                    tokens.Add(token, path);
                    origins.Add(ObjectRegistry.GetReference(copy), path);
                    // Do not register this as a replacement for source. The atlas
                    // and excluded consumers deliberately have different copies.
                }
                property.objectReferenceValue = copy;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        internal void Validate(GameObject avatar, IEnumerable<VirtualAnimatorController> controllers)
        {
            if (tokens.Count == 0) return;
            var visited = new HashSet<Object>();
            void Visit(Object asset, string owner)
            {
                if (asset == null || !visited.Add(asset) || asset is GameObject || asset is Transform ||
                    asset is Shader || asset is Texture || asset is Mesh || asset is MonoScript) return;
                if (asset is Material material)
                {
                    if (tokens.TryGetValue(material.GetTag(ProvenanceTag, false, ""), out string source) ||
                        origins.TryGetValue(ObjectRegistry.GetReference(material), out source))
                        throw new InvalidOperationException("Before Generators material conversion deferred a shared renderer exclusion, " +
                            "but its original material is still used after rendering filters: " + owner +
                            " (input from " + source + "). A retained reference or generated consumer prevents removal. " +
                            "Adjust Rendering Filter preservation/dependencies, separate the shared material, or choose After Generators.");
                    return;
                }
                using var serialized = new SerializedObject(asset);
                foreach (var property in serialized.ObjectProperties())
                {
                    var child = property.objectReferenceValue;
                    if (child is GameObject || child is Component) continue;
                    // These controllers are represented by the current virtual
                    // graphs below; their physical clips may contain stale keys.
                    if ((asset is Animator || asset is VRCAvatarDescriptor) && child is RuntimeAnimatorController) continue;
                    Visit(child, owner);
                }
            }
            foreach (var component in avatar.GetComponentsInChildren<Component>(true).Where(c => c != null))
                Visit(component, AnimationUtility.CalculateTransformPath(component.transform, avatar.transform) +
                    " (" + component.GetType().Name + ")");
            var nodes = controllers.SelectMany(c => c.AllReachableNodes()).Distinct().ToArray();
            foreach (var behaviour in nodes.OfType<VirtualState>().SelectMany(s => s.Behaviours))
                Visit(behaviour, behaviour.name);
            foreach (var clip in nodes.OfType<VirtualClip>())
            {
                var source = MotionAnalysis.Source(clip);
                var bindings = clip.IsMarkerClip && source != null
                    ? AnimationUtility.GetObjectReferenceCurveBindings(source) : clip.GetObjectCurveBindings();
                foreach (var binding in bindings)
                {
                    var keys = clip.IsMarkerClip && source != null
                        ? AnimationUtility.GetObjectReferenceCurve(source, binding) : clip.GetObjectCurve(binding);
                    foreach (var key in keys ?? Array.Empty<ObjectReferenceKeyframe>()) Visit(key.value, clip.Name + "/" + binding.path);
                }
                if (source != null)
                    foreach (var animationEvent in source.events) Visit(animationEvent.objectReferenceParameter, clip.Name + " (animation event)");
            }
        }
    }
}
