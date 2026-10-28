using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace Noname.AvatarTools.Build
{
    [Serializable]
    public sealed class PhysBoneResourceCounts
    {
        public int Components, Transforms, Colliders, CollisionChecks;

        // Use the installed SDK's rules for endpoints, nested chains and collision checks.
        // Call on a build copy, never on the source avatar during Inspector repaint.
        internal static PhysBoneResourceCounts Capture(GameObject avatar, bool mobile)
        {
            var stats = new AvatarPerformanceStats(mobile);
            AvatarPerformance.CalculatePerformanceStats(avatar.name, avatar, stats, mobile);
            var physics = stats.physBone ?? default;
            return new PhysBoneResourceCounts { Components = physics.componentCount, Transforms = physics.transformCount,
                Colliders = physics.colliderCount, CollisionChecks = physics.collisionCheckCount };
        }

        internal IEnumerable<string> Exceeded(PhysBoneBudget budget)
        {
            if (budget == null) yield break;
            foreach (var (name, value, limit) in new[] { ("Components", Components, budget.Components),
                ("Transforms", Transforms, budget.Transforms), ("Colliders", Colliders, budget.Colliders),
                ("Collision Checks", CollisionChecks, budget.CollisionChecks) })
                if (limit >= 0 && value > limit) yield return name + " " + value + " > " + limit;
        }

        public override string ToString() => Components + " components, " + Transforms + " transforms, " +
            Colliders + " colliders, " + CollisionChecks + " collision checks";
    }
}
