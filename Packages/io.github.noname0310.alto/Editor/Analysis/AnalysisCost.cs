using System;
using System.Diagnostics;

namespace Noname.AvatarTools.Analysis
{
    // Optional build-local profiling. Disabled scopes allocate nothing and do
    // not read the clock; callers own collection, lifetime and nesting semantics.
    internal static class AnalysisCost
    {
        [ThreadStatic] internal static Action<string, double> Observer;
        internal readonly struct Scope : IDisposable
        {
            private readonly Action<string, double> observer;
            private readonly string name;
            private readonly long started;
            internal Scope(string name)
            { observer = Observer; this.name = name; started = observer == null ? 0 : Stopwatch.GetTimestamp(); }
            public void Dispose()
            {
                observer?.Invoke(name, (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency);
            }
        }
        internal static Scope Measure(string name) => new(name);
        internal static Scope Measure(string name, Type type) => Observer == null ? default : new(name + "/" + type.FullName);
    }
}
