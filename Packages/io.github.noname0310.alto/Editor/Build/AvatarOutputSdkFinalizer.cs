using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.Physics;
using Noname.AvatarTools.ParameterPolicy;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Editor.BuildPipeline;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Build
{
    // A direct NDMF bake performs cleanup in its optimizing pass. SDK builds defer it until
    // late preprocessors have consumed the original menus (notably VRCFury's compressor).
    // Weak keys also release sessions when another callback cancels the build before ours.
    internal static class AvatarOutputSdkFinalizer
    {
        private sealed class Session
        {
            internal ExpressionSettings Settings;
            internal ISet<string> Frozen, LocalCandidates;
            internal OptimizationBuildReport Report;
            internal Object AssetContainer;
            internal IObjectRegistry Registry;
            internal BuildContext Context;
        }

        private static readonly ConditionalWeakTable<GameObject, Session> Sessions = new();

        internal static void Begin(GameObject avatar)
        {
            Sessions.Remove(avatar);
            Sessions.Add(avatar, new Session());
        }

        internal static void Capture(BuildContext context)
        {
            if (!Sessions.TryGetValue(context.AvatarRootObject, out var session)) return;
            session.Context = context;
            session.Report = context.GetState<OptimizationBuildReport>();
            session.AssetContainer = context.AssetSaver.CurrentContainer;
            session.Registry = ObjectRegistry.ActiveRegistry;
        }

        internal static bool HasSession(BuildContext context) => Sessions.TryGetValue(context.AvatarRootObject, out _);

        internal static void PrepareTransport(GameObject avatar, Action<Object> saveForTests = null)
        {
            if (!Sessions.TryGetValue(avatar, out var session) || session.Context == null) return;
            using var registry = new ObjectRegistryScope(session.Registry);
            // A late compressor may still inspect menus/authoring settings. Retain
            // those readers here; only settle their logical runtime outputs.
            string reason = ObjectOptimizationSafety.UnsupportedRuntime(avatar);
            if (reason == null)
                foreach (var (_, controller, _) in VRChatPlatformAnimatorBindings.Instance.GetInnateControllers(avatar))
                    if (controller != null && controller.animationClips.Any(c => c != null && c.events.Length != 0))
                    { reason = "animation events"; break; }
            if (reason == null)
                try { ParameterFreezeSdkFinalizer.ValidateNativeBehaviours(avatar); }
                catch (InvalidOperationException exception) { reason = exception.Message; }
            if (reason != null) { session.Report.Messages.Add("Transport input preparation retained outputs: " + reason); return; }
            using var measurement = OptimizationMeasurement.Begin(session.Report, "SDK preparation", "logical output convergence");
            Apply(avatar, session, saveForTests, prepareTransport: true);
            measurement?.Complete();
        }

        internal static bool Defer(BuildContext context, ExpressionSettings settings,
            ISet<string> frozen, OptimizationBuildReport report)
        {
            if (!Sessions.TryGetValue(context.AvatarRootObject, out var session)) return false;
            Capture(context);
            session.Settings = settings; session.Frozen = frozen; session.Report = report;
            // A compressor can make originally synchronized parameters local and add transport
            // declarations. Neither category is eligible for local-parameter pruning.
            var parameters = context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>()
                .expressionParameters?.parameters ?? Array.Empty<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter>();
            session.LocalCandidates = new HashSet<string>(parameters
                .Where(p => p != null && !p.networkSynced && !string.IsNullOrEmpty(p.name)).Select(p => p.name), StringComparer.Ordinal);
            session.LocalCandidates.ExceptWith(parameters.Where(p => p != null && p.networkSynced).Select(p => p.name));
            return true;
        }

        internal static void Complete(GameObject avatar, Action<Object> saveForTests = null)
        {
            if (!Sessions.TryGetValue(avatar, out var session)) return;
            Sessions.Remove(avatar);
            if (session.Context == null) return;
            // Selection checks also need the build's replacement registry, before
            // deciding whether there is any late filtering work to perform.
            using var registry = new ObjectRegistryScope(session.Registry);
            if (session.Settings?.Enabled != true &&
                !session.Context.GetState<ObjectOptimizationState>().Enabled &&
                session.Context.GetState<AnimatorOptimizationState>().Settings?.Enabled != true &&
                !session.Context.GetState<MeshOptimizationState>().Enabled &&
                session.Context.GetState<OptimizationState>().Scopes.Count == 0 &&
                !session.Context.GetState<Materials.MaterialOptimizationState>().BakeShaderInputs &&
                !session.Context.GetState<PhysBoneOptimizationState>().Enabled &&
                !session.Context.GetState<MaterialAtlas.MaterialAtlasState>().Pending &&
                !MeshRendererSettingsOverridePass.HasPendingOutputs(session.Context) &&
                !session.Context.GetState<MeshReduction.MeshReductionState>().Pending &&
                !session.Context.GetState<PhysBoneFilterState>().Applied &&
                !GeneratedOutputFilterPass.HasPendingRemovals(session.Context)) return;
            using var measurement = OptimizationMeasurement.Begin(session.Report, "SDK finalizer", "runtime output cleanup");
            string unsupported = ObjectOptimizationSafety.UnsupportedRuntime(avatar);
            if (unsupported == null)
                foreach (var (_, controller, _) in VRChatPlatformAnimatorBindings.Instance.GetInnateControllers(avatar))
                    if (controller != null && controller.animationClips.Any(c => c != null && c.events.Length != 0))
                    { unsupported = "late animation events may observe runtime outputs"; break; }
            if (unsupported == null)
                try { ParameterFreezeSdkFinalizer.ValidateNativeBehaviours(avatar); }
                catch (InvalidOperationException exception) { unsupported = exception.Message; }
            // Virtualization itself can discard unsupported behaviours and clip events.
            // Reject these native inputs before opening a context, even if no graph edit
            // would otherwise be selected. A skipped optimizer must leave them intact.
            if (unsupported == null) Apply(avatar, session, saveForTests);
            else
            {
                if (session.Context.GetState<MaterialAtlas.MaterialAtlasState>().Pending)
                    throw new InvalidOperationException("Final atlas packing cannot safely process generated animation inputs: " + unsupported);
                if (session.Context.GetState<MaterialAtlas.MaterialAtlasState>().Liveness.HasSurvivingInputs)
                    throw new InvalidOperationException("Cannot validate omitted atlas inputs after SDK preprocessing: " + unsupported);
                if (ContactFilterPass.HasPendingRemovals(session.Context))
                    throw new InvalidOperationException("Final Contact filtering cannot safely process the generated Animator: " + unsupported);
                if (AudioOutputFilterPass.HasPendingRemovals(session.Context) || PhysBoneFilterPass.HasPendingRemovals(session.Context))
                    throw new InvalidOperationException("Final audio/PhysBone filtering cannot safely process the generated Animator: " + unsupported);
                session.Report.Messages.Add("Final runtime output cleanup skipped: " + unsupported);
                MeshReduction.MeshReductionPass.ApplyFinal(session.Context, new Dictionary<object, VirtualAnimatorController>(),
                    null, saveForTests ?? session.Context.AssetSaver.SaveAsset, unsupported);
            }
            PhysBoneFilterPass.CheckBudget(session.Context);
            AvatarBuildAnalysis.PublishReport(avatar, session.Report);
            measurement?.Complete();
        }

        private static void Apply(GameObject avatar, Session session, Action<Object> saveForTests, bool prepareTransport = false)
        {
            if (saveForTests == null && (session.AssetContainer == null || !EditorUtility.IsPersistent(session.AssetContainer)))
                throw new InvalidOperationException("Runtime output cleanup requires the generated NDMF asset container after SDK preprocessing.");
            using var saver = saveForTests == null ? new SingleAssetSaver(session.AssetContainer) : null;
            // Reopen current controllers through NDMF's public serialization API. A fresh
            // virtual context includes late SDK edits and avoids reusing pre-SDK graphs or
            // path mappings. The original build's asset saver owns generated controllers.
            var current = new VirtualControllerContext();
            current.OnActivate(session.Context);
            var analysis = session.Context.GetState<OptimizationAnalysis>();
            analysis.Invalidate(AnalysisDomain.All);
            try
            {
                var finalEntries = current.Controllers.Where(e => !(e.Key is Object owner) || owner != null)
                    .ToDictionary(e => e.Key, e => e.Value);
                GeneratedOutputFilterPass.Apply(session.Context, finalEntries);
                var policy = ParameterPolicyPass.Resolve(session.Context, finalEntries);
                using var shapeBoundary = session.Context.GetState<OptimizationState>().Provenance.BeginBoundary();
                void SettleOutputs() => FinalOutputOptimization.Apply(avatar, finalEntries, policy, session.Frozen ??
                    new HashSet<string>(session.Context.GetState<ParameterPolicyState>().Constants.Keys, StringComparer.Ordinal),
                    session.Context.GetState<ObjectOptimizationState>(), session.Context.GetState<AnimatorOptimizationState>().Settings,
                    prepareTransport ? null : session.Settings, session.Report, saveForTests ?? saver.SaveAsset, session.LocalCandidates,
                    prepareTransport ? null : () => MeshOptimizationPass.ApplyFinal(session.Context, finalEntries, policy, saveForTests ?? saver.SaveAsset),
                    prepareTransport ? null : () => PhysBoneOptimizationPass.ApplyFinalChanges(session.Context, finalEntries, policy),
                    prepareTransport ? null : () => StaticTransformOptimizationPass.ApplyFinalChanges(session.Context, finalEntries, policy, mesh => (saveForTests ?? saver.SaveAsset)(mesh)),
                    null, session.Context.GetState<OptimizationAnalysis>().Shaders,
                    session.Context.GetState<OptimizationPolicy>(), session.Context.GetState<Materials.MaterialOptimizationState>(),
                    session.Context.GetState<MeshOptimizationState>(),
                    prepareTransport ? null : () => ALTOPlugin.ApplyFinal(session.Context, finalEntries, policy, saveForTests ?? saver.SaveAsset),
                    prepareTransport ? null : () => Materials.MaterialOptimizationPass.SpecializeFinalChanges(session.Context, finalEntries, policy,
                        null, saveForTests ?? saver.SaveAsset), analysis, runtimeDependenciesOnly: !prepareTransport);
                SettleOutputs();
                if (!prepareTransport && MaterialAtlas.MaterialAtlasPass.ApplyFinal(session.Context, finalEntries, null,
                    saveForTests ?? saver.SaveAsset)) SettleOutputs();
                if (!prepareTransport && session.Context.GetState<MeshReduction.MeshReductionState>().Pending)
                {
                    using var reduction = OptimizationMeasurement.Begin(session.Report, "SDK finalizer", "mesh reduction");
                    MeshReduction.MeshReductionPass.ApplyFinal(session.Context, finalEntries, null, saveForTests ?? saver.SaveAsset);
                    reduction?.Complete();
                    // Reduction changes topology once. Downstream assembly and dead
                    // output removal can now converge without reducing that result again.
                    SettleOutputs();
                }
                MaterialAtlas.MaterialAtlasPass.ValidateOmittedInputs(session.Context, finalEntries, null, policy.Preserved);
            }
            finally
            {
                try { AnimatorAssetPersistence.Commit(session.Context, current, saveForTests ?? saver.SaveAsset); }
                finally { analysis.Invalidate(AnalysisDomain.All); }
            }
        }
    }

    internal sealed class AvatarOutputSdkBuildStart : IVRCSDKPreprocessAvatarCallback
    {
        public int callbackOrder => int.MinValue + 2;
        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            ParameterFreezeSdkFinalizer.Begin(avatarGameObject);
            AvatarOutputSdkFinalizer.Begin(avatarGameObject);
            ParameterLayout.ParameterLayoutSdkSession.Begin(avatarGameObject);
            return true;
        }
    }

    internal sealed class AvatarParameterSdkBuildPrepare : IVRCSDKPreprocessAvatarCallback
    {
        // After ordinary generator/material validation, before late cost-based
        // compression (int.MaxValue - 100). Uses only the SDK callback contract.
        public int callbackOrder => int.MaxValue - 200;
        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            try { ParameterLayout.ParameterLayoutSdkSession.Prepare(avatarGameObject); return true; }
            catch (Exception exception) { Debug.LogException(exception, avatarGameObject); return false; }
        }
    }

    internal sealed class AvatarOutputSdkBuildFinish : IVRCSDKPreprocessAvatarCallback
    {
        // Runs after late compression (int.MaxValue - 100), before emulator finalization and
        // editor-only component removal (int.MaxValue). No VRCFury assembly/API dependency.
        public int callbackOrder => int.MaxValue - 1;
        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            try
            {
                ParameterFreezeSdkFinalizer.Complete(avatarGameObject);
                AvatarOutputSdkFinalizer.Complete(avatarGameObject);
                ParameterLayout.ParameterLayoutSdkSession.Complete(avatarGameObject);
                return true;
            }
            catch (Exception exception) { Debug.LogException(exception, avatarGameObject); return false; }
        }
    }
}
