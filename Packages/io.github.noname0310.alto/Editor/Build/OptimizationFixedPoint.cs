using nadena.dev.ndmf;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.Physics;
using System.Collections.Generic;

namespace Noname.AvatarTools.Build
{
    internal static class OptimizationFixedPoint
    {
        // Only run after hierarchy generators have finished. Each reported change
        // removes graph data, a runtime component or an object. Default-state
        // normalization also removes control fields and cannot repeat indefinitely.
        // Do not repeat generators, atlas packing, sampling or mesh reduction.
        internal static int Apply(BuildContext context, bool includeMeshes = false)
        {
            int total = 0;
            const AnalysisDomain geometryInputs = AnalysisDomain.Animation | AnalysisDomain.Hierarchy | AnalysisDomain.Meshes |
                AnalysisDomain.Physics | AnalysisDomain.ExternalInputs;
            var stages = new List<OptimizationWorklist.Stage>
            {
                new("physics data", geometryInputs, AnalysisDomain.Physics | AnalysisDomain.Hierarchy,
                    () => PhysBoneOptimizationPass.ApplyAndCountChanges(context)),
                new("static frames and consumers", geometryInputs, AnalysisDomain.Physics | AnalysisDomain.Meshes | AnalysisDomain.Hierarchy | AnalysisDomain.Animation,
                    () => StaticTransformOptimizationPass.Apply(context)),
                new("object dependencies", AnalysisDomain.All, AnalysisDomain.Animation | AnalysisDomain.Hierarchy | AnalysisDomain.Physics | AnalysisDomain.ExternalInputs,
                    () => ObjectOptimizationPass.ApplyFinalAndCountChanges(context)),
                new("Animator outputs", AnalysisDomain.All, AnalysisDomain.Animation | AnalysisDomain.Materials,
                    () => AnimatorOptimizationPass.ApplyFinalAndCountChanges(context))
            };
            if (includeMeshes) stages.Add(new("mesh assembly", AnalysisDomain.All,
                AnalysisDomain.Meshes | AnalysisDomain.Hierarchy | AnalysisDomain.Animation, () => MeshOptimizationPass.ApplyAndCountChanges(context)));
            int iterations = OptimizationWorklist.Run(stages, context.GetState<OptimizationAnalysis>().Invalidate, (_, changes) => total += changes,
                report: context.GetState<OptimizationBuildReport>());
            context.GetState<OptimizationBuildReport>().Messages.Add("Optimization dependency worklist converged after " +
                iterations + " iterations (including the final unchanged check), with " + total + " simplifications.");
            return iterations;
        }
    }
}
