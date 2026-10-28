using System;
using System.Diagnostics;
using UnityEngine.Profiling;

namespace Noname.AvatarTools.Build
{
    [Serializable]
    public sealed class OptimizationMemoryUsage
    {
        public bool Available;
        public long UnityAllocatedBytes, UnityReservedBytes, ManagedUsedBytes, ManagedHeapBytes;

        // Editor-wide snapshots, not retained avatar memory or allocation totals.
        // Managed usage includes objects awaiting GC. Do not force collection and
        // change the build being measured. These counters are not additive.
        // https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Profiling.Profiler.GetMonoUsedSizeLong.html
        public static OptimizationMemoryUsage Capture() => new()
        {
            Available = Profiler.supported,
            UnityAllocatedBytes = Profiler.GetTotalAllocatedMemoryLong(),
            UnityReservedBytes = Profiler.GetTotalReservedMemoryLong(),
            ManagedUsedBytes = Profiler.GetMonoUsedSizeLong(),
            ManagedHeapBytes = Profiler.GetMonoHeapSizeLong()
        };
    }

    [Serializable]
    public sealed class OptimizationStageMeasurement
    {
        public string Name, Kind;
        public int ParentIndex = -1;
        public double StartMilliseconds, Milliseconds;
        public bool Completed, Succeeded;
        public OptimizationMemoryUsage Before, After;
    }

    // One timeline per build report, including SDK work after NDMF serialization.
    // Parent timings are inclusive; consumers must not sum them with their children.
    internal sealed class OptimizationMeasurement : IDisposable
    {
        internal sealed class Timeline
        {
            internal readonly long Started = Stopwatch.GetTimestamp();
            internal int Current = -1;
        }

        private Timeline timeline;
        private readonly OptimizationStageMeasurement sample;
        private readonly long started;

        private OptimizationMeasurement(OptimizationBuildReport report, string kind, string name)
        {
            timeline = report.Timeline ??= new Timeline();
            sample = new OptimizationStageMeasurement
            {
                Kind = kind, Name = name, ParentIndex = timeline.Current,
                Before = OptimizationMemoryUsage.Capture()
            };
            timeline.Current = report.Measurements.Count;
            report.Measurements.Add(sample);
            started = Stopwatch.GetTimestamp();
            sample.StartMilliseconds = (started - timeline.Started) * 1000d / Stopwatch.Frequency;
        }

        internal static OptimizationMeasurement Begin(OptimizationBuildReport report, string kind, string name) =>
            report.Input != null ? new OptimizationMeasurement(report, kind, name) : null;

        internal void Complete() => sample.Succeeded = true;

        public void Dispose()
        {
            if (timeline == null) return;
            sample.Milliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
            sample.After = OptimizationMemoryUsage.Capture();
            sample.Completed = true;
            timeline.Current = sample.ParentIndex;
            timeline = null;
        }
    }
}
