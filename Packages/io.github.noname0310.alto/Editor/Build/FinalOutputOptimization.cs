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
    // the freshly opened controllers; checked hierarchy relocation commits native paths.
    internal static class FinalOutputOptimization
    {
        internal static void Apply(GameObject avatar, IDictionary<object, VirtualAnimatorController> entries,
            ResolvedParameterPolicy policy, ISet<string> frozen, ObjectOptimizationState objects,
            AnimatorOptimizationSettings animator, ExpressionSettings expressions, OptimizationBuildReport report,
            Action<UnityEngine.Object> save, ISet<string> localCandidates, Func<int> optimizeMeshes = null,
            Func<OptimizationChanges> optimizePhysics = null, Func<OptimizationChanges> optimizeStaticFrames = null, Action<AnalysisDomain> invalidate = null,
            ShaderPropertyUsage shaders = null, OptimizationPolicy initialization = null,
            Materials.MaterialOptimizationState materials = null, Meshes.MeshOptimizationState meshes = null,
            Func<int> optimizeBlendShapes = null, Func<OptimizationChanges> optimizeShaderConstants = null, OptimizationAnalysis facts = null,
            bool runtimeDependenciesOnly = true)
        {
            shaders ??= new ShaderPropertyUsage();
            facts ??= new OptimizationAnalysis();
            bool previous = objects.RuntimeDependenciesOnly;
            objects.RuntimeDependenciesOnly = runtimeDependenciesOnly;
            // Late SDK writers can invalidate any proof captured before generation.
            objects.PrunedSensorInputs.Clear();
            var expressionReports = new List<ExpressionOptimizationReport>();
            int iterations;
            try
            {
                // Capture avatar-owned readers introduced by late generators before
                // cleanup removes them. Private child inputs do not prove sensor use.
                ObjectOptimizationPass.IncludeConsumers(objects, avatar, entries);
                int Outputs()
                {
                    int changes = 0;
                    if (expressions != null)
                    {
                        changes = ExpressionOptimizationPass.ApplyToAvatar(avatar, expressions, policy, frozen,
                            entries, null, report, save, localCandidates, objects, animator, shaders, initialization, facts);
                        expressionReports.Add(report.Expressions);
                    }
                    bool expressionOwnsSensorProofs = expressions?.LocalParameters == true &&
                        avatar.GetComponent<VRCAvatarDescriptor>()?.expressionParameters != null;
                    // Its inner fixed point does not include physics or static-frame
                    // rewrites. Those consumers must observe expression removals too.
                    return changes + ApplyOutputs(avatar, entries, policy, objects, animator, report, expressionOwnsSensorProofs, frozen, shaders,
                        initialization, materials, meshes, save, facts);
                }
                // These structural passes inspect raw references and animation/
                // simulation writers. Their eligibility does not use material output
                // liveness, shader constants or shader program semantics.
                const AnalysisDomain structuralInputs = AnalysisDomain.Animation | AnalysisDomain.Hierarchy | AnalysisDomain.Meshes |
                    AnalysisDomain.Physics | AnalysisDomain.ExternalInputs;
                iterations = OptimizationWorklist.Run(new[]
                {
                    OptimizationWorklist.Stage.WithChanges("runtime physics", structuralInputs, AnalysisDomain.Physics | AnalysisDomain.Hierarchy | AnalysisDomain.Animation,
                        () => optimizePhysics?.Invoke() ?? default),
                    OptimizationWorklist.Stage.WithChanges("runtime static frames", structuralInputs,
                        AnalysisDomain.Physics | AnalysisDomain.Meshes | AnalysisDomain.Hierarchy | AnalysisDomain.Animation,
                        () => optimizeStaticFrames?.Invoke() ?? default),
                    new OptimizationWorklist.Stage("runtime consumers", AnalysisDomain.All,
                        AnalysisDomain.Animation | AnalysisDomain.Hierarchy | AnalysisDomain.Physics | AnalysisDomain.ExternalInputs | AnalysisDomain.Materials | AnalysisDomain.Meshes, Outputs),
                    new OptimizationWorklist.Stage("runtime BlendShapes", AnalysisDomain.All,
                        AnalysisDomain.Animation | AnalysisDomain.Meshes | AnalysisDomain.Hierarchy, () => optimizeBlendShapes?.Invoke() ?? 0),
                    OptimizationWorklist.Stage.WithChanges("runtime shader constants", AnalysisDomain.All,
                        AnalysisDomain.Materials | AnalysisDomain.Animation, () => optimizeShaderConstants?.Invoke() ?? default),
                    new OptimizationWorklist.Stage("runtime meshes", AnalysisDomain.All,
                        AnalysisDomain.Meshes | AnalysisDomain.Hierarchy | AnalysisDomain.Animation, () => optimizeMeshes?.Invoke() ?? 0)
                }, report: report, invalidateChanges: changed => { facts.Invalidate(changed); invalidate?.Invoke(changed.Domains); });
                ExpressionOptimizationPass.MergeReports(expressionReports, report);
                report.Messages.Add("Final runtime output analysis converged after " + iterations + " iterations.");
            }
            finally { objects.RuntimeDependenciesOnly = previous; facts.Invalidate(AnalysisDomain.All); }
        }

        private static int ApplyOutputs(GameObject avatar, IDictionary<object, VirtualAnimatorController> entries,
            ResolvedParameterPolicy policy, ObjectOptimizationState objects, AnimatorOptimizationSettings animator,
            OptimizationBuildReport report, bool expressionOwnsSensorProofs, ISet<string> frozen, ShaderPropertyUsage shaders,
            OptimizationPolicy initialization, Materials.MaterialOptimizationState materials,
            Meshes.MeshOptimizationState meshes, Action<UnityEngine.Object> save, OptimizationAnalysis facts)
        {
            if (!objects.Enabled && animator?.Enabled != true) return 0;
            var controllers = entries.Values.Distinct().ToArray();
            AvatarAnalysisSnapshot Read() => facts.Avatar(avatar, entries, null, policy.Preserved);
            var analysis = Read().Reachability;
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
                {
                    var sensors = SensorParameterPruning.ApplyChanges(avatar, controllers, objects, policy.Preserved, external,
                        report.Messages, frozenInputs: frozen);
                    changes += sensors.Count;
                    facts.Invalidate(sensors.Domains);
                }
                var snapshot = new ObjectAnalysisSnapshot(avatar, entries, null, objects, policy.Preserved, shaders, Read());
                bool Allow(Transform target) => initialization?.AllowsInitialValueChanges(target) == true;
                if (objects.BakeConstantComponentProperties)
                {
                    changes += snapshot.Rewrite(() => ConstantComponentProperties.Apply(snapshot.Index, objects, Allow, report.Messages,
                        numericPolicy: initialization, snapshot: snapshot));
                    changes += snapshot.Rewrite(() => ConstantResourceReferences.Apply(snapshot, objects, Allow, report.Messages, meshes));
                }
                if (objects.BakeConstantTransforms)
                    changes += snapshot.Rewrite(() => ConstantTransformOutputs.Apply(snapshot.Index, objects, Allow, report.Messages,
                        numericPolicy: initialization, snapshot: snapshot));
                if (objects.RemoveUnusedTransforms)
                    changes += UnusedTransformOutputs.Apply(avatar, entries, null, objects, policy.Preserved, report.Messages, shaders, snapshot);
                if (objects.RemoveRedundantConstraints)
                {
                    changes += snapshot.Rewrite(() => ConstantConstraintSettings.Apply(snapshot, objects, Allow, report.Messages));
                    changes += ConstraintSourcePruning.Apply(avatar, entries, null, objects, policy.Preserved, report.Messages, shaders, snapshot);
                    changes += RedundantConstraints.Apply(avatar, entries, null, objects, policy.Preserved, report.Messages, shaders, snapshot);
                    changes += ParentConstraintOptimization.Apply(avatar, entries, null, objects, policy.Preserved, report.Messages,
                        shaders, snapshot, commitNativePaths: true, numericPolicy: initialization);
                }
                if (objects.RemoveUnusedObjects)
                    changes += UnusedObjectCollection.Apply(avatar, entries, null, objects, policy.Preserved, report.Messages, shaders, snapshot);
            }
            if (animator?.Enabled == true)
            {
                var additive = new HashSet<VirtualAnimatorController>(entries.Where(e =>
                    e.Key is VRCAvatarDescriptor.AnimLayerType type && type == VRCAvatarDescriptor.AnimLayerType.Additive).Select(e => e.Value));
                var outputs = new ExpressionOutputUsage(avatar, entries, null);
                var rounding = new AnimationOutputRounding(avatar, entries, null, initialization, policy.Preserved);
                var result = AnimatorOptimizer.Optimize(avatar, controllers, animator, additive,
                    Read().Reachability, policy.Preserved,
                    animator.StateLogic ? outputs.IsObservable : null, policy.Roots, entries, finalParameterDeclarations: true,
                    allowNumericalDifferences: rounding.Allows);
                AnimatorOptimizationPass.BakeMaterialOutputs(avatar, entries, null, policy.Preserved, animator, result,
                    initialization, objects, materials, material => save(material));
                changes += result.Changes;
                if (result.Changes != 0) facts.Invalidate(AnalysisDomain.Animation | AnalysisDomain.Materials);
                report.Messages.Add("Animator after final runtime cleanup: " + result);
            }
            return changes;
        }
    }
}
