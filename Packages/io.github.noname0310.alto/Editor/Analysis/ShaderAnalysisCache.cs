using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Rendering;

namespace Noname.AvatarTools.Analysis
{
    // Content-addressed facts about a compiler program, never about an avatar.
    // Includes and material constants are already expanded into Source. Callers
    // must still prove current keyword coverage and the validity of those constants.
    internal sealed class ShaderAnalysisCache
    {
        private const int Format = 1;
        private const int MaximumRecordBytes = 1024 * 1024;
        private const long MaximumDiskBytes = 128L * 1024 * 1024;
        private const int MaximumFiles = 16384;
        private static readonly object[] Gates = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
        private static readonly Lazy<ShaderAnalysisCache> SharedCache = new(() =>
            new ShaderAnalysisCache(Path.GetFullPath("Library/ALTO/ShaderAnalysis"),
                typeof(ShaderAnalysisCache).Module.ModuleVersionId.ToString("N")));
        internal static ShaderAnalysisCache Shared => SharedCache.Value;
        private readonly string directory, analyzer;
        private int writes;
        private long hits, misses;
        internal long Hits => Interlocked.Read(ref hits);
        internal long Misses => Interlocked.Read(ref misses);

        internal ShaderAnalysisCache(string directory, string analyzer)
        { this.directory = Path.GetFullPath(directory); this.analyzer = analyzer; }

        internal string Key(string source, string entry, ShaderType stage, string compiler)
        {
            if (string.IsNullOrEmpty(compiler)) return null;
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
            {
                writer.Write(Format); writer.Write(analyzer); writer.Write(compiler);
                writer.Write((int)stage); writer.Write(entry ?? ""); writer.Write(source ?? "");
            }
            return Digest(bytes.ToArray());
        }

        internal (D3DShaderInputs.Input[] Inputs, string Error) Compile(string key,
            Func<(D3DShaderInputs.Input[] Inputs, string Error)> compile)
        {
            if (key == null) return compile();
            lock (Gate(key))
            {
                if (TryRead(key, out var bytes))
                {
                    try
                    {
                        using var stream = new MemoryStream(bytes, false);
                        using var reader = new BinaryReader(stream, Encoding.UTF8);
                        int count = reader.ReadInt32();
                        if (count < 0 || count > 16384) throw new InvalidDataException();
                        var inputs = new D3DShaderInputs.Input[count];
                        for (int i = 0; i < count; i++)
                        {
                            string name = reader.ReadString(); var kind = (D3DShaderInputs.InputKind)reader.ReadByte();
                            uint index = reader.ReadUInt32(); byte mask = reader.ReadByte();
                            if (name.Length == 0 || name.Length > 1024 || !Enum.IsDefined(typeof(D3DShaderInputs.InputKind), kind) || mask > 15)
                                throw new InvalidDataException();
                            inputs[i] = new D3DShaderInputs.Input(name, mask, index, kind);
                        }
                        if (stream.Position != stream.Length) throw new InvalidDataException();
                        Interlocked.Increment(ref hits); return (inputs, null);
                    }
                    catch (Exception exception) when (ReadFailure(exception)) { }
                }
                Interlocked.Increment(ref misses);
                var result = compile();
                // Transient compiler failures, unsupported stages and incomplete
                // reflection are not durable evidence about an input's absence.
                if (result.Inputs == null || result.Error != null) return result;
                using var data = new MemoryStream();
                using (var writer = new BinaryWriter(data, Encoding.UTF8, true))
                {
                    writer.Write(result.Inputs.Length);
                    foreach (var input in result.Inputs)
                    { writer.Write(input.Semantic); writer.Write((byte)input.Kind); writer.Write(input.SemanticIndex); writer.Write(input.ReadMask); }
                }
                Store(key, data.ToArray());
                return result;
            }
        }

        private bool TryRead(string key, out byte[] payload)
        {
            payload = null;
            try
            {
                string path = RecordPath(key);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < 68 || info.Length > MaximumRecordBytes) return false;
                byte[] bytes = File.ReadAllBytes(path);
                using var stream = new MemoryStream(bytes, false);
                using var reader = new BinaryReader(stream, Encoding.UTF8);
                if (reader.ReadInt32() != Format || reader.ReadString() != key) return false;
                string digest = reader.ReadString(); int count = reader.ReadInt32();
                if (count < 0 || count != stream.Length - stream.Position) return false;
                byte[] body = reader.ReadBytes(count);
                if (Digest(body) != digest) return false;
                payload = body; return true;
            }
            catch (Exception exception) when (ReadFailure(exception)) { return false; }
        }

        private void Store(string key, byte[] payload)
        {
            if (payload.Length > MaximumRecordBytes - 256) return;
            string temporary = null;
            try
            {
                Directory.CreateDirectory(directory);
                string path = RecordPath(key); temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(Format); writer.Write(key); writer.Write(Digest(payload));
                    writer.Write(payload.Length); writer.Write(payload);
                }
                // Readers see the complete old or new record, never a partial write.
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                temporary = null;
                if (Interlocked.Increment(ref writes) % 256 == 1) Trim();
            }
            catch (Exception exception) when (ReadFailure(exception)) { }
            finally { if (temporary != null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        private void Trim()
        {
            var files = new DirectoryInfo(directory).GetFiles("*.bin").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            long total = 0;
            for (int i = 0; i < files.Length; i++)
            {
                total += files[i].Length;
                if (i >= MaximumFiles || total > MaximumDiskBytes)
                    try { files[i].Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private string RecordPath(string key)
        {
            if (key == null || key.Length != 64 || key.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                throw new ArgumentException("Invalid shader cache key", nameof(key));
            return Path.Combine(directory, key + ".bin");
        }
        private static object Gate(string key) => Gates[(key[0] + 17 * key[1]) % Gates.Length];
        private static bool ReadFailure(Exception exception) => exception is IOException || exception is UnauthorizedAccessException ||
            exception is ArgumentException || exception is System.Security.SecurityException || exception is NotSupportedException;
        internal static string Digest(byte[] bytes)
        { return BitConverter.ToString(ContentDigest.Bytes(bytes)).Replace("-", "").ToLowerInvariant(); }

        [MenuItem("Tools/Noname/ALTO/Clear Shader Analysis Cache")]
        private static void Clear()
        {
            ContentFactCache.Shared.Clear();
            var cache = Shared;
            if (!Directory.Exists(cache.directory)) return;
            // Delete only our flat records, never recursively delete a caller's path.
            foreach (string file in Directory.GetFiles(cache.directory, "*.bin")) File.Delete(file);
        }
    }
}
