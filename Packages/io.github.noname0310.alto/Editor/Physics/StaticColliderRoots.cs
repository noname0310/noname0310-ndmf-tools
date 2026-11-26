using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
using UnityEngine;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.Physics
{
    internal static class StaticColliderRoots
    {
        // Collider activation belongs to the component/owner hierarchy. Only the
        // geometric reference frame changes; no collider is moved or combined.
        // Shape semantics were checked against the installed SDK and AAO's
        // OptimizePhysBone collider geometry comparison. Independent implementation.
        internal static int Apply(GameObject avatar, PhysBoneOptimizationState settings, ObjectOptimizationState objects,
            AvatarAnimationIndex animations, AvatarDependencyGraph references, StaticTransformProof proof, ICollection<string> messages)
        {
            if (!settings.RebaseColliderRoots || settings.Scope == null || proof.Mutations.Uncertain != null) return 0;
            var objectKeys = animations.Bindings.SelectMany(u => u.ObjectKeys ?? Array.Empty<UnityEditor.ObjectReferenceKeyframe>())
                .Select(k => k.value).ToHashSet();
            int changed = 0;
            foreach (var collider in avatar.GetComponentsInChildren<VRCPhysBoneCollider>(true))
            {
                if (collider.GetType() != typeof(VRCPhysBoneCollider) || PhysBoneOptimizationPass.Protected(settings, collider, objects) ||
                    objectKeys.Contains(collider) || !StaticTransformProof.Finite(collider.position) ||
                    !StaticTransformProof.Finite(collider.rotation) ||
                    !StaticTransformProof.Finite(collider.radius) || !StaticTransformProof.Finite(collider.height)) continue;
                // On/off curves remain on the same component. Geometric curves and
                // reference swaps require a different curve-rewriting proof.
                if (animations.ForObject(collider).Any(u => !u.Complete || u.Binding.isPPtrCurve || u.Binding.propertyName != "m_Enabled")) continue;
                if (references.ReferenceOwners.TryGetValue(collider, out var readers) && readers.Any(r => !(r is VRCPhysBone))) continue;
                var original = collider.GetRootTransform(); var target = original;
                var contributors = new HashSet<Transform> { original, collider.transform };
                bool allowRounding = true;
                var position = collider.position; var rotation = collider.rotation;
                float radius = collider.radius, height = collider.height;
                while (proof.CanSubstitute(target, settings.Scope) && SpatialFrameProof.UniformShapeScale(target, avatar.transform, proof.Mutations, out float scale))
                {
                    contributors.Add(target); contributors.Add(target.parent);
                    allowRounding &= proof.AllowsRounding(NumericalOptimization.ComponentHoisting, collider.transform, target, target.parent);
                    if (!allowRounding)
                    {
                        if (!StaticTransformProof.ExactLocalIdentity(target)) break;
                        target = target.parent;
                        continue;
                    }
                    var nextPosition = StaticTransformProof.Local(target).MultiplyPoint3x4(position);
                    var nextRotation = target.localRotation * rotation;
                    if (!StaticTransformProof.Finite(nextPosition) || !StaticTransformProof.Finite(nextRotation) || !StaticTransformProof.Finite(radius * scale) ||
                        !StaticTransformProof.Finite(height * scale)) break;
                    position = nextPosition; rotation = nextRotation; radius *= scale; height *= scale;
                    target = target.parent;
                }
                if (target == original) continue;
                proof.NumericalPolicy?.InheritNumericalConstraints(collider.transform, contributors);
                collider.rootTransform = target; collider.position = position; collider.rotation = rotation;
                collider.radius = radius; collider.height = height;
                changed++;
            }
            if (changed != 0) messages.Add("Rebased " + changed + " static collider roots; owner activation, collider identity and collision lists preserved.");
            return changed;
        }

    }
}
