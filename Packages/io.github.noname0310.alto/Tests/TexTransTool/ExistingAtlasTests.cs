using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using net.rs64.TexTransTool.TextureAtlas;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.MaterialAtlas.Tests
{
    public sealed class ExistingAtlasTests
    {
        private Scene scene;
        private GameObject root;
        private BuildContext context;
        private MaterialAtlasState state;
        private string buildFolder, sourceFolder;
        private readonly List<Object> assets = new();
        private T Track<T>(T item) where T : Object { assets.Add(item); return item; }
        private Material Material() => Track(new Material(Shader.Find("Unlit/Texture")));
        private MeshRenderer Renderer(params Material[] materials)
        {
            var child = Track(new GameObject("Mesh")); child.transform.SetParent(root.transform, false);
            var renderer = child.AddComponent<MeshRenderer>(); renderer.sharedMaterials = materials;
            return renderer;
        }
        private AtlasTexture Atlas(params Material[] materials)
        {
            var child = Track(new GameObject("Existing atlas")); child.transform.SetParent(root.transform, false);
            var atlas = child.AddComponent<AtlasTexture>(); atlas.AtlasTargetMaterials.AddRange(materials); return atlas;
        }

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene(); root = Track(new GameObject("Atlas test"));
            SceneManager.MoveGameObjectToScene(root, scene); root.AddComponent<VRCAvatarDescriptor>();
            context = new BuildContext(root, null); state = context.GetState<MaterialAtlasState>();
            state.Settings = root.AddComponent<ALTOMaterialAtlas>(); state.Settings.MergeGroups[0].ReferenceMaterial = Material();
            state.Settings.ApplyOnPlatforms = OptimizationPlatforms.All;
            state.Scope = OptimizationBuildScope.Capture(state.Settings, root);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var asset in assets.AsEnumerable().Reverse()) if (asset != null && !EditorUtility.IsPersistent(asset)) Object.DestroyImmediate(asset);
            assets.Clear(); if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            if (!string.IsNullOrEmpty(buildFolder) && AssetDatabase.IsValidFolder(buildFolder)) AssetDatabase.DeleteAsset(buildFolder);
            if (!string.IsNullOrEmpty(sourceFolder) && AssetDatabase.IsValidFolder(sourceFolder)) AssetDatabase.DeleteAsset(sourceFolder);
            buildFolder = null;
            sourceFolder = null;
        }

        [Test]
        public void PreserveClosesOverSharedMaterialsAndOtherAtlasesBeforeEarlyConversion()
        {
            var a = Material(); var b = Material(); var c = Material(); var free = Material();
            Renderer(a, b); Renderer(b, c); Renderer(c); Renderer(free);
            var first = Atlas(a); var second = Atlas(c);
            ExistingAtlasResolution.Apply(context, state);
            Assert.That(first != null && second != null, Is.True);
            Assert.That(new[] { a, b, c }.All(state.Preserved), Is.True);
            Assert.That(state.Group(free), Is.EqualTo(0));
            state.Early = EarlyMaterialConversion.Apply(context, state);
            Assert.That(state.Early.Find(a), Is.Null);
            Assert.That(state.Early.Find(b), Is.Null);
        }

        [Test]
        public void ReplaceFullyCoveredRemovesOnlyTheAtlasComponentAndRequiresItsConsumers()
        {
            var material = Material(); var renderer = Renderer(material); var atlas = Atlas(material); var owner = atlas.gameObject;
            state.Settings.ExistingAtlases = ExistingAtlasPolicy.ReplaceFullyCovered;
            ExistingAtlasResolution.Apply(context, state);
            Assert.That(atlas == null, Is.True); Assert.That(owner != null, Is.True);
            Assert.That(state.RequiredRenderers.Contains(ObjectRegistry.GetReference(renderer)), Is.True);
            Assert.That(state.PreservedMaterials, Is.Empty);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ReplacePreservesPartialCoverageOrOutOfScopeConsumers(bool selectedMaterials)
        {
            var a = Material(); var b = Material(); var first = Renderer(a); var second = Renderer(selectedMaterials ? b : a);
            var atlas = Atlas(a, b);
            state.Settings.ExistingAtlases = ExistingAtlasPolicy.ReplaceFullyCovered;
            if (selectedMaterials)
            { state.Settings.MergeGroups[0].MaterialSelection = AtlasMaterialSelectionMode.SelectedMaterials; state.Settings.MergeGroups[0].Materials.Add(a); }
            else
            { state.Settings.TargetSelection = RendererSelectionMode.SelectedRenderers; state.Targets.Add(ObjectRegistry.GetReference(first)); }
            ExistingAtlasResolution.Apply(context, state);
            Assert.That(atlas != null, Is.True);
            Assert.That(state.Preserved(a), Is.True); Assert.That(state.RequiredRenderers, Is.Empty);
        }

        [Test]
        public void ChainDefersConversionAndLeavesTheExistingAtlasUntouched()
        {
            var material = Material(); Renderer(material); var atlas = Atlas(material);
            state.Settings.ExistingAtlases = ExistingAtlasPolicy.Chain;
            ExistingAtlasResolution.Apply(context, state);
            Assert.That(state.Deferred, Is.True); Assert.That(atlas != null, Is.True);
            Assert.That(state.Early, Is.Null); Assert.That(state.Group(material), Is.EqualTo(0));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ChainedMaterialSelectionFollowsMergedInputsInsteadOfTheReference(bool selected)
        {
            var input = Material(); var reference = Material(); Renderer(input);
            var atlas = Atlas(input); atlas.AllMaterialMergeReference = reference;
            state.Settings.ExistingAtlases = ExistingAtlasPolicy.Chain;
            state.Settings.MergeGroups[0].MaterialSelection = AtlasMaterialSelectionMode.SelectedMaterials;
            state.Settings.MergeGroups[0].Materials.Add(selected ? input : reference);
            ExistingAtlasResolution.Apply(context, state);
            Assert.That(state.Group(reference), Is.EqualTo(selected ? 0 : -1));
        }

        [Test]
        public void ChainingRejectsAnExistingMergeThatErasesMaterialSelectionBoundaries()
        {
            var selected = Material(); var unselected = Material(); Renderer(selected, unselected);
            var atlas = Atlas(selected, unselected); atlas.AllMaterialMergeReference = selected;
            state.Settings.ExistingAtlases = ExistingAtlasPolicy.Chain;
            state.Settings.MergeGroups[0].MaterialSelection = AtlasMaterialSelectionMode.SelectedMaterials;
            state.Settings.MergeGroups[0].Materials.Add(selected);
            Assert.Throws<InvalidOperationException>(() => ExistingAtlasResolution.Apply(context, state));
            Assert.That(atlas != null, Is.True);
        }

        [Test]
        public void ChainingRejectsAReferenceAliasThatWouldConvertAnUntouchedUnselectedInput()
        {
            var selected = Material(); var reference = Material(); Renderer(selected); Renderer(reference);
            var atlas = Atlas(selected); atlas.AllMaterialMergeReference = reference;
            state.Settings.ExistingAtlases = ExistingAtlasPolicy.Chain;
            state.Settings.MergeGroups[0].MaterialSelection = AtlasMaterialSelectionMode.SelectedMaterials;
            state.Settings.MergeGroups[0].Materials.Add(selected);
            Assert.Throws<InvalidOperationException>(() => ExistingAtlasResolution.Apply(context, state));
        }

        [TestCase(ExistingAtlasPolicy.Preserve)]
        [TestCase(ExistingAtlasPolicy.Chain)]
        public void UnconvertedInputsDiscardedByEarlierGeneratorsFailBeforeObjectCollection(ExistingAtlasPolicy policy)
        {
            var material = Material(); var renderer = Renderer(material); Atlas(material);
            var mesh = Track(new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } });
            renderer.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            state.Settings.ExistingAtlases = policy; ExistingAtlasResolution.Apply(context, state);
            renderer.sharedMaterial = null;
            var exception = Assert.Throws<InvalidOperationException>(() => MaterialAtlasPass.RegisterEarlyCopies(context));
            Assert.That(exception.Message, Does.Contain("materials disappeared"));
            Assert.That(renderer != null, Is.True); Assert.That(mesh.triangles.Length, Is.EqualTo(3));
        }

        [Test]
        public void ChainedPackingUsesCompletedTextureAndPreservesUnselectedSharedConsumer()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Requires a GPU.");
            var source = Material(); var texture = Track(new Texture2D(8, 8, TextureFormat.RGBA32, false));
            texture.SetPixels(Enumerable.Repeat(new Color(0.3f, 0.6f, 0.8f, 0.4f), 64).ToArray()); texture.Apply(); source.mainTexture = texture;
            var selected = Renderer(source); var untouched = Renderer(source);
            var mesh = Track(new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, uv = new[] { Vector2.zero, Vector2.right, Vector2.up }, triangles = new[] { 0, 1, 2 } });
            selected.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var group = state.Settings.MergeGroups[0]; group.AtlasSize = 64; group.PaddingPixels = 2; group.Compression = AtlasCompression.None;
            state.Plans.Add(new AtlasRendererPlan { Renderer = selected, Mesh = mesh, Materials = new[] { source }, Groups = new[] { 0 }, UvDomain = new Rect(0, 0, 1, 1) });
            ChainedAtlasPacking.Apply(context, state);
            var output = Track(selected.sharedMaterial); var atlas = Track((Texture2D)output.mainTexture); var outputMesh = Track(selected.GetComponent<MeshFilter>().sharedMesh);
            Assert.That(untouched.sharedMaterial, Is.SameAs(source)); Assert.That(source.mainTexture, Is.SameAs(texture));
            Assert.That(mesh.uv[1], Is.EqualTo(Vector2.right));
            var uv = (outputMesh.uv[0] + outputMesh.uv[1] + outputMesh.uv[2]) / 3;
            var color = atlas.GetPixelBilinear(uv.x, uv.y);
            for (int c = 0; c < 4; c++) Assert.That(color[c], Is.EqualTo(texture.GetPixel(3, 3)[c]).Within(2f / 255));
            Assert.That(outputMesh.triangles, Is.EqualTo(mesh.triangles));
        }

        [Test]
        public void ChainPackerDownscalesWithoutOverlappingPaddedRegions()
        {
            var packed = ChainedAtlasPacking.Pack(new[] { new Vector2(2048, 1024), new Vector2(1024, 2048), new Vector2(512, 512) }, 4, 128);
            Assert.That(packed.Length, Is.EqualTo(3));
            for (int i = 0; i < packed.Length; i++)
            {
                var expanded = new Rect(packed[i].x - 4, packed[i].y - 4, packed[i].width + 8, packed[i].height + 8);
                Assert.That(expanded.xMin >= 0 && expanded.yMin >= 0 && expanded.xMax <= 128 && expanded.yMax <= 128, Is.True);
                for (int j = 0; j < i; j++) Assert.That(expanded.Overlaps(new Rect(packed[j].x - 4, packed[j].y - 4, packed[j].width + 8, packed[j].height + 8)), Is.False);
            }
        }

        [TestCase(ExistingAtlasPolicy.Preserve)]
        [TestCase(ExistingAtlasPolicy.ReplaceFullyCovered)]
        [TestCase(ExistingAtlasPolicy.Chain)]
        public void ExistingAtlasPoliciesSurviveTheCompleteNdmfTextureLifecycle(ExistingAtlasPolicy policy)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Requires a GPU.");
            root.AddComponent<Animator>();
            var red = Material(); var blue = Material();
            foreach (var pair in new[] { (red, Color.red), (blue, Color.blue) })
            {
                var texture = Track(new Texture2D(16, 16, TextureFormat.RGBA32, false));
                texture.SetPixels(Enumerable.Repeat(pair.Item2, 256).ToArray()); texture.Apply(); pair.Item1.mainTexture = texture;
            }
            var renderer = Renderer(red, blue);
            var mesh = Track(new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.right * 2, Vector3.right * 3, Vector3.right * 2 + Vector3.up },
                uv = new[] { new Vector2(0.1f, 0.1f), new Vector2(0.9f, 0.1f), new Vector2(0.1f, 0.9f), new Vector2(0.1f, 0.1f), new Vector2(0.9f, 0.1f), new Vector2(0.1f, 0.9f) }
            });
            mesh.subMeshCount = 2; mesh.SetTriangles(new[] { 0, 1, 2 }, 0); mesh.SetTriangles(new[] { 3, 4, 5 }, 1);
            renderer.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var atlas = Atlas(red, blue); atlas.AllMaterialMergeReference = red;
            atlas.MergeMaterialGroups.Add(new AtlasTexture.MaterialMergeGroup { Group = new List<Material> { red, blue }, Reference = red });
            atlas.AtlasSetting.AtlasTextureSize = 128; atlas.AtlasSetting.TextureFineTuning.Clear(); atlas.AtlasSetting.IncludeDisabledRenderer = true;
            state.Settings.ExistingAtlases = policy;
            state.Settings.MergeGroups[0].AtlasSize = 128; state.Settings.MergeGroups[0].PaddingPixels = 2;
            state.Settings.MergeGroups[0].Compression = AtlasCompression.None;
            buildFolder = "Assets/ALTOAtlasLifecycle_" + Guid.NewGuid().ToString("N");
            sourceFolder = "Assets/ALTOAtlasInputs_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", sourceFolder.Substring("Assets/".Length));
            AssetDatabase.CreateAsset(red.mainTexture, sourceFolder + "/RedTexture.asset");
            AssetDatabase.CreateAsset(blue.mainTexture, sourceFolder + "/BlueTexture.asset");
            AssetDatabase.CreateAsset(red, sourceFolder + "/Red.asset"); AssetDatabase.CreateAsset(blue, sourceFolder + "/Blue.asset");
            AssetDatabase.CreateAsset(mesh, sourceFolder + "/Mesh.asset");
            using (new OverrideTemporaryDirectoryScope(buildFolder)) nadena.dev.ndmf.AvatarProcessor.ProcessAvatar(root);
            var outputMesh = Track(renderer.GetComponent<MeshFilter>().sharedMesh);
            foreach (var material in renderer.sharedMaterials.Distinct())
            {
                if (material != red && material != blue) Track(material);
                if (material.mainTexture != red.mainTexture && material.mainTexture != blue.mainTexture) Track(material.mainTexture);
            }
            Assert.That(root.GetComponent<ALTOMaterialAtlas>() == null, Is.True);
            Assert.That(outputMesh, Is.Not.SameAs(mesh));
            int triangles = 0;
            for (int slot = 0; slot < outputMesh.subMeshCount; slot++)
            {
                var indices = outputMesh.GetTriangles(slot);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    var uv = (outputMesh.uv[indices[i]] + outputMesh.uv[indices[i + 1]] + outputMesh.uv[indices[i + 2]]) / 3;
                    var x = outputMesh.vertices[indices[i]].x;
                    var sampled = SampleTexture(renderer.sharedMaterials[slot].mainTexture, uv);
                    var expected = x < 1.5f ? Color.red : Color.blue;
                    Assert.That(sampled.r, Is.EqualTo(expected.r).Within(0.02f)); Assert.That(sampled.b, Is.EqualTo(expected.b).Within(0.02f));
                    triangles++;
                }
            }
            Assert.That(triangles, Is.EqualTo(2));
            if (policy != ExistingAtlasPolicy.Preserve)
                Assert.That(renderer.sharedMaterials.Select(m => m.GetInstanceID()).Distinct().Count(), Is.EqualTo(1));
            Assert.That(mesh.vertexCount, Is.EqualTo(6));
            Assert.That(red.mainTexture.width, Is.EqualTo(16)); Assert.That(blue.mainTexture.width, Is.EqualTo(16));
        }

        private Color SampleTexture(Texture texture, Vector2 uv)
        {
            var previous = RenderTexture.active; bool previousWrite = GL.sRGBWrite;
            var sampler = Track(new Material(Shader.Find("Hidden/Noname/MaterialAtlasInput")));
            sampler.SetVector("_AtlasSampleST", new Vector4(0, 0, uv.x, uv.y));
            var target = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            try
            {
                GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
                Graphics.Blit(texture, target, sampler); RenderTexture.active = target;
                var result = Track(new Texture2D(1, 1, TextureFormat.RGBA32, false));
                result.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); result.Apply(); return result.GetPixel(0, 0);
            }
            finally { RenderTexture.active = previous; GL.sRGBWrite = previousWrite; RenderTexture.ReleaseTemporary(target); }
        }
    }
}
