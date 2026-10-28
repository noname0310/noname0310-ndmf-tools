using System;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDKBase.Editor;

namespace Noname.AvatarTools.ParameterLayout
{
    internal enum ParameterLayoutConfirmation { None, Recorded, Invalidated }

    // Transaction state is kept in memory. A reload, cancellation or failed upload
    // cannot turn a preview/candidate into a confirmed cross-platform contract.
    internal sealed class ParameterLayoutUploadTransaction
    {
        private string expectedId;
        private ParameterLayoutPlatform platform;
        private bool active, buildSucceeded;
        private ParameterLayoutRecord candidate;

        internal void Begin(string id, ParameterLayoutPlatform target, bool publishing)
        { Cancel(); expectedId = id; platform = target; active = publishing; }
        internal void Stage(ParameterLayoutRecord record)
        {
            if (!active || record.ReferencePlatform != platform) return;
            if (!string.Equals(record.BlueprintId, expectedId, StringComparison.Ordinal))
                throw new InvalidOperationException("The build's parameter layout does not belong to the avatar being uploaded.");
            if (candidate != null) throw new InvalidOperationException("Multiple reference parameter layouts were produced for one upload.");
            ParameterLayoutModel.Validate(record);
            candidate = JsonUtility.FromJson<ParameterLayoutRecord>(JsonUtility.ToJson(record));
        }
        internal void BuildSucceeded() => buildSucceeded = active;
        internal ParameterLayoutConfirmation Confirm(string uploadedId, ParameterLayoutStore store)
        {
            try
            {
                if (!active || !buildSucceeded) return ParameterLayoutConfirmation.None;
                if (!ParameterLayoutStore.ValidId(uploadedId) || !string.IsNullOrEmpty(expectedId) && expectedId != uploadedId)
                    throw new InvalidOperationException("Upload confirmation does not match the parameter layout's Blueprint ID.");
                if (candidate == null)
                    return store.InvalidateReference(uploadedId, platform)
                        ? ParameterLayoutConfirmation.Invalidated : ParameterLayoutConfirmation.None;
                candidate.BlueprintId = uploadedId; candidate.UploadedUtc = DateTime.UtcNow.ToString("O");
                store.Commit(candidate);
                return ParameterLayoutConfirmation.Recorded;
            }
            finally { Cancel(); }
        }
        internal void Cancel() { active = buildSucceeded = false; candidate = null; expectedId = null; }
    }

    [InitializeOnLoad]
    internal static class ParameterLayoutUploadRecorder
    {
        private static IVRCSdkBuilderApi builder;
        private static readonly ParameterLayoutUploadTransaction Transaction = new();
        static ParameterLayoutUploadRecorder()
        {
            VRCSdkControlPanel.OnSdkPanelEnable += (_, _) => EditorApplication.delayCall += Connect;
            EditorApplication.delayCall += Connect;
        }
        private static void Connect()
        {
            if (Resources.FindObjectsOfTypeAll<VRCSdkControlPanel>().Length == 0) return;
            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkBuilderApi>(out var next) || ReferenceEquals(next, builder)) return;
            if (builder != null)
            {
                builder.OnSdkBuildStart -= Begin; builder.OnSdkBuildSuccess -= Built;
                builder.OnSdkBuildError -= Failed; builder.OnSdkUploadError -= Failed;
                builder.OnSdkUploadSuccess -= Uploaded; builder.OnSdkUploadFinish -= Finished;
            }
            Transaction.Cancel(); builder = next;
            builder.OnSdkBuildStart += Begin; builder.OnSdkBuildSuccess += Built;
            builder.OnSdkBuildError += Failed; builder.OnSdkUploadError += Failed;
            builder.OnSdkUploadSuccess += Uploaded; builder.OnSdkUploadFinish += Finished;
        }
        private static void Begin(object sender, object target)
        {
            Transaction.Cancel();
            if (target is not GameObject avatar || VRC_SdkBuilder.ActiveBuildType != VRC_SdkBuilder.BuildType.Publish) return;
            Transaction.Begin(avatar.GetComponent<PipelineManager>()?.blueprintId,
                ParameterLayoutPass.Platform(EditorUserBuildSettings.activeBuildTarget), true);
        }
        internal static void Stage(ParameterLayoutRecord candidate)
        {
            if (VRC_SdkBuilder.ActiveBuildType == VRC_SdkBuilder.BuildType.Publish) Transaction.Stage(candidate);
        }
        private static void Built(object sender, string _) => Transaction.BuildSucceeded();
        private static void Failed(object sender, string _) => Transaction.Cancel();
        private static void Finished(object sender, string _) => Transaction.Cancel();
        private static void Uploaded(object sender, string id)
        {
            try
            {
                var result = Transaction.Confirm(id, ParameterLayoutStore.Project);
                if (result == ParameterLayoutConfirmation.Recorded)
                    Debug.Log("[ALTO] Confirmed reference parameter layout: " + ParameterLayoutStore.Project.PathFor(id));
                else if (result == ParameterLayoutConfirmation.Invalidated)
                    Debug.LogWarning("[ALTO] This reference-platform upload did not include ALTO Parameter Layout. " +
                        "The previous layout record was invalidated; upload with the component enabled before building other platforms.");
            }
            catch (Exception exception)
            {
                Debug.LogError("[ALTO] Upload succeeded, but its parameter layout could not be recorded. " +
                    "Do not build other platforms against the previous record. " + exception);
            }
        }
    }
}
