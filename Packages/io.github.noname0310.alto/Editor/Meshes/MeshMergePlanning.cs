using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using UnityEngine;

namespace Noname.AvatarTools.Meshes
{
    // One read-only planning epoch. A mesh/material/animation rewrite ends it.
    // The ordered group includes the retained renderer: pairwise compatibility
    // alone cannot prove renderer-wide defaults or newly introduced writers.
    internal sealed class MeshMergePlanning
    {
        internal static bool Reuse = true;
        private readonly MeshMergePolicy policy;
        private readonly Dictionary<(MeshMergeCandidate, MeshMergeCandidate, bool), string> pairs = new();
        private readonly Dictionary<GroupKey, MeshMaterialAnimationPlan> plans = new();
        private readonly Dictionary<MeshMergeCandidate, Candidate> candidates = new();
        private readonly Dictionary<(MeshMergeCandidate, string, string), bool> reads = new();

        private sealed class GroupKey : IEquatable<GroupKey>
        {
            private readonly MeshMergeCandidate[] members;
            private readonly int hash;
            internal GroupKey(IReadOnlyList<MeshMergeCandidate> group)
            {
                members = new MeshMergeCandidate[group.Count];
                unchecked
                {
                    hash = 17;
                    for (int i = 0; i < group.Count; i++)
                    { members[i] = group[i]; hash = hash * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(group[i]); }
                }
            }
            public bool Equals(GroupKey other)
            {
                if (other == null || hash != other.hash || members.Length != other.members.Length) return false;
                for (int i = 0; i < members.Length; i++) if (!ReferenceEquals(members[i], other.members[i])) return false;
                return true;
            }
            public override bool Equals(object obj) => obj is GroupKey key && Equals(key);
            public override int GetHashCode() => hash;
        }

        private sealed class Candidate
        {
            internal Material[] Materials;
            internal readonly Dictionary<string, Dictionary<string, MeshAnimationSignal>> Profiles = new(StringComparer.Ordinal);
        }

        internal MeshMergePlanning(MeshMergePolicy policy) { this.policy = policy; }
        internal void Clear() { pairs.Clear(); plans.Clear(); candidates.Clear(); reads.Clear(); }

        internal string Incompatibility(MeshMergeCandidate first, MeshMergeCandidate second, bool materialAnimations = true)
        {
            var key = (first, second, materialAnimations);
            if (Reuse && pairs.TryGetValue(key, out var previous))
            { AnalysisCost.Count("Mesh planning/pair reuse"); return previous; }
            using var cost = AnalysisCost.Measure("Mesh planning/pair inspection");
            string result = MeshMergeAnalysis.Incompatibility(first, second, policy, materialAnimations, this);
            if (Reuse) pairs[key] = result;
            return result;
        }

        internal MeshMaterialAnimationPlan Plan(IReadOnlyList<MeshMergeCandidate> group)
        {
            var key = Reuse ? new GroupKey(group) : null;
            if (key != null && plans.TryGetValue(key, out var previous))
            { AnalysisCost.Count("Mesh planning/group reuse"); return previous; }
            var result = MeshMaterialAnimationPlan.Create(group, policy, planning: this);
            if (key != null) plans.Add(key, result);
            return result;
        }

        private Candidate Get(MeshMergeCandidate candidate)
        {
            if (Reuse && candidates.TryGetValue(candidate, out var previous)) return previous;
            var result = new Candidate { Materials = candidate.PossibleMaterials.ToArray() };
            foreach (var pair in candidate.Properties)
            {
                string property = MaterialAnimationInputs.PropertyName(pair.Key);
                if (property == null) continue;
                if (!result.Profiles.TryGetValue(property, out var profile))
                    result.Profiles.Add(property, profile = new(StringComparer.Ordinal));
                profile.Add(pair.Key, pair.Value);
            }
            if (Reuse) candidates.Add(candidate, result);
            return result;
        }
        internal Dictionary<string, MeshAnimationSignal> Profile(MeshMergeCandidate candidate, string property)
            => Get(candidate).Profiles.TryGetValue(property, out var profile) ? profile : EmptyProfile;
        private static readonly Dictionary<string, MeshAnimationSignal> EmptyProfile = new(StringComparer.Ordinal);

        internal bool Reads(MeshMergeCandidate candidate, string property, string[] properties,
            HashSet<string> changingUniforms, string writers)
        {
            var key = (candidate, property, writers);
            if (Reuse && reads.TryGetValue(key, out bool previous))
            { AnalysisCost.Count("Mesh planning/reader reuse"); return previous; }
            using var cost = AnalysisCost.Measure("Mesh planning/material readers");
            var shaders = candidate.Shaders ??= new ShaderPropertyUsage();
            var materials = Get(candidate).Materials;
            using (AnalysisCost.Measure("Mesh planning/source reader proof"))
                foreach (var shader in materials.Where(m => m != null).Select(m => m.shader).Distinct())
                    shaders.PrimePropertyReads(shader, properties, policy?.Target);
            bool result;
            using (AnalysisCost.Measure("Mesh planning/material reader proof"))
                result = materials.Any(m => m == null || m.shader == null ||
                    shaders.MayRead(m, candidate.Renderer, candidate.Animations, property, changingUniforms, policy?.Target));
            if (Reuse) reads[key] = result;
            return result;
        }
    }
}
