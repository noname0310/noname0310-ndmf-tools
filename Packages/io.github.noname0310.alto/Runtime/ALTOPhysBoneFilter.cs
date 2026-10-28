using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools
{
    public enum PhysBoneFilterMode { RemoveListed, KeepListed }
    public enum BudgetExceededAction { Warning, Error }

    [Serializable]
    public sealed class PhysBoneRootSelection
    {
        [Tooltip("Match PhysBones by the bone they control, including PhysBones created during the build.")]
        public Transform Root;
        public bool IncludeChildren = true;
    }

    [Serializable]
    public sealed class PhysBoneBudget
    {
        [Tooltip("Maximum SDK component count, or -1 for no limit.")]
        public int Components = -1;
        public int Transforms = -1;
        public int Colliders = -1;
        public int CollisionChecks = -1;
        public BudgetExceededAction OnExceeded;
    }

    [AddComponentMenu("Noname/ALTO PhysBone Filter")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedPhysBoneFilter")]
    public sealed class ALTOPhysBoneFilter : ALTOPlatformOptimizationComponent
    {
        public PhysBoneFilterMode FilterMode = PhysBoneFilterMode.KeepListed;
        public List<VRCPhysBone> PhysBones = new();
        public List<PhysBoneRootSelection> BoneRoots = new();
        [Tooltip("Remove unused colliders inside the scope. Shared, global, animated-global and explicitly protected colliders remain.")]
        public bool RemoveUnreferencedColliders = true;
        public List<VRCPhysBoneCollider> PreserveColliders = new();
        [Tooltip("Avatar-wide SDK counts checked after mesh and bone optimizers. -1 disables an individual limit.")]
        public PhysBoneBudget Budget = new();
    }
}
