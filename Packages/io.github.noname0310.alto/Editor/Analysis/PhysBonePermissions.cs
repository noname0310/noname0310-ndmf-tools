using System;
using VRC.Dynamics;

namespace Noname.AvatarTools.Analysis
{
    // Design reference: AAO 1.9.23 ParseAdvancedBool / ParsePermissionFilter.
    // Interpret SDK 3.10.6 PermissionFilter.IsAllowed without consulting raw,
    // inactive custom settings. Unknown modes remain observable and unequal.
    internal static class PhysBonePermissions
    {
        private readonly struct EffectivePermissions : IEquatable<EffectivePermissions>
        {
            private readonly bool self, others;
            private readonly DynamicsUsageFlags contentTypes;

            internal EffectivePermissions(bool self, bool others, DynamicsUsageFlags contentTypes)
            { this.self = self; this.others = others; this.contentTypes = contentTypes; }

            internal bool IsEmpty => !self && !others && contentTypes == 0;

            public bool Equals(EffectivePermissions other) =>
                self == other.self && others == other.others && contentTypes == other.contentTypes;
        }

        private static bool TryResolve(VRCPhysBoneBase.AdvancedBool mode, bool self, bool others,
            DynamicsUsageFlags contentTypes, out EffectivePermissions permissions)
        {
            permissions = default;
            switch (mode)
            {
                case VRCPhysBoneBase.AdvancedBool.False:
                    return true;
                case VRCPhysBoneBase.AdvancedBool.True:
                    permissions = new EffectivePermissions(true, true, (DynamicsUsageFlags)(-1));
                    return true;
                case VRCPhysBoneBase.AdvancedBool.Other:
                    permissions = new EffectivePermissions(self, others, contentTypes);
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryResolve(VRCPhysBoneBase.AdvancedBool mode, VRCPhysBoneBase.PermissionFilter filter,
            out EffectivePermissions permissions) =>
            TryResolve(mode, filter.allowSelf, filter.allowOthers, filter.contentTypes, out permissions);

        internal static bool MayAllowGlobalCollision(VRCPhysBoneColliderBase collider) =>
            !TryResolve(collider.globalCollision, collider.globalCollisionAllowSelf,
                collider.globalCollisionAllowOthers, collider.globalCollisionFlags, out var permissions) || !permissions.IsEmpty;

        internal static bool MayAllow(VRCPhysBoneBase.AdvancedBool mode, VRCPhysBoneBase.PermissionFilter filter) =>
            !TryResolve(mode, filter, out var permissions) || !permissions.IsEmpty;

        internal static bool Equivalent(VRCPhysBoneBase.AdvancedBool a, VRCPhysBoneBase.PermissionFilter aFilter,
            VRCPhysBoneBase.AdvancedBool b, VRCPhysBoneBase.PermissionFilter bFilter) =>
            TryResolve(a, aFilter, out var left) && TryResolve(b, bFilter, out var right) && left.Equals(right);
    }
}
