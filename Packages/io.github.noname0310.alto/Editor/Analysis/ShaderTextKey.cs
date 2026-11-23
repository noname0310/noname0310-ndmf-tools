using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Noname.AvatarTools.Analysis
{
    // Dictionary lookups must not hash entire HLSL programs on every cache hit.
    // Weak registration shares identical source identities without permanent
    // string interning. Content equality remains authoritative after eviction
    // and for colliding hashes; the registry never owns a source's lifetime.
    internal readonly struct ShaderTextKey : IEquatable<ShaderTextKey>
    {
        private sealed class Identity
        {
            internal readonly string Text;
            internal readonly int Hash;
            internal Identity(string text, int hash) { Text = text; Hash = hash; }
        }
        private static readonly ConditionalWeakTable<string, Identity> Identities = new();
        private static readonly object Gate = new();
        private static readonly BoundedAnalysisCache<int, List<WeakReference<Identity>>> Buckets = new(4096, 4096);
        private readonly Identity identity;
        internal string Text => identity?.Text;
        internal ShaderTextKey(string text) { identity = text == null ? null : Identities.GetValue(text, Register); }

        private static Identity Register(string text)
        {
            int hash = StringComparer.Ordinal.GetHashCode(text);
            lock (Gate)
            {
                if (!Buckets.TryGetValue(hash, out var bucket))
                { bucket = new List<WeakReference<Identity>>(); Buckets.Add(hash, bucket, 1); }
                for (int i = bucket.Count - 1; i >= 0; i--)
                {
                    if (!bucket[i].TryGetTarget(out var previous)) bucket.RemoveAt(i);
                    else if (string.Equals(text, previous.Text, StringComparison.Ordinal)) return previous;
                }
                var value = new Identity(text, hash);
                // Keep registration bounded even for adversarial hash collisions.
                if (bucket.Count < 64) bucket.Add(new WeakReference<Identity>(value));
                return value;
            }
        }

        public bool Equals(ShaderTextKey other) => ReferenceEquals(identity, other.identity) ||
            GetHashCode() == other.GetHashCode() && string.Equals(Text, other.Text, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is ShaderTextKey other && Equals(other);
        public override int GetHashCode() => identity?.Hash ?? 0;
        public static implicit operator ShaderTextKey(string text) => new(text);
    }
}
