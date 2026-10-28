using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Editor.BuildPipeline;
using VRC.SDKBase.Editor;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Build
{
    [Serializable]
    public sealed class AvatarBuildErrorTrace
    {
        public string Message, StackTrace;
    }

    [Serializable]
    public sealed class AvatarBuildAnalysisResult
    {
        public string Avatar, SourceId, Platform, StartedUtc, ReportPath, Error;
        public bool Success;
        public long BuildMilliseconds;
        public OptimizationMemoryUsage BeforePreprocessing, AfterPreprocessing, AfterCleanup;
        public List<OptimizationStageMeasurement> Measurements = new();
        public string MeasurementNotes = "Stage timings include their children; do not sum parents and children. " +
            "Only this package's instrumented stages are attributed. SDK time also includes other processors and dialog waits. " +
            "Memory samples cover the entire Editor and managed objects awaiting collection. No forced GC is performed. " +
            "These samples do not measure transient peaks, process working set, or the runtime avatar's memory usage.";
        public int InputSkinnedRenderers, OutputSkinnedRenderers, InputMaterialSlots, OutputMaterialSlots;
        public long InputTriangles, OutputTriangles;
        public int InputPhysBones, OutputPhysBones, OutputPhysBoneColliders;
        public int SyncedParameters, ExpressionParameters;
        public PhysBoneResourceCounts NdmfPhysics;
        public MeshReduction.MeshReductionReport MeshReduction;
        public Expressions.ExpressionOptimizationReport Expressions;
        public List<AnimatorOptimization.MaterialOutputOptimizationReport> MaterialOutputs = new();
        public List<string> Warnings = new();
        public List<string> Errors = new();
        public List<AvatarBuildErrorTrace> ErrorTraces = new();
        public List<string> Messages = new();
        public List<string> PreservedParameters = new();
        public List<string> ParameterMappings = new();
        public List<FrozenParameterReport> FrozenParameters = new();
    }

    // This invokes the SDK's preprocessing callbacks, including NDMF and late parameter compression.
    // It does not build an asset bundle, run upload validation, or upload an avatar.
    public static class AvatarBuildAnalysis
    {
        private static bool running;
        internal static event Action<GameObject, OptimizationBuildReport> ReportReady;

        internal static void PublishReport(BuildContext context)
        {
            var report = context.GetState<OptimizationBuildReport>();
            PublishReport(context.AvatarRootObject, report);
        }

        internal static void PublishReport(GameObject avatar, OptimizationBuildReport report)
        {
            if (report.Input != null) ReportReady?.Invoke(avatar, report);
        }

        [MenuItem("Tools/Noname/ALTO/Build and Analyze Selected Avatar")]
        private static void AnalyzeSelected()
        {
            var descriptor = FindDescriptor(Selection.activeGameObject);
            if (descriptor == null) return;
            var source = descriptor.gameObject;
            EditorBuildReadiness.Schedule(() =>
            {
                if (source != null) Analyze(source);
            });
        }

        [MenuItem("Tools/Noname/ALTO/Build and Analyze Selected Avatar", true)]
        private static bool ValidateSelected() => !running && !EditorApplication.isPlayingOrWillChangePlaymode &&
            VRC_SdkBuilder.ActiveBuildType == VRC_SdkBuilder.BuildType.None &&
            EditorBuildReadiness.Ready && FindDescriptor(Selection.activeGameObject) != null &&
            !EditorUtility.IsPersistent(Selection.activeGameObject);

        private static VRCAvatarDescriptor FindDescriptor(GameObject value)
        {
            for (var current = value != null ? value.transform : null; current != null; current = current.parent)
            {
                var descriptor = current.GetComponent<VRCAvatarDescriptor>();
                if (descriptor != null) return descriptor;
            }
            return null;
        }

        public static AvatarBuildAnalysisResult Analyze(GameObject source, Action<GameObject> configureClone = null,
            Action<GameObject> inspectBuiltAvatar = null)
        {
            EditorBuildReadiness.RequireReady();
            if (running || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                VRC_SdkBuilder.ActiveBuildType != VRC_SdkBuilder.BuildType.None)
                throw new InvalidOperationException("Build analysis requires an idle Editor in Edit Mode.");
            if (source == null || source.GetComponent<VRCAvatarDescriptor>() == null || EditorUtility.IsPersistent(source))
                throw new ArgumentException("Select a scene avatar root with a VRC Avatar Descriptor.", nameof(source));
            running = true;
            var result = new AvatarBuildAnalysisResult
            {
                Avatar = source.name, SourceId = GlobalObjectId.GetGlobalObjectIdSlow(source).ToString(),
                Platform = EditorUserBuildSettings.activeBuildTarget.ToString(), StartedUtc = DateTime.UtcNow.ToString("O")
            };
            var input = OptimizationResourceCounts.Capture(source);
            result.InputSkinnedRenderers = input.SkinnedRenderers; result.InputMaterialSlots = input.MaterialSlots;
            result.InputTriangles = input.Triangles; result.InputPhysBones = input.PhysBones;
            GameObject clone = null;
            AnalysisAssetIsolation isolatedAssets = null;
            Scene analysisScene = default;
            var originalActiveScene = SceneManager.GetActiveScene();
            const string temporaryRoot = "Assets/Noname/BuildAnalysisTemporary";
            bool rootExisted = Directory.Exists(temporaryRoot);
            string temporaryPath = temporaryRoot + "/" + Guid.NewGuid().ToString("N");
            var watch = new Stopwatch();
            void Log(string message, string stack, LogType type)
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                {
                    if (result.Errors.Count < 100) result.Errors.Add(message);
                    if (result.ErrorTraces.Count < 100) result.ErrorTraces.Add(new AvatarBuildErrorTrace { Message = message, StackTrace = stack });
                }
                else if (type == LogType.Warning && result.Warnings.Count < 100) result.Warnings.Add(message);
            }
            void Report(GameObject avatar, OptimizationBuildReport report)
            {
                if (avatar != clone) return;
                result.Messages = new List<string>(report.Messages);
                result.PreservedParameters = new List<string>(report.PreservedParameters);
                result.ParameterMappings = new List<string>(report.ParameterMappings);
                result.FrozenParameters = new List<FrozenParameterReport>(report.FrozenParameters);
                result.NdmfPhysics = report.Physics;
                result.MeshReduction = report.MeshReduction;
                result.Expressions = report.Expressions;
                result.MaterialOutputs = new List<AnimatorOptimization.MaterialOutputOptimizationReport>(report.MaterialOutputs);
                // A scope can publish while still active, and SDK finalizers append
                // measurements later. Keep the build-local list until serialization.
                result.Measurements = report.Measurements;
            }
            Application.logMessageReceived += Log;
            ReportReady += Report;
            try
            {
                // Opening a saved empty scene also works when the user has an unsaved untitled scene.
                string scenePath = AssetDatabase.GUIDToAssetPath("4e3531870ac74a99b7804047f9db1d96");
                if (string.IsNullOrEmpty(scenePath)) throw new InvalidOperationException("The build analysis scene asset is missing.");
                if (SceneManager.GetSceneByPath(scenePath).isLoaded)
                    throw new InvalidOperationException("Close the package's analysis scene before starting build analysis.");
                analysisScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                analysisScene.isSubScene = true;
                SceneManager.SetActiveScene(analysisScene);
                // Keep ExecuteAlways.OnEnable from modifying shared temporary assets before isolation.
                // The active scratch scene also owns any unparented helpers created by build callbacks.
                var staging = new GameObject("Inactive analysis staging");
                staging.SetActive(false);
                clone = Object.Instantiate(source, staging.transform, true);
                clone.name = source.name + " (Build Analysis)";
                isolatedAssets = AnalysisAssetIsolation.Capture(clone);
                clone.transform.SetParent(null, true);
                Object.DestroyImmediate(staging);
                configureClone?.Invoke(clone);
                EditorUtility.DisplayProgressBar("ALTO", "Preprocessing " + source.name, 0.1f);
                result.BeforePreprocessing = OptimizationMemoryUsage.Capture();
                watch.Start();
                using (AnalysisParameterRecordScope.Capture(clone))
                using (new OverrideTemporaryDirectoryScope(temporaryPath))
                    result.Success = VRCBuildPipelineCallbacks.OnPreprocessAvatar(clone);
                watch.Stop();
                result.AfterPreprocessing = OptimizationMemoryUsage.Capture();
                if (result.Success)
                {
                    var output = OptimizationResourceCounts.Capture(clone);
                    result.OutputSkinnedRenderers = output.SkinnedRenderers; result.OutputMaterialSlots = output.MaterialSlots;
                    result.OutputTriangles = output.Triangles; result.OutputPhysBones = output.PhysBones;
                    result.OutputPhysBoneColliders = output.PhysBoneColliders;
                    var expressions = clone.GetComponent<VRCAvatarDescriptor>()?.expressionParameters?.parameters;
                    if (expressions != null)
                    {
                        result.ExpressionParameters = expressions.Count(p => p != null && !string.IsNullOrEmpty(p.name));
                        result.SyncedParameters = expressions.Count(p => p != null && !string.IsNullOrEmpty(p.name) && p.networkSynced);
                    }
                    inspectBuiltAvatar?.Invoke(clone);
                }
                else result.Error = "An avatar preprocess callback failed. See Errors and the Unity Console.";
            }
            catch (Exception exception)
            {
                result.Success = false; result.Error = exception.ToString();
                Debug.LogException(exception, source);
            }
            finally
            {
                watch.Stop(); result.BuildMilliseconds = watch.ElapsedMilliseconds;
                if (result.BeforePreprocessing != null && result.AfterPreprocessing == null)
                    result.AfterPreprocessing = OptimizationMemoryUsage.Capture();
                ReportReady -= Report;
                Application.logMessageReceived -= Log;
                if (clone != null) Object.DestroyImmediate(clone);
                if (originalActiveScene.IsValid() && originalActiveScene.isLoaded)
                    SceneManager.SetActiveScene(originalActiveScene);
                if (analysisScene.IsValid()) EditorSceneManager.CloseScene(analysisScene, true);
                isolatedAssets?.Dispose();
                // Only remove this run's unique asset folder, after releasing the preview avatar.
                if (AssetDatabase.IsValidFolder(temporaryPath) && !AssetDatabase.DeleteAsset(temporaryPath))
                    result.Warnings.Add("Could not remove temporary build assets: " + temporaryPath);
                if (!rootExisted && Directory.Exists(temporaryRoot) && Directory.GetFileSystemEntries(temporaryRoot).Length == 0)
                    AssetDatabase.DeleteAsset(temporaryRoot);
                EditorUtility.ClearProgressBar();
                running = false;
                result.AfterCleanup = OptimizationMemoryUsage.Capture();
            }
            string directory = Path.Combine("Library", "ALTO", "BuildReports");
            Directory.CreateDirectory(directory);
            result.ReportPath = Path.GetFullPath(Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json"));
            File.WriteAllText(result.ReportPath, JsonUtility.ToJson(result, true));
            Debug.Log("[ALTO] Build analysis " + (result.Success ? "completed" : "failed") +
                " in " + result.BuildMilliseconds + " ms. Report: " + result.ReportPath, source);
            return result;
        }
    }
}
