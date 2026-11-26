using System;
using System.IO;
using System.Linq;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed class ContentDigestArrayTests
    {
        [TestCase(false)] [TestCase(true)]
        public void ContentDigestArrays_VectorFramesMatchCanonicalBytesWithNativeAndManagedPaths(bool managed)
        {
            var values = Enumerable.Range(0, 2049).Select(i => new Vector3(i * .125f, -i, i + .5f)).ToArray();
            values[3].x = BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x80000000)), 0);
            values[4].z = BitConverter.ToSingle(BitConverter.GetBytes(0x7fc00001), 0);
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                foreach (var value in values) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
            Assert.That(ContentDigest.BlittableArray(values, managed), Is.EqualTo(ContentDigest.Bytes(stream.ToArray(), managed: true)));
            var changed = (Vector3[])values.Clone(); changed[3].x = 0;
            Assert.That(ContentDigest.BlittableArray(changed, managed), Is.Not.EqualTo(ContentDigest.BlittableArray(values, managed)));
            changed = (Vector3[])values.Clone(); changed[4].z = BitConverter.ToSingle(BitConverter.GetBytes(0x7fc00002), 0);
            Assert.That(ContentDigest.BlittableArray(changed, managed), Is.Not.EqualTo(ContentDigest.BlittableArray(values, managed)));
        }

        [TestCase(false)] [TestCase(true)]
        public void ContentDigestArrays_MatrixStrideAndOrderingAreExact(bool managed)
        {
            var values = Enumerable.Range(0, 129).Select(i => Matrix4x4.TRS(new Vector3(i, 2, 3), Quaternion.identity, Vector3.one * .5f)).ToArray();
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                foreach (var value in values) for (int field = 0; field < 16; field++) writer.Write(value[field]);
            Assert.That(ContentDigest.BlittableArray(values, managed), Is.EqualTo(ContentDigest.Bytes(stream.ToArray(), managed: true)));
        }

        [TestCase(false)] [TestCase(true)]
        public void ContentDigestArrays_EmptyAndIndexBuffersMatchByteDigest(bool managed)
        {
            Assert.That(ContentDigest.BlittableArray(Array.Empty<int>(), managed), Is.EqualTo(ContentDigest.Bytes(Array.Empty<byte>(), managed: true)));
            var values = Enumerable.Range(0, 4097).Select(i => i % 2 == 0 ? int.MinValue + i : int.MaxValue - i).ToArray();
            var bytes = values.SelectMany(BitConverter.GetBytes).ToArray();
            Assert.That(ContentDigest.BlittableArray(values, managed), Is.EqualTo(ContentDigest.Bytes(bytes, managed: true)));
            Assert.Throws<ArgumentNullException>(() => ContentDigest.BlittableArray<int>(null, managed));
        }
    }
}
