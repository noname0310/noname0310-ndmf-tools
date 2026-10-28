using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.ObjectOptimization
{
    internal static class InvariantObjectCurves
    {
        private sealed class Use
        {
            internal object Owner;
            internal VirtualLayer Layer;
            internal VirtualClip Clip;
            internal EditorCurveBinding Binding;
            internal Object Target;
            internal Transform Transform;
            internal string Group;
            internal float Initial;
        }

        private static readonly Dictionary<string, string[]> TransformGroups = new()
        {
            ["m_LocalPosition"] = new[] { "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z" },
            ["m_LocalScale"] = new[] { "m_LocalScale.x", "m_LocalScale.y", "m_LocalScale.z" },
            ["m_LocalRotation"] = new[] { "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w" }
        };

        internal static void Apply(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths, ObjectOptimizationState settings, AnimatorReachability analysis,
            ICollection<string> messages)
        {
            var uses = new List<Use>();
            var roots = new Dictionary<VirtualClip, Transform>();
            var ambiguous = new HashSet<VirtualClip>();
            var unsafeTransforms = TransformMutationAnalysis.NativeWriters(avatar);
            var unsafeVisibility = VisibilityMutationAnalysis.NativeWriters(avatar);
            foreach (var entry in controllers)
            foreach (var layer in entry.Value.Layers)
            foreach (var clip in layer.AllReachableNodes().OfType<VirtualClip>().Distinct())
            {
                var root = entry.Key is Animator animator ? animator.transform : avatar.transform;
                if (roots.TryGetValue(clip, out var other) && root != other) ambiguous.Add(clip); else roots[clip] = root;
                foreach (var binding in clip.GetObjectCurveBindings())
                    foreach (var key in clip.GetObjectCurve(binding))
                        if (key.value is Transform transform) unsafeTransforms.Add(transform);
                foreach (var binding in clip.GetFloatCurveBindings())
                {
                    if (binding.type != typeof(Transform) && binding.type != typeof(GameObject) &&
                        !typeof(Renderer).IsAssignableFrom(binding.type)) continue;
                    var target = Resolve(root, avatar.transform, binding.path, paths);
                    if (target == null) continue;
                    if (binding.type == typeof(Transform) && !TransformGroups.Values.Any(g => g.Contains(binding.propertyName)))
                    { unsafeTransforms.Add(target); continue; }
                    if (target == avatar.transform || !settings.Scope.Contains(target) || settings.Protected(target)) continue;
                    var use = new Use { Owner = entry.Key, Layer = layer, Clip = clip, Binding = binding, Transform = target };
                    if (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive" && settings.Component.BakeConstantVisibility)
                    { use.Target = target.gameObject; use.Group = binding.propertyName; use.Initial = target.gameObject.activeSelf ? 1 : 0; }
                    else if (typeof(Renderer).IsAssignableFrom(binding.type) && binding.propertyName == "m_Enabled" && settings.Component.BakeConstantVisibility)
                    {
                        var renderers = target.GetComponents(binding.type).OfType<Renderer>().ToArray();
                        if (renderers.Length != 1 || settings.Protected(renderers[0])) continue;
                        use.Target = renderers[0]; use.Group = binding.propertyName; use.Initial = renderers[0].enabled ? 1 : 0;
                    }
                    else if (binding.type == typeof(Transform) && settings.Component.BakeConstantTransforms && !(target is RectTransform))
                    {
                        use.Target = target; use.Group = binding.propertyName.Substring(0, binding.propertyName.LastIndexOf('.'));
                        int channel = "xyzw".IndexOf(binding.propertyName[binding.propertyName.Length - 1]);
                        use.Initial = use.Group == "m_LocalPosition" ? target.localPosition[channel] :
                            use.Group == "m_LocalScale" ? target.localScale[channel] : target.localRotation[channel];
                    }
                    else continue;
                    uses.Add(use);
                }
            }

            var removals = new Dictionary<VirtualClip, HashSet<EditorCurveBinding>>();
            var bindings = new Dictionary<VirtualMotion, HashSet<EditorCurveBinding>>();
            var fxOutputs = new FxPropertyOutput(controllers, analysis);
            int targets = 0;
            foreach (var group in uses.GroupBy(u => (u.Target, u.Group)))
            {
                var first = group.First();
                if (unsafeVisibility.Contains(first.Target)) continue;
                if (first.Target is Transform && (unsafeTransforms.Contains(first.Transform) ||
                    !TransformGroups[first.Group].All(p => group.Any(u => u.Binding.propertyName == p)))) continue;
                // Initialization and enabled/active callbacks can observe the scene
                // value before the first Animator evaluation. Constant motion output
                // alone does not authorize changing that initial state.
                if (group.Any(u => ambiguous.Contains(u.Clip) || u.Clip.IsMarkerClip || u.Clip.Legacy ||
                    u.Clip.AdditiveReferencePoseClip != null || MotionAnalysis.Source(u.Clip) == null ||
                    MotionAnalysis.Source(u.Clip).events.Length != 0 ||
                    u.Layer.BlendingMode == AnimatorLayerBlendingMode.Additive || u.Layer.AvatarMask != null ||
                    u.Layer.SyncedLayerIndex != -1 ||
                    u.Owner is VRCAvatarDescriptor.AnimLayerType type && type == VRCAvatarDescriptor.AnimLayerType.Additive)) continue;
                bool proven = true;
                foreach (var channel in group.GroupBy(u => u.Binding.propertyName))
                {
                    // A property reached through aliases or different Animator roots is left intact.
                    if (channel.Select(u => u.Binding).Distinct().Count() != 1) { proven = false; break; }
                    var use = channel.First();
                    if (!(first.Target is Transform) && fxOutputs.MatchesFinalOverride(
                        channel.Select(u => (u.Owner, u.Layer)), use.Binding, use.Initial, bindings)) continue;
                    var cache = new Dictionary<VirtualMotion, bool>();
                    foreach (var layer in channel.Select(u => u.Layer).Distinct())
                    {
                        var states = layer.AllReachableNodes().OfType<VirtualState>().Where(analysis.IsReachable).ToArray();
                        if (states.Length == 0 || states.Any(s =>
                            !ConstantPropertyOutput.Matches(s.Motion, use.Binding, use.Initial,
                                true, analysis, bindings, cache, new(),
                                first.Target is Transform && first.Group is "m_LocalPosition" or "m_LocalScale")))
                        { proven = false; break; }
                    }
                    if (!proven) break;
                }
                if (!proven) continue;
                foreach (var use in group)
                {
                    if (!removals.TryGetValue(use.Clip, out var removed)) removals.Add(use.Clip, removed = new());
                    removed.Add(use.Binding);
                }
                targets++;
                messages.Add("Invariant object output: " + AnimationUtility.CalculateTransformPath(first.Transform, avatar.transform) +
                    " / " + first.Group + " (all outputs equal the initial value).");
            }
            foreach (var pair in removals) MotionAnalysis.RemoveCurves(pair.Key, pair.Value);
            messages.Add("Removed " + removals.Sum(p => p.Value.Count) + " invariant object curves in " + targets +
                " property groups; initial visibility and transforms were unchanged.");
        }

        private static Transform Resolve(Transform root, Transform avatar, string path, IReadOnlyDictionary<string, string> paths)
        {
            if (root == avatar && paths != null && paths.TryGetValue(path, out var mapped)) path = mapped;
            var target = root;
            foreach (var part in (path ?? "").Split('/'))
            {
                if (part.Length == 0) continue;
                var children = target.Cast<Transform>().Where(t => t.name == part).ToArray();
                if (children.Length != 1) return null;
                target = children[0];
            }
            return target;
        }

    }
}
