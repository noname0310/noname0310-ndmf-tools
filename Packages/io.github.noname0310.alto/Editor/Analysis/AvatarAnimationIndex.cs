using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // One index for all animation roots, including shared clips, aliases and immutable
    // SDK marker clips. A binding is a writer, not evidence that its target is observed.
    internal sealed class AvatarAnimationIndex
    {
        internal sealed class BindingUse
        {
            internal VirtualClip Clip;
            internal EditorCurveBinding Binding;
            internal Transform[] Targets;
            internal Transform[] Roots;
            internal bool Complete;
            internal bool Immutable;
            internal bool PreservedInput;
            // Lossy output rewrites must respect input preservation. Mutable clips can
            // still be remapped equivalently, or lose bindings to proven unobserved objects.
            internal bool Protected => Immutable || PreservedInput;
            internal AnimationCurve Curve;
            internal ObjectReferenceKeyframe[] ObjectKeys;

            internal IEnumerable<UnityEngine.Object> Objects()
            {
                foreach (var target in Targets)
                {
                    if (Binding.type == typeof(GameObject)) yield return target.gameObject;
                    else if (Binding.type != null && typeof(Component).IsAssignableFrom(Binding.type))
                        foreach (var component in target.GetComponents(Binding.type)) yield return component;
                }
            }
        }

        internal readonly List<BindingUse> Bindings = new();
        internal readonly HashSet<string> PreservedParameters = new(StringComparer.Ordinal);
        internal VirtualAnimatorController[] Controllers;
        internal IDictionary<object, VirtualAnimatorController> ControllerEntries;
        internal IReadOnlyDictionary<string, string> Paths;
        internal string UnsafeReason;
        private readonly HashSet<(UnityEngine.Object, string)> writes = new();
        private readonly Dictionary<UnityEngine.Object, List<BindingUse>> writers = new();
        private readonly Dictionary<VirtualAnimatorController, HashSet<Type>> controllerBindingTypes = new();

        internal bool Writes(UnityEngine.Object target, string property) => writes.Contains((target, property));
        internal IReadOnlyList<BindingUse> ForObject(UnityEngine.Object target) =>
            target != null && writers.TryGetValue(target, out var found) ? found : Array.Empty<BindingUse>();
        internal bool MayAnimateType(VirtualAnimatorController controller, Type type) =>
            !controllerBindingTypes.TryGetValue(controller, out var types) ||
            types.Any(bound => bound == null || type.IsAssignableFrom(bound) || bound.IsAssignableFrom(type));

        internal static AvatarAnimationIndex Create(GameObject avatar,
            IDictionary<object, VirtualAnimatorController> controllers, IReadOnlyDictionary<string, string> paths,
            IEnumerable<string> preservedParameters = null)
        {
            var result = new AvatarAnimationIndex { Controllers = controllers.Values.Distinct().ToArray(), ControllerEntries = controllers, Paths = paths };
            var owners = new Dictionary<VirtualClip, HashSet<Transform>>();
            var clipControllers = new Dictionary<VirtualClip, HashSet<VirtualAnimatorController>>();
            foreach (var pair in controllers)
            {
                if (!result.controllerBindingTypes.ContainsKey(pair.Value)) result.controllerBindingTypes.Add(pair.Value, new());
                foreach (var clip in pair.Value.AllReachableNodes().OfType<VirtualClip>())
                {
                    if (!owners.TryGetValue(clip, out var roots)) owners.Add(clip, roots = new());
                    roots.Add(pair.Key is Animator animator ? animator.transform : avatar.transform);
                    if (!clipControllers.TryGetValue(clip, out var users)) clipControllers.Add(clip, users = new());
                    users.Add(pair.Value);
                }
            }
            var preserved = result.PreservedParameters;
            preserved.UnionWith(preservedParameters ?? Array.Empty<string>());
            var dependencies = preserved.Count == 0 ? null : AnimatorParameterDependencies.Collect(avatar, result.Controllers,
                forExpressions: true, controllerEntries: controllers);
            var resolutions = new Dictionary<(VirtualClip, string), (Transform[] Targets, bool Complete)>();
            foreach (var pair in owners)
            {
                var clip = pair.Key;
                var source = MotionAnalysis.Source(clip);
                if (source == null || source.events.Length > 0)
                    result.UnsafeReason ??= "animation events or unavailable clip metadata: " + clip.Name;
                bool immutable = clip.IsMarkerClip || clip.Legacy || clip.AdditiveReferencePoseClip != null ||
                    source == null || source.humanMotion;
                bool preservedInput = dependencies?.MotionDependsOn(clip, preserved) == true;
                var floats = clip.IsMarkerClip && source != null ? AnimationUtility.GetCurveBindings(source) : clip.GetFloatCurveBindings();
                var objects = clip.IsMarkerClip && source != null ? AnimationUtility.GetObjectReferenceCurveBindings(source) : clip.GetObjectCurveBindings();
                foreach (var binding in floats.Concat(objects))
                {
                    foreach (var controller in clipControllers[clip]) result.controllerBindingTypes[controller].Add(binding.type);
                    if (!resolutions.TryGetValue((clip, binding.path), out var resolution))
                    {
                        var targets = new HashSet<Transform>();
                        bool complete = true;
                        foreach (var owner in pair.Value)
                        {
                            var found = AnimationUsage.Resolve(avatar.transform, owner, binding.path, paths);
                            complete &= found.Count == 1;
                            targets.UnionWith(found);
                        }
                        resolutions.Add((clip, binding.path), resolution = (targets.ToArray(), complete));
                    }
                    var use = new BindingUse
                    {
                        Clip = clip, Binding = binding, Targets = resolution.Targets, Roots = pair.Value.ToArray(), Complete = resolution.Complete,
                        Immutable = immutable, PreservedInput = preservedInput,
                        Curve = binding.isPPtrCurve ? null : clip.IsMarkerClip && source != null ?
                            AnimationUtility.GetEditorCurve(source, binding) : clip.GetFloatCurve(binding),
                        ObjectKeys = !binding.isPPtrCurve ? null : clip.IsMarkerClip && source != null ?
                            AnimationUtility.GetObjectReferenceCurve(source, binding) : clip.GetObjectCurve(binding)
                    };
                    result.Bindings.Add(use);
                    foreach (var target in use.Objects())
                    {
                        if (target == null) continue;
                        result.writes.Add((target, binding.propertyName));
                        if (!result.writers.TryGetValue(target, out var uses)) result.writers.Add(target, uses = new());
                        uses.Add(use);
                    }
                }
            }
            return result;
        }
    }
}
