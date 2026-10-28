using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Build;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.ParameterPolicy
{
    internal static class ParameterFreezeScope
    {
        internal static void Validate(GameObject avatar, OptimizationBuildScope scope,
            IDictionary<object, VirtualAnimatorController> entries, IEnumerable<string> names,
            IReadOnlyDictionary<string, string> paths = null)
        {
            if (scope.Contains(avatar.transform)) return;
            var controllers = entries.Values.Distinct().ToArray();
            var graph = AnimatorParameterDependencies.Collect(avatar, controllers, controllerEntries: entries);
            var selected = new HashSet<string>(names, StringComparer.Ordinal);
            var affected = graph.AffectedBy(selected);
            var expressions = avatar.GetComponent<VRCAvatarDescriptor>()?.expressionParameters;
            if (expressions != null && expressions.parameters.Any(p => p != null && affected.Contains(p.name) && !selected.Contains(p.name)))
                throw new InvalidOperationException("Frozen inputs affect another Expression Parameter outside Scope Root. Use the avatar root scope.");
            foreach (var entry in entries)
            {
                var animationRoot = entry.Key is Animator animator ? animator.transform : avatar.transform;
                foreach (var layer in entry.Value.Layers)
                {
                    var nodes = layer.AllReachableNodes().ToArray();
                    if (!ParameterSpecialization.Reads(nodes).Any(n => n != null && affected.Contains(n))) continue;
                    if (ParameterSpecialization.Behaviours(nodes).Any(b => b != null && !(b is VRC_AvatarParameterDriver)))
                        throw new InvalidOperationException("A frozen parameter controls state behaviour effects outside the proven scope: " + layer.Name);
                    foreach (var clip in nodes.OfType<VirtualClip>())
                    {
                        var source = MotionAnalysis.Source(clip);
                        if (clip.IsMarkerClip || source == null || source.humanMotion || source.events.Length > 0)
                            throw new InvalidOperationException("Cannot prove the scope of a frozen parameter's motion: " + clip.Name);
                        foreach (var binding in clip.GetFloatCurveBindings().Concat(clip.GetObjectCurveBindings()))
                        {
                            if (graph.IsParameter(clip, binding)) continue;
                            var targets = AnimationUsage.Resolve(avatar.transform, animationRoot, binding.path, paths);
                            if (targets.Count == 0 || targets.Any(t => !scope.Contains(t)))
                                throw new InvalidOperationException("Frozen parameter affects an animation target outside Scope Root or an unresolved target: " + clip.Name + " / " + binding.path);
                            // Transform/constraint/physics changes can influence external renderers through
                            // bones or references. Until that graph is proven, accept local render outputs only.
                            if (typeof(Renderer).IsAssignableFrom(binding.type)) continue;
                            if (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive" &&
                                targets.All(t => t.GetComponentsInChildren<Component>(true).All(c =>
                                    c is Transform || c is Renderer || c is MeshFilter))) continue;
                            throw new InvalidOperationException("Cannot prove that this frozen output stays inside Scope Root: " +
                                binding.path + " / " + binding.propertyName + ". Use the avatar root scope.");
                        }
                    }
                }
            }
        }
    }
}
