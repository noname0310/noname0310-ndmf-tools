using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Editor
{
    internal sealed class ResolvedPose
    {
        internal string ShapeName;
        internal readonly Dictionary<string, float> Weights = new Dictionary<string, float>();
    }

    internal sealed class PoseOverridePlan
    {
        internal SkinnedMeshRenderer Renderer;
        internal readonly List<ResolvedPose> Poses = new List<ResolvedPose>();
        internal readonly List<string> Warnings = new List<string>();
    }

    internal static class PoseOverrideConfiguration
    {
        internal const string MultiFrameWarning = "This animation spans more than one frame. Only the first frame (time 0) is used.";

        internal static bool HasMultipleFrames(AnimationClip clip)
            => clip != null && clip.length * clip.frameRate > 1.0001f;

        internal static bool IsEditorOnly(Transform transform)
        {
            for (var current = transform; current != null; current = current.parent)
                if (current.CompareTag("EditorOnly")) return true;
            return false;
        }

        internal static bool TryResolve(BlendShapePoseOverride component, out PoseOverridePlan plan, out string error)
        {
            plan = null;
            error = null;
            if (component.Overrides == null || component.Overrides.Count == 0)
            {
                plan = new PoseOverridePlan { Renderer = component.TargetMesh };
                return true;
            }
            var descriptor = component.FindDescriptor();
            if (descriptor == null)
            {
                error = "Place BlendShape Pose Override on or below a VRC Avatar Descriptor.";
                return false;
            }
            var renderer = component.TargetMesh;
            if (renderer == null || renderer.sharedMesh == null)
            {
                error = "Assign a Target Mesh with a mesh containing blendshapes.";
                return false;
            }
            if (!renderer.transform.IsChildOf(descriptor.transform) || IsEditorOnly(renderer.transform))
            {
                error = "Target Mesh must belong to this avatar and must not be EditorOnly.";
                return false;
            }
            if (component.PathMode != PoseAnimationPathMode.Absolute && component.PathMode != PoseAnimationPathMode.Relative)
            {
                error = "Select a valid animation Path Mode.";
                return false;
            }
            var animationRoot = component.PathMode == PoseAnimationPathMode.Absolute
                ? descriptor.transform : component.RelativePathRoot != null ? component.RelativePathRoot : component.transform;
            if (!animationRoot.IsChildOf(descriptor.transform))
            {
                error = "Relative Path Root must belong to the same avatar.";
                return false;
            }
            if (descriptor.GetComponentsInChildren<BlendShapePoseOverride>(true).Count(c =>
                c.enabled && c.Overrides != null && c.Overrides.Count > 0 && !IsEditorOnly(c.transform) &&
                c.TargetMesh == renderer && c.FindDescriptor() == descriptor) > 1)
            {
                error = "Only one enabled BlendShape Pose Override may modify a renderer. Combine its entries into one list.";
                return false;
            }
            var resolved = new PoseOverridePlan { Renderer = renderer };
            var targets = new HashSet<string>(StringComparer.Ordinal);
            for (int row = 0; row < component.Overrides.Count; row++)
            {
                var entry = component.Overrides[row];
                string prefix = $"Entry {row + 1}: ";
                if (entry == null || string.IsNullOrEmpty(entry.BlendShapeName) || renderer.sharedMesh.GetBlendShapeIndex(entry.BlendShapeName) < 0)
                {
                    error = prefix + "Select a blendshape that exists on Target Mesh.";
                    return false;
                }
                if (!targets.Add(entry.BlendShapeName))
                {
                    error = prefix + $"'{entry.BlendShapeName}' is already overridden by another entry.";
                    return false;
                }
                var pose = new ResolvedPose { ShapeName = entry.BlendShapeName };
                if (!TryReadAnimations(entry.GetAnimations(), animationRoot, renderer, out var weights, out int ignored, out var clipError))
                {
                    error = prefix + clipError;
                    return false;
                }
                foreach (var weight in weights) pose.Weights.Add(weight.Key, weight.Value);
                if (ignored > 0)
                    resolved.Warnings.Add(prefix + $"Ignoring {ignored} curve(s) that do not animate blendshapes on Target Mesh. Only blendshape deformation is supported.");
                if (pose.Weights.All(p => p.Value == renderer.GetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex(p.Key))))
                    resolved.Warnings.Add(prefix + "The keyed values match the current basis. This produces no movement.");
                resolved.Poses.Add(pose);
            }
            if (targets.Any(name => renderer.GetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex(name)) != 0))
                resolved.Warnings.Add("Current contributions from overridden shapes are preserved in the neutral mesh. Their built default weights become 0.");
            plan = resolved;
            return true;
        }

        internal static bool TryReadAnimations(IEnumerable<AnimationClip> clips, Transform animationRoot,
            SkinnedMeshRenderer renderer, out Dictionary<string, float> weights, out int ignored, out string error)
        {
            weights = new Dictionary<string, float>(StringComparer.Ordinal);
            ignored = 0;
            error = null;
            if (animationRoot == null || renderer == null || renderer.sharedMesh == null)
            {
                error = "Assign a valid animation root and Target Mesh.";
                return false;
            }
            var owners = new Dictionary<string, int>(StringComparer.Ordinal);
            int index = 0;
            foreach (var clip in clips)
            {
                index++;
                string prefix = $"Animation {index}" + (clip == null ? ": " : $" ('{clip.name}'): ");
                if (clip == null)
                {
                    error = prefix + "Assign an animation containing the desired pose, or remove this slot.";
                    return false;
                }
                int matched = 0;
                ignored += AnimationUtility.GetObjectReferenceCurveBindings(clip).Length;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    // Merge keyed values first, then generate one displacement from the shared original basis.
                    if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                    {
                        ignored++;
                        continue;
                    }
                    var target = string.IsNullOrEmpty(binding.path) ? animationRoot : animationRoot.Find(binding.path);
                    if (target == null || target.GetComponent<SkinnedMeshRenderer>() != renderer)
                    {
                        ignored++;
                        continue;
                    }
                    string name = binding.propertyName.Substring("blendShape.".Length);
                    if (renderer.sharedMesh.GetBlendShapeIndex(name) < 0)
                    {
                        error = prefix + $"The animation references a missing blendshape: '{name}'.";
                        return false;
                    }
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (curve == null || curve.length == 0) continue;
                    float value = curve.Evaluate(0);
                    if (!PoseOverrideProcessor.IsFinite(value))
                    {
                        error = prefix + $"The first-frame value for '{name}' must be finite.";
                        return false;
                    }
                    if (weights.TryGetValue(name, out float previous) && previous != value)
                    {
                        error = prefix + $"Conflicting values for '{name}': animation {owners[name]} sets {previous}, but this animation sets {value}.";
                        return false;
                    }
                    weights[name] = value;
                    owners[name] = index;
                    matched++;
                }
                if (matched == 0)
                {
                    error = prefix + "No first-frame blendshape curves resolve to Target Mesh. Check Path Mode and Relative Path Root.";
                    return false;
                }
            }
            if (index != 0) return true;
            error = "Assign at least one animation containing the desired pose.";
            return false;
        }
    }
}
