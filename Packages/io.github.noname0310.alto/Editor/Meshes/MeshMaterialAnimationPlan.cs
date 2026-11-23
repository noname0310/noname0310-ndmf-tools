using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;

namespace Noname.AvatarTools.Meshes
{
    // A renderer-wide property block can carry independent outputs together.
    // Prove interference per whole uniform (including implicit vector channels),
    // then retain one original writer profile and relocate it without blending it
    // with previously unrelated writers. Native defaults remain a separate proof.
    internal sealed class MeshMaterialAnimationPlan
    {
        internal readonly HashSet<AvatarAnimationIndex.BindingUse> Retained = new();
        internal readonly List<(MeshMergeCandidate Source, string Property, float Previous, float Retained)> DefaultChanges = new();
        internal string Issue;

        private static Dictionary<string, MeshAnimationSignal> Profile(MeshMergeCandidate candidate, string property) =>
            candidate.Properties.Where(p => MaterialAnimationInputs.PropertyName(p.Key) == property)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        private static bool SameProfile(IReadOnlyDictionary<string, MeshAnimationSignal> a, IReadOnlyDictionary<string, MeshAnimationSignal> b) =>
            a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var signal) && p.Value.Equals(signal));

        internal static MeshMaterialAnimationPlan Create(IReadOnlyList<MeshMergeCandidate> group, MeshMergePolicy policy = null,
            bool validateDefaults = true)
        {
            var result = new MeshMaterialAnimationPlan();
            if (group.Count == 0) return result;
            var destination = group[0];
            var properties = group.SelectMany(c => c.Properties.Keys).Select(MaterialAnimationInputs.PropertyName).Distinct().ToArray();
            // Every writer that may be introduced by this merge must stay dynamic
            // in every reader proof. Freezing only the currently queried property
            // would allow one newly introduced writer to invalidate another proof.
            var changingUniforms = new HashSet<string>(properties.Where(p => p != null), StringComparer.Ordinal);
            var prepared = new HashSet<MeshMergeCandidate>();
            bool Reads(MeshMergeCandidate candidate, string property)
            {
                var shaders = candidate.Shaders ??= new ShaderPropertyUsage();
                var materials = candidate.PossibleMaterials.ToArray();
                if (prepared.Add(candidate))
                    foreach (var shader in materials.Where(m => m != null).Select(m => m.shader).Distinct())
                        shaders.PrimePropertyReads(shader, properties, policy?.Target);
                return materials.Any(m => m == null || m.shader == null ||
                    shaders.MayRead(m, candidate.Renderer, candidate.Animations, property, changingUniforms, policy?.Target));
            }
            foreach (string property in properties)
            {
                if (property == null) { result.Issue = "unsupported material animation binding"; return result; }
                var profiles = group.ToDictionary(c => c, c => Profile(c, property));
                bool identical = group.All(c => SameProfile(profiles[destination], profiles[c]));
                // Preserve the established identical-profile path without needing
                // a shader absence proof. Only differences require reader analysis.
                var readers = identical ? group.ToArray() : group.Where(c => Reads(c, property)).ToArray();
                if (readers.Length == 0) continue;
                var producer = identical ? destination : readers[0];
                if (readers.Any(c => !SameProfile(profiles[producer], profiles[c])))
                { result.Issue = "interfering material-property animation signals: " + property; return result; }
                var profile = profiles[producer];
                // Every observable material uses its own unanimated value. Dead
                // writers on non-reading inputs must not become new shared writes.
                if (profile.Count == 0) continue;
                if (validateDefaults && group.Count > 1)
                {
                    if (destination.MaterialSwaps.Values.Contains(0) || readers.Any(c => c.MaterialSwaps.Values.Contains(0)))
                    { result.Issue = "animated first material can change renderer-wide property defaults"; return result; }
                    string[] channels = profile.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
                    foreach (var reader in readers)
                    {
                        bool sameDestinationWriters = SameProfile(profiles[destination], profile);
                        bool allowDefaults = policy?.AllowsAnimatedMaterialDefaultChanges(destination.Renderer) == true &&
                            policy.AllowsAnimatedMaterialDefaultChanges(reader.Renderer);
                        string issue = destination.Animations.MaterialInputs.RelocationIncompatibility(destination.Renderer,
                            reader.Renderer, property, channels, sameDestinationWriters, allowDefaults);
                        if (issue != null) { result.Issue = issue; return result; }
                        if (allowDefaults && reader != destination)
                            foreach (var change in destination.Animations.MaterialInputs.RelocationDefaultChanges(destination.Renderer,
                                reader.Renderer, property, channels, sameDestinationWriters))
                                result.DefaultChanges.Add((reader, change.Property, change.Previous, change.Retained));
                    }
                }
                foreach (var signal in profile.Values) result.Retained.UnionWith(signal.Writers);
            }
            return result;
        }
    }
}
