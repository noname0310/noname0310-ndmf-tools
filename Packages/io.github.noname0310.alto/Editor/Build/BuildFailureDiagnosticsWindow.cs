using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    internal sealed class BuildFailureDiagnosticsWindow : EditorWindow
    {
        [SerializeField] private ALTOBuildExclusions filter;
        private AvatarBuildAnalysisResult baseline;
        private IReadOnlyList<BuildFailureCandidate> candidates = Array.Empty<BuildFailureCandidate>();
        private readonly Dictionary<Component, AvatarBuildAnalysisResult> trials = new();
        private Vector2 scroll;
        private bool busy;
        private string error;

        [MenuItem("Tools/Noname/ALTO/Build Failure Diagnostics")]
        private static void OpenSelected()
        {
            var selected = Selection.activeGameObject;
            var descriptor = selected != null ? selected.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true) : null;
            Open(descriptor != null ? descriptor.GetComponentInChildren<ALTOBuildExclusions>(true) : null);
        }

        internal static void Open(ALTOBuildExclusions value)
        {
            var window = GetWindow<BuildFailureDiagnosticsWindow>("ALTO Build Diagnostics");
            window.filter = value; window.Clear(); window.Show();
        }

        private void Clear() { baseline = null; candidates = Array.Empty<BuildFailureCandidate>(); trials.Clear(); error = null; }

        private void Run(Action action)
        {
            busy = true; error = null;
            EditorBuildReadiness.Schedule(() =>
            {
                try { action(); }
                catch (Exception exception) { error = exception.Message; Debug.LogException(exception); }
                finally { busy = false; Repaint(); }
            });
        }

        private void OnGUI()
        {
            using (new EditorGUI.DisabledScope(busy))
            {
                EditorGUI.BeginChangeCheck();
                filter = (ALTOBuildExclusions)EditorGUILayout.ObjectField("Build Exclusions", filter, typeof(ALTOBuildExclusions), true);
                if (EditorGUI.EndChangeCheck()) Clear();
            }
            EditorGUILayout.HelpBox("Analyze a fresh build copy on the current platform. Error stacks identify participating build-only components, not proven causes. " +
                "Each test adds one candidate to the copied filter and reruns preprocessing from a fresh avatar. Successful preprocessing does not prove equivalent appearance or behavior, or successful upload validation. " +
                "Only Add To Filter changes the source scene; the change can be undone.", MessageType.Info);
            var descriptor = filter != null ? filter.FindDescriptor() : null;
            using (new EditorGUI.DisabledScope(busy || descriptor == null || EditorApplication.isPlayingOrWillChangePlaymode || !EditorBuildReadiness.Ready))
                if (GUILayout.Button("Analyze Build And Find Candidates"))
                    Run(() =>
                    {
                        Clear(); baseline = AvatarBuildAnalysis.Analyze(filter.FindDescriptor().gameObject);
                        if (!baseline.Success) candidates = BuildFailureCandidates.Find(filter.FindDescriptor().gameObject, baseline.ErrorTraces);
                    });
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (baseline == null) return;
            EditorGUILayout.LabelField(baseline.Platform + ": " + (baseline.Success ? "Preprocessing succeeded" : "Preprocessing failed"));
            if (GUILayout.Button("Open Analysis Report")) EditorUtility.RevealInFinder(baseline.ReportPath);
            if (!baseline.Success && candidates.Count == 0)
                EditorGUILayout.HelpBox("No build-only component types were identified in the captured stack signatures. No automatic exclusion is proposed.", MessageType.Info);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var candidate in candidates)
            {
                if (candidate.Component == null) continue;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.ObjectField(candidate.Component, typeof(Component), true);
                    EditorGUILayout.LabelField(candidate.Component.GetType().FullName, EditorStyles.wordWrappedLabel);
                    EditorGUILayout.LabelField(candidate.Evidence, EditorStyles.wordWrappedMiniLabel);
                    if (candidate.MatchingInstances > 1)
                        EditorGUILayout.HelpBox(candidate.MatchingInstances + " instances match this type. The stack does not identify which instance was involved.", MessageType.Warning);
                    trials.TryGetValue(candidate.Component, out var trial);
                    if (trial != null)
                    {
                        EditorGUILayout.LabelField("Trial: " + (trial.Success ? "preprocessing succeeded" : "preprocessing failed") + " (" + trial.BuildMilliseconds + " ms)");
                        if (GUILayout.Button("Open Trial Report")) EditorUtility.RevealInFinder(trial.ReportPath);
                    }
                    string unavailable = null;
                    try { BuildFailureCandidates.Validate(filter, candidate.Component); }
                    catch (Exception exception) { unavailable = exception.Message; }
                    if (unavailable != null) EditorGUILayout.HelpBox(unavailable, MessageType.None);
                    using (new EditorGUI.DisabledScope(busy || unavailable != null || !EditorBuildReadiness.Ready ||
                        baseline.Platform != EditorUserBuildSettings.activeBuildTarget.ToString()))
                    {
                        if (GUILayout.Button("Test Exclusion On Fresh Copy"))
                            Run(() => trials[candidate.Component] = BuildFailureCandidates.Test(filter, candidate.Component));
                        using (new EditorGUI.DisabledScope(trial == null || !trial.Success))
                            if (GUILayout.Button("Add To Filter"))
                            {
                                BuildFailureCandidates.AddSelected(filter, candidate.Component);
                                trials.Clear();
                            }
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
