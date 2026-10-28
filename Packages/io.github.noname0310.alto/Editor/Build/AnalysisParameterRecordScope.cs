using System;
using System.IO;
using System.Linq;
using UnityEngine;
using VRC.Core;

namespace Noname.AvatarTools.Build
{
    // MA 1.18 writes its parameter-sequence record during preprocessing, including manual builds.
    // Let analysis read the real baseline, then restore it so analysis cannot become a PC baseline.
    // VRCFury only records while VRC_SdkBuilder.ActiveBuildType != None, which analysis rejects.
    internal sealed class AnalysisParameterRecordScope : IDisposable
    {
        private readonly string path;
        private readonly byte[] original;
        private readonly DateTime timestamp;

        private AnalysisParameterRecordScope(string path)
        {
            this.path = path;
            if (path == null || !File.Exists(path)) return;
            original = File.ReadAllBytes(path);
            timestamp = File.GetLastWriteTimeUtc(path);
        }

        internal static AnalysisParameterRecordScope Capture(GameObject avatar)
        {
            var id = avatar.GetComponent<PipelineManager>()?.blueprintId;
            bool valid = id != null && id.StartsWith("avtr_", StringComparison.Ordinal) && Guid.TryParseExact(id.Substring(5), "D", out _);
            return new AnalysisParameterRecordScope(valid
                ? Path.Combine("Library", "nadena.dev.modular-avatar", "AvatarParameterInfo", id) : null);
        }

        public void Dispose()
        {
            if (path == null) return;
            if (original == null)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(original))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, original);
            }
            if (original != null) File.SetLastWriteTimeUtc(path, timestamp);
        }
    }
}
