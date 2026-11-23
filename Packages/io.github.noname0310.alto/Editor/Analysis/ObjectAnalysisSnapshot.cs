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
        private AvatarAnimationIndex index;
        private AvatarDependencyGraph transforms, objects;
        private TransformMutationAnalysis mutations;
        private SerializedReferenceSnapshot references;

        internal ObjectAnalysisSnapshot(GameObject avatar, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths, ObjectOptimizationState settings,
            IEnumerable<string> preserved, ShaderPropertyUsage shaders)
        {
            this.avatar = avatar; this.controllers = controllers; this.paths = paths;
            this.settings = settings; this.preserved = preserved; this.shaders = shaders;
        }

        internal AvatarAnimationIndex Index => index ??= AvatarAnimationIndex.Create(avatar, controllers, paths, preserved);
        internal TransformMutationAnalysis Mutations => mutations ??= TransformMutationAnalysis.Create(avatar, Index,
            settings.AssumeFixedHumanoidBoneScale, references ??= new());
        internal AvatarDependencyGraph Dependencies(bool collectObjects) => collectObjects
            ? objects ??= AvatarDependencyGraph.Create(avatar, Index, settings, true, shaders, references ??= new())
            : transforms ??= AvatarDependencyGraph.Create(avatar, Index, settings, false, shaders, references ??= new());

        internal int Rewrite(Func<int> run)
        {
            int destructionVersion = settings.DestructionVersion;
            bool unchanged = false;
            try
            {
                int changes = run();
                // A deletion veto may still have run an editor callback with
                // other side effects. Do not reuse facts across that callback.
                unchanged = changes == 0 && settings.DestructionVersion == destructionVersion;
                return changes;
            }
            finally { if (!unchanged) Invalidate(); }
        }

        internal void Invalidate() { index = null; transforms = objects = null; references = null; mutations = null; }
    }
}
