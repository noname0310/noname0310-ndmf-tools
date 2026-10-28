using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEngine;
using VRC.SDKBase;
using Operation = VRC.SDKBase.VRC_AvatarParameterDriver.Parameter;
using Change = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType;

namespace Noname.AvatarTools.Analysis
{
    // Within one entry callback, a read observes the most recent earlier write.
    // Each set describes dependencies on values at callback entry. Callers still
    // retain all writes to a destination when its intermediate values are observed.
    // https://creators.vrchat.com/avatars/state-behaviors/#avatar-parameter-driver
    internal static class ParameterDriverDataflow
    {
        internal static bool ValidSet(IReadOnlyCollection<VirtualAnimatorController> owners, Operation operation) =>
            operation != null && operation.type == Change.Set && !string.IsNullOrEmpty(operation.name) &&
            !AnimatorReachability.BuiltIns.Contains(operation.name) &&
            owners != null && owners.Count > 0 && owners.All(c => c.Parameters.TryGetValue(operation.name, out var p) &&
                ValidValue(p.type, operation.value));

        private static bool ValidValue(AnimatorControllerParameterType type, float value) => MotionAnalysis.Finite(value) &&
            (type == AnimatorControllerParameterType.Float ||
             type == AnimatorControllerParameterType.Bool && (value == 0 || value == 1) ||
             type == AnimatorControllerParameterType.Int && value >= (double)int.MinValue &&
                value <= (double)int.MaxValue && Math.Truncate(value) == value);

        internal static HashSet<string>[] Analyze(VRC_AvatarParameterDriver driver,
            IReadOnlyCollection<VirtualAnimatorController> owners)
        {
            if (driver == null || driver.parameters == null || owners == null || owners.Count == 0 ||
                driver.parameters.Any(operation => !Valid(operation, owners))) return null;

            var current = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            HashSet<string> Read(string name) => current.TryGetValue(name, out var value)
                ? new HashSet<string>(value, StringComparer.Ordinal) : new HashSet<string>(new[] { name }, StringComparer.Ordinal);
            var result = new HashSet<string>[driver.parameters.Count];
            for (int i = 0; i < result.Length; i++)
            {
                var operation = driver.parameters[i];
                var sources = operation.type == Change.Copy ? Read(operation.source) :
                    operation.type == Change.Add || operation.type == Change.Random && operation.preventRepeats ? Read(operation.name) :
                    new HashSet<string>(StringComparer.Ordinal);
                result[i] = sources;
                current[operation.name] = sources;
            }
            return result;
        }

        private static bool Valid(Operation operation, IReadOnlyCollection<VirtualAnimatorController> owners)
        {
            if (operation == null || string.IsNullOrEmpty(operation.name) ||
                AnimatorReachability.BuiltIns.Contains(operation.name) ||
                owners.Any(c => !c.Parameters.TryGetValue(operation.name, out var p) || !Numeric(p.type))) return false;
            switch (operation.type)
            {
                case Change.Set: return ValidSet(owners, operation);
                case Change.Add:
                    // Integer overflow and float-to-int conversion need range proofs before
                    // treating them as definite assignments. Keep the original graph for now.
                    return MotionAnalysis.Finite(operation.value) &&
                        owners.All(c => c.Parameters[operation.name].type == AnimatorControllerParameterType.Float);
                case Change.Copy:
                    return !string.IsNullOrEmpty(operation.source) &&
                        !AnimatorReachability.BuiltIns.Contains(operation.source) &&
                        owners.All(c => c.Parameters.TryGetValue(operation.source, out var p) && Numeric(p.type) &&
                            (c.Parameters[operation.name].type != AnimatorControllerParameterType.Int ||
                             p.type != AnimatorControllerParameterType.Float && !operation.convertRange)) &&
                        (!operation.convertRange || MotionAnalysis.Finite(operation.sourceMin) && MotionAnalysis.Finite(operation.sourceMax) &&
                        MotionAnalysis.Finite(operation.destMin) && MotionAnalysis.Finite(operation.destMax) &&
                        MotionAnalysis.Finite(operation.sourceMax - operation.sourceMin) && operation.sourceMax > operation.sourceMin &&
                        MotionAnalysis.Finite(operation.destMax - operation.destMin));
                case Change.Random:
                    return MotionAnalysis.Finite(operation.chance) && operation.chance >= 0 && operation.chance <= 1 &&
                        MotionAnalysis.Finite(operation.valueMin) && MotionAnalysis.Finite(operation.valueMax) && operation.valueMin <= operation.valueMax &&
                        MotionAnalysis.Finite(operation.valueMax - operation.valueMin) &&
                        (!operation.preventRepeats || operation.valueMin < operation.valueMax &&
                            owners.All(c => c.Parameters[operation.name].type == AnimatorControllerParameterType.Int)) &&
                        owners.All(c => c.Parameters[operation.name].type == AnimatorControllerParameterType.Bool ||
                            ValidValue(c.Parameters[operation.name].type, operation.valueMin) &&
                            ValidValue(c.Parameters[operation.name].type, operation.valueMax));
                default: return false;
            }
        }

        private static bool Numeric(AnimatorControllerParameterType type) => type is
            AnimatorControllerParameterType.Float or AnimatorControllerParameterType.Int or AnimatorControllerParameterType.Bool;
    }
}
