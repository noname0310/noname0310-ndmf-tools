using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noname.AvatarTools.Meshes
{
    // One immutable mesh-layout snapshot for index deletion, compaction and
    // assembly. Retaining original IDs and carrying an ID attribute are costed
    // against actual streams and sparse morph deltas, not a deletion percentage.
    internal sealed class VertexRewriteLayout
    {
        private readonly Mesh mesh;
        private readonly bool[] used;
        private long? removedBytes;
        internal readonly int UsedVertices;
        internal readonly int VertexCount;
        internal int CompactVertexCount => UsedVertices == 0 ? VertexCount : UsedVertices;

        internal VertexRewriteLayout(Mesh mesh)
        {
            this.mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
            VertexCount = mesh.vertexCount; used = new bool[VertexCount];
            var indices = new List<int>(); int count = 0;
            for (int slot = 0; slot < mesh.subMeshCount; slot++)
            {
                mesh.GetIndices(indices, slot, true);
                foreach (int vertex in indices)
                {
                    if (vertex < 0 || vertex >= used.Length) throw new InvalidOperationException("Out-of-range vertex index.");
                    if (!used[vertex]) { used[vertex] = true; count++; }
                }
            }
            UsedVertices = count;
        }

        internal long RemovedBytes
        {
            get
            {
                if (removedBytes.HasValue) return removedBytes.Value;
                if (UsedVertices == 0 || UsedVertices == VertexCount) return (removedBytes = 0).Value;
                long stride = 0;
                for (int stream = 0; stream < mesh.vertexBufferCount; stream++) stride += mesh.GetVertexBufferStride(stream);
                var removed = new int[VertexCount - UsedVertices]; int cursor = 0;
                for (int vertex = 0; vertex < used.Length; vertex++) if (!used[vertex]) removed[cursor++] = vertex;
                long bytes = removed.Length * stride;
                if (mesh.blendShapeCount != 0)
                {
                    var positions = new Vector3[VertexCount]; var normals = new Vector3[VertexCount]; var tangents = new Vector3[VertexCount];
                    for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                    for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
                    {
                        mesh.GetBlendShapeFrameVertices(shape, frame, positions, normals, tangents);
                        foreach (int vertex in removed)
                            if (Nonzero(positions[vertex]) || Nonzero(normals[vertex]) || Nonzero(tangents[vertex])) bytes += 40;
                    }
                }
                removedBytes = bytes; return bytes;
            }
        }

        internal static long OriginalIdAttributeBytes(long vertices) => checked(vertices * 8);
        private static bool Nonzero(Vector3 value) => value.x != 0 || value.y != 0 || value.z != 0;
    }
}
