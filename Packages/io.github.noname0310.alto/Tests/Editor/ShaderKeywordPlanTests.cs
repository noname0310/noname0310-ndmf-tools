using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static ShaderKeywordPlan KeywordPlan(string source, string[] names,
            IReadOnlyDictionary<string, bool> fixedValues = null, int limit = 256)
        {
            ShaderKeywordPlan.Declarations(source, ShaderType.Vertex, out var groups, out var skipped);
            return ShaderKeywordPlan.Enumerate(names, groups, fixedValues ?? new Dictionary<string, bool>(), skipped, limit);
        }

        [TestCase(ShaderCompilerPlatform.Vulkan)]
        [TestCase(ShaderCompilerPlatform.GLES3x)]
        [TestCase(ShaderCompilerPlatform.OpenGLCore)]
        public void ShaderKeywordIgnoredStageSuffixesDoNotHideOverlappingSets(ShaderCompilerPlatform backend)
        {
            ShaderKeywordPlan.Declarations("#pragma multi_compile_vertex _ A B\n#pragma multi_compile_fragment _ A C\n",
                ShaderType.Vertex, out var groups, out var skipped, backend);
            var plan = ShaderKeywordPlan.Enumerate(new[] { "A", "B", "C" }, groups, new Dictionary<string, bool>(), skipped, 256);
            Assert.That(plan.Variants.Length, Is.EqualTo(8));
        }

        [Test]
        public void ShaderKeywordMetalVertexAndTessellationSetsShareCoverage()
        {
            ShaderKeywordPlan.Declarations("#pragma multi_compile_vertex _ A B\n#pragma multi_compile_domain _ A C\n",
                ShaderType.Vertex, out var groups, out var skipped, ShaderCompilerPlatform.Metal);
            var plan = ShaderKeywordPlan.Enumerate(new[] { "A", "B", "C" }, groups, new Dictionary<string, bool>(), skipped, 256);
            Assert.That(plan.Variants.Length, Is.EqualTo(8));
        }

        [Test]
        public void ShaderKeywordAlternativesDoNotCreateImpossibleCrossProducts()
        {
            var plan = KeywordPlan("#pragma multi_compile _ A B C\n#pragma shader_feature_local _ D E",
                new[] { "A", "B", "C", "D", "E" });
            Assert.That(plan.Issue, Is.Null);
            Assert.That(plan.Variants.Length, Is.EqualTo(12));
            Assert.That(plan.Variants.All(v => v.Intersect(new[] { "A", "B", "C" }).Count() <= 1 &&
                v.Intersect(new[] { "D", "E" }).Count() <= 1), Is.True);
            Assert.That(plan.Variants.Any(v => v.SequenceEqual(new[] { "C", "E" })), Is.True);
        }

        [Test]
        public void ShaderKeywordFixedValuesReduceChoicesWithoutDroppingUnknownInputs()
        {
            var plan = KeywordPlan("#pragma shader_feature_local _ A B\n#pragma shader_feature_local C",
                new[] { "A", "B", "C", "UNKNOWN" }, new Dictionary<string, bool> { ["A"] = false, ["B"] = true, ["C"] = false });
            Assert.That(plan.Issue, Is.Null);
            Assert.That(plan.Variants.Select(v => string.Join(",", v)), Is.EquivalentTo(new[] { "B", "B,UNKNOWN" }));
        }

        [TestCase("#pragma multi_compile _ A B\n#if FEATURE\n#pragma multi_compile _ A C\n#endif")]
        [TestCase("#pragma multi_compile _ A B\n#pragma multi_compile _ A B")]
        [TestCase("#pragma multi_compile _ A B C\n#pragma multi_compile _ A B D")]
        [TestCase("#pragma multi_compile _ A B\n#include_with_pragmas \"variants.hlsl\"")]
        [TestCase("#pragma multi_compile _ A B\n#include \"uninspected.hlsl\"")]
        public void ShaderKeywordRepeatedOrImportedDeclarationsCannotProveExclusion(string source)
        {
            var plan = KeywordPlan(source, new[] { "A", "B" });
            Assert.That(plan.Issue, Is.Null);
            Assert.That(plan.Variants.Any(v => v.SequenceEqual(new[] { "A", "B" })), Is.True);
        }

        [Test]
        public void ShaderKeywordDeclarationsSharePreprocessorLineSplicingAndComments()
        {
            var plan = KeywordPlan("#pra\\\ngma multi_compile _ A \\\n B // one set\n", new[] { "A", "B" });
            Assert.That(plan.Variants.Length, Is.EqualTo(3));
            plan = KeywordPlan("#i\\\nf FEATURE\n#pragma skip_variants A\n#end\\\nif\n", new[] { "A" });
            Assert.That(plan.Variants.Length, Is.EqualTo(2), "A spliced conditional cannot become unconditional stripping.");
            plan = KeywordPlan("// continued comment \\\n#pragma skip_variants A\n", new[] { "A" });
            Assert.That(plan.Variants.Length, Is.EqualTo(2));
        }

        [TestCase("#pragma skip_variants A\n#if BROKEN")]
        [TestCase("#pragma skip_variants A\n#include_with_pragmas \"variants.hlsl\"")]
        public void ShaderKeywordIncompleteDeclarationParsingDoesNotStripVariants(string source)
        {
            var plan = KeywordPlan(source, new[] { "A" });
            Assert.That(plan.Variants.Length, Is.EqualTo(2));
        }

        [TestCase("#if ANY\n#pragma multi_compile _ A B\n#endif")]
        [TestCase("#pragma multi_compile _ A B\n#pragma multi_compile _ B C")]
        [TestCase("#pragma multi_compile_fragment _ A B")]
        [TestCase("#pragma multi_compilelocal _ A B")]
        [TestCase("#pragma multi_compile_local_local _ A B")]
        public void ShaderKeywordAmbiguousOrOtherStageDeclarationsStayIndependent(string source)
        {
            var plan = KeywordPlan(source, new[] { "A", "B", "C" });
            Assert.That(plan.Issue, Is.Null);
            Assert.That(plan.Variants.Length, Is.EqualTo(8));
            Assert.That(plan.Variants.Any(v => v.Length == 3), Is.True);
        }

        [Test]
        public void ShaderKeywordPlanOnlyAppliesUnconditionalVariantStripping()
        {
            var plan = KeywordPlan("#pragma skip_variants A\n#if CONDITION\n#pragma skip_variants B\n#endif",
                new[] { "A", "B" });
            Assert.That(plan.Issue, Is.Null);
            Assert.That(plan.Variants.Select(v => string.Join(",", v)), Is.EquivalentTo(new[] { "", "B" }));
            plan = KeywordPlan("#pragma skip_variants A", new[] { "A" }, new Dictionary<string, bool> { ["A"] = true });
            Assert.That(plan.Issue, Is.Not.Null, "An inconsistent material cannot prove absence vacuously.");
        }

        [Test]
        public void ShaderKeywordPlanRetainsConflictingAlternativesAndBoundsIndependentInputs()
        {
            var plan = KeywordPlan("#pragma multi_compile A B", new[] { "A", "B" },
                new Dictionary<string, bool> { ["A"] = true, ["B"] = true });
            Assert.That(plan.Issue, Is.Not.Null);
            plan = KeywordPlan("", Enumerable.Range(0, 100).Select(i => "K" + i).ToArray());
            Assert.That(plan.Issue, Does.Contain("limit"));
            Assert.That(plan.Variants, Is.Null);
        }

        [Test]
        public void ShaderKeywordPlanHandlesFogProjectionAndRepeatedDeclarations()
        {
            var plan = KeywordPlan("#pragma multi_compile_fog\n#pragma multi_compile _ A B\n#pragma multi_compile _ A B",
                new[] { "FOG_LINEAR", "FOG_EXP", "FOG_EXP2", "A" });
            Assert.That(plan.Issue, Is.Null);
            Assert.That(plan.Variants.Length, Is.EqualTo(8));
        }

        [Test]
        public void ShaderMaterialKeywordProofCacheTracksKeywordAndShaderChanges()
        {
            RequireD3DAnalysis(); var material = Track(new Material(MeshInputShader("LocalKeyword")));
            renderer.sharedMaterial = material; var index = FrameIndex(); var sources = new ShaderPropertyUsage();
            Assert.That(sources.MeshInputs.Issue(material.shader, false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"),
                "Unreferenced features may be removed, but the live local index keyword remains variable.");
            foreach (bool enabled in new[] { false, true, false })
            {
                if (enabled) material.EnableKeyword("ALTO_LOCAL_INDEX"); else material.DisableKeyword("ALTO_LOCAL_INDEX");
                string issue = sources.MeshInputs.Issue(material, renderer, index, false, BuildTarget.StandaloneWindows64);
                Assert.That(issue == null, Is.EqualTo(!enabled), issue);
            }
            int calls = sources.MeshInputs.PreprocessCalls;
            Assert.That(sources.MeshInputs.Issue(material, renderer, index, false, BuildTarget.StandaloneWindows64), Is.Null);
            Assert.That(sources.MeshInputs.PreprocessCalls, Is.EqualTo(calls));
            material.shader = MeshInputShader("Keyword");
            Assert.That(sources.MeshInputs.Issue(material, renderer, index, false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"),
                "A generated or replacement shader requires a new proof.");
        }

        [TestCase(false)] [TestCase(true)]
        public void ShaderMaterialKeywordProofDoesNotFreezeGlobalKeywordsToTheEditorState(bool enabled)
        {
            RequireD3DAnalysis(); var material = Track(new Material(MeshInputShader("Keyword"))); renderer.sharedMaterial = material;
            if (enabled) material.EnableKeyword("ALTO_INDEX_INPUT"); else material.DisableKeyword("ALTO_INDEX_INPUT");
            Assert.That(new ShaderPropertyUsage().MeshInputs.Issue(material, renderer, FrameIndex(), false,
                BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
        }

        [TestCase(false)] [TestCase(true)]
        public void ShaderMaterialKeywordsDistinguishAnimatedPropertiesFromMaterialSwaps(bool swap)
        {
            RequireD3DAnalysis(); var material = Track(new Material(MeshInputShader("LocalKeyword"))); renderer.sharedMaterial = material;
            var clip = Clip(); string path = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform);
            if (swap)
                AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve(path, typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = material } });
            else
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "material._Switch"),
                    AnimationCurve.Linear(0, 0, 1, 1));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var index = AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController>
                { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, null);
            string issue = new ShaderPropertyUsage().MeshInputs.Issue(material, renderer, index, false, BuildTarget.StandaloneWindows64);
            Assert.That(issue == null, Is.EqualTo(!swap), issue);
        }
    }
}
