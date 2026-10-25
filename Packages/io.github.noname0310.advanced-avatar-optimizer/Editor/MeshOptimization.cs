using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization
{
    internal static class ContentHash
    {
        internal static string Text(string value) => Compute(w => w.Write(value));

        internal static string Compute(Action<BinaryWriter> write)
        {
            using var hash = SHA256.Create();
            using (var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                write(writer);
                writer.Flush();
                stream.FlushFinalBlock();
            }
            return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
        }
    }

    internal sealed class ShapeDelta
    {
        internal readonly Vector3[] Vertices, Normals, Tangents;
        internal ShapeDelta(int count)
        {
            Vertices = new Vector3[count]; Normals = new Vector3[count]; Tangents = new Vector3[count];
        }
        internal void Read(Mesh mesh, int shape, int frame)
            => mesh.GetBlendShapeFrameVertices(shape, frame, Vertices, Normals, Tangents);
        internal void Add(ShapeDelta other, float weight)
        {
            if (weight == 0) return;
            for (int i = 0; i < Vertices.Length; i++)
            {
                Vertices[i] += other.Vertices[i] * weight;
                Normals[i] += other.Normals[i] * weight;
                Tangents[i] += other.Tangents[i] * weight;
            }
        }
    }

    internal sealed class DeformationRecipe
    {
        internal ShapeDelta Frozen;
        internal ShapeDelta[] Groups;
    }

    internal static class MeshOptimization
    {
        internal static DeformationRecipe CreateRecipe(OptimizationPlan plan)
        {
            var result = new DeformationRecipe { Frozen = new ShapeDelta(plan.Source.vertexCount),
                Groups = plan.Groups.Select(_ => new ShapeDelta(plan.Source.vertexCount)).ToArray() };
            var buffer = new ShapeDelta(plan.Source.vertexCount);
            foreach (var shape in plan.Frozen) AddAtWeight(plan.Source, shape.Index, shape.Weight, result.Frozen, buffer);
            for (int i = 0; i < plan.Groups.Count; i++)
                foreach (var shape in plan.Groups[i].Inputs)
                    AddAtWeight(plan.Source, shape.Index, shape.Weight, result.Groups[i], buffer);
            return result;
        }

        internal static void AddAtWeight(Mesh mesh, int shape, float weight, ShapeDelta destination, ShapeDelta buffer)
        {
            if (weight == 0) return;
            int count = mesh.GetBlendShapeFrameCount(shape);
            // Unity has an implicit zero frame when no explicit frame exists at zero.
            var frames = new List<(float weight, int index)> { (0, -1) };
            for (int i = 0; i < count; i++)
            {
                float frameWeight = mesh.GetBlendShapeFrameWeight(shape, i);
                if (frameWeight == 0) frames.RemoveAll(x => x.index == -1);
                frames.Add((frameWeight, i));
            }
            frames.Sort((a, b) => a.weight.CompareTo(b.weight));
            if (frames.Count < 2) throw new InvalidOperationException("A blendshape has no usable frame range.");
            int right = 1;
            while (right < frames.Count - 1 && weight > frames[right].weight) right++;
            var a = frames[right - 1]; var b = frames[right];
            float t = (weight - a.weight) / (b.weight - a.weight);
            if (a.index >= 0) { buffer.Read(mesh, shape, a.index); destination.Add(buffer, 1 - t); }
            if (b.index >= 0) { buffer.Read(mesh, shape, b.index); destination.Add(buffer, t); }
        }

        internal static Mesh Generate(OptimizationPlan plan, DeformationRecipe recipe)
        {
            var mesh = UnityEngine.Object.Instantiate(plan.Source);
            mesh.name = plan.Source.name + " (BlendShapes Optimized)";
            try
            {
                var vertices = mesh.vertices; var normals = mesh.normals; var tangents = mesh.tangents;
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i] += recipe.Frozen.Vertices[i];
                    if (normals.Length == vertices.Length) normals[i] += recipe.Frozen.Normals[i];
                    if (tangents.Length == vertices.Length)
                    {
                        var delta = recipe.Frozen.Tangents[i];
                        tangents[i] += new Vector4(delta.x, delta.y, delta.z, 0);
                    }
                }
                mesh.vertices = vertices;
                if (normals.Length != 0) mesh.normals = normals;
                if (tangents.Length != 0) mesh.tangents = tangents;
                // Rebuild the actual frame buffers: removing only serialized channel records leaves dead vertex data.
                mesh.ClearBlendShapes();
                var removed = new HashSet<int>(plan.Removed.Select(s => s.Index));
                var buffer = new ShapeDelta(mesh.vertexCount);
                for (int shape = 0; shape < plan.Source.blendShapeCount; shape++)
                {
                    if (removed.Contains(shape)) continue;
                    for (int frame = 0; frame < plan.Source.GetBlendShapeFrameCount(shape); frame++)
                    {
                        buffer.Read(plan.Source, shape, frame);
                        mesh.AddBlendShapeFrame(plan.Source.GetBlendShapeName(shape),
                            plan.Source.GetBlendShapeFrameWeight(shape, frame), buffer.Vertices, buffer.Normals, buffer.Tangents);
                    }
                }
                for (int i = 0; i < plan.Groups.Count; i++)
                {
                    var delta = recipe.Groups[i];
                    mesh.AddBlendShapeFrame(plan.Groups[i].Name, 100, delta.Vertices, delta.Normals, delta.Tangents);
                }
                var bounds = plan.Source.bounds;
                foreach (var vertex in vertices) bounds.Encapsulate(vertex);
                mesh.bounds = bounds;
                return mesh;
            }
            catch { UnityEngine.Object.DestroyImmediate(mesh); throw; }
        }

        internal static void Apply(OptimizationPlan plan, Mesh output)
        {
            var defaults = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int i = 0; i < plan.Source.blendShapeCount; i++)
                defaults[plan.Source.GetBlendShapeName(i)] = plan.Renderer.GetBlendShapeWeight(i);
            foreach (var group in plan.Groups) defaults[group.Name] = group.DefaultWeight;
            plan.Renderer.sharedMesh = output;
            for (int i = 0; i < output.blendShapeCount; i++)
                plan.Renderer.SetBlendShapeWeight(i, defaults[output.GetBlendShapeName(i)]);
        }
    }
}
