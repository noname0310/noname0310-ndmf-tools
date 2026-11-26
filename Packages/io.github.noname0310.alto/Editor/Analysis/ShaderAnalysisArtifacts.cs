using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Analysis
{
    // Owned by the build, independently of mutable avatar/source snapshots.
    // No avatar objects, material assumptions, variant-coverage certificates or
    // compiler budgets are shared. Native programs require a freshly validated
    // input identity at every source snapshot boundary.
    internal sealed class ShaderAnalysisArtifacts
    {
        // Internal differential-build switch; never changes proof coverage.
        internal static bool ShareAcrossSnapshots = true;
        internal readonly BoundedAnalysisCache<ShaderTextKey, ShaderConstantInputs.Plan> Declarations = new(256, 32 * 1024 * 1024);
        internal readonly BoundedAnalysisCache<(ShaderTextKey, string), (string Code, int Count)> Specializations = new(256, 32 * 1024 * 1024);
        private readonly BoundedAnalysisCache<ShaderTextKey, ShaderProgramInspection> inspections = new(512, 32 * 1024 * 1024);
        private readonly BoundedAnalysisCache<ShaderTextKey, ShaderPropertyUsage.SourceSummary> sourceFacts = new(512, 32 * 1024 * 1024);
        private readonly BoundedAnalysisCache<ShaderTextKey, ShaderPropertyUsage.LabSummary> labFacts = new(128, 32 * 1024 * 1024);
        private readonly BoundedAnalysisCache<ShaderTextKey, string> commentFree = new(512, 32 * 1024 * 1024);
        private readonly BoundedAnalysisCache<ShaderTextKey, List<List<string>>> nativeImports = new(128, 32 * 1024 * 1024);
        internal int SourceScans { get; private set; }
        internal int LabScans { get; private set; }

        internal List<List<string>> NativeImports(string source)
        {
            ShaderTextKey key = source;
            if (nativeImports.TryGetValue(key, out var previous)) return previous;
            using var cost = AnalysisCost.Measure("Shader discovery/import slot parsing");
            var result = ShaderPropertyUsage.NativeImportSlots(source);
            int size = source.Length * 2;
            if (result != null)
                foreach (var sub in result) foreach (string name in sub) size += 16 + (name?.Length ?? 0) * 2;
            nativeImports.Add(key, result, size);
            return result;
        }

        internal string WithoutComments(string source)
        {
            ShaderTextKey key = source;
            if (commentFree.TryGetValue(key, out var previous)) return previous;
            using var cost = AnalysisCost.Measure("Shader discovery/comment removal");
            string result = ShaderPropertyUsage.StripComments(source);
            commentFree.Add(key, result, (source.Length + result.Length) * 2);
            return result;
        }

        internal ShaderPropertyUsage.SourceSummary SourceFacts(string source)
        {
            ShaderTextKey key = source;
            if (sourceFacts.TryGetValue(key, out var previous)) return previous;
            using var cost = AnalysisCost.Measure("Shader lexical summary");
            var facts = ShaderPropertyUsage.SourceSummary.Get(source); SourceScans++;
            sourceFacts.Add(key, facts, source.Length * 2 + facts.Cost); return facts;
        }

        internal ShaderPropertyUsage.LabSummary LabFacts(string source)
        {
            ShaderTextKey key = source;
            if (labFacts.TryGetValue(key, out var previous)) return previous;
            using var cost = AnalysisCost.Measure("ShaderLab summary");
            var facts = ShaderPropertyUsage.LabSummary.Get(source); LabScans++;
            labFacts.Add(key, facts, source.Length * 2 + facts.Cost); return facts;
        }
        private sealed class NativeProgram
        {
            internal readonly ShaderTextKey Source;
            internal NativeProgram(string source) { Source = source; }
        }
        private readonly BoundedAnalysisCache<ShaderTextKey, NativeProgram> nativePrograms = new(2048, 64 * 1024 * 1024);
        private readonly BoundedAnalysisCache<(string, ShaderType, string, ShaderCompilerPlatform, BuildTarget, GraphicsTier), WeakReference<NativeProgram>>
            preprocessed = new(2048, 2048);
        internal int InspectionScans { get; private set; }
        internal int PreprocessHits { get; private set; }
        internal int NativeProgramCount => nativePrograms.Count;
        internal int NativeVariantCount => preprocessed.Count;
        internal long NativeProgramBytes => nativePrograms.Cost;

        internal ShaderProgramInspection Inspection(string source)
        {
            ShaderTextKey key = source;
            if (inspections.TryGetValue(key, out var value)) return value;
            value = new ShaderProgramInspection(source); InspectionScans++;
            inspections.Add(key, value, (source?.Length ?? 0) * 2 + value.Cost);
            return value;
        }

        internal bool TryPreprocessed(string identity, ShaderType stage, string keywords, ShaderCompilerPlatform backend,
            BuildTarget target, GraphicsTier tier, out string source)
        {
            source = null;
            if (identity == null || !preprocessed.TryGetValue((identity, stage, keywords, backend, target, tier), out var reference) ||
                !reference.TryGetTarget(out var program)) return false;
            // Equivalent tiers/pass wrappers share one program's storage budget.
            // Missing/collected content is a cache miss, never an absence proof.
            if (!nativePrograms.TryGetValue(program.Source, out _)) return false;
            source = program.Source.Text;
            PreprocessHits++; return true;
        }

        internal void StorePreprocessed(string identity, ShaderType stage, string keywords, ShaderCompilerPlatform backend,
            BuildTarget target, GraphicsTier tier, bool success, string source)
        {
            // Failed imports and missing native programs can be transient.
            if (identity == null || !success || string.IsNullOrEmpty(source)) return;
            ShaderTextKey key = source;
            if (!nativePrograms.TryGetValue(key, out var program))
            { program = new NativeProgram(source); nativePrograms.Add(key, program, source.Length * 2); }
            preprocessed.Add((identity, stage, keywords, backend, target, tier), new WeakReference<NativeProgram>(program), 1);
        }
    }
}
