using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using UnityEngine;
using VRC.SDKBase;

namespace Noname.AvatarTools.AnimatorOptimization
{
    // Driver operations run top to bottom on state entry. Track reads between writes,
    // without treating separate drivers, states or controllers as one atomic block.
    // https://creators.vrchat.com/avatars/state-behaviors/#avatar-parameter-driver
    internal sealed class DriverOperationLiveness
    {
        private readonly HashSet<string> observers;
        private readonly ExternalParameterAccess external;
        private readonly Dictionary<VRC_AvatarParameterDriver, List<VirtualAnimatorController>> owners = new();

        internal DriverOperationLiveness(GameObject avatar, VirtualAnimatorController[] controllers, ISet<string> preserved)
        {
            observers = new HashSet<string>(AvatarParameterInputs.ExternalNames(avatar), StringComparer.Ordinal);
            observers.UnionWith(AnimatorReachability.BuiltIns);
            if (preserved != null) observers.UnionWith(preserved);
            external = ExternalParameterAccess.Collect();
            foreach (var controller in controllers)
            {
                var nodes = controller.AllReachableNodes().ToArray();
                foreach (var driver in nodes.OfType<VirtualState>().SelectMany(s => s.Behaviours)
                    .Concat(nodes.OfType<VirtualStateMachine>().SelectMany(m => m.Behaviours))
                    .OfType<VRC_AvatarParameterDriver>().Distinct())
                {
                    if (!owners.TryGetValue(driver, out var list)) owners.Add(driver, list = new());
                    list.Add(controller);
                }
            }
        }

        private bool Observed(string name) => observers.Contains(name) || external.IsRead(name) || external.IsWritten(name);

        internal int RemoveUnused(VRC_AvatarParameterDriver driver, ISet<string> live)
        {
            var operations = driver.parameters;
            if (operations == null || operations.Count == 0) return 0;
            // Unknown or malformed operations may abort execution or publish intermediate
            // values. Do not infer a later overwrite across an unverified operation.
            if (operations.Any(p => p == null || string.IsNullOrEmpty(p.name) || !Supported(p.type) ||
                p.type == VRC_AvatarParameterDriver.ChangeType.Copy && string.IsNullOrEmpty(p.source))) return 0;

            // A shared behaviour can execute in several controllers. A Set must be
            // valid in every owner before it can kill an earlier value.
            owners.TryGetValue(driver, out var driverOwners);
            bool Overwrites(VRC_AvatarParameterDriver.Parameter operation)
            {
                if (operation.type != VRC_AvatarParameterDriver.ChangeType.Set || Observed(operation.name) ||
                    !MotionAnalysis.Finite(operation.value) || driverOwners == null || driverOwners.Count == 0) return false;
                return ParameterDriverDataflow.ValidSet(driverOwners, operation);
            }

            var needed = new HashSet<string>(live, StringComparer.Ordinal);
            var removed = new bool[operations.Count];
            for (int i = operations.Count - 1; i >= 0; i--)
            {
                var operation = operations[i];
                if (operation == null || string.IsNullOrEmpty(operation.name)) continue;
                if (operation.type == VRC_AvatarParameterDriver.ChangeType.Random)
                {
                    // Random is never removed: it can advance shared runtime RNG state.
                    // Prevent Repeats can also consult the previous destination value.
                    if (operation.preventRepeats) needed.Add(operation.name);
                    continue;
                }
                if (!needed.Contains(operation.name) && !Observed(operation.name))
                {
                    removed[i] = true;
                    continue;
                }
                if (Overwrites(operation)) needed.Remove(operation.name);
                // Add reads its destination; Copy reads the source at this exact point,
                // even if another operation subsequently overwrites that source.
                if (operation.type == VRC_AvatarParameterDriver.ChangeType.Add) needed.Add(operation.name);
                if (operation.type == VRC_AvatarParameterDriver.ChangeType.Copy && !string.IsNullOrEmpty(operation.source))
                    needed.Add(operation.source);
            }
            for (int i = operations.Count - 1; i >= 0; i--)
                if (removed[i]) operations.RemoveAt(i);
            return removed.Count(r => r);
        }

        private static bool Supported(VRC_AvatarParameterDriver.ChangeType type) =>
            type == VRC_AvatarParameterDriver.ChangeType.Set || type == VRC_AvatarParameterDriver.ChangeType.Add ||
            type == VRC_AvatarParameterDriver.ChangeType.Copy || type == VRC_AvatarParameterDriver.ChangeType.Random;
    }
}
