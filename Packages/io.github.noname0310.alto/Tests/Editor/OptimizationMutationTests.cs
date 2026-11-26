using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Build;
using NUnit.Framework;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void MutationWorklistInvalidatesActualWritesAndPreservesUnrelatedFacts()
        {
            int physics = 0, materials = 0, mutations = 0;
            var invalidated = new List<AnalysisDomain>();
            var stages = new[] {
                new OptimizationWorklist.Stage("physics reader", AnalysisDomain.Physics, AnalysisDomain.None, () => { physics++; return 0; }),
                new OptimizationWorklist.Stage("material reader", AnalysisDomain.Materials, AnalysisDomain.None, () => { materials++; return 0; }),
                OptimizationWorklist.Stage.WithChanges("material rewrite", AnalysisDomain.Materials,
                    AnalysisDomain.Materials | AnalysisDomain.Animation | AnalysisDomain.Physics,
                    () => ++mutations == 1 ? OptimizationChanges.From(1, AnalysisDomain.Materials) : default)
            };
            Assert.That(OptimizationWorklist.Run(stages, invalidated.Add), Is.EqualTo(2));
            Assert.That(physics, Is.EqualTo(1));
            Assert.That(materials, Is.EqualTo(2));
            Assert.That(invalidated, Is.EqualTo(new[] { AnalysisDomain.Materials }));
        }

        [TestCase(false)] [TestCase(true)]
        public void MutationWorklistFailureInvalidatesEveryPossibleWrite(bool undeclared)
        {
            var invalidated = new List<AnalysisDomain>();
            var stage = OptimizationWorklist.Stage.WithChanges("failing rewrite", AnalysisDomain.All, AnalysisDomain.Physics,
                () => undeclared ? new OptimizationChanges(1, AnalysisDomain.Animation) : throw new InvalidOperationException("Partial mutation"));
            Assert.Throws<InvalidOperationException>(() => OptimizationWorklist.Run(new[] { stage }, invalidated.Add));
            Assert.That(invalidated.Aggregate(AnalysisDomain.None, (a, b) => a | b),
                Is.EqualTo(undeclared ? AnalysisDomain.Animation | AnalysisDomain.Physics : AnalysisDomain.Physics));
        }

        [Test]
        public void MutationWorklistMatchesExhaustiveFixedPointsForCyclicDependencies()
        {
            var random = new Random(7619);
            for (int trial = 0; trial < 200; trial++)
            {
                const int domains = 7, count = 20;
                var reads = new int[count]; var writes = new int[count];
                for (int i = 0; i < count; i++) { reads[i] = random.Next(1 << domains); writes[i] = random.Next(1, 1 << domains); }
                // Each rule consumes a conjunction of facts and adds a subset of
                // its declared outputs. Cycles, self-dependencies and late seeds
                // are deliberately included; termination follows from finite bits.
                reads[count - 1] = 0;
                int expected = 0;
                bool changed;
                do
                {
                    int previous = expected;
                    for (int i = 0; i < count; i++) if ((expected & reads[i]) == reads[i]) expected |= writes[i];
                    changed = previous != expected;
                } while (changed);
                int actual = 0;
                var stages = new List<OptimizationWorklist.Stage>();
                for (int i = 0; i < count; i++)
                {
                    int rule = i;
                    stages.Add(OptimizationWorklist.Stage.WithChanges("rule " + i, (AnalysisDomain)reads[i], (AnalysisDomain)writes[i], () =>
                    {
                        if ((actual & reads[rule]) != reads[rule]) return default;
                        int added = writes[rule] & ~actual;
                        actual |= added;
                        return OptimizationChanges.From(added == 0 ? 0 : 1, (AnalysisDomain)added);
                    }));
                }
                OptimizationWorklist.Run(stages);
                Assert.That(actual, Is.EqualTo(expected), "Dependency graph " + trial);
            }
        }
    }
}
