using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.MaterialAtlas.Tests
{
    public sealed class TexturePreparationTests
    {
        private Scene scene;
        private readonly List<Object> assets = new();
        private GameObject root;
        private BuildContext context;
        private T Track<T>(T item) where T : Object { assets.Add(item); return item; }

        [SetUp]
        public void SetUp()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("Texture sampling validation requires a graphics device.");
            scene = EditorSceneManager.NewPreviewScene(); root = Track(new GameObject("Atlas test"));
            SceneManager.MoveGameObjectToScene(root, scene); root.AddComponent<VRCAvatarDescriptor>();
            context = new BuildContext(root, null);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var asset in assets.AsEnumerable().Reverse()) if (asset != null) Object.DestroyImmediate(asset);
            assets.Clear(); if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
        }

        private Material Source()
        {
            var texture = Track(new Texture2D(16, 16, TextureFormat.RGBA32, false));
            texture.SetPixels(Enumerable.Range(0, 256).Select(i => new Color(0.2f + 0.6f * (i % 16) / 15f, 0.2f, 0.4f, 1)).ToArray());
            texture.Apply();
            return Track(new Material(Shader.Find("Unlit/Texture")) { mainTexture = texture });
        }

        [TestCase(TextureWrapMode.Repeat, false)]
        [TestCase(TextureWrapMode.Clamp, false)]
        [TestCase(TextureWrapMode.Repeat, true)]
        public void TiledInputMatchesGpuSamplingAndRetainsSourceSettings(TextureWrapMode wrap, bool negative)
        {
            var source = Source(); source.mainTexture.wrapMode = wrap;
            var scale = new Vector2(negative ? -1.5f : 1.5f, 0.75f); var offset = new Vector2(0.17f, 0.09f);
            source.mainTextureScale = scale; source.mainTextureOffset = offset;
            var domain = new Rect(-1, 0, 3, 2);
            var prepared = Track(new TexturePreparation(context).Get(source, new AtlasTextureMapping(), domain, 1, 512));
            foreach (int x in new[] { 20, 60, 100 })
            {
                int y = prepared.height / 3;
                var uv = new Vector2((x + 0.5f) / prepared.width, (y + 0.5f) / prepared.height);
                var expectedUv = new Vector2((domain.x + uv.x * domain.width) * scale.x + offset.x,
                    (domain.y + uv.y * domain.height) * scale.y + offset.y);
                var expected = Sample(source.mainTexture, expectedUv); var actual = prepared.GetPixel(x, y);
                for (int c = 0; c < 4; c++) Assert.That(actual[c], Is.EqualTo(expected[c]).Within(2f / 255), "Channel " + c);
            }
            Assert.That(source.mainTextureScale, Is.EqualTo(scale));
            Assert.That(source.mainTextureOffset, Is.EqualTo(offset));
            Assert.That(source.mainTexture.wrapMode, Is.EqualTo(wrap));
        }

        [Test]
        public void IdentitySamplingReusesTheReadOnlyTexture()
        {
            var source = Source();
            Assert.That(new TexturePreparation(context).Get(source, new AtlasTextureMapping(), new Rect(0, 0, 1, 1), 1, 2048), Is.SameAs(source.mainTexture));
        }

        [Test]
        public void ConversionInputSharingIgnoresMaterialAndShaderIdentityButKeepsSamplingDifferences()
        {
            var source = Source();
            var other = Track(new Material(Shader.Find("Unlit/Transparent")) { mainTexture = source.mainTexture });
            var preparation = new TexturePreparation(context);
            var mapping = new AtlasTextureMapping(); var domain = new Rect(0, 0, 1, 1);
            var first = preparation.Get(source, mapping, domain, 1, 128);
            var second = preparation.Get(other, mapping, domain, 1, 128);
            Assert.That(new TexTransToolAtlasBackend.InputKey(0, new[] { first }), Is.EqualTo(new TexTransToolAtlasBackend.InputKey(0, new[] { second })));
            other.mainTextureOffset = new Vector2(0.25f, 0);
            var shifted = Track(preparation.Get(other, mapping, domain, 1, 128));
            Assert.That(shifted.GetPixel(2, 2), Is.Not.EqualTo(first.GetPixel(2, 2)));
            Assert.That(new TexTransToolAtlasBackend.InputKey(0, new[] { first }), Is.Not.EqualTo(new TexTransToolAtlasBackend.InputKey(0, new[] { shifted })));
            Assert.That(new TexTransToolAtlasBackend.InputKey(0, new[] { first }), Is.Not.EqualTo(new TexTransToolAtlasBackend.InputKey(1, new[] { first })));
            Assert.That(new TexTransToolAtlasBackend.InputKey(0, new[] { first, shifted }), Is.Not.EqualTo(new TexTransToolAtlasBackend.InputKey(0, new[] { shifted, first })));
        }

        [Test]
        public void MissingTexturesHaveAnExplicitFallbackIncludingAlpha()
        {
            var source = Track(new Material(Shader.Find("Unlit/Texture"))); source.mainTexture = null;
            var mapping = new AtlasTextureMapping { MissingTextureColor = new Color(0.25f, 0.5f, 0.75f, 0.4f) };
            var texture = Track(new TexturePreparation(context).Get(source, mapping, new Rect(0, 0, 1, 1), 1, 128));
            var pixel = texture.GetPixel(0, 0);
            for (int c = 0; c < 4; c++) Assert.That(pixel[c], Is.EqualTo(mapping.MissingTextureColor[c]).Within(1f / 255));
        }

        [Test]
        public void HdrInputsAreReportedBeforeTheyCanBeClamped()
        {
            var source = Source(); source.mainTexture = Track(new Texture2D(4, 4, TextureFormat.RGBAHalf, false, true));
            Assert.That(TexturePreparation.Unsupported(source, new AtlasTextureMapping()), Does.Contain("HDR"));
        }

        private static Color Sample(Texture source, Vector2 uv)
        {
            var shader = new Material(Shader.Find("Hidden/Noname/MaterialAtlasInput"));
            shader.SetVector("_AtlasSampleST", new Vector4(0, 0, uv.x, uv.y));
            var rt = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var pixels = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active; bool write = GL.sRGBWrite;
            try
            {
                GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
                Graphics.Blit(source, rt, shader); RenderTexture.active = rt;
                pixels.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); pixels.Apply(); return pixels.GetPixel(0, 0);
            }
            finally
            {
                GL.sRGBWrite = write; RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(pixels); Object.DestroyImmediate(shader);
            }
        }
    }
}
