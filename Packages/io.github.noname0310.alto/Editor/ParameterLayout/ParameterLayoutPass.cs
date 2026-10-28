using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.ParameterLayout
{
    internal sealed class ParameterLayoutSettings
    {
        internal ParameterLayoutPlatform ReferencePlatform;
        internal AdditionalSyncedParameterPolicy Additional;
        internal ParameterCompressionSettings Compression;
    }

    internal sealed class ParameterLayoutState
    {
        internal ParameterLayoutSettings Settings;
    }

    internal static class ParameterLayoutPass
    {
        internal static ParameterLayoutPlatform Platform(BuildTarget target) => target switch
        {
            BuildTarget.StandaloneWindows64 or BuildTarget.StandaloneWindows or BuildTarget.StandaloneLinux64 => ParameterLayoutPlatform.PC,
            BuildTarget.Android => ParameterLayoutPlatform.Android,
            BuildTarget.iOS => ParameterLayoutPlatform.IOS,
            _ => throw new InvalidOperationException("ALTO Parameter Layout does not support this build target: " + target)
        };

        internal static void Capture(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOParameterLayout>(true);
            var active = all.Where(c => c.enabled && !ALTOPlugin.IsEditorOnly(c.transform) &&
                BuildPlatformFilters.Includes(c.transform, context)).ToArray();
            if (active.Length > 1) throw new InvalidOperationException("Use one enabled ALTO Parameter Layout per avatar.");
            var compression = ParameterCompressionPass.Capture(context, active.Length == 1);
            if (active.Length == 1)
            {
                var component = active[0];
                if (component.FindDescriptor()?.gameObject != context.AvatarRootObject)
                    throw new InvalidOperationException("ALTO Parameter Layout requires a VRC Avatar Descriptor ancestor.");
                if (!Enum.IsDefined(typeof(ParameterLayoutPlatform), component.ReferencePlatform) ||
                    !Enum.IsDefined(typeof(AdditionalSyncedParameterPolicy), component.AdditionalParameters))
                    throw new InvalidOperationException("Choose valid parameter layout policies.");
                context.GetState<ParameterLayoutState>().Settings = new ParameterLayoutSettings
                { ReferencePlatform = component.ReferencePlatform, Additional = component.AdditionalParameters, Compression = compression };
                context.GetState<OptimizationBuildReport>().Initialize(context.AvatarRootObject,
                    context.GetState<OptimizationBuildEnvironment>().Target.ToString());
            }
            foreach (var component in all) Object.DestroyImmediate(component);
        }

        internal static void Apply(BuildContext context)
        {
            var settings = context.GetState<ParameterLayoutState>().Settings;
            if (settings == null) return;
            var platform = Platform(context.GetState<OptimizationBuildEnvironment>().Target);
            var report = context.GetState<OptimizationBuildReport>();
            try
            {
                if (ParameterLayoutSdkSession.Defer(context, settings, platform, report)) return;
                ApplyFinal(context.AvatarRootObject, settings, platform, report, context.AssetSaver.SaveAsset,
                    ParameterLayoutStore.Project, context);
            }
            finally
            {
                // Failed budget/contract validation must retain its selection diagnostics.
                AvatarBuildAnalysis.PublishReport(context.AvatarRootObject, report);
            }
        }

        internal static ParameterLayoutRecord ApplyFinal(GameObject avatar, ParameterLayoutSettings settings,
            ParameterLayoutPlatform platform, OptimizationBuildReport report, Action<Object> save, ParameterLayoutStore store,
            BuildContext context = null, Action<ParameterLayoutResult> onAligned = null)
        {
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) throw new InvalidOperationException("The avatar descriptor was removed before layout finalization.");
            var source = descriptor.expressionParameters;
            var id = avatar.GetComponent<PipelineManager>()?.blueprintId;
            if (platform == settings.ReferencePlatform)
            {
                var encoding = ParameterCompressionPass.Apply(context, settings.Compression, null, report, save);
                source = descriptor.expressionParameters;
                var candidate = new ParameterLayoutRecord
                {
                    BlueprintId = id, ReferencePlatform = platform,
                    Parameters = ParameterLayoutModel.Capture(source?.parameters), IntegerEncodings = encoding.Integers,
                    Multiplexing = encoding.Multiplexing
                };
                ParameterLayoutModel.Validate(candidate);
                report.Messages.Add("Reference parameter layout evaluated: " + candidate.Parameters.Count +
                    " synchronized declarations. Only a successful reference-platform SDK upload commits the record.");
                return candidate;
            }
            if (!ParameterLayoutStore.ValidId(id))
                throw new InvalidOperationException("Upload the reference platform first to assign this avatar's Blueprint ID and parameter layout.");
            var record = store.Read(id);
            if (record == null) throw new InvalidOperationException("No confirmed parameter layout for " + id +
                ". Upload the reference platform with ALTO Parameter Layout enabled first. Records are stored in ProjectSettings/ALTO/ParameterLayouts.");
            if (record.ReferencePlatform != settings.ReferencePlatform)
                throw new InvalidOperationException("The recorded reference platform differs from this component. Upload the selected reference platform first.");
            ParameterLayoutModel.Validate(record);
            ParameterCompressionPass.Apply(context, settings.Compression, record, report, save);
            source = descriptor.expressionParameters;
            var reserved = new HashSet<string>(AvatarParameterInputs.Sensors(avatar).Select(s => s.Name), StringComparer.Ordinal);
            foreach (var (_, runtime, _) in VRChatPlatformAnimatorBindings.Instance.GetInnateControllers(avatar))
            {
                var controller = runtime;
                var visited = new HashSet<RuntimeAnimatorController>();
                while (controller is AnimatorOverrideController wrapper && visited.Add(controller)) controller = wrapper.runtimeAnimatorController;
                if (controller is AnimatorController native) reserved.UnionWith(native.parameters.Select(p => p.name));
                if (runtime != null)
                    foreach (var clip in runtime.animationClips)
                        reserved.UnionWith(AnimationUtility.GetCurveBindings(clip)
                            .Where(b => b.type == typeof(Animator)).Select(b => b.propertyName));
            }
            foreach (var menu in Expressions.ExpressionMenuOptimizer.Menus(descriptor.expressionsMenu))
                reserved.UnionWith((menu.controls ?? new()).Where(c => c != null).SelectMany(Expressions.ExpressionMenuOptimizer.Parameters));
            var aligned = ParameterLayoutModel.Align(record, source?.parameters, settings.Additional, reserved);
            var copy = source != null ? Object.Instantiate(source) : ScriptableObject.CreateInstance<VRCExpressionParameters>();
            copy.name = "Aligned Expression Parameters"; copy.parameters = aligned.Parameters;
            save(copy);
            if (source != null) ObjectRegistry.RegisterReplacedObject(source, copy);
            descriptor.expressionParameters = copy;
            onAligned?.Invoke(aligned);
            report.Messages.Add("Aligned synchronized parameter layout: " + record.Parameters.Count + " slots, " + aligned.Padding +
                " unused padding declarations, " + aligned.MadeLocal.Count + " additional parameters kept local.");
            foreach (string name in aligned.MadeLocal) report.Messages.Add("Parameter layout kept additional input local: " + name);
            return null;
        }
    }

    internal static class ParameterLayoutSdkSession
    {
        private sealed class Session
        {
            internal ParameterLayoutSettings Settings;
            internal ParameterLayoutPlatform Platform;
            internal List<ParameterLayoutEntry> Before;
            internal OptimizationBuildReport Report;
            internal Object Container;
            internal IObjectRegistry Registry;
            internal BuildContext Context;
            internal bool Prepared;
            internal ParameterLayoutRecord Candidate;
            internal readonly HashSet<string> Padding = new(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> PaddingReferences = new(StringComparer.Ordinal);
            internal VRCExpressionParameters.Parameter[] LocalBefore;
        }
        private static readonly ConditionalWeakTable<GameObject, Session> Sessions = new();
        internal static void Begin(GameObject avatar)
        { Sessions.Remove(avatar); Sessions.Add(avatar, new Session()); }

        internal static bool Defer(BuildContext context, ParameterLayoutSettings settings,
            ParameterLayoutPlatform platform, OptimizationBuildReport report, ParameterLayoutStore store = null)
        {
            if (!Sessions.TryGetValue(context.AvatarRootObject, out var session)) return false;
            session.Settings = settings; session.Platform = platform; session.Report = report;
            // Prepare an over-budget transport before late SDK optimizers inspect its cost.
            // Finalization still rejects external protocol changes. No external tool API or
            // callback patch is used, and only successful uploads commit the candidate.
            if (platform != settings.ReferencePlatform ||
                settings.Compression != null && settings.Compression.Multiplexing != ParameterMultiplexingMode.Disabled &&
                MultiplexEncoding.Cost(ParameterLayoutModel.Read(context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>()
                    .expressionParameters?.parameters)) > settings.Compression.Budget)
            {
                session.Candidate = ParameterLayoutPass.ApplyFinal(context.AvatarRootObject, settings, platform, report,
                    context.AssetSaver.SaveAsset, store ?? ParameterLayoutStore.Project, context,
                    aligned => {
                        session.Padding.UnionWith(aligned.PaddingNames);
                        foreach (var entry in aligned.PaddingReferenceNames) session.PaddingReferences.Add(entry.Key, entry.Value);
                    });
                session.Prepared = true;
            }
            session.Before = ParameterLayoutModel.Capture(context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>().expressionParameters?.parameters);
            session.LocalBefore = ParameterLayoutModel.Read(context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>().expressionParameters?.parameters)
                .Where(p => !p.networkSynced).Select(ParameterLayoutModel.Copy).ToArray();
            session.Container = context.AssetSaver.CurrentContainer; session.Registry = ObjectRegistry.ActiveRegistry;
            session.Context = context;
            return true;
        }

        internal static void Complete(GameObject avatar, Action<Object> saveForTests = null, ParameterLayoutStore storeForTests = null)
        {
            if (!Sessions.TryGetValue(avatar, out var session)) return;
            Sessions.Remove(avatar);
            if (session.Settings == null) return;
            using var measurement = OptimizationMeasurement.Begin(session.Report, "SDK finalizer", "parameter layout");
            Apply(avatar, session, saveForTests, storeForTests);
            AvatarBuildAnalysis.PublishReport(avatar, session.Report);
            measurement?.Complete();
        }

        private static void Apply(GameObject avatar, Session session, Action<Object> saveForTests, ParameterLayoutStore storeForTests)
        {
            var parameters = avatar.GetComponent<VRCAvatarDescriptor>()?.expressionParameters?.parameters;
            var padding = new Lazy<ParameterPaddingUsage>(() => new ParameterPaddingUsage(avatar));
            var normalized = ParameterLayoutModel.ReconcileLatePadding(session.Before, parameters,
                session.Padding, name => padding.Value.IsUnobserved(name), session.PaddingReferences, session.LocalBefore);
            if (saveForTests == null && (session.Container == null || !EditorUtility.IsPersistent(session.Container)))
                throw new InvalidOperationException("Parameter layout finalization requires the generated NDMF asset container.");
            using var registry = new ObjectRegistryScope(session.Registry);
            using var saver = saveForTests == null ? new SingleAssetSaver(session.Container) : null;
            var candidate = session.Candidate;
            if (!session.Prepared)
                candidate = ParameterLayoutPass.ApplyFinal(avatar, session.Settings, session.Platform, session.Report,
                    saveForTests ?? saver.SaveAsset, storeForTests ?? ParameterLayoutStore.Project, session.Context);
            else if (candidate != null)
            {
                candidate.Parameters = ParameterLayoutModel.Capture(avatar.GetComponent<VRCAvatarDescriptor>().expressionParameters?.parameters);
                ParameterLayoutModel.Validate(candidate);
            }
            else
            {
                // Restore reference slot order even if a late processor reordered unchanged
                // declarations. The prepared ordinary wire names also include anonymous padding.
                var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
                var current = descriptor.expressionParameters;
                var aligned = ParameterLayoutModel.Align(new ParameterLayoutRecord { Parameters = session.Before },
                    normalized, AdditionalSyncedParameterPolicy.Error);
                var copy = current != null ? Object.Instantiate(current) : ScriptableObject.CreateInstance<VRCExpressionParameters>();
                copy.name = "Final Aligned Expression Parameters"; copy.parameters = aligned.Parameters;
                (saveForTests ?? saver.SaveAsset)(copy);
                if (current != null) ObjectRegistry.RegisterReplacedObject(current, copy);
                descriptor.expressionParameters = copy;
            }
            if (candidate != null && saveForTests == null) ParameterLayoutUploadRecorder.Stage(candidate);
        }
    }
}
