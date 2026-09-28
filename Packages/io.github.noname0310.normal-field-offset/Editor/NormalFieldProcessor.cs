using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Editor
{
    internal readonly struct OffsetRequest
    {
        internal readonly SkinnedMeshRenderer Clothing, Body;
        internal readonly float Offset, MaxDistance;

        internal OffsetRequest(SkinnedMeshRenderer clothing, SkinnedMeshRenderer body, float offset, float maxDistance)
        {
            Clothing = clothing;
            Body = body;
            Offset = offset;
            MaxDistance = maxDistance;
        }
    }

    internal sealed class OffsetResult : IDisposable
    {
        internal SkinnedMeshRenderer Renderer;
        internal Mesh Source, Mesh;
        internal Bounds LocalBounds;
        internal int MovedVertices;

        internal void Apply()
        {
            Renderer.sharedMesh = Mesh;
            Renderer.localBounds = LocalBounds;
        }

        public void Dispose()
        {
            if (Mesh != null) Object.DestroyImmediate(Mesh);
        }
    }

    internal static class NormalFieldProcessor
    {
        internal static string Validate(OffsetRequest request)
        {
            if (request.Clothing == null || request.Clothing.sharedMesh == null)
                return "Add Normal Field Offset to the clothing's Skinned Mesh Renderer with a mesh assigned.";
            if (request.Body == null || request.Body.sharedMesh == null)
                return "Assign the avatar's body Skinned Mesh Renderer to Target Body.";
            if (request.Body == request.Clothing)
                return "Target Body must be a different renderer from the clothing.";
            if (!SurfaceNormalField.Finite(request.Offset) || !SurfaceNormalField.Finite(request.MaxDistance) || request.MaxDistance < 0)
                return "Offset must be finite, and Max Distance must be finite and non-negative.";
            return null;
        }

        /// <summary>Generate everything before assigning meshes, so body fields never depend on component order.</summary>
        internal static List<OffsetResult> Generate(IReadOnlyList<OffsetRequest> requests)
        {
            var fields = new Dictionary<SkinnedMeshRenderer, SurfaceNormalField>();
            var outputs = new List<OffsetResult>();
            try
            {
                foreach (var request in requests)
                {
                    var error = Validate(request);
                    if (error != null) throw new InvalidOperationException("Normal Field Offset: " + error);
                    if (request.Offset == 0 || fields.ContainsKey(request.Body)) continue;
                    fields.Add(request.Body, CreateField(request.Body));
                }
                foreach (var request in requests)
                {
                    if (request.Offset == 0) continue;
                    outputs.Add(Generate(request, fields[request.Body]));
                }
                return outputs;
            }
            catch
            {
                foreach (var output in outputs) output.Dispose();
                throw;
            }
        }

        internal static SurfaceNormalField CreateField(SkinnedMeshRenderer body)
        {
            BakeWorld(body, out var positions, out var normals);
            var indices = new List<int>();
            var mesh = body.sharedMesh;
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                if (mesh.GetTopology(submesh) == MeshTopology.Triangles)
                    indices.AddRange(mesh.GetTriangles(submesh));
            return new SurfaceNormalField(positions, normals, indices.ToArray());
        }

        internal static void BakeWorld(SkinnedMeshRenderer renderer, out Vector3[] positions, out Vector3[] normals)
        {
            var baked = new Mesh();
            try
            {
                // true is essential: the baked coordinates are then in renderer-local space,
                // including compensation for the renderer's scale (FBX renderers often use 100).
                renderer.BakeMesh(baked, true);
                if (baked.vertexCount != renderer.sharedMesh.vertexCount)
                    throw new InvalidOperationException($"Normal Field Offset: could not bake '{renderer.name}'.");
                positions = baked.vertices;
                normals = baked.normals;
                if (normals.Length != positions.Length)
                {
                    baked.RecalculateNormals();
                    normals = baked.normals;
                }
                var toWorld = renderer.localToWorldMatrix;
                var normalToWorld = toWorld.inverse.transpose;
                for (int i = 0; i < positions.Length; i++)
                {
                    positions[i] = toWorld.MultiplyPoint3x4(positions[i]);
                    normals[i] = normalToWorld.MultiplyVector(normals[i]).normalized;
                    if (!SurfaceNormalField.Finite(positions[i]))
                        throw new InvalidOperationException($"Normal Field Offset: '{renderer.name}' contains non-finite vertices.");
                }
            }
            finally
            {
                Object.DestroyImmediate(baked);
            }
        }

        private static OffsetResult Generate(OffsetRequest request, SurfaceNormalField field)
        {
            var renderer = request.Clothing;
            var source = renderer.sharedMesh;
            var vertices = source.vertices;
            BakeWorld(renderer, out var posedVertices, out _);
            var inverseSkinning = InverseSkinning(renderer);
            int moved = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (!field.Sample(posedVertices[i], request.MaxDistance, out var normal, out float distance)) continue;
                float strength = request.MaxDistance > 0
                    ? 1f - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(request.MaxDistance * 0.8f, request.MaxDistance, distance))
                    : 1f;
                if (strength == 0) continue;
                var displacement = inverseSkinning[i].MultiplyVector(normal * (request.Offset * strength));
                if (!SurfaceNormalField.Finite(displacement))
                    throw new InvalidOperationException($"Normal Field Offset: invalid skinning at vertex {i} of '{renderer.name}'.");
                vertices[i] += displacement;
                moved++;
            }

            // Clone preserves all submeshes, UV sets, authored normals/tangents, blendshape frames,
            // bindposes and variable-count bone weights. Only base positions change; shape deltas stay relative.
            var output = Object.Instantiate(source);
            try
            {
                output.name = source.name + " (Normal Field Offset)";
                output.vertices = vertices;
                output.RecalculateBounds();
                var meshBounds = output.bounds;
                meshBounds.Encapsulate(source.bounds);
                output.bounds = meshBounds;

                // SMR bounds use the root bone's space. Keep existing animation bounds and expand them.
                var boundsSpace = renderer.rootBone != null ? renderer.rootBone : renderer.transform;
                var inverse = boundsSpace.worldToLocalMatrix;
                var expansion = new Vector3(
                    new Vector3(inverse.m00, inverse.m01, inverse.m02).magnitude,
                    new Vector3(inverse.m10, inverse.m11, inverse.m12).magnitude,
                    new Vector3(inverse.m20, inverse.m21, inverse.m22).magnitude) * (2 * Mathf.Abs(request.Offset));
                var bounds = renderer.localBounds;
                bounds.Expand(expansion);
                return new OffsetResult { Renderer = renderer, Source = source, Mesh = output, LocalBounds = bounds, MovedVertices = moved };
            }
            catch
            {
                Object.DestroyImmediate(output);
                throw;
            }
        }

        private static Matrix4x4[] InverseSkinning(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer.sharedMesh;
            var bones = renderer.bones;
            var bindposes = mesh.bindposes;
            var result = new Matrix4x4[mesh.vertexCount];
            var boneMatrices = new Matrix4x4[bindposes.Length];
            for (int i = 0; i < boneMatrices.Length; i++)
                if (i < bones.Length && bones[i] != null)
                    boneMatrices[i] = bones[i].localToWorldMatrix * bindposes[i];

            using var counts = mesh.GetBonesPerVertex();
            using var weights = mesh.GetAllBoneWeights();
            int cursor = 0;
            int limit = renderer.quality == SkinQuality.Auto ? (int)QualitySettings.skinWeights : (int)renderer.quality;
            if (limit <= 0) limit = 255;
            for (int vertex = 0; vertex < result.Length; vertex++)
            {
                int count = counts.Length == result.Length ? counts[vertex] : 0;
                var matrix = Matrix4x4.zero;
                float total = 0;
                for (int j = 0; j < count; j++)
                {
                    var weight = weights[cursor++];
                    if (j >= limit || weight.weight <= 0) continue;
                    int bone = weight.boneIndex;
                    if (bone >= bones.Length || bone >= boneMatrices.Length || bone < 0 || bones[bone] == null)
                        throw new InvalidOperationException($"Normal Field Offset: '{renderer.name}' has a missing skinning bone.");
                    var m = boneMatrices[bone];
                    float w = weight.weight;
                    matrix.m00 += m.m00 * w; matrix.m01 += m.m01 * w; matrix.m02 += m.m02 * w;
                    matrix.m10 += m.m10 * w; matrix.m11 += m.m11 * w; matrix.m12 += m.m12 * w;
                    matrix.m20 += m.m20 * w; matrix.m21 += m.m21 * w; matrix.m22 += m.m22 * w;
                    total += w;
                }
                if (total <= 0)
                {
                    matrix = renderer.localToWorldMatrix;
                }
                else
                {
                    // Unity renormalizes the influences when the renderer quality limits their count.
                    for (int row = 0; row < 3; row++)
                        for (int col = 0; col < 3; col++) matrix[row, col] /= total;
                    matrix.m33 = 1;
                }
                float scaleProduct = ((Vector3)matrix.GetColumn(0)).magnitude *
                                     ((Vector3)matrix.GetColumn(1)).magnitude * ((Vector3)matrix.GetColumn(2)).magnitude;
                if (!SurfaceNormalField.Finite(scaleProduct) || scaleProduct <= 0 || Mathf.Abs(matrix.determinant) < 1e-8f * scaleProduct)
                    throw new InvalidOperationException($"Normal Field Offset: '{renderer.name}' has singular skinning. Remove zero scales or use a neutral pose.");
                result[vertex] = matrix.inverse;
            }
            return result;
        }
    }
}
