using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using net.rs64.TexTransTool;
using net.rs64.TexTransTool.TextureAtlas;
using net.rs64.TexTransTool.TextureAtlas.FineTuning;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using PropertyName = net.rs64.TexTransTool.PropertyName;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Noname.ALTO.TexTransTool.Tests")]

namespace Noname.AvatarTools.MaterialAtlas
{
    internal sealed class TexTransAtlasState
    {
        internal GameObject Root;
        internal readonly List<AtlasTexture> Atlases = new();
        internal readonly List<Material> Carriers = new();
        internal readonly List<Material> Results = new();
    }

    [InitializeOnLoad]
    internal static class TexTransToolAtlasBackend
    {
        // Material identity is not an atlas input. Conversion has already selected the
        // texture channels, sampling domain, resolution and color space at this point.
        internal sealed class InputKey : IEquatable<InputKey>
        {
            private readonly int group;
            private readonly Texture[] textures;
            internal InputKey(int group, IEnumerable<Texture> textures)
            { this.group = group; this.textures = textures.ToArray(); }
            public bool Equals(InputKey other) => other != null && group == other.group && textures.SequenceEqual(other.textures);
            public override bool Equals(object other) => other is InputKey key && Equals(key);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = group;
                    foreach (var texture in textures) hash = hash * 397 ^ (texture != null ? texture.GetInstanceID() : 0);
                    return hash;
                }
            }
        }

        private const string InputShader = "Hidden/Noname/MaterialAtlasInput";
        private static Shader registeredShader;
        private static IDisposable shaderInformation;
        private static string Channel(int index) => index == 0 ? "_MainTex" : "_AtlasTexture" + index;

        static TexTransToolAtlasBackend()
        {
            MaterialAtlasBackend.ResolveExisting = ExistingAtlasResolution.Apply;
            MaterialAtlasBackend.Capture = Capture;
            MaterialAtlasBackend.Prepare = Prepare;
            MaterialAtlasBackend.Finish = Finish;
            MaterialAtlasBackend.ValidateRenderer = ValidateRenderer;
        }

        private sealed class InputUsage : ITTShaderTextureUsageInformation
        {
            public void GetMaterialTextureUVUsage(ITTTextureUVUsageWriter writer)
            {
                int count = Mathf.Clamp(Mathf.RoundToInt(writer.GetFloat("_AtlasChannelCount")), 1, 8);
                for (int i = 0; i < count; i++) writer.WriteTextureUVUsage(Channel(i), UsageUVChannel.UV0);
            }
        }

        private static void Capture(BuildContext context, MaterialAtlasState state)
        {
            EditorBuildReadiness.RequireReady();
            if (state.Deferred) return;
            var shader = Shader.Find(InputShader);
            if (shader == null) throw new InvalidOperationException("The material atlas input shader is missing.");
            if (registeredShader != shader)
            {
                shaderInformation?.Dispose();
                shaderInformation = TTShaderTextureUsageInformationRegistry.RegisterTTShaderTextureUsageInformation(shader, new InputUsage());
                registeredShader = shader;
            }
            var backend = context.GetState<TexTransAtlasState>();
            backend.Root = new GameObject("ALTO Material Atlas (Build)");
            backend.Root.transform.SetParent(context.AvatarRootTransform, false);
            for (int i = 0; i < state.Settings.MergeGroups.Count; i++)
            {
                var group = state.Settings.MergeGroups[i];
                var child = new GameObject("Atlas " + (i + 1)); child.transform.SetParent(backend.Root.transform, false);
                var atlas = child.AddComponent<AtlasTexture>();
                var carrier = new Material(shader) { name = "Atlas " + (i + 1) + " carrier" };
                carrier.SetFloat("_AtlasChannelCount", group.TextureMappings.Count);
                context.AssetSaver.SaveAsset(carrier);
                atlas.AllMaterialMergeReference = carrier;
                atlas.AtlasSetting.AtlasTextureSize = group.AtlasSize;
                atlas.AtlasSetting.UsePrimaryMaximumTexture = true;
                atlas.AtlasSetting.IslandPadding = group.PaddingPixels / (float)group.AtlasSize;
                atlas.AtlasSetting.IncludeDisabledRenderer = true;
                atlas.AtlasSetting.ForceSetTexture = true;
                atlas.AtlasSetting.TextureFineTuning.Clear();
                var channels = Enumerable.Range(0, group.TextureMappings.Count).Select(c => new PropertyName(Channel(c), true)).ToList();
                atlas.AtlasSetting.TextureFineTuning.Add(new Compress
                {
                    PropertyNameList = channels, FormatQualityValue = (FormatQuality)group.Compression,
                    CompressionQuality = 100
                });
                atlas.AtlasSetting.TextureFineTuning.Add(new MipMap { PropertyNameList = channels, UseMipMap = group.GenerateMipMaps });
                for (int c = 0; c < group.TextureMappings.Count; c++)
                    atlas.AtlasSetting.TextureFineTuning.Add(new net.rs64.TexTransTool.TextureAtlas.FineTuning.ColorSpace
                    {
                        PropertyNameList = new List<PropertyName> { new(Channel(c), true) }, AsLinear = group.TextureMappings[c].Linear
                    });
                backend.Atlases.Add(atlas); backend.Carriers.Add(carrier); backend.Results.Add(null);
            }
        }

        private static string ValidateRenderer(BuildContext context, Renderer renderer)
        {
            if (renderer.GetComponents<Component>().Any(c => c != null &&
                c.GetType().Assembly == typeof(AtlasTexture).Assembly && c.GetType().FullName == "net.rs64.TexTransTool.TextureAtlas.AtlasExcludeTag"))
                return "TTT Atlas Exclude Tag";
            var own = context.GetState<TexTransAtlasState>().Atlases;
            foreach (var atlas in context.AvatarRootObject.GetComponentsInChildren<AtlasTexture>(true).Except(own))
                if (atlas.AtlasTargetMaterials.Any(m => renderer.sharedMaterials.Any(s => MaterialSlotPlan.SameMaterial(s, m))))
                    return "another TTT AtlasTexture targets this renderer's materials";
            return null;
        }

        private static void Prepare(BuildContext context, MaterialAtlasState state)
        {
            var backend = context.GetState<TexTransAtlasState>();
            var groups = state.Settings.MergeGroups;
            var report = context.GetState<OptimizationBuildReport>();
            foreach (var plan in state.Plans.ToArray())
            {
                string reason = null;
                for (int slot = 0; slot < plan.Groups.Length && reason == null; slot++)
                    if (plan.Groups[slot] >= 0)
                        foreach (var mapping in groups[plan.Groups[slot]].TextureMappings)
                            if ((reason = TexturePreparation.Unsupported(plan.Materials[slot], state.Mapping(plan.Materials[slot], mapping))) != null) break;
                if (reason == null) continue;
                var message = "Atlas retained " + AnimationUtility.CalculateTransformPath(plan.Renderer.transform, context.AvatarRootTransform) + ": " + reason;
                if (state.Settings.UnsupportedInputs == UnsupportedAtlasInput.Error || state.Converted(plan.Renderer) ||
                    state.RequiredRenderers.Contains(ObjectRegistry.GetReference(plan.Renderer))) throw new InvalidOperationException(message);
                report.Messages.Add(message); state.Plans.Remove(plan);
            }
            if (state.Deferred) return;
            var textures = new TexturePreparation(context);
            var inputs = new Dictionary<InputKey, Material>();
            int slots = 0;
            foreach (var plan in state.Plans)
            {
                if (plan.Renderer is SkinnedMeshRenderer skin) MaterialAtlasBackend.PreserveOriginalUv?.Invoke(context, skin);
                var materials = plan.Renderer.sharedMaterials;
                for (int slot = 0; slot < plan.Groups.Length; slot++)
                {
                    int groupIndex = plan.Groups[slot];
                    if (groupIndex < 0) continue;
                    var source = plan.Materials[slot]; var group = groups[groupIndex];
                    float priority = (group.ResolutionPriorities ?? new()).Where(p => MaterialSlotPlan.SameMaterial(state.Original(source), p.Material))
                        .Select(p => p.ResolutionScale).DefaultIfEmpty(1).Min();
                    var channels = group.TextureMappings.Select(mapping =>
                        textures.Get(source, state.Mapping(source, mapping), plan.UvDomain, priority, group.AtlasSize)).ToArray();
                    var key = new InputKey(groupIndex, channels);
                    slots++;
                    if (!inputs.TryGetValue(key, out var input))
                    {
                        input = new Material(registeredShader) { name = source.name + " (Atlas input)" };
                        input.SetFloat("_AtlasChannelCount", group.TextureMappings.Count);
                        for (int c = 0; c < group.TextureMappings.Count; c++)
                            input.SetTexture(Channel(c), channels[c]);
                        context.AssetSaver.SaveAsset(input);
                        inputs.Add(key, input); backend.Atlases[groupIndex].AtlasTargetMaterials.Add(input);
                    }
                    // These are private staging materials. Registering source replacement here would
                    // make TTT's provenance matching also select unconverted renderers outside Scope.
                    materials[slot] = input;
                }
                plan.Renderer.sharedMaterials = materials;
                if (plan.UvDomain != new Rect(0, 0, 1, 1))
                {
                    var current = plan.Renderer is SkinnedMeshRenderer smr ? smr.sharedMesh : plan.Renderer.GetComponent<MeshFilter>().sharedMesh;
                    var mesh = Object.Instantiate(current); mesh.name = current.name + " (Atlas UV domain)";
                    var domain = plan.UvDomain;
                    mesh.uv = mesh.uv.Select(uv => new Vector2((uv.x - domain.x) / domain.width, (uv.y - domain.y) / domain.height)).ToArray();
                    ObjectRegistry.RegisterReplacedObject(current, mesh); context.AssetSaver.SaveAsset(mesh);
                    if (plan.Renderer is SkinnedMeshRenderer skinned) skinned.sharedMesh = mesh;
                    else plan.Renderer.GetComponent<MeshFilter>().sharedMesh = mesh;
                }
            }
            report.Messages.Add("Atlas inputs: " + slots + " material slots use " + inputs.Count + " distinct converted texture inputs.");
        }

        private static void Finish(BuildContext context, MaterialAtlasState state)
        {
            if (state.Deferred) { ChainedAtlasPacking.Apply(context, state); return; }
            var backend = context.GetState<TexTransAtlasState>();
            var remapped = new Dictionary<Material, Material>();
            foreach (var plan in state.Plans)
            {
                if (plan.Renderer == null) throw new InvalidOperationException("An atlas renderer disappeared during texture processing.");
                var materials = plan.Renderer.sharedMaterials;
                if (materials.Length != plan.Groups.Length) throw new InvalidOperationException("Atlas material slots changed during texture processing.");
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    int groupIndex = plan.Groups[slot]; if (groupIndex < 0) continue;
                    var atlased = materials[slot];
                    if (atlased == null || atlased.shader != registeredShader || !MaterialSlotPlan.SameMaterial(atlased, backend.Carriers[groupIndex]))
                        throw new InvalidOperationException("TexTransTool did not produce the expected atlas for " + plan.Renderer.name + ". Check its build diagnostics.");
                    if (!remapped.TryGetValue(atlased, out var output))
                    {
                        var group = state.Settings.MergeGroups[groupIndex];
                        output = new Material(group.ReferenceMaterial) { name = group.Name + " (Atlased)" };
                        for (int c = 0; c < group.TextureMappings.Count; c++)
                        {
                            var mapping = group.TextureMappings[c];
                            var texture = atlased.GetTexture(Channel(c)) as Texture2D;
                            if (texture == null) throw new InvalidOperationException("TexTransTool did not generate atlas channel " + c + " for " + group.Name);
                            output.SetTexture(mapping.DestinationProperty, texture);
                            output.SetTextureScale(mapping.DestinationProperty, Vector2.one);
                            output.SetTextureOffset(mapping.DestinationProperty, Vector2.zero);
                        }
                        context.AssetSaver.SaveAsset(output);
                        ObjectRegistry.RegisterReplacedObject(atlased, output);
                        remapped.Add(atlased, output); backend.Results[groupIndex] = output;
                    }
                    materials[slot] = output;
                }
                plan.Renderer.sharedMaterials = materials;
            }
            var report = context.GetState<OptimizationBuildReport>();
            for (int i = 0; i < backend.Results.Count; i++)
            {
                var output = backend.Results[i];
                if (output == null) { report.Messages.Add("Atlas group " + (i + 1) + " selected no compatible material slots."); continue; }
                var group = state.Settings.MergeGroups[i];
                report.Messages.Add("Atlas group " + (i + 1) + " / " + group.Name + ": " + output.shader.name + "; " +
                    string.Join(", ", group.TextureMappings.Select(m => { var t = output.GetTexture(m.DestinationProperty); return m.DestinationProperty + "=" + t.width + "x" + t.height; })));
            }
            Object.DestroyImmediate(backend.Root);
        }
    }
}
