using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using net.rs64.TexTransTool.TextureAtlas;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.MaterialAtlas
{
    internal static class ExistingAtlasResolution
    {
        internal static void Apply(BuildContext context, MaterialAtlasState state)
        {
            var avatar = context.AvatarRootObject;
            var atlases = avatar.GetComponentsInChildren<AtlasTexture>(true);
            var renderers = avatar.GetComponentsInChildren<Renderer>(true);
            var selected = new HashSet<Renderer>(MaterialAtlasPass.SelectRenderers(avatar, state));
            var report = context.GetState<OptimizationBuildReport>();
            if (state.Settings.ExistingAtlases == ExistingAtlasPolicy.Chain)
            {
                foreach (var atlas in atlases)
                {
                    if (selected.Any(r => Touches(atlas, r)) && (int)atlas.AtlasSetting.AtlasTargetUVChannel != 0)
                        throw new InvalidOperationException("Chaining requires existing overlapping atlases to target UV0: " + atlas.name);
                    CaptureChainedGroups(atlas, selected, state);
                }
                state.Deferred = true;
                foreach (var renderer in selected.Where(r => r.sharedMaterials.Any(m => state.Group(m) >= 0))) state.UnconvertedRenderers.Add(renderer);
                report.Messages.Add("Existing atlas policy: Chain. Conversion and final packing run after TexTransTool completes; whole intermediate textures are repacked.");
                return;
            }

            // Preserve a connected set, including other slots of an affected renderer.
            // Early conversion rewrites material references throughout the avatar, so a
            // shared material cannot be converted safely on just one of its consumers.
            var retained = new HashSet<AtlasTexture>();
            foreach (var atlas in atlases)
            {
                var consumers = renderers.Where(r => Touches(atlas, r)).ToArray();
                if (!consumers.Any(selected.Contains)) continue;
                bool replace = state.Settings.ExistingAtlases == ExistingAtlasPolicy.ReplaceFullyCovered &&
                    (int)atlas.AtlasSetting.AtlasTargetUVChannel == 0 && consumers.All(r => selected.Contains(r) &&
                        !r.GetComponents<Component>().Any(c => c != null && c.GetType().Assembly == typeof(AtlasTexture).Assembly &&
                            c.GetType().FullName == "net.rs64.TexTransTool.TextureAtlas.AtlasExcludeTag") && r.sharedMaterials.All(m => state.Group(m) >= 0));
                if (!replace) retained.Add(atlas);
            }
            bool changed;
            do
            {
                changed = false;
                foreach (var atlas in retained)
                    foreach (var material in atlas.AtlasTargetMaterials.Where(m => m != null))
                        changed |= state.PreservedMaterials.Add(ObjectRegistry.GetReference(material));
                foreach (var renderer in renderers)
                    if (renderer.sharedMaterials.Any(state.Preserved))
                        foreach (var material in renderer.sharedMaterials.Where(m => m != null))
                            changed |= state.PreservedMaterials.Add(ObjectRegistry.GetReference(material));
                foreach (var atlas in atlases)
                    if (atlas.AtlasTargetMaterials.Any(state.Preserved)) changed |= retained.Add(atlas);
            } while (changed);
            foreach (var renderer in selected.Where(r => r.sharedMaterials.Any(state.Preserved))) state.UnconvertedRenderers.Add(renderer);
            foreach (var atlas in atlases)
            {
                var consumers = renderers.Where(r => Touches(atlas, r)).ToArray();
                if (!consumers.Any(selected.Contains)) continue;
                string path = AnimationUtility.CalculateTransformPath(atlas.transform, avatar.transform);
                if (retained.Contains(atlas))
                    report.Messages.Add("Preserved existing TTT atlas and shared material consumers: " + path);
                else
                {
                    foreach (var renderer in consumers) state.RequiredRenderers.Add(ObjectRegistry.GetReference(renderer));
                    report.Messages.Add("Replaced fully covered TTT atlas with ALTO group settings: " + path);
                    UnityEngine.Object.DestroyImmediate(atlas);
                }
            }
        }

        private static bool Touches(AtlasTexture atlas, Renderer renderer) => atlas.AtlasTargetMaterials.Any(m =>
            m != null && renderer.sharedMaterials.Any(s => MaterialSlotPlan.SameMaterial(s, m)));

        private static void CaptureChainedGroups(AtlasTexture atlas, IEnumerable<Renderer> selected, MaterialAtlasState state)
        {
            // TTT identifies a merged output by its merge reference, which need not
            // be one of ALTO's selected input materials. Preserve that selection and
            // reject merges that would erase a boundary between ALTO groups.
            var groups = new Dictionary<ObjectReference, HashSet<int>>();
            var materials = selected.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToArray();
            foreach (var material in materials)
            {
                if (!atlas.AtlasTargetMaterials.Any(m => MaterialSlotPlan.SameMaterial(m, material))) continue;
                var explicitGroup = atlas.MergeMaterialGroups.FirstOrDefault(g => g != null && g.Group.Any(m => MaterialSlotPlan.SameMaterial(m, material)));
                var reference = explicitGroup != null ? explicitGroup.Reference : atlas.AllMaterialMergeReference;
                if (reference == null) continue;
                var key = ObjectRegistry.GetReference(reference);
                if (!groups.TryGetValue(key, out var values)) groups.Add(key, values = new());
                values.Add(MaterialAtlasPass.SelectGroup(material, state.Settings.MergeGroups));
                // The reference may also survive unchanged on a renderer outside
                // this TTT atlas. Its shared provenance cannot select another group.
                foreach (var untouched in materials.Where(m => MaterialSlotPlan.SameMaterial(m, reference) &&
                    !atlas.AtlasTargetMaterials.Any(target => MaterialSlotPlan.SameMaterial(target, m))))
                    values.Add(MaterialAtlasPass.SelectGroup(untouched, state.Settings.MergeGroups));
            }
            foreach (var pair in groups)
            {
                if (pair.Value.Count != 1 || state.ChainedMaterialGroups.TryGetValue(pair.Key, out int existing) && !pair.Value.Contains(existing))
                    throw new InvalidOperationException("An existing TTT merge crosses ALTO material selection/group boundaries: " + atlas.name +
                        ". Keep those inputs in one ALTO group, split the existing material merge, or choose Preserve/Replace Fully Covered.");
                state.ChainedMaterialGroups[pair.Key] = pair.Value.Single();
            }
        }
    }
}
