using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Build
{
    // Rendering semantics reviewed against AAO 1.9.20's material-slot cleanup.
    // Independently implemented explicit exclusion policy; see CREDITS.md.
    internal static class AutomaticRenderingRules
    {
        internal static bool Preserved(BuildFilterState state, Renderer renderer) =>
            state.PreserveAutomaticRenderers.Contains(ObjectRegistry.GetReference(renderer));

        internal static List<MaterialExclusion> MaterialRules(BuildFilterState state, IEnumerable<Renderer> renderers)
        {
            var rules = new List<MaterialExclusion>(state.Materials);
            if (!state.RemoveAdditionalMaterialPasses) return rules;
            foreach (var renderer in renderers)
            {
                if (Preserved(state, renderer)) continue;
                var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh :
                    renderer is MeshRenderer && renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
                // There is no last submesh to repeat when a renderer has no geometry.
                if (mesh == null || mesh.subMeshCount == 0) continue;
                for (int slot = mesh.subMeshCount; slot < renderer.sharedMaterials.Length; slot++)
                    rules.Add(new MaterialExclusion { Renderer = renderer, Slot = slot });
            }
            return rules;
        }

        internal static void RemoveRenderers(GameObject avatar, BuildFilterState state,
            IEnumerable<VirtualAnimatorController> controllers, ICollection<string> messages)
        {
            if (state.ExcludedRendererTypes == RendererFilterTypes.None) return;
            var selected = new HashSet<Component>();
            var pairs = new List<(Renderer Renderer, ParticleSystem Simulation)>();
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (!state.Scope.Contains(renderer.transform) || ALTOPlugin.IsEditorOnly(renderer.transform) || Preserved(state, renderer)) continue;
                bool remove = renderer is ParticleSystemRenderer && (state.ExcludedRendererTypes & RendererFilterTypes.ParticleSystems) != 0 ||
                    renderer is LineRenderer && (state.ExcludedRendererTypes & RendererFilterTypes.Lines) != 0 ||
                    renderer is TrailRenderer && (state.ExcludedRendererTypes & RendererFilterTypes.Trails) != 0;
                if (!remove) continue;
                selected.Add(renderer);
                if (renderer is ParticleSystemRenderer && renderer.GetComponent<ParticleSystem>() is ParticleSystem simulation)
                { selected.Add(simulation); pairs.Add((renderer, simulation)); }
            }
            if (selected.Count == 0) return;
            var dependencies = new List<(Object Owner, Component Target)>();
            var nodes = controllers.SelectMany(c => c.AllReachableNodes()).Distinct().ToArray();
            foreach (var clip in nodes.OfType<VirtualClip>())
            {
                var source = MotionAnalysis.Source(clip);
                if (source == null || source.events.Length > 0)
                { messages.Add("Automatic renderer rules retained all candidates: animation side effects are not inspectable."); return; }
                var bindings = clip.IsMarkerClip ? AnimationUtility.GetObjectReferenceCurveBindings(source) : clip.GetObjectCurveBindings();
                foreach (var binding in bindings)
                {
                    var keys = clip.IsMarkerClip ? AnimationUtility.GetObjectReferenceCurve(source, binding) : clip.GetObjectCurve(binding);
                    foreach (var key in keys ?? Array.Empty<ObjectReferenceKeyframe>())
                        if (key.value is Component target && selected.Contains(target)) dependencies.Add((source, target));
                }
            }
            var owners = avatar.GetComponentsInChildren<Component>(true).Where(c => c != null).Cast<Object>()
                .Concat(nodes.OfType<VirtualState>().SelectMany(s => s.Behaviours)).Distinct().ToArray();
            foreach (var owner in owners)
            {
                if (owner is Transform) continue;
                if (owner is Component component)
                    foreach (var dependency in selected.Where(c => c.gameObject == component.gameObject && BuildFilterPass.Requires(component, c)))
                        dependencies.Add((owner, dependency));
                using var serialized = new SerializedObject(owner);
                var property = serialized.GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue is Component target && selected.Contains(target))
                        dependencies.Add((owner, target));
            }
            bool changed;
            do
            {
                changed = false;
                foreach (var dependency in dependencies)
                    if (!(dependency.Owner is Component c && selected.Contains(c)) && selected.Remove(dependency.Target))
                    {
                        messages.Add("Automatic renderer rules retained " + dependency.Target.name + " (" + dependency.Target.GetType().Name +
                            "): referenced by " + dependency.Owner.name + ".");
                        changed = true;
                    }
                // Keep simulation and drawing together if either is needed by a retained consumer.
                foreach (var pair in pairs)
                    if (!selected.Contains(pair.Renderer) || !selected.Contains(pair.Simulation))
                    { changed |= selected.Remove(pair.Renderer); changed |= selected.Remove(pair.Simulation); }
            } while (changed);
            var order = new List<Component>();
            var remaining = new HashSet<Component>(selected);
            while (remaining.Count > 0)
            {
                var next = remaining.FirstOrDefault(candidate => !remaining.Any(other => other != candidate &&
                    other.gameObject == candidate.gameObject && BuildFilterPass.Requires(other, candidate)));
                if (next == null)
                { messages.Add("Automatic renderer rules retained candidates with cyclic component dependencies."); return; }
                order.Add(next); remaining.Remove(next);
            }
            foreach (var component in order)
            {
                messages.Add("Automatic renderer exclusion: " + AnimationUtility.CalculateTransformPath(component.transform, avatar.transform) +
                    " (" + component.GetType().Name + ").");
                Object.DestroyImmediate(component);
            }
        }
    }
}
