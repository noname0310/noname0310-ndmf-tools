using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Physics
{
    // Design reference: AAO 1.9.20 AutoMergeCompatiblePhysBone and
    // MergePhysBoneProcessor. Independently implemented conservative subset:
    // constant settings, no grabs/outputs, identity sibling roots and no writers.
    internal static class PhysBoneSimulationMerge
    {
        private static readonly HashSet<string> Metadata = new(StringComparer.Ordinal)
        { "m_GameObject", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "rootTransform" };

        internal static int Apply(GameObject avatar, VRCPhysBone[] bones, PhysBoneOptimizationState settings,
            ObjectOptimizationState objects, AvatarAnimationIndex animations, AvatarDependencyGraph references,
            TransformMutationAnalysis mutations, ObjectPathRemapper paths)
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
            bool AlwaysActive(Transform target)
            {
                for (var current = target; current != null && current != avatar.transform; current = current.parent)
                    if (!current.gameObject.activeSelf || animations.ForObject(current.gameObject).Count > 0) return false;
                return true;
            }
            bool Eligible(VRCPhysBone bone)
            {
                var root = bone.GetRootTransform(); var chain = chains[bone];
                if (PhysBoneOptimizationPass.Protected(settings, bone, objects) || !bone.enabled || !AlwaysActive(bone.transform) ||
                    root != bone.transform || root.parent == null || !settings.Scope.Contains(root.parent) ||
                    !root.localPosition.Equals(Vector3.zero) || !root.localRotation.Equals(Quaternion.identity) || !root.localScale.Equals(Vector3.one) ||
                    bone.multiChildType != VRCPhysBoneBase.MultiChildType.Ignore || bone.allowGrabbing != VRCPhysBoneBase.AdvancedBool.False ||
                    bone.allowPosing != VRCPhysBoneBase.AdvancedBool.False || !string.IsNullOrEmpty(bone.parameter) ||
                    bone.maxStretch != 0 || bone.maxSquish != 0 || bone.stretchMotion != 0 ||
                    (bone.ignoreTransforms != null && bone.ignoreTransforms.Any(t => t != null)) ||
                    chain.Length >= 127 || chain.Any(t => owners[t].Count > 1 || objects.Protected(t) || mutations.WrittenByOther(t, bone)) ||
                    animations.ForObject(bone).Count > 0) return false;
                if (references.ReferenceOwners.TryGetValue(bone, out var readers) && readers.Count > 0) return false;
                if (bone.GetComponents<Component>().Any(c => c != null && c != bone && Build.BuildFilterPass.Requires(c, bone))) return false;
                // Keep every animation interface in the moved subtrees intact,
                // including immutable and shared relative clips.
                if (animations.Bindings.Any(u => u.Targets.Any(t => t.IsChildOf(root)))) return false;
                using var serialized = new SerializedObject(bone);
                var property = serialized.GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.AnimationCurve && property.animationCurveValue?.length > 0) return false;
                return true;
            }
            bool Same(VRCPhysBone a, VRCPhysBone b) => a.transform.parent == b.transform.parent &&
                a.gameObject.layer == b.gameObject.layer && a.tag == b.tag && SerializedValueEquality.Matches(a, b, Metadata);
            var groups = new List<List<VRCPhysBone>>();
            foreach (var bone in bones.Where(Eligible))
            {
                var group = groups.FirstOrDefault(g => Same(g[0], bone) && 1 + chains[bone].Length + g.Sum(b => chains[b].Length) <= 128);
                if (group == null) groups.Add(group = new());
                group.Add(bone);
            }
            int removed = 0;
            foreach (var group in groups.Where(g => g.Count > 1))
            {
                var first = group[0]; var parent = first.transform.parent; string name = "Merged PhysBones";
                for (int suffix = 1; parent.Find(name) != null; suffix++) name = "Merged PhysBones " + suffix;
                var holder = new GameObject(name) { layer = first.gameObject.layer, tag = first.tag };
                holder.transform.SetParent(parent, false);
                var merged = holder.AddComponent<VRCPhysBone>(); EditorUtility.CopySerialized(first, merged);
                merged.rootTransform = holder.transform;
                foreach (var bone in group) bone.transform.SetParent(holder.transform, false);
                foreach (var bone in group) Object.DestroyImmediate(bone);
                removed += group.Count - 1;
            }
            if (removed > 0) paths?.ClearCache();
            return removed;
        }
    }
}
