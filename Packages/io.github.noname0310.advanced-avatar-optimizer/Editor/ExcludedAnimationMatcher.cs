using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization
{
    internal sealed class ExcludedAnimationMatcher
    {
        // NDMF currently exposes provenance only internally. This optional lookup is isolated here;
        // tools which discard provenance are handled by verified name-and-curve matching below.
        private readonly HashSet<AnimationClip> sources;
        private readonly Dictionary<AnimationClip, HashSet<string>> signatures = new();
        private readonly Dictionary<VirtualClip, bool> matches = new();
        private readonly HashSet<AnimationClip> matchedSources = new();
        internal IEnumerable<AnimationClip> Unmatched => sources.Except(matchedSources);

        internal ExcludedAnimationMatcher(IEnumerable<AnimationClip> clips)
            => sources = new HashSet<AnimationClip>((clips ?? Array.Empty<AnimationClip>()).Where(c => c != null));

        internal bool Matches(VirtualClip clip)
        {
            if (sources.Count == 0) return false;
            if (matches.TryGetValue(clip, out bool matched)) return matched;
            var original = MotionAnalysis.Source(clip);
            var source = original != null ? ObjectRegistry.GetReference(original).Object as AnimationClip : null;
            if (source != null && sources.Contains(source))
            {
                matchedSources.Add(source);
                return matches[clip] = true;
            }

            var candidates = sources.Where(c => CompatibleName(clip.Name, c.name)).ToArray();
            if (candidates.Length == 0) return matches[clip] = false;
            var curves = clip.GetFloatCurveBindings().Where(IsBlendShape)
                .Select(b => Signature(b.propertyName, clip.GetFloatCurve(b))).ToHashSet();
            if (curves.Count == 0) return matches[clip] = false;
            foreach (var candidate in candidates)
            {
                if (!signatures.TryGetValue(candidate, out var expected))
                    signatures[candidate] = expected = AnimationUtility.GetCurveBindings(candidate).Where(IsBlendShape)
                        .Select(b => Signature(b.propertyName, AnimationUtility.GetEditorCurve(candidate, b))).ToHashSet();
                // Path rebasing and removal of unavailable bindings are allowed. Changed or added
                // BlendShape curves are not evidence that an unrelated clip is the selected source.
                if (!curves.IsSubsetOf(expected)) continue;
                matchedSources.Add(candidate);
                matched = true;
            }
            return matches[clip] = matched;
        }

        private static bool CompatibleName(string actual, string expected)
        {
            while (actual.EndsWith("(Clone)", StringComparison.Ordinal)) actual = actual[..^7].TrimEnd();
            return actual == expected || actual.EndsWith(" " + expected, StringComparison.Ordinal);
        }

        private static bool IsBlendShape(EditorCurveBinding binding)
            => binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal);

        private static string Signature(string property, AnimationCurve curve) => ContentHash.Compute(writer =>
        {
            writer.Write(property);
            if (curve == null) { writer.Write(-1); return; }
            writer.Write(curve.length);
            foreach (var key in curve.keys)
            {
                writer.Write(key.time); writer.Write(key.value);
                writer.Write(key.inTangent); writer.Write(key.outTangent);
                writer.Write((int)key.weightedMode); writer.Write(key.inWeight); writer.Write(key.outWeight);
            }
        });
    }
}
