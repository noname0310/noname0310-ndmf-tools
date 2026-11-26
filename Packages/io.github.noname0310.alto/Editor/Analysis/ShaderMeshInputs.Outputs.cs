using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.Analysis
{
    internal sealed class ShaderPositionInputs
    {
        internal readonly Dictionary<(string Semantic, uint Index), byte> Reads = new();
        internal string Issue;
        internal bool NonFinitePositionIsHidden;
        internal string NonFiniteIssue;
        internal string NonFiniteDiagnostic;
    }

    internal sealed partial class ShaderMeshInputs
    {
        private readonly Dictionary<(Shader Shader, BuildTarget Target, string Inputs, bool NonFinite), ShaderPositionInputs> positionInputs = new();
        private readonly Dictionary<(ShaderData.Pass Pass, string Keywords, BuildTarget Target, GraphicsTier Tier), (string Issue, string Diagnostic)> nonFinitePrograms = new();

        // A complete all-pass contract for removing degenerate triangles. Unlike
        // a whole-program input query, this excludes attributes used only for
        // interpolated color/UVs. Unsupported generated geometry blocks removal.
        internal ShaderPositionInputs PositionInputs(Material material, Renderer renderer,
            AvatarAnimationIndex animations, BuildTarget target, bool proveNonFinite = false)
        {
            if (material == null || material.shader == null)
                return new ShaderPositionInputs { Issue = "missing material shader" };
            var constants = animations.MaterialInputs.ConstantUniforms(renderer, material);
            bool stable = animations.UnsafeReason == null && animations.ForObject(renderer).All(u => u.Complete && !u.Binding.isPPtrCurve);
            string signature = (stable ? ShaderKeywordPlan.MaterialSignature(material) : "<dynamic>") + "|" +
                string.Join(";", constants.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + FloatBits(p.Value)));
            var key = (material.shader, target, signature, proveNonFinite);
            if (positionInputs.TryGetValue(key, out var previous)) return previous;
            var result = new ShaderPositionInputs();
            try { result.Issue = InspectPosition(material.shader, stable ? material : null, target, constants, result, proveNonFinite); }
            catch (Exception exception) { result.Issue = "vertex output inspection failed: " + exception.Message; }
            result.NonFinitePositionIsHidden = proveNonFinite && result.Issue == null && result.NonFiniteIssue == null;
            positionInputs.Add(key, result); return result;
        }

        private string InspectPosition(Shader shader, Material material, BuildTarget target,
            IReadOnlyDictionary<string, float> constants, ShaderPositionInputs result, bool proveNonFinite)
        {
            var backends = Backends(PlayerSettings.GetGraphicsAPIs(target));
            if (Application.platform != RuntimePlatform.WindowsEditor || backends == null || backends.Length == 0 ||
                backends.Any(b => b != ShaderCompilerPlatform.D3D))
                return "vertex output reflection requires complete D3D platform coverage";
            var programs = sources.GetPrograms(shader);
            if (programs.Uncertain != null) return programs.Uncertain;
            if (programs.DegenerateRasterizationIssue != null) return programs.DegenerateRasterizationIssue;
            if (proveNonFinite && target != BuildTarget.StandaloneWindows && target != BuildTarget.StandaloneWindows64)
                result.NonFiniteIssue = "nonfinite skinning proof requires a Windows player target";
            int inspected = 0;
            foreach (var pass in programs.Passes)
            {
                if (!ShaderPassSchedule.MayExecute(pass.Pass)) continue;
                if (pass.Pass.HasShaderStage(ShaderType.Geometry) || pass.Pass.HasShaderStage(ShaderType.Hull) ||
                    pass.Pass.HasShaderStage(ShaderType.Domain)) return "shader can generate or tessellate geometry";
                if (!pass.Pass.HasShaderStage(ShaderType.Vertex)) return "a pass has no inspectable vertex program";
                string entry = Entry(pass.Pass, ShaderType.Vertex);
                if (entry == null) return "vertex entry point is not uniquely inspectable";
                if (!preprocessingIdentities.ContainsKey(pass.Pass))
                    preprocessingIdentities[pass.Pass] = sources.NativePreprocessIdentity(shader, pass.Shader,
                        pass.Pass, pass.Identifier, UnityCompilerIdentity.Value);
                var keywords = programs.ShaderWideKeywordOwners.Contains(pass.Shader) ? pass.Shader.keywordSpace.keywords :
                    ShaderUtil.GetPassKeywords(pass.Shader, pass.Identifier, ShaderType.Vertex, ShaderCompilerPlatform.D3D);
                foreach (var tier in Tiers)
                {
                    var unit = new InspectionUnit(pass.Shader, pass.Pass, ShaderType.Vertex, ShaderCompilerPlatform.D3D, tier, keywords);
                    // Until an output-use slice proves a smaller dependency set,
                    // every possible vertex keyword combination participates.
                    var plan = Plan(shader, unit, material, null);
                    if (plan.Issue != null) return plan.Issue;
                    foreach (var enabled in plan.Variants)
                    {
                        // The nonfinite proof must use the actual player program.
                        // Recompiling a source slice with stricter IEEE flags would
                        // hide Unity/driver refactoring and NaN recovery behavior.
                        if (proveNonFinite && result.NonFiniteIssue == null)
                            result.NonFiniteIssue = InspectNonFiniteProgram(pass.Pass, enabled, target, tier, out result.NonFiniteDiagnostic);
                        var variantKey = (pass.Pass, ShaderType.Vertex, string.Join(";", enabled), ShaderCompilerPlatform.D3D, target, tier);
                        if (!preprocessed.TryGetValue(variantKey, out var variant))
                        {
                            PreprocessCalls++;
                            variant = NativePreprocess(pass.Pass, ShaderType.Vertex, enabled, ShaderCompilerPlatform.D3D, target, tier);
                            preprocessed.Add(variantKey, variant);
                        }
                        if (!variant.Success || string.IsNullOrEmpty(variant.Code)) return "vertex program preprocessing failed";
                        // A projected source compiled with stricter floating-point
                        // flags can erase a branch that the player still executes.
                        // Slice the actual native program, even with no constants.
                        var compiled = NativeConstantInputs(pass.Pass, ShaderType.Vertex, enabled,
                            ShaderCompilerPlatform.D3D, target, tier, constants, "SV_POSITION", semanticInputsOnly: true);
                        if (compiled.Inputs == null) return compiled.Error ?? "native vertex output dependencies are unavailable";
                        foreach (var input in compiled.Inputs)
                        {
                            if (input.Kind != D3DShaderInputs.InputKind.Semantic || input.ReadMask == 0) continue;
                            var inputKey = (input.Semantic.ToUpperInvariant(), input.SemanticIndex);
                            result.Reads.TryGetValue(inputKey, out byte mask);
                            result.Reads[inputKey] = (byte)(mask | input.ReadMask);
                        }
                        inspected++;
                    }
                }
            }
            return inspected == 0 ? "no inspectable vertex outputs" : null;
        }

        private string InspectNonFiniteProgram(ShaderData.Pass pass, string[] keywords, BuildTarget target, GraphicsTier tier,
            out string diagnostic)
        {
            diagnostic = null;
            var key = (pass, string.Join(";", keywords), target, tier);
            if (nonFinitePrograms.TryGetValue(key, out var previous)) { diagnostic = previous.Diagnostic; return previous.Issue; }
            string issue;
            try
            {
                using var cost = AnalysisCost.Measure("Native nonfinite vertex output proof");
                // The position dependency slice and nonfinite proof inspect the
                // same player bytecode. Share its complete native artifact and
                // count this variant once among the snapshot's logical requests.
                if (TryNativeProgram(pass, ShaderType.Vertex, keywords, ShaderCompilerPlatform.D3D, target, tier,
                    out var compiled, out issue) && !D3DVertexNonFiniteProof.TryProve(compiled.ShaderData, out issue, out string program))
                    diagnostic = "Tier " + tier + "; keywords " + string.Join(";", keywords) + "\n" +
                        (program == null ? "" : program.Substring(0, Math.Min(program.Length, 32768)));
            }
            catch (Exception exception) { issue = "native vertex proof failed: " + exception.GetType().Name; }
            nonFinitePrograms.Add(key, (issue, diagnostic)); return issue;
        }
    }
}
