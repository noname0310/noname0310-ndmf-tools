using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.BlendShapeOptimization;
using Noname.AvatarTools.Meshes;
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
        public long Before, After, ReservedMaterialPassTriangles;
        public int Target, Minimum;
    }

    [Serializable]
    public sealed class MeshReductionReport
    {
        public int Budget, Iterations;
        public long InputTriangles, OutputTriangles, FixedTriangles, Milliseconds;
        public long ReservedMaterialPassTriangles, BudgetedTriangles;
        public long FinalOutputTriangles = -1;
        public bool BudgetReached;
        public List<MeshReductionEntryReport> Renderers = new();
    }

    internal sealed class ReductionRequest
    {
        internal Renderer Renderer;
        internal Mesh Source, Result;
        internal int Count, Target, Minimum, Floor, AdditionalMaterialPasses;
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
        // Authoring markers may be stripped by SDK preprocessors. Keep the recipe,
        // selection boundaries and current source renderers until geometry settles.
        internal bool Pending, PreserveBorders, Completed;
        internal readonly HashSet<ObjectReference> BudgetRenderers = new();
        internal int Budget;
        internal BudgetExceededAction OnUnreachable;
        internal RendererSelectionMode SelectionMode;
        internal readonly Dictionary<ObjectReference, List<ReductionInput>> MappedInputs = new();
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
            if (enabled.Length > 1) throw BuildDiagnostics.Validation("Use one enabled ALTO Mesh Reduction per avatar and platform.");
            foreach (var inactive in all.Except(enabled)) Object.DestroyImmediate(inactive);
            if (enabled.Length == 0) return;
            if (MeshReductionBackend.Simplify == null)
                throw BuildDiagnostics.Validation("ALTO Mesh Reduction requires Meshia Mesh Simplification 3.2 or a compatible 3.x version.");
            var state = context.GetState<MeshReductionState>();
            state.Settings = enabled[0]; state.Scope = OptimizationBuildScope.Capture(state.Settings, context.AvatarRootObject);
            ValidateSettings(state.Settings);
            state.Pending = true;
            state.Budget = state.Settings.TriangleBudget;
            state.PreserveBorders = state.Settings.PreserveBorderEdges;
            state.OnUnreachable = state.Settings.OnBudgetUnreachable;
            state.SelectionMode = state.Settings.TargetSelection;
            void Add(Renderer renderer, HashSet<ObjectReference> set)
            {
                if (!state.Scope.Contains(renderer.transform))
                    throw BuildDiagnostics.Validation("Mesh reduction selections must be inside Scope Root: " + renderer.name);
                set.Add(ObjectRegistry.GetReference(renderer));
            }
            if (state.Settings.TargetSelection == RendererSelectionMode.SelectedRenderers)
                foreach (var renderer in state.Settings.ReductionTargets) Add(renderer, state.Targets);
            foreach (var renderer in state.Settings.PreserveRenderers) Add(renderer, state.Preserved);
            foreach (var priority in state.Settings.ReductionPriorities)
            {
                if (!state.Scope.Contains(priority.Renderer.transform))
                    throw BuildDiagnostics.Validation("Mesh reduction priorities must be inside Scope Root.");
                state.Priorities.Add(ObjectRegistry.GetReference(priority.Renderer), priority);
            }
            context.GetState<OptimizationBuildReport>().Initialize(context.AvatarRootObject,
                context.GetState<OptimizationBuildEnvironment>().Target.ToString());
        }

        internal static void ValidateSettings(ALTOMeshReduction settings)
        {
            if (settings.TriangleBudget < 0) throw BuildDiagnostics.Validation("Triangle Budget cannot be negative.");
            if (!Enum.IsDefined(typeof(RendererSelectionMode), settings.TargetSelection))
                throw BuildDiagnostics.Validation("Choose a valid reduction Target Selection mode.");
            if (settings.ReductionTargets == null || settings.PreserveRenderers == null || settings.ReductionPriorities == null ||
                settings.PreserveRenderers.Any(r => r == null || !IsMeshRenderer(r)) ||
                settings.TargetSelection == RendererSelectionMode.SelectedRenderers && settings.ReductionTargets.Any(r => r == null || !IsMeshRenderer(r)))
                throw BuildDiagnostics.Validation("ALTO Mesh Reduction selections must reference existing MeshRenderers or SkinnedMeshRenderers.");
            var seen = new HashSet<Renderer>();
            foreach (var priority in settings.ReductionPriorities)
                if (priority == null || priority.Renderer == null || !IsMeshRenderer(priority.Renderer) || !seen.Add(priority.Renderer) ||
                    priority.MinimumTriangles < 0 || float.IsNaN(priority.Weight) || float.IsInfinity(priority.Weight) || priority.Weight <= 0)
                    throw BuildDiagnostics.Validation("Reduction priorities require unique renderers, positive finite weights and nonnegative minima.");
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
                    throw BuildDiagnostics.Validation("ALTO Mesh Reduction overlaps an enabled Meshia reducer on " + renderer.name + ". Use one reduction policy for this renderer.");
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
                InScope = state.Scope.Contains(renderer.transform), Selected = state.SelectionMode == RendererSelectionMode.AllRenderersInScope || state.Targets.Contains(reference),
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
        internal static long Triangles(Mesh mesh)
        {
            if (mesh == null) return 0;
            long result = 0;
            // Match the SDK's MeshUtils count, including fixed, non-reducible quad meshes.
            for (int i = 0; i < mesh.subMeshCount; i++)
                result += mesh.GetIndexCount(i) / (mesh.GetTopology(i) == MeshTopology.Quads ? 2L : 3L);
            return result;
        }

        internal static long BudgetCost(Mesh mesh, int additionalPasses) => Triangles(mesh) +
            (mesh == null || mesh.subMeshCount == 0 ? 0 : additionalPasses *
                ((long)mesh.GetIndexCount(mesh.subMeshCount - 1) / (mesh.GetTopology(mesh.subMeshCount - 1) == MeshTopology.Quads ? 2 : 3)));

        private static int AdditionalPasses(BuildContext context, Renderer renderer, Mesh mesh)
        {
            var assembly = context.GetState<MeshOptimizationState>();
            if (mesh == null || mesh.subMeshCount == 0 || !assembly.Enabled || !(assembly.MergeRenderers || assembly.MergeSlots) ||
                !assembly.Scope.Contains(renderer.transform) || MeshOptimizationPass.InputProtected(assembly, renderer, out _) ||
                renderer is MeshRenderer && !assembly.MayConvertRigid) return 0;
            // Assembly can turn Unity's implicit repeats of the final submesh into
            // explicit submeshes. Charge their upper bound before reduction, including
            // outputs which only become mergeable once reduction has completed.
            return Math.Max(0, renderer.sharedMaterials.Length - mesh.subMeshCount);
        }

        internal static void Apply(BuildContext context)
        {
            var state = context.GetState<MeshReductionState>();
            if (!state.Pending) return;
            if (state.Settings != null)
            {
                var mapped = state.Settings.BuildInputRenderers;
                if (mapped.Length != state.Inputs.Count)
                    throw new InvalidOperationException("Mesh reduction input mapping is incomplete.");
                for (int i = 0; i < mapped.Length; i++)
                {
                    if (mapped[i] == null) continue;
                    var reference = ObjectRegistry.GetReference(mapped[i]);
                    if (!state.MappedInputs.TryGetValue(reference, out var inputs)) state.MappedInputs.Add(reference, inputs = new());
                    inputs.Add(state.Inputs[i]);
                }
                Object.DestroyImmediate(state.Settings);
            }
            if (AvatarOutputSdkFinalizer.HasSession(context) || context.GetState<MaterialAtlas.MaterialAtlasState>().Pending) return;
            // Geometry-only mesh passes participate in this fixed point. Assembly
            // is gated by Pending, so a preserved face cannot absorb reducible cloth.
            OptimizationFixedPoint.Apply(context, includeMeshes: true);
            using var lease = OptimizationSession.Acquire(context);
            ApplyFinal(context, lease.Services.ControllerContext.Controllers,
                lease.Services.ObjectPathRemapper.GetVirtualToRealPathMap(), context.AssetSaver.SaveAsset);
        }

        internal static void ApplyFinal(BuildContext context, IDictionary<object, VirtualAnimatorController> controllers,
            IReadOnlyDictionary<string, string> paths, Action<Object> save, string unsupported = null)
        {
            var state = context.GetState<MeshReductionState>();
            if (!state.Pending) return;
            var watch = Stopwatch.StartNew();
            var buildReport = context.GetState<OptimizationBuildReport>();
            var report = new MeshReductionReport { Budget = state.Budget };
            buildReport.MeshReduction = report;
            var hazards = MeshReductionSafety.Find(context.AvatarRootObject, controllers, paths);
            var requests = new List<ReductionRequest>();
            foreach (var renderer in context.AvatarRootObject.GetComponentsInChildren<Renderer>(true).Where(IsMeshRenderer))
            {
                if (!state.MappedInputs.TryGetValue(ObjectRegistry.GetReference(renderer), out var entries)) entries = new() { Selection(state, renderer) };
                if (!entries.Any(e => e.InScope)) continue;
                state.BudgetRenderers.Add(ObjectRegistry.GetReference(renderer));
                var mesh = MeshOf(renderer); long triangles = Triangles(mesh);
                int additionalPasses = AdditionalPasses(context, renderer, mesh);
                long cost = BudgetCost(mesh, additionalPasses);
                string reason = entries.Any(e => !e.InScope) ? "merged with an out-of-scope input; counting the whole renderer conservatively" :
                    entries.Any(e => e.Preserved) ? "preserved renderer or merged preserved input" :
                    entries.Any(e => !e.Selected) ? "unselected renderer or merged unselected input" :
                    unsupported != null ? unsupported :
                    hazards.TryGetValue(renderer, out var hazard) ? hazard :
                    mesh == null || triangles == 0 ? "no triangles" :
                    cost > int.MaxValue ? "triangle count exceeds the reducer's range" :
                    MeshReductionBackend.Validate?.Invoke(renderer, mesh);
                var entry = new MeshReductionEntryReport
                {
                    Renderer = AnimationUtility.CalculateTransformPath(renderer.transform, context.AvatarRootTransform),
                    Sources = entries.Select(e => e.Path).Where(p => p != null).Distinct().ToArray(),
                    Before = triangles, After = triangles, Reason = reason
                };
                report.Renderers.Add(entry); report.InputTriangles += triangles;
                entry.ReservedMaterialPassTriangles = cost - triangles;
                report.ReservedMaterialPassTriangles += cost - triangles;
                if (reason != null) { report.FixedTriangles += triangles; continue; }
                int minimum = (int)Math.Min(triangles, Math.Max(1L, entries.Sum(e => (long)e.Minimum)));
                entry.Minimum = minimum; entry.Target = (int)triangles;
                requests.Add(new ReductionRequest
                {
                    Renderer = renderer, Source = mesh, Count = (int)triangles, Minimum = minimum, Floor = minimum,
                    Weight = entries.Max(e => e.Weight), Report = entry, AdditionalMaterialPasses = additionalPasses
                });
            }
            try
            {
                Reduce(requests, report, state.PreserveBorders);
                foreach (var request in requests)
                {
                    if (request.Result == null) continue;
                    ObjectRegistry.RegisterReplacedObject(request.Source, request.Result);
                    save(request.Result);
                    if (request.Renderer is SkinnedMeshRenderer skin)
                    {
                        var weights = Enumerable.Range(0, request.Source.blendShapeCount).Select(skin.GetBlendShapeWeight).ToArray();
                        context.GetState<OptimizationState>().Provenance.RecordMeshRewrite(skin, request.Source, request.Result);
                        skin.sharedMesh = request.Result;
                        for (int i = 0; i < weights.Length; i++) skin.SetBlendShapeWeight(i, weights[i]);
                    }
                    else request.Renderer.GetComponent<MeshFilter>().sharedMesh = request.Result;
                    request.Result = null; // Saved mesh ownership has transferred to the build.
                }
                report.BudgetReached = report.BudgetedTriangles <= report.Budget;
                report.Milliseconds = watch.ElapsedMilliseconds;
                buildReport.Messages.Add($"Mesh reduction: {report.InputTriangles} -> {report.OutputTriangles} triangles, budget {report.Budget}; {report.FixedTriangles} fixed; {report.Iterations} allocation rounds; {report.Milliseconds} ms.");
                if (report.ReservedMaterialPassTriangles > 0)
                    buildReport.Messages.Add($"Reserved {report.ReservedMaterialPassTriangles} additional triangles for material-pass expansion during final mesh assembly; budgeted total {report.BudgetedTriangles}.");
                foreach (var entry in report.Renderers.Where(e => e.Reason != null && e.Before > 0))
                    buildReport.Messages.Add("Reduction retained " + entry.Renderer + ": " + entry.Reason);
                // A reservation is an upper bound, not a reason to fail a build whose
                // final output fits. Recheck the actual topology after final assembly.
                if (!report.BudgetReached && report.OutputTriangles > report.Budget)
                {
                    string message = $"Mesh reduction did not reach its budget: {report.OutputTriangles} / {report.Budget} triangles. " +
                        "Preserved inputs, minimum counts and topology constraints limit reduction; inspect the per-renderer build report.";
                    buildReport.Messages.Add(message);
                    string details = message + "\n\n" + string.Join("\n", report.Renderers
                        .Where(e => e.Reason != null && e.After > 0)
                        .Select(e => e.Renderer + ": " + e.After + " triangles; " + e.Reason));
                    const string hint = "Review Preserve Renderers, per-renderer minimums and Preserve Border Edges in Mesh Reduction.";
                    if (state.OnUnreachable == BudgetExceededAction.Error)
                        throw BuildDiagnostics.Validation("Triangle budget could not be reached", details, hint, context.AvatarRootObject);
                    BuildDiagnostics.Warning(context, "Triangle budget could not be reached", details, hint, context.AvatarRootObject);
                }
                state.Completed = true;
            }
            finally
            {
                foreach (var request in requests) if (request.Result != null) Object.DestroyImmediate(request.Result);
                state.Pending = false;
                context.GetState<OptimizationAnalysis>().Invalidate(AnalysisDomain.Meshes);
            }
        }

        internal static void Reduce(List<ReductionRequest> requests, MeshReductionReport report, bool preserveBorders)
        {
            report.OutputTriangles = report.InputTriangles;
            long fixedReservation = report.ReservedMaterialPassTriangles - requests.Sum(r => r.Report.ReservedMaterialPassTriangles);
            foreach (var request in requests)
                request.Report.ReservedMaterialPassTriangles = BudgetCost(request.Source, request.AdditionalMaterialPasses) - request.Count;
            report.ReservedMaterialPassTriangles = fixedReservation + requests.Sum(r => r.Report.ReservedMaterialPassTriangles);
            report.BudgetedTriangles = report.OutputTriangles + report.ReservedMaterialPassTriangles;
            if (report.BudgetedTriangles <= report.Budget) return;
            // Redistribute budget when topology prevents a renderer from reaching its allocation.
            // Always simplify the original input, avoiding cumulative quality loss between rounds.
            for (int round = 0; round < 3 && report.BudgetedTriangles > report.Budget; round++)
            {
                var targets = TriangleBudgetAllocator.Allocate(requests.Select(r => new TriangleAllocationInput(
                        checked((int)BudgetCost(r.Source, r.AdditionalMaterialPasses)), r.Floor, r.Weight)).ToArray(),
                    (long)report.Budget - report.FixedTriangles - fixedReservation);
                var work = new List<ReductionRequest>();
                var costTargets = new Dictionary<ReductionRequest, int>();
                bool raisedFloor = false;
                for (int i = 0; i < requests.Count; i++)
                {
                    var request = requests[i];
                    long currentCost = request.Report.After + request.Report.ReservedMaterialPassTriangles;
                    if (targets[i] >= currentCost) continue;
                    // Translate the cost allocation into Meshia's geometry target. Use
                    // the measured submesh distribution on retries, while simplifying
                    // the original mesh each time to avoid cumulative quality loss.
                    int geometryTarget = Math.Max(request.Minimum,
                        (int)(request.Report.After * targets[i] / Math.Max(1L, currentCost)));
                    if (geometryTarget >= request.Report.After)
                    {
                        raisedFloor |= currentCost > request.Floor;
                        request.Floor = checked((int)currentCost);
                        continue;
                    }
                    if (request.Result != null) Object.DestroyImmediate(request.Result);
                    request.Result = null; request.Target = geometryTarget; request.Report.Target = geometryTarget; work.Add(request);
                    costTargets.Add(request, targets[i]);
                }
                if (work.Count == 0)
                {
                    if (raisedFloor) continue;
                    break;
                }
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
                    long actualCost = BudgetCost(request.Result != null ? request.Result : request.Source, request.AdditionalMaterialPasses);
                    request.Report.ReservedMaterialPassTriangles = actualCost - actual;
                    if (actualCost > costTargets[request] && (actual > request.Target || request.Target <= request.Minimum))
                    {
                        request.Floor = checked((int)actualCost);
                        request.Report.Reason ??= "Meshia stopped above its allocation under the current topology constraints";
                    }
                }
                report.OutputTriangles = report.FixedTriangles + requests.Sum(r => r.Report.After);
                report.ReservedMaterialPassTriangles = fixedReservation + requests.Sum(r => r.Report.ReservedMaterialPassTriangles);
                report.BudgetedTriangles = report.OutputTriangles + report.ReservedMaterialPassTriangles;
            }
        }

        internal static void RecordAssembly(BuildContext context, IEnumerable<Renderer> sources, Renderer destination)
        {
            var state = context.GetState<MeshReductionState>();
            if (!state.Completed) return;
            foreach (var source in sources)
                if (state.Scope.Contains(source.transform) || state.BudgetRenderers.Contains(ObjectRegistry.GetReference(source)))
                {
                    // A destination outside the original scope still contains its
                    // geometry. Count that merged renderer rather than losing the budget.
                    state.BudgetRenderers.Add(ObjectRegistry.GetReference(destination));
                    break;
                }
        }

        internal static void ValidateFinalBudget(BuildContext context)
        {
            var state = context.GetState<MeshReductionState>();
            if (!state.Completed || state.Pending) return;
            var buildReport = context.GetState<OptimizationBuildReport>();
            var report = buildReport.MeshReduction;
            long total = 0;
            foreach (var renderer in context.AvatarRootObject.GetComponentsInChildren<Renderer>(true))
                if (IsMeshRenderer(renderer) && (state.Scope.Contains(renderer.transform) ||
                    state.BudgetRenderers.Contains(ObjectRegistry.GetReference(renderer)))) total += Triangles(MeshOf(renderer));
            if (report.FinalOutputTriangles == total) return;
            report.FinalOutputTriangles = total;
            report.BudgetReached = total <= report.Budget;
            buildReport.Messages.Add($"Final mesh triangle budget: {total} / {report.Budget} triangles after mesh assembly.");
            if (report.BudgetReached || report.OutputTriangles > report.Budget) return;
            string details = $"Final mesh assembly produced {total} triangles, exceeding the configured budget of {report.Budget}.";
            const string hint = "Review Mesh Reduction preservation and minimums. Additional material passes can require explicit submeshes after merging.";
            if (state.OnUnreachable == BudgetExceededAction.Error)
                throw BuildDiagnostics.Validation("Final triangle budget exceeded", details, hint, context.AvatarRootObject);
            BuildDiagnostics.Warning(context, "Final triangle budget exceeded", details, hint, context.AvatarRootObject);
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
