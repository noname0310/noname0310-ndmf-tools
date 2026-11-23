using System;
using System.Collections.Generic;
using System.Linq;

namespace Noname.AvatarTools.Analysis
{
    // Copy-on-write snapshots share unchanged macro definitions. Joining sibling
    // branches visits their changes instead of copying/scanning the whole table.
    internal sealed class PpEnvironment
    {
        private sealed class Frame
        {
            internal readonly Frame Parent;
            internal readonly int Depth;
            internal readonly Dictionary<string, PpBinding> Values = new(StringComparer.Ordinal);
            internal bool Frozen;
            internal Frame(Frame parent = null) { Parent = parent; Depth = parent == null ? 0 : parent.Depth + 1; }
        }
        private Frame root;
        private readonly PpBudget budget;
        private readonly Dictionary<string, (bool Found, PpBinding Value)> lookups = new(StringComparer.Ordinal);
        internal PpEnvironment(PpBudget budget) { root = new Frame(); this.budget = budget; }
        private PpEnvironment(Frame root, PpBudget budget) { this.root = root; this.budget = budget; root.Frozen = true; }
        internal PpEnvironment Fork() => new(root, budget);
        internal PpBinding this[string name]
        {
            set
            {
                if (root.Frozen)
                {
                    if (root.Depth >= 24)
                    {
                        var flattened = new Frame();
                        foreach (var pair in Entries()) { budget.Charge(); flattened.Values.Add(pair.Key, pair.Value); }
                        root = flattened;
                    }
                    else root = new Frame(root);
                }
                root.Values[name] = value;
                if (lookups.ContainsKey(name)) lookups[name] = (true, value);
            }
        }
        internal bool TryGetValue(string name, out PpBinding value)
        {
            if (lookups.TryGetValue(name, out var cached)) { value = cached.Value; return cached.Found; }
            for (var frame = root; frame != null; frame = frame.Parent)
                if (frame.Values.TryGetValue(name, out value)) { lookups.Add(name, (true, value)); return true; }
            lookups.Add(name, (false, null));
            value = null; return false;
        }
        internal bool ContainsKey(string name) => TryGetValue(name, out _);
        internal IEnumerable<string> Keys => Entries().Select(p => p.Key);
        internal IEnumerable<PpBinding> Values => Entries().Select(p => p.Value);
        private IEnumerable<KeyValuePair<string, PpBinding>> Entries()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var frame = root; frame != null; frame = frame.Parent)
                foreach (var pair in frame.Values)
                    if (seen.Add(pair.Key)) yield return pair;
        }

        internal static PpEnvironment Join(PpEnvironment a, PpEnvironment b, Func<string, PpBinding> merge)
        {
            var left = a.root; var right = b.root;
            while (left != null && right != null && !ReferenceEquals(left, right))
            {
                if (left.Depth >= right.Depth) left = left.Parent;
                else right = right.Parent;
            }
            Frame common = ReferenceEquals(left, right) ? left : null;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            void Changes(Frame frame)
            {
                for (; frame != common && frame != null; frame = frame.Parent)
                    foreach (string key in frame.Values.Keys) { a.budget.Charge(); keys.Add(key); }
            }
            Changes(a.root); Changes(b.root);
            var result = common == null ? new PpEnvironment(a.budget) : new PpEnvironment(common, a.budget);
            foreach (string key in keys)
            {
                a.budget.Charge(); var value = merge(key);
                if (!result.TryGetValue(key, out var previous) || !ReferenceEquals(previous, value)) result[key] = value;
            }
            return result;
        }
    }
}
