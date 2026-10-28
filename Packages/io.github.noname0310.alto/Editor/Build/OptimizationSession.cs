using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;

namespace Noname.AvatarTools.Build
{
    [Flags]
    internal enum AnalysisDomain
    {
        None = 0, Animation = 1, Hierarchy = 2, Meshes = 4, Materials = 8,
        Physics = 16, ShaderSources = 32, ExternalInputs = 64, All = 127
    }

    // Build-local facts have explicit mutation domains. Nothing survives a build or
    // an external-plugin boundary. A failed pass invalidates its possible writes too.
    internal sealed class OptimizationAnalysis
    {
        private sealed class Entry
        {
            internal AnalysisDomain Dependencies;
            internal object Value;
        }
        private readonly Dictionary<object, Entry> entries = new();
        internal T Get<T>(object key, AnalysisDomain dependencies, Func<T> create)
        {
            if (entries.TryGetValue(key, out var entry))
            {
                if (entry.Dependencies != dependencies) throw new InvalidOperationException("Analysis dependencies changed for the same key.");
                return (T)entry.Value;
            }
            var value = create(); entries.Add(key, new Entry { Dependencies = dependencies, Value = value }); return value;
        }
        internal void Invalidate(AnalysisDomain changed)
        {
            foreach (var key in entries.Where(p => (p.Value.Dependencies & changed) != 0).Select(p => p.Key).ToArray()) entries.Remove(key);
        }
        internal ShaderPropertyUsage Shaders => Get(typeof(ShaderPropertyUsage), AnalysisDomain.ShaderSources, () => new ShaderPropertyUsage());
    }

    // Design reference: AAO 1.9.20 OptimizerPlugin's staged execution. Independently
    // implemented session ownership and dependency invalidation; see CREDITS.md.
    internal sealed class OptimizationSession
    {
        private BuildContext context;
        private AnimatorServicesContext services;
        private int users;
        private bool boundary;

        internal sealed class AnimationLease : IDisposable
        {
            private OptimizationSession owner;
            internal AnimatorServicesContext Services => owner.services;
            internal AnimationLease(OptimizationSession owner) { this.owner = owner; owner.users++; }
            public void Dispose()
            {
                if (owner == null) return;
                var previous = owner; owner = null; previous.users--;
                if (!previous.boundary && previous.users == 0) previous.Release();
            }
        }

        internal static AnimationLease Acquire(BuildContext context)
        {
            var state = context.GetState<OptimizationSession>(); state.context = context;
            state.services ??= context.ActivateExtensionContextRecursive<AnimatorServicesContext>();
            return new AnimationLease(state);
        }

        private void Release()
        {
            if (services == null) return;
            try { context.DeactivateExtensionContext<AnimatorServicesContext>(); }
            finally
            {
                services = null;
                context.DeactivateExtensionContext<VirtualControllerContext>();
            }
        }

        internal static void RunBoundary(BuildContext context, string name, Action run)
        {
            var state = context.GetState<OptimizationSession>(); state.context = context;
            if (state.boundary || state.users != 0) throw new InvalidOperationException("Optimization boundaries must not overlap.");
            var analysis = context.GetState<OptimizationAnalysis>();
            analysis.Invalidate(AnalysisDomain.All);
            state.boundary = true;
            var watch = Stopwatch.StartNew();
            var report = context.GetState<OptimizationBuildReport>();
            using var measurement = OptimizationMeasurement.Begin(report, "Boundary", name);
            try { run(); }
            finally
            {
                state.boundary = false;
                // Every pass owns a short lease; only the boundary owns serialization.
                // Release even after an exception so another plugin sees committed data.
                try { state.Release(); }
                finally { analysis.Invalidate(AnalysisDomain.All); }
                if (report.Input != null) report.Messages.Add("Optimization boundary " + name + ": " + watch.ElapsedMilliseconds + " ms.");
            }
            measurement?.Complete();
        }

        internal static void Step(BuildContext context, string name, AnalysisDomain writes, Action<BuildContext> run)
        {
            var watch = Stopwatch.StartNew();
            var report = context.GetState<OptimizationBuildReport>();
            using var measurement = OptimizationMeasurement.Begin(report, "Step", name);
            try { run(context); }
            finally
            {
                context.GetState<OptimizationAnalysis>().Invalidate(writes);
                if (report.Input != null) report.Messages.Add("Optimization step " + name + ": " + watch.ElapsedMilliseconds + " ms.");
            }
            measurement?.Complete();
        }
    }
}
