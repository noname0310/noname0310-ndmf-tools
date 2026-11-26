using System;
using System.Collections.Generic;
using System.Linq;

namespace Noname.AvatarTools.Analysis
{
    // Pass-local accounting of committed output proofs. A permission being
    // enabled, or an attempted proof being rejected, is not a consumption.
    internal sealed class AppliedProofReport
    {
        private static readonly NumericalOptimization[] individualFeatures = Enum.GetValues(typeof(NumericalOptimization))
            .Cast<NumericalOptimization>().Where(f => f != NumericalOptimization.None && f != NumericalOptimization.All).ToArray();
        private int strict, numerical, initialization;
        private readonly Dictionary<NumericalOptimization, int> features = new();
        internal bool HasApplications => strict + numerical != 0;

        internal void Record(NumericalOptimization consumed = NumericalOptimization.None, bool initializationChanged = false)
        {
            if (consumed == NumericalOptimization.None) strict++;
            else
            {
                numerical++;
                foreach (var feature in individualFeatures)
                    if ((consumed & feature) != 0)
                        features[feature] = features.TryGetValue(feature, out int count) ? count + 1 : 1;
            }
            if (initializationChanged) initialization++;
        }

        internal void AppendTo(ICollection<string> messages, string consumer)
        {
            if (HasApplications) messages.Add("Proof assumptions [" + consumer + "]: " + this + ".");
        }

        public override string ToString() => "strict proof=" + strict + "; numerical permission=" + numerical +
            (features.Count == 0 ? "" : " (" + string.Join(", ", features.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)) + ")") +
            "; initialization changes=" + initialization;
    }
}
