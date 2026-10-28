using nadena.dev.ndmf;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.ObjectOptimization;

namespace Noname.AvatarTools.Build
{
    internal static class OptimizationFixedPoint
    {
        // Only run after hierarchy generators have finished. Each reported change
        // removes graph data, a runtime component or an object. Default-state
        // normalization also removes control fields and cannot repeat indefinitely.
        // Do not repeat generators, atlas packing, sampling or mesh reduction.
        internal static int Apply(BuildContext context)
        {
            int iterations = 0, total = 0, changes;
            do
            {
                changes = 0;
                iterations++;
                OptimizationSession.Step(context, "object dependencies, iteration " + iterations,
                    AnalysisDomain.Animation | AnalysisDomain.Hierarchy | AnalysisDomain.Physics,
                    current => changes += ObjectOptimizationPass.ApplyFinalAndCountChanges(current));
                OptimizationSession.Step(context, "Animator outputs, iteration " + iterations,
                    AnalysisDomain.Animation | AnalysisDomain.Materials,
                    current => changes += AnimatorOptimizationPass.ApplyFinalAndCountChanges(current));
                total += changes;
            } while (changes != 0);

            context.GetState<OptimizationBuildReport>().Messages.Add("Object and Animator dependency analysis converged after " +
                iterations + " iterations (including the final unchanged check), with " + total + " graph/hierarchy removals.");
            return iterations;
        }
    }
}
