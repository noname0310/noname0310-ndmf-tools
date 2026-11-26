using System;
using System.Collections.Generic;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using Noname.AvatarTools.Build;

namespace Noname.AvatarTools.Analysis
{
    // A build-owned, read-only epoch. Query assumptions are part of its identity;
    // mutable protection/liveness graphs remain owned by their individual passes.
    // Every rewrite must invalidate the epoch before asking another question.
    internal sealed class AvatarAnalysisSnapshot
    {
        internal readonly GameObject Avatar;
        internal readonly IDictionary<object, VirtualAnimatorController> Entries;
        internal readonly IReadOnlyDictionary<string, string> Paths;
        internal readonly HashSet<string> Preserved;
        private readonly VirtualAnimatorController[] controllers;
        private readonly Action<AnalysisDomain> invalidate;
        private readonly Func<SerializedReferenceSnapshot> references;
        private AvatarAnimationIndex index;
        private AnimatorReachability reachability;
        private AnimatorInstanceContexts.Context[] instances;
        private bool instancesCaptured;
        private readonly AnimatorParameterDependencies[] dependencies = new AnimatorParameterDependencies[2];

        internal AvatarAnalysisSnapshot(GameObject avatar, IDictionary<object, VirtualAnimatorController> entries,
            IReadOnlyDictionary<string, string> paths, IEnumerable<string> preserved,
            Action<AnalysisDomain> invalidate, Func<SerializedReferenceSnapshot> references)
        {
            Avatar = avatar;
            Entries = new Dictionary<object, VirtualAnimatorController>(entries);
            if (paths != null)
            {
                var copy = new Dictionary<string, string>(paths.Count, StringComparer.Ordinal);
                foreach (var pair in paths) copy.Add(pair.Key, pair.Value);
                Paths = copy;
            }
            Preserved = new HashSet<string>(preserved ?? Array.Empty<string>(), StringComparer.Ordinal);
            var unique = new HashSet<VirtualAnimatorController>();
            var ordered = new List<VirtualAnimatorController>();
            foreach (var pair in Entries) if (unique.Add(pair.Value)) ordered.Add(pair.Value);
            controllers = ordered.ToArray();
            this.invalidate = invalidate;
            this.references = references;
        }

        // Controller order is retained as well as ownership. Child Animator roots
        // and playable-layer roots cannot share a proof just because clips match.
        internal bool Matches(GameObject avatar, IDictionary<object, VirtualAnimatorController> entries,
            IReadOnlyDictionary<string, string> paths, HashSet<string> preserved)
        {
            if (Avatar != avatar || Entries.Count != entries.Count || (Paths == null) != (paths == null) ||
                !Preserved.SetEquals(preserved)) return false;
            using var left = Entries.GetEnumerator();
            foreach (var pair in entries)
            {
                if (!left.MoveNext() || !Equals(left.Current.Key, pair.Key) || left.Current.Value != pair.Value) return false;
            }
            if (Paths != null)
            {
                if (Paths.Count != paths.Count) return false;
                foreach (var pair in paths)
                    if (!Paths.TryGetValue(pair.Key, out var value) || value != pair.Value) return false;
            }
            return true;
        }

        internal AvatarAnimationIndex Index => index ??= AvatarAnimationIndex.Create(Avatar, Entries, Paths, Preserved,
            Preserved.Count == 0 ? null : Dependencies(true), () => Reachability);
        internal AnimatorReachability Reachability => reachability ??= AnimatorReachability.Analyze(Avatar, controllers, Preserved);
        internal SerializedReferenceSnapshot References => references();

        private AnimatorInstanceContexts.Context[] Instances()
        {
            if (!instancesCaptured)
            {
                instances = AnimatorInstanceContexts.Analyze(Avatar, controllers, Entries);
                instancesCaptured = true;
            }
            return instances;
        }

        internal AnimatorParameterDependencies Dependencies(bool forExpressions,
            Func<VirtualClip, EditorCurveBinding, bool> observable = null, AnimatorReachability analysis = null)
        {
            // A caller's output predicate/input bounds are not an interchangeable
            // assumption. Only share the verified local/remote identity contexts.
            if (observable != null || analysis != null)
                return AnimatorParameterDependencies.Collect(Avatar, controllers, forExpressions, observable, analysis, Entries, Instances);
            int key = forExpressions ? 1 : 0;
            return dependencies[key] ??= AnimatorParameterDependencies.Collect(Avatar, controllers, forExpressions,
                controllerEntries: Entries, instanceContexts: Instances);
        }

        internal void Invalidate(bool externalMutation = false)
        {
            Reset();
            invalidate(externalMutation ? AnalysisDomain.All : AnalysisDomain.All & ~AnalysisDomain.ShaderSources);
        }
        internal void Reset()
        {
            index = null; reachability = null; instances = null; instancesCaptured = false;
            Array.Clear(dependencies, 0, dependencies.Length);
        }

        internal void Invalidate(AnalysisDomain changed)
        {
            // Parameter flow reads controller graphs, expression declarations,
            // sensor declarations and native tracking settings. Mesh/material
            // data do not change these inputs. Ownership/hierarchy edits do.
            const AnalysisDomain parameterInputs = AnalysisDomain.Animation | AnalysisDomain.Hierarchy |
                AnalysisDomain.Physics | AnalysisDomain.ExternalInputs;
            if ((changed & parameterInputs) != 0)
            {
                index = null; reachability = null; instances = null; instancesCaptured = false;
                Array.Clear(dependencies, 0, dependencies.Length);
                AnalysisCost.Count("Avatar snapshot/parameter facts dirtied");
            }
            else
            {
                if ((changed & AnalysisDomain.Meshes) != 0)
                {
                    // Native output liveness can depend on BlendShape presence
                    // without changing the runtime parameter value domain. Drop
                    // those backwards proofs independently of reachability.
                    Array.Clear(dependencies, 0, dependencies.Length);
                    if (Preserved.Count != 0) index = null;
                }
                // The binding inventory is unchanged, but default values and
                // consumers may have changed. Never retain those derived proofs.
                index?.InvalidateOutputs();
                AnalysisCost.Count("Avatar snapshot/parameter facts retained");
            }
        }
    }
}
