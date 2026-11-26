using System;
using System.Collections.Generic;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.ObjectOptimization;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // A lexical transaction for consecutive object rewrites. A no-op preserves
    // its facts; a mutation or exception drops every dependent fact immediately.
    // Do not carry this snapshot across sensor-policy changes, other optimizers,
    // external callbacks or NDMF boundaries.
    internal sealed class ObjectAnalysisSnapshot
    {
        private readonly GameObject avatar;
        private readonly IDictionary<object, VirtualAnimatorController> controllers;
        private readonly IReadOnlyDictionary<string, string> paths;
        private readonly ObjectOptimizationState settings;
        private readonly IEnumerable<string> preserved;
        private readonly ShaderPropertyUsage shaders;
        private readonly AvatarAnalysisSnapshot shared;
        private AvatarAnimationIndex index;
        private AvatarDependencyGraph transforms, objects;
        private TransformMutationAnalysis mutations;
        private SerializedReferenceSnapshot references;
        private InitialPoseReaders.Inputs initialReaders;

        internal ObjectAnalysisSnapshot(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths, ObjectOptimizationState settings,
            IEnumerable<string> preserved, ShaderPropertyUsage shaders, AvatarAnalysisSnapshot shared = null)
        {
            AnalysisCost.Count("Object snapshot/created");
            this.avatar = avatar; this.controllers = controllers; this.paths = paths;
            this.settings = settings; this.preserved = preserved; this.shaders = shaders;
            this.shared = shared;
        }

        internal AvatarAnimationIndex Index
        {
            get
            {
                if (index != null) { AnalysisCost.Count("Object snapshot/animation index reused"); return index; }
                using var cost = AnalysisCost.Measure("Object snapshot/animation index built");
                return index = shared?.Index ?? AvatarAnimationIndex.Create(avatar, controllers, paths, preserved);
            }
        }
        internal TransformMutationAnalysis Mutations => mutations ??= TransformMutationAnalysis.Create(avatar, Index,
            settings.AssumeFixedHumanoidBoneScale, References);
        // Consumers using these facts must finish planning before applying their
        // first edit. Rewrite invalidates this closure along with its references.
        internal InitialPoseReaders.Inputs InitialReaders => initialReaders ??=
            InitialPoseReaders.Analyze(avatar, References, Index, reuseNativeReferences: true);
        private SerializedReferenceSnapshot References => references ??= shared?.References ?? new();
        internal AvatarDependencyGraph Dependencies(bool collectObjects) => collectObjects
            ? objects ??= AvatarDependencyGraph.Create(avatar, Index, settings, true, shaders, References)
            : transforms ??= AvatarDependencyGraph.Create(avatar, Index, settings, false, shaders, References);

        internal int Rewrite(Func<int> run)
        {
            using var cost = AnalysisCost.Measure("Object snapshot/rewrite transaction");
            int destructionVersion = settings.DestructionVersion;
            bool unchanged = false, completed = false;
            try
            {
                int changes = run();
                // A deletion veto may still have run an editor callback with
                // other side effects. Do not reuse facts across that callback.
                unchanged = changes == 0 && settings.DestructionVersion == destructionVersion;
                if (unchanged) AnalysisCost.Count("Object snapshot/unchanged transaction");
                completed = true;
                return changes;
            }
            finally { if (!unchanged) Invalidate(!completed || settings.DestructionVersion != destructionVersion); }
        }

        internal void Invalidate(bool externalMutation = false)
        {
            using var cost = AnalysisCost.Measure("Object snapshot/invalidation requested");
            if (index != null || transforms != null || objects != null || references != null || mutations != null || initialReaders != null)
                AnalysisCost.Count("Object snapshot/captured facts discarded");
            index = null; transforms = objects = null; references = null; mutations = null; initialReaders = null;
            shared?.Invalidate(externalMutation);
        }
    }
}
