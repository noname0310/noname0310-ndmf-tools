using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Noname.AvatarTools.Analysis
{
    // One capacity for all persistent shader-analysis artifacts. The estimate is
    // conservative: overwrites may overcount, but a filesystem scan resolves
    // that before eviction. Reads never enumerate directories. There is no file
    // count limit that could evict tiny facts while most of the byte budget is free.
    internal sealed class AnalysisCacheDiskBudget
    {
        internal const long MaximumBytes = 100L * 1024 * 1024 * 1024;
        internal static readonly AnalysisCacheDiskBudget Shared = new(new[] {
            Path.GetFullPath("Library/ALTO/ShaderAnalysis"), Path.GetFullPath("Library/ALTO/AnalysisFacts") });
        private readonly string[] directories;
        private readonly long maximumBytes;
        private readonly object gate = new();
        private readonly Dictionary<string, DateTime> touched = new(StringComparer.Ordinal);
        private long upperBound;
        private bool measured;

        internal AnalysisCacheDiskBudget(string[] directories, long maximumBytes = MaximumBytes)
        {
            if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            this.directories = directories.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
            this.maximumBytes = maximumBytes;
        }

        internal void Stored(long bytes, long replacedBytes = 0)
        {
            lock (gate)
            {
                upperBound = Math.Max(0, upperBound - replacedBytes);
                upperBound = bytes > long.MaxValue - upperBound ? long.MaxValue : upperBound + bytes;
                if (!measured || upperBound > maximumBytes) MeasureAndTrim();
            }
        }

        internal void Used(string path)
        {
            DateTime now = DateTime.UtcNow;
            lock (gate)
            {
                if (touched.TryGetValue(path, out var previous) && now - previous < TimeSpan.FromMinutes(5)) return;
                // A bounded touch memo amortizes metadata writes even for facts
                // retained in the memory cache. It is not part of cache validity.
                if (touched.Count >= 65536) touched.Clear();
                touched[path] = now;
                try { File.SetLastWriteTimeUtc(path, now); }
                catch (Exception e) when (StorageFailure(e)) { }
            }
        }

        internal void InvalidateSize() { lock (gate) measured = false; }

        private void MeasureAndTrim()
        {
            var files = new List<FileInfo>();
            long total = 0;
            foreach (string directory in directories)
            {
                if (!Directory.Exists(directory)) continue;
                foreach (var file in new DirectoryInfo(directory).GetFiles("*.bin"))
                {
                    try { total += file.Length; files.Add(file); }
                    catch (Exception e) when (StorageFailure(e)) { }
                }
            }
            if (total > maximumBytes)
            {
                long target = maximumBytes - maximumBytes / 20;
                files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                foreach (var file in files)
                {
                    if (total <= target) break;
                    try { long size = file.Length; file.Delete(); total -= size; touched.Remove(file.FullName); }
                    catch (Exception e) when (StorageFailure(e)) { }
                }
            }
            upperBound = total; measured = true;
        }

        private static bool StorageFailure(Exception e) => e is IOException || e is UnauthorizedAccessException ||
            e is System.Security.SecurityException || e is NotSupportedException || e is ArgumentException;
    }
}
