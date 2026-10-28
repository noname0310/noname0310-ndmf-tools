using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Noname.AvatarTools.ObjectOptimization
{
    // SDK semantics: https://creators.vrchat.com/common-components/constraints/constraints-api/
    // A full-weight local scale constraint with an identity offset copies local scale.
    // Parent frames do not affect this proof. World-space solves remain untouched.
    internal static class RedundantConstraints
    {
        internal static int Apply(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths, ObjectOptimizationState settings,
            IEnumerable<string> preservedParameters, ICollection<string> messages)
        {
            var index = AvatarAnimationIndex.Create(avatar, controllers, paths, preservedParameters);
            var mutation = TransformMutationAnalysis.Create(avatar, index);
            string unsafeReason = ObjectOptimizationSafety.UnsupportedRuntime(avatar) ?? index.UnsafeReason ?? mutation.Uncertain;
            if (unsafeReason != null)
            { messages.Add("Redundant constraint analysis skipped: " + unsafeReason); return 0; }
            var graph = AvatarDependencyGraph.Create(avatar, index, settings, collectObjects: false);
            var objectKeys = new HashSet<UnityEngine.Object>(index.Bindings.SelectMany(use =>
                use.ObjectKeys ?? Array.Empty<ObjectReferenceKeyframe>()).Select(key => key.value));
            int removed = 0;
            foreach (var constraint in avatar.GetComponentsInChildren<VRCScaleConstraint>(true))
            {
                if (constraint.GetType() != typeof(VRCScaleConstraint) || !settings.Scope.Contains(constraint.transform) ||
                    settings.Protected(constraint) || index.ForObject(constraint).Count != 0 || objectKeys.Contains(constraint) ||
                    graph.ReferenceOwners.ContainsKey(constraint) ||
                    constraint.GetComponents<Component>().Any(other => other != null && other != constraint && BuildFilterPass.Requires(other, constraint))) continue;
                var target = constraint.TargetTransform != null ? constraint.TargetTransform : constraint.transform;
                if (target == avatar.transform || !settings.Scope.Contains(target) || settings.Protected(target) ||
                    !constraint.SolveInLocalSpace || constraint.FreezeToWorld || !constraint.Locked || constraint.GlobalWeight != 1 ||
                    constraint.Sources.Count != 1 || mutation.WrittenByOther(target, constraint, scaleOnly: true)) continue;
                var source = constraint.Sources[0];
                if (source.SourceTransform == null || source.SourceTransform == avatar.transform ||
                    !source.SourceTransform.IsChildOf(avatar.transform) || source.Weight != 1 ||
                    mutation.WrittenByOther(source.SourceTransform, constraint, scaleOnly: true)) continue;
                var original = target.localScale;
                var input = source.SourceTransform.localScale;
                var offset = constraint.ScaleOffset;
                var rest = constraint.ScaleAtRest;
                bool constant = true;
                for (int axis = 0; axis < 3; axis++)
                {
                    if (!MotionAnalysis.Finite(original[axis]) || !MotionAnalysis.Finite(input[axis]) ||
                        !MotionAnalysis.Finite(offset[axis]) || !MotionAnalysis.Finite(rest[axis])) { constant = false; break; }
                    bool affected = axis == 0 ? constraint.AffectsScaleX : axis == 1 ? constraint.AffectsScaleY : constraint.AffectsScaleZ;
                    if (affected && (offset[axis] != 1 || input[axis] != original[axis])) { constant = false; break; }
                }
                if (!constant) continue;
                string path = AnimationUtility.CalculateTransformPath(constraint.transform, avatar.transform);
                if (!settings.TryDestroy(constraint)) continue;
                // Editor preview teardown can restore its cached pose. Keep the
                // proven current value, including a non-neutral authored scale.
                target.localScale = original;
                messages.Add("Redundant local scale constraint: " + path + "; existing target scale preserved.");
                removed++;
            }
            return removed;
        }
    }
}
