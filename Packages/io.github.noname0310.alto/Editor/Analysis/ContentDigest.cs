using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Unity.Collections.LowLevel.Unsafe;

namespace Noname.AvatarTools.Analysis
{
    // Unity's Mono SHA256 providers share a managed implementation on Windows.
    // Use the OS implementation for large buffers, with byte-identical managed
    // fallback. Small keys avoid the native call and pinning overhead.
    internal static class ContentDigest
    {
        private sealed class Provider : SafeHandle
        {
            internal Provider(IntPtr value) : base(IntPtr.Zero, true) { SetHandle(value); }
            public override bool IsInvalid => handle == IntPtr.Zero;
            protected override bool ReleaseHandle() => BCryptCloseAlgorithmProvider(handle, 0) == 0;
        }
        private static readonly Lazy<Provider> Algorithm = new(Open);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        private static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string name, string implementation, uint flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, uint flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptHash(Provider algorithm, IntPtr secret, uint secretLength, IntPtr input, uint inputLength,
            [Out] byte[] output, uint outputLength);

        private static Provider Open()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            try { return BCryptOpenAlgorithmProvider(out var handle, "SHA256", null, 0) == 0 ? new Provider(handle) : null; }
            catch (Exception e) when (Unavailable(e)) { return null; }
        }
        private static bool Unavailable(Exception e) => e is DllNotFoundException || e is EntryPointNotFoundException ||
            e is BadImageFormatException || e is PlatformNotSupportedException;

        private static bool TryHash(object data, uint bytes, out byte[] digest)
        {
            digest = null;
            if (bytes < 4096) return false;
            var algorithm = Algorithm.Value;
            if (algorithm == null || algorithm.IsInvalid) return false;
            var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var result = new byte[32];
                if (BCryptHash(algorithm, IntPtr.Zero, 0, pinned.AddrOfPinnedObject(), bytes, result, 32) != 0) return false;
                digest = result; return true;
            }
            catch (Exception e) when (Unavailable(e)) { return false; }
            finally { pinned.Free(); }
        }
        internal static bool TryText(string text, out byte[] digest)
        {
            digest = null;
            // Pinned strings expose UTF-16 code units, including unpaired
            // surrogates, without allocating a second source-sized buffer.
            return BitConverter.IsLittleEndian && TryHash(text, checked((uint)text.Length * 2), out digest);
        }
        internal static byte[] Bytes(byte[] data, bool managed = false)
        {
            if (!managed && TryHash(data, checked((uint)data.Length), out var digest)) return digest;
            using var hash = SHA256.Create(); return hash.ComputeHash(data);
        }

        // Hash the exact native representation without allocating a second
        // frame-sized buffer. Unmanaged is required at compile time; the runtime
        // check also rejects marshalled layouts such as bool-containing structs.
        internal static byte[] BlittableArray<T>(T[] data, bool managed = false) where T : unmanaged
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (!UnsafeUtility.IsBlittable<T>()) throw new ArgumentException("The digest requires a blittable element layout.", nameof(data));
            long bytes = checked(data.LongLength * UnsafeUtility.SizeOf<T>());
            if (!managed && bytes <= uint.MaxValue && TryHash(data, (uint)bytes, out var digest)) return digest;
            using var hash = SHA256.Create();
            if (bytes == 0) return hash.ComputeHash(System.Array.Empty<byte>());
            var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var buffer = new byte[(int)Math.Min(bytes, 64 * 1024)];
                long address = pinned.AddrOfPinnedObject().ToInt64();
                for (long offset = 0; offset < bytes; offset += buffer.Length)
                {
                    int count = (int)Math.Min(buffer.Length, bytes - offset);
                    Marshal.Copy(new IntPtr(address + offset), buffer, 0, count);
                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                }
                hash.TransformFinalBlock(System.Array.Empty<byte>(), 0, 0);
                return hash.Hash;
            }
            finally { pinned.Free(); }
        }
    }
}
