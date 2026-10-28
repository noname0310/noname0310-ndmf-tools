using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.util;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.MaterialAtlas;
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
