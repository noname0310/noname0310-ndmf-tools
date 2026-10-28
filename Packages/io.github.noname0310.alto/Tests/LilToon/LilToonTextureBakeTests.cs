using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.LilToon;
using Noname.AvatarTools.Materials;
using Noname.AvatarTools.MaterialAtlas;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Tests
{
    public class LilToonTextureBakeTests
    {
        private readonly List<Object> owned = new();
        private T Own<T>(T value) where T : Object { owned.Add(value); return value; }

        [TearDown]
        public void Cleanup()
        {
            foreach (var value in owned.AsEnumerable().Reverse()) if (value != null) Object.DestroyImmediate(value);
            owned.Clear();
        }

        private Texture2D Texture(bool mask = false)
        {
            var texture = Own(new Texture2D(64, 64, TextureFormat.RGBA32, false));
            texture.wrapMode = TextureWrapMode.Repeat; texture.filterMode = FilterMode.Bilinear;
            texture.SetPixels(Enumerable.Range(0, 4096).Select(i => mask ? new Color((i % 64 + 1) / 65f, 0, 0, 1) :
                new Color(0.1f + i % 64 / 90f, 0.15f + i / 64 / 120f, 0.3f, 1)).ToArray());
            texture.Apply(false); return texture;
        }

        private Material Source(bool layers)
        {
            var shader = Shader.Find("lilToon"); Assert.That(shader, Is.Not.Null);
            var material = Own(new Material(shader));
            material.SetTexture("_MainTex", Texture()); material.SetFloat("_AsUnlit", 1); material.SetFloat("_UseShadow", 0);
            material.SetFloat("_Cull", 0); material.SetColor("_Color", new Color(0.8f, 0.95f, 0.7f, 0.7f));
            material.SetVector("_MainTexHSVG", new Vector4(0.13f, 0.7f, 0.8f, 1.2f));
            if (layers)
            {
                material.SetFloat("_UseMain2ndTex", 1); material.SetTexture("_Main2ndTex", Texture());
                material.SetColor("_Color2nd", new Color(0.8f, 0.5f, 0.7f, 0.4f)); material.SetTexture("_Main2ndBlendMask", Texture(true));
                material.SetFloat("_UseMain3rdTex", 1); material.SetColor("_Color3rd", new Color(0.7f, 0.8f, 0.5f, 0.3f));
            }
            return material;
        }

        private MaterialTextureBakeResult Bake(Material source, params string[] animated)
        {
            var result = LilToonTextureBaker.Bake(source, new HashSet<string>(animated, StringComparer.Ordinal));
            if (result?.Material != null) Own(result.Material);
            if (result?.Texture != null) Own(result.Texture);
            return result;
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeShaderRenderingMatchesSupportedStaticBake(bool layers)
        {
            var source = Source(layers); string original = EditorJsonUtility.ToJson(source);
            var result = Bake(source); Assert.That(result?.Material, Is.Not.Null, result?.Detail);
            var before = Render(source); var after = Render(result.Material);
            float maximum = 0, sum = 0;
            for (int i = 0; i < before.Length; i++)
                for (int channel = 0; channel < 3; channel++)
                { float error = Mathf.Abs(before[i][channel] - after[i][channel]); maximum = Mathf.Max(maximum, error); sum += error; }
            TestContext.WriteLine("Native shader difference: mean=" + sum / (before.Length * 3) + ", max=" + maximum);
            TestContext.WriteLine("Center: " + before[2080] + " -> " + after[2080]);
            Assert.That(maximum, Is.LessThan(0.025f));
            Assert.That(sum / (before.Length * 3), Is.LessThan(0.006f));
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(original));
            Assert.That(result.Material.GetColor("_Color").a, Is.EqualTo(source.GetColor("_Color").a));
            Assert.That(result.Texture.mipmapCount, Is.EqualTo(1));
            if (layers) Assert.That(result.Material.GetFloat("_UseMain2ndTex"), Is.Zero);
        }

        private Material AtlasFrameSource(bool tone, int columns, int rows, int frame, float speed, System.Random random = null)
        {
            var source = Source(false);
            source.SetColor("_Color", Color.white);
            if (!tone) source.SetVector("_MainTexHSVG", new Vector4(0, 1, 1, 1));
            var atlas = Own(new Texture2D(64, 64, TextureFormat.RGBA32, false));
            atlas.wrapMode = TextureWrapMode.Repeat;
            var cells = Enumerable.Range(0, columns * rows).Select(i => random == null ?
                (i == 0 ? Color.red : Color.blue) : new Color(0.1f + (float)random.NextDouble() * 0.8f,
                    0.1f + (float)random.NextDouble() * 0.8f, 0.1f + (float)random.NextDouble() * 0.8f, 1)).ToArray();
            atlas.SetPixels(Enumerable.Range(0, 4096).Select(i => cells[i % 64 * columns / 64 +
                (rows - 1 - i / 64 * rows / 64) * columns]).ToArray());
            atlas.Apply(false);
            source.SetFloat("_UseMain2ndTex", 1);
            source.SetTexture("_Main2ndTex", atlas);
            source.SetColor("_Color2nd", Color.white);
            source.SetVector("_Main2ndTexDecalAnimation", new Vector4(columns, rows, frame, speed));
            source.SetVector("_Main2ndTexDecalSubParam", new Vector4(1, 1, 0, 1));
            return source;
        }

        private void AssertNativeRenderingEquivalent(Material source, Material output, string description)
        {
            var before = Render(source); var after = Render(output);
            var errors = before.SelectMany((color, i) => Enumerable.Range(0, 3)
                .Select(channel => Mathf.Abs(color[channel] - after[i][channel]))).ToArray();
            Assert.That(errors.Max(), Is.LessThan(0.025f), description + ": maximum native shader difference");
            Assert.That(errors.Average(), Is.LessThan(0.006f), description + ": mean native shader difference");
        }

        [TestCase(false)] [TestCase(true)]
        public void ZeroSpeedAtlasFrameSelectionSurvivesBake(bool tone)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Requires a GPU.");
            var source = AtlasFrameSource(tone, 2, 1, 1, 0);
            string original = EditorJsonUtility.ToJson(source);
            var before = Render(source);
            Assert.That(before[16 + 32 * 64].b, Is.GreaterThan(before[16 + 32 * 64].r + 0.5f),
                "The runtime shader must select the blue second frame, independently of the baker.");
            var result = Bake(source); var output = result?.Material ?? source;
            AssertNativeRenderingEquivalent(source, output, "Static second atlas frame");
            Assert.That(output.GetFloat("_UseMain2ndTex"), Is.EqualTo(1));
            Assert.That(output.GetTexture("_Main2ndTex"), Is.SameAs(source.GetTexture("_Main2ndTex")));
            Assert.That(output.GetVector("_Main2ndTexDecalAnimation"), Is.EqualTo(new Vector4(2, 1, 1, 0)));
            if (tone)
            {
                Assert.That(result.Material, Is.Not.Null, "Independent tone correction should still bake.");
                Assert.That(output.GetVector("_MainTexHSVG"), Is.EqualTo(new Vector4(0, 1, 1, 1)));
            }
            else Assert.That(result.Material, Is.Null, "Retaining the only active operation needs no generated material.");
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(original));
        }

        [TestCase(1, 30f)] [TestCase(0, 0f)]
        public void DefaultAndExplicitFirstFrameLayersStillBake(int frame, float speed)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Requires a GPU.");
            var source = AtlasFrameSource(true, 1, 1, frame, speed);
            var result = Bake(source);
            Assert.That(result?.Material, Is.Not.Null, result?.Detail);
            Assert.That(result.Material.GetFloat("_UseMain2ndTex"), Is.Zero);
            AssertNativeRenderingEquivalent(source, result.Material, "First atlas frame");
        }

        [TestCase(310)] [TestCase(0x5EED)]
        public void SeededStaticAtlasCombinationsPreserveNativeRendering(int seed)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Requires a GPU.");
            var random = new System.Random(seed);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            const int cases = 8;
            for (int iteration = 0; iteration < cases; iteration++)
            {
                bool selectedFrame = iteration % 3 == 0;
                int columns = selectedFrame ? 2 << random.Next(2) : 1;
                int rows = selectedFrame ? 1 << random.Next(2) : 1;
                int frame = selectedFrame ? random.Next(1, columns * rows) : iteration % 3 == 1 ? 0 : 1;
                float speed = iteration % 3 == 2 ? 30 : 0;
                bool tone = random.Next(2) == 0;
                var source = AtlasFrameSource(tone, columns, rows, frame, speed, random);
                source.SetColor("_Color2nd", new Color(0.8f, 0.7f, 0.9f, 0.4f + (float)random.NextDouble() * 0.5f));
                source.SetFloat("_Main2ndTexBlendMode", new[] { 0, 2, 3 }[random.Next(3)]);
                string original = EditorJsonUtility.ToJson(source);
                var result = Bake(source);
                string description = "seed=" + seed + ", case=" + iteration + ", atlas=" + columns + "x" + rows +
                    ", frame=" + frame + ", speed=" + speed + ", tone=" + tone;
                AssertNativeRenderingEquivalent(source, result?.Material ?? source, description);
                Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(original), description + ": source immutability");
            }
            TestContext.WriteLine("Static atlas differential seed " + seed + ": " + cases + " cases, " + watch.ElapsedMilliseconds + " ms.");
        }

        private Color[] Render(Material material)
        {
            var quad = Own(GameObject.CreatePrimitive(PrimitiveType.Quad)); quad.layer = 31;
            quad.transform.position = new Vector3(100, 0, 0); quad.GetComponent<Renderer>().sharedMaterial = material;
            var go = Own(new GameObject("Bake validation camera")); var camera = go.AddComponent<Camera>();
            camera.enabled = false; camera.transform.position = new Vector3(100, 0, -2); camera.orthographic = true;
            camera.orthographicSize = 0.5f; camera.aspect = 1; camera.cullingMask = 1 << 31;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            camera.renderingPath = RenderingPath.Forward; camera.allowHDR = true; camera.allowMSAA = false;
            var target = Own(new RenderTexture(64, 64, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear));
            target.Create(); camera.targetTexture = target;
            var previous = RenderTexture.active;
            try
            {
                camera.Render(); RenderTexture.active = target;
                var pixels = Own(new Texture2D(64, 64, TextureFormat.RGBAFloat, false, true));
                pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); pixels.Apply(false);
                var colors = pixels.GetPixels(); Assert.That(colors.Max(c => Mathf.Max(c.r, c.g, c.b)), Is.GreaterThan(0.1f)); return colors;
            }
            finally { RenderTexture.active = previous; Object.DestroyImmediate(go); Object.DestroyImmediate(quad); }
        }

        [TestCase("_Main2ndTex_UVMode", 1)] [TestCase("_Main2ndTexAlphaMode", 1)]
        [TestCase("_Main2ndTex_Cull", 1)] [TestCase("_Main2ndEnableLighting", 0)]
        [TestCase("_AudioLink2Main2nd", 1)] [TestCase("_Main2ndTexIsDecal", 1)]
        [TestCase("_Main2ndTexBlendMode", 1)]
        public void UnsupportedLayersRemainWhileStaticToneCorrectionCanBake(string property, float value)
        {
            var source = Source(true); source.SetFloat(property, value);
            var result = Bake(source); Assert.That(result?.Material, Is.Not.Null);
            Assert.That(result.Material.GetFloat("_UseMain2ndTex"), Is.EqualTo(1));
            Assert.That(result.Material.GetTexture("_Main2ndTex"), Is.SameAs(source.GetTexture("_Main2ndTex")));
            Assert.That(result.Material.GetVector("_MainTexHSVG"), Is.EqualTo(new Vector4(0, 1, 1, 1)));
        }

        [TestCase("_MainTex")] [TestCase("_MainTexHSVG")] [TestCase("_MainColorAdjustMask")]
        public void AnimatedMainInputsPreventBaking(string property)
        {
            Assert.That(Bake(Source(true), property).Material, Is.Null);
        }

        [TestCase("_Color")] [TestCase("_UseMain2ndTex")] [TestCase("_Main3rdTex")]
        public void AnimatedLayersDoNotPreventIndependentToneBake(string property)
        {
            var result = Bake(Source(true), property); Assert.That(result.Material, Is.Not.Null);
            Assert.That(result.Material.GetFloat("_UseMain2ndTex"), Is.EqualTo(1));
        }

        [TestCase(2)] [TestCase(3)]
        public void StaticScreenAndMultiplyLayersMatchNativeShader(int mode)
        {
            var source = Source(true); source.SetFloat("_Main2ndTexBlendMode", mode);
            source.SetFloat("_Main3rdTexBlendMode", mode);
            var result = Bake(source); Assert.That(result.Material, Is.Not.Null);
            var before = Render(source); var after = Render(result.Material);
            Assert.That(before.Select((c, i) => Mathf.Max(Mathf.Abs(c.r - after[i].r), Mathf.Abs(c.g - after[i].g), Mathf.Abs(c.b - after[i].b))).Max(), Is.LessThan(0.025f));
        }

        [TestCase("Fade")] [TestCase("Dissolve")] [TestCase("Scroll")] [TestCase("Resolution")]
        public void SpatialAndTimeDependentLayersRemain(string kind)
        {
            var source = Source(true);
            if (kind == "Fade") source.SetVector("_Main2ndDistanceFade", new Vector4(0, 1, 1, 0));
            if (kind == "Dissolve") source.SetVector("_Main2ndDissolveParams", new Vector4(1, 0, 0.5f, 0.1f));
            if (kind == "Scroll") source.SetVector("_Main2ndTex_ScrollRotate", new Vector4(1, 0, 0, 0));
            if (kind == "Resolution") source.SetTexture("_Main2ndTex", Own(new Texture2D(128, 128)));
            var result = Bake(source); Assert.That(result.Material, Is.Not.Null);
            Assert.That(result.Material.GetFloat("_UseMain2ndTex"), Is.EqualTo(1));
        }

        [TestCase("Bake")] [TestCase("Disabled")] [TestCase("Preserved")] [TestCase("OutsideScope")]
        public void EarlyAtlasConversionBakesOnlySelectedUnprotectedInputs(string mode)
        {
            var root = Own(new GameObject("Avatar")); root.AddComponent<VRCAvatarDescriptor>();
            var child = Own(new GameObject("Face")); child.transform.SetParent(root.transform);
            var renderer = child.AddComponent<SkinnedMeshRenderer>(); var source = Source(true); renderer.sharedMaterial = source;
            string before = EditorJsonUtility.ToJson(source);
            var optimizer = root.AddComponent<ALTOTraceAndOptimizeMaterials>(); optimizer.BakeLilToonMainTextures = mode != "Disabled";
            if (mode == "Preserved") optimizer.PreserveMaterials.Add(source);
            if (mode == "OutsideScope")
            {
                var scope = Own(new GameObject("Other scope")); scope.transform.SetParent(root.transform); optimizer.ScopeRoot = scope.transform;
            }
            var atlas = root.AddComponent<ALTOMaterialAtlas>();
            atlas.MergeGroups[0].ReferenceMaterial = Own(new Material(Shader.Find("Standard")));
            var context = new BuildContext(root, null); MaterialOptimizationPass.Capture(context);
            var state = new MaterialAtlasState { Settings = atlas, Scope = OptimizationBuildScope.Capture(atlas, root) };
            using var registry = new ObjectRegistryScope(context.ObjectRegistry);
            EarlyMaterialConversion.Apply(context, state);
            Own(renderer.sharedMaterial);
            var texture = renderer.sharedMaterial.GetTexture("_MainTex");
            if (texture != source.GetTexture("_MainTex")) Own(texture);
            Assert.That(texture == source.GetTexture("_MainTex"), Is.EqualTo(mode != "Bake"));
            Assert.That(renderer.sharedMaterial.shader, Is.SameAs(atlas.MergeGroups[0].ReferenceMaterial.shader));
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(before));
        }

        [TestCase("_Main2ndTex", "_EmissionMap")]
        [TestCase("_Main2ndBlendMask", "_DetailAlbedoMap")]
        [TestCase("_MainColorAdjustMask", "_OcclusionMap")]
        public void EarlyAtlasConversionRetainsExplicitInputsConsumedByMainBake(string sourceProperty, string destinationProperty)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Requires a GPU.");
            var root = Own(new GameObject("Avatar")); root.AddComponent<VRCAvatarDescriptor>();
            var renderer = root.AddComponent<SkinnedMeshRenderer>();
            var source = Source(true); source.SetTexture("_MainColorAdjustMask", Texture(true)); renderer.sharedMaterial = source;
            source.SetTextureScale(sourceProperty, new Vector2(2, 3));
            source.SetTextureOffset(sourceProperty, new Vector2(0.125f, -0.25f));
            string original = EditorJsonUtility.ToJson(source);
            var optimizer = root.AddComponent<ALTOTraceAndOptimizeMaterials>(); optimizer.BakeLilToonMainTextures = true;
            var atlas = root.AddComponent<ALTOMaterialAtlas>();
            var group = atlas.MergeGroups[0]; group.ReferenceMaterial = Own(new Material(Shader.Find("Standard")));
            group.TextureMappings.Add(new AtlasTextureMapping { SourceProperty = sourceProperty, DestinationProperty = destinationProperty });
            var context = new BuildContext(root, null); MaterialOptimizationPass.Capture(context);
            var state = new MaterialAtlasState { Settings = atlas, Scope = OptimizationBuildScope.Capture(atlas, root) };
            using var registry = new ObjectRegistryScope(context.ObjectRegistry);
            EarlyMaterialConversion.Apply(context, state);
            var converted = Own(renderer.sharedMaterial); Own(converted.GetTexture("_MainTex"));
            Assert.That(converted.GetTexture("_MainTex"), Is.Not.SameAs(source.GetTexture("_MainTex")), "The main texture must actually be baked.");
            Assert.That(converted.GetTexture(destinationProperty), Is.SameAs(source.GetTexture(sourceProperty)), "An explicit secondary mapping still selects its original input.");
            Assert.That(converted.GetTextureScale(destinationProperty), Is.EqualTo(source.GetTextureScale(sourceProperty)));
            Assert.That(converted.GetTextureOffset(destinationProperty), Is.EqualTo(source.GetTextureOffset(sourceProperty)));
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(original));
        }

        [Test]
        public void MaterialPassUsesWholeAvatarWritersIncludingMaterialSwapTargets()
        {
            var root = Own(new GameObject("Avatar")); var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            var child = Own(new GameObject("Face")); child.transform.SetParent(root.transform);
            var renderer = child.AddComponent<SkinnedMeshRenderer>(); var source = Source(true); renderer.sharedMaterial = source;
            var swapped = Source(true); var clip = Own(new AnimationClip());
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "material._MainTexHSVG.x"), AnimationCurve.Linear(0, 0, 1, 1));
            AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("Face", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = swapped } });
            var controller = Own(new AnimatorController()); controller.AddLayer("FX"); var state = controller.layers[0].stateMachine.AddState("Default"); state.motion = clip;
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = controller } };
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            var component = root.AddComponent<ALTOTraceAndOptimizeMaterials>(); component.BakeLilToonMainTextures = true;
            component.RemoveUnusedSavedProperties = component.ShareEquivalentMaterials = false;
            var context = new BuildContext(root, null); MaterialOptimizationPass.Capture(context); MaterialOptimizationPass.Apply(context);
            Assert.That(renderer.sharedMaterial, Is.SameAs(source));
            Assert.That(AnimationUtility.GetObjectReferenceCurve(clip, AnimationUtility.GetObjectReferenceCurveBindings(clip).Single()).Single().value, Is.SameAs(swapped));
        }

        [TestCase(false)] [TestCase(true)]
        public void MaterialPassBakesStaticInputsAndKeepsAtlasProvenance(bool swap)
        {
            var root = Own(new GameObject("Avatar")); var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            var child = Own(new GameObject("Face")); child.transform.SetParent(root.transform);
            var renderer = child.AddComponent<SkinnedMeshRenderer>(); var source = Source(true); renderer.sharedMaterial = source;
            var alternate = Source(true); var clip = Own(new AnimationClip());
            var binding = EditorCurveBinding.PPtrCurve("Face", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]");
            if (swap) AnimationUtility.SetObjectReferenceCurve(clip, binding, new[] { new ObjectReferenceKeyframe { time = 0, value = alternate } });
            var controller = Own(new AnimatorController()); controller.AddLayer("FX"); controller.layers[0].stateMachine.AddState("Default").motion = clip;
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = controller } };
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            var optimizer = root.AddComponent<ALTOTraceAndOptimizeMaterials>(); optimizer.BakeLilToonMainTextures = true;
            var context = new BuildContext(root, null);
            using var registry = new ObjectRegistryScope(context.ObjectRegistry);
            MaterialOptimizationPass.Capture(context); MaterialOptimizationPass.BakeBeforeAtlas(context);
            Own(renderer.sharedMaterial); Own(renderer.sharedMaterial.GetTexture("_MainTex"));
            Assert.That(renderer.sharedMaterial, Is.Not.SameAs(source));
            Assert.That(renderer.sharedMaterial.GetFloat("_UseMain2ndTex"), Is.Zero);
            Assert.That(MaterialSlotPlan.SameMaterial(renderer.sharedMaterial, source), Is.True);
            Assert.That(source.GetFloat("_UseMain2ndTex"), Is.EqualTo(1));
            if (swap)
            {
                var output = descriptor.baseAnimationLayers[0].animatorController.animationClips.Single(c => AnimationUtility.GetObjectReferenceCurveBindings(c).Length > 0);
                var material = (Material)AnimationUtility.GetObjectReferenceCurve(output, binding).Single().value;
                Own(material); Own(material.GetTexture("_MainTex"));
                Assert.That(material.GetFloat("_UseMain2ndTex"), Is.Zero);
                Assert.That(MaterialSlotPlan.SameMaterial(material, alternate), Is.True);
                Assert.That(AnimationUtility.GetObjectReferenceCurve(clip, binding).Single().value, Is.SameAs(alternate));
            }
        }
    }
}
