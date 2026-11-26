using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Rendering;
using VRC.SDK3.Dynamics.Constraint.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Meshes
{
    // This proof leaves transforms, animation, constraints and vertex streams
    // intact. It only supplies a NaN POSITION premise to the all-pass shader proof.
    // NaN is intentionally kept outside the general finite constant-value lattice.
    internal sealed class NonFiniteSkinningProof
    {
        private readonly AvatarAnimationIndex index;
        private readonly AnimatorReachability reachability;
        private readonly TransformMutationAnalysis mutations;
        private readonly FxPropertyOutput outputs;
        private readonly Dictionary<VirtualClip, List<(object Owner, VirtualLayer Layer)>> owners = new();
        private readonly Dictionary<VirtualMotion, HashSet<EditorCurveBinding>> bindings = new();
        private readonly Dictionary<(Transform Bone, bool InitialChanges), bool> bones = new();
        private static readonly string[] Scale = { "m_LocalScale.x", "m_LocalScale.y", "m_LocalScale.z" };

        internal NonFiniteSkinningProof(GameObject avatar, AvatarAnimationIndex index)
        {
            this.index = index; reachability = index.Reachability;
            mutations = TransformMutationAnalysis.Create(avatar, index);
            outputs = new FxPropertyOutput(index.ControllerEntries, reachability);
            foreach (var entry in index.ControllerEntries)
            foreach (var layer in entry.Value.Layers)
            foreach (var clip in layer.AllReachableNodes().OfType<VirtualClip>().Distinct())
            {
                if (!owners.TryGetValue(clip, out var contexts)) owners.Add(clip, contexts = new());
                contexts.Add((entry.Key, layer));
            }
        }

        internal bool[] Vertices(SkinnedMeshRenderer renderer, bool allowInitialChanges)
        {
            var mesh = renderer.sharedMesh; var result = new bool[mesh.vertexCount];
            if (index.UnsafeReason != null || reachability.FallbackReason != null || mutations.Uncertain != null ||
                !Enum.IsDefined(typeof(SkinQuality), renderer.quality) || index.Writes(renderer, "m_Quality") ||
                !ConstantFalse(renderer, MeshMergeAnalysis.UpdateWhenOffscreenProperty, allowInitialChanges) ||
                !mesh.HasVertexAttribute(VertexAttribute.Position) ||
                mesh.GetVertexAttributeDimension(VertexAttribute.Position) != 3 ||
                (mesh.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32 &&
                 mesh.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float16)) return result;
            // Native automatic bounds updates can observe nonfinite skinning.
            // Require their persistent disabled state rather than assuming that
            // index changes preserve an undocumented invalid-AABB fallback.
            var transforms = renderer.bones; var poses = mesh.bindposes;
            if (renderer.rootBone == null || transforms.Length == 0 || poses.Length != transforms.Length) return result;
            var poisoned = new bool[transforms.Length];
            var validPose = new bool[transforms.Length];
            for (int i = 0; i < transforms.Length; i++)
            {
                bool finite = true;
                for (int lane = 0; lane < 16; lane++) finite &= MotionAnalysis.Finite(poses[i][lane]);
                var bone = transforms[i];
                validPose[i] = finite && bone != null;
                // A poisoned coordinate-space root may be cancelled by native
                // skinning setup instead of arriving as a poisoned vertex input.
                poisoned[i] = finite && bone != null && !renderer.transform.IsChildOf(bone) &&
                    !renderer.rootBone.IsChildOf(bone) && NaNScale(bone, allowInitialChanges);
            }
            if (!poisoned.Any(value => value)) return result;
            using var counts = mesh.GetBonesPerVertex();
            using var weights = mesh.GetAllBoneWeights();
            if (counts.Length != mesh.vertexCount) return result;
            int start = 0;
            for (int vertex = 0; vertex < counts.Length; vertex++)
            {
                int count = counts[vertex];
                if (start + count > weights.Length) return new bool[mesh.vertexCount];
                bool valid = count != 0; float previous = float.PositiveInfinity;
                for (int influence = 0; influence < count; influence++)
                {
                    var weight = weights[start + influence];
                    valid &= weight.boneIndex >= 0 && weight.boneIndex < transforms.Length && validPose[weight.boneIndex] &&
                        MotionAnalysis.Finite(weight.weight) && weight.weight >= 0 && weight.weight <= 1 && weight.weight <= previous;
                    previous = weight.weight;
                }
                if (valid)
                {
                    var first = weights[start];
                    // Unity's quality cap retains the leading sorted influence.
                    // A lower influence can disappear at Bone1 quality. Reject
                    // denormals as well: their native multiplication may flush to 0.
                    result[vertex] = first.weight >= 1.17549435E-38f && poisoned[first.boneIndex];
                }
                start += count;
            }
            return start == weights.Length ? result : new bool[mesh.vertexCount];
        }

        private bool NaNScale(Transform bone, bool allowInitialChanges)
        {
            if (bone == null || !bone.IsChildOf(index.Avatar.transform) || bone == index.Avatar.transform) return false;
            var key = (bone, allowInitialChanges);
            if (bones.TryGetValue(key, out bool cached)) return cached;
            // Insert before recursion through component output proofs.
            bones.Add(key, false);
            foreach (var writer in mutations.WritersOf(bone))
                if (!(writer is AnimationClip) && !InactiveScaleWriter(writer, allowInitialChanges)) return false;
            var all = index.ForObject(bone);
            if (all.Any(u => u.Binding.isPPtrCurve || !KnownTransformTrack(u.Binding.propertyName))) return false;
            var initial = bone.localScale;
            HashSet<VirtualClip> footprint = null;
            for (int axis = 0; axis < 3; axis++)
            {
                var uses = all.Where(u => u.Binding.propertyName == Scale[axis]).ToArray();
                if (uses.Length == 0)
                {
                    if (!float.IsNaN(initial[axis])) return false;
                    continue;
                }
                if (!allowInitialChanges && !float.IsNaN(initial[axis])) return false;
                var current = new HashSet<VirtualClip>(uses.Select(u => u.Clip));
                if (footprint != null && !footprint.SetEquals(current)) return false;
                footprint = current;
                if (!NaNOutput(bone, uses)) return false;
            }
            // A partial animated vector can implicitly reset its other channels.
            if (footprint != null && Scale.Any(name => !all.Any(u => u.Binding.propertyName == name))) return false;
            return bones[key] = true;
        }

        private static bool KnownTransformTrack(string name) => Scale.Contains(name) ||
            name.StartsWith("m_LocalPosition.", StringComparison.Ordinal) ||
            name.StartsWith("m_LocalRotation.", StringComparison.Ordinal) ||
            name.StartsWith("localEulerAngles", StringComparison.Ordinal);

        private bool InactiveScaleWriter(Object writer, bool allowInitialChanges)
        {
            // Known constraints have explicit active switches. Never infer that
            // a PhysBone, arbitrary behaviour or an unindexed native writer stops
            // writing scale merely because an animation also writes the target.
            if (!(writer is ScaleConstraint) && !(writer is VRCScaleConstraint)) return false;
            var component = (Component)writer;
            return ConstantFalse(component, "m_Enabled", allowInitialChanges) ||
                writer is VRCScaleConstraint && ConstantFalse(component, "IsActive", allowInitialChanges);
        }

        private bool ConstantFalse(Component component, string property, bool allowInitialChanges)
        {
            using var serialized = new SerializedObject(component);
            if (!ConstantOutputValue.TryRead(serialized.FindProperty(property), out var initial) ||
                initial.Kind == ConstantOutputKind.ObjectReference) return false;
            if (!index.Writes(component, property)) return initial.Number == 0;
            if (!allowInitialChanges && initial.Number != 0) return false;
            return index.ConstantOutputs.TryProve(component, property, initial, out var proof, out _) && proof.Value.Number == 0;
        }

        private bool NaNOutput(Transform target, AvatarAnimationIndex.BindingUse[] uses)
        {
            var binding = uses[0].Binding;
            var contexts = new HashSet<(object Owner, VirtualLayer Layer)>();
            foreach (var use in uses)
            {
                if (use.Immutable || !use.Complete || use.Roots.Length != 1 || use.Roots[0] != index.Avatar.transform ||
                    use.Targets.Length != 1 || !binding.Equals(use.Binding) ||
                    use.Objects().Count() != 1 || use.Objects().Single() != target ||
                    !owners.TryGetValue(use.Clip, out var found) || found.Any(c => !FxPropertyOutput.IsFx(c.Owner))) return false;
                contexts.UnionWith(found);
            }
            if (!outputs.TryGetFinalOutputStates(contexts, out var states, out _, includeInitialOutputs: true))
                return false;
            foreach (var state in states)
                if (!NaNMotion(state.Motion, binding, state.WriteDefaultValues, new(), new())) return false;
            return true;
        }

        private bool NaNMotion(VirtualMotion motion, EditorCurveBinding binding, bool writeDefaults,
            Dictionary<VirtualMotion, bool> cache, HashSet<VirtualMotion> visiting)
        {
            if (motion == null) return false;
            if (cache.TryGetValue(motion, out bool previous)) return previous;
            if (!visiting.Add(motion)) return false;
            bool result = Evaluate(); visiting.Remove(motion); cache.Add(motion, result); return result;

            bool Evaluate()
            {
                if (motion is VirtualClip clip)
                {
                    var source = MotionAnalysis.Source(clip);
                    if (clip.IsMarkerClip || clip.Legacy || clip.AdditiveReferencePoseClip != null || source == null ||
                        source.humanMotion || source.events.Length != 0 || !MotionAnalysis.Finite(clip.FrameRate) || clip.FrameRate <= 0)
                        return false;
                    var curve = clip.GetFloatCurve(binding);
                    // One key has no interpolation, tangent or wrap-time ambiguity.
                    return !binding.isPPtrCurve && !binding.isDiscreteCurve && curve != null && curve.length == 1 &&
                        MotionAnalysis.Finite(curve.keys[0].time) && float.IsNaN(curve.keys[0].value);
                }
                if (!(motion is VirtualBlendTree tree) || tree.Children.Count == 0 ||
                    tree.Children.Any(c => c.Motion == null || c.Mirror || !MotionAnalysis.Finite(c.TimeScale) ||
                        !MotionAnalysis.Finite(c.CycleOffset))) return false;
                if (tree.BlendType == BlendTreeType.Simple1D)
                {
                    var selected = MotionAnalysis.Constant1DChildren(tree, reachability);
                    return selected?.Length == 1 && NaNMotion(selected[0].Motion, binding, writeDefaults, cache, visiting);
                }
                if (tree.BlendType != BlendTreeType.Direct || tree.NormalizedBlendValues ||
                    !writeDefaults && ConstantMotionOutput.DirectReadsRetainedValue(tree, binding, reachability, bindings)) return false;
                VirtualMotion selectedMotion = null;
                foreach (var child in tree.Children)
                {
                    if (!bindings.TryGetValue(child.Motion, out var writes))
                        bindings.Add(child.Motion, writes = MotionAnalysis.Bindings(child.Motion));
                    if (!writes.Contains(binding)) continue;
                    if (!reachability.Values(child.DirectBlendParameter).TryGetConstant(out float weight)) return false;
                    if (weight == 0) continue;
                    if (weight != 1 || selectedMotion != null) return false;
                    selectedMotion = child.Motion;
                }
                return selectedMotion != null && NaNMotion(selectedMotion, binding, writeDefaults, cache, visiting);
            }
        }
    }
}
