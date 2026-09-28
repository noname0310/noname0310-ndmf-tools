using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.Editor
{
    internal enum EyePose { Closed, Joyful, ClosedLeft, JoyfulLeft, ClosedRight, JoyfulRight }

    internal sealed class EyeClipCandidate
    {
        internal AnimationClip Clip;
        internal Transform Root;
    }

    internal sealed class EyeAnimationSearchResult
    {
        internal readonly Dictionary<EyePose, List<EyeClipCandidate>> Candidates =
            Enum.GetValues(typeof(EyePose)).Cast<EyePose>().ToDictionary(p => p, p => new List<EyeClipCandidate>());
    }

    internal static class EyeAnimationDiscovery
    {
        private const int Left = 1, Right = 2, Both = Left | Right;
        private const float Epsilon = 0.0001f;

        internal static EyeAnimationSearchResult Find(BlendShapePoseOverride component)
        {
            var result = new EyeAnimationSearchResult();
            var descriptor = component.FindDescriptor();
            if (descriptor == null || component.TargetMesh == null || component.TargetMesh.sharedMesh == null) return result;
            var sources = new HashSet<(RuntimeAnimatorController, Transform)>();
            foreach (var layer in (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                         .Concat(descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>()))
                if (layer.animatorController != null) sources.Add((layer.animatorController, descriptor.transform));
            foreach (var merge in descriptor.GetComponentsInChildren<ModularAvatarMergeAnimator>(true))
            {
                if (merge.animator == null || PoseOverrideConfiguration.IsEditorOnly(merge.transform)) continue;
                if (merge.GetComponentInParent<VRCAvatarDescriptor>() != descriptor) continue;
                var root = merge.pathMode == MergeAnimatorPathMode.Absolute ? descriptor.transform
                    : merge.relativePathRoot?.Get(descriptor.transform)?.transform ?? merge.transform;
                if (root.IsChildOf(descriptor.transform)) sources.Add((merge.animator, root));
            }
            foreach (var source in sources) new ControllerSearch(source.Item1, source.Item2, component.TargetMesh, result).Run();
            return result;
        }

        internal static bool ClipMatchesTarget(AnimationClip clip, Transform root, SkinnedMeshRenderer target)
        {
            if (clip == null || root == null || target == null || target.sharedMesh == null) return false;
            bool found = false;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) continue;
                var bound = string.IsNullOrEmpty(binding.path) ? root : root.Find(binding.path);
                if (bound == null || bound.GetComponent<SkinnedMeshRenderer>() != target) continue;
                if (target.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length)) < 0) return false;
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0) continue;
                if (!PoseOverrideProcessor.IsFinite(curve.Evaluate(0))) return false;
                found = true;
            }
            return found;
        }

        private static int Sides(EyePose pose)
        {
            if (pose == EyePose.ClosedLeft || pose == EyePose.JoyfulLeft) return Left;
            if (pose == EyePose.ClosedRight || pose == EyePose.JoyfulRight) return Right;
            return Both;
        }

        private static bool IsJoyful(EyePose pose)
            => pose == EyePose.Joyful || pose == EyePose.JoyfulLeft || pose == EyePose.JoyfulRight;

        private static string StandardName(string parameter)
        {
            if (parameter == null) return "";
            if (parameter.StartsWith("v2/", StringComparison.Ordinal)) return parameter.Substring(3);
            // Namespaced standard parameters, including OSCmooth proxy parameters, retain their v2 suffix.
            int start = parameter.LastIndexOf("/v2/", StringComparison.Ordinal);
            return start < 0 ? "" : parameter.Substring(start + 4);
        }

        private static bool IsEyelid(string parameter)
        {
            string name = StandardName(parameter);
            return name == "EyeLid" || name == "EyeLidLeft" || name == "EyeLidRight";
        }

        private static bool ParameterValue(string parameter, EyePose pose, out float value, out int closed, out int joyful)
        {
            value = 0;
            closed = joyful = 0;
            string name = StandardName(parameter);
            int sides = name.EndsWith("Left", StringComparison.Ordinal) ? Left : name.EndsWith("Right", StringComparison.Ordinal) ? Right : Both;
            string stem = sides == Left ? name.Substring(0, name.Length - 4) : sides == Right ? name.Substring(0, name.Length - 5) : name;
            if (stem == "EyeLid")
            {
                if (sides == Both && Sides(pose) != Both) return false;
                bool close = (Sides(pose) & sides) != 0;
                value = close ? 0 : 0.75f;
                if (close) closed = sides;
                return true;
            }
            if (stem == "SmileSad" || stem == "SmileFrown" || stem == "MouthSmile")
            {
                value = IsJoyful(pose) ? 1 : 0;
                if (IsJoyful(pose)) joyful = sides;
                return true;
            }
            return false;
        }

        private sealed class Evaluation
        {
            internal AnimationClip Clip;
            internal int Closed, Joyful;
        }

        private sealed class ControllerSearch
        {
            private readonly RuntimeAnimatorController source;
            private readonly Transform root;
            private readonly SkinnedMeshRenderer target;
            private readonly EyeAnimationSearchResult result;
            private readonly List<Dictionary<AnimationClip, AnimationClip>> overrides = new List<Dictionary<AnimationClip, AnimationClip>>();
            private readonly Dictionary<(Motion, EyePose), Evaluation> evaluations = new Dictionary<(Motion, EyePose), Evaluation>();
            private readonly Dictionary<AnimationClip, bool> matchingClips = new Dictionary<AnimationClip, bool>();
            private readonly HashSet<Motion> visited = new HashSet<Motion>();

            internal ControllerSearch(RuntimeAnimatorController source, Transform root, SkinnedMeshRenderer target, EyeAnimationSearchResult result)
            {
                this.source = source;
                this.root = root;
                this.target = target;
                this.result = result;
            }

            internal void Run()
            {
                var current = source;
                var seen = new HashSet<RuntimeAnimatorController>();
                while (current is AnimatorOverrideController wrapper)
                {
                    if (!seen.Add(current)) return;
                    var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                    wrapper.GetOverrides(pairs);
                    overrides.Insert(0, pairs.Where(p => p.Key != null && p.Value != null).ToDictionary(p => p.Key, p => p.Value));
                    current = wrapper.runtimeAnimatorController;
                }
                if (!(current is AnimatorController controller)) return;
                var layers = controller.layers;
                for (int layer = 0; layer < layers.Length; layer++)
                {
                    int stateLayer = layer;
                    var synced = new HashSet<int>();
                    while (stateLayer >= 0 && stateLayer < layers.Length && layers[stateLayer].syncedLayerIndex >= 0)
                    {
                        if (!synced.Add(stateLayer)) { stateLayer = -1; break; }
                        stateLayer = layers[stateLayer].syncedLayerIndex;
                    }
                    if (stateLayer < 0 || stateLayer >= layers.Length) continue;
                    VisitStates(layers[stateLayer].stateMachine, controller, layer, new HashSet<AnimatorStateMachine>());
                }
            }

            private void VisitStates(AnimatorStateMachine machine, AnimatorController controller, int layer, HashSet<AnimatorStateMachine> seen)
            {
                if (machine == null || !seen.Add(machine)) return;
                foreach (var state in machine.states) VisitMotion(controller.GetStateEffectiveMotion(state.state, layer));
                foreach (var child in machine.stateMachines) VisitStates(child.stateMachine, controller, layer, seen);
            }

            private void VisitMotion(Motion motion)
            {
                if (motion == null || !visited.Add(motion) || !(motion is BlendTree tree)) return;
                bool eyelid = tree.blendType != BlendTreeType.Direct && (IsEyelid(tree.blendParameter) ||
                    (tree.blendType != BlendTreeType.Simple1D && IsEyelid(tree.blendParameterY)));
                if (eyelid)
                {
                    foreach (EyePose pose in Enum.GetValues(typeof(EyePose)))
                    {
                        var evaluation = Evaluate(tree, pose);
                        int sides = Sides(pose);
                        if (evaluation == null || evaluation.Closed != sides || (IsJoyful(pose) && (evaluation.Joyful & sides) != sides)) continue;
                        var candidates = result.Candidates[pose];
                        if (!candidates.Any(c => c.Clip == evaluation.Clip && c.Root == root))
                            candidates.Add(new EyeClipCandidate { Clip = evaluation.Clip, Root = root });
                    }
                }
                foreach (var child in tree.children) VisitMotion(child.motion);
            }

            private Evaluation Evaluate(Motion motion, EyePose pose)
            {
                if (motion == null) return null;
                var key = (motion, pose);
                if (evaluations.TryGetValue(key, out var cached)) return cached;
                // A null sentinel prevents cycles in malformed motion graphs.
                evaluations[key] = null;
                if (motion is AnimationClip original)
                {
                    var clip = original;
                    foreach (var replacements in overrides)
                        if (replacements.TryGetValue(original, out var replacement) || replacements.TryGetValue(clip, out replacement)) clip = replacement;
                    if (!matchingClips.TryGetValue(clip, out bool matches))
                        matchingClips[clip] = matches = ClipMatchesTarget(clip, root, target);
                    return evaluations[key] = matches ? new Evaluation { Clip = clip } : null;
                }
                if (!(motion is BlendTree tree) || tree.blendType == BlendTreeType.Direct) return null;
                var children = tree.children;
                if (children.Length == 0) return null;
                bool oneDimensional = tree.blendType == BlendTreeType.Simple1D;
                bool hasX = ParameterValue(tree.blendParameter, pose, out float x, out int closedX, out int joyfulX);
                bool hasY = ParameterValue(tree.blendParameterY, pose, out float y, out int closedY, out int joyfulY);
                if (oneDimensional) { closedY = joyfulY = 0; }
                if (!hasX || (!oneDimensional && !hasY))
                {
                    // A constant wrapper is safe; an unrelated selector cannot identify a pose uniquely.
                    if (children.Select(c => c.motion).Distinct().Count() != 1) return null;
                    return evaluations[key] = Evaluate(children[0].motion, pose);
                }
                x = Mathf.Clamp(x, children.Min(c => oneDimensional ? c.threshold : c.position.x), children.Max(c => oneDimensional ? c.threshold : c.position.x));
                if (!oneDimensional) y = Mathf.Clamp(y, children.Min(c => c.position.y), children.Max(c => c.position.y));
                var selected = children.Where(c => Mathf.Abs((oneDimensional ? c.threshold : c.position.x) - x) < Epsilon &&
                    (oneDimensional || Mathf.Abs(c.position.y - y) < Epsilon)).ToArray();
                // Only a pure child pose can be assigned as an existing clip; do not approximate blended poses.
                if (selected.Length != 1 || selected[0].mirror || Mathf.Abs(selected[0].cycleOffset) > Epsilon) return null;
                var childResult = Evaluate(selected[0].motion, pose);
                if (childResult == null) return null;
                return evaluations[key] = new Evaluation
                {
                    Clip = childResult.Clip, Closed = childResult.Closed | closedX | closedY,
                    Joyful = childResult.Joyful | joyfulX | joyfulY
                };
            }
        }
    }
}
