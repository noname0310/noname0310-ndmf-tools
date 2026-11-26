using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace Noname.AvatarTools.Materials
{
    internal static partial class SharedShaderConstantSource
    {
        private static bool EntryInputsAbsent(Shader shader, ValidationPlan validation, ShaderPropertyUsage sources,
            BuildTarget target, IEnumerable<string> names, Statistics statistics)
        {
            try { return EntryInputsAbsentCore(shader, validation, sources, target, names, statistics); }
            catch (Exception exception)
            {
                // This is a profitability prefilter, never a prerequisite for
                // runtime equivalence. Unknown source structure must leave the
                // established native validation path available.
                if (statistics != null) statistics.EntryUseDetail = "entry-use prefilter unavailable: " + exception.Message;
                return false;
            }
        }

        private static bool EntryInputsAbsentCore(Shader shader, ValidationPlan validation, ShaderPropertyUsage sources,
            BuildTarget target, IEnumerable<string> names, Statistics statistics)
        {
            var query = ShaderInputQuery.Identifiers(names);
            foreach (var unit in validation.Stages)
            {
                if (unit.Keywords == null) continue;
                // Hull entry attributes can invoke additional entry functions.
                // Retain the ordinary native proof for stages with such contracts.
                if (unit.Stage != ShaderType.Vertex && unit.Stage != ShaderType.Fragment) return false;
                string stageName = unit.Stage == ShaderType.Vertex ? "vertex" : "fragment";
                var entries = Regex.Matches(ShaderPropertyUsage.StripComments(sources.PassSource(unit.Original)),
                    @"(?m)^\s*#\s*pragma\s+" + stageName + @"\s+([_A-Za-z][_A-Za-z0-9]*)\s*$")
                    .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToArray();
                if (entries.Length != 1) return false;
                string entry = entries[0];
                foreach (var tier in ShaderMeshInputs.Tiers)
                {
                    if (sources.PreprocessorGraph(shader, shader, unit.Original, query,
                        target, ShaderCompilerPlatform.D3D, unit.Stage, tier).InputAbsent) continue;
                    ShaderInputUseSlice latest = null;
                    // Project the already-complete native keyword universe onto
                    // the discovered entry dependencies. Representatives are real
                    // variants; the fixed point reopens any newly observed calls.
                    ShaderKeywordPlan Project(ShaderPreprocessorGraph graph, Func<string, bool> ignored)
                    {
                        var representatives = new Dictionary<string, string[]>(StringComparer.Ordinal);
                        foreach (var variant in unit.Keywords)
                        {
                            string signature = string.Join(";", variant.Where(k => ignored?.Invoke(k) != true));
                            if (!representatives.ContainsKey(signature)) representatives.Add(signature, variant);
                        }
                        return new ShaderKeywordPlan { Variants = representatives.Values.ToArray(),
                            Issue = representatives.Count > ShaderMeshInputs.MaximumVariantsPerUnit
                                ? "entry-use keyword projection limit reached" : null };
                    }
                    var proof = ShaderInputVariantProof.Analyze(query, entry, enabled =>
                    {
                        var timer = System.Diagnostics.Stopwatch.StartNew();
                        var native = unit.Original.PreprocessVariant(unit.Stage, enabled, ShaderCompilerPlatform.D3D, target, tier, false);
                        if (statistics != null) statistics.PreprocessMilliseconds += timer.Elapsed.TotalMilliseconds;
                        if (!native.Success || string.IsNullOrWhiteSpace(native.PreprocessedCode))
                            return new ShaderInputVariantProof.Sample { Issue = "entry-use native preprocessing unavailable" };
                        latest = ShaderEntryUse.Analyze(new ShaderInputProgram(native.PreprocessedCode,
                            ShaderPropertyUsage.ProgramSourceId(shader)), entry, query, out bool live);
                        return new ShaderInputVariantProof.Sample { InputLive = live, Issue = latest.Issue };
                    }, demand => sources.InputDemand(shader, shader, unit.Original, demand,
                        target, ShaderCompilerPlatform.D3D, unit.Stage, tier), Project, null, _ => latest);
                    if (!proof.Complete || proof.InputLive)
                    {
                        if (statistics != null) statistics.EntryUseDetail = unit.Stage + "/" + tier + ": " +
                            (proof.InputLive ? "potential input read" : proof.Issue ?? "incomplete entry-use proof");
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
