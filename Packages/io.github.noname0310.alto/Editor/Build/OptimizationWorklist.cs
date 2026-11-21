using System;
using System.Collections.Generic;
using System.Linq;

namespace Noname.AvatarTools.Build
{
    // A build-local worklist for convergent simplifications. Generators, atlas
    // packing, occlusion sampling and mesh reduction never enter this scheduler.
    // A positive result must represent an actual, monotone rewrite, not an attempt.
    internal sealed class OptimizationWorklist
    {
        internal sealed class Stage
        {
            internal string Name;
            internal AnalysisDomain Reads, Writes;
            internal Func<int> Run;
            internal Stage(string name, AnalysisDomain reads, AnalysisDomain writes, Func<int> run)
            { Name = name; Reads = reads; Writes = writes; Run = run; }
        }

        internal static int Run(IReadOnlyList<Stage> stages, Action<AnalysisDomain> invalidate = null,
            Action<string, int> observe = null, int maximumRounds = 4096, OptimizationBuildReport report = null)
        {
            var dirty = Enumerable.Repeat(true, stages.Count).ToArray();
            int rounds = 0;
            while (dirty.Any(v => v))
            {
                if (++rounds > maximumRounds)
                    throw new InvalidOperationException("Optimization did not converge. Pending stages: " +
                        string.Join(", ", stages.Where((_, i) => dirty[i]).Select(s => s.Name)));
                for (int i = 0; i < stages.Count; i++)
                {
                    if (!dirty[i]) continue;
                    dirty[i] = false;
                    var stage = stages[i]; int changes;
                    using var measurement = report == null ? null : OptimizationMeasurement.Begin(report, "worklist", stage.Name);
                    try { changes = stage.Run(); measurement?.Complete(); }
                    catch { invalidate?.Invoke(stage.Writes); throw; }
                    if (changes < 0) throw new InvalidOperationException("Negative progress from " + stage.Name);
                    observe?.Invoke(stage.Name, changes);
                    if (changes == 0) continue;
                    invalidate?.Invoke(stage.Writes);
                    for (int next = 0; next < stages.Count; next++)
                        if ((stages[next].Reads & stage.Writes) != 0) dirty[next] = true;
                }
            }
            return rounds;
        }
    }
}
