using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using Noname.AvatarTools.Physics;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    internal static class StaticTransformOptimizationPass
    {
        internal static int Apply(BuildContext context)
            => ApplyChanges(context).Count;

        internal static OptimizationChanges ApplyChanges(BuildContext context)
        {
            if (!Enabled(context)) return default;
            using var lease = OptimizationSession.Acquire(context);
            var services = lease.Services;
            var entries = services.ControllerContext.Controllers.Where(e => !(e.Key is UnityEngine.Object o) || o != null)
                .ToDictionary(e => e.Key, e => e.Value);
            var policy = ParameterPolicyPass.Resolve(context, entries);
            return Converge(context, () => ApplyIteration(context, entries, policy,
                services.ObjectPathRemapper.GetVirtualToRealPathMap(), mesh => context.AssetSaver.SaveAsset(mesh), services.ObjectPathRemapper.GetVirtualPathForObject));
        }

        internal static bool Enabled(BuildContext context) => context.GetState<MeshOptimizationState>().RebindStaticBones ||
            context.GetState<PhysBoneOptimizationState>().RebaseColliderRoots || context.GetState<ObjectOptimizationState>().MergeStaticObjects ||
            context.GetState<ObjectOptimizationState>().HoistComponents || context.GetState<ObjectOptimizationState>().FoldStaticTransformsIntoComponents;

        internal static OptimizationChanges ApplyFinalChanges(BuildContext context, IDictionary<object, VirtualAnimatorController> entries,
            ResolvedParameterPolicy policy, Action<Mesh> save)
            => Converge(context, () => ApplyIteration(context, entries, policy, null, save, null));

        private static OptimizationChanges Converge(BuildContext context, Func<OptimizationChanges> iteration)
        {
            if (!Enabled(context)) return default;
            OptimizationChanges changes = default;
            // Structural rewrites form a local closure. Rebuild their proof snapshots
            // after every batch, but do not rerun whole-Animator/output analysis for
            // every level in a static hierarchy. The outer worklist revisits this
            // closure if another pass removes a writer or consumer afterwards.
            const AnalysisDomain affected = AnalysisDomain.Animation | AnalysisDomain.Hierarchy | AnalysisDomain.Meshes | AnalysisDomain.Physics;
            OptimizationWorklist.Run(new[] { OptimizationWorklist.Stage.WithChanges("static frame closure", affected, affected,
                () => { var next = iteration(); changes += next; return next; }) },
                context.GetState<OptimizationAnalysis>().Invalidate,
                report: context.GetState<OptimizationBuildReport>());
            return changes;
        }

        private static OptimizationChanges ApplyIteration(BuildContext context, IDictionary<object, VirtualAnimatorController> entries,
            ResolvedParameterPolicy policy, IReadOnlyDictionary<string, string> paths, Action<Mesh> save, Func<Transform, string> pathFor)
        {
            if (!Enabled(context) || policy.Uncertain) return default;
            var avatar = context.AvatarRootObject;
            var objects = context.GetState<ObjectOptimizationState>();
            var report = context.GetState<OptimizationBuildReport>();
            var facts = context.GetState<OptimizationAnalysis>().Avatar(avatar, entries, paths, policy.Preserved);
            var animations = facts.Index;
            var issue = ObjectOptimizationSafety.UnsupportedRuntime(avatar) ?? animations.UnsafeReason;
            if (issue != null) { report.Messages.Add("Static frame optimization retained inputs: " + issue); return default; }
            var referenceSettings = new ObjectOptimizationState { Scope = new OptimizationBuildScope { Root = avatar.transform },
                RuntimeDependenciesOnly = objects.RuntimeDependenciesOnly };
            referenceSettings.ProtectedSubtrees.AddRange(objects.ProtectedSubtrees);
            referenceSettings.ProtectedComponents.UnionWith(objects.ProtectedComponents);
            var shaders = context.GetState<OptimizationAnalysis>().Shaders;
            var records = facts.References;
            var references = AvatarDependencyGraph.Create(avatar, animations, referenceSettings, false, shaders, records);
            var proof = new StaticTransformProof(avatar, animations, objects, records, context.GetState<OptimizationPolicy>());
            int changes = StaticSkinningOptimization.Apply(avatar, context.GetState<MeshOptimizationState>(), objects,
                animations, references, proof, save, context.GetState<BlendShapeOptimization.OptimizationState>().Provenance);
            if (changes > 0) report.Messages.Add("Independently rebound " + changes + " static skin-bone entries; renderer batching eligibility was not required.");
            var result = OptimizationChanges.From(changes, AnalysisDomain.Meshes);
            int colliders = StaticColliderRoots.Apply(avatar, context.GetState<PhysBoneOptimizationState>(), objects,
                animations, references, proof, report.Messages);
            result += OptimizationChanges.From(colliders, AnalysisDomain.Physics);
            int constraints = StaticConstraintSources.Apply(avatar, objects, animations, references, proof, report.Messages);
            result += OptimizationChanges.From(constraints, AnalysisDomain.Hierarchy);
            changes += colliders + constraints;
            if (changes > 0) references = AvatarDependencyGraph.Create(avatar, animations, referenceSettings, false, shaders);
            int hoisted = ComponentHoisting.Apply(context, animations, references, proof,
                pathFor ?? (target => UnityEditor.AnimationUtility.CalculateTransformPath(target, avatar.transform)));
            if (hoisted > 0)
            {
                animations = AvatarAnimationIndex.Create(avatar, entries, paths, policy.Preserved);
                records = new SerializedReferenceSnapshot();
                references = AvatarDependencyGraph.Create(avatar, animations, referenceSettings, false, shaders, records);
                proof = new StaticTransformProof(avatar, animations, objects, records, context.GetState<OptimizationPolicy>());
                result += OptimizationChanges.From(hoisted, AnalysisDomain.Animation | AnalysisDomain.Hierarchy | AnalysisDomain.Physics | AnalysisDomain.Meshes);
            }
            int merged = StaticObjectMerging.Apply(avatar, objects, animations, references, proof, report.Messages);
            result += OptimizationChanges.From(merged, AnalysisDomain.Animation | AnalysisDomain.Hierarchy | AnalysisDomain.Physics | AnalysisDomain.Meshes);
            return result;
        }
    }
}
