using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Noname.AvatarTools.Meshes
{
    internal static class SkinningBoundaryDependencies
    {
        // Interoperability reference: AAO 1.9.20 ConfigureMergeBone and
        // MergeBoneProcessor.DoBoneMap2. Independently implemented; see CREDITS.md.
        // The later static-skinning pass consumes original bone coordinate systems.
        // With singular inputs, inverse-based normalization in an external optimizer
        // can mix collapsed and noncollapsed palettes under the same parent. Retain
        // the scoped skinning inputs until our multiplication-only substitution runs.
        // The optional integration declares dependencies; no external settings change.
        internal static Transform[] Collect(Transform avatar, IEnumerable<SkinnedMeshRenderer> renderers)
        {
            var dependencies = new HashSet<Transform>();
            bool singular = false;
            foreach (var renderer in renderers)
            {
                if (renderer == null || renderer.sharedMesh == null) continue;
                var mesh = renderer.sharedMesh; var bones = renderer.bones; var poses = mesh.bindposes;
                using var weights = mesh.GetAllBoneWeights();
                var used = new HashSet<int>();
                foreach (var weight in weights)
                    if (weight.weight > 0 && weight.boneIndex >= 0 && weight.boneIndex < bones.Length && weight.boneIndex < poses.Length)
                        used.Add(weight.boneIndex);
                foreach (int index in used)
                {
                    var bone = bones[index];
                    if (bone == null || !bone.IsChildOf(avatar)) continue;
                    singular |= !MeshMergePolicy.Invertible(poses[index]);
                    for (var current = bone; current != null && current != avatar; current = current.parent)
                    {
                        dependencies.Add(current);
                        singular |= !MeshMergePolicy.Invertible(Matrix4x4.TRS(current.localPosition, current.localRotation, current.localScale));
                    }
                }
            }
            // Retaining only the collapsed bone is insufficient: palettes from
            // other scoped renderers can be normalized to that same parent's seed.
            return singular ? dependencies.ToArray() : Array.Empty<Transform>();
        }
    }
}
