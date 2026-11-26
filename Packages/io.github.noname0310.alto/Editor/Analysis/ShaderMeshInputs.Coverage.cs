using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    internal sealed partial class ShaderMeshInputs
    {
        private const string CoverageKind = "complete shader input coverage";
        // Differential validation can exercise the lower-level artifact caches.
        internal static bool CacheCompleteCoverage = true;
        internal static bool CacheCompletedUnits = true;
        internal int CoverageCacheHits { get; private set; }
        private const string UnitCoverageKind = "completed shader unit coverage";

        // This certificate includes the completed keyword fixed point, not just
        // one compiled representative. Revalidate source/import/include/settings
        // identities and the caller's freshly proved constant set before reuse.
        // Avatar objects, incomplete proofs and failures never enter this cache.
        private string CoverageKey(Shader shader, BuildTarget target, ShaderInputQuery query, string inputs)
        {
            if (!ContentFactCache.Enabled || !CacheCompleteCoverage) return null;
            using var cost = AnalysisCost.Measure("Shader coverage cache/key validation");
            try
            {
                string identity = sources.NativeBuildIdentity(shader, target);
                return identity == null ? null : ContentFactCache.Shared.Key(CoverageKind, identity, query.Signature, inputs);
            }
            catch (Exception) { return null; }
        }

        private bool HasCoverage(string key)
        {
            using var cost = AnalysisCost.Measure("Shader coverage cache/lookup");
            if (!ContentFactCache.Shared.TryRead(CoverageKind, key, reader => reader.ReadBoolean(), out bool complete) || !complete) return false;
            CoverageCacheHits++;
            return true;
        }

        private static void StoreCoverage(string key) => ContentFactCache.Shared.Store(CoverageKind, key, writer => writer.Write(true));

        // One complete pass/stage/tier keyword proof remains useful when a later
        // unit is live or unsupported. Only its actually observed constant
        // premises belong in the certificate; the exact source/environment and
        // keyword universe belong in the key. Never persist a partial proof.
        private string UnitCoverageKey(InspectionUnit unit, BuildTarget target, ShaderInputQuery query, string keywords)
        {
            if (!CacheCompletedUnits || !ContentFactCache.Enabled ||
                !preprocessingIdentities.TryGetValue(unit.Pass, out string identity) || identity == null) return null;
            return ContentFactCache.Shared.Key(UnitCoverageKind, identity, unit.Stage.ToString(), unit.Backend.ToString(),
                unit.Tier.ToString(), target.ToString(), query.Signature, keywords);
        }

        private static List<Dictionary<string, int>> ReadUnitCoverage(BinaryReader reader)
        {
            int count = ContentFactCache.Count(reader, 32);
            var certificates = new List<Dictionary<string, int>>(count);
            for (int i = 0; i < count; i++)
            {
                int names = ContentFactCache.Count(reader, 32768);
                var certificate = new Dictionary<string, int>(names, StringComparer.Ordinal);
                for (int j = 0; j < names; j++) certificate.Add(reader.ReadString(), reader.ReadInt32());
                certificates.Add(certificate);
            }
            return certificates;
        }

        private static void StoreUnitCoverage(string key, List<Dictionary<string, int>> certificates)
        {
            ContentFactCache.Shared.Store(UnitCoverageKind, key, writer =>
            {
                // Bound alternate value domains without limiting analysis. An
                // evicted domain is simply proved again on a later request.
                int start = Math.Max(0, certificates.Count - 32);
                writer.Write(certificates.Count - start);
                for (int i = start; i < certificates.Count; i++)
                {
                    writer.Write(certificates[i].Count);
                    foreach (var pair in certificates[i]) { writer.Write(pair.Key); writer.Write(pair.Value); }
                }
            });
        }
    }
}
