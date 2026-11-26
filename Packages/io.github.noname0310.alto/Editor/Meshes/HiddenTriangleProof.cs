using System;
using System.Collections.Generic;
using Noname.AvatarTools.Analysis;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Meshes
{
    // Coincident positions alone are insufficient: skinning, any remaining morph
    // and any input used by a vertex position program can separate the corners.
    // This certificate compares the full remaining deformation interface exactly.
    internal sealed class HiddenTriangleProof
    {
        private readonly Mesh mesh;
        private readonly Vector3[] positions, normals;
        private readonly Vector4[] tangents;
        private readonly Color[] colors;
        private readonly List<Vector4>[] uvs = new List<Vector4>[8];
        private readonly byte[] influenceCounts;
        private readonly int[] influenceStarts;
        private readonly BoneWeight1[] influences;
        private readonly int[] positionMorphs, normalMorphs, tangentMorphs;
        private readonly HashSet<VertexAttribute> unreadable = new();
        internal readonly int[][] Indices;
        internal readonly bool[][] Candidates;
        internal int CandidateCount { get; private set; }

        internal HiddenTriangleProof(Mesh mesh)
        {
            this.mesh = mesh;
            Indices = new int[mesh.subMeshCount][]; Candidates = new bool[mesh.subMeshCount][];
            foreach (var attribute in mesh.GetVertexAttributes())
                if (!ExactGetterLayout(attribute)) unreadable.Add(attribute.attribute);
            if (unreadable.Contains(VertexAttribute.Position)) return;
            positions = mesh.vertices;
            for (int slot = 0; slot < mesh.subMeshCount; slot++)
            {
                if (mesh.GetTopology(slot) != MeshTopology.Triangles) continue;
                var indices = Indices[slot] = mesh.GetIndices(slot, true);
                if (indices.Length % 3 != 0) throw new InvalidOperationException("Incomplete triangle indices.");
                var candidates = Candidates[slot] = new bool[indices.Length / 3];
                for (int triangle = 0; triangle < candidates.Length; triangle++)
                {
                    int a = indices[triangle * 3], b = indices[triangle * 3 + 1], c = indices[triangle * 3 + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= positions.Length || b >= positions.Length || c >= positions.Length)
                        throw new InvalidOperationException("Out-of-range triangle index.");
                    if (Same(positions[a], positions[b]) && Same(positions[a], positions[c]))
                    { candidates[triangle] = true; CandidateCount++; }
                }
            }
            if (CandidateCount == 0) return;
            using (var counts = mesh.GetBonesPerVertex()) influenceCounts = counts.ToArray();
            using (var weights = mesh.GetAllBoneWeights()) influences = weights.ToArray();
            influenceStarts = new int[influenceCounts.Length + 1];
            for (int i = 0; i < influenceCounts.Length; i++) influenceStarts[i + 1] = influenceStarts[i] + influenceCounts[i];
            if (influenceCounts.Length != 0 && influenceCounts.Length != positions.Length || influenceStarts[influenceCounts.Length] != influences.Length)
                throw new InvalidOperationException("Inconsistent skinning influences.");
            normals = unreadable.Contains(VertexAttribute.Normal) ? Array.Empty<Vector3>() : mesh.normals;
            tangents = unreadable.Contains(VertexAttribute.Tangent) ? Array.Empty<Vector4>() : mesh.tangents;
            colors = unreadable.Contains(VertexAttribute.Color) ? Array.Empty<Color>() : mesh.colors;
            for (int channel = 0; channel < uvs.Length; channel++)
            {
                uvs[channel] = new List<Vector4>();
                if (!unreadable.Contains(VertexAttribute.TexCoord0 + channel)) mesh.GetUVs(channel, uvs[channel]);
            }
            positionMorphs = new int[positions.Length]; normalMorphs = new int[positions.Length]; tangentMorphs = new int[positions.Length];
            var selected = new HashSet<int>();
            for (int slot = 0; slot < Candidates.Length; slot++)
                if (Candidates[slot] != null)
                    for (int triangle = 0; triangle < Candidates[slot].Length; triangle++)
                        if (Candidates[slot][triangle]) for (int corner = 0; corner < 3; corner++) selected.Add(Indices[slot][triangle * 3 + corner]);
            var p = new Vector3[positions.Length]; var n = new Vector3[p.Length]; var t = new Vector3[p.Length];
            var groups = new Dictionary<(int Previous, int X, int Y, int Z), int>();
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
            {
                if (!MotionAnalysis.Finite(mesh.GetBlendShapeFrameWeight(shape, frame)))
                { foreach (int vertex in selected) positionMorphs[vertex] = normalMorphs[vertex] = tangentMorphs[vertex] = -1; return; }
                mesh.GetBlendShapeFrameVertices(shape, frame, p, n, t);
                Refine(p, positionMorphs); Refine(n, normalMorphs); Refine(t, tangentMorphs);
            }

            // Refine exact equivalence classes while reusing one native frame
            // buffer. Keeping every morph frame would scale memory as V * frames.
            void Refine(Vector3[] values, int[] partition)
            {
                groups.Clear();
                foreach (int vertex in selected)
                {
                    if (partition[vertex] < 0) continue;
                    var value = values[vertex];
                    if (!MotionAnalysis.Finite(value.x) || !MotionAnalysis.Finite(value.y) || !MotionAnalysis.Finite(value.z))
                    { partition[vertex] = -1; continue; }
                    var key = (partition[vertex], BitConverter.SingleToInt32Bits(value.x), BitConverter.SingleToInt32Bits(value.y), BitConverter.SingleToInt32Bits(value.z));
                    if (!groups.TryGetValue(key, out int group)) { group = groups.Count; groups.Add(key, group); }
                    partition[vertex] = group;
                }
            }
        }

        internal bool Equivalent(int slot, int triangle, ShaderPositionInputs inputs)
        {
            if (inputs == null || inputs.Issue != null || Candidates[slot] == null || !Candidates[slot][triangle]) return false;
            int a = Indices[slot][triangle * 3], b = Indices[slot][triangle * 3 + 1], c = Indices[slot][triangle * 3 + 2];
            return Pair(a, b, inputs) && Pair(a, c, inputs);
        }

        private bool Pair(int a, int b, ShaderPositionInputs inputs)
        {
            if (a == b) return true;
            foreach (var read in inputs.Reads)
            {
                string name = read.Key.Semantic; uint channel = read.Key.Index;
                switch (name)
                {
                    case "SV_INSTANCEID": break; // Identical within this unchanged draw.
                    case "POSITION":
                        if (channel != 0 || !Same(positions[a], positions[b]) || !SameSkin(a, b)) return false;
                        if (positionMorphs[a] < 0 || positionMorphs[a] != positionMorphs[b]) return false;
                        break;
                    case "NORMAL":
                        if (channel != 0 || unreadable.Contains(VertexAttribute.Normal) ||
                            !SameSkin(a, b) || normals.Length != 0 && !Same(normals[a], normals[b])) return false;
                        if (normalMorphs[a] < 0 || normalMorphs[a] != normalMorphs[b]) return false;
                        break;
                    case "TANGENT":
                        if (channel != 0 || unreadable.Contains(VertexAttribute.Tangent) ||
                            !SameSkin(a, b) || tangents.Length != 0 && !Same(tangents[a], tangents[b])) return false;
                        if (tangentMorphs[a] < 0 || tangentMorphs[a] != tangentMorphs[b]) return false;
                        break;
                    case "COLOR":
                        if (channel != 0 || unreadable.Contains(VertexAttribute.Color) ||
                            colors.Length != 0 && !Same((Vector4)colors[a], (Vector4)colors[b])) return false;
                        break;
                    case "TEXCOORD":
                        if (channel >= uvs.Length) return false;
                        int effective = (int)channel;
                        while (effective >= 0 && !mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + effective)) effective--;
                        if (effective >= 0 && (unreadable.Contains(VertexAttribute.TexCoord0 + effective) ||
                            !Same(uvs[effective][a], uvs[effective][b]))) return false;
                        break;
                    case "BLENDINDICES": case "BLENDWEIGHT":
                        // The semantic influence list used for skinning can omit
                        // zero-weight lanes. It is not a raw shader-input proof.
                        return false;
                    default: return false; // Includes VertexID and unrecognized native inputs.
                }
            }
            return true;
        }

        private bool SameSkin(int a, int b)
        {
            if (influenceCounts.Length == 0) return true;
            if (influenceCounts[a] != influenceCounts[b]) return false;
            for (int i = 0; i < influenceCounts[a]; i++)
            {
                var x = influences[influenceStarts[a] + i]; var y = influences[influenceStarts[b] + i];
                if (x.boneIndex != y.boneIndex || !Same(x.weight, y.weight)) return false;
            }
            return true;
        }

        private static bool ExactGetterLayout(VertexAttributeDescriptor attribute)
        {
            if (attribute.dimension < 1 || attribute.dimension > 4 ||
                (attribute.attribute == VertexAttribute.Position || attribute.attribute == VertexAttribute.Normal) && attribute.dimension != 3 ||
                attribute.attribute == VertexAttribute.Tangent && attribute.dimension != 4) return false;
            // Vector3 getters omit a fourth position/normal component. A float
            // getter can also merge distinct 32-bit integers above 2^24, while a
            // uint/int vertex input still observes their original values.
            switch (attribute.format)
            {
                case VertexAttributeFormat.Float32: case VertexAttributeFormat.Float16:
                case VertexAttributeFormat.UNorm8: case VertexAttributeFormat.SNorm8:
                case VertexAttributeFormat.UNorm16: case VertexAttributeFormat.SNorm16: return true;
                default: return false;
            }
        }

        private static bool Same(float a, float b) => MotionAnalysis.Finite(a) && MotionAnalysis.Finite(b) &&
            BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        private static bool Same(Vector3 a, Vector3 b) => Same(a.x, b.x) && Same(a.y, b.y) && Same(a.z, b.z);
        private static bool Same(Vector4 a, Vector4 b) => Same(a.x, b.x) && Same(a.y, b.y) && Same(a.z, b.z) && Same(a.w, b.w);
    }
}
