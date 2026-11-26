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
        public bool ManagedAllocationsAvailable;
        public long ThreadAllocatedBytes;
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
        private readonly long allocatedBefore;
        private readonly int allocationThread;
        private static Func<long> allocationCounter = CreateAllocationCounter();

        // Unlike the editor-wide heap snapshots, this is an allocation total for
        // the executing thread. Nested measurements are inclusive; worker-thread
        // allocations and native Unity/shader-compiler memory are not included.
        private static Func<long> CreateAllocationCounter()
        {
            var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
            return method == null || method.ReturnType != typeof(long)
                ? null : (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
        }

        private static long ReadAllocatedBytes()
        {
            if (allocationCounter == null) return -1;
            try { return allocationCounter(); }
            catch (NotSupportedException) { allocationCounter = null; return -1; }
            catch (NotImplementedException) { allocationCounter = null; return -1; }
        }

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
            allocationThread = Environment.CurrentManagedThreadId;
            allocatedBefore = ReadAllocatedBytes();
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
            long allocatedAfter = Environment.CurrentManagedThreadId == allocationThread ? ReadAllocatedBytes() : -1;
            // Some Unity Mono versions expose the API but return zero even after
            // allocating this measurement and its timeline. Do not report that
            // unsupported counter as a measured zero-allocation optimization.
            sample.ManagedAllocationsAvailable = allocatedBefore > 0 && allocatedAfter >= allocatedBefore;
            if (sample.ManagedAllocationsAvailable) sample.ThreadAllocatedBytes = allocatedAfter - allocatedBefore;
            sample.After = OptimizationMemoryUsage.Capture();
            sample.Completed = true;
            timeline.Current = sample.ParentIndex;
            timeline = null;
        }
    }
}
