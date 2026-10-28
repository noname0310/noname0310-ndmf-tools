using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.MeshReduction
{
    [Serializable]
    public sealed class MeshReductionEntryReport
    {
        public string Renderer, Reason;
        public string[] Sources;
        public long Before, After;
        public int Target, Minimum;
    }

    [Serializable]
    public sealed class MeshReductionReport
    {
        public int Budget, Iterations;
        public long InputTriangles, OutputTriangles, FixedTriangles, Milliseconds;
        public bool BudgetReached;
        public List<MeshReductionEntryReport> Renderers = new();
    }

    internal sealed class ReductionRequest
    {
        internal Renderer Renderer;
        internal Mesh Source, Result;
        internal int Count, Target, Minimum, Floor;
        internal double Weight;
        internal MeshReductionEntryReport Report;
    }

    internal sealed class ReductionInput
    {
        internal bool InScope, Selected, Preserved;
        internal string Path;
        internal double Weight;
        internal int Minimum;
    }

    internal sealed class MeshReductionState
    {
        internal ALTOMeshReduction Settings;
        internal OptimizationBuildScope Scope;
        internal readonly HashSet<ObjectReference> Targets = new(), Preserved = new();
        internal readonly Dictionary<ObjectReference, MeshReductionPriority> Priorities = new();
        internal readonly List<ReductionInput> Inputs = new();
    }

    internal static class MeshReductionBackend
    {
        internal static Action<IReadOnlyList<ReductionRequest>, bool> Simplify;
        internal static Func<Renderer, Mesh, string> Validate;
        internal static readonly List<Func<GameObject, IEnumerable<Renderer>>> OtherReductionTargets = new();
    }

    internal static class MeshReductionPass
    {
        internal static void Capture(BuildContext context)
        {
            var all = context.AvatarRootObject.GetComponentsInChildren<ALTOMeshReduction>(true);
            var enabled = all.Where(c => OptimizationBuildScope.Applies(c, context)).ToArray();
            if (enabled.Length > 1) throw new InvalidOperationException("Use one enabled ALTO Mesh Reduction per avatar and platform.");
            foreach (var inactive in all.Except(enabled)) Object.DestroyImmediate(inactive);
            if (enabled.Length == 0) return;
            if (MeshReductionBackend.Simplify == null)
                throw new InvalidOperationException("ALTO Mesh Reduction requires Meshia Mesh Simplification 3.2 or a compatible 3.x version.");
            var state = context.GetState<MeshReductionState>();
            state.Settings = enabled[0]; state.Scope = OptimizationBuildScope.Capture(state.Settings, context.AvatarRootObject);
            ValidateSettings(state.Settings);
            void Add(Renderer renderer, HashSet<ObjectReference> set)
            {
                if (!state.Scope.Contains(renderer.transform))
                    throw new InvalidOperationException("Mesh reduction selections must be inside Scope Root: " + renderer.name);
                set.Add(ObjectRegistry.GetReference(renderer));
            }
            if (state.Settings.TargetSelection == RendererSelectionMode.SelectedRenderers)
                foreach (var renderer in state.Settings.ReductionTargets) Add(renderer, state.Targets);
            foreach (var renderer in state.Settings.PreserveRenderers) Add(renderer, state.Preserved);
            foreach (var priority in state.Settings.ReductionPriorities)
            {
                if (!state.Scope.Contains(priority.Renderer.transform))
                    throw new InvalidOperationException("Mesh reduction priorities must be inside Scope Root.");
                state.Priorities.Add(ObjectRegistry.GetReference(priority.Renderer), priority);
            }
            context.GetState<OptimizationBuildReport>().Initialize(context.AvatarRootObject,
                context.GetState<OptimizationBuildEnvironment>().Target.ToString());
        }

        internal static void ValidateSettings(ALTOMeshReduction settings)
        {
            if (settings.TriangleBudget < 0) throw new InvalidOperationException("Triangle Budget cannot be negative.");
            if (!Enum.IsDefined(typeof(RendererSelectionMode), settings.TargetSelection))
                throw new InvalidOperationException("Choose a valid reduction Target Selection mode.");
            if (settings.ReductionTargets == null || settings.PreserveRenderers == null || settings.ReductionPriorities == null ||
                settings.PreserveRenderers.Any(r => r == null || !IsMeshRenderer(r)) ||
                settings.TargetSelection == RendererSelectionMode.SelectedRenderers && settings.ReductionTargets.Any(r => r == null || !IsMeshRenderer(r)))
                throw new InvalidOperationException("ALTO Mesh Reduction selections must reference existing MeshRenderers or SkinnedMeshRenderers.");
            var seen = new HashSet<Renderer>();
            foreach (var priority in settings.ReductionPriorities)
                if (priority == null || priority.Renderer == null || !IsMeshRenderer(priority.Renderer) || !seen.Add(priority.Renderer) ||
                    priority.MinimumTriangles < 0 || float.IsNaN(priority.Weight) || float.IsInfinity(priority.Weight) || priority.Weight <= 0)
                    throw new InvalidOperationException("Reduction priorities require unique renderers, positive finite weights and nonnegative minima.");
        }

        // Capture every renderer so AAO merges that cross selection boundaries can be recognized.
        // The optional mapping adapter follows replacements without keeping otherwise dead renderers alive.
        internal static void Prepare(BuildContext context)
        {
            var state = context.GetState<MeshReductionState>();
            if (state.Settings == null) return;
            var renderers = context.AvatarRootObject.GetComponentsInChildren<Renderer>(true).Where(IsMeshRenderer).ToArray();
            var other = new HashSet<Renderer>(MeshReductionBackend.OtherReductionTargets.SelectMany(f => f(context.AvatarRootObject)));
            state.Settings.BuildInputRenderers = renderers;
            foreach (var renderer in renderers)
            {
                var input = Selection(state, renderer);
                if (input.InScope && input.Selected && !input.Preserved && other.Contains(renderer))
                    throw new InvalidOperationException("ALTO Mesh Reduction overlaps an enabled Meshia reducer on " + renderer.name + ". Use one reduction policy for this renderer.");
                state.Inputs.Add(input);
                input.Path = AnimationUtility.CalculateTransformPath(renderer.transform, context.AvatarRootTransform);
            }
        }

        private static ReductionInput Selection(MeshReductionState state, Renderer renderer)
        {
            var reference = ObjectRegistry.GetReference(renderer);
            state.Priorities.TryGetValue(reference, out var priority);
            return new ReductionInput
            {
                InScope = state.Scope.Contains(renderer.transform), Selected = state.Settings.TargetSelection == RendererSelectionMode.AllRenderersInScope || state.Targets.Contains(reference),
                Preserved = state.Preserved.Contains(reference), Weight = priority?.Weight ?? 1, Minimum = priority?.MinimumTriangles ?? 0
            };
        }

        internal static bool IsMeshRenderer(Renderer renderer) => renderer is SkinnedMeshRenderer || renderer is MeshRenderer;
        internal static Mesh MeshOf(Renderer renderer)
        {
            if (renderer == null) return null;
            if (renderer is SkinnedMeshRenderer skin) return skin.sharedMesh;
            var filter = renderer is MeshRenderer ? renderer.GetComponent<MeshFilter>() : null;
            return filter != null ? filter.sharedMesh : null;
        }
        internal static long Triangles(Mesh mesh) => mesh == null ? 0 : Enumerable.Range(0, mesh.subMeshCount)
            .Where(s => mesh.GetTopology(s) == MeshTopology.Triangles).Sum(s => (long)mesh.GetIndexCount(s) / 3);

        internal static void Apply(BuildContext context)
        {
            var state = context.GetState<MeshReductionState>();
            if (state.Settings == null) return;
            var settings = state.Settings;
            var watch = Stopwatch.StartNew();
            var buildReport = context.GetState<OptimizationBuildReport>();
            var report = new MeshReductionReport { Budget = settings.TriangleBudget };
            buildReport.MeshReduction = report;
            var groups = new Dictionary<Renderer, List<ReductionInput>>();
            for (int i = 0; i < state.Inputs.Count; i++)
            {
                var renderer = settings.BuildInputRenderers[i];
                if (renderer == null) continue;
                if (!groups.TryGetValue(renderer, out var entries)) groups.Add(renderer, entries = new());
                entries.Add(state.Inputs[i]);
            }
            var animationLease = Noname.AvatarTools.Build.OptimizationSession.Acquire(context);
            var services = animationLease.Services;
            Dictionary<Renderer, string> hazards;
            try { hazards = MeshReductionSafety.Find(context.AvatarRootObject, services.ControllerContext, services.ObjectPathRemapper.GetVirtualToRealPathMap()); }
            finally
            {
                animationLease.Dispose();
            }
            var requests = new List<ReductionRequest>();
            foreach (var renderer in context.AvatarRootObject.GetComponentsInChildren<Renderer>(true).Where(IsMeshRenderer))
            {
                if (!groups.TryGetValue(renderer, out var entries)) entries = new() { Selection(state, renderer) };
                if (!entries.Any(e => e.InScope)) continue;
                var mesh = MeshOf(renderer); long triangles = Triangles(mesh);
                string reason = entries.Any(e => !e.InScope) ? "merged with an out-of-scope input; counting the whole renderer conservatively" :
                    entries.Any(e => e.Preserved) ? "preserved renderer or merged preserved input" :
                    entries.Any(e => !e.Selected) ? "unselected renderer or merged unselected input" :
                    hazards.TryGetValue(renderer, out var hazard) ? hazard :
                    mesh == null || triangles == 0 ? "no triangles" :
                    triangles > int.MaxValue ? "triangle count exceeds the reducer's range" :
                    MeshReductionBackend.Validate?.Invoke(renderer, mesh);
                var entry = new MeshReductionEntryReport
                {
                    Renderer = AnimationUtility.CalculateTransformPath(renderer.transform, context.AvatarRootTransform),
                    Sources = entries.Select(e => e.Path).Where(p => p != null).Distinct().ToArray(),
                    Before = triangles, After = triangles, Reason = reason
                };
                report.Renderers.Add(entry); report.InputTriangles += triangles;
                if (reason != null) { report.FixedTriangles += triangles; continue; }
                int minimum = (int)Math.Min(triangles, Math.Max(1L, entries.Sum(e => (long)e.Minimum)));
                entry.Minimum = minimum; entry.Target = (int)triangles;
                requests.Add(new ReductionRequest
                {
                    Renderer = renderer, Source = mesh, Count = (int)triangles, Minimum = minimum, Floor = minimum,
                    Weight = entries.Max(e => e.Weight), Report = entry
                });
            }
            try
            {
                Reduce(requests, report, settings.PreserveBorderEdges);
                foreach (var request in requests)
                {
                    if (request.Result == null) continue;
                    ObjectRegistry.RegisterReplacedObject(request.Source, request.Result);
                    context.AssetSaver.SaveAsset(request.Result);
                    if (request.Renderer is SkinnedMeshRenderer skin)
                    {
                        var weights = Enumerable.Range(0, request.Source.blendShapeCount).Select(skin.GetBlendShapeWeight).ToArray();
                        skin.sharedMesh = request.Result;
                        for (int i = 0; i < weights.Length; i++) skin.SetBlendShapeWeight(i, weights[i]);
                    }
                    else request.Renderer.GetComponent<MeshFilter>().sharedMesh = request.Result;
                    request.Result = null; // Saved mesh ownership has transferred to the build.
                }
                report.BudgetReached = report.OutputTriangles <= report.Budget;
                report.Milliseconds = watch.ElapsedMilliseconds;
                buildReport.Messages.Add($"Mesh reduction: {report.InputTriangles} -> {report.OutputTriangles} triangles, budget {report.Budget}; {report.FixedTriangles} fixed; {report.Iterations} allocation rounds; {report.Milliseconds} ms.");
                foreach (var entry in report.Renderers.Where(e => e.Reason != null && e.Before > 0))
                    buildReport.Messages.Add("Reduction retained " + entry.Renderer + ": " + entry.Reason);
                if (!report.BudgetReached)
                {
                    string message = $"Mesh reduction did not reach its budget: {report.OutputTriangles} / {report.Budget} triangles. " +
                        "Preserved inputs, minimum counts and topology constraints limit reduction; inspect the per-renderer build report.";
                    buildReport.Messages.Add(message);
                    if (settings.OnBudgetUnreachable == BudgetExceededAction.Error) throw new InvalidOperationException(message);
                    UnityEngine.Debug.LogWarning("[ALTO] " + message, context.AvatarRootObject);
                }
            }
            finally
            {
                foreach (var request in requests) if (request.Result != null) Object.DestroyImmediate(request.Result);
                Object.DestroyImmediate(settings);
            }
        }

        internal static void Reduce(List<ReductionRequest> requests, MeshReductionReport report, bool preserveBorders)
        {
            report.OutputTriangles = report.InputTriangles;
            if (report.OutputTriangles <= report.Budget) return;
            // Redistribute budget when topology prevents a renderer from reaching its allocation.
            // Always simplify the original input, avoiding cumulative quality loss between rounds.
            for (int round = 0; round < 3 && report.OutputTriangles > report.Budget; round++)
            {
                var targets = TriangleBudgetAllocator.Allocate(requests.Select(r => new TriangleAllocationInput(r.Count, r.Floor, r.Weight)).ToArray(),
                    (long)report.Budget - report.FixedTriangles);
                var work = new List<ReductionRequest>();
                for (int i = 0; i < requests.Count; i++)
                {
                    var request = requests[i];
                    if (targets[i] >= request.Count || round > 0 && targets[i] >= request.Report.After) continue;
                    if (request.Result != null) Object.DestroyImmediate(request.Result);
                    request.Result = null; request.Target = targets[i]; request.Report.Target = targets[i]; work.Add(request);
                }
                if (work.Count == 0) break;
                report.Iterations++;
                MeshReductionBackend.Simplify(work, preserveBorders);
                foreach (var request in work)
                {
                    ValidateResult(request);
                    long actual = Triangles(request.Result);
                    if (actual < request.Minimum)
                    {
                        Object.DestroyImmediate(request.Result); request.Result = null;
                        actual = request.Count; request.Report.Reason = "reducer would cross the minimum triangle count";
                    }
                    request.Report.After = actual;
                    if (actual > request.Target)
                    {
                        request.Floor = (int)actual;
                        request.Report.Reason ??= "Meshia stopped above its allocation under the current topology constraints";
                    }
                }
                report.OutputTriangles = report.FixedTriangles + requests.Sum(r => r.Report.After);
            }
        }

        internal static void ValidateResult(ReductionRequest request)
        {
            var source = request.Source; var mesh = request.Result;
            if (mesh == null || mesh.subMeshCount != source.subMeshCount || Triangles(mesh) > request.Count ||
                !mesh.bindposes.SequenceEqual(source.bindposes) || mesh.vertices.Any(v => !Finite(v)))
                throw new InvalidOperationException("Meshia produced invalid mesh data for " + request.Renderer.name);
            // An empty result is rejected by the minimum guard without adding frames to a zero-vertex mesh.
            if (mesh.vertexCount == 0) return;
            if (mesh.blendShapeCount != source.blendShapeCount)
                throw new InvalidOperationException("Meshia changed the BlendShape layout on " + request.Renderer.name);
            for (int s = 0; s < source.blendShapeCount; s++)
            {
                if (mesh.GetBlendShapeName(s) != source.GetBlendShapeName(s) || mesh.GetBlendShapeFrameCount(s) != source.GetBlendShapeFrameCount(s))
                    throw new InvalidOperationException("Meshia changed a BlendShape name or frame count.");
                for (int f = 0; f < source.GetBlendShapeFrameCount(s); f++)
                    if (mesh.GetBlendShapeFrameWeight(s, f) != source.GetBlendShapeFrameWeight(s, f))
                        throw new InvalidOperationException("Meshia changed a BlendShape frame weight.");
            }
        }

        private static bool Finite(Vector3 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
            float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
    }
}
