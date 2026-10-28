using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Meshes
{
    internal sealed class SkinnedMeshAssembly : IDisposable
    {
        internal Mesh Mesh;
        internal Transform[] Bones;
        internal Material[] Materials;
        internal float[] ShapeWeights;
        internal readonly Dictionary<SkinnedMeshRenderer, Dictionary<string, string>> ShapeNames = new();
        internal readonly Dictionary<SkinnedMeshRenderer, int[]> MaterialSlots = new();
        public void Dispose() { if (Mesh != null) Object.DestroyImmediate(Mesh); Mesh = null; }
    }

    // Geometry representation was reviewed against AAO 1.9.20 MeshInfo2 and
    // MergeSkinnedMeshProcessor; see CREDITS.md. Independently implemented assembly.
    // Keep each source's vertex coordinates, bind poses and deformation deltas together.
    // No inverse of a currently collapsed bone is needed, and source assets are immutable.
    internal static class SkinnedMeshAssembler
    {
        private sealed class Input
        {
            internal SkinnedMeshRenderer Renderer;
            internal int[] Vertices, Remap;
            internal Mesh Mesh => Renderer.sharedMesh;
            internal Input(SkinnedMeshRenderer renderer, bool trim)
            {
                Renderer = renderer;
                var used = new bool[Mesh.vertexCount];
                if (trim)
                    for (int slot = 0; slot < Mesh.subMeshCount; slot++)
                        foreach (int vertex in Mesh.GetIndices(slot, applyBaseVertex: true))
                        {
                            if (vertex < 0 || vertex >= used.Length) throw new ArgumentException("Submesh index is outside its vertex buffer.");
                            used[vertex] = true;
                        }
                // Empty draw outputs are handled by object/geometry collection. Keep their
                // morph interface here, rather than creating unsupported zero-vertex frames.
                Vertices = !trim || !used.Any(v => v) ? Enumerable.Range(0, used.Length).ToArray() :
                    Enumerable.Range(0, used.Length).Where(v => used[v]).ToArray();
                Remap = Enumerable.Repeat(-1, used.Length).ToArray();
                for (int i = 0; i < Vertices.Length; i++) Remap[Vertices[i]] = i;
            }
        }

        internal static bool HasUnusedData(SkinnedMeshRenderer renderer)
        {
            var input = new Input(renderer, true);
            if (input.Vertices.Length < input.Mesh.vertexCount) return true;
            using var weights = input.Mesh.GetAllBoneWeights();
            var poses = input.Mesh.bindposes; var bones = renderer.bones;
            var used = new HashSet<(Transform, Matrix4x4)>();
            foreach (var weight in weights) used.Add((bones[weight.boneIndex], poses[weight.boneIndex]));
            return used.Count < poses.Length || used.Count < bones.Length;
        }

        internal static string Validate(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer != null ? renderer.sharedMesh : null;
            string geometry = ValidateGeometry(renderer, mesh);
            if (geometry != null) return geometry;
            var poses = mesh.bindposes; var bones = renderer.bones;
            using var counts = mesh.GetBonesPerVertex();
            using var weights = mesh.GetAllBoneWeights();
            if (counts.Length != mesh.vertexCount || counts.Any(c => c == 0)) return "unweighted vertices require a separate rigid-mesh conversion";
            if (counts.Sum(c => (int)c) != weights.Length) return "invalid skinning influence layout";
            foreach (var weight in weights)
                if (weight.boneIndex < 0 || weight.boneIndex >= poses.Length || weight.boneIndex >= bones.Length ||
                    bones[weight.boneIndex] == null || !Finite(weight.weight) || weight.weight <= 0) return "invalid skinning influence";
            foreach (var pose in poses)
                for (int i = 0; i < 16; i++) if (!Finite(pose[i])) return "non-finite bind pose";
            return null;
        }

        internal static string ValidateGeometry(Renderer renderer, Mesh mesh)
        {
            if (mesh == null || mesh.vertexCount == 0) return "no mesh vertices";
            if (!mesh.isReadable) return "mesh vertex data is not readable";
            if (renderer.sharedMaterials.Length != mesh.subMeshCount) return "material slots do not match submeshes";
            if (Enumerable.Range(0, mesh.subMeshCount).Any(s => mesh.GetTopology(s) != MeshTopology.Triangles)) return "non-triangle topology";
            if (mesh.GetVertexAttributes().Any(a => a.attribute != VertexAttribute.BlendIndices && a.attribute != VertexAttribute.BlendWeight &&
                !(a.format == VertexAttributeFormat.Float32 || a.format == VertexAttributeFormat.Float16 ||
                  a.attribute == VertexAttribute.Color && a.format == VertexAttributeFormat.UNorm8))) return "unsupported vertex attribute encoding";
            for (int channel = 0; channel < 8; channel++)
                if (mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0 + channel) == 1) return "one-dimensional UV data";
            return null;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static SkinnedMeshAssembly Create(IReadOnlyList<SkinnedMeshRenderer> sources, bool mergeMaterialSlots,
            IEnumerable<string> reservedShapeNames = null, bool removeUnusedData = false,
            IReadOnlyDictionary<SkinnedMeshRenderer, StaticSkinningPlan> skinning = null,
            ISet<SkinnedMeshRenderer> reverseWinding = null, bool reorderMaterialSlots = false)
        {
            if (sources == null || sources.Count == 0 || sources.Distinct().Count() != sources.Count)
                throw new ArgumentException("Mesh assembly requires unique source renderers.");
            foreach (var source in sources)
                if (Validate(source) is string reason) throw new ArgumentException((source != null ? source.name : "Missing renderer") + ": " + reason);
            bool normals = sources[0].sharedMesh.HasVertexAttribute(VertexAttribute.Normal);
            if (sources.Any(r => r.sharedMesh.HasVertexAttribute(VertexAttribute.Normal) != normals))
                throw new ArgumentException("Mesh assembly requires consistent normal attribute presence.");
            var inputs = sources.Select(r => new Input(r, removeUnusedData)).ToArray();
            int vertexCount = checked(inputs.Sum(i => i.Vertices.Length));
            var result = new SkinnedMeshAssembly { Mesh = new Mesh { name = sources[0].sharedMesh.name + " (Merged)",
                indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 } };
            try
            {
                CopyAttributes(inputs, result.Mesh, vertexCount, normals, reverseWinding);
                CopySkinning(inputs, result, vertexCount, skinning);
                CopyTopology(inputs, result, mergeMaterialSlots, reverseWinding, reorderMaterialSlots);
                CopyShapes(inputs, result, vertexCount, reservedShapeNames);
                result.Mesh.RecalculateBounds();
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private static void CopyAttributes(IReadOnlyList<Input> sources, Mesh output, int count, bool normals,
            ISet<SkinnedMeshRenderer> reverseWinding)
        {
            var positions = new List<Vector3>(count); var normalData = new List<Vector3>(normals ? count : 0);
            bool tangents = sources.Any(r => r.Mesh.HasVertexAttribute(VertexAttribute.Tangent));
            bool colors = sources.Any(r => r.Mesh.HasVertexAttribute(VertexAttribute.Color));
            var tangentData = new List<Vector4>(tangents ? count : 0); var colorData = new List<Color>(colors ? count : 0);
            var uvDimensions = Enumerable.Range(0, 8).Select(c => sources.Max(r => r.Mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0 + c))).ToArray();
            var uvData = uvDimensions.Select(d => d == 0 ? null : new List<Vector4>(count)).ToArray();
            foreach (var input in sources)
            {
                var mesh = input.Mesh; var sourcePositions = mesh.vertices;
                positions.AddRange(input.Vertices.Select(v => sourcePositions[v]));
                if (normals) { var values = mesh.normals; normalData.AddRange(input.Vertices.Select(v => values[v])); }
                if (tangents)
                {
                    var values = mesh.tangents;
                    var selected = values.Length > 0 ? input.Vertices.Select(v => values[v]) : Enumerable.Repeat(new Vector4(1, 0, 0, 1), input.Vertices.Length);
                    // The shader multiplies tangent.w by the renderer matrix sign.
                    // Preserve the world bitangent when changing that coordinate root.
                    if (reverseWinding?.Contains(input.Renderer) == true) selected = selected.Select(v => new Vector4(v.x, v.y, v.z, -v.w));
                    tangentData.AddRange(selected);
                }
                if (colors)
                {
                    var values = mesh.colors;
                    colorData.AddRange(values.Length > 0 ? input.Vertices.Select(v => values[v]) : Enumerable.Repeat(Color.white, input.Vertices.Length));
                }
                var previous = Enumerable.Repeat(Vector4.zero, mesh.vertexCount).ToList();
                for (int channel = 0; channel < 8; channel++)
                {
                    if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0 + channel))
                    {
                        var current = new List<Vector4>(mesh.vertexCount); mesh.GetUVs(channel, current); previous = current;
                    }
                    // Unity aliases a missing UV channel to the preceding available channel.
                    // This convention is also handled explicitly by AAO's MeshInfo2 reader.
                    uvData[channel]?.AddRange(input.Vertices.Select(v => previous[v]));
                }
            }
            output.SetVertices(positions);
            if (normals) output.SetNormals(normalData);
            if (tangents) output.SetTangents(tangentData);
            if (colors) output.SetColors(colorData);
            for (int channel = 0; channel < 8; channel++)
            {
                var values = uvData[channel]; if (values == null) continue;
                if (uvDimensions[channel] == 2) output.SetUVs(channel, values.Select(v => new Vector2(v.x, v.y)).ToList());
                else if (uvDimensions[channel] == 3) output.SetUVs(channel, values.Select(v => new Vector3(v.x, v.y, v.z)).ToList());
                else output.SetUVs(channel, values);
            }
        }

        private static void CopySkinning(IReadOnlyList<Input> sources, SkinnedMeshAssembly output, int count,
            IReadOnlyDictionary<SkinnedMeshRenderer, StaticSkinningPlan> skinning)
        {
            var palette = new Dictionary<(Transform Bone, Matrix4x4 Pose), int>();
            var bones = new List<Transform>(); var poses = new List<Matrix4x4>();
            var counts = new List<byte>(count); var weights = new List<BoneWeight1>();
            foreach (var input in sources)
            {
                var renderer = input.Renderer;
                var sourcePoses = renderer.sharedMesh.bindposes; var sourceBones = renderer.bones;
                if (skinning != null && skinning.TryGetValue(renderer, out var plan))
                { sourcePoses = plan.Poses; sourceBones = plan.Bones; }
                using var sourceCounts = renderer.sharedMesh.GetBonesPerVertex();
                using var sourceWeights = renderer.sharedMesh.GetAllBoneWeights();
                int cursor = 0;
                for (int vertex = 0; vertex < sourceCounts.Length; vertex++)
                {
                    if (input.Remap[vertex] >= 0)
                    {
                        counts.Add(sourceCounts[vertex]);
                        for (int influence = 0; influence < sourceCounts[vertex]; influence++)
                        {
                            var sourceWeight = sourceWeights[cursor + influence];
                            var key = (sourceBones[sourceWeight.boneIndex], sourcePoses[sourceWeight.boneIndex]);
                            if (!palette.TryGetValue(key, out int index))
                            { palette.Add(key, index = bones.Count); bones.Add(key.Item1); poses.Add(key.Item2); }
                            weights.Add(new BoneWeight1 { boneIndex = index, weight = sourceWeight.weight });
                        }
                    }
                    cursor += sourceCounts[vertex];
                }
            }
            output.Mesh.bindposes = poses.ToArray(); output.Bones = bones.ToArray();
            using var nativeCounts = new NativeArray<byte>(counts.ToArray(), Allocator.Temp);
            using var nativeWeights = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp);
            output.Mesh.SetBoneWeights(nativeCounts, nativeWeights);
        }

        private static void CopyTopology(IReadOnlyList<Input> sources, SkinnedMeshAssembly output, bool merge,
            ISet<SkinnedMeshRenderer> reverseWinding, bool reorder)
        {
            var materials = new List<Material>(); var indices = new List<List<int>>();
            var firstSlot = new Dictionary<Material, int>();
            int offset = 0;
            foreach (var input in sources)
            {
                var renderer = input.Renderer;
                var mesh = renderer.sharedMesh; var sourceMaterials = renderer.sharedMaterials;
                var slots = new int[sourceMaterials.Length]; output.MaterialSlots.Add(renderer, slots);
                for (int submesh = 0; submesh < slots.Length; submesh++)
                {
                    var material = sourceMaterials[submesh];
                    // Default merging keeps material order. The explicit reorder policy
                    // groups by first occurrence, retaining the first material interface.
                    // Behavior reference: AAO 1.9.20 MergeMaterialSlots; see CREDITS.md.
                    int slot = -1;
                    if (merge && material != null)
                    {
                        if (reorder) { if (!firstSlot.TryGetValue(material, out slot)) slot = -1; }
                        else if (materials.Count > 0 && materials[materials.Count - 1] == material) slot = materials.Count - 1;
                    }
                    if (slot < 0)
                    {
                        slot = materials.Count; materials.Add(material); indices.Add(new());
                        if (material != null && !firstSlot.ContainsKey(material)) firstSlot.Add(material, slot);
                    }
                    slots[submesh] = slot;
                    var sourceIndices = mesh.GetIndices(submesh, applyBaseVertex: true);
                    if (reverseWinding?.Contains(renderer) == true)
                        for (int triangle = 0; triangle < sourceIndices.Length; triangle += 3)
                            (sourceIndices[triangle], sourceIndices[triangle + 1]) = (sourceIndices[triangle + 1], sourceIndices[triangle]);
                    foreach (int index in sourceIndices)
                    {
                        if (index < 0 || index >= mesh.vertexCount) throw new ArgumentException("Submesh index is outside its vertex buffer.");
                        indices[slot].Add(checked(input.Remap[index] + offset));
                    }
                }
                offset += input.Vertices.Length;
            }
            output.Materials = materials.ToArray(); output.Mesh.subMeshCount = materials.Count;
            for (int slot = 0; slot < indices.Count; slot++) output.Mesh.SetTriangles(indices[slot], slot, calculateBounds: false);
        }

        private static void CopyShapes(IReadOnlyList<Input> sources, SkinnedMeshAssembly output, int count, IEnumerable<string> reserved)
        {
            var names = new HashSet<string>(reserved ?? Array.Empty<string>(), StringComparer.Ordinal); var weights = new List<float>();
            var vertices = new Vector3[count]; var normals = new Vector3[count]; var tangents = new Vector3[count];
            int offset = 0;
            for (int input = 0; input < sources.Count; input++)
            {
                var source = sources[input]; var renderer = source.Renderer; var mesh = renderer.sharedMesh;
                var mapping = new Dictionary<string, string>(StringComparer.Ordinal); output.ShapeNames.Add(renderer, mapping);
                var dv = new Vector3[mesh.vertexCount]; var dn = new Vector3[mesh.vertexCount]; var dt = new Vector3[mesh.vertexCount];
                for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                {
                    string sourceName = mesh.GetBlendShapeName(shape);
                    string name = input == 0 ? sourceName : "__AdvancedMerged_" + input + "_" + sourceName;
                    while (!names.Add(name)) name = "_" + name;
                    mapping.Add(sourceName, name); weights.Add(renderer.GetBlendShapeWeight(shape));
                    for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
                    {
                        mesh.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);
                        for (int vertex = 0; vertex < source.Vertices.Length; vertex++)
                        {
                            int original = source.Vertices[vertex];
                            vertices[offset + vertex] = dv[original]; normals[offset + vertex] = dn[original]; tangents[offset + vertex] = dt[original];
                        }
                        output.Mesh.AddBlendShapeFrame(name, mesh.GetBlendShapeFrameWeight(shape, frame), vertices, normals, tangents);
                    }
                }
                Array.Clear(vertices, offset, source.Vertices.Length); Array.Clear(normals, offset, source.Vertices.Length); Array.Clear(tangents, offset, source.Vertices.Length);
                offset += source.Vertices.Length;
            }
            output.ShapeWeights = weights.ToArray();
        }
    }
}
