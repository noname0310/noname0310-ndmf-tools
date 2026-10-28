using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf;
using Noname.AvatarTools.MaterialAtlas;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private MaterialAtlasGroup AtlasGroup(string shader = "Unlit/Texture") => new()
        { ReferenceMaterial = Track(new Material(Shader.Find(shader))) };

        [TestCase(RendererSelectionMode.AllRenderersInScope, false)]
        [TestCase(RendererSelectionMode.SelectedRenderers, false)]
        [TestCase(RendererSelectionMode.SelectedRenderers, true)]
        public void AtlasSelectionExplicitlyDistinguishesAllFromAnEmptyList(RendererSelectionMode mode, bool includeListed)
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var subtree = PolicyChild("Scope");
            var listed = subtree.AddComponent<SkinnedMeshRenderer>();
            var settings = subtree.AddComponent<ALTOMaterialAtlas>(); settings.ApplyOnPlatforms = OptimizationPlatforms.All;
            settings.TargetSelection = mode; settings.MergeGroups[0] = AtlasGroup();
            if (includeListed) settings.TargetRenderers.Add(listed);
            if (mode == RendererSelectionMode.AllRenderersInScope) settings.TargetRenderers.Add(null); // An inactive manual list is ignored.
            var context = new BuildContext(root, null); var capture = MaterialAtlasBackend.Capture;
            try { MaterialAtlasBackend.Capture = (_, _) => { }; MaterialAtlasPass.Capture(context); }
            finally { MaterialAtlasBackend.Capture = capture; }
            var generated = PolicyChild("Generated", subtree.transform); var lateRenderer = generated.AddComponent<MeshRenderer>(); generated.SetActive(false);
            var selected = MaterialAtlasPass.SelectRenderers(root, context.GetState<MaterialAtlasState>()).ToArray();
            Assert.That(selected, Is.EquivalentTo(mode == RendererSelectionMode.AllRenderersInScope ? new Renderer[] { listed, lateRenderer } :
                includeListed ? new Renderer[] { listed } : Array.Empty<Renderer>()));
            Assert.That(selected.Contains(renderer), Is.False, "Outside-scope renderers remain outside the selection");
        }

        [Test]
        public void AtlasExplicitGroupsWinOverFallbackAndRejectOverlappingMaterials()
        {
            var source = Track(new Material(Shader.Find("Standard")));
            var fallback = AtlasGroup(); var selected = AtlasGroup(); selected.MaterialSelection = AtlasMaterialSelectionMode.SelectedMaterials; selected.Materials.Add(source);
            var groups = new[] { fallback, selected };
            Assert.That(MaterialAtlasPass.SelectGroup(source, groups), Is.EqualTo(1));
            Assert.That(MaterialAtlasPass.SelectGroup(fallback.ReferenceMaterial, groups), Is.EqualTo(0));
            Assert.That(MaterialAtlasPass.SelectGroup(null, groups), Is.EqualTo(-1));
            var duplicate = AtlasGroup(); duplicate.MaterialSelection = AtlasMaterialSelectionMode.SelectedMaterials; duplicate.Materials.Add(source);
            Assert.Throws<InvalidOperationException>(() => MaterialAtlasPass.SelectGroup(source, new[] { selected, duplicate }));
        }

        [TestCase(AtlasMaterialSelectionMode.AllUnassignedMaterials, false)]
        [TestCase(AtlasMaterialSelectionMode.AllUnassignedMaterials, true)]
        [TestCase(AtlasMaterialSelectionMode.SelectedMaterials, false)]
        [TestCase(AtlasMaterialSelectionMode.SelectedMaterials, true)]
        public void AtlasMaterialSelectionDoesNotInferModeFromListLength(AtlasMaterialSelectionMode mode, bool populated)
        {
            var group = AtlasGroup(); group.MaterialSelection = mode;
            var selected = Track(new Material(Shader.Find("Standard")));
            var other = Track(new Material(Shader.Find("Standard")));
            if (populated) group.Materials.Add(selected);
            MaterialAtlasPass.ValidateGroups(new[] { group });
            Assert.That(MaterialAtlasPass.SelectGroup(selected, new[] { group }), Is.EqualTo(mode == AtlasMaterialSelectionMode.AllUnassignedMaterials || populated ? 0 : -1));
            Assert.That(MaterialAtlasPass.SelectGroup(other, new[] { group }), Is.EqualTo(mode == AtlasMaterialSelectionMode.AllUnassignedMaterials ? 0 : -1));
            group.Materials.Clear();
            Assert.That(MaterialAtlasPass.SelectGroup(selected, new[] { group }), Is.EqualTo(mode == AtlasMaterialSelectionMode.AllUnassignedMaterials ? 0 : -1));
        }

        [Test]
        public void EmptySelectedMaterialGroupsDoNotCompeteWithAnExplicitCatchAllGroup()
        {
            var a = AtlasGroup(); a.MaterialSelection = AtlasMaterialSelectionMode.SelectedMaterials;
            var b = AtlasGroup(); b.MaterialSelection = AtlasMaterialSelectionMode.SelectedMaterials; b.Materials = null;
            var remaining = AtlasGroup(); remaining.Materials.Add(null); // Inactive manual lists are ignored.
            var groups = new[] { a, b, remaining };
            Assert.DoesNotThrow(() => MaterialAtlasPass.ValidateGroups(groups));
            Assert.That(MaterialAtlasPass.SelectGroup(remaining.ReferenceMaterial, groups), Is.EqualTo(2));
            a.Materials.Add(null);
            Assert.Throws<InvalidOperationException>(() => MaterialAtlasPass.ValidateGroups(groups));
            a.Materials.Clear(); a.MaterialSelection = (AtlasMaterialSelectionMode)123;
            Assert.Throws<InvalidOperationException>(() => MaterialAtlasPass.ValidateGroups(groups));
        }

        [TestCase("Reference")] [TestCase("Destination")] [TestCase("DuplicateMapping")]
        [TestCase("Size")] [TestCase("Padding")] [TestCase("Fallback")]
        [TestCase("CubeDestination")] [TestCase("InvalidFallbackColor")]
        public void AtlasRejectsInvalidConversionContracts(string reason)
        {
            var group = AtlasGroup(); var groups = new List<MaterialAtlasGroup> { group };
            if (reason == "Reference") group.ReferenceMaterial = null;
            if (reason == "Destination") group.TextureMappings[0].DestinationProperty = "_MissingTexture";
            if (reason == "DuplicateMapping") group.TextureMappings.Add(new AtlasTextureMapping());
            if (reason == "Size") group.AtlasSize = 500;
            if (reason == "Padding") group.PaddingPixels = 0;
            if (reason == "Fallback") groups.Add(AtlasGroup());
            if (reason == "CubeDestination")
            {
                group.ReferenceMaterial = Track(new Material(Shader.Find("Skybox/Cubemap")));
                group.TextureMappings[0].DestinationProperty = "_Tex";
            }
            if (reason == "InvalidFallbackColor") group.TextureMappings[0].MissingTextureColor = new Color(float.NaN, 1, 1);
            Assert.Throws<InvalidOperationException>(() => MaterialAtlasPass.ValidateGroups(groups));
        }

        [Test]
        public void AtlasUvDomainIncludesRepeatedTilesAndPreservesPartialSelections()
        {
            var mesh = Mesh("Shape");
            mesh.uv = new[] { new Vector2(-0.25f, 0), new Vector2(2.25f, 0), new Vector2(0, 1.25f) };
            Assert.That(MaterialAtlasPass.ValidateMesh(mesh, new[] { 0 }, out var domain), Is.Null);
            Assert.That(domain, Is.EqualTo(Rect.MinMaxRect(-1, 0, 3, 2)));
            mesh.subMeshCount = 2; mesh.SetTriangles(new[] { 0, 1, 2 }, 1);
            Assert.That(MaterialAtlasPass.ValidateMesh(mesh, new[] { 0, -1 }, out _), Does.Contain("unselected"));
            Assert.That(mesh.GetBlendShapeName(0), Is.EqualTo("Shape"));
        }

        [TestCase("MissingUV")] [TestCase("Overlay")] [TestCase("InvalidUV")]
        public void AtlasRejectsAmbiguousMeshInputs(string reason)
        {
            var mesh = Mesh(); var groups = new[] { 0 };
            if (reason == "MissingUV") mesh.uv = Array.Empty<Vector2>();
            if (reason == "Overlay") groups = new[] { 0, 0 };
            if (reason == "InvalidUV") mesh.uv = new[] { new Vector2(float.NaN, 0), Vector2.up, Vector2.right };
            Assert.That(MaterialAtlasPass.ValidateMesh(mesh, groups, out _), Is.Not.Null);
        }

        private Dictionary<Renderer, string> AtlasHazards(UnityEditor.Animations.AnimatorController source,
            MaterialAtlasGroup group, params string[] preserved)
        {
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            return AtlasAnimationSafety.Find(root,
                new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller },
                new HashSet<string>(preserved), null, new[] { group });
        }

        [TestCase(false)] [TestCase(true)]
        public void AtlasLostShaderOutputsRespectPreservedParameterInfluence(bool preserve)
        {
            Mesh(); renderer.sharedMaterial = Track(new Material(Shader.Find("Standard")));
            var output = StaticCurve(EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Glossiness"), 1);
            var source = TreeController(("FaceInput", output));
            AtlasExternalInput("FaceInput");
            var hazards = AtlasHazards(source, AtlasGroup(), preserve ? new[] { "FaceInput" } : Array.Empty<string>());
            Assert.That(hazards.ContainsKey(renderer), Is.EqualTo(preserve));
        }

        [Test]
        public void AtlasDoesNotConfuseUnrelatedDirectBranchesWithPreservedFacialOutputs()
        {
            Mesh("Face"); renderer.sharedMaterial = Track(new Material(Shader.Find("Standard")));
            var material = StaticCurve(EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Glossiness"), 1);
            var source = TreeController(("FaceInput", Clip("Face")), ("Lighting", material));
            AtlasExternalInput("FaceInput");
            Assert.That(AtlasHazards(source, AtlasGroup(), "FaceInput").ContainsKey(renderer), Is.False);
        }

        private void AtlasExternalInput(string name)
        {
            var descriptor = root.GetComponent<VRCAvatarDescriptor>() ?? root.AddComponent<VRCAvatarDescriptor>();
            descriptor.expressionParameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            descriptor.expressionParameters.parameters = new[]
            { new VRCExpressionParameters.Parameter { name = name, valueType = VRCExpressionParameters.ValueType.Float } };
        }

        [TestCase(BlendTreeType.Simple1D, false)]
        [TestCase(BlendTreeType.FreeformCartesian2D, true)]
        public void AtlasReadsTheSerializedYAxisOnlyForTwoDimensionalTrees(BlendTreeType type, bool readsY)
        {
            Mesh("Face"); renderer.sharedMaterial = Track(new Material(Shader.Find("Standard")));
            var output = StaticCurve(EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Glossiness"), 1);
            var source = TreeController(("Lighting", output));
            var tree = (BlendTree)source.layers[0].stateMachine.defaultState.motion;
            tree.blendType = type;
            tree.blendParameter = "Lighting";
            tree.blendParameterY = "FaceInput";
            source.AddParameter("FaceInput", AnimatorControllerParameterType.Float);
            AtlasExternalInput("FaceInput");
            Assert.That(AtlasHazards(source, AtlasGroup(), "FaceInput").ContainsKey(renderer), Is.EqualTo(readsY));
        }

        [TestCase(false)] [TestCase(true)]
        public void AtlasSharedPreservedWeightProtectsOtherOutputsOnlyWhenItCanVary(bool external)
        {
            Mesh(); renderer.sharedMaterial = Track(new Material(Shader.Find("Standard")));
            var output = StaticCurve(EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Glossiness"), 1);
            var source = TreeController(("SharedWeight", output));
            source.parameters = new[] { new AnimatorControllerParameter { name = "SharedWeight", type = AnimatorControllerParameterType.Float, defaultFloat = 1 } };
            if (external) AtlasExternalInput("SharedWeight");
            Assert.That(AtlasHazards(source, AtlasGroup(), "SharedWeight").ContainsKey(renderer), Is.EqualTo(external));
        }

        [Test]
        public void AtlasChangingDriverOutputIsNotTreatedAsAnInvariantWeight()
        {
            Mesh(); renderer.sharedMaterial = Track(new Material(Shader.Find("Standard")));
            var output = StaticCurve(EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Glossiness"), 1);
            var source = TreeController(("Weight", output));
            Driver(source.layers[0].stateMachine.states[0].state, "Weight", 1);
            Assert.That(AtlasHazards(source, AtlasGroup(), "Weight").ContainsKey(renderer), Is.True);
        }

        [Test]
        public void AtlasKeepsCompatibleNumericBindingsAndRejectsSlotSwaps()
        {
            Mesh(); var material = Track(new Material(Shader.Find("Standard"))); renderer.sharedMaterial = material;
            var clip = StaticCurve(EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._Color.r"), 0.5f);
            Assert.That(AtlasHazards(Controller(clip), AtlasGroup("Standard")).ContainsKey(renderer), Is.False);
            Assert.That(AnimationUtility.GetCurveBindings(clip), Has.Length.EqualTo(1));
            var swap = Track(new AnimationClip());
            AnimationUtility.SetObjectReferenceCurve(swap,
                EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { value = material } });
            Assert.That(AtlasHazards(Controller(swap), AtlasGroup()).ContainsKey(renderer), Is.True);
        }

        [Test]
        public void AtlasRetainsAnimatedTextureCoordinatesAfterPropertyRemapping()
        {
            Mesh(); renderer.sharedMaterial = Track(new Material(Shader.Find("Standard")));
            var clip = StaticCurve(EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._MainTex_ST.x"), 0.5f);
            var group = AtlasGroup("Standard"); group.TextureMappings[0].SourceProperty = "_DetailAlbedoMap";
            Assert.That(AtlasHazards(Controller(clip), group).ContainsKey(renderer), Is.True);
        }
    }
}
