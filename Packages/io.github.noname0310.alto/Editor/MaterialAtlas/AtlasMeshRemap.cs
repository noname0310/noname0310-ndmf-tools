using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.MaterialAtlas
{
    internal static class AtlasMeshRemap
    {
        // Duplicate only vertices shared by slots with different UV transforms. Keep
        // every source attribute, arbitrary skinning influences and all morph frames.
        internal static Mesh Apply(Mesh source, Rect?[] regions, Rect domain)
        {
            if (regions.Length != source.subMeshCount) throw new ArgumentException("Atlas regions must match submeshes.");
            var vertices = Enumerable.Range(0, source.vertexCount).ToList();
            var transforms = new List<Rect?>(Enumerable.Repeat<Rect?>(null, source.vertexCount));
            var assigned = new bool[source.vertexCount];
            var copies = new Dictionary<(int, Rect?), int>();
            var indices = new int[source.subMeshCount][];
            for (int slot = 0; slot < indices.Length; slot++)
            {
                indices[slot] = source.GetIndices(slot, true);
                for (int i = 0; i < indices[slot].Length; i++)
                {
                    int original = indices[slot][i]; var key = (original, regions[slot]);
                    if (!copies.TryGetValue(key, out int mapped))
                    {
                        if (!assigned[original]) { mapped = original; assigned[original] = true; transforms[original] = regions[slot]; }
                        else { mapped = vertices.Count; vertices.Add(original); transforms.Add(regions[slot]); }
                        copies.Add(key, mapped);
                    }
                    indices[slot][i] = mapped;
                }
            }
            var output = new Mesh { name = source.name + " (Chained atlas)", indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try
            {
                output.SetVertexBufferParams(vertices.Count, source.GetVertexAttributes());
                using (var data = Mesh.AcquireReadOnlyMeshData(source))
                    for (int stream = 0; stream < source.vertexBufferCount; stream++)
                    {
                        int stride = source.GetVertexBufferStride(stream);
                        var input = data[0].GetVertexData<byte>(stream);
                        using var buffer = new NativeArray<byte>(checked(vertices.Count * stride), Allocator.Temp);
                        for (int i = 0; i < vertices.Count; i++) NativeArray<byte>.Copy(input, vertices[i] * stride, buffer, i * stride, stride);
                        output.SetVertexBufferData(buffer, 0, 0, buffer.Length, stream);
                    }
                output.bindposes = source.bindposes;
                using (var counts = source.GetBonesPerVertex())
                using (var weights = source.GetAllBoneWeights())
                    if (weights.Length != 0)
                    {
                        var offsets = new int[source.vertexCount + 1];
                        for (int i = 0; i < source.vertexCount; i++) offsets[i + 1] = offsets[i] + counts[i];
                        using var newCounts = new NativeArray<byte>(vertices.Select(v => counts[v]).ToArray(), Allocator.Temp);
                        using var newWeights = new NativeArray<BoneWeight1>(vertices.Sum(v => (int)counts[v]), Allocator.Temp);
                        int offset = 0;
                        foreach (int vertex in vertices)
                        { NativeArray<BoneWeight1>.Copy(weights, offsets[vertex], newWeights, offset, counts[vertex]); offset += counts[vertex]; }
                        output.SetBoneWeights(newCounts, newWeights);
                    }
                var uv = new List<Vector4>(); source.GetUVs(0, uv);
                var mappedUv = vertices.Select(v => uv[v]).ToList();
                for (int i = 0; i < mappedUv.Count; i++)
                    if (transforms[i] is Rect region)
                    {
                        var value = mappedUv[i];
                        value.x = region.x + (value.x - domain.x) / domain.width * region.width;
                        value.y = region.y + (value.y - domain.y) / domain.height * region.height;
                        mappedUv[i] = value;
                    }
                int dimension = source.GetVertexAttributeDimension(VertexAttribute.TexCoord0);
                if (dimension == 2) output.SetUVs(0, mappedUv.Select(v => new Vector2(v.x, v.y)).ToList());
                else if (dimension == 3) output.SetUVs(0, mappedUv.Select(v => new Vector3(v.x, v.y, v.z)).ToList());
                else output.SetUVs(0, mappedUv);
                output.subMeshCount = indices.Length;
                for (int slot = 0; slot < indices.Length; slot++) output.SetIndices(indices[slot], source.GetTopology(slot), slot, false);
                var positions = new Vector3[source.vertexCount]; var normals = new Vector3[source.vertexCount]; var tangents = new Vector3[source.vertexCount];
                for (int shape = 0; shape < source.blendShapeCount; shape++)
                    for (int frame = 0; frame < source.GetBlendShapeFrameCount(shape); frame++)
                    {
                        source.GetBlendShapeFrameVertices(shape, frame, positions, normals, tangents);
                        output.AddBlendShapeFrame(source.GetBlendShapeName(shape), source.GetBlendShapeFrameWeight(shape, frame),
                            vertices.Select(v => positions[v]).ToArray(), vertices.Select(v => normals[v]).ToArray(), vertices.Select(v => tangents[v]).ToArray());
                    }
                output.bounds = source.bounds;
                return output;
            }
            catch { UnityEngine.Object.DestroyImmediate(output); throw; }
        }
    }
}
