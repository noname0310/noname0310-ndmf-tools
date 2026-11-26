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
            internal Func<OptimizationChanges> Run;
            internal Stage(string name, AnalysisDomain reads, AnalysisDomain writes, Func<int> run)
            { Name = name; Reads = reads; Writes = writes; Run = () => OptimizationChanges.From(run(), writes); }
            private Stage() { }
            internal static Stage WithChanges(string name, AnalysisDomain reads, AnalysisDomain writes, Func<OptimizationChanges> run)
                => new() { Name = name, Reads = reads, Writes = writes, Run = run };
        }

        internal static int Run(IReadOnlyList<Stage> stages, Action<AnalysisDomain> invalidate = null,
            Action<string, int> observe = null, int maximumRounds = 4096, OptimizationBuildReport report = null,
            Action<OptimizationChanges> invalidateChanges = null)
        {
            var dirty = new bool[stages.Count];
            for (int i = 0; i < dirty.Length; i++) dirty[i] = true;
            int rounds = 0;
            bool pending = stages.Count != 0;
            while (pending)
            {
                if (++rounds > maximumRounds)
                    throw new InvalidOperationException("Optimization did not converge. Pending stages: " +
                        string.Join(", ", stages.Where((_, i) => dirty[i]).Select(s => s.Name)));
                for (int i = 0; i < stages.Count; i++)
                {
                    if (!dirty[i]) continue;
                    dirty[i] = false;
                    var stage = stages[i]; OptimizationChanges changes;
                    using var measurement = report == null ? null : OptimizationMeasurement.Begin(report, "worklist", stage.Name);
                    try
                    {
                        changes = stage.Run();
                        if ((changes.Domains & ~stage.Writes) != 0)
                        {
                            invalidate?.Invoke(changes.Domains);
                            invalidateChanges?.Invoke(new OptimizationChanges(0, changes.Domains));
                            throw new InvalidOperationException("Undeclared mutation from " + stage.Name + ": " + changes.Domains);
                        }
                        measurement?.Complete();
                    }
                    catch { invalidate?.Invoke(stage.Writes); invalidateChanges?.Invoke(new OptimizationChanges(0, stage.Writes)); throw; }
                    observe?.Invoke(stage.Name, changes.Count);
                    if (changes.Domains == AnalysisDomain.None) continue;
                    invalidate?.Invoke(changes.Domains);
                    invalidateChanges?.Invoke(changes);
                    for (int next = 0; next < stages.Count; next++)
                        if ((stages[next].Reads & changes.Domains) != 0) dirty[next] = true;
                }
                pending = false;
                for (int i = 0; i < dirty.Length; i++) pending |= dirty[i];
            }
            return rounds;
        }
    }
}
