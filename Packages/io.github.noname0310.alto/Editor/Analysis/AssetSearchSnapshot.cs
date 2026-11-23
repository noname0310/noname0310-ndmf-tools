using System.Collections.Generic;
using UnityEditor;

namespace Noname.AvatarTools.Analysis
{
    // Cache the database search, not asset contents. Dirty in-memory values are
    // still read by callers on every query. Both artifact versions participate:
    // creation/deletion and newly imported artifacts must invalidate the result.
    internal sealed class AssetSearchSnapshot
    {
        private readonly string filter;
        private string[] guids;
        private uint dependencies, processed;

        internal AssetSearchSnapshot(string filter) { this.filter = filter; }

        internal IReadOnlyList<string> Guids
        {
            get
            {
                uint currentDependencies = AssetDatabase.GlobalArtifactDependencyVersion;
                uint currentProcessed = AssetDatabase.GlobalArtifactProcessedVersion;
                if (guids == null || dependencies != currentDependencies || processed != currentProcessed)
                {
                    guids = AssetDatabase.FindAssets(filter);
                    dependencies = currentDependencies;
                    processed = currentProcessed;
                }
                return guids;
            }
        }
    }
}
