using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.util;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.MaterialAtlas;
using Noname.AvatarTools.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private MaterialAtlasState EarlyAtlas(BuildContext context, MaterialAtlasGroup group, Transform scope = null)
        {
            var settings = root.AddComponent<ALTOMaterialAtlas>(); settings.ApplyOnPlatforms = OptimizationPlatforms.All;
            settings.ScopeRoot = scope ?? root.transform; settings.MergeGroups = new() { group };
            var state = context.GetState<MaterialAtlasState>(); state.Settings = settings;
            state.Scope = OptimizationBuildScope.Capture(settings, root);
            return state;
        }

        private void TrackEarlyAtlasAssets()
        {
            foreach (var asset in root.ReferencedAssets().Where(a => a != null && !(a is Component) && !(a is GameObject) &&
                !(a is Shader) && !(a is MonoScript) && !EditorUtility.IsPersistent(a))) Track(asset);
        }

        [TestCase(AtlasMaterialConversionTiming.BeforeGenerators)] [TestCase(AtlasMaterialConversionTiming.AfterGenerators)]
        public void MaterialConversionTimingIsExplicitAndLeavesSourceAssetsUnchanged(AtlasMaterialConversionTiming timing)
        {
            MaterialDescriptor(); Mesh(); var source = Track(new Material(Shader.Find("Standard"))); renderer.sharedMaterial = source;
            var texture = Track(new Texture2D(2, 2)); source.SetTexture("_DetailAlbedoMap", texture);
            source.SetTextureScale("_DetailAlbedoMap", new Vector2(2, 3)); source.SetTextureOffset("_DetailAlbedoMap", new Vector2(0.1f, 0.2f));
            var before = EditorJsonUtility.ToJson(source); var group = AtlasGroup(); group.TextureMappings[0].SourceProperty = "_DetailAlbedoMap";
            var context = new BuildContext(root, null); var state = EarlyAtlas(context, group); state.Settings.MaterialConversion = timing;
            using var registry = new ObjectRegistryScope(context.ObjectRegistry);
            var capture = MaterialAtlasBackend.Capture;
            try { MaterialAtlasBackend.Capture = (_, _) => { }; MaterialAtlasPass.Capture(context); }
            finally { MaterialAtlasBackend.Capture = capture; TrackEarlyAtlasAssets(); }
            bool early = timing == AtlasMaterialConversionTiming.BeforeGenerators;
            Assert.That(renderer.sharedMaterial == source, Is.EqualTo(!early));
            if (early)
            {
                Assert.That(renderer.sharedMaterial.shader, Is.EqualTo(group.ReferenceMaterial.shader));
                Assert.That(renderer.sharedMaterial.GetTexture("_MainTex"), Is.EqualTo(texture));
                Assert.That(renderer.sharedMaterial.GetTextureScale("_MainTex"), Is.EqualTo(new Vector2(2, 3)));
                Assert.That(renderer.sharedMaterial.GetTextureOffset("_MainTex"), Is.EqualTo(new Vector2(0.1f, 0.2f)));
                var copy = Track(new Material(renderer.sharedMaterial));
                Assert.That(state.Original(copy), Is.EqualTo(source), "Generator material copies retain provenance without registry participation");
                Assert.That(state.Mapping(copy, group.TextureMappings[0]).SourceProperty, Is.EqualTo("_MainTex"));
                Assert.That(group.TextureMappings[0].SourceProperty, Is.EqualTo("_DetailAlbedoMap"));
                renderer.sharedMaterial = copy;
                state.Early.RegisterCopies(root);
                Assert.That(MaterialSlotPlan.SameMaterial(copy, source), Is.True, "Source-based filters and priorities also recognize generator copies");
            }
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(before));
        }

        [TestCase(310)] [TestCase(0xA71A5)]
        public void SeededEarlyTextureMappingsMatchDeclaredReplacementOverlay(int seed)
        {
            MaterialDescriptor(); Mesh();
            var optimizer = root.AddComponent<ALTOTraceAndOptimizeMaterials>(); optimizer.BakeLilToonMainTextures = true;
            var context = new BuildContext(root, null); MaterialOptimizationPass.Capture(context);
            var group = AtlasGroup("Standard"); var state = EarlyAtlas(context, group);
            using var registry = new ObjectRegistryScope(context.ObjectRegistry);
            var random = new System.Random(seed);
            var properties = new[] { "_MainTex", "_DetailAlbedoMap", "_MetallicGlossMap", "_OcclusionMap", "_EmissionMap" };
            var backend = MaterialTextureBake.LilToon;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            const int cases = 16;
            try
            {
                for (int iteration = 0; iteration < cases; iteration++)
                {
                    var source = Track(new Material(Shader.Find("Standard"))); renderer.sharedMaterial = source;
                    var expected = new Dictionary<string, (Texture Texture, Vector2 Scale, Vector2 Offset)>(StringComparer.Ordinal);
                    foreach (string property in properties)
                    {
                        var texture = property == "_MainTex" || random.Next(3) != 0 ? Track(new Texture2D(4, 4)) : null;
                        var scale = new Vector2(random.Next(1, 9) * 0.25f, random.Next(1, 9) * 0.25f);
                        var offset = new Vector2(random.Next(-8, 9) * 0.125f, random.Next(-8, 9) * 0.125f);
                        source.SetTexture(property, texture); source.SetTextureScale(property, scale); source.SetTextureOffset(property, offset);
                        expected.Add(property, (texture, scale, offset));
                    }
                    string original = EditorJsonUtility.ToJson(source);
                    int count = random.Next(2, properties.Length + 1);
                    var inputs = new[] { "_MainTex" }.Concat(properties.Skip(1).OrderBy(_ => random.Next())).Take(count).ToArray();
                    var destinations = properties.OrderBy(_ => random.Next()).Take(count).ToArray();
                    group.TextureMappings = inputs.Select((property, i) => new AtlasTextureMapping
                    {
                        SourceProperty = property, DestinationProperty = destinations[i], Linear = random.Next(2) == 0,
                        MissingTextureColor = new Color(random.Next(256) / 255f, random.Next(256) / 255f,
                            random.Next(256) / 255f, random.Next(256) / 255f)
                    }).ToList();
                    string replaced = properties[random.Next(properties.Length)];
                    var baked = Track(new Texture2D(4, 4));
                    var bakedScale = new Vector2(0.75f, 1.25f); var bakedOffset = new Vector2(-0.5f, 0.25f);
                    // Independent oracle: input channels with one declared replacement overlaid.
                    // Incidental cleanup in a backend's renderer material is not part of this map.
                    expected[replaced] = (baked, bakedScale, bakedOffset);
                    int calls = 0;
                    MaterialTextureBake.LilToon = (input, _) =>
                    {
                        calls++;
                        var material = Track(new Material(input));
                        foreach (string property in properties)
                        {
                            material.SetTexture(property, null);
                            material.SetTextureScale(property, Vector2.zero); material.SetTextureOffset(property, Vector2.one);
                        }
                        material.SetTexture(replaced, baked); material.SetTextureScale(replaced, bakedScale); material.SetTextureOffset(replaced, bakedOffset);
                        return new MaterialTextureBakeResult
                        { Material = material, Texture = baked, ReplacedTextureProperty = replaced, Detail = "Declared texture replacement fixture." };
                    };
                    try { state.Early = EarlyMaterialConversion.Apply(context, state); }
                    finally { TrackEarlyAtlasAssets(); }
                    string description = "seed=" + seed + ", case=" + iteration + ", replaced=" + replaced;
                    Assert.That(calls, Is.EqualTo(1), description);
                    foreach (var mapping in group.TextureMappings)
                    {
                        var expectedInput = expected[mapping.SourceProperty];
                        var output = renderer.sharedMaterial;
                        if (expectedInput.Texture != null)
                            Assert.That(output.GetTexture(mapping.DestinationProperty), Is.SameAs(expectedInput.Texture), description + ": " + mapping.SourceProperty);
                        else
                        {
                            var fallback = output.GetTexture(mapping.DestinationProperty) as Texture2D;
                            Assert.That(fallback, Is.Not.Null, description + ": missing input fallback");
                            var color = fallback.GetPixel(0, 0);
                            for (int channel = 0; channel < 4; channel++)
                                Assert.That(color[channel], Is.EqualTo(mapping.MissingTextureColor[channel]).Within(1f / 255), description);
                        }
                        Assert.That(output.GetTextureScale(mapping.DestinationProperty), Is.EqualTo(expectedInput.Texture != null ? expectedInput.Scale : Vector2.one), description);
                        Assert.That(output.GetTextureOffset(mapping.DestinationProperty), Is.EqualTo(expectedInput.Texture != null ? expectedInput.Offset : Vector2.zero), description);
                    }
                    Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(original), description + ": source immutability");
                }
            }
            finally { MaterialTextureBake.LilToon = backend; }
            TestContext.WriteLine("Early texture mapping differential seed " + seed + ": " + cases + " cases, " + watch.ElapsedMilliseconds + " ms.");
        }

        [TestCase("Valid", false)] [TestCase("Retained", false)]
        [TestCase("NoDeclaration", false)] [TestCase("WhitespaceDeclaration", false)]
        [TestCase("MissingProperty", true)] [TestCase("MissingTexture", true)]
        [TestCase("WrongTexture", true)] [TestCase("UnassignedTexture", true)]
        public void TextureMappingRejectsInconsistentDeclaredReplacement(string kind, bool invalid)
        {
            var source = Track(new Material(Shader.Find("Standard")));
            var originalTexture = Track(new Texture2D(4, 4)); source.SetTexture("_MainTex", originalTexture);
            string original = EditorJsonUtility.ToJson(source);
            var replacement = Track(new Material(source)); var baked = Track(new Texture2D(4, 4));
            replacement.SetTexture("_MainTex", baked);
            var result = new MaterialTextureBakeResult
            { Material = replacement, Texture = baked, ReplacedTextureProperty = "_MainTex" };
            string requested = "_MainTex";
            if (kind == "Retained") result.Material = null;
            if (kind == "NoDeclaration") result.ReplacedTextureProperty = null;
            if (kind == "WhitespaceDeclaration") result.ReplacedTextureProperty = " ";
            if (kind == "MissingProperty") requested = result.ReplacedTextureProperty = "_MissingTexture";
            if (kind == "MissingTexture") result.Texture = null;
            if (kind == "WrongTexture") replacement.SetTexture("_MainTex", originalTexture);
            if (kind == "UnassignedTexture") replacement.SetTexture("_MainTex", null);
            Assert.That(result.TextureSource(source, "_EmissionMap"), Is.SameAs(source), "Unrelated mappings never read incidental bake state.");
            if (invalid) Assert.Throws<InvalidOperationException>(() => result.TextureSource(source, requested));
            else Assert.That(result.TextureSource(source, requested), Is.SameAs(kind == "Valid" ? replacement : source));
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(original));
        }

        [TestCase("Outside", false)] [TestCase("Inside", true)]
        [TestCase("Unresolved", false)] [TestCase("SharedDefaults", false)]
        [TestCase("ChildAnimatorInside", true)] [TestCase("SharedAnimatorRoots", false)]
        [TestCase("SwapOnlyInside", true)] [TestCase("UnknownOwner", false)]
        [TestCase("AnimationEventReader", false)] [TestCase("UnknownMaterialReader", false)]
        public void EarlyMainBakeScopeIncludesEveryMaterialSwapConsumer(string kind, bool expectedBake)
        {
            Mesh();
            var scope = PolicyChild("Bake scope"); var inside = scope.AddComponent<SkinnedMeshRenderer>();
            inside.sharedMesh = renderer.sharedMesh;
            var source = Track(new Material(Shader.Find("Standard"))); var alternative = Track(new Material(source));
            var sourceTexture = Track(new Texture2D(4, 4)); source.SetTexture("_MainTex", sourceTexture);
            alternative.SetTexture("_MainTex", Track(new Texture2D(4, 4)));
            inside.sharedMaterial = kind == "SwapOnlyInside" ? alternative : source;
            renderer.sharedMaterial = kind == "SharedDefaults" ? source : alternative;
            bool childAnimator = kind == "ChildAnimatorInside" || kind == "SharedAnimatorRoots";
            string path = childAnimator || kind == "Outside" ? "" : kind == "Unresolved" ? "Generated later" : "Bake scope";
            var clip = Clip();
            var binding = EditorCurveBinding.PPtrCurve(path, typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]");
            AnimationUtility.SetObjectReferenceCurve(clip, binding, new[] { new ObjectReferenceKeyframe { time = 0, value = source } });
            if (kind == "AnimationEventReader") AnimationUtility.SetAnimationEvents(clip,
                new[] { new AnimationEvent { functionName = "UseMaterial", objectReferenceParameter = source } });
            if (kind == "UnknownOwner") clip.legacy = true;
            var controller = kind == "UnknownOwner" ? null : Controller(clip); MaterialDescriptor(childAnimator ? null : controller);
            if (childAnimator) scope.AddComponent<Animator>().runtimeAnimatorController = controller;
            if (kind == "SharedAnimatorRoots") root.AddComponent<Animator>().runtimeAnimatorController = controller;
            const string legacyClipName = "Additional consumer";
            if (kind == "UnknownOwner")
            {
                var legacy = root.AddComponent<Animation>(); legacy.AddClip(clip, legacyClipName);
                Assert.That(legacy.GetClip(legacyClipName), Is.Not.Null, "The unknown owner must retain an actual clip consumer.");
            }
            if (kind == "UnknownMaterialReader") root.AddComponent<Projector>().material = source;
            string originalMaterial = EditorJsonUtility.ToJson(source), originalClip = EditorJsonUtility.ToJson(clip);
            var optimizer = root.AddComponent<ALTOTraceAndOptimizeMaterials>();
            optimizer.BakeLilToonMainTextures = true; optimizer.ScopeRoot = scope.transform;
            var context = new BuildContext(root, null); MaterialOptimizationPass.Capture(context);
            var state = EarlyAtlas(context, AtlasGroup("Standard"));
            using var registry = new ObjectRegistryScope(context.ObjectRegistry);
            var backend = MaterialTextureBake.LilToon;
            var baked = new Dictionary<Material, Texture2D>();
            try
            {
                MaterialTextureBake.LilToon = (input, _) =>
                {
                    var material = Track(new Material(input)); var texture = Track(new Texture2D(4, 4));
                    material.SetTexture("_MainTex", texture); baked.Add(input, texture);
                    return new MaterialTextureBakeResult
                    { Material = material, Texture = texture, ReplacedTextureProperty = "_MainTex", Detail = "Scope fixture." };
                };
                state.Early = EarlyMaterialConversion.Apply(context, state);
            }
            finally { MaterialTextureBake.LilToon = backend; TrackEarlyAtlasAssets(); }
            Assert.That(baked.ContainsKey(source), Is.EqualTo(expectedBake), kind + ": the bake scope must include defaults and every animation destination.");
            Assert.That(baked.ContainsKey(alternative), Is.False, "The alternate material has an outside-scope default consumer.");
            // A legacy Animation registers clips by its state name; that name is
            // not required to match the source asset's name after serialization.
            var outputClip = kind == "UnknownOwner" ? root.GetComponent<Animation>().GetClip(legacyClipName) :
                root.ReferencedAssets(traverseSaved: true).OfType<AnimationClip>().Single(c => c.name == clip.name);
            Assert.That(outputClip, Is.Not.Null, "Conversion must preserve the consumer's animation clip.");
            var outputKeys = AnimationUtility.GetObjectReferenceCurve(outputClip, binding);
            Assert.That(outputKeys, Has.Length.EqualTo(1), "Conversion must preserve the material swap binding.");
            var outputMaterial = outputKeys[0].value as Material;
            Assert.That(outputMaterial, Is.Not.Null, "Conversion must preserve the material swap target.");
            Assert.That(outputMaterial.GetTexture("_MainTex"), Is.SameAs(expectedBake ? baked[source] : sourceTexture),
                "Conversion may replace the shader, but must not leak the narrower bake operation through a clip.");
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(originalMaterial));
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(originalClip));
        }

        [TestCase(false)] [TestCase(true)]
        public void EarlyConversionCopiesMaterialSwapGraphsIncludingUnassignedAlternatives(bool useOverride)
        {
            Mesh(); var source = Track(new Material(Shader.Find("Standard"))); var alternative = Track(new Material(source));
            renderer.sharedMaterial = source; var clip = Clip();
            var binding = EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]");
            AnimationUtility.SetObjectReferenceCurve(clip, binding, new[] { new ObjectReferenceKeyframe { time = 0, value = source },
                new ObjectReferenceKeyframe { time = 1, value = alternative } });
            var controller = Controller(clip); MaterialDescriptor(controller);
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (useOverride) descriptor.baseAnimationLayers[0].animatorController = Track(new AnimatorOverrideController(controller));
            var clipBefore = EditorJsonUtility.ToJson(clip); var controllerBefore = EditorJsonUtility.ToJson(controller);
            var context = new BuildContext(root, null); var state = EarlyAtlas(context, AtlasGroup());
            state.Early = EarlyMaterialConversion.Apply(context, state); TrackEarlyAtlasAssets();
            var runtime = descriptor.baseAnimationLayers[0].animatorController;
            var outputController = (AnimatorController)(runtime is AnimatorOverrideController replacement ? replacement.runtimeAnimatorController : runtime);
            Assert.That(outputController, Is.Not.SameAs(controller));
            var outputClip = (AnimationClip)outputController.layers[0].stateMachine.defaultState.motion;
            var keys = AnimationUtility.GetObjectReferenceCurve(outputClip, binding);
            Assert.That(keys.Select(k => ((Material)k.value).shader), Is.All.EqualTo(state.Settings.MergeGroups[0].ReferenceMaterial.shader));
            Assert.That(keys[0].value, Is.EqualTo(renderer.sharedMaterial));
            Assert.That(state.Original((Material)keys[1].value), Is.EqualTo(alternative));
            Assert.That(keys.Select(k => k.time), Is.EqualTo(new[] { 0f, 1f }));
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(clipBefore));
            Assert.That(EditorJsonUtility.ToJson(controller), Is.EqualTo(controllerBefore));
        }

        [Test]
        public void EarlyConversionReportsDiscardedMaterialInputsInsteadOfSilentlySkippingTheAtlas()
        {
            MaterialDescriptor(); Mesh(); renderer.sharedMaterial = Track(new Material(Shader.Find("Standard")));
            var context = new BuildContext(root, null); var state = EarlyAtlas(context, AtlasGroup());
            state.Early = EarlyMaterialConversion.Apply(context, state); TrackEarlyAtlasAssets();
            renderer.sharedMaterial = null;
            Assert.Throws<InvalidOperationException>(() => state.Early.ValidateSurvivingInputs(new[] { renderer }));
            renderer.sharedMesh.subMeshCount = 0;
            Assert.DoesNotThrow(() => state.Early.ValidateSurvivingInputs(new[] { renderer }), "Explicitly removed geometry needs no material input");
        }

        [TestCase(false)] [TestCase(true)]
        public void EarlyConversionAcceptsMaterialInputsMovedToGeneratedRenderers(bool rigidOutput)
        {
            MaterialDescriptor(); Mesh(); renderer.sharedMaterial = Track(new Material(Shader.Find("Standard")));
            var context = new BuildContext(root, null); var state = EarlyAtlas(context, AtlasGroup());
            using var registry = new ObjectRegistryScope(context.ObjectRegistry);
            state.Early = EarlyMaterialConversion.Apply(context, state); TrackEarlyAtlasAssets();
            var generated = PolicyChild("Generated output");
            Renderer output;
            if (rigidOutput)
            {
                generated.AddComponent<MeshFilter>().sharedMesh = renderer.sharedMesh;
                output = generated.AddComponent<MeshRenderer>();
            }
            else
            {
                var skin = generated.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = renderer.sharedMesh; output = skin;
            }
            output.sharedMaterial = Track(new Material(renderer.sharedMaterial));
            renderer.sharedMaterials = Array.Empty<Material>();
            Assert.DoesNotThrow(() => state.Early.ValidateSurvivingInputs(new[] { renderer, output }),
                "Copied materials retain input provenance without registering a renderer replacement");
            Assert.Throws<InvalidOperationException>(() => state.Early.ValidateSurvivingInputs(new[] { renderer }),
                "Outputs outside the atlas selection cannot preserve its inputs");
            output.sharedMaterial = Track(new Material(state.Settings.MergeGroups[0].ReferenceMaterial));
            Assert.Throws<InvalidOperationException>(() => state.Early.ValidateSurvivingInputs(new[] { renderer, output }),
                "The same shader alone is not evidence that the original inputs survived");
        }

        [Test]
        public void EarlyConversionRequiresEveryTransferredInputOnGeometryOutputs()
        {
            MaterialDescriptor(); Mesh();
            renderer.sharedMaterials = new[] { Track(new Material(Shader.Find("Standard"))), Track(new Material(Shader.Find("Standard"))) };
            var context = new BuildContext(root, null); var state = EarlyAtlas(context, AtlasGroup());
            using var registry = new ObjectRegistryScope(context.ObjectRegistry);
            state.Early = EarlyMaterialConversion.Apply(context, state); TrackEarlyAtlasAssets();
            var materials = renderer.sharedMaterials;
            var first = PolicyChild("First output").AddComponent<SkinnedMeshRenderer>();
            var second = PolicyChild("Second output").AddComponent<SkinnedMeshRenderer>();
            first.sharedMesh = second.sharedMesh = renderer.sharedMesh;
            first.sharedMaterial = Track(new Material(materials[0]));
            var outputs = new[] { renderer, first, second };
            renderer.sharedMaterials = Array.Empty<Material>();
            Assert.Throws<InvalidOperationException>(() => state.Early.ValidateSurvivingInputs(outputs));
            second.sharedMaterial = Track(new Material(materials[1]));
            Assert.DoesNotThrow(() => state.Early.ValidateSurvivingInputs(outputs));
            second.sharedMesh = null;
            Assert.Throws<InvalidOperationException>(() => state.Early.ValidateSurvivingInputs(outputs),
                "A material on an output without geometry is not a surviving rendering input");
        }

        [Test]
        public void EarlyConversionRejectsSharedOutOfScopeInputsBeforeWritingRenderers()
        {
            MaterialDescriptor(); var scope = PolicyChild("Scope"); var inside = scope.AddComponent<SkinnedMeshRenderer>();
            var source = Track(new Material(Shader.Find("Standard"))); inside.sharedMaterial = renderer.sharedMaterial = source;
            var context = new BuildContext(root, null); var state = EarlyAtlas(context, AtlasGroup(), scope.transform);
            Assert.Throws<InvalidOperationException>(() => EarlyMaterialConversion.Apply(context, state));
            Assert.That(inside.sharedMaterial, Is.EqualTo(source)); Assert.That(renderer.sharedMaterial, Is.EqualTo(source));
        }

        [Test]
        public void EmptyExplicitEarlyMaterialSelectionDoesNotConvertAnything()
        {
            MaterialDescriptor(); renderer.sharedMaterial = Track(new Material(Shader.Find("Standard"))); var original = renderer.sharedMaterial;
            var group = AtlasGroup(); group.MaterialSelection = AtlasMaterialSelectionMode.SelectedMaterials;
            var context = new BuildContext(root, null); var state = EarlyAtlas(context, group);
            state.Early = EarlyMaterialConversion.Apply(context, state);
            Assert.That(renderer.sharedMaterial, Is.EqualTo(original)); Assert.That(state.Early.Find(original), Is.Null);
        }

        [TestCase(false)] [TestCase(true)]
        public void EarlyConversionStillDetectsLostPreservedShaderOutputs(bool preserve)
        {
            Mesh(); var sourceMaterial = Track(new Material(Shader.Find("Standard"))); renderer.sharedMaterial = sourceMaterial;
            var output = StaticCurve(EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Glossiness"), 1);
            var source = TreeController(("FaceInput", output)); MaterialDescriptor(source); AtlasExternalInput("FaceInput");
            var context = new BuildContext(root, null); var state = EarlyAtlas(context, AtlasGroup());
            state.Early = EarlyMaterialConversion.Apply(context, state); TrackEarlyAtlasAssets();
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var hazards = AtlasAnimationSafety.Find(root, new Dictionary<object, VirtualAnimatorController>
                { [VRCAvatarDescriptor.AnimLayerType.FX] = controller }, new HashSet<string>(preserve ? new[] { "FaceInput" } : Array.Empty<string>()),
                null, state.Settings.MergeGroups, state.Original, state.Group);
            Assert.That(hazards.ContainsKey(renderer), Is.EqualTo(preserve));
        }
    }
}
