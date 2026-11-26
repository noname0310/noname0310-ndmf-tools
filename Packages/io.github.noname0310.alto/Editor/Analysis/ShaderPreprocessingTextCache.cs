using System;
using System.Collections.Generic;

namespace Noname.AvatarTools.Analysis
{
    // A certificate for a text instruction whose every expansion chunk was
    // rejected by the conservative input projection. The instruction is
    // immutable. Reuse requires the same demand and source-location mapping,
    // and identical macro alternatives for every observed read. Thus chunk
    // boundaries and projection decisions repeat by induction over that trace.
    // No expansion, emitted token, counter, macro write or dialect effect is
    // skipped. Replay the logical work charge as well; near a limit, execute
    // normally so failure and partial observations retain their original order.
    internal sealed class PpTextProjectionCache
    {
        private sealed class Certificate
        {
            internal string Demand, ProjectionRoot;
            internal PpLocations Locations;
            internal int Work;
            internal KeyValuePair<string, PpBinding>[] Reads;
        }

        // Each immutable instruction owns a small bounded history. This shares
        // source-header facts across pass contexts without retaining an avatar,
        // material or an entire interpreter environment.
        private readonly List<Certificate> certificates = new(4);
        internal bool TrySkip(string demand, PpState state, string defaultFile = null)
        {
            foreach (var certificate in certificates)
            {
                if (certificate.Demand != demand || !certificate.Locations.Same(state.Locations) ||
                    certificate.ProjectionRoot != state.Locations.ProjectionRoot(defaultFile) ||
                    state.Budget.Remaining < certificate.Work) continue;
                bool matches = true;
                foreach (var read in certificate.Reads)
                    if (!SameProjectionValue(read.Value, state.Peek(read.Key))) { matches = false; break; }
                if (!matches) continue;
                if (state.ObserveMacroRead != null)
                    foreach (var read in certificate.Reads) state.ObserveMacroRead(read.Key, state.Peek(read.Key));
                state.Budget.Charge(certificate.Work);
                state.Budget.TextProjectionHits++;
                return true;
            }
            return false;
        }

        private static bool SameProjectionValue(PpBinding left, PpBinding right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (right == null || left.Presence != right.Presence || left.Alternatives.Length != right.Alternatives.Length) return false;
            // Projection observes macro spelling, not dependency bit ordinals.
            // Macro identity by reference retains tokens, locations, spacing,
            // parameters and all other expansion metadata exactly.
            for (int i = 0; i < left.Alternatives.Length; i++)
                if (!ReferenceEquals(left.Alternatives[i], right.Alternatives[i])) return false;
            return true;
        }

        internal void Add(string demand, PpLocations locations, Dictionary<string, PpBinding> reads, int work, string defaultFile = null)
        {
            if (reads.Count > 1024) return;
            var captured = new KeyValuePair<string, PpBinding>[reads.Count];
            int index = 0;
            foreach (var read in reads) captured[index++] = read;
            if (certificates.Count == 4) certificates.RemoveAt(0);
            certificates.Add(new Certificate { Demand = demand, Locations = locations, ProjectionRoot = locations.ProjectionRoot(defaultFile),
                Reads = captured, Work = work });
        }
    }
}
