using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Meshes
{
    internal sealed class VertexIdMergePlan : IDisposable
    {
        internal SkinnedMeshRenderer[] VertexOrder;
        internal readonly HashSet<SkinnedMeshRenderer> KeepVertices = new();
        internal readonly Dictionary<SkinnedMeshRenderer, Material[]> Materials = new();
        internal readonly HashSet<SkinnedMeshRenderer> AttributeInputs = new();
        internal int AttributeChannel = -1;
        internal long RetainedBytes, AttributeBytes;
        internal int LiteralShaders, UniformShaders;
        private readonly List<Object> generated = new();
        private readonly Dictionary<(SkinnedMeshRenderer, Material), Material> remappedMaterials = new();
        private bool saved;

        internal Material RemapMaterial(SkinnedMeshRenderer renderer, Material material) => material != null &&
            remappedMaterials.TryGetValue((renderer, material), out var replacement) ? replacement : material;

        internal static VertexIdMergePlan Create(IReadOnlyList<MeshMergeCandidate> group, bool trim, int uniformThreshold, BuildTarget? target = null)
        {
            var plan = new VertexIdMergePlan();
            try
            {
                var sensitive = group.Where(c => c.VertexIdMaterials.Count != 0).ToArray();
                var first = sensitive.OrderBy(c => c.VertexIdAdapters.Count == c.VertexIdMaterials.Select(m => m.shader).Distinct().Count() ? 1 : 0)
                    .ThenByDescending(c => c.Renderer.sharedMesh.vertexCount).FirstOrDefault();
                plan.VertexOrder = (first == null ? group : new[] { first }.Concat(group.Where(c => c != first)))
                    .Select(c => c.Renderer).ToArray();
                foreach (var candidate in sensitive) plan.KeepVertices.Add(candidate.Renderer);

                // The attribute is an alternative to retaining holes, not a mandatory
                // remap table. Account for the entire merged vertex stream it widens.
                if (trim)
                {
                    var compactable = sensitive.Where(c => c.VertexIdAdapters.Count == c.VertexIdMaterials.Select(m => m.shader).Distinct().Count() &&
                        c.Renderer.sharedMesh.vertexCount <= 16777216 && UsedCount(c.Renderer.sharedMesh) < c.Renderer.sharedMesh.vertexCount).ToArray();
                    long savings = compactable.Sum(c => RemovedVertexBytes(c.Renderer.sharedMesh));
                    long vertices = group.Sum(c => (long)(sensitive.Contains(c) && !compactable.Contains(c) ? c.Renderer.sharedMesh.vertexCount : UsedCount(c.Renderer.sharedMesh)));
                    long cost = vertices * 8; // A portable float2 UV stream, without half precision or compression.
                    int channel = savings > cost && compactable.Length != 0 ? SpareChannel(group, target ?? EditorUserBuildSettings.activeBuildTarget) : -1;
                    if (channel >= 0)
                    {
                        plan.AttributeChannel = channel; plan.AttributeBytes = cost;
                        foreach (var candidate in compactable)
                        { plan.AttributeInputs.Add(candidate.Renderer); plan.KeepVertices.Remove(candidate.Renderer); }
                    }
                    else plan.RetainedBytes = savings;
                }
                var offsets = new Dictionary<SkinnedMeshRenderer, int>(); int cursor = 0;
                foreach (var renderer in plan.VertexOrder)
                {
                    offsets.Add(renderer, cursor);
                    cursor = checked(cursor + (trim && !plan.KeepVertices.Contains(renderer) ? UsedCount(renderer.sharedMesh) : renderer.sharedMesh.vertexCount));
                }
                var modes = sensitive.SelectMany(c => c.VertexIdMaterials.Select(m => new { Shader = m.shader, Renderer = c.Renderer }))
                    .Where(x => !plan.AttributeInputs.Contains(x.Renderer)).GroupBy(x => x.Shader)
                    .ToDictionary(g => g.Key, g => g.Select(x => offsets[x.Renderer]).Distinct().Count() >= Math.Max(2, uniformThreshold));
                var shaders = new Dictionary<(Shader, int Offset, bool Uniform, int Attribute), Shader>();
                var materials = new Dictionary<(Material, Shader, int), Material>();
                foreach (var candidate in sensitive)
                {
                    var renderer = candidate.Renderer; int offset = -offsets[renderer];
                    int attribute = plan.AttributeInputs.Contains(renderer) ? plan.AttributeChannel : -1;
                    // Include materials that appear only in animation keys. A
                    // later swap must not restore an unadapted shader interface.
                    foreach (var original in candidate.VertexIdMaterials)
                    {
                        bool uniform = attribute < 0 && modes[original.shader];
                        if (uniform && (candidate.Properties.ContainsKey("material." + ShaderVertexIdRewriter.OffsetProperty) ||
                            candidate.MaterialContacts.SelectMany(ContactOutputs.Properties).Contains(ShaderVertexIdRewriter.OffsetProperty)))
                            throw new InvalidOperationException("A material animation writes the reserved vertex-ID offset property");
                        if (offset == 0 && attribute < 0 && !uniform) continue;
                        if (!candidate.VertexIdAdapters.TryGetValue(original.shader, out var adapter))
                        {
                            if (offset == 0 && attribute < 0) continue;
                            throw new InvalidOperationException("No verified vertex-ID source adapter for " + original.shader.name);
                        }
                        var key = (original.shader, uniform || attribute >= 0 ? 0 : offset, uniform, attribute);
                        if (!shaders.TryGetValue(key, out var shader))
                        {
                            shader = adapter.CreateShader(key.Item2, uniform, attribute);
                            shaders.Add(key, shader); plan.generated.Add(shader);
                            if (uniform) plan.UniformShaders++; else plan.LiteralShaders++;
                        }
                        var materialKey = (original, shader, uniform ? offset : 0);
                        if (!materials.TryGetValue(materialKey, out var material))
                        {
                            material = new Material(original) { name = original.name + " (Vertex ID Preserved)", shader = shader };
                            material.shaderKeywords = original.shaderKeywords;
                            if (uniform) material.SetInteger(ShaderVertexIdRewriter.OffsetProperty, offset);
                            materials.Add(materialKey, material); plan.generated.Add(material);
                        }
                        plan.remappedMaterials.Add((renderer, original), material);
                    }
                    plan.Materials.Add(renderer, renderer.sharedMaterials.Select(m => plan.RemapMaterial(renderer, m)).ToArray());
                }
                return plan;
            }
            catch { plan.Dispose(); throw; }
        }

        internal static int UsedCount(Mesh mesh)
        {
            var used = new HashSet<int>();
            for (int i = 0; i < mesh.subMeshCount; i++) used.UnionWith(mesh.GetIndices(i, true));
            return used.Count == 0 ? mesh.vertexCount : used.Count;
        }

        private static long RemovedVertexBytes(Mesh mesh)
        {
            long stride = 0;
            for (int stream = 0; stream < mesh.vertexBufferCount; stream++) stride += mesh.GetVertexBufferStride(stream);
            var used = new HashSet<int>();
            for (int slot = 0; slot < mesh.subMeshCount; slot++) used.UnionWith(mesh.GetIndices(slot, true));
            if (used.Count == 0) return 0;
            var removed = Enumerable.Range(0, mesh.vertexCount).Where(v => !used.Contains(v)).ToArray();
            long bytes = removed.Length * stride;
            // Count nonzero removed deltas; sparse morphs do not pay for every
            // zero vertex. The vertex-index entry accompanies each stored delta.
            if (mesh.blendShapeCount != 0)
            {
                var positions = new Vector3[mesh.vertexCount]; var normals = new Vector3[mesh.vertexCount]; var tangents = new Vector3[mesh.vertexCount];
                for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                    for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
                    {
                        mesh.GetBlendShapeFrameVertices(shape, frame, positions, normals, tangents);
                        foreach (int vertex in removed)
                            if (positions[vertex] != Vector3.zero || normals[vertex] != Vector3.zero || tangents[vertex] != Vector3.zero) bytes += 40;
                    }
            }
            return bytes;
        }

        private static int SpareChannel(IReadOnlyList<MeshMergeCandidate> group, BuildTarget target)
        {
            var present = Enumerable.Range(0, 8).Select(channel =>
                group.Any(c => c.Renderer.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord0 + channel))).ToArray();
            for (int channel = 7; channel >= 1; channel--)
            {
                if (present[channel]) continue;
                // Unity aliases absent UV channels to the preceding available one.
                // Adding this stream also changes every following absent channel,
                // until UvLayout emits the next explicit stream from an input.
                var affected = new List<ShaderInputQuery.Semantic>();
                for (int next = channel; next < 8 && !present[next]; next++)
                    affected.Add(new ShaderInputQuery.Semantic("TEXCOORD", (uint)next));
                var query = new ShaderInputQuery(affected.ToArray());
                if (group.Any(c => c.PossibleMaterials.Any(m => m == null ||
                    (c.Shaders ?? new ShaderPropertyUsage()).MeshInputs.Issue(m, c.Renderer, c.Animations, query, target) != null))) continue;
                return channel;
            }
            return -1;
        }

        internal void Save(Action<Object> save)
        {
            foreach (var asset in generated) save?.Invoke(asset);
            saved = true;
        }
        public void Dispose()
        {
            if (!saved) foreach (var asset in generated.AsEnumerable().Reverse()) if (asset != null) Object.DestroyImmediate(asset);
            generated.Clear();
        }
    }
}
