using System;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static Shader MeshInputShader(string suffix)
        {
            var shader = Shader.Find("Hidden/Noname/ALTO/MeshInputs" + suffix);
            Assert.That(shader, Is.Not.Null);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            return shader;
        }

        [Test]
        public void ShaderMeshProofResolvesInactiveIncludesWithoutWeakeningMaterialUsage()
        {
            var shader = MeshInputShader("Inactive"); var sources = new ShaderPropertyUsage();
            Assert.That(sources.Get(shader).Uncertain, Does.Contain("include"));
            Assert.That(sources.MeshInputs.Issue(shader, true, BuildTarget.StandaloneWindows64), Is.Null);
            Assert.That(sources.MeshInputs.PreprocessCalls, Is.Zero,
                "A complete preprocessor absence proof covers all keyword states without native enumeration.");
            int calls = sources.MeshInputs.PreprocessCalls;
            Assert.That(sources.MeshInputs.Issue(shader, true, BuildTarget.StandaloneWindows64), Is.Null);
            Assert.That(sources.MeshInputs.PreprocessCalls, Is.EqualTo(calls), "Reuse the complete proof within the source snapshot.");
            Assert.That(sources.Get(shader).MayUse("_UnusedButDeclared"), Is.True, "Material output analysis keeps its all-source contract.");
        }

        [Test]
        public void ShaderMeshProofRetainsConsumersInNondefaultKeywordVariants()
        {
            var shader = MeshInputShader("Keyword"); var sources = new ShaderPropertyUsage();
            Assert.That(sources.MeshInputs.Issue(shader, false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
            Assert.That(sources.MeshInputs.PreprocessCalls, Is.GreaterThan(1), "The default vertex variant has no index input.");
        }

        [Test]
        public void ShaderMeshProofDoesNotTreatTheEditorBackendAsTheBuildTarget()
        {
            var shader = MeshInputShader("Mobile"); var sources = new ShaderPropertyUsage();
            Assert.That(sources.MeshInputs.Issue(shader, false, BuildTarget.StandaloneWindows64), Is.Null);
            Assert.That(sources.MeshInputs.Issue(shader, false, BuildTarget.Android), Is.Not.Null);
        }

        [Test]
        public void ShaderMeshProofFindsLiveInputsWithoutEnumeratingUnrelatedKeywords()
        {
            var shader = MeshInputShader("Budget"); var sources = new ShaderPropertyUsage();
            Assert.That(sources.MeshInputs.Issue(shader, false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
            Assert.That(sources.MeshInputs.PreprocessCalls, Is.LessThan(10));
        }

        [TestCase("SV_VertexID", false)] [TestCase("sV_PrimitiveId", false)]
        [TestCase("BLENDINDICES", false)] [TestCase("BLENDWEIGHT", true)]
        [TestCase("BLENDINDICES3", false)] [TestCase("BLENDWEIGHT1", true)]
        [TestCase("SV_VertexID0", false)] [TestCase("sv_primitiveid12", false)]
        public void ShaderMeshProofKeepsInputDeclarations(string semantic, bool addSkinning)
        {
            Assert.That(ShaderMeshInputs.InspectPreprocessed(true, "struct Input { uint id : " + semantic + "; };", addSkinning), Does.Contain("may depend"));
            var proof = new ShaderPropertyUsage.Proof(); proof.Tokens.Add(semantic);
            Assert.That(ShaderMeshInputs.SourceIssue(proof, addSkinning), Does.Contain("may depend"));
        }

        [TestCase("SV_", "VertexID0", false)] [TestCase("sv_ver", "texid1234567", false)]
        [TestCase("SV_Primitive", "ID9", false)] [TestCase("BLEND", "INDICES3", false)]
        [TestCase("BLEND", "WEIGHT12", true)]
        public void ShaderMeshSourceProofRecognizesPastedIndexedSemanticFamilies(string prefix, string suffix, bool addSkinning)
        {
            var proof = new ShaderPropertyUsage.Proof(); proof.Tokens.Add(prefix); proof.Tokens.Add(suffix);
            Assert.That(ShaderMeshInputs.SourceIssue(proof, addSkinning), Is.Null, "Separate tokens do not form a semantic without pasting.");
            proof.TokenPasting = true;
            Assert.That(ShaderMeshInputs.SourceIssue(proof, addSkinning), Does.Contain("may depend"));
        }

        [TestCase("VertexIDExtra")] [TestCase("PrimitiveIdentifier")] [TestCase("COLOR0")]
        public void ShaderMeshSourceProofKeepsFastAbsenceForUnrelatedPastedNames(string suffix)
        {
            var proof = new ShaderPropertyUsage.Proof { TokenPasting = true };
            proof.Tokens.Add("SV_"); proof.Tokens.Add(suffix);
            Assert.That(ShaderMeshInputs.SourceIssue(proof, true), Is.Null);
        }

        [TestCase(false, "float4 vert() { return 0; }")]
        [TestCase(true, "")]
        [TestCase(true, "#include \"Unknown.hlsl\"")]
        public void ShaderMeshProofRejectsFailedEmptyAndIncompletePrograms(bool success, string source)
        {
            Assert.That(ShaderMeshInputs.InspectPreprocessed(success, source, false), Does.Contain("unverified"));
        }

        [Test]
        public void ShaderMeshProofIgnoresSourceLocationsAndCommentsButPreservesUnknownBackends()
        {
            Assert.That(ShaderMeshInputs.InspectPreprocessed(true,
                "#line 10 \"SV_VertexID.hlsl\"\n// SV_PrimitiveID\nfloat4 vert() { return 0; }", false), Is.Null);
            Assert.That(ShaderMeshInputs.Backends(new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12 }).Length, Is.EqualTo(1));
            Assert.That(ShaderMeshInputs.Backends(new[] { GraphicsDeviceType.Null }), Is.Null);
        }

        [Test]
        public void ShaderMeshProofIncludesUsePassAndFallbackConsumers()
        {
            var sources = new ShaderPropertyUsage();
            Assert.That(sources.MeshInputs.Issue(MeshInputShader("Fallback"), false, BuildTarget.StandaloneWindows64), Does.Contain("may depend"));
        }

        [Test]
        public void ShaderMeshProofRetainsOversizedBuiltinFallbackWithoutInvalidPassQueries()
        {
            var shader = Shader.Find("Legacy Shaders/Diffuse"); Assert.That(shader, Is.Not.Null);
            var sources = new ShaderPropertyUsage();
            Assert.That(sources.MeshInputs.Issue(shader, false, BuildTarget.StandaloneWindows64), Does.Contain("unverified"));
            Assert.That(sources.MeshInputs.PreprocessCalls, Is.LessThanOrEqualTo(1),
                "A discovery probe cannot authorize an incomplete variant universe.");
        }

        [TestCase(BuildTarget.StandaloneWindows64)] [TestCase(BuildTarget.Android)] [TestCase(BuildTarget.iOS)]
        public void ShaderIndexPermissionStartsDisabledOnEveryPlatform(BuildTarget target)
        {
            var pair = MergePair(); MaterialDescriptor(); root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            var policy = MeshPolicy(coordinates: false);
            Assert.That(policy.AllowShaderIndexChanges, Is.False);
            policy.AllowShaderIndexChanges = true;
            Assert.That(CaptureMeshPolicy(target).GetState<MeshOptimizationState>().PermissionsFor(pair.First),
                Is.EqualTo(MeshOptimizationPermissions.ShaderIndexChanges));
        }

        [TestCase(false)] [TestCase(true)]
        public void ShaderIndexMergeRequiresPermissionForEachDependentInput(bool allowBoth)
        {
            var pair = MergePair(); MaterialDescriptor(); var optimizer = root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            optimizer.RebindStaticBones = optimizer.RemoveUnusedMeshData = false;
            pair.First.sharedMaterial = pair.Second.sharedMaterial = Track(new Material(Shader.Find("Hidden/Noname/Mesh Index Consumer")));
            MeshPolicy(coordinates: false, scope: allowBoth ? root.transform : pair.First.transform).AllowShaderIndexChanges = true;
            var context = CaptureMeshPolicy(); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (skin.sharedMesh != null) Track(skin.sharedMesh);
            Assert.That(pair.Second == null, Is.EqualTo(allowBoth));
            if (allowBoth) Assert.That(context.GetState<MeshOptimizationState>().PermissionsFor(pair.First), Is.EqualTo(MeshOptimizationPermissions.ShaderIndexChanges));
        }

        [Test]
        public void ShaderIndexPermissionSkipsIncompleteProofButKeepsOtherShaderChecks()
        {
            var sources = new ShaderPropertyUsage(); var material = Track(new Material(MeshInputShader("Budget")));
            Assert.That(MeshMergeAnalysis.ShaderIssue(new[] { material }, sources, target: BuildTarget.StandaloneWindows64,
                allowShaderIndexChanges: true), Is.Null);
            Assert.That(sources.MeshInputs.PreprocessCalls, Is.Zero);
            Assert.That(MeshMergeAnalysis.ShaderIssue(new Material[] { null }, sources, allowShaderIndexChanges: true), Does.Contain("missing"));
            material.renderQueue = 3000;
            Assert.That(MeshMergeAnalysis.ShaderIssue(new[] { material }, sources, allowShaderIndexChanges: true), Does.Contain("transparent"));
        }

        [TestCase(false)] [TestCase(true)]
        public void ShaderIndexPermissionReachesRigidConversion(bool allow)
        {
            MaterialDescriptor(); var source = RigidPart(); var holder = source.gameObject;
            source.sharedMaterial = Track(new Material(Shader.Find("Hidden/Noname/Mesh Index Consumer")));
            source.lightProbeUsage = LightProbeUsage.Off; source.reflectionProbeUsage = ReflectionProbeUsage.Off;
            var optimizer = root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            optimizer.RebindStaticBones = optimizer.RemoveUnusedMeshData = false;
            MeshPolicy().AllowShaderIndexChanges = allow;
            var context = CaptureMeshPolicy(); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (skin.sharedMesh != null) Track(skin.sharedMesh);
            Assert.That(source == null, Is.EqualTo(allow));
            Assert.That(holder.GetComponent<SkinnedMeshRenderer>() != null, Is.EqualTo(allow));
        }

        [TestCase("activation")] [TestCase("sorting")] [TestCase("coordinates")]
        public void ShaderIndexPermissionKeepsOtherMergeConditions(string reason)
        {
            var pair = MergePair(); pair.First.sharedMaterial = pair.Second.sharedMaterial = Track(new Material(Shader.Find("Hidden/Noname/Mesh Index Consumer")));
            if (reason == "activation") pair.Second.gameObject.SetActive(false);
            if (reason == "sorting") pair.Second.sortingOrder = 1;
            if (reason == "coordinates") pair.Second.rootBone = PolicyChild("Different root").transform;
            var policy = new MeshMergePolicy { AllowShaderIndexChanges = true };
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, policy: policy);
            Assert.That(MeshMergeAnalysis.Group(candidates, true, policy).Count, Is.EqualTo(2));
        }

        [TestCase(false)] [TestCase(true)]
        public void TransparentRigidConversionRequiresBothPermissions(bool allowSorting)
        {
            MaterialDescriptor(); var source = RigidPart(); var holder = source.gameObject;
            source.sharedMaterial.renderQueue = 3000;
            source.lightProbeUsage = LightProbeUsage.Off; source.reflectionProbeUsage = ReflectionProbeUsage.Off;
            var optimizer = root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            optimizer.RebindStaticBones = optimizer.RemoveUnusedMeshData = false;
            MeshPolicy().AllowTransparentSortingChanges = allowSorting;
            var context = CaptureMeshPolicy(); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (skin.sharedMesh != null) Track(skin.sharedMesh);
            Assert.That(source == null, Is.EqualTo(allowSorting));
            Assert.That(holder.GetComponent<SkinnedMeshRenderer>() != null, Is.EqualTo(allowSorting));
        }

        [TestCase(BuildTarget.StandaloneWindows64)] [TestCase(BuildTarget.Android)] [TestCase(BuildTarget.iOS)]
        public void TransparentMergePermissionIsExplicitOnEveryPlatform(BuildTarget target)
        {
            var pair = MergePair(); MaterialDescriptor(); root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            var policy = MeshPolicy(coordinates: false);
            Assert.That(policy.AllowTransparentSortingChanges, Is.False);
            policy.AllowTransparentSortingChanges = true;
            var state = CaptureMeshPolicy(target).GetState<MeshOptimizationState>();
            Assert.That(state.PermissionsFor(pair.First), Is.EqualTo(MeshOptimizationPermissions.TransparentSortingChanges));
        }

        [TestCase(false)] [TestCase(true)]
        public void TransparentMergeRequiresPermissionForEveryInput(bool allowBoth)
        {
            var pair = MergePair(); MaterialDescriptor(); var optimizer = root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            optimizer.RebindStaticBones = optimizer.RemoveUnusedMeshData = false;
            pair.First.sharedMaterial.renderQueue = 3000;
            var policy = MeshPolicy(coordinates: false, scope: allowBoth ? root.transform : pair.First.transform);
            policy.AllowTransparentSortingChanges = true;
            var context = CaptureMeshPolicy(); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (skin.sharedMesh != null) Track(skin.sharedMesh);
            Assert.That(pair.Second == null, Is.EqualTo(allowBoth));
            if (allowBoth)
            {
                var permissions = context.GetState<MeshOptimizationState>().PermissionsFor(pair.First);
                Assert.That(permissions, Is.EqualTo(MeshOptimizationPermissions.TransparentSortingChanges));
                Assert.That(pair.First.sharedMesh.vertexCount, Is.EqualTo(6));
            }
        }

        [TestCase("sorting")] [TestCase("activation")] [TestCase("indices")]
        public void TransparentMergePermissionPreservesOtherCompatibilityChecks(string reason)
        {
            var pair = MergePair(); pair.First.sharedMaterial.renderQueue = 3000;
            if (reason == "sorting") pair.Second.sortingOrder = 1;
            if (reason == "activation") pair.Second.gameObject.SetActive(false);
            if (reason == "indices") pair.Second.sharedMaterial = Track(new Material(Shader.Find("Hidden/Noname/Mesh Index Consumer")));
            var policy = new MeshMergePolicy { AllowTransparentSortingChanges = true };
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, assertEligible: false, policy: policy);
            if (reason == "indices") Assert.That(candidates[1], Is.Null);
            else Assert.That(MeshMergeAnalysis.Group(candidates, true, policy).Count, Is.EqualTo(2));
        }

        [Test]
        public void TransparentMergeDoesNotGrantMaterialSlotReordering()
        {
            var pair = MergePair(); MaterialDescriptor(); var optimizer = root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            optimizer.RebindStaticBones = optimizer.RemoveUnusedMeshData = false;
            var a = pair.First.sharedMaterial; a.renderQueue = 3000; var b = Track(new Material(a));
            RepeatMaterialSlots(pair.First, new[] { a, b, a }); RepeatMaterialSlots(pair.Second, new[] { a, b, a });
            MeshPolicy(coordinates: false).AllowTransparentSortingChanges = true;
            var context = CaptureMeshPolicy(); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            Track(pair.First.sharedMesh);
            Assert.That(pair.Second == null, Is.True);
            Assert.That(pair.First.sharedMaterials, Is.EqualTo(new[] { a, b, a, b, a }));
        }
    }
}
