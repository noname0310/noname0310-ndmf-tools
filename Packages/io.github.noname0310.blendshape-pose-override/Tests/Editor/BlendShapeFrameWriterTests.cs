using System;
using System.Collections.Generic;
using Noname.AvatarTools.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Tests
{
    public sealed class BlendShapeFrameWriterTests
    {
        private readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var obj in owned) if (obj != null) Object.DestroyImmediate(obj);
            owned.Clear();
        }

        private Mesh Source()
        {
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            owned.Add(mesh);
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward };
            mesh.tangents = new[] { new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1), new Vector4(1, 0, 0, -1) };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            mesh.uv2 = new[] { Vector2.one, Vector2.zero, Vector2.right };
            mesh.colors32 = new[] { new Color32(1, 2, 3, 4), new Color32(5, 6, 7, 8), new Color32(9, 10, 11, 12) };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.bindposes = new[] { Matrix4x4.identity };
            var weight = new BoneWeight { boneIndex0 = 0, weight0 = 1 };
            mesh.boneWeights = new[] { weight, weight, weight };
            mesh.AddBlendShapeFrame("First", 100, new[] { Vector3.up, Vector3.zero, Vector3.zero }, null, null);
            mesh.AddBlendShapeFrame("Multi", 35, new[] { Vector3.zero, Vector3.forward, Vector3.zero }, mesh.vertices, null);
            mesh.AddBlendShapeFrame("Multi", 100, new[] { Vector3.zero, Vector3.forward * 2, Vector3.zero }, mesh.normals, mesh.vertices);
            mesh.AddBlendShapeFrame("\u307e\u3070\u305f\u304d", 75, new[] { Vector3.zero, Vector3.zero, Vector3.left }, null, mesh.vertices);
            // Deliberately collide with the first temporary name chosen by the writer.
            mesh.AddBlendShapeFrame("__noname_pose_override_5", 100, mesh.vertices, null, null);
            mesh.AddBlendShapeFrame("Empty", 100, new Vector3[3], null, null);
            return mesh;
        }

        private Mesh Clone(Mesh mesh)
        {
            var clone = Object.Instantiate(mesh);
            owned.Add(clone);
            return clone;
        }

        private static BlendShapeReplacement[] Replacements() => new[]
        {
            new BlendShapeReplacement(4, new Vector3[3], new Vector3[3], new Vector3[3]),
            new BlendShapeReplacement(1, new Vector3[3], new[] { Vector3.up, Vector3.zero, Vector3.zero }, new Vector3[3]),
            new BlendShapeReplacement(0, new[] { Vector3.forward, Vector3.zero, Vector3.right }, new Vector3[3], new[] { Vector3.zero, Vector3.left, Vector3.zero })
        };

        private static void AssertEquivalent(Mesh expected, Mesh actual)
        {
            Assert.AreEqual(expected.indexFormat, actual.indexFormat);
            CollectionAssert.AreEqual(expected.vertices, actual.vertices);
            CollectionAssert.AreEqual(expected.normals, actual.normals);
            CollectionAssert.AreEqual(expected.tangents, actual.tangents);
            CollectionAssert.AreEqual(expected.uv, actual.uv);
            CollectionAssert.AreEqual(expected.uv2, actual.uv2);
            CollectionAssert.AreEqual(expected.colors32, actual.colors32);
            CollectionAssert.AreEqual(expected.triangles, actual.triangles);
            CollectionAssert.AreEqual(expected.bindposes, actual.bindposes);
            CollectionAssert.AreEqual(expected.boneWeights, actual.boneWeights);
            Assert.AreEqual(expected.blendShapeCount, actual.blendShapeCount);
            var ev = new Vector3[expected.vertexCount];
            var en = new Vector3[expected.vertexCount];
            var et = new Vector3[expected.vertexCount];
            var av = new Vector3[actual.vertexCount];
            var an = new Vector3[actual.vertexCount];
            var at = new Vector3[actual.vertexCount];
            for (int shape = 0; shape < expected.blendShapeCount; shape++)
            {
                string name = expected.GetBlendShapeName(shape);
                Assert.AreEqual(name, actual.GetBlendShapeName(shape));
                Assert.AreEqual(shape, actual.GetBlendShapeIndex(name));
                Assert.AreEqual(expected.GetBlendShapeFrameCount(shape), actual.GetBlendShapeFrameCount(shape));
                for (int frame = 0; frame < expected.GetBlendShapeFrameCount(shape); frame++)
                {
                    Assert.AreEqual(expected.GetBlendShapeFrameWeight(shape, frame), actual.GetBlendShapeFrameWeight(shape, frame));
                    expected.GetBlendShapeFrameVertices(shape, frame, ev, en, et);
                    actual.GetBlendShapeFrameVertices(shape, frame, av, an, at);
                    CollectionAssert.AreEqual(ev, av, name + " vertices");
                    CollectionAssert.AreEqual(en, an, name + " normals");
                    CollectionAssert.AreEqual(et, at, name + " tangents");
                }
            }
        }

        [Test]
        public void PartialReplacementMatchesRebuildAndPreservesSourceAndUnchangedVertexRanges()
        {
            var source = Source();
            var before = EditorJsonUtility.ToJson(source);
            var expected = Clone(source);
            var actual = Clone(source);
            var replacements = Replacements();
            BlendShapeFrameWriter.Rebuild(expected, source, replacements);
            BlendShapeFrameWriter.Write(actual, source, replacements);
            AssertEquivalent(expected, actual);
            Assert.AreEqual(before, EditorJsonUtility.ToJson(source));

            // An unchanged frame must still refer to its original sparse vertex range. A complete
            // rebuild would move it after the preceding multi-frame shape was replaced.
            using var original = new SerializedObject(source);
            using var result = new SerializedObject(actual);
            var originalFrame = original.FindProperty("m_Shapes.shapes").GetArrayElementAtIndex(3);
            var resultFrame = result.FindProperty("m_Shapes.shapes").GetArrayElementAtIndex(2);
            Assert.AreEqual(originalFrame.FindPropertyRelative("firstVertex").intValue,
                resultFrame.FindPropertyRelative("firstVertex").intValue);
            Assert.AreEqual(actual.blendShapeCount, result.FindProperty("m_Shapes.channels").arraySize);
            Assert.AreEqual(5, result.FindProperty("m_Shapes.shapes").arraySize);
        }

        [Test]
        public void ReplacementSurvivesAssetSerializationAndReload()
        {
            var source = Source();
            var expected = Clone(source);
            var actual = Clone(source);
            var replacements = Replacements();
            BlendShapeFrameWriter.Rebuild(expected, source, replacements);
            BlendShapeFrameWriter.Write(actual, source, replacements);
            string path = "Assets/__NonameBlendShapeRoundTrip_" + Guid.NewGuid().ToString("N") + ".asset";
            try
            {
                AssetDatabase.CreateAsset(actual, path);
                AssetDatabase.SaveAssetIfDirty(actual);
                Resources.UnloadAsset(actual);
                AssertEquivalent(expected, AssetDatabase.LoadAssetAtPath<Mesh>(path));
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }

        [TestCase(BlendShapeBufferLayout.PerShape)]
        [TestCase(BlendShapeBufferLayout.PerVertex)]
        public void ReplacementUpdatesAnAlreadyCreatedGpuBlendShapeBuffer(BlendShapeBufferLayout layout)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("A graphics device is required.");
            var source = Source();
            var actual = Clone(source);
            using (actual.GetBlendShapeBuffer(layout)) { }
            var expected = Clone(source);
            var replacements = Replacements();
            BlendShapeFrameWriter.Rebuild(expected, source, replacements);
            BlendShapeFrameWriter.Write(actual, source, replacements);
            using var expectedBuffer = expected.GetBlendShapeBuffer(layout);
            using var actualBuffer = actual.GetBlendShapeBuffer(layout);
            Assert.AreEqual(expectedBuffer.stride, actualBuffer.stride);
            var expectedData = new uint[expectedBuffer.count * expectedBuffer.stride / sizeof(uint)];
            var actualData = new uint[actualBuffer.count * actualBuffer.stride / sizeof(uint)];
            expectedBuffer.GetData(expectedData);
            actualBuffer.GetData(actualData);
            if (layout == BlendShapeBufferLayout.PerVertex)
            {
                // The first vertexCount + 1 words give vertex record offsets. Unity may allocate
                // unused space after the final offset for old sparse ranges; shaders never read it.
                Assert.AreEqual(sizeof(uint), expectedBuffer.stride);
                int usedWords = (int)expectedData[expected.vertexCount];
                Assert.That(usedWords, Is.InRange(expected.vertexCount + 1, expectedData.Length));
                Assert.That(actualData.Length, Is.GreaterThanOrEqualTo(usedWords));
                for (int word = 0; word < usedWords; word++)
                    Assert.AreEqual(expectedData[word], actualData[word], "GPU data word " + word);
                return;
            }
            int words = expectedBuffer.stride / sizeof(uint);
            // After replacing Multi all channels have one frame. Empty ranges use an inverted range.
            for (int shape = 0; shape < expected.blendShapeCount; shape++)
            {
                var er = expected.GetBlendShapeBufferRange(shape);
                var ar = actual.GetBlendShapeBufferRange(shape);
                int expectedCount = (int)((long)er.endIndex - er.startIndex + 1);
                int actualCount = (int)((long)ar.endIndex - ar.startIndex + 1);
                Assert.AreEqual(expectedCount, actualCount);
                for (int word = 0; word < expectedCount * words; word++)
                    Assert.AreEqual(expectedData[er.startIndex * words + word], actualData[ar.startIndex * words + word],
                        "GPU data for shape " + shape + ", word " + word);
            }
        }
    }
}
