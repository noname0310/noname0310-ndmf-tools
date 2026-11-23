using System;
using System.Collections.Generic;

namespace Noname.AvatarTools.Analysis
{
    // Build-local immutable facts. Eviction repeats a computation; it cannot
    // change the analysis result or consume additional native compiler budget.
    internal sealed class BoundedAnalysisCache<TKey, TValue>
    {
        private readonly struct Entry
        {
            internal readonly TKey Key;
            internal readonly TValue Value;
            internal readonly int Cost;
            internal Entry(TKey key, TValue value, int cost) { Key = key; Value = value; Cost = cost; }
        }
        private readonly int maximumCount, maximumCost;
        private readonly Dictionary<TKey, LinkedListNode<Entry>> entries;
        private readonly LinkedList<Entry> recent = new();
        private long cost;
        internal int Count => entries.Count;
        internal long Cost => cost;
        internal BoundedAnalysisCache(int maximumCount, int maximumCost, IEqualityComparer<TKey> comparer = null)
        {
            if (maximumCount < 1 || maximumCost < 1) throw new ArgumentOutOfRangeException();
            this.maximumCount = maximumCount; this.maximumCost = maximumCost;
            entries = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
        }
        internal bool TryGetValue(TKey key, out TValue value)
        {
            if (!entries.TryGetValue(key, out var node)) { value = default; return false; }
            if (node != recent.Last) { recent.Remove(node); recent.AddLast(node); }
            value = node.Value.Value; return true;
        }
        internal void Add(TKey key, TValue value, int itemCost)
        {
            if (itemCost < 0) throw new ArgumentOutOfRangeException(nameof(itemCost));
            if (entries.TryGetValue(key, out var previous)) Remove(previous);
            if (itemCost > maximumCost) return;
            while (entries.Count >= maximumCount || cost + itemCost > maximumCost) Remove(recent.First);
            var node = recent.AddLast(new Entry(key, value, itemCost)); entries.Add(key, node); cost += itemCost;
        }
        private void Remove(LinkedListNode<Entry> node)
        { entries.Remove(node.Value.Key); cost -= node.Value.Cost; recent.Remove(node); }
        internal void Clear() { entries.Clear(); recent.Clear(); cost = 0; }
    }
}
