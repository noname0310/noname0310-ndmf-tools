using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Noname.AvatarTools.ObjectOptimization
{
    internal static class ConstraintSourcePruning
    {
        // The public SDK contract permits animation of only the first sixteen sources.
        // https://creators.vrchat.com/common-components/constraints/constraints-api/#sources
        private const int KeyableSources = 16;

        internal static int Apply(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths, ObjectOptimizationState settings,
            IEnumerable<string> preservedParameters, ICollection<string> messages)
        {
            var index = AvatarAnimationIndex.Create(avatar, controllers, paths, preservedParameters);
            var analysis = AnimatorReachability.Analyze(avatar, index.Controllers, preservedParameters);
            string reason = ObjectOptimizationSafety.UnsupportedRuntime(avatar) ?? index.UnsafeReason ?? analysis.FallbackReason;
            if (reason != null) { messages.Add("Constraint source pruning skipped: " + reason); return 0; }
            var graph = AvatarDependencyGraph.Create(avatar, index, settings, collectObjects: false);
            var proof = new InvariantComponentSettings(index, analysis, controllers);
            var objectKeys = index.Bindings.SelectMany(u => u.ObjectKeys ?? Array.Empty<ObjectReferenceKeyframe>())
                .Select(k => k.value).ToHashSet();
            int removed = 0;
            foreach (var constraint in avatar.GetComponentsInChildren<VRCConstraintBase>(true))
            {
                var type = constraint.GetType();
                if (type != typeof(VRCParentConstraint) && type != typeof(VRCPositionConstraint) &&
                    type != typeof(VRCRotationConstraint) && type != typeof(VRCScaleConstraint) &&
                    type != typeof(VRCAimConstraint) && type != typeof(VRCLookAtConstraint)) continue;
                var target = constraint.TargetTransform != null ? constraint.TargetTransform : constraint.transform;
                if (!settings.Scope.Contains(constraint.transform) || !settings.Scope.Contains(target) ||
                    settings.Protected(constraint) || settings.Protected(target) || constraint.FreezeToWorld ||
                    !constraint.Locked || objectKeys.Contains(constraint) || graph.ReferenceOwners.ContainsKey(constraint) ||
                    constraint.GetComponents<Component>().Any(c => c != null && c != constraint && BuildFilterPass.Requires(c, constraint))) continue;
                var sources = constraint.Sources.ToArray();
                // Keep the all-zero/empty-source case: removing the last source can
                // stop an evaluator from writing its At Rest pose.
                if (!sources.Any(s => s.Weight == 0) || !sources.Any(s => s.Weight > 0 && s.SourceTransform != null) ||
                    sources.Any(s => !MotionAnalysis.Finite(s.Weight) || s.Weight < 0)) continue;
                var uses = index.ForObject(constraint);
                if (uses.Any(u => u.Immutable || !u.Complete || u.Binding.isPPtrCurve ||
                        u.Objects().Count() != 1 || u.Objects().Single() != constraint) ||
                    uses.Any(u => u.Binding.propertyName.StartsWith("Sources", StringComparison.Ordinal) &&
                        (!SourceProperty(u.Binding.propertyName, out int slot, out _) || slot >= sources.Length)) ||
                    !proof.IsConstantProperty(constraint, nameof(constraint.Locked)) ||
                    !proof.IsConstantProperty(constraint, nameof(constraint.FreezeToWorld))) continue;

                var remap = new int[sources.Length];
                var retained = new List<VRCConstraintSource>();
                for (int i = 0; i < sources.Length; i++)
                {
                    // Overflow sources cannot be animated. Unsupported source bindings
                    // above were rejected so compaction cannot activate a dormant track.
                    bool unused = sources[i].Weight == 0 && (i >= KeyableSources ||
                        proof.IsConstantProperty(constraint, "Sources.source" + i + ".Weight"));
                    remap[i] = unused ? -1 : retained.Count;
                    if (!unused) retained.Add(sources[i]);
                }
                // A single-source SDK solve can apply fractional weight differently
                // from a normalized multi-source solve. A zero-weight alias retains
                // the latter without keeping an otherwise unused source hierarchy.
                if (retained.Count == 1)
                {
                    int survivor = Array.FindIndex(remap, i => i == 0);
                    if (retained[0].Weight != 1 || survivor < KeyableSources &&
                        !proof.IsConstantProperty(constraint, "Sources.source" + survivor + ".Weight"))
                        retained.Add(new VRCConstraintSource(retained[0].SourceTransform, 0));
                }
                var kept = retained.ToArray();
                int count = sources.Length - kept.Length;
                bool changedSources = !sources.SequenceEqual(kept);
                int editedCurves = 0;
                var constantProperties = uses.Select(u => u.Binding.propertyName).Distinct()
                    .Where(p => proof.IsConstantProperty(constraint, p)).ToHashSet();
                foreach (var group in uses.GroupBy(u => u.Clip))
                {
                    var clip = group.Key;
                    float end = MotionAnalysis.EndTime(clip);
                    var rewritten = new List<(EditorCurveBinding Binding, AnimationCurve Curve)>();
                    foreach (var use in group)
                    {
                        var binding = use.Binding;
                        bool isSource = SourceProperty(binding.propertyName, out int slot, out string member);
                        if (constantProperties.Contains(binding.propertyName) || isSource && remap[slot] < 0)
                        {
                            clip.SetFloatCurve(binding, null);
                            editedCurves++;
                        }
                        else if (isSource && remap[slot] != slot)
                        {
                            clip.SetFloatCurve(binding, null);
                            binding.propertyName = "Sources.source" + remap[slot] + "." + member;
                            rewritten.Add((binding, use.Curve));
                            editedCurves++;
                        }
                    }
                    // Remove old slots first: a destination may still be another old
                    // source's binding. Retain curve tangents, wrapping and binding flags.
                    foreach (var edit in rewritten) clip.SetFloatCurve(edit.Binding, edit.Curve);
                    MotionAnalysis.PreserveDuration(clip, end);
                }
                if (!changedSources && editedCurves == 0) continue;
                // The SDK list retains serialized fixed-slot references beyond
                // Count after RemoveAt/Clear. Rebuild it so those obsolete edges
                // cannot retain unused source objects in dependency analysis.
                constraint.Sources = new VRCConstraintSourceKeyableList(kept);
                constraint.ApplyConfigurationChanges();
                removed += Math.Max(1, count);
                messages.Add("Removed " + count + " invariant zero-weight constraint sources: " +
                    AnimationUtility.CalculateTransformPath(constraint.transform, avatar.transform) + " / " + type.Name +
                    "; retained normalization where required and updated " + editedCurves + " curves.");
            }
            return removed;
        }

        private static bool SourceProperty(string property, out int slot, out string member)
        {
            const string prefix = "Sources.source";
            slot = -1; member = null;
            if (!property.StartsWith(prefix, StringComparison.Ordinal)) return false;
            int separator = property.IndexOf('.', prefix.Length);
            if (separator < 0 || !int.TryParse(property.Substring(prefix.Length, separator - prefix.Length), out slot) ||
                slot < 0 || slot >= KeyableSources ||
                !property.StartsWith(prefix + slot + ".", StringComparison.Ordinal)) return false;
            member = property.Substring(separator + 1);
            if (member == "Weight") return true;
            foreach (string offset in new[] { "ParentPositionOffset.", "ParentRotationOffset." })
                if (member.StartsWith(offset, StringComparison.Ordinal) && member.Length == offset.Length + 1 &&
                    "xyz".Contains(member[member.Length - 1])) return true;
            return false;
        }
    }
}
