using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.Build
{
    // SDK generators must complete before this boundary. Authoring references protect
    // inputs during NDMF processing; only runtime outputs root the final dependency graph.
    // Current native paths are authoritative at this boundary. Mesh rewrites update
    // the freshly opened controllers; transform reparenting remains in NDMF.
    internal static class FinalOutputOptimization
    {
        internal static void Apply(GameObject avatar, IDictionary<object, VirtualAnimatorController> entries,
            ResolvedParameterPolicy policy, ISet<string> frozen, ObjectOptimizationState objects,
            AnimatorOptimizationSettings animator, ExpressionSettings expressions, OptimizationBuildReport report,
            Action<UnityEngine.Object> save, ISet<string> localCandidates, Func<int> optimizeMeshes = null)
        {
            bool previous = objects.RuntimeDependenciesOnly;
            objects.RuntimeDependenciesOnly = true;
            // Late SDK writers can invalidate any proof captured before generation.
            objects.PrunedSensorInputs.Clear();
            var expressionReports = new List<ExpressionOptimizationReport>();
            int changes, iterations = 0;
            try
            {
                // Capture avatar-owned readers introduced by late generators before
                // cleanup removes them. Private child inputs do not prove sensor use.
                ObjectOptimizationPass.IncludeConsumers(objects, avatar, entries);
                do
                {
                    iterations++;
                    if (expressions != null)
                    {
                        ExpressionOptimizationPass.ApplyToAvatar(avatar, expressions, policy, frozen,
                            entries, null, report, save, localCandidates, objects, animator);
                        expressionReports.Add(report.Expressions);
                    }
                    bool expressionOwnsSensorProofs = expressions?.LocalParameters == true &&
                        avatar.GetComponent<VRCAvatarDescriptor>()?.expressionParameters != null;
                    // Expression cleanup already reaches its own fixed point. Only changes
                    // made afterwards can invalidate that result and require another round.
                    changes = ApplyOutputs(avatar, entries, policy, objects, animator, report, expressionOwnsSensorProofs, frozen);
                    changes += optimizeMeshes?.Invoke() ?? 0;
                } while (changes != 0);
                ExpressionOptimizationPass.MergeReports(expressionReports, report);
                report.Messages.Add("Final runtime output analysis converged after " + iterations + " iterations.");
            }
            finally { objects.RuntimeDependenciesOnly = previous; }
        }

        private static int ApplyOutputs(GameObject avatar, IDictionary<object, VirtualAnimatorController> entries,
            ResolvedParameterPolicy policy, ObjectOptimizationState objects, AnimatorOptimizationSettings animator,
            OptimizationBuildReport report, bool expressionOwnsSensorProofs, ISet<string> frozen)
        {
            if (!objects.Enabled && animator?.Enabled != true) return 0;
            var controllers = entries.Values.Distinct().ToArray();
            var analysis = AnimatorReachability.Analyze(avatar, controllers, policy.Preserved);
            var external = ExternalParameterAccess.Collect();
            string reason = ObjectOptimizationSafety.UnsupportedRuntime(avatar) ?? analysis.FallbackReason;
            if (reason == null && policy.Uncertain) reason = "parameter preservation mapping is uncertain";
            if (reason == null && external.Uncertain) reason = string.Join("; ", external.Diagnostics);
            if (reason != null)
            {
                report.Messages.Add("Final runtime output cleanup skipped: " + reason);
                return 0;
            }
            int changes = 0;
            if (objects.Enabled)
            {
                // Local Expression declarations remain consumers unless expression cleanup
                // separately proves they can be removed. Disabling that option is respected.
                // One owner evaluates sensor proofs for this iteration. Rechecking with a
                // different local-declaration eligibility set would alternate the proof for
                // partially observed sensors whose result Transform must remain alive.
                if (objects.PruneUnusedSensorParameters && !expressionOwnsSensorProofs)
                    changes += SensorParameterPruning.Apply(avatar, controllers, objects, policy.Preserved, external,
                        report.Messages, frozenInputs: frozen);
                if (objects.RemoveUnusedTransforms)
                    changes += UnusedTransformOutputs.Apply(avatar, entries, null, objects, policy.Preserved, report.Messages);
                if (objects.RemoveRedundantConstraints)
                {
                    changes += ConstraintSourcePruning.Apply(avatar, entries, null, objects, policy.Preserved, report.Messages);
                    changes += RedundantConstraints.Apply(avatar, entries, null, objects, policy.Preserved, report.Messages);
                }
                if (objects.RemoveUnusedObjects)
                    changes += UnusedObjectCollection.Apply(avatar, entries, null, objects, policy.Preserved, report.Messages);
            }
            if (animator?.Enabled == true)
            {
                var additive = new HashSet<VirtualAnimatorController>(entries.Where(e =>
                    e.Key is VRCAvatarDescriptor.AnimLayerType type && type == VRCAvatarDescriptor.AnimLayerType.Additive).Select(e => e.Value));
                var outputs = new ExpressionOutputUsage(avatar, entries, null);
                var result = AnimatorOptimizer.Optimize(avatar, controllers, animator, additive,
                    AnimatorReachability.Analyze(avatar, controllers, policy.Preserved), policy.Preserved,
                    animator.StateLogic ? outputs.IsObservable : null, policy.Roots, entries, finalParameterDeclarations: true);
                changes += result.Changes;
                report.Messages.Add("Animator after final runtime cleanup: " + result);
            }
            return changes;
        }
    }
}
