using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.Installer
{
    [Serializable]
    internal sealed class MeshRegionPresetMatch
    {
        public SkinnedMeshRenderer Renderer;
        public MeshRegionPreset Preset;
        public bool Exact;
        public float Similarity;
    }

    internal static class MeshRegionPresetDiscovery
    {
        // Version the encodings so a later matching algorithm cannot reinterpret existing presets.
        private const string PositionsVersion = "positions-v1:";
        private const string DistributionVersion = "distribution-v1:";
        internal const float SuggestionThreshold = 0.82f;

        internal static MeshRegionPreset[] LoadPresets() => AssetDatabase.FindAssets("t:MeshRegionPreset")
            .Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal)
            .Select(AssetDatabase.LoadAssetAtPath<MeshRegionPreset>).Where(p => p != null).ToArray();

        internal static MeshGeometryFingerprint Fingerprint(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable || mesh.vertexCount == 0) return null;
            // UV seams and vertex ordering can change during FBX export. Use the distinct base
            // positions, not vertex indices, weights, bones, normals, UVs or BlendShape deltas.
            var points = mesh.vertices.Distinct().OrderBy(v => v.x).ThenBy(v => v.y).ThenBy(v => v.z).ToArray();
            if (points.Any(p => !Finite(p.x) || !Finite(p.y) || !Finite(p.z))) return null;
            string exact;
            using (var stream = new MemoryStream())
            {
                using var writer = new BinaryWriter(stream);
                writer.Write(points.Length);
                foreach (var p in points)
                {
                    writer.Write(p.x == 0 ? 0f : p.x); writer.Write(p.y == 0 ? 0f : p.y); writer.Write(p.z == 0 ? 0f : p.z);
                }
                writer.Flush();
                using var sha = SHA256.Create();
                exact = Hex(sha.ComputeHash(stream.ToArray()));
            }
            return new MeshGeometryFingerprint { PositionsSha256 = PositionsVersion + exact,
                DistributionSignature = DistributionVersion + Distribution(points) };
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        private static string Distribution(Vector3[] points)
        {
            var bounds = new Bounds(points[0], Vector3.zero);
            foreach (var point in points) bounds.Encapsulate(point);
            float size = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            if (size <= 0 || !Finite(size)) return "";
            var normalized = points.Select(p => (p - bounds.center) / size + Vector3.one * 0.5f).ToArray();
            var bytes = new byte[32];
            // Four 64-bit SimHash summaries of occupied spatial cells. We retain neither the
            // cells nor sampled coordinates. This measures signature agreement, not vertex error.
            for (int level = 0; level < 4; level++)
            {
                int resolution = 4 << level;
                var cells = new HashSet<int>();
                foreach (var p in normalized)
                {
                    int x = Mathf.Clamp(Mathf.FloorToInt(p.x * resolution), 0, resolution - 1);
                    int y = Mathf.Clamp(Mathf.FloorToInt(p.y * resolution), 0, resolution - 1);
                    int z = Mathf.Clamp(Mathf.FloorToInt(p.z * resolution), 0, resolution - 1);
                    cells.Add(x + resolution * (y + resolution * z));
                }
                var votes = new int[64];
                foreach (int cell in cells)
                {
                    ulong hash = Mix((ulong)cell + (ulong)resolution * 0x100000001b3UL);
                    for (int bit = 0; bit < 64; bit++) votes[bit] += ((hash >> bit) & 1) != 0 ? 1 : -1;
                }
                for (int bit = 0; bit < 64; bit++)
                    if (votes[bit] > 0) bytes[level * 8 + bit / 8] |= (byte)(1 << (bit % 8));
            }
            return Hex(bytes);
        }

        private static ulong Mix(ulong value)
        {
            unchecked
            {
                value += 0x9e3779b97f4a7c15UL;
                value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
                value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
                return value ^ (value >> 31);
            }
        }

        internal static float Similarity(string first, string second)
        {
            if (!TrySignature(first, out var a) || !TrySignature(second, out var b)) return 0;
            int different = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int bits = a[i] ^ b[i];
                while (bits != 0) { different++; bits &= bits - 1; }
            }
            return 1f - different / 256f;
        }

        private static bool TrySignature(string text, out byte[] result)
        {
            result = null;
            if (text == null || !text.StartsWith(DistributionVersion, StringComparison.Ordinal) || text.Length != DistributionVersion.Length + 64) return false;
            string hex = text.Substring(DistributionVersion.Length);
            if (hex.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))) return false;
            result = Enumerable.Range(0, 32).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
            return true;
        }

        internal static bool HasRegions(Mesh mesh, MeshRegionPreset preset) => mesh != null && preset != null &&
            preset.BlendShapes != null && preset.BlendShapes.Count > 0 && preset.BlendShapes.All(n => !string.IsNullOrWhiteSpace(n) && mesh.GetBlendShapeIndex(n) >= 0);

        internal static List<MeshRegionPresetMatch> Find(VRCAvatarDescriptor avatar, IEnumerable<MeshRegionPreset> presets)
        {
            var matches = new List<MeshRegionPresetMatch>();
            if (avatar == null) return matches;
            var available = presets.Where(p => p != null).Distinct().ToArray();
            var fingerprints = new Dictionary<Mesh, MeshGeometryFingerprint>();
            foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.GetComponentInParent<VRCAvatarDescriptor>(true) != avatar || ALTOPlugin.IsEditorOnly(renderer.transform)) continue;
                var candidates = available.Where(p => HasRegions(renderer.sharedMesh, p)).ToArray();
                if (candidates.Length == 0) continue;
                if (!fingerprints.TryGetValue(renderer.sharedMesh, out var fingerprint))
                    fingerprints[renderer.sharedMesh] = fingerprint = Fingerprint(renderer.sharedMesh);
                if (fingerprint == null) continue;
                foreach (var preset in candidates)
                {
                    bool exact = preset.Fingerprints.Any(f => f != null && f.PositionsSha256 == fingerprint.PositionsSha256);
                    float similarity = preset.Fingerprints.Where(f => f != null)
                        .Select(f => Similarity(f.DistributionSignature, fingerprint.DistributionSignature)).DefaultIfEmpty(0).Max();
                    if (exact || similarity >= SuggestionThreshold)
                        matches.Add(new MeshRegionPresetMatch { Renderer = renderer, Preset = preset, Exact = exact, Similarity = similarity });
                }
            }
            return matches.OrderByDescending(m => m.Exact).ThenByDescending(m => m.Similarity).ToList();
        }

        internal static void ApplyInitialExactMatches(InstallerDraft draft, IEnumerable<MeshRegionPresetMatch> matches)
        {
            foreach (var group in matches.Where(m => m.Exact).GroupBy(m => m.Renderer))
                if (group.Count() == 1) Apply(draft, group.Single());
        }

        internal static void Apply(InstallerDraft draft, MeshRegionPresetMatch match)
        {
            if (match == null || match.Renderer == null || draft.Avatar == null ||
                match.Renderer.GetComponentInParent<VRCAvatarDescriptor>(true) != draft.Avatar ||
                !HasRegions(match.Renderer.sharedMesh, match.Preset)) return;
            var row = draft.BlendShapeExclusions.FirstOrDefault(r => r != null && r.Renderer == match.Renderer);
            if (row == null) draft.BlendShapeExclusions.Add(row = new BlendShapeMeshExclusion { Renderer = match.Renderer });
            foreach (var name in match.Preset.BlendShapes) if (!row.BlendShapes.Contains(name)) row.BlendShapes.Add(name);
        }
    }
}
