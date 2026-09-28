using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Editor
{
    internal sealed class PoseOverrideResult : IDisposable
    {
        internal Mesh Source;
        internal Mesh Mesh;
        internal float[] DefaultWeights;
        internal int[] TargetIndices;

        internal void Apply(SkinnedMeshRenderer renderer)
        {
            renderer.sharedMesh = Mesh;
            for (int i = 0; i < DefaultWeights.Length; i++) renderer.SetBlendShapeWeight(i, DefaultWeights[i]);
        }

        public void Dispose()
        {
            if (Mesh != null) Object.DestroyImmediate(Mesh);
        }
    }

    internal static class PoseOverrideProcessor
    {
        internal static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static PoseOverrideResult Generate(SkinnedMeshRenderer renderer, IReadOnlyList<ResolvedPose> poses)
        {
            if (renderer == null || renderer.sharedMesh == null)
                throw new InvalidOperationException("Target Mesh must have a mesh.");
            var source = renderer.sharedMesh;
            if (source.vertexCount == 0 || poses == null || poses.Count == 0)
                throw new InvalidOperationException("A nonempty mesh and at least one override are required.");

            var current = new Dictionary<string, float>(StringComparer.Ordinal);
            var defaultWeights = new float[source.blendShapeCount];
            for (int i = 0; i < source.blendShapeCount; i++)
            {
                float weight = renderer.GetBlendShapeWeight(i);
                if (!IsFinite(weight)) throw new InvalidOperationException("Current blendshape weights must be finite.");
                defaultWeights[i] = weight;
                current.Add(source.GetBlendShapeName(i), weight);
            }
            var targetNames = new HashSet<string>(StringComparer.Ordinal);
            var sampledNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pose in poses)
            {
                if (pose == null || string.IsNullOrEmpty(pose.ShapeName) || !current.ContainsKey(pose.ShapeName))
                    throw new InvalidOperationException("An override target does not exist on Target Mesh.");
                if (!targetNames.Add(pose.ShapeName))
                    throw new InvalidOperationException($"Duplicate override target: '{pose.ShapeName}'.");
                foreach (var pair in pose.Weights)
                {
                    if (!current.ContainsKey(pair.Key) || !IsFinite(pair.Value))
                        throw new InvalidOperationException($"Invalid pose value for '{pair.Key}'.");
                    sampledNames.Add(pair.Key);
                }
            }

            var replacements = new Dictionary<string, Sample>(StringComparer.Ordinal);
            // Unity evaluates all original frames, including nonstandard frame weights and extrapolation.
            // Sampling only keyed shapes is sufficient: all unkeyed contributions cancel in the difference.
            using (var sampler = new ShapeSampler(source, sampledNames))
            {
                var basis = sampler.Evaluate(current);
                foreach (var pose in poses)
                {
                    var desiredWeights = new Dictionary<string, float>(current, StringComparer.Ordinal);
                    foreach (var pair in pose.Weights) desiredWeights[pair.Key] = pair.Value;
                    var delta = sampler.Evaluate(desiredWeights);
                    delta.Subtract(basis);
                    replacements.Add(pose.ShapeName, delta);
                }
            }

            Sample preserved;
            // Old contributions from ALL replaced shapes must remain in the neutral mesh. New shapes start at 0.
            using (var sampler = new ShapeSampler(source, targetNames)) preserved = sampler.Evaluate(current);

            Mesh output = null;
            try
            {
                output = Object.Instantiate(source);
                output.name = source.name + " (Pose Overrides)";
                var vertices = source.vertices;
                var normals = source.normals;
                var tangents = source.tangents;
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i] += preserved.Vertices[i];
                    if (normals.Length == vertices.Length) normals[i] += preserved.Normals[i];
                    if (tangents.Length == vertices.Length)
                    {
                        var t = preserved.Tangents[i];
                        tangents[i] += new Vector4(t.x, t.y, t.z, 0);
                    }
                }
                output.vertices = vertices;
                if (normals.Length == vertices.Length) output.normals = normals;
                if (tangents.Length == vertices.Length) output.tangents = tangents;
                output.ClearBlendShapes();
                var dv = new Vector3[source.vertexCount];
                var dn = new Vector3[source.vertexCount];
                var dt = new Vector3[source.vertexCount];
                for (int shape = 0; shape < source.blendShapeCount; shape++)
                {
                    string name = source.GetBlendShapeName(shape);
                    if (replacements.TryGetValue(name, out var replacement))
                    {
                        output.AddBlendShapeFrame(name, 100, replacement.Vertices, replacement.Normals, replacement.Tangents);
                        defaultWeights[shape] = 0;
                    }
                    else
                    {
                        for (int frame = 0; frame < source.GetBlendShapeFrameCount(shape); frame++)
                        {
                            source.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);
                            output.AddBlendShapeFrame(name, source.GetBlendShapeFrameWeight(shape, frame), dv, dn, dt);
                        }
                    }
                }
                output.RecalculateBounds();
                var bounds = output.bounds;
                bounds.Encapsulate(source.bounds);
                output.bounds = bounds;
                return new PoseOverrideResult
                {
                    Source = source, Mesh = output, DefaultWeights = defaultWeights,
                    TargetIndices = poses.Select(p => source.GetBlendShapeIndex(p.ShapeName)).ToArray()
                };
            }
            catch
            {
                if (output != null) Object.DestroyImmediate(output);
                throw;
            }
        }

        private sealed class Sample
        {
            internal Vector3[] Vertices;
            internal Vector3[] Normals;
            internal Vector3[] Tangents;

            internal void Subtract(Sample basis)
            {
                for (int i = 0; i < Vertices.Length; i++)
                {
                    Vertices[i] -= basis.Vertices[i];
                    Normals[i] -= basis.Normals[i];
                    Tangents[i] -= basis.Tangents[i];
                    Check(Vertices[i]);
                    Check(Normals[i]);
                    Check(Tangents[i]);
                }
            }

            internal static void Check(Vector3 value)
            {
                if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z))
                    throw new InvalidOperationException("The mesh produced non-finite blendshape deformation. Check its frame weights and pose values.");
            }
        }

        private sealed class ShapeSampler : IDisposable
        {
            private Scene scene;
            private Mesh mesh;
            private Mesh baked;
            private SkinnedMeshRenderer renderer;
            private readonly string[] names;

            internal ShapeSampler(Mesh source, IEnumerable<string> shapes)
            {
                // Retain mesh order so floating point accumulation is independent of list order.
                names = shapes.OrderBy(source.GetBlendShapeIndex).ToArray();
                try
                {
                    scene = EditorSceneManager.NewPreviewScene();
                    var go = new GameObject("BlendShape Pose Sampler") { hideFlags = HideFlags.HideAndDontSave };
                    SceneManager.MoveGameObjectToScene(go, scene);
                    renderer = go.AddComponent<SkinnedMeshRenderer>();
                    renderer.updateWhenOffscreen = true;
                    mesh = new Mesh { name = "BlendShape Pose Sample", hideFlags = HideFlags.HideAndDontSave, indexFormat = source.indexFormat };
                    mesh.vertices = new Vector3[source.vertexCount];
                    mesh.normals = new Vector3[source.vertexCount];
                    mesh.tangents = Enumerable.Repeat(new Vector4(0, 0, 0, 1), source.vertexCount).ToArray();
                    if (source.vertexCount >= 3) mesh.triangles = new[] { 0, 1, 2 };
                    var dv = new Vector3[source.vertexCount];
                    var dn = new Vector3[source.vertexCount];
                    var dt = new Vector3[source.vertexCount];
                    foreach (string name in names)
                    {
                        int index = source.GetBlendShapeIndex(name);
                        for (int frame = 0; frame < source.GetBlendShapeFrameCount(index); frame++)
                        {
                            source.GetBlendShapeFrameVertices(index, frame, dv, dn, dt);
                            mesh.AddBlendShapeFrame(name, source.GetBlendShapeFrameWeight(index, frame), dv, dn, dt);
                        }
                    }
                    renderer.sharedMesh = mesh;
                    baked = new Mesh { name = "BlendShape Pose Result", hideFlags = HideFlags.HideAndDontSave };
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            internal Sample Evaluate(IReadOnlyDictionary<string, float> weights)
            {
                for (int i = 0; i < names.Length; i++) renderer.SetBlendShapeWeight(i, weights[names[i]]);
                // No bones or transforms: capture raw local deltas, without the avatar's current skeletal pose.
                renderer.BakeMesh(baked, true);
                var sample = new Sample
                {
                    Vertices = baked.vertices, Normals = baked.normals,
                    Tangents = baked.tangents.Select(t => new Vector3(t.x, t.y, t.z)).ToArray()
                };
                for (int i = 0; i < sample.Vertices.Length; i++)
                {
                    Sample.Check(sample.Vertices[i]);
                    Sample.Check(sample.Normals[i]);
                    Sample.Check(sample.Tangents[i]);
                }
                return sample;
            }

            public void Dispose()
            {
                if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
                if (mesh != null) Object.DestroyImmediate(mesh);
                if (baked != null) Object.DestroyImmediate(baked);
            }
        }
    }
}
