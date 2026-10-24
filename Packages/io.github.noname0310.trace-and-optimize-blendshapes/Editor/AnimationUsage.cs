using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization
{
    internal sealed class CurveUse
    {
        internal VirtualClip Clip;
        internal EditorCurveBinding Binding;
        internal AnimationCurve Curve;
        internal int ClipId;
        internal bool ReadOnly, Additive, Unreachable;
        internal readonly HashSet<int> DefaultContexts = new();
    }

    internal sealed class AnimationUsage
    {
        private readonly Dictionary<SkinnedMeshRenderer, Dictionary<string, List<CurveUse>>> usages = new();
        internal readonly HashSet<SkinnedMeshRenderer> MeshSwaps = new();
        private readonly Dictionary<VirtualClip, int> clipIds = new();
        private readonly Dictionary<(VirtualClip, EditorCurveBinding), HashSet<SkinnedMeshRenderer>> destinations = new();
        private int nextDefaultContext;
        internal AnimatorReachability Reachability;
        internal int ClipCount => clipIds.Count;

        internal IReadOnlyList<CurveUse> Get(SkinnedMeshRenderer renderer, string name)
            => usages.TryGetValue(renderer, out var shapes) && shapes.TryGetValue(name, out var uses)
                ? uses : Array.Empty<CurveUse>();

        internal static AnimationUsage Collect(GameObject avatar, VirtualControllerContext controllers,
            IReadOnlyDictionary<string, string> pathMap = null, bool analyzeReachability = false)
        {
            var result = new AnimationUsage();
            // NDMF can retain controller entries after another build pass removes their component.
            var entries = controllers.Controllers.Where(e => !(e.Key is UnityEngine.Object owner) || owner != null).ToArray();
            if (analyzeReachability)
                result.Reachability = AnimatorReachability.Analyze(avatar, entries.Select(e => e.Value));
            foreach (var entry in entries)
            {
                var root = entry.Key is Animator animator ? animator.transform : avatar.transform;
                bool additivePlayable = entry.Key is VRCAvatarDescriptor.AnimLayerType type &&
                                        type == VRCAvatarDescriptor.AnimLayerType.Additive;
                var additiveClips = new HashSet<VirtualClip>();
                foreach (var layer in entry.Value.Layers)
                    if (additivePlayable || layer.BlendingMode == AnimatorLayerBlendingMode.Additive)
                        additiveClips.UnionWith(layer.AllReachableNodes().OfType<VirtualClip>());
                result.AddController(avatar.transform, root, entry.Value, additiveClips, pathMap, result.Reachability?.Clips(entry.Value));
            }
            // A shared virtual clip can be installed at more than one animation root. Rewriting a binding
            // in that case would affect another renderer, possibly outside the requested mesh scope.
            foreach (var item in result.destinations.Where(x => x.Value.Count > 1))
                foreach (var renderer in item.Value)
                    foreach (var use in result.Get(renderer, item.Key.Item2.propertyName.Substring(11)))
                        if (use.Clip == item.Key.Item1 && use.Binding.Equals(item.Key.Item2)) use.ReadOnly = true;
            return result;
        }

        internal void AddController(Transform avatarRoot, Transform animationRoot, VirtualAnimatorController controller,
            HashSet<VirtualClip> additiveClips = null, IReadOnlyDictionary<string, string> pathMap = null,
            HashSet<VirtualClip> reachableClips = null)
        {
            var defaultContexts = new Dictionary<VirtualClip, HashSet<int>>();
            foreach (var layer in reachableClips == null ? Enumerable.Empty<VirtualLayer>() : controller.Layers)
            {
                int context = nextDefaultContext++;
                foreach (var clip in layer.AllReachableNodes().OfType<VirtualClip>().Distinct())
                {
                    if (!defaultContexts.TryGetValue(clip, out var contexts)) defaultContexts.Add(clip, contexts = new());
                    contexts.Add(context);
                }
            }
            foreach (var clip in controller.AllReachableNodes().OfType<VirtualClip>().Distinct())
            {
                if (!clipIds.TryGetValue(clip, out int id)) clipIds.Add(clip, id = clipIds.Count);
                foreach (var binding in clip.GetFloatCurveBindings())
                {
                    if (binding.type != typeof(SkinnedMeshRenderer) ||
                        !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) continue;
                    var targets = Resolve(avatarRoot, animationRoot, binding.path, pathMap);
                    foreach (var target in targets)
                    {
                        var renderer = target.GetComponent<SkinnedMeshRenderer>();
                        if (renderer == null) continue;
                        if (!usages.TryGetValue(renderer, out var shapes)) usages.Add(renderer, shapes = new());
                        var name = binding.propertyName.Substring(11);
                        if (!shapes.TryGetValue(name, out var uses)) shapes.Add(name, uses = new());
                        bool unreachable = reachableClips != null && !reachableClips.Contains(clip);
                        var use = uses.FirstOrDefault(u => u.Clip == clip && u.Binding.Equals(binding));
                        if (use == null)
                            uses.Add(use = new CurveUse { Clip = clip, Binding = binding, Curve = clip.GetFloatCurve(binding),
                                ClipId = id, ReadOnly = clip.IsMarkerClip || targets.Count != 1,
                                Unreachable = unreachable });
                        use.Unreachable &= unreachable;
                        use.Additive |= additiveClips != null && additiveClips.Contains(clip);
                        if (defaultContexts.TryGetValue(clip, out var layerContexts)) use.DefaultContexts.UnionWith(layerContexts);
                        var key = (clip, binding);
                        if (!destinations.TryGetValue(key, out var renderers)) destinations.Add(key, renderers = new());
                        renderers.Add(renderer);
                    }
                }
                foreach (var binding in clip.GetObjectCurveBindings())
                    if (binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName == "m_Mesh")
                        foreach (var target in Resolve(avatarRoot, animationRoot, binding.path, pathMap))
                        {
                            var renderer = target.GetComponent<SkinnedMeshRenderer>();
                            if (renderer != null) MeshSwaps.Add(renderer);
                        }
            }
        }

        private static List<Transform> Resolve(Transform avatar, Transform root, string path,
            IReadOnlyDictionary<string, string> pathMap)
        {
            if (root == avatar && pathMap != null && pathMap.TryGetValue(path, out var mapped))
            {
                if (mapped == null) return new();
                path = mapped;
            }
            var current = new List<Transform> { root };
            if (string.IsNullOrEmpty(path)) return current;
            foreach (var part in path.Split('/'))
            {
                var next = new List<Transform>();
                foreach (var parent in current)
                    foreach (Transform child in parent)
                        if (child.name == part) next.Add(child);
                current = next;
                if (current.Count == 0) break;
            }
            return current;
        }

        internal static bool IsConstantZero(AnimationCurve curve)
        {
            if (curve == null || curve.length == 0) return false;
            var keys = curve.keys;
            if (keys.Any(k => k.value != 0 || !Finite(k.time) || float.IsNaN(k.inTangent) ||
                float.IsNaN(k.outTangent) || !Finite(k.inWeight) || !Finite(k.outWeight))) return false;
            for (int i = 0; i + 1 < keys.Length; i++)
            {
                if (keys[i + 1].time <= keys[i].time) return false;
                // A stepped zero segment is constant. Equal endpoint values alone are insufficient:
                // non-zero tangents can create a non-zero excursion between the keys.
                if (float.IsInfinity(keys[i].outTangent) || float.IsInfinity(keys[i + 1].inTangent)) continue;
                if (keys[i].outTangent != 0 || keys[i + 1].inTangent != 0) return false;
            }
            return true;
        }

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
