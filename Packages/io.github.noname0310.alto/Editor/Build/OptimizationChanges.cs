using System;
using System.Collections.Generic;
using System.Linq;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Build
{
    // Progress and affected facts are distinct. A nested pass reports what it
    // actually changed; its stage declaration remains an upper bound for failures.
    internal readonly struct OptimizationChanges
    {
        internal readonly int Count;
        internal readonly AnalysisDomain Domains;
        // null means arbitrary reference owners may have changed. An explicit
        // empty set is a proof that only scalar/virtual data changed.
        internal readonly IReadOnlyCollection<Object> ReferenceOwners;
        internal OptimizationChanges(int count, AnalysisDomain domains, IEnumerable<Object> referenceOwners = null)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (count > 0 && domains == AnalysisDomain.None)
                throw new ArgumentException("A rewrite must identify the facts it changes.", nameof(domains));
            Count = count; Domains = domains;
            ReferenceOwners = referenceOwners == null ? null : referenceOwners.Distinct().ToArray();
        }
        internal static OptimizationChanges From(int count, AnalysisDomain domains)
            => new(count, count == 0 ? AnalysisDomain.None : domains);
        public static OptimizationChanges operator +(OptimizationChanges left, OptimizationChanges right)
            => new(checked(left.Count + right.Count), left.Domains | right.Domains,
                left.Domains == AnalysisDomain.None ? right.ReferenceOwners :
                right.Domains == AnalysisDomain.None ? left.ReferenceOwners :
                left.ReferenceOwners == null || right.ReferenceOwners == null ? null : left.ReferenceOwners.Concat(right.ReferenceOwners));
    }
}
