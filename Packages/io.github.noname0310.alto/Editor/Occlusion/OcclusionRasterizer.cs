using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Occlusion
{
    internal sealed class OcclusionSurface
    {
        // Positions are already in world space. These are geometric proxies,
        // never source materials or shader deformations.
        internal Mesh Mesh;
        internal CullMode Culling = CullMode.Back;
    }

    internal static class OcclusionRasterizer
    {
        private const string Directory = "Packages/io.github.noname0310.alto/Editor/Occlusion/";
        internal static string UnsupportedReason()
        {
            if (SystemInfo.graphicsShaderLevel < 50 || !SystemInfo.supportsComputeShaders ||
                SystemInfo.maxComputeBufferInputsFragment < 1 || SystemInfo.supportedRandomWriteTargetCount < 2)
                return "The editor GPU does not support fragment visibility buffers.";
            if (!SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, FormatUsage.Render) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, FormatUsage.Blend) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, FormatUsage.LoadStore))
                return "The editor GPU does not support the required linear depth format.";
            return null;
        }

        internal static Vector3[] Directions(int count)
        {
            if (count < 6 || count > 1024) throw new ArgumentOutOfRangeException(nameof(count));
            var result = new List<Vector3> { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            for (int i = 0; i < count - 6; i++)
            {
                double y = 1 - 2 * (i + 0.5) / (count - 6), radius = Math.Sqrt(Math.Max(0, 1 - y * y));
                double angle = i * Math.PI * (3 - Math.Sqrt(5));
                result.Add(new Vector3((float)(Math.Cos(angle) * radius), (float)y, (float)(Math.Sin(angle) * radius)));
            }
            return result.ToArray();
        }

        internal static uint[] Evaluate(Vector3[] vertices, int[] indices, IReadOnlyList<OcclusionSurface> occluders,
            IReadOnlyList<Vector3> directions, int resolution, int boundaryPixels, float depthTolerance)
        {
            if (vertices == null || indices == null || indices.Length % 3 != 0 || indices.Any(i => i < 0 || i >= vertices.Length))
                throw new ArgumentException("Invalid candidate triangle geometry.");
            if (resolution < 16 || resolution > 4096 || boundaryPixels < 0 || boundaryPixels > 32 ||
                directions == null || directions.Count == 0 || directions.Any(d => !Finite(d) || d.sqrMagnitude < 0.5f) ||
                !Finite(depthTolerance) || depthTolerance < 0 || vertices.Any(v => !Finite(v)))
                throw new ArgumentException("Invalid visibility sampling settings.");
            int count = indices.Length / 3;
            if (count == 0) return Array.Empty<uint>();
            if (count >= 16777216) throw new InvalidOperationException("The visibility mesh exceeds the exact triangle-ID range.");
            if (occluders == null || occluders.Count == 0) return Enumerable.Repeat(3u, count).ToArray();
            string unsupported = UnsupportedReason(); if (unsupported != null) throw new NotSupportedException(unsupported);
            var depthShader = AssetDatabase.LoadAssetAtPath<Shader>(Directory + "OcclusionDepth.shader");
            var visibilityShader = AssetDatabase.LoadAssetAtPath<Shader>(Directory + "OcclusionVisibility.shader");
            var boundary = AssetDatabase.LoadAssetAtPath<ComputeShader>(Directory + "OcclusionBoundary.compute");
            if (depthShader == null || !depthShader.isSupported || visibilityShader == null || !visibilityShader.isSupported || boundary == null)
                throw new NotSupportedException("Occlusion analysis shaders are unavailable or unsupported.");

            var owned = new List<Object>(); var textures = new List<RenderTexture>();
            ComputeBuffer flags = null; CommandBuffer command = null; var previous = RenderTexture.active;
            try
            {
                var target = new Mesh { name = "Occlusion triangle IDs", indexFormat = IndexFormat.UInt32 }; owned.Add(target);
                target.vertices = indices.Select(i => vertices[i]).ToArray();
                target.uv = Enumerable.Range(0, indices.Length).Select(i => new Vector2(i / 3, 0)).ToArray();
                target.SetIndices(Enumerable.Range(0, indices.Length).ToArray(), MeshTopology.Triangles, 0);
                var depthMaterials = Enumerable.Range(0, 3).Select(cull =>
                {
                    var material = new Material(depthShader); material.SetInt("_Cull", cull); owned.Add(material); return material;
                }).ToArray();
                var visible = new Material(visibilityShader); owned.Add(visible);
                var depth = Texture(RenderTextureFormat.RFloat, false);
                var erodedX = boundaryPixels > 0 ? Texture(RenderTextureFormat.RFloat, true) : null;
                var erodedY = boundaryPixels > 0 ? Texture(RenderTextureFormat.RFloat, true) : null;
                var output = Texture(RenderTextureFormat.ARGB32, false);
                flags = new ComputeBuffer(count, sizeof(uint)); flags.SetData(new uint[count]); visible.SetBuffer("_VisibilityFlags", flags);
                var bounds = new Bounds(vertices[0], Vector3.zero);
                foreach (var vertex in vertices) bounds.Encapsulate(vertex);
                foreach (var surface in occluders)
                {
                    if (surface?.Mesh == null || !surface.Mesh.isReadable || !Enum.IsDefined(typeof(CullMode), surface.Culling))
                        throw new ArgumentException("Occluders require readable world-space meshes and valid culling.");
                    foreach (var vertex in surface.Mesh.vertices)
                    {
                        if (!Finite(vertex)) throw new ArgumentException("An occluder contains non-finite positions.");
                        bounds.Encapsulate(vertex);
                    }
                }
                float radius = Mathf.Max(bounds.extents.magnitude, 0.001f) * 1.05f;
                float distance = radius * 2 + 1, far = distance + radius * 2;
                var projection = GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-radius, radius, -radius, radius, 0.001f, far), true);
                command = new CommandBuffer { name = "Noname occlusion visibility" };
                int kernel = boundary.FindKernel("Erode");
                foreach (var direction in directions)
                {
                    var forward = -direction.normalized; var up = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
                    var camera = Matrix4x4.TRS(bounds.center - forward * distance, Quaternion.LookRotation(forward, up), Vector3.one);
                    var view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * camera.inverse;
                    var properties = new MaterialPropertyBlock(); properties.SetMatrix("_ClipFromWorld", projection * view);
                    properties.SetMatrix("_ViewFromWorld", view); properties.SetFloat("_DepthTolerance", depthTolerance);
                    command.SetRenderTarget(depth); command.SetViewport(new Rect(0, 0, resolution, resolution));
                    command.ClearRenderTarget(false, true, new Color(far, 0, 0, 0));
                    foreach (var surface in occluders)
                        for (int subMesh = 0; subMesh < surface.Mesh.subMeshCount; subMesh++)
                            command.DrawMesh(surface.Mesh, Matrix4x4.identity, depthMaterials[(int)surface.Culling], subMesh, 0, properties);
                    // Detach the rendered depth color before sampling it. Otherwise
                    // graphics-to-compute reads can return zero inside editor builds.
                    command.SetRenderTarget(output);
                    command.ClearRenderTarget(false, true, Color.clear);
                    if (boundaryPixels > 0)
                    {
                        command.SetComputeIntParam(boundary, "_Size", resolution); command.SetComputeIntParam(boundary, "_Radius", boundaryPixels);
                        command.SetComputeFloatParam(boundary, "_BackgroundDepth", far);
                        Erode(depth, erodedX, 1, 0); Erode(erodedX, erodedY, 0, 1);
                    }
                    properties.SetTexture("_OccluderDepth", boundaryPixels > 0 ? erodedY : depth, RenderTextureSubElement.Color);
                    command.SetRandomWriteTarget(1, flags);
                    command.DrawMesh(target, Matrix4x4.identity, visible, 0, 0, properties);
                    command.ClearRandomWriteTargets();
                }
                Graphics.ExecuteCommandBuffer(command);
                var result = new uint[count]; flags.GetData(result); return result;

                void Erode(RenderTexture input, RenderTexture destination, int x, int y)
                {
                    command.SetComputeIntParams(boundary, "_Direction", x, y);
                    // Linear distance is stored in RFloat color, not in a depth attachment.
                    command.SetComputeTextureParam(boundary, kernel, "_Input", input, 0, RenderTextureSubElement.Color);
                    command.SetComputeTextureParam(boundary, kernel, "_Output", destination, 0, RenderTextureSubElement.Color);
                    command.DispatchCompute(boundary, kernel, (resolution + 7) / 8, (resolution + 7) / 8, 1);
                }
            }
            finally
            {
                RenderTexture.active = previous; command?.Dispose(); flags?.Dispose();
                foreach (var texture in textures) { texture.Release(); Object.DestroyImmediate(texture); }
                foreach (var item in owned) Object.DestroyImmediate(item);
            }

            RenderTexture Texture(RenderTextureFormat format, bool randomWrite)
            {
                var texture = new RenderTexture(resolution, resolution, 0, format, RenderTextureReadWrite.Linear)
                { enableRandomWrite = randomWrite, filterMode = FilterMode.Point, useMipMap = false, antiAliasing = 1 };
                textures.Add(texture);
                if (!texture.Create()) throw new NotSupportedException("Could not allocate an occlusion render target.");
                return texture;
            }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
