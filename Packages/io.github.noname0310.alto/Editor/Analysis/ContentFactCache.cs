using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Noname.AvatarTools.Analysis
{
    // Only content-derived, versioned facts belong here. Callers retain their cheap
    // snapshot-local lookup and must validate every external dependency on a hit.
    internal sealed class ContentFactCache
    {
        internal sealed class Statistics
        {
            public long Hits, Misses, Writes, Rejected;
            public double KeyMilliseconds, ReadMilliseconds, WriteMilliseconds;
        }

        internal static bool Enabled = true;
        internal static Action<string, string, string[]> KeyObserver;
        internal static readonly ContentFactCache Shared = new(Path.GetFullPath("Library/ALTO/AnalysisFacts"),
            typeof(UnityEditor.ShaderUtil).Module.ModuleVersionId + "/" + typeof(UnityEngine.Application).Module.ModuleVersionId,
            AnalysisCacheContracts.Revision, AnalysisCacheDiskBudget.Shared);
        private const int Format = 1, MaximumRecord = 64 * 1024 * 1024;
        private readonly string directory, revision;
        private readonly Func<string, string> contractRevision;
        private readonly AnalysisCacheDiskBudget diskBudget;
        private readonly object gate = new();
        private readonly BoundedAnalysisCache<string, byte[]> memory = new(1024, 32 * 1024 * 1024, StringComparer.Ordinal);
        private readonly Dictionary<string, Statistics> statistics = new(StringComparer.Ordinal);
        private sealed class DigestValue { internal string Value; }
        private static ConditionalWeakTable<string, DigestValue> textDigests = new();
        internal static long TextHashTicks, TextHashBytes, TextHashCalls;

        internal ContentFactCache(string directory, string revision, Func<string, string> contractRevision = null,
            AnalysisCacheDiskBudget diskBudget = null)
        {
            this.directory = Path.GetFullPath(directory); this.revision = revision; this.contractRevision = contractRevision;
            this.diskBudget = diskBudget ?? new AnalysisCacheDiskBudget(new[] { this.directory });
        }

        // Strings are immutable. A weak identity memo avoids re-reading a large
        // source for each query without keeping shaders or avatars alive.
        internal static string TextDigest(string text) => text == null ? "null" : textDigests.GetValue(text, value =>
        {
            long start = Stopwatch.GetTimestamp();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                if (ContentDigest.TryText(value, out var native))
                {
                    Interlocked.Add(ref TextHashBytes, (long)value.Length * 2); Interlocked.Increment(ref TextHashCalls);
                    return new DigestValue { Value = Hex(native) };
                }
                using var hash = SHA256.Create();
                for (int offset = 0; offset < value.Length;)
                {
                    int count = Math.Min(buffer.Length / 2, value.Length - offset);
                    for (int i = 0; i < count; i++)
                    { char c = value[offset + i]; buffer[i * 2] = (byte)c; buffer[i * 2 + 1] = (byte)(c >> 8); }
                    hash.TransformBlock(buffer, 0, count * 2, buffer, 0); offset += count;
                }
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                Interlocked.Add(ref TextHashBytes, (long)value.Length * 2);
                Interlocked.Increment(ref TextHashCalls);
                return new DigestValue { Value = Hex(hash.Hash) };
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); Interlocked.Add(ref TextHashTicks, Stopwatch.GetTimestamp() - start); }
        }).Value;

        internal string Key(string kind, params string[] parts)
        {
            if (!Enabled) return null;
            string contract = contractRevision?.Invoke(kind);
            if (contractRevision != null && contract == null) return null;
            long start = Stopwatch.GetTimestamp();
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Format); writer.Write(revision); writer.Write(kind); writer.Write(contract ?? ""); writer.Write(parts.Length);
                foreach (string part in parts) { writer.Write(part != null); if (part != null) writer.Write(part); }
            }
            string key = ShaderAnalysisCache.Digest(stream.ToArray());
            lock (gate) Stats(kind).KeyMilliseconds += Elapsed(start);
            KeyObserver?.Invoke(kind, key, parts);
            return key;
        }

        internal bool TryRead<T>(string kind, string key, Func<BinaryReader, T> read, out T result)
        {
            result = default;
            if (!Enabled || key == null) return false;
            long start = Stopwatch.GetTimestamp(); bool hit = false;
            try
            {
                byte[] payload;
                lock (gate)
                {
                    if (!memory.TryGetValue(key, out payload))
                    {
                        string path = PathFor(key);
                        var info = new FileInfo(path);
                        if (!info.Exists || info.Length < 104 || info.Length > MaximumRecord + 256) return false;
                        using var file = File.OpenRead(path);
                        using var reader = new BinaryReader(file, Encoding.UTF8);
                        if (reader.ReadInt32() != Format || reader.ReadString() != key) return false;
                        int length = reader.ReadInt32(); byte[] digest = reader.ReadBytes(32);
                        if (length < 0 || length > MaximumRecord || length != file.Length - file.Position) return false;
                        payload = reader.ReadBytes(length);
                        if (!ContentDigest.Bytes(payload).SequenceEqual(digest)) return false;
                        memory.Add(key, payload, payload.Length);
                    }
                }
                using var data = new MemoryStream(payload, false);
                using var body = new BinaryReader(data, Encoding.UTF8);
                result = read(body);
                if (data.Position != data.Length) { result = default; return false; }
                diskBudget.Used(PathFor(key));
                return hit = true;
            }
            catch (Exception e) when (StorageFailure(e)) { result = default; return false; }
            finally
            {
                lock (gate) { var s = Stats(kind); if (hit) s.Hits++; else s.Misses++; s.ReadMilliseconds += Elapsed(start); }
            }
        }

        internal void Store(string kind, string key, Action<BinaryWriter> write)
        {
            if (!Enabled || key == null) return;
            long start = Stopwatch.GetTimestamp(); string temporary = null;
            try
            {
                using var data = new MemoryStream();
                using (var body = new BinaryWriter(data, Encoding.UTF8, true)) write(body);
                if (data.Length > MaximumRecord) return;
                byte[] payload = data.ToArray();
                lock (gate)
                {
                    Directory.CreateDirectory(directory);
                    string path = PathFor(key); temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var file = File.Create(temporary))
                    using (var writer = new BinaryWriter(file, Encoding.UTF8))
                    { writer.Write(Format); writer.Write(key); writer.Write(payload.Length); writer.Write(ContentDigest.Bytes(payload)); writer.Write(payload); }
                    var previous = new FileInfo(path); long replacedBytes = previous.Exists ? previous.Length : 0;
                    if (previous.Exists) File.Replace(temporary, path, null); else File.Move(temporary, path);
                    temporary = null; memory.Add(key, payload, payload.Length); Stats(kind).Writes++;
                    diskBudget.Stored(new FileInfo(path).Length, replacedBytes);
                }
            }
            catch (Exception e) when (StorageFailure(e)) { }
            finally
            {
                if (temporary != null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                lock (gate) Stats(kind).WriteMilliseconds += Elapsed(start);
            }
        }

        internal void Reject(string kind) { lock (gate) Stats(kind).Rejected++; }
        internal bool Contains(string key)
        {
            if (!Enabled || key == null) return false;
            lock (gate)
            {
                if (memory.TryGetValue(key, out _)) return true;
                try { return File.Exists(PathFor(key)); } catch (Exception e) when (StorageFailure(e)) { return false; }
            }
        }
        internal Dictionary<string, Statistics> Snapshot()
        {
            lock (gate) return statistics.ToDictionary(p => p.Key, p => new Statistics
            { Hits = p.Value.Hits, Misses = p.Value.Misses, Writes = p.Value.Writes, Rejected = p.Value.Rejected,
                KeyMilliseconds = p.Value.KeyMilliseconds, ReadMilliseconds = p.Value.ReadMilliseconds, WriteMilliseconds = p.Value.WriteMilliseconds });
        }
        internal void ResetStatistics() { lock (gate) statistics.Clear(); TextHashTicks = TextHashBytes = TextHashCalls = 0; }
        internal void Clear()
        {
            lock (gate)
            {
                memory.Clear(); textDigests = new();
                if (Directory.Exists(directory)) foreach (string file in Directory.GetFiles(directory, "*.bin")) File.Delete(file);
                diskBudget.InvalidateSize();
            }
        }
        private Statistics Stats(string kind)
        { if (!statistics.TryGetValue(kind, out var value)) statistics.Add(kind, value = new()); return value; }
        private string PathFor(string key)
        {
            if (key.Length != 64 || key.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))) throw new ArgumentException("Invalid fact key");
            return Path.Combine(directory, key + ".bin");
        }
        internal static int Count(BinaryReader reader, int maximum = 1000000)
        { int count = reader.ReadInt32(); if (count < 0 || count > maximum) throw new InvalidDataException(); return count; }
        private static bool StorageFailure(Exception e) => e is IOException || e is InvalidDataException || e is InvalidOperationException || e is UnauthorizedAccessException ||
            e is ArgumentException || e is System.Security.SecurityException || e is NotSupportedException || e is OverflowException;
        private static double Elapsed(long start) => (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
