using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Rendering;

namespace Noname.AvatarTools.Analysis
{
    // Discover a finite input-use slice, then cover its preprocessing choices.
    // Intermediate representatives are discovery probes, never absence proofs.
    // Success requires a fixed point: every projected choice was compiled and
    // proved, and none of those programs introduces a new input-use dependency.
    internal static class ShaderInputVariantProof
    {
        internal sealed class Sample : IDisposable
        {
            internal string Original, Specialized, SourceId, Issue;
            internal bool InputLive;
            internal ShaderInputPreparation Preparation;
            public void Dispose() => Preparation?.Dispose();
        }
        internal sealed class Result
        {
            internal bool Complete, InputLive;
            internal string Issue;
            internal int Samples;
            internal readonly HashSet<string> Names = new(StringComparer.Ordinal);
        }

        internal static Result Analyze(ShaderInputQuery query, string entry,
            Func<string[], Sample> sample, Func<ShaderInputDemand, ShaderPreprocessorGraph> dependencies,
            Func<ShaderPreprocessorGraph, Func<string, bool>, ShaderKeywordPlan> plan,
            Func<string, string, ShaderType, (D3DShaderInputs.Input[] Inputs, string Error)> compile,
            Func<Sample, ShaderInputUseSlice> analyzeSlice = null,
            Func<IReadOnlyList<string[]>, IDisposable> prefetch = null)
        {
            var result = new Result();
            var demand = new ShaderInputDemand(query);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var choices = plan(null, _ => true);
            for (int round = 0; round < 32; round++)
            {
                if (choices.Issue != null) { result.Issue = choices.Issue; return result; }
                List<string[]> unvisited = null;
                if (prefetch != null)
                {
                    unvisited = new List<string[]>();
                    foreach (var keywords in choices.Variants)
                        if (!visited.Contains(string.Join(";", keywords))) unvisited.Add(keywords);
                }
                using var batch = unvisited?.Count > 1 ? prefetch(unvisited) : null;
                foreach (var keywords in choices.Variants)
                {
                    if (!visited.Add(string.Join(";", keywords))) continue;
                    if (++result.Samples > ShaderMeshInputs.MaximumVariantsPerUnit)
                    { result.Issue = "Input-query representative limit reached"; return result; }
                    using var value = sample(keywords);
                    if (value.InputLive) { result.InputLive = true; return result; }
                    if (value.Issue != null) { result.Issue = value.Issue; return result; }
                    var slice = analyzeSlice == null ? Slice(value, query, entry, compile) : analyzeSlice(value);
                    if (!slice.Complete || !slice.LocationsComplete)
                    { result.Issue = slice.Issue ?? "Input-use source locations are incomplete"; return result; }
                    demand.Include(slice);
                }
                var graph = dependencies(demand);
                if (graph.Issue != null) { result.Issue = graph.Issue; return result; }
                choices = plan(graph, keyword => !graph.CodeDependencies.Contains(keyword));
                if (choices.Issue != null) { result.Issue = choices.Issue; return result; }
                if (choices.Variants.All(v => visited.Contains(string.Join(";", v))))
                { result.Names.UnionWith(demand.Names); result.Complete = true; return result; }
            }
            result.Issue = "Input-use dependency fixed point did not converge"; return result;
        }

        internal static ShaderInputUseSlice Slice(Sample value, ShaderInputQuery query, string entry,
            Func<string, string, ShaderType, (D3DShaderInputs.Input[] Inputs, string Error)> compile)
        {
            (ShaderInputProgram, ShaderInputProgram) programs;
            using (AnalysisCost.Measure("Shader slice structural parse"))
                programs = value.Preparation == null
                    ? (new ShaderInputProgram(value.Original, value.SourceId ?? ""), new ShaderInputProgram(value.Specialized))
                    : value.Preparation.Get(value.Original, value.Specialized);
            var original = programs.Item1; var specialized = programs.Item2;
            using var cost = AnalysisCost.Measure("Shader slice dataflow");
            var slice = new ShaderInputUseSlice(value.Specialized, original, query, entry, specialized.ScalarConstants, compile);
            if (specialized.Issue != null) { slice.Complete = false; slice.Issue = specialized.Issue; }
            return slice;
        }
    }
}
