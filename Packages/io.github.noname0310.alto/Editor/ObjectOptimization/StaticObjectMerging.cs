using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using UnityEngine;

namespace Noname.AvatarTools.ObjectOptimization
{
    internal static class StaticObjectMerging
    {
        // A component-free frame can still own activation, a simulation boundary,
        // an externally observed transform or inherited mask weights. Prove those
        // independently; being an animation path segment is not itself a blocker.
        internal static int Apply(GameObject avatar, ObjectOptimizationState settings, AvatarAnimationIndex animations,
            AvatarDependencyGraph references, StaticTransformProof proof, ICollection<string> messages)
        {
            if (!settings.MergeStaticObjects || settings.Scope == null || references.UnsafeReason != null) return 0;
            var relocation = new HierarchyRelocation(animations);
            var keys = animations.Bindings.SelectMany(u => u.ObjectKeys ?? Array.Empty<UnityEditor.ObjectReferenceKeyframe>())
                .Select(k => k.value).ToHashSet();
            // Batch disjoint subtrees against one snapshot. Ancestors of a changed
            // subtree are reconsidered by the worklist, avoiding stale path facts.
            var changedSubtrees = new List<Transform>();
            var nativePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            int changed = 0;
            foreach (var frame in avatar.GetComponentsInChildren<Transform>(true).Reverse())
            {
                if (!proof.CanSubstitute(frame, settings.Scope) || frame.childCount == 0 ||
                    changedSubtrees.Any(t => t == frame || t.IsChildOf(frame)) ||
                    frame.GetComponents<Component>().Any(c => c == null || !(c is Transform)) ||
                    references.ReferenceOwners.ContainsKey(frame) || references.ReferenceOwners.ContainsKey(frame.gameObject) ||
                    keys.Contains(frame) || keys.Contains(frame.gameObject)) continue;
                var children = frame.Cast<Transform>().ToArray();
                if (!relocation.RedundantGate(frame, frame.parent)) continue;
                var poses = new List<(Transform Child, Vector3 Position, Quaternion Rotation, Vector3 Scale, Action Edit, bool Identity)>();
                foreach (var child in children)
                {
                    if (!settings.Scope.Contains(child) || settings.Protected(child)) continue;
                    // Keep ambiguous original paths; destination collisions are
                    // resolved by the checked relocation plan before any edits.
                    if (children.Count(c => c.name == child.name) != 1) continue;
                    bool allowRounding = proof.AllowsRounding(NumericalOptimization.TransformMerging, frame, frame.parent) &&
                        proof.AllowsSubtreeRounding(child, NumericalOptimization.TransformMerging);
                    bool identity = allowRounding ? StaticTransformProof.Near(StaticTransformProof.Local(frame), Matrix4x4.identity,
                        !proof.HasStatefulReaders(frame)) : StaticTransformProof.ExactLocalIdentity(frame);
                    // Preserve local-space writers/readers when a nonidentity frame
                    // would change the coordinate values they observe. Identity
                    // containers can surround arbitrarily animated descendants.
                    Action edit = null;
                    bool physBoneFrame = !identity && PhysBoneRootRelocation.CanRebase(frame, child, animations, proof.Mutations, allowRounding);
                    if (!identity && (HasLocalReader(child, references, frame, physBoneFrame, allowRounding) ||
                        proof.Mutations.Mutable.Contains(child) && !physBoneFrame &&
                        !StaticChildRelocation.TryPlan(frame, child, animations, proof.Mutations, out edit, allowRounding))) continue;
                    if (!identity && child.GetComponents<Component>().Any(c => c is ParticleSystem || c is Rigidbody ||
                        c is ArticulationBody || c is Animator || c is UnityEngine.Animations.IConstraint)) continue;
                    if (!StaticTransformProof.TryCompose(frame, child, out var position, out var rotation, out var scale, allowRounding)) continue;
                    poses.Add((child, position, rotation, scale, edit, identity));
                }
                if (poses.Count == 0) continue;
                bool removeFrame = poses.Count == children.Length;
                // SDK sensors can overwrite the same output in hierarchy order.
                // A surviving frame must retain one contiguous interval of the
                // original traversal. Only its movable prefix/suffix can escape
                // without reordering a branch across a retained sibling.
                var movable = poses.Select(p => p.Child).ToHashSet();
                var prefix = children.TakeWhile(movable.Contains).ToHashSet();
                if (!removeFrame)
                {
                    int firstRetained = Array.FindIndex(children, c => !movable.Contains(c));
                    int lastRetained = Array.FindLastIndex(children, c => !movable.Contains(c));
                    var interior = children.Skip(firstRetained + 1).Take(lastRetained - firstRetained - 1).ToHashSet();
                    poses.RemoveAll(p => interior.Contains(p.Child));
                    if (poses.Count == 0) continue;
                }
                if (!relocation.CanFlatten(frame, poses.Select(p => p.Child).ToArray(), removeFrame, out var plan)) continue;
                var originals = poses.Select(p => p.Child).Select(c => (Child: c, Name: c.name, Position: c.localPosition, Rotation: c.localRotation,
                    Scale: c.localScale, Index: c.GetSiblingIndex())).ToArray();
                var parent = frame.parent;
                int suffixCount = 0;
                foreach (var pose in poses)
                {
                    foreach (var output in pose.Child.GetComponentsInChildren<Transform>(true))
                        proof.NumericalPolicy?.InheritNumericalConstraints(output, new[] { frame });
                    if (plan.Names.TryGetValue(pose.Child, out var relocatedName)) pose.Child.name = relocatedName;
                    pose.Child.SetParent(parent, false);
                    pose.Child.SetSiblingIndex(frame.GetSiblingIndex() +
                        (removeFrame || prefix.Contains(pose.Child) ? 0 : 1 + suffixCount++));
                    // Do not perturb an animated child's defaults for numerical noise.
                    if (pose.Identity) continue;
                    pose.Child.localPosition = pose.Position; pose.Child.localRotation = pose.Rotation; pose.Child.localScale = pose.Scale;
                }
                string name = frame.name;
                if (removeFrame && !settings.TryDestroy(frame.gameObject))
                {
                    foreach (var pose in originals)
                    {
                        pose.Child.SetParent(frame, false); pose.Child.name = pose.Name; pose.Child.localPosition = pose.Position;
                        pose.Child.localRotation = pose.Rotation; pose.Child.localScale = pose.Scale; pose.Child.SetSiblingIndex(pose.Index);
                    }
                    continue;
                }
                foreach (var pair in plan.Paths) nativePaths[pair.Key] = pair.Value;
                foreach (var pose in poses) pose.Edit?.Invoke();
                changedSubtrees.Add(parent);
                messages.Add(removeFrame ? "Flattened static grouping object '" + name + "'; " + poses.Count + " children and their animation paths relocated." :
                    "Bypassed static grouping object '" + name + "' for " + poses.Count + " independent branches; retained the frame for its remaining children.");
                changed++;
            }
            if (changed != 0 && animations.Paths == null) HierarchyRelocation.CommitNativePaths(animations, nativePaths);
            return changed;
        }

        private static bool HasLocalReader(Transform child, AvatarDependencyGraph references, Transform frame, bool physBoneFrame, bool allowRounding) =>
            references.ReferenceOwners.TryGetValue(child, out var readers) && readers.Any(r => !(r is SkinnedMeshRenderer) &&
                !(physBoneFrame && r != null && r.GetType() == typeof(VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone) &&
                  ((VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone)r).GetRootTransform() == child) &&
                !(r is VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider) &&
                !(r is VRC.SDK3.Dynamics.Contact.Components.VRCContactSender) && !(r is VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver) &&
                !(SpatialFrameProof.TranslationOnly(frame, allowRounding) && r != null &&
                  (r.GetType() == typeof(VRC.SDK3.Dynamics.Constraint.Components.VRCRotationConstraint) ||
                   r.GetType() == typeof(VRC.SDK3.Dynamics.Constraint.Components.VRCScaleConstraint))));
    }
}
