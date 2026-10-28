using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Meshes
{
    internal sealed class BlendShapeRegion
    {
        internal string Name;
        internal int[] Triangles;
    }

    internal class BlendShapeRegionMasks
    {
        internal int[] Indices, SubMeshTriangleCounts;
        internal BlendShapeRegion[] Regions;

        // As in AAO Remove Mesh By BlendShape, a primitive belongs to the mask
        // when any of its vertices moves. Boundary triangles must be tested too.
        // Independent extraction; no optimizer-specific morph names are assumed.
        internal static BlendShapeRegionMasks Create(Mesh mesh, IEnumerable<string> names, float displacementThreshold)
        {
            if (mesh == null || !mesh.isReadable) throw new InvalidOperationException("BlendShape regions require a readable mesh.");
            if (float.IsNaN(displacementThreshold) || float.IsInfinity(displacementThreshold) || displacementThreshold < 0)
                throw new ArgumentOutOfRangeException(nameof(displacementThreshold));
            var indices = new List<int>(); var counts = new List<int>();
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles)
                    throw new InvalidOperationException("BlendShape regions require triangle submeshes.");
                var part = mesh.GetIndices(subMesh, applyBaseVertex: true); indices.AddRange(part); counts.Add(part.Length / 3);
            }
            if (indices.Count % 3 != 0 || indices.Any(i => i < 0 || i >= mesh.vertexCount))
                throw new InvalidOperationException("BlendShape regions contain invalid triangle indices.");
            var positions = new Vector3[mesh.vertexCount]; var moved = new bool[mesh.vertexCount];
            double thresholdSquared = (double)displacementThreshold * displacementThreshold;
            var regions = new List<BlendShapeRegion>();
            foreach (var name in (names ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal))
            {
                int index = mesh.GetBlendShapeIndex(name);
                if (index < 0) throw new InvalidOperationException("BlendShape region BlendShape is missing: " + name);
                Array.Clear(moved, 0, moved.Length);
                for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(index); frame++)
                {
                    mesh.GetBlendShapeFrameVertices(index, frame, positions, null, null);
                    for (int vertex = 0; vertex < positions.Length; vertex++)
                    {
                        var delta = positions[vertex];
                        double magnitude = (double)delta.x * delta.x + (double)delta.y * delta.y + (double)delta.z * delta.z;
                        if (double.IsNaN(magnitude) || double.IsInfinity(magnitude))
                            throw new InvalidOperationException("BlendShape region contains a non-finite displacement: " + name);
                        moved[vertex] |= magnitude > thresholdSquared;
                    }
                }
                regions.Add(new BlendShapeRegion { Name = name, Triangles = Enumerable.Range(0, indices.Count / 3)
                    .Where(t => moved[indices[t * 3]] || moved[indices[t * 3 + 1]] || moved[indices[t * 3 + 2]]).ToArray() });
            }
            return new BlendShapeRegionMasks { Indices = indices.ToArray(), SubMeshTriangleCounts = counts.ToArray(), Regions = regions.ToArray() };
        }

        internal Mesh Remove(Mesh source, bool[] triangles)
        {
            if (triangles == null || triangles.Length * 3 != Indices.Length) throw new ArgumentException("Invalid removal mask.", nameof(triangles));
            var result = UnityEngine.Object.Instantiate(source); result.name = source.name + " (Region filtered)";
            try
            {
                int offset = 0;
                for (int subMesh = 0; subMesh < SubMeshTriangleCounts.Length; subMesh++)
                {
                    int count = SubMeshTriangleCounts[subMesh]; var kept = new List<int>();
                    for (int triangle = offset; triangle < offset + count; triangle++)
                        if (!triangles[triangle]) { kept.Add(Indices[triangle * 3]); kept.Add(Indices[triangle * 3 + 1]); kept.Add(Indices[triangle * 3 + 2]); }
                    result.SetIndices(kept, MeshTopology.Triangles, subMesh, calculateBounds: false, baseVertex: 0); offset += count;
                }
                return result;
            }
            catch { UnityEngine.Object.DestroyImmediate(result); throw; }
        }
    }
}
