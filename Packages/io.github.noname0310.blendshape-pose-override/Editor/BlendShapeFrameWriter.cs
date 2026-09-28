using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Editor
{
    internal readonly struct BlendShapeReplacement
    {
        internal readonly int Index;
        internal readonly Vector3[] Vertices, Normals, Tangents;

        internal BlendShapeReplacement(int index, Vector3[] vertices, Vector3[] normals, Vector3[] tangents)
        {
            Index = index;
            Vertices = vertices;
            Normals = normals;
            Tangents = tangents;
        }
    }

    internal static class BlendShapeFrameWriter
    {
        private readonly struct Frame
        {
            internal readonly int SourceIndex, FirstVertex, VertexCount;
            internal readonly bool HasNormals, HasTangents;
            internal readonly float Weight;

            internal Frame(int index, SerializedProperty frame, SerializedProperty weight)
            {
                SourceIndex = index;
                FirstVertex = frame.FindPropertyRelative("firstVertex").intValue;
                VertexCount = frame.FindPropertyRelative("vertexCount").intValue;
                HasNormals = frame.FindPropertyRelative("hasNormals").boolValue;
                HasTangents = frame.FindPropertyRelative("hasTangents").boolValue;
                Weight = weight.floatValue;
            }

            internal void Write(SerializedProperty frame, SerializedProperty weight)
            {
                frame.FindPropertyRelative("firstVertex").intValue = FirstVertex;
                frame.FindPropertyRelative("vertexCount").intValue = VertexCount;
                frame.FindPropertyRelative("hasNormals").boolValue = HasNormals;
                frame.FindPropertyRelative("hasTangents").boolValue = HasTangents;
                weight.floatValue = Weight;
            }
        }

        internal static void Write(Mesh output, Mesh source, IReadOnlyList<BlendShapeReplacement> replacements)
        {
            if (replacements.Count == 0) return;
            var appendedChannels = new Dictionary<int, int>();
            foreach (var replacement in replacements)
            {
                int index = output.blendShapeCount;
                string name = "__noname_pose_override_" + index;
                while (output.GetBlendShapeIndex(name) >= 0) name += "_";
                output.AddBlendShapeFrame(name, 100, replacement.Vertices, replacement.Normals, replacement.Tangents);
                appendedChannels.Add(replacement.Index, index);
            }

            if (!TryRelink(output, source.blendShapeCount, appendedChannels))
                Rebuild(output, source, replacements);
        }

        private static bool TryRelink(Mesh output, int originalCount, IReadOnlyDictionary<int, int> replacements)
        {
            using var serialized = new SerializedObject(output);
            var channels = serialized.FindProperty("m_Shapes.channels");
            var frames = serialized.FindProperty("m_Shapes.shapes");
            var weights = serialized.FindProperty("m_Shapes.fullWeights");

            // This is an editor serialization layout, not a public Mesh mutation API. If Unity changes
            // it, use the public API implementation instead of writing an unfamiliar representation.
            if (channels == null || frames == null || weights == null ||
                !channels.isArray || !frames.isArray || !weights.isArray ||
                channels.arraySize != originalCount + replacements.Count || frames.arraySize == 0 ||
                weights.arraySize != frames.arraySize) return false;

            var channelSchema = channels.GetArrayElementAtIndex(0);
            var frameSchema = frames.GetArrayElementAtIndex(0);
            if (!HasType(channelSchema, "frameIndex", SerializedPropertyType.Integer) ||
                !HasType(channelSchema, "frameCount", SerializedPropertyType.Integer) ||
                !HasType(frameSchema, "firstVertex", SerializedPropertyType.Integer) ||
                !HasType(frameSchema, "vertexCount", SerializedPropertyType.Integer) ||
                !HasType(frameSchema, "hasNormals", SerializedPropertyType.Boolean) ||
                !HasType(frameSchema, "hasTangents", SerializedPropertyType.Boolean) ||
                weights.GetArrayElementAtIndex(0).propertyType != SerializedPropertyType.Float) return false;

            // Snapshot the small frame table before rewriting it; the large sparse vertex array stays
            // intact. Original replaced vertex ranges become unused. No unchanged morph is re-added.
            var retained = new List<Frame>(frames.arraySize);
            var firstFrames = new int[originalCount];
            var frameCounts = new int[originalCount];
            for (int shape = 0; shape < originalCount; shape++)
            {
                int input = replacements.TryGetValue(shape, out var replacement) ? replacement : shape;
                var channel = channels.GetArrayElementAtIndex(input);
                int first = channel.FindPropertyRelative("frameIndex").intValue;
                int count = channel.FindPropertyRelative("frameCount").intValue;
                if (first < 0 || count <= 0 || first > frames.arraySize - count) return false;
                firstFrames[shape] = retained.Count;
                frameCounts[shape] = count;
                for (int frame = first; frame < first + count; frame++)
                    retained.Add(new Frame(frame, frames.GetArrayElementAtIndex(frame), weights.GetArrayElementAtIndex(frame)));
            }

            // Keep channels and frames in their original order, including when a multi-frame target
            // becomes one frame. Names, hashes and blendshape indices remain unchanged.
            channels.arraySize = originalCount;
            for (int shape = 0; shape < originalCount; shape++)
            {
                var channel = channels.GetArrayElementAtIndex(shape);
                channel.FindPropertyRelative("frameIndex").intValue = firstFrames[shape];
                channel.FindPropertyRelative("frameCount").intValue = frameCounts[shape];
            }
            frames.arraySize = retained.Count;
            weights.arraySize = retained.Count;
            for (int frame = 0; frame < retained.Count; frame++)
                if (retained[frame].SourceIndex != frame)
                    retained[frame].Write(frames.GetArrayElementAtIndex(frame), weights.GetArrayElementAtIndex(frame));
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        private static bool HasType(SerializedProperty parent, string name, SerializedPropertyType type)
            => parent.FindPropertyRelative(name)?.propertyType == type;

        // Also used as a reference implementation in tests. The source mesh is never modified.
        internal static void Rebuild(Mesh output, Mesh source, IReadOnlyList<BlendShapeReplacement> replacements)
        {
            var byIndex = new Dictionary<int, BlendShapeReplacement>();
            foreach (var replacement in replacements) byIndex.Add(replacement.Index, replacement);
            output.ClearBlendShapes();
            var dv = new Vector3[source.vertexCount];
            var dn = new Vector3[source.vertexCount];
            var dt = new Vector3[source.vertexCount];
            for (int shape = 0; shape < source.blendShapeCount; shape++)
            {
                string name = source.GetBlendShapeName(shape);
                if (byIndex.TryGetValue(shape, out var replacement))
                    output.AddBlendShapeFrame(name, 100, replacement.Vertices, replacement.Normals, replacement.Tangents);
                else
                    for (int frame = 0; frame < source.GetBlendShapeFrameCount(shape); frame++)
                    {
                        source.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);
                        output.AddBlendShapeFrame(name, source.GetBlendShapeFrameWeight(shape, frame), dv, dn, dt);
                    }
            }
        }
    }
}
