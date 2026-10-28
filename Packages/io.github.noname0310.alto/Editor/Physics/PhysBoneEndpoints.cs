using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
using UnityEngine;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Physics
{
    // Design reference: AAO 1.9.20 ReplaceEndBoneWithEndpointPositionProcessor.
    // This independent variant removes only unreferenced real leaf objects. It
    // requires exact offsets, identity leaf rotation/scale and unchanged forks.
    internal static class PhysBoneEndpoints
    {
        internal static int ReplaceUnused(GameObject avatar, VRCPhysBone[] bones, PhysBoneOptimizationState settings,
            ObjectOptimizationState objects, AvatarAnimationIndex animations, AvatarDependencyGraph references,
            TransformMutationAnalysis mutations)
        {
            if (mutations.Uncertain != null) return 0;
            var chains = bones.ToDictionary(b => b, b => b.GetRootTransform().GetComponentsInChildren<Transform>(true));
            var owners = new Dictionary<Transform, HashSet<VRCPhysBone>>();
            foreach (var pair in chains)
                foreach (var target in pair.Value)
                {
                    if (!owners.TryGetValue(target, out var set)) owners.Add(target, set = new());
                    set.Add(pair.Key);
                }
            int removed = 0;
            foreach (var bone in bones)
            {
                if (PhysBoneOptimizationPass.Protected(settings, bone, objects) || animations.ForObject(bone).Count > 0 ||
                    bone.endpointPosition != Vector3.zero || (bone.ignoreTransforms != null && bone.ignoreTransforms.Any(t => t != null)) ||
                    bone.maxStretch != 0 || bone.maxSquish != 0 || bone.stretchMotion != 0) continue;
                if (references.ReferenceOwners.TryGetValue(bone, out var readers) && readers.Count > 0) continue;
                var target = bone.GetRootTransform(); var chain = chains[bone];
                if (!target.IsChildOf(avatar.transform) || chain.Any(t => owners[t].Count > 1)) continue;
                var leaves = chain.Where(t => t.childCount == 0).ToArray();
                if (leaves.Length == 0 || leaves.Contains(target)) continue;
                var offset = leaves[0].localPosition;
                if (offset == Vector3.zero || !Finite(offset)) continue;
                if (leaves.Any(t => !t.localPosition.Equals(offset) || !t.localRotation.Equals(Quaternion.identity) || !t.localScale.Equals(Vector3.one) ||
                    t.parent.childCount != 1 || !settings.Scope.Contains(t) || objects.Protected(t) ||
                    t.GetComponents<Component>().Length != 1 || mutations.WrittenByOther(t, bone) ||
                    animations.ForObject(t.gameObject).Count > 0 || HasReaders(t) || HasReaders(t.gameObject))) continue;
                bone.endpointPosition = offset;
                foreach (var leaf in leaves) { Object.DestroyImmediate(leaf.gameObject); removed++; }
            }
            return removed;

            bool HasReaders(Object value) => references.ReferenceOwners.TryGetValue(value, out var readers) && readers.Count > 0;
        }

        private static bool Finite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
