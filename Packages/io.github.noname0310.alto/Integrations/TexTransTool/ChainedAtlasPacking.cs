using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.MaterialAtlas
{
    // TTT has no public API for starting a second build session after its final GPU
    // readback. Repack completed textures using Unity's public rectangle packer.
    // This deliberately packs complete intermediate images, not their UV islands.
    internal static class ChainedAtlasPacking
    {
        private sealed class Input
        {
            internal Texture2D[] Textures;
            internal Rect Region;
            internal Material Output;
        }

        internal static void Apply(BuildContext context, MaterialAtlasState state)
        {
            var preparation = new TexturePreparation(context);
            var inputs = new Dictionary<TexTransToolAtlasBackend.InputKey, Input>();
            var slots = new Dictionary<(AtlasRendererPlan, int), Input>();
            for (int groupIndex = 0; groupIndex < state.Settings.MergeGroups.Count; groupIndex++)
            {
                var group = state.Settings.MergeGroups[groupIndex];
                var groupInputs = new List<Input>();
                foreach (var plan in state.Plans)
                    for (int slot = 0; slot < plan.Groups.Length; slot++)
                    {
                        if (plan.Groups[slot] != groupIndex) continue;
                        var source = plan.Materials[slot];
                        float priority = (group.ResolutionPriorities ?? new()).Where(p => MaterialSlotPlan.SameMaterial(state.Original(source), p.Material))
                            .Select(p => p.ResolutionScale).DefaultIfEmpty(1).Min();
                        var channels = group.TextureMappings.Select(m => preparation.Get(source, state.Mapping(source, m), plan.UvDomain, priority, group.AtlasSize)).ToArray();
                        var key = new TexTransToolAtlasBackend.InputKey(groupIndex, channels);
                        if (!inputs.TryGetValue(key, out var input))
                        { input = new Input { Textures = channels }; inputs.Add(key, input); groupInputs.Add(input); }
                        slots.Add((plan, slot), input);
                    }
                if (groupInputs.Count == 0) continue;
                var sizes = groupInputs.Select(i => new Vector2(i.Textures.Max(t => t.width), i.Textures.Max(t => t.height))).ToArray();
                var regions = Pack(sizes, group.PaddingPixels, group.AtlasSize);
                var output = new Material(group.ReferenceMaterial) { name = group.Name + " (Chained atlas)" };
                context.AssetSaver.SaveAsset(output);
                for (int channel = 0; channel < group.TextureMappings.Count; channel++)
                {
                    var mapping = group.TextureMappings[channel];
                    var texture = Compose(groupInputs.Select(i => i.Textures[channel]).ToArray(), regions, group, mapping.Linear,
                        context.GetState<OptimizationBuildEnvironment>().Target);
                    context.AssetSaver.SaveAsset(texture);
                    output.SetTexture(mapping.DestinationProperty, texture);
                    output.SetTextureScale(mapping.DestinationProperty, Vector2.one);
                    output.SetTextureOffset(mapping.DestinationProperty, Vector2.zero);
                }
                for (int i = 0; i < groupInputs.Count; i++)
                {
                    var r = regions[i];
                    groupInputs[i].Region = new Rect(r.x / group.AtlasSize, r.y / group.AtlasSize, r.width / group.AtlasSize, r.height / group.AtlasSize);
                    groupInputs[i].Output = output;
                }
                context.GetState<OptimizationBuildReport>().Messages.Add("Chained atlas " + group.Name + ": " + groupInputs.Count +
                    " completed texture inputs packed into " + group.AtlasSize + "x" + group.AtlasSize + ".");
            }
            foreach (var plan in state.Plans)
            {
                if (plan.Renderer is SkinnedMeshRenderer skin) MaterialAtlasBackend.PreserveOriginalUv?.Invoke(context, skin);
                var regions = new Rect?[plan.Groups.Length];
                var materials = (Material[])plan.Materials.Clone();
                for (int slot = 0; slot < materials.Length; slot++)
                    if (slots.TryGetValue((plan, slot), out var input))
                    { regions[slot] = input.Region; materials[slot] = input.Output; }
                var mesh = AtlasMeshRemap.Apply(plan.Mesh, regions, plan.UvDomain);
                context.AssetSaver.SaveAsset(mesh); ObjectRegistry.RegisterReplacedObject(plan.Mesh, mesh);
                if (plan.Renderer is SkinnedMeshRenderer skinned) skinned.sharedMesh = mesh;
                else plan.Renderer.GetComponent<MeshFilter>().sharedMesh = mesh;
                // Do not replace material provenance globally: unselected consumers
                // can still use the same source material with its original UVs.
                plan.Renderer.sharedMaterials = materials;
            }
        }

        internal static Rect[] Pack(Vector2[] sizes, int padding, int maximum)
        {
            if (sizes.Length == 0) return Array.Empty<Rect>();
            var results = new List<Rect>();
            for (float scale = 1; scale > 0.00001f; scale *= 0.8f)
            {
                var padded = sizes.Select(s => new Vector2(Mathf.Max(2, Mathf.Ceil(s.x * scale)) + padding * 2,
                    Mathf.Max(2, Mathf.Ceil(s.y * scale)) + padding * 2)).ToArray();
                results.Clear();
                if (!Texture2D.GenerateAtlas(padded, 0, maximum, results) || results.Count != sizes.Length) continue;
                if (results.Where((r, i) => r.size != padded[i] || r.x < 0 || r.y < 0 || r.xMax > maximum || r.yMax > maximum).Any()) continue;
                return results.Select(r => new Rect(r.x + padding, r.y + padding, r.width - padding * 2, r.height - padding * 2)).ToArray();
            }
            throw new InvalidOperationException("Too many chained atlas inputs for the requested size and padding. Increase Atlas Size or split the groups.");
        }

        internal static Texture2D Compose(Texture2D[] textures, Rect[] regions, MaterialAtlasGroup group, bool linear, BuildTarget target)
        {
            var previous = RenderTexture.active; bool previousWrite = GL.sRGBWrite;
            var readWrite = linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;
            var atlas = RenderTexture.GetTemporary(group.AtlasSize, group.AtlasSize, 0, RenderTextureFormat.ARGB32, readWrite);
            var sampler = new Material(Shader.Find("Hidden/Noname/MaterialAtlasInput"));
            Texture2D result = null;
            try
            {
                RenderTexture.active = atlas; GL.Clear(false, true, Color.clear);
                GL.sRGBWrite = !linear && QualitySettings.activeColorSpace == ColorSpace.Linear;
                sampler.SetFloat("_AtlasClampUv", 1);
                for (int i = 0; i < textures.Length; i++)
                {
                    var rect = regions[i]; int padding = group.PaddingPixels;
                    int width = Mathf.RoundToInt(rect.width), height = Mathf.RoundToInt(rect.height);
                    var tile = RenderTexture.GetTemporary(width + padding * 2, height + padding * 2, 0, RenderTextureFormat.ARGB32, readWrite);
                    try
                    {
                        sampler.SetVector("_AtlasSampleST", new Vector4(1 + 2f * padding / width, 1 + 2f * padding / height,
                            -(float)padding / width, -(float)padding / height));
                        Graphics.Blit(textures[i], tile, sampler);
                        Graphics.CopyTexture(tile, 0, 0, 0, 0, tile.width, tile.height, atlas, 0, 0,
                            Mathf.RoundToInt(rect.x) - padding, Mathf.RoundToInt(rect.y) - padding);
                    }
                    finally { RenderTexture.ReleaseTemporary(tile); }
                }
                RenderTexture.active = atlas;
                result = new Texture2D(group.AtlasSize, group.AtlasSize, TextureFormat.RGBA32, group.GenerateMipMaps, linear)
                { name = group.Name + " (Chained atlas texture)", wrapMode = TextureWrapMode.Clamp };
                result.ReadPixels(new Rect(0, 0, group.AtlasSize, group.AtlasSize), 0, 0); result.Apply(group.GenerateMipMaps);
                if (group.Compression != AtlasCompression.None)
                {
                    bool mobile = target == BuildTarget.Android || target == BuildTarget.iOS;
                    var format = mobile ? group.Compression == AtlasCompression.High ? TextureFormat.ASTC_4x4 :
                        group.Compression == AtlasCompression.Low ? TextureFormat.ASTC_8x8 : TextureFormat.ASTC_6x6 :
                        group.Compression == AtlasCompression.High ? TextureFormat.BC7 : TextureFormat.DXT5;
                    EditorUtility.CompressTexture(result, format, TextureCompressionQuality.Best);
                }
                return result;
            }
            catch { if (result != null) Object.DestroyImmediate(result); throw; }
            finally
            {
                GL.sRGBWrite = previousWrite; RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(atlas); Object.DestroyImmediate(sampler);
            }
        }
    }
}
