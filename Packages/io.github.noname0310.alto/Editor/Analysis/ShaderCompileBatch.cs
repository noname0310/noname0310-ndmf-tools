using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEditor.Rendering;

namespace Noname.AvatarTools.Analysis
{
    // Workers only receive immutable compiler inputs. The owner consumes results
    // and charges its analysis budget in the original, deterministic order.
    // Disposal joins every speculative job, including abandoned proof branches.
    internal sealed class ShaderCompileBatch : IDisposable
    {
        private readonly struct Result
        {
            internal readonly D3DShaderInputs.Input[] Inputs;
            internal readonly string Error;
            internal readonly double Milliseconds;
            internal Result(D3DShaderInputs.Input[] inputs, string error, double milliseconds)
            { Inputs = inputs; Error = error; Milliseconds = milliseconds; }
        }

        private readonly Dictionary<(ShaderTextKey, string, ShaderType), Task<Result>> jobs = new();
        private readonly Func<string, string, ShaderType, (D3DShaderInputs.Input[] Inputs, string Error)> compile;
        private readonly int limit;
        private bool disposed;
        internal int Count => jobs.Count;
        internal bool Contains(string source, string entry, ShaderType stage) => jobs.ContainsKey((source, entry, stage));
        internal double WorkMilliseconds { get; private set; }
        internal double WaitMilliseconds { get; private set; }

        internal ShaderCompileBatch(int limit,
            Func<string, string, ShaderType, (D3DShaderInputs.Input[] Inputs, string Error)> compile = null)
        {
            this.limit = Math.Max(1, limit);
            this.compile = compile ?? ((source, entry, stage) =>
            {
                D3DShaderInputs.CompileNative(source, entry, stage, out var inputs, out string error);
                return (inputs, error);
            });
        }

        internal bool Add(string source, string entry, ShaderType stage)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ShaderCompileBatch));
            var key = (source, entry, stage);
            if (jobs.ContainsKey(key)) return true;
            if (jobs.Count >= limit) return false;
            jobs.Add(key, Task.Run(() =>
            {
                var timer = Stopwatch.StartNew();
                try
                {
                    var result = compile(source, entry, stage);
                    return new Result(result.Inputs, result.Error, timer.Elapsed.TotalMilliseconds);
                }
                catch (Exception exception)
                { return new Result(null, "D3D input analysis failed: " + exception.GetType().Name, timer.Elapsed.TotalMilliseconds); }
            }));
            return true;
        }

        internal bool TryGet(string source, string entry, ShaderType stage,
            out (D3DShaderInputs.Input[] Inputs, string Error) proof)
        {
            proof = default;
            if (!jobs.TryGetValue((source, entry, stage), out var job)) return false;
            var timer = Stopwatch.StartNew();
            var result = job.GetAwaiter().GetResult();
            WaitMilliseconds += timer.Elapsed.TotalMilliseconds;
            proof = (result.Inputs, result.Error); return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var timer = Stopwatch.StartNew();
            foreach (var job in jobs.Values) WorkMilliseconds += job.GetAwaiter().GetResult().Milliseconds;
            WaitMilliseconds += timer.Elapsed.TotalMilliseconds;
        }
    }
}
