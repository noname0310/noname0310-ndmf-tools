using System.Linq;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void PreprocessorLookupCacheInvalidatesWritesAndIsolatesForks()
        {
            var original = new PpEnvironment(new PpBudget());
            Assert.That(original.ContainsKey("X"), Is.False);
            original["X"] = MacroValue(1); Assert.That(original.TryGetValue("X", out var first), Is.True);
            var fork = original.Fork(); Assert.That(fork.TryGetValue("X", out var shared), Is.True);
            Assert.That(shared, Is.SameAs(first)); Assert.That(fork.ContainsKey("Y"), Is.False);
            original["X"] = MacroValue(2); original["Y"] = MacroValue(3);
            Assert.That(fork.TryGetValue("X", out var unchanged), Is.True); Assert.That(unchanged, Is.SameAs(first));
            Assert.That(fork.ContainsKey("Y"), Is.False);
            fork["Y"] = MacroValue(4); Assert.That(fork.ContainsKey("Y"), Is.True);
        }

        [Test]
        public void PreprocessorInlineHideSetsKeepSharedTokensImmutable()
        {
            var a = default(PpHideSet).With("A"); var b = a.With("B"); var c = b.With("C");
            Assert.That(a.Contains("B"), Is.False); Assert.That(b.Contains("C"), Is.False);
            var intersection = c.Intersect(default(PpHideSet).With("A").With("C"));
            Assert.That(intersection.Contains("A"), Is.True); Assert.That(intersection.Contains("C"), Is.True);
            Assert.That(intersection.Contains("B"), Is.False);
            var union = intersection.Union(b); Assert.That(union.Contains("B"), Is.True);
            Assert.That(intersection.Contains("B"), Is.False);
            Assert.That(default(PpHideSet).Intersect(c).Contains("A"), Is.False);
        }

        private static PpBinding MacroValue(int value) => new(new[] { new PpMacro("X", PpLexer.Lex(value.ToString()).ToArray()) });

        [Test]
        public void PreprocessorEnvironmentSnapshotsIsolateSiblingAndParentWrites()
        {
            var original = new PpEnvironment(new PpBudget()); var initial = MacroValue(0); original["X"] = initial;
            var a = original.Fork(); var b = original.Fork(); a["X"] = MacroValue(1); b["Y"] = MacroValue(2);
            original["Z"] = MacroValue(3);
            Assert.That(b.TryGetValue("X", out var old), Is.True); Assert.That(old, Is.SameAs(initial));
            Assert.That(original.ContainsKey("Y"), Is.False); Assert.That(a.ContainsKey("Y"), Is.False);
            Assert.That(a.ContainsKey("Z"), Is.False); Assert.That(b.ContainsKey("Z"), Is.False);
        }

        [Test]
        public void PreprocessorEnvironmentJoinsOnlyChangedBindings()
        {
            var original = new PpEnvironment(new PpBudget());
            for (int i = 0; i < 4000; i++) original["K" + i] = MacroValue(i);
            var a = original.Fork(); var b = original.Fork(); a["K7"] = MacroValue(8); b["NEW"] = MacroValue(4);
            int visited = 0;
            var joined = PpEnvironment.Join(a, b, name =>
            { visited++; return a.TryGetValue(name, out var av) ? av : b.TryGetValue(name, out var bv) ? bv : PpBinding.Undefined; });
            Assert.That(visited, Is.EqualTo(2)); Assert.That(joined.Keys.Count(), Is.EqualTo(4001));
        }

        [Test]
        public void PreprocessorEnvironmentFlatteningRetainsSnapshotsAndJoinCoverage()
        {
            var a = new PpEnvironment(new PpBudget()); var old = a.Fork();
            for (int i = 0; i < 80; i++) { a = a.Fork(); a["K" + i] = MacroValue(i); }
            Assert.That(old.Keys, Is.Empty); Assert.That(a.Keys.Count(), Is.EqualTo(80));
            var b = old.Fork(); b["OTHER"] = MacroValue(2);
            var joined = PpEnvironment.Join(a, b, name => a.TryGetValue(name, out var av) ? av : b.TryGetValue(name, out var bv) ? bv : PpBinding.Undefined);
            Assert.That(joined.Keys.Count(), Is.EqualTo(81));
        }
    }
}
