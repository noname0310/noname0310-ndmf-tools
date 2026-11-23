using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private Material UniformProofMaterial(SkinnedMeshRenderer target, float gate = 0)
        {
            var material = Track(new Material(Shader.Find("Hidden/Noname/Material Uniform Proof")));
            material.SetFloat("_Gate", gate); target.sharedMaterial = material; return material;
        }

        [Test]
        public void ShaderUniformReflectionDistinguishesLiveDeadAndAbsentVariables()
        {
            RequireD3DAnalysis();
            const string source = "float _Live; float _Dead; static const float _Gate=0; float4 frag():SV_Target { if (_Gate>0) return _Dead; return _Live; }";
            Assert.That(D3DShaderInputs.CompileUncached(source, "frag", ShaderType.Fragment, out var inputs, out var error), Is.True, error);
            Assert.That(inputs.Any(i => ShaderInputQuery.Uniform("_Live").MatchesCompiled(i) && i.ReadMask != 0), Is.True);
            Assert.That(inputs.Any(i => ShaderInputQuery.Uniform("_Dead").MatchesCompiled(i) && i.ReadMask != 0), Is.False);
            Assert.That(inputs.Any(i => ShaderInputQuery.Uniform("_Absent").MatchesCompiled(i) && i.ReadMask != 0), Is.False);
        }

        [Test]
        public void ShaderUniformReflectionDoesNotTreatStrippedMetadataAsAnEmptyInterface()
        {
            RequireD3DAnalysis();
            var shader = Shader.Find("Hidden/Noname/Material Uniform Proof");
            var pass = ShaderUtil.GetShaderData(shader).GetSubshader(0).GetPass(0);
            var result = pass.CompileVariant(ShaderType.Fragment, new string[0], ShaderCompilerPlatform.D3D,
                BuildTarget.StandaloneWindows64, UnityEngine.Rendering.GraphicsTier.Tier1, true);
            Assert.That(result.Success, Is.True);
            // Some Unity versions retain metadata; either outcome must be safe.
            bool complete = D3DShaderInputs.Reflect(result.ShaderData, ShaderType.Fragment, out var inputs, out string error, true);
            if (complete) Assert.That(inputs.Any(i => ShaderInputQuery.Uniform("_Self").MatchesCompiled(i) && i.ReadMask != 0), Is.True);
            else Assert.That(error, Does.Contain("uniform reflection metadata"));
        }

        [TestCase(false)] [TestCase(true)]
        public void ShaderUniformAnalysisUsesOnlyUnchangedMaterialConstants(bool animatedGate)
        {
            RequireD3DAnalysis(); var pair = MergePair(); var material = UniformProofMaterial(pair.First);
            var clip = Clip();
            if (animatedGate) AnimationUtility.SetEditorCurve(clip, UniformBinding("First", "_Gate"), AnimationCurve.Linear(0, 0, 1, 1));
            var index = FrameIndex(clip); var sources = new ShaderPropertyUsage();
            Assert.That(sources.MayRead(material, pair.First, index, "_Target", new HashSet<string> { "_Target" }, BuildTarget.StandaloneWindows64),
                Is.EqualTo(animatedGate), sources.MeshInputs.Issue(material, pair.First, index, ShaderInputQuery.Uniform("_Target"),
                    BuildTarget.StandaloneWindows64, new HashSet<string> { "_Target" }) + "\n" + string.Join("\n", sources.MeshInputs.UnitDiagnostics));
        }

        [Test]
        public void ShaderUniformAnalysisNeverFreezesItsQueryOrOtherIntroducedWriters()
        {
            RequireD3DAnalysis(); var pair = MergePair(); var material = UniformProofMaterial(pair.First);
            var sources = new ShaderPropertyUsage(); var index = FrameIndex();
            Assert.That(sources.MayRead(material, pair.First, index, "_Target", new HashSet<string> { "_Target" }, BuildTarget.StandaloneWindows64), Is.False);
            Assert.That(sources.MayRead(material, pair.First, index, "_Target", new HashSet<string> { "_Target", "_Gate" }, BuildTarget.StandaloneWindows64), Is.True);
            Assert.That(sources.MayRead(material, pair.First, index, "_Self", new HashSet<string>(), BuildTarget.StandaloneWindows64), Is.True);
            Assert.That(sources.MayRead(material, pair.First, index, "_Cull", new HashSet<string>(), BuildTarget.StandaloneWindows64), Is.True,
                "ShaderLab render state is observable even without a uniform read.");
        }

        [Test]
        public void ShaderUniformAnalysisRetainsRuntimeVariantsAndInvalidatesMaterialKeywordProofs()
        {
            RequireD3DAnalysis(); var pair = MergePair(); var material = UniformProofMaterial(pair.First);
            var sources = new ShaderPropertyUsage(); var index = FrameIndex(); var changed = new HashSet<string>();
            Assert.That(sources.MayRead(material, pair.First, index, "_GlobalTarget", changed, BuildTarget.StandaloneWindows64), Is.True);
            Assert.That(sources.MayRead(material, pair.First, index, "_HelperTarget", changed, BuildTarget.StandaloneWindows64), Is.True,
                "Variant dependencies must include a helper that controls the queried read without naming the uniform.");
            Assert.That(sources.MayRead(material, pair.First, index, "_Target", changed, BuildTarget.StandaloneWindows64), Is.False);
            material.EnableKeyword("ALTO_FORCE_TARGET");
            Assert.That(sources.MayRead(material, pair.First, index, "_Target", changed, BuildTarget.StandaloneWindows64), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void ShaderUniformAnalysisRetainsUnknownPropertyBlocksAndMaterialSwaps(bool swap)
        {
            RequireD3DAnalysis(); var pair = MergePair(); var material = UniformProofMaterial(pair.First);
            var clip = Clip();
            if (swap)
            {
                var replacement = Track(new Material(material)); replacement.SetFloat("_Gate", 1);
                AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("First", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = material }, new ObjectReferenceKeyframe { time = 1, value = replacement } });
            }
            else
            {
                var block = new MaterialPropertyBlock(); block.SetFloat("_Gate", 1); pair.First.SetPropertyBlock(block);
            }
            Assert.That(new ShaderPropertyUsage().MayRead(material, pair.First, FrameIndex(clip), "_Target", new HashSet<string>(),
                BuildTarget.StandaloneWindows64), Is.True);
        }

        [Test]
        public void ShaderUniformSourceQueryRetainsMacroPastedUniforms()
        {
            var proof = new ShaderPropertyUsage.Proof { TokenPasting = true };
            proof.Tokens.Add("_"); proof.Tokens.Add("Target");
            proof.PastePatterns.Add("_ ## name", new System.Text.RegularExpressions.Regex("^_.*$"));
            Assert.That(ShaderMeshInputs.SourceIssue(proof, ShaderInputQuery.Uniform("_Target")), Is.Not.Null);
        }

        [Test]
        public void ShaderUniformCacheKeepsCoverageAndRechecksChangedConstantsAcrossSnapshots()
        {
            RequireD3DAnalysis(); var pair = MergePair(); var material = UniformProofMaterial(pair.First);
            var first = new ShaderPropertyUsage(); var second = new ShaderPropertyUsage();
            var index = FrameIndex(); var changed = new HashSet<string> { "_Target" };
            Assert.That(first.MayRead(material, pair.First, index, "_Target", changed, BuildTarget.StandaloneWindows64), Is.False);
            long hits = ShaderAnalysisCache.Shared.Hits, misses = ShaderAnalysisCache.Shared.Misses;
            Assert.That(second.MayRead(material, pair.First, index, "_Target", changed, BuildTarget.StandaloneWindows64), Is.False);
            Assert.That(ShaderAnalysisCache.Shared.Hits, Is.GreaterThan(hits));
            Assert.That(ShaderAnalysisCache.Shared.Misses, Is.EqualTo(misses));
            Assert.That(second.MeshInputs.UnitDiagnostics, Is.EqualTo(first.MeshInputs.UnitDiagnostics));
            Assert.That(second.MeshInputs.CompileCalls, Is.EqualTo(first.MeshInputs.CompileCalls));
            Assert.That(second.MeshInputs.PreprocessCalls, Is.EqualTo(first.MeshInputs.PreprocessCalls));
            material.SetFloat("_Gate", 1);
            Assert.That(second.MayRead(material, pair.First, FrameIndex(), "_Target", changed, BuildTarget.StandaloneWindows64), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void MaterialMergeUsesConstantUniformProofsButRetainsCrossPropertyInterference(bool alsoChangesGate)
        {
            RequireD3DAnalysis(); var pair = MergePair(); UniformProofMaterial(pair.First, 1); UniformProofMaterial(pair.Second);
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, UniformBinding("First", "_Target"), AnimationCurve.Linear(0, 0, 1, 1));
            if (alsoChangesGate) AnimationUtility.SetEditorCurve(clip, UniformBinding("First", "_Gate"), AnimationCurve.Linear(0, 0, 1, 1));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            string issue = MeshMergeAnalysis.Incompatibility(candidates[0], candidates[1]);
            Assert.That(issue == null, Is.EqualTo(!alsoChangesGate), issue);
        }
    }
}
