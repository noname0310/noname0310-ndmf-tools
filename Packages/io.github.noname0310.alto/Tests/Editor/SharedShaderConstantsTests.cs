using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Materials;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private const string SharedShaderName = "Hidden/Noname/ALTO/SharedShaderConstants";
        private const BuildTarget SharedShaderTarget = BuildTarget.StandaloneWindows64;

        private void RequireRuntimeShaderTarget()
        {
            RequireD3DAnalysis();
            if (!SharedShaderConstantSource.SupportedTarget(SharedShaderTarget)) Assert.Ignore("A desktop D3D graphics target is required.");
        }

        private Dictionary<object, VirtualAnimatorController> SharedShaderControllers(AnimationClip clip = null)
        {
            var source = Controller(clip ?? Clip());
            return new Dictionary<object, VirtualAnimatorController>
            { [VRCAvatarDescriptor.AnimLayerType.FX] = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source) };
        }

        private int SpecializeSharedShaders(IDictionary<object, VirtualAnimatorController> entries, List<string> diagnostics,
            Func<Renderer, bool> eligible = null, Func<Material, bool> preserved = null, Func<Renderer, bool> initialization = null,
            BuildTarget target = SharedShaderTarget)
            => SharedShaderConstants.Apply(root, entries, null, target, eligible, preserved, asset => Track(asset), diagnostics,
                allowInitialChanges: initialization);

        private Material SharedShaderMaterial(Renderer target, float different = .2f)
        {
            var shader = Shader.Find(SharedShaderName); Assert.That(shader, Is.Not.Null);
            var material = Track(new Material(shader)); material.SetFloat("_Different", different);
            target.sharedMaterial = material; return material;
        }

        private Color SharedShaderPixel(Material material, int pass)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("A graphics device is required.");
            var target = RenderTexture.GetTemporary(4, 4, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active; var readback = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true);
            try
            {
                Graphics.Blit(null, target, material, pass); RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, 4, 4), 0, 0); readback.Apply(); return readback.GetPixel(1, 1);
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); Object.DestroyImmediate(readback); }
        }

        [Test]
        public void SharedShaderConstants_OneReplacementKeepsDifferentMaterialValuesAndAllPassPixels()
        {
            RequireRuntimeShaderTarget(); Mesh("Timing");
            var other = PolicyChild("Other").AddComponent<MeshRenderer>();
            var first = SharedShaderMaterial(renderer, .2f); var second = SharedShaderMaterial(other, .8f);
            string original = EditorJsonUtility.ToJson(first);
            var expected = new List<Color>();
            foreach (bool alternate in new[] { false, true })
            foreach (var material in new[] { first, second })
            {
                if (alternate) material.EnableKeyword("ALTO_TEST_SHARED_ALTERNATE"); else material.DisableKeyword("ALTO_TEST_SHARED_ALTERNATE");
                for (int pass = 0; pass < 2; pass++) expected.Add(SharedShaderPixel(material, pass));
            }
            first.DisableKeyword("ALTO_TEST_SHARED_ALTERNATE"); second.DisableKeyword("ALTO_TEST_SHARED_ALTERNATE");
            var diagnostics = new List<string>(); var entries = SharedShaderControllers();
            Assert.That(SpecializeSharedShaders(entries, diagnostics), Is.EqualTo(1), string.Join("\n", diagnostics));
            Assert.That(renderer.sharedMaterial.shader, Is.SameAs(other.sharedMaterial.shader));
            Assert.That(renderer.sharedMaterial.shader, Is.Not.SameAs(first.shader));
            Assert.That(new[] { renderer.sharedMaterial, other.sharedMaterial }.Select(m => m.shader).Distinct().Count(), Is.EqualTo(1));
            int frame = 0;
            foreach (bool alternate in new[] { false, true })
            foreach (var material in new[] { renderer.sharedMaterial, other.sharedMaterial })
            {
                if (alternate) material.EnableKeyword("ALTO_TEST_SHARED_ALTERNATE"); else material.DisableKeyword("ALTO_TEST_SHARED_ALTERNATE");
                for (int pass = 0; pass < 2; pass++)
                    Assert.That(Vector4.Distance(SharedShaderPixel(material, pass), expected[frame++]), Is.LessThan(.00001f));
            }
            var pass0 = ShaderUtil.GetShaderData(renderer.sharedMaterial.shader).GetSubshader(0).GetPass(0);
            var program = pass0.PreprocessVariant(ShaderType.Fragment, Array.Empty<string>(), ShaderCompilerPlatform.D3D,
                SharedShaderTarget, GraphicsTier.Tier1, false);
            Assert.That(program.Success, Is.True);
            Assert.That(D3DShaderInputs.CompileUncached(program.PreprocessedCode, "frag0", ShaderType.Fragment, out var uniforms, out string error), Is.True, error);
            Assert.That(uniforms.Any(u => ShaderInputQuery.Uniform("_Different").MatchesCompiled(u) && u.ReadMask != 0), Is.True);
            Assert.That(uniforms.Any(u => ShaderInputQuery.Uniform("_Shared").MatchesCompiled(u) && u.ReadMask != 0), Is.False);
            Assert.That(EditorJsonUtility.ToJson(first), Is.EqualTo(original));
            Assert.That(SpecializeSharedShaders(entries, new()), Is.Zero, "Generated shaders must not multiply on a second pass.");
        }

        [TestCase("Renderer")] [TestCase("Material")] [TestCase("PropertyBlock")]
        [TestCase("MaterialReference")] [TestCase("ShaderReference")]
        public void SharedShaderConstants_AnyUnreplaceableUserProtectsTheEntireShader(string reason)
        {
            RequireRuntimeShaderTarget();
            var other = PolicyChild("Other").AddComponent<MeshRenderer>();
            var first = SharedShaderMaterial(renderer); var second = SharedShaderMaterial(other);
            if (reason == "PropertyBlock")
            { var block = new MaterialPropertyBlock(); block.SetFloat("_Shared", 1); other.SetPropertyBlock(block); }
            if (reason == "MaterialReference" || reason == "ShaderReference")
            {
                var reference = root.AddComponent<SharedShaderReferenceFixture>();
                Assert.That(reference, Is.Not.Null, "The runtime test-fixture assembly must be available.");
                if (reason == "MaterialReference") reference.Material = second; else reference.Shader = second.shader;
            }
            Assert.That(SpecializeSharedShaders(SharedShaderControllers(), new(),
                r => reason != "Renderer" || r != other, m => reason == "Material" && m == second), Is.Zero);
            Assert.That(renderer.sharedMaterial, Is.SameAs(first)); Assert.That(other.sharedMaterial, Is.SameAs(second));
        }

        [TestCase(false)] [TestCase(true)]
        public void SharedShaderConstants_MappedInputProtectionSurvivesMaterialSettingsRemoval(bool protectedInput)
        {
            RequireRuntimeShaderTarget(); MaterialDescriptor();
            var destination = PolicyChild("Destination").AddComponent<MeshRenderer>();
            var source = PolicyChild("Source").AddComponent<MeshRenderer>();
            var material = SharedShaderMaterial(destination); source.sharedMaterial = material;
            var settings = root.AddComponent<ALTOTraceAndOptimizeMaterials>();
            settings.ScopeRoot = protectedInput ? destination.transform : root.transform;
            settings.BakeConstantShaderInputs = true;
            settings.RemoveUnusedSavedProperties = settings.ShareEquivalentMaterials = settings.BakeLilToonMainTextures = false;
            var context = new BuildContext(root, null);
            MaterialOptimizationPass.Capture(context); MaterialOptimizationPass.Prepare(context);
            var state = context.GetState<MaterialOptimizationState>();
            int sourceIndex = Array.IndexOf(settings.BuildInputRenderers, source); Assert.That(sourceIndex, Is.GreaterThanOrEqualTo(0));
            settings.BuildInputRenderers[sourceIndex] = destination;
            Object.DestroyImmediate(source);
            Assert.That(MaterialOptimizationPass.ProtectedInput(state, destination), Is.EqualTo(protectedInput));
            MaterialOptimizationPass.Apply(context);
            Assert.That(settings == null, Is.True);
            Assert.That(MaterialOptimizationPass.ProtectedInput(state, destination), Is.EqualTo(protectedInput),
                "Destroying settings must not discard source protections already remapped by a generator.");
            MaterialOptimizationPass.SpecializeFinal(context, SharedShaderControllers(), new ResolvedParameterPolicy(), null, asset => Track(asset));
            Assert.That(destination.sharedMaterial == material, Is.EqualTo(protectedInput));
            Assert.That(destination.sharedMaterial.shader == material.shader, Is.EqualTo(protectedInput));
        }

        [TestCase(false)] [TestCase(true)]
        public void SharedShaderConstants_OptionControlsFinalSpecialization(bool enabled)
        {
            RequireRuntimeShaderTarget(); MaterialDescriptor();
            var material = SharedShaderMaterial(renderer);
            var settings = root.AddComponent<ALTOTraceAndOptimizeMaterials>(); settings.BakeConstantShaderInputs = enabled;
            var context = new BuildContext(root, null);
            MaterialOptimizationPass.Capture(context); MaterialOptimizationPass.Prepare(context);
            MaterialOptimizationPass.SpecializeFinal(context, SharedShaderControllers(), new ResolvedParameterPolicy(), null, asset => Track(asset));
            Assert.That(renderer.sharedMaterial == material, Is.EqualTo(!enabled));
            Assert.That(renderer.sharedMaterial.shader == material.shader, Is.EqualTo(!enabled));
        }

        [TestCase("Material", false)] [TestCase("Material", true)]
        [TestCase("Animation", false)] [TestCase("Animation", true)]
        [TestCase("Component", false)] [TestCase("Component", true)]
        public void SharedShaderConstants_CustomRenderTextureMaterialsProtectTheEntireShader(string reference, bool initialization)
        {
            RequireRuntimeShaderTarget();
            var material = SharedShaderMaterial(renderer); var update = Track(new Material(material));
            update.SetFloat("_Shared", .9f);
            var texture = Track(new CustomRenderTexture(4, 4));
            if (initialization) texture.initializationMaterial = update; else texture.material = update;
            var clip = Clip();
            if (reference == "Material") material.SetTexture("_InputTexture", texture);
            else if (reference == "Animation")
                AnimationUtility.SetObjectReferenceCurve(clip,
                    EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "material._InputTexture"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = texture } });
            else root.AddComponent<SharedShaderReferenceFixture>().Asset = texture;
            var diagnostics = new List<string>();
            Assert.That(SpecializeSharedShaders(SharedShaderControllers(clip), diagnostics), Is.Zero,
                "The native texture material cannot be remapped with a renderer slot: " + string.Join("\n", diagnostics));
            Assert.That(renderer.sharedMaterial, Is.SameAs(material));
            Assert.That(initialization ? texture.initializationMaterial : texture.material, Is.SameAs(update));
        }

        [TestCase(false)] [TestCase(true)]
        public void SharedShaderConstants_ArbitraryClipAndControllerReferencesProtectTheEntireShader(bool controllerReference)
        {
            RequireRuntimeShaderTarget();
            var material = SharedShaderMaterial(renderer); var clip = ConstantMaterialClip(material);
            var reference = root.AddComponent<SharedShaderReferenceFixture>();
            reference.Asset = controllerReference ? Controller(clip) : (Object)clip;
            var diagnostics = new List<string>();
            Assert.That(SpecializeSharedShaders(SharedShaderControllers(), diagnostics), Is.Zero,
                "Only exact indexed controller fields are remappable: " + string.Join("\n", diagnostics));
            Assert.That(renderer.sharedMaterial, Is.SameAs(material));
        }

        [Test]
        public void SharedShaderConstants_AnimationOnlyMaterialsParticipateInIntersectionAndRelocation()
        {
            RequireRuntimeShaderTarget();
            var initial = SharedShaderMaterial(renderer, .2f); var replacement = Track(new Material(initial));
            replacement.SetFloat("_Shared", .75f); replacement.SetFloat("_Different", .8f);
            var clip = ConstantMaterialClip(initial);
            var slot = AnimationUtility.GetObjectReferenceCurveBindings(clip).Single();
            AnimationUtility.SetObjectReferenceCurve(clip, slot, new[] {
                new ObjectReferenceKeyframe { time = 0, value = initial }, new ObjectReferenceKeyframe { time = .3f, value = replacement } });
            var entries = SharedShaderControllers(clip); var diagnostics = new List<string>();
            Assert.That(SpecializeSharedShaders(entries, diagnostics), Is.EqualTo(1), string.Join("\n", diagnostics));
            var keys = entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single().GetObjectCurve(slot);
            var materials = keys.Select(k => (Material)k.value).ToArray();
            Assert.That(materials[0], Is.SameAs(renderer.sharedMaterial));
            Assert.That(materials.Select(m => m.shader).Distinct().Single(), Is.SameAs(renderer.sharedMaterial.shader));
            Assert.That(materials[1].GetFloat("_Shared"), Is.EqualTo(.75f));
            Assert.That(Vector4.Distance(SharedShaderPixel(initial, 0), SharedShaderPixel(materials[0], 0)), Is.LessThan(.00001f));
            Assert.That(Vector4.Distance(SharedShaderPixel(replacement, 0), SharedShaderPixel(materials[1], 0)), Is.LessThan(.00001f));
            Assert.That(AnimationUtility.GetObjectReferenceCurve(clip, slot)[1].value, Is.SameAs(replacement));
        }

        [Test]
        public void SharedShaderConstants_CrossShaderSwapIsRewrittenOnceWithoutRestoringEitherOriginal()
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/__ALTO_ShaderSwapTest_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory);
            try
            {
                var first = SharedShaderMaterial(renderer);
                string sourcePath = ShaderPropertyUsage.PhysicalPath(AssetDatabase.GetAssetPath(first.shader));
                string shaderPath = directory + "/Alternate.shader", headerPath = directory + "/SharedShaderConstants.cginc";
                File.WriteAllText(shaderPath, File.ReadAllText(sourcePath).Replace(SharedShaderName, "Hidden/ALTO/ShaderSwap/" + Guid.NewGuid().ToString("N")));
                File.WriteAllText(headerPath, File.ReadAllText(Path.Combine(Path.GetDirectoryName(sourcePath), "SharedShaderConstants.cginc")));
                AssetDatabase.ImportAsset(headerPath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceSynchronousImport);
                var second = Track(new Material(AssetDatabase.LoadAssetAtPath<Shader>(shaderPath))); second.SetFloat("_Shared", .75f);
                var originals = new[] { first.shader, second.shader };
                var clip = ConstantMaterialClip(first); var slot = AnimationUtility.GetObjectReferenceCurveBindings(clip).Single();
                AnimationUtility.SetObjectReferenceCurve(clip, slot, new[] {
                    new ObjectReferenceKeyframe { time = 0, value = first }, new ObjectReferenceKeyframe { time = .3f, value = second },
                    new ObjectReferenceKeyframe { time = .6f, value = first } });
                var entries = SharedShaderControllers(clip); var diagnostics = new List<string>();
                Assert.That(SpecializeSharedShaders(entries, diagnostics), Is.EqualTo(2), string.Join("\n", diagnostics));
                var keys = entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single().GetObjectCurve(slot);
                var materials = keys.Select(k => (Material)k.value).ToArray();
                Assert.That(materials[0], Is.SameAs(renderer.sharedMaterial)); Assert.That(materials[2], Is.SameAs(materials[0]));
                Assert.That(materials.Select(m => m.shader).Distinct().Count(), Is.EqualTo(2));
                Assert.That(materials.Any(m => originals.Contains(m.shader)), Is.False);
                Assert.That(Vector4.Distance(SharedShaderPixel(first, 0), SharedShaderPixel(materials[0], 0)), Is.LessThan(.00001f));
                Assert.That(Vector4.Distance(SharedShaderPixel(second, 0), SharedShaderPixel(materials[1], 0)), Is.LessThan(.00001f));
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }

        [TestCase(false)] [TestCase(true)]
        public void SharedShaderConstants_AnimatedConstantsConsumeExplicitInitializationPolicy(bool allowInitialization)
        {
            RequireRuntimeShaderTarget();
            var material = SharedShaderMaterial(renderer);
            var clip = Clip(); var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Animated");
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, .3f, .6f));
            var entries = SharedShaderControllers(clip); var diagnostics = new List<string>();
            Assert.That(SpecializeSharedShaders(entries, diagnostics, initialization: _ => allowInitialization), Is.EqualTo(1), string.Join("\n", diagnostics));
            var pass = ShaderUtil.GetShaderData(renderer.sharedMaterial.shader).GetSubshader(0).GetPass(0);
            var program = pass.PreprocessVariant(ShaderType.Fragment, Array.Empty<string>(), ShaderCompilerPlatform.D3D,
                SharedShaderTarget, GraphicsTier.Tier1, false);
            Assert.That(D3DShaderInputs.CompileUncached(program.PreprocessedCode, "frag0", ShaderType.Fragment, out var uniforms, out string error), Is.True, error);
            Assert.That(uniforms.Any(u => ShaderInputQuery.Uniform("_Animated").MatchesCompiled(u) && u.ReadMask != 0), Is.EqualTo(!allowInitialization));
            Assert.That(material.GetFloat("_Animated"), Is.EqualTo(.1f));
            Assert.That(entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurve(binding), Is.Not.Null);
        }

        [TestCase(false, false)] [TestCase(false, true)]
        [TestCase(true, false)] [TestCase(true, true)]
        public void SharedShaderConstants_SignedZeroRetainsNativeShaderBits(bool negative, bool animated)
        {
            RequireRuntimeShaderTarget();
            var material = SharedShaderMaterial(renderer); float value = SignedZero(negative);
            var expected = Track(new Material(material)); expected.SetFloat("_Shared", value);
            var clip = Clip();
            if (animated) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Shared"), AnimationCurve.Constant(0, .3f, value));
            else material.SetFloat("_Shared", value);
            int before = BitConverter.SingleToInt32Bits(SharedShaderPixel(expected, 0).r);
            float sign = SharedShaderPixel(expected, 1).a;
            Assert.That(before, Is.EqualTo(BitConverter.SingleToInt32Bits(value)), "The native uniform preserves its IEEE-754 bits.");
            Assert.That(sign, Is.EqualTo(negative ? 1 : 0), "The source shader can observe the sign with asuint.");
            var diagnostics = new List<string>();
            Assert.That(SpecializeSharedShaders(SharedShaderControllers(clip), diagnostics, initialization: _ => true),
                Is.EqualTo(1), string.Join("\n", diagnostics));
            var pass = ShaderUtil.GetShaderData(renderer.sharedMaterial.shader).GetSubshader(0).GetPass(0);
            var program = pass.PreprocessVariant(ShaderType.Fragment, Array.Empty<string>(), ShaderCompilerPlatform.D3D,
                SharedShaderTarget, GraphicsTier.Tier1, false);
            Assert.That(D3DShaderInputs.CompileUncached(program.PreprocessedCode, "frag0", ShaderType.Fragment, out var uniforms, out string issue), Is.True, issue);
            Assert.That(uniforms.Any(u => ShaderInputQuery.Uniform("_Shared").MatchesCompiled(u) && u.ReadMask != 0), Is.EqualTo(negative),
                "Native constant folding cannot preserve negative zero, so that input remains a runtime uniform.");
            // Blit has no Renderer property block. Supply the same native value
            // that the retained animation writes when exercising that draw path.
            if (animated) renderer.sharedMaterial.SetFloat("_Shared", value);
            Assert.That(BitConverter.SingleToInt32Bits(SharedShaderPixel(renderer.sharedMaterial, 0).r), Is.EqualTo(before));
            Assert.That(SharedShaderPixel(renderer.sharedMaterial, 1).a, Is.EqualTo(sign));
        }

        [TestCase(BuildTarget.Android)] [TestCase(BuildTarget.iOS)]
        public void SharedShaderConstants_RespectMobileShaderAllowlists(BuildTarget target)
        {
            var material = SharedShaderMaterial(renderer);
            Assert.That(SpecializeSharedShaders(SharedShaderControllers(), new(), target: target), Is.Zero);
            Assert.That(renderer.sharedMaterial, Is.SameAs(material));
        }

        [Test]
        public void SharedShaderConstants_CachedSourceAvoidsNativeValidationAndRetainsUnsupportedDeclarations()
        {
            RequireRuntimeShaderTarget();
            var shader = Shader.Find(SharedShaderName); var constants = new Dictionary<string, float>
            { ["_Shared"] = .25f, ["_Included"] = .5f, ["_UnsupportedArray"] = 0, ["_UnsupportedBuffer"] = 0, ["_Writable"] = 0 };
            var domains = new Dictionary<string, float[]> { ["_Different"] = new[] { .2f }, ["_Animated"] = new[] { .1f } };
            bool enabled = ContentFactCache.Enabled;
            try
            {
                ContentFactCache.Enabled = false;
                var cold = new SharedShaderConstantSource.Statistics();
                Assert.That(SharedShaderConstantSource.TryCreate(shader, constants, SharedShaderTarget, new ShaderPropertyUsage(),
                    out var first, out string expected, out _, out string reason, cold, domains), Is.True, reason); Track(first);
                Assert.That(cold.ValidatedPrograms, Is.GreaterThan(0)); Assert.That(cold.CacheHits, Is.Zero);
                ContentFactCache.Enabled = true;
                Assert.That(SharedShaderConstantSource.TryCreate(shader, constants, SharedShaderTarget, new ShaderPropertyUsage(),
                    out var populate, out _, out _, out reason, uniformDomains: domains), Is.True, reason); Track(populate);
                var warm = new SharedShaderConstantSource.Statistics();
                Assert.That(SharedShaderConstantSource.TryCreate(shader, constants, SharedShaderTarget, new ShaderPropertyUsage(),
                    out var second, out string actual, out _, out reason, warm, domains), Is.True, reason); Track(second);
                Assert.That(actual, Is.EqualTo(expected)); Assert.That(warm.CacheHits, Is.EqualTo(1)); Assert.That(warm.ValidatedPrograms, Is.Zero);
                Assert.That(warm.NativeCompiles, Is.Zero); Assert.That(warm.RuntimeArtifactCompiles, Is.Zero);
                Assert.That(actual, Does.Contain("float _UnsupportedArray[2]").And.Contain("float _UnsupportedBuffer;").And.Contain("float _Writable;"));
                TestContext.WriteLine("Cold validation: " + cold.ValidatedPrograms + " programs, " + cold.ValidationMilliseconds +
                    " ms. Warm key: " + warm.KeyMilliseconds + " ms; lookup: " + warm.LookupMilliseconds + " ms; validated programs: " + warm.ValidatedPrograms);
            }
            finally { ContentFactCache.Enabled = enabled; }
        }

        [Test]
        public void SharedShaderConstants_IncludeEditsInvalidateValidatedSource()
        {
            RequireRuntimeShaderTarget();
            string directory = "Assets/__ALTO_SharedShaderTest_" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory);
            string header = directory + "/Constant.cginc", shaderPath = directory + "/Constant.shader";
            try
            {
                File.WriteAllText(header, "float _Shared; float Value() { return _Shared; }\n");
                File.WriteAllText(shaderPath, "Shader \"Hidden/ALTO/CacheFixture/" + Guid.NewGuid().ToString("N") + "\" { " +
                    "Properties { _Shared (\"Shared\", Float) = 0.25 } SubShader { Pass { CGPROGRAM\n" +
                    "#pragma vertex vert\n#pragma fragment frag\n#include \"UnityCG.cginc\"\n#include \"Constant.cginc\"\n" +
                    "float4 vert(float4 v:POSITION):SV_POSITION{return UnityObjectToClipPos(v);}\n" +
                    "float4 frag():SV_Target{return Value();}\nENDCG\n} } Fallback Off }");
                AssetDatabase.ImportAsset(header, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceSynchronousImport);
                var constants = new Dictionary<string, float> { ["_Shared"] = .25f };
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
                Assert.That(SharedShaderConstantSource.TryCreate(shader, constants, SharedShaderTarget, new ShaderPropertyUsage(),
                    out var first, out string previous, out _, out string reason), Is.True, reason); Track(first);
                File.WriteAllText(header, "float _Shared; float Value() { return _Shared * 2; }\n");
                AssetDatabase.ImportAsset(header, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var statistics = new SharedShaderConstantSource.Statistics();
                shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
                Assert.That(SharedShaderConstantSource.TryCreate(shader, constants, SharedShaderTarget, new ShaderPropertyUsage(),
                    out var second, out string current, out _, out reason, statistics), Is.True, reason); Track(second);
                Assert.That(current, Is.Not.EqualTo(previous)); Assert.That(statistics.CacheHits, Is.Zero);
                Assert.That(statistics.ValidatedPrograms, Is.GreaterThan(0));
            }
            finally { AssetDatabase.DeleteAsset(directory); }
        }
    }
}
