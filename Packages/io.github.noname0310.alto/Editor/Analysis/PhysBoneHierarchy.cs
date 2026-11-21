using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.Analysis
{
    // A simulation reads and writes its included chain, not every descendant.
    // The SDK always includes the root; exclusion is checked when visiting children.
    internal static class PhysBoneHierarchy
    {
        internal static bool FixedExclusions(VRCPhysBone bone, AvatarAnimationIndex animations) =>
            bone.GetType() == typeof(VRCPhysBone) && animations != null &&
            !animations.ForObject(bone).Any(u => !u.Complete || u.Binding.propertyName == "rootTransform" ||
                u.Binding.propertyName.StartsWith("ignoreTransforms", StringComparison.Ordinal));

        internal static IEnumerable<Transform> Included(VRCPhysBone bone, AvatarAnimationIndex animations)
        {
            var root = bone.GetRootTransform();
            if (!FixedExclusions(bone, animations)) return root.GetComponentsInChildren<Transform>(true);
            var ignored = new HashSet<Transform>(bone.ignoreTransforms ?? new List<Transform>());
            var result = new List<Transform>();
            var pending = new Stack<Transform>(); pending.Push(root);
            while (pending.Count != 0)
            {
                var current = pending.Pop(); result.Add(current);
                foreach (Transform child in current) if (!ignored.Contains(child)) pending.Push(child);
            }
            // Ignore Other PhysBones remains conservative: deleting another component
            // can change that implicit boundary. Explicit ignore entries are stable.
            return result;
        }
    }
}
