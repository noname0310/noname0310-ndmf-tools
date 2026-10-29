using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.Installer
{
    internal sealed class BodyMeshCandidate
    {
        internal SkinnedMeshRenderer Renderer;
        internal int Score;
    }

    // Authoring suggestions only, never a build-time proof that a surface is skin or hidden.
    // Uses generic naming and descriptor evidence, without an avatar or asset-package dependency.
    internal static class BodyMeshDiscovery
    {
        private static readonly string[] BodyWords = { "body", "skin", "torso", "nude", "naked", "hada", "\u7d20\u4f53", "\u808c" };
        private static readonly string[] FaceWords = { "face", "facial", "head", "eye", "eyes", "iris", "pupil", "mouth", "teeth",
            "tongue", "lip", "brow", "eyelash", "hair", "\u9854", "\u9aea" };
        private static readonly string[] ClothingWords = { "cloth", "clothing", "dress", "shirt", "pants", "skirt", "stocking", "sock",
            "shoe", "boot", "jacket", "coat", "suit", "uniform", "costume", "bra", "underwear", "accessory" };
        private static readonly string[] BodyRegions = { "bust", "breast", "chest", "waist", "belly", "hip", "thigh", "leg", "arm", "shoulder" };
        private static readonly string[] FaceRegions = { "eye", "mouth", "lip", "brow", "blink", "viseme", "tongue", "nose", "cheek" };

        internal static SkinnedMeshRenderer Find(VRCAvatarDescriptor avatar)
        {
            var ranked = Rank(avatar);
            if (ranked.Length == 0 || ranked[0].Score < 6 || ranked.Length > 1 && ranked[0].Score - ranked[1].Score < 3)
                return null;
            return ranked[0].Renderer;
        }

        internal static BodyMeshCandidate[] Rank(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return Array.Empty<BodyMeshCandidate>();
            return avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0 &&
                    r.GetComponentInParent<VRCAvatarDescriptor>(true) == avatar && !ALTOPlugin.IsEditorOnly(r.transform))
                .Select(r => new BodyMeshCandidate { Renderer = r, Score = Score(avatar, r) })
                .OrderByDescending(c => c.Score).ToArray();
        }

        private static int Score(VRCAvatarDescriptor avatar, SkinnedMeshRenderer renderer)
        {
            var mesh = renderer.sharedMesh;
            int score = LabelScore(renderer.name, 5, -12, -16) + LabelScore(mesh.name, 2, -3, -6);
            // A combined face/body mesh may legitimately contain both kinds of material.
            // Score its best body surface rather than penalizing every facial submesh.
            var materials = renderer.sharedMaterials.Where(m => m != null).ToArray();
            if (materials.Length > 0) score += materials.Max(m => LabelScore(m.name, 8, -8, -6));
            var regions = new HashSet<string>(StringComparer.Ordinal);
            int facialShapes = 0;
            for (int index = 0; index < mesh.blendShapeCount; index++)
            {
                string name = mesh.GetBlendShapeName(index);
                var words = Words(name);
                foreach (var region in BodyRegions) if (Matches(words, region)) regions.Add(region);
                if (name.StartsWith("vrc.v", StringComparison.OrdinalIgnoreCase) || FaceRegions.Any(w => Matches(words, w))) facialShapes++;
            }
            score += Math.Min(4, regions.Count);
            // Descriptor references are evidence, not an exclusion: unified meshes remain candidates.
            if (avatar.VisemeSkinnedMesh == renderer || avatar.customEyeLookSettings.eyelidsSkinnedMesh == renderer) score -= 6;
            else if (facialShapes >= 6) score -= 4;
            return score;
        }

        private static int LabelScore(string label, int body, int face, int clothing)
        {
            var words = Words(label);
            // A bodysuit is clothing even though its name also contains "body".
            if (ClothingWords.Any(w => Matches(words, w))) return clothing;
            if (FaceWords.Any(w => Matches(words, w))) return face;
            return BodyWords.Any(w => Matches(words, w)) ? body : 0;
        }

        private static string[] Words(string value) => string.IsNullOrEmpty(value) ? Array.Empty<string>() :
            new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        private static bool Matches(IEnumerable<string> tokens, string word) => tokens.Any(token => token == word ||
            word.Length >= 4 && (token.StartsWith(word, StringComparison.Ordinal) || token.EndsWith(word, StringComparison.Ordinal)));
    }
}
