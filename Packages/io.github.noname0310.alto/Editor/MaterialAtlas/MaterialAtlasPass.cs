using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ParameterPolicy;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.MaterialAtlas
{
    internal sealed class MaterialAtlasState
    {
        internal ALTOMaterialAtlas Settings;
        internal OptimizationBuildScope Scope;
        internal readonly HashSet<ObjectReference> Targets = new();
        internal readonly List<AtlasRendererPlan> Plans = new();
        internal readonly Stopwatch Watch = new();
        internal bool Prepared;
        internal EarlyMaterialConversion Early;
        internal bool Deferred;
        internal readonly HashSet<ObjectReference> PreservedMaterials = new(), RequiredRenderers = new();
        internal readonly HashSet<Renderer> UnconvertedRenderers = new();
        internal readonly Dictionary<ObjectReference, int> ChainedMaterialGroups = new();
        internal bool Preserved(Material material) => material != null && PreservedMaterials.Contains(ObjectRegistry.GetReference(material));
        internal int Group(Material material) => Preserved(material) ? -1 : Early?.Find(material)?.Group ??
            (material != null && ChainedMaterialGroups.TryGetValue(ObjectRegistry.GetReference(material), out int group) ? group : MaterialAtlasPass.SelectGroup(material, Settings.MergeGroups));
        internal Material Original(Material material) => Early?.Find(material)?.Source ?? material;
        internal bool Converted(Renderer renderer) => renderer.sharedMaterials.Any(m => Early?.Find(m) != null);
        internal AtlasTextureMapping Mapping(Material material, AtlasTextureMapping mapping) => Early?.Find(material) == null ? mapping : new AtlasTextureMapping
        {
            SourceProperty = mapping.DestinationProperty, DestinationProperty = mapping.DestinationProperty,
            MissingTextureColor = mapping.MissingTextureColor, Linear = mapping.Linear
        };
    }

    internal sealed class AtlasRendererPlan
    {
        internal Renderer Renderer;
        internal Mesh Mesh;
        internal Material[] Materials;
        internal int[] Groups;
        internal Rect UvDomain;
    }

    internal static class MaterialAtlasBackend
    {
        internal static Action<BuildContext, MaterialAtlasState> ResolveExisting, Capture, Prepare, Finish;
        internal static Func<BuildContext, Renderer, string> ValidateRenderer;
        internal static Action<BuildContext, SkinnedMeshRenderer> PreserveOriginalUv;
    }

    internal static class MaterialAtlasPass
    {
        internal static void RegisterEarlyCopies(BuildContext context)
        {
            var state = context.GetState<MaterialAtlasState>();
            state.Early?.RegisterCopies(context.AvatarRootObject);
            // Check before object simplification can collect renderers whose source
            // shaders were discarded by an earlier generator. An empty final atlas
            // must not look like a successful Preserve/Chain conversion in that case.
            foreach (var renderer in state.UnconvertedRenderers)
            {
                if (renderer == null || renderer.sharedMaterials.Any(m => m != null)) continue;
                var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null || !Enumerable.Range(0, mesh.subMeshCount).Any(s => mesh.GetIndexCount(s) > 0)) continue;
                throw new InvalidOperationException("Existing atlas input materials disappeared before packing on " +
                    AnimationUtility.CalculateTransformPath(renderer.transform, context.AvatarRootTransform) + ". " +
                    "Preserve/Chain cannot recover materials discarded by earlier generators. Use Replace Fully Covered with Before Generators conversion, or adjust the earlier generator.");
            }
        }

        internal static void Capture(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOMaterialAtlas>(true);
            var enabled = all.Where(c => OptimizationBuildScope.Applies(c, context)).ToArray();
            if (enabled.Length > 1) throw new InvalidOperationException("Use one enabled ALTO Material Atlas per avatar and platform.");
            foreach (var inactive in all.Except(enabled)) UnityEngine.Object.DestroyImmediate(inactive);
            if (enabled.Length == 0) return;
            if (MaterialAtlasBackend.Capture == null)
                throw new InvalidOperationException("ALTO Material Atlas requires the supported TexTransTool integration (1.0.2).");
            var state = context.GetState<MaterialAtlasState>();
            state.Settings = enabled[0];
            state.Scope = OptimizationBuildScope.Capture(state.Settings, context.AvatarRootObject);
            ValidateGroups(state.Settings.MergeGroups);
            if (!Enum.IsDefined(typeof(AtlasMaterialConversionTiming), state.Settings.MaterialConversion))
                throw new InvalidOperationException("Choose a valid material conversion timing.");
            if (!Enum.IsDefined(typeof(ExistingAtlasPolicy), state.Settings.ExistingAtlases))
                throw new InvalidOperationException("Choose a valid Existing Atlases policy.");
            if (!Enum.IsDefined(typeof(RendererSelectionMode), state.Settings.TargetSelection))
                throw new InvalidOperationException("Choose a valid atlas Target Selection mode.");
            foreach (var renderer in state.Settings.TargetSelection == RendererSelectionMode.SelectedRenderers ? state.Settings.TargetRenderers ?? new() : new())
            {
                if (renderer == null || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer))
                    throw new InvalidOperationException("Atlas targets must reference existing MeshRenderers or SkinnedMeshRenderers.");
                if (!state.Scope.Contains(renderer.transform)) throw new InvalidOperationException("Atlas target is outside Scope Root: " + renderer.name);
                state.Targets.Add(ObjectRegistry.GetReference(renderer));
            }
            context.GetState<OptimizationBuildReport>().Initialize(context.AvatarRootObject,
                context.GetState<OptimizationBuildEnvironment>().Target.ToString());
            MaterialAtlasBackend.ResolveExisting?.Invoke(context, state);
            if (!state.Deferred && state.Settings.MaterialConversion == AtlasMaterialConversionTiming.BeforeGenerators)
                state.Early = EarlyMaterialConversion.Apply(context, state);
            MaterialAtlasBackend.Capture(context, state);
        }

        internal static IEnumerable<Renderer> SelectRenderers(GameObject avatar, MaterialAtlasState state) =>
            avatar.GetComponentsInChildren<Renderer>(true).Where(r => (r is MeshRenderer || r is SkinnedMeshRenderer) &&
                state.Scope.Contains(r.transform) && (state.Settings.TargetSelection == RendererSelectionMode.AllRenderersInScope ||
                    state.Targets.Contains(ObjectRegistry.GetReference(r))));

        internal static void ValidateGroups(IReadOnlyList<MaterialAtlasGroup> groups)
        {
            if (groups == null || groups.Count == 0) throw new InvalidOperationException("Add at least one material atlas group.");
            if (groups.Count(g => g != null && g.MaterialSelection == AtlasMaterialSelectionMode.AllUnassignedMaterials) > 1)
                throw new InvalidOperationException("Only one atlas group may select all remaining materials.");
            foreach (var group in groups)
            {
                if (group == null || group.ReferenceMaterial == null || group.ReferenceMaterial.shader == null)
                    throw new InvalidOperationException("Each atlas group requires a Reference Material.");
                if (!Enum.IsDefined(typeof(AtlasMaterialSelectionMode), group.MaterialSelection))
                    throw new InvalidOperationException("Choose a valid atlas Material Selection mode.");
                if (group.MaterialSelection == AtlasMaterialSelectionMode.SelectedMaterials && (group.Materials ?? new()).Any(m => m == null))
                    throw new InvalidOperationException("Selected Materials entries must reference existing materials.");
                if (group.AtlasSize < 32 || group.AtlasSize > 8192 || !Mathf.IsPowerOfTwo(group.AtlasSize))
                    throw new InvalidOperationException("Atlas Size must be a power of two from 32 to 8192.");
                if (group.PaddingPixels < 1 || group.PaddingPixels > group.AtlasSize * 0.05f)
                    throw new InvalidOperationException("Atlas padding must be at least one pixel and at most 5% of Atlas Size.");
                if (group.TextureMappings == null || group.TextureMappings.Count == 0 || group.TextureMappings.Count > 8)
                    throw new InvalidOperationException("Each atlas group supports one to eight texture mappings.");
                var destinations = new HashSet<string>(StringComparer.Ordinal);
                foreach (var mapping in group.TextureMappings)
                {
                    if (mapping == null || string.IsNullOrWhiteSpace(mapping.SourceProperty) ||
                        string.IsNullOrWhiteSpace(mapping.DestinationProperty) ||
                        !destinations.Add(mapping.DestinationProperty) || !group.ReferenceMaterial.HasTexture(mapping.DestinationProperty))
                        throw new InvalidOperationException("Atlas texture mappings require unique texture properties present on the Reference Material.");
                    var shader = group.ReferenceMaterial.shader;
                    int property = shader.FindPropertyIndex(mapping.DestinationProperty);
                    if (property < 0 || shader.GetPropertyTextureDimension(property) != UnityEngine.Rendering.TextureDimension.Tex2D)
                        throw new InvalidOperationException("Atlas destinations must be 2D texture properties on the Reference Material.");
                    if (Enumerable.Range(0, 4).Any(c => !MotionAnalysis.Finite(mapping.MissingTextureColor[c]) ||
                        mapping.MissingTextureColor[c] < 0 || mapping.MissingTextureColor[c] > 1))
                        throw new InvalidOperationException("Missing texture colors must have finite channels from zero to one.");
                }
                foreach (var priority in group.ResolutionPriorities ?? new())
                    if (priority == null || priority.Material == null || !MotionAnalysis.Finite(priority.ResolutionScale) ||
                        priority.ResolutionScale < 0.125f || priority.ResolutionScale > 1)
                        throw new InvalidOperationException("Atlas resolution priorities require a material and a scale from 0.125 to 1.");
            }
        }

        internal static int SelectGroup(Material material, IReadOnlyList<MaterialAtlasGroup> groups)
        {
            int selected = -1, fallback = -1;
            for (int i = 0; i < groups.Count; i++)
            {
                var materials = groups[i].Materials;
                if (groups[i].MaterialSelection == AtlasMaterialSelectionMode.AllUnassignedMaterials) { fallback = i; continue; }
                if (materials == null || !materials.Any(m => MaterialSlotPlan.SameMaterial(material, m))) continue;
                if (selected != -1) throw new InvalidOperationException("A material matches multiple atlas groups: " + material.name);
                selected = i;
            }
            return material == null ? -1 : selected != -1 ? selected : fallback;
        }

        internal static void Prepare(BuildContext context)
        {
            if (context.GetState<MaterialAtlasState>().Deferred) return;
            PrepareInputs(context);
        }

        private static void PrepareInputs(BuildContext context)
        {
            var state = context.GetState<MaterialAtlasState>();
            if (state.Settings == null) return;
            state.Watch.Start();
            var avatar = context.AvatarRootObject;
            var report = context.GetState<OptimizationBuildReport>();
            var animationLease = Noname.AvatarTools.Build.OptimizationSession.Acquire(context);
            var services = animationLease.Services;
            try
            {
                var controllers = services.ControllerContext.Controllers.Where(e => !(e.Key is UnityEngine.Object owner) || owner != null)
                    .ToDictionary(e => e.Key, e => e.Value);
                var policy = ParameterPolicyPass.Resolve(context, controllers);
                if (policy.Uncertain) throw new InvalidOperationException("Material conversion requires verified parameter preservation mappings.");
                var hazards = AtlasAnimationSafety.Find(avatar, controllers, policy.Preserved,
                    services.ObjectPathRemapper.GetVirtualToRealPathMap(), state.Settings.MergeGroups, state.Original, state.Group);
                var renderers = SelectRenderers(avatar, state).ToArray();
                state.Early?.ValidateSurvivingInputs(renderers);
                foreach (var renderer in renderers)
                {
                    if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                    var materials = renderer.sharedMaterials;
                    var groups = materials.Select(state.Group).ToArray();
                    if (groups.All(g => g < 0)) continue;
                    var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
                    string reason = mesh == null ? "no mesh" : renderer.GetComponent<Cloth>() != null ? "Cloth owns this mesh" :
                        hazards.TryGetValue(renderer, out var hazard) ? hazard : MaterialAtlasBackend.ValidateRenderer?.Invoke(context, renderer);
                    Rect domain = default;
                    if (reason == null) reason = ValidateMesh(mesh, groups, out domain);
                    if (reason != null)
                    {
                        var message = "Atlas retained " + AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform) + ": " + reason;
                        if (state.Settings.UnsupportedInputs == UnsupportedAtlasInput.Error || state.Converted(renderer) ||
                            state.RequiredRenderers.Contains(ObjectRegistry.GetReference(renderer))) throw new InvalidOperationException(message);
                        report.Messages.Add(message); continue;
                    }
                    state.Plans.Add(new AtlasRendererPlan { Renderer = renderer, Mesh = mesh, Materials = materials, Groups = groups, UvDomain = domain });
                }
                MaterialAtlasBackend.Prepare(context, state);
                state.Prepared = true;
                report.Messages.Add("Prepared texture atlas inputs for " + state.Plans.Count + " renderers; source properties other than mapped textures follow the Reference Material.");
            }
            finally
            {
                animationLease.Dispose();
            }
        }

        internal static string ValidateMesh(Mesh mesh, int[] groups, out Rect domain)
        {
            domain = new Rect(0, 0, 1, 1);
            if (!mesh.isReadable)
            {
                var source = ObjectRegistry.GetReference(mesh)?.Object as Mesh ?? mesh;
                string path = AssetDatabase.GetAssetPath(source);
                string action = !string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is ModelImporter
                    ? "Select '" + path + "' in the Project window, open the Model tab, enable Read/Write, and click Apply."
                    : "Enable Read/Write in the source model's Model import settings and click Apply. " +
                      "For a generated mesh, ensure the generating tool keeps its mesh data readable until optimization finishes.";
                return "mesh '" + mesh.name + "' has Read/Write disabled, so ALTO cannot read its vertex and UV data. " + action;
            }
            var uv = mesh.uv;
            if (uv.Length != mesh.vertexCount || uv.Length == 0) return "a complete UV0 channel is required";
            if (mesh.subMeshCount != groups.Length) return "material slots must match submeshes; remove overlay passes first";
            if (Enumerable.Range(0, mesh.subMeshCount).Any(s => mesh.GetTopology(s) != MeshTopology.Triangles)) return "non-triangle topology";
            if (uv.Any(p => !MotionAnalysis.Finite(p.x) || !MotionAnalysis.Finite(p.y))) return "non-finite UV coordinates";
            float x0 = Mathf.Min(0, Mathf.Floor(uv.Min(p => p.x))), y0 = Mathf.Min(0, Mathf.Floor(uv.Min(p => p.y)));
            float x1 = Mathf.Max(1, Mathf.Ceil(uv.Max(p => p.x))), y1 = Mathf.Max(1, Mathf.Ceil(uv.Max(p => p.y)));
            domain = Rect.MinMaxRect(x0, y0, x1, y1);
            if (domain.width > 64 || domain.height > 64) return "UV span exceeds 64 tiles";
            if (domain != new Rect(0, 0, 1, 1) && groups.Any(g => g < 0))
                return "normalizing tiled UVs would affect unselected material slots";
            return null;
        }

        internal static void Finish(BuildContext context)
        {
            var state = context.GetState<MaterialAtlasState>();
            if (state.Settings == null) return;
            if (state.Deferred) PrepareInputs(context);
            if (state.Prepared) MaterialAtlasBackend.Finish(context, state);
            state.Watch.Stop();
            context.GetState<OptimizationBuildReport>().Messages.Add("Material atlasing including TexTransTool completed in " + state.Watch.ElapsedMilliseconds + " ms.");
            UnityEngine.Object.DestroyImmediate(state.Settings);
        }
    }
}
