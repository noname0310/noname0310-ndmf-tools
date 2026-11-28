using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.MaterialAtlas;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("ExternalOptimizers")] [TestCase("SdkPreparation")] [TestCase("SdkFinal")]
        public void LateRendererOverridesReuseAnchorAndAllowCompatibleMerging(string boundary)
        {
            var pair = MergePair(); MaterialDescriptor();
            var settings = AnchorSettings(); settings.OverrideShadowCasting = true;
            var context = new BuildContext(root, null);
            MeshRendererSettingsOverridePass.Capture(context); MeshRendererSettingsOverridePass.Apply(context);
            var anchor = pair.First.probeAnchor;
            Assert.That(settings == null, Is.True);
            var late = PolicyChild("Late renderer").AddComponent<SkinnedMeshRenderer>();
            UnityEditor.EditorUtility.CopySerialized(pair.Second, late);
            late.probeAnchor = null; late.shadowCastingMode = ShadowCastingMode.Off;
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            if (boundary == "ExternalOptimizers") OptimizationPipeline.AfterExternalOptimizers(context);
            if (boundary == "SdkPreparation") AvatarOutputSdkFinalizer.PrepareTransport(root, o => Track(o));
            if (boundary == "SdkFinal") AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            Assert.That(late.probeAnchor, Is.SameAs(anchor));
            Assert.That(late.shadowCastingMode, Is.EqualTo(pair.First.shadowCastingMode));
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, late }), true).Count, Is.EqualTo(1));
        }

        [Test]
        public void LateRendererOverridesDoNotOverwritePreviouslyBakedInitialSettings()
        {
            MaterialDescriptor();
            var settings = root.AddComponent<ALTOMeshRendererSettingsOverride>(); settings.ApplyOnPlatforms = OptimizationPlatforms.All;
            settings.OverrideReceiveShadows = true; settings.ReceiveShadows = true;
            var context = new BuildContext(root, null);
            MeshRendererSettingsOverridePass.Capture(context); MeshRendererSettingsOverridePass.Apply(context);
            renderer.receiveShadows = false; // A later constant animation bake.
            var late = PolicyChild("Late").AddComponent<SkinnedMeshRenderer>(); late.receiveShadows = false;
            MeshRendererSettingsOverridePass.Apply(context);
            Assert.That(renderer.receiveShadows, Is.False);
            Assert.That(late.receiveShadows, Is.True);
            Assert.That(MeshRendererSettingsOverridePass.HasPendingOutputs(context), Is.False);
        }

        [Test]
        public void LateRendererOverridesRetainPreservedReplacementAndOutOfScopeOrigins()
        {
            MaterialDescriptor();
            var scope = PolicyChild("Scope");
            var kept = PolicyChild("Kept", scope.transform).AddComponent<SkinnedMeshRenderer>();
            var outside = PolicyChild("Outside").AddComponent<SkinnedMeshRenderer>();
            var settings = scope.AddComponent<ALTOMeshRendererSettingsOverride>(); settings.ScopeRoot = scope.transform;
            settings.ApplyOnPlatforms = OptimizationPlatforms.All; settings.OverrideReceiveShadows = true; settings.ReceiveShadows = false;
            settings.PreserveRenderers.Add(kept);
            using var registry = new ObjectRegistryScope(new ObjectRegistry(root.transform));
            var context = new BuildContext(root, null);
            MeshRendererSettingsOverridePass.Capture(context); MeshRendererSettingsOverridePass.Apply(context);
            var replacement = PolicyChild("Replacement", scope.transform).AddComponent<SkinnedMeshRenderer>();
            var relocated = PolicyChild("Relocated", scope.transform).AddComponent<SkinnedMeshRenderer>();
            replacement.receiveShadows = relocated.receiveShadows = true;
            ObjectRegistry.RegisterReplacedObject(kept, replacement); ObjectRegistry.RegisterReplacedObject(outside, relocated);
            UnityEngine.Object.DestroyImmediate(kept); UnityEngine.Object.DestroyImmediate(outside);
            MeshRendererSettingsOverridePass.Apply(context);
            Assert.That(replacement.receiveShadows && relocated.receiveShadows, Is.True);
        }

        private ALTORenderingFilter LateRenderingFilter()
        {
            if (root.GetComponent<VRCAvatarDescriptor>() == null) root.AddComponent<VRCAvatarDescriptor>();
            var filter = root.AddComponent<ALTORenderingFilter>(); filter.ApplyOnPlatforms = OptimizationPlatforms.All;
            filter.RemoveAdditionalMaterialPasses = false; filter.ExcludedRendererTypes = RendererFilterTypes.None;
            return filter;
        }

        [TestCase(false)] [TestCase(true)]
        public void AuthoredSlotExclusionFollowsRegisteredSourceCopiesWithoutReplayingRelocatedIndices(bool replacement)
        {
            MaterialFixture(2, 3); var source = renderer.sharedMesh; var materials = renderer.sharedMaterials;
            var filter = LateRenderingFilter();
            filter.MaterialExclusions.Add(new MaterialExclusion { Renderer = renderer, Slot = 0, Mode = MaterialRemovalMode.RemoveSubMeshGeometry });
            using var registry = new ObjectRegistryScope(new ObjectRegistry(root.transform));
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context); MaterialFilterPass.Apply(context);
            Track(renderer.sharedMesh);
            GeneratedOutputFilterPass.Apply(context);
            Assert.That(renderer.sharedMaterials, Is.EqualTo(materials.Skip(1)), "The relocated slot zero must survive.");
            var target = renderer;
            if (replacement)
            {
                target = PolicyChild("Registered copy").AddComponent<SkinnedMeshRenderer>();
                ObjectRegistry.RegisterReplacedObject(renderer, target);
            }
            target.sharedMesh = source; target.sharedMaterials = materials;
            GeneratedOutputFilterPass.Apply(context); Track(target.sharedMesh);
            Assert.That(target.sharedMesh.subMeshCount, Is.EqualTo(1));
            Assert.That(target.sharedMaterials, Is.EqualTo(materials.Skip(1)));
            Assert.That(source.subMeshCount, Is.EqualTo(2));
        }

        [Test]
        public void GlobalMaterialExclusionRemovesLaterGeneratedMatchingSlotsOnly()
        {
            MaterialFixture(1, 2); var source = renderer.sharedMesh; var materials = renderer.sharedMaterials;
            var filter = LateRenderingFilter(); filter.MaterialExclusions.Add(new MaterialExclusion { Material = materials[1] });
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context); MaterialFilterPass.Apply(context);
            var late = PolicyChild("Generated").AddComponent<SkinnedMeshRenderer>(); late.sharedMesh = source; late.sharedMaterials = materials;
            GeneratedOutputFilterPass.Apply(context);
            Assert.That(late.sharedMaterials, Is.EqualTo(new[] { materials[0] }));
            Assert.That(late.sharedMesh, Is.SameAs(source));
        }

        [Test]
        public void ExplicitSlotExclusionDoesNotSelectUnregisteredCopiesOfTheSameMesh()
        {
            MaterialFixture(1, 2); var source = renderer.sharedMesh; var materials = renderer.sharedMaterials;
            var filter = LateRenderingFilter(); filter.MaterialExclusions.Add(new MaterialExclusion { Renderer = renderer, Slot = 1 });
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context); MaterialFilterPass.Apply(context);
            var late = PolicyChild("Unrelated copy").AddComponent<SkinnedMeshRenderer>(); late.sharedMesh = source; late.sharedMaterials = materials;
            GeneratedOutputFilterPass.Apply(context);
            Assert.That(late.sharedMaterials, Is.EqualTo(materials));
        }

        [Test]
        public void ExplicitSlotExclusionResolvesRendererReplacementBeforeItsFirstApplication()
        {
            MaterialFixture(1, 2); var source = renderer.sharedMesh; var materials = renderer.sharedMaterials;
            var filter = LateRenderingFilter(); filter.MaterialExclusions.Add(new MaterialExclusion { Renderer = renderer, Slot = 1 });
            using var registry = new ObjectRegistryScope(new ObjectRegistry(root.transform));
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            var late = PolicyChild("Replacement").AddComponent<SkinnedMeshRenderer>(); late.sharedMesh = source; late.sharedMaterials = materials;
            ObjectRegistry.RegisterReplacedObject(renderer, late); UnityEngine.Object.DestroyImmediate(renderer);
            MaterialFilterPass.Apply(context);
            Assert.That(late.sharedMaterials, Is.EqualTo(materials.Take(1)));
        }

        [TestCase(false)] [TestCase(true)]
        public void BlendShapeRegionExclusionsFollowRestoredAndRegisteredSourceMeshes(bool replace)
        {
            MaterialDescriptor(); var source = renderer.sharedMesh = OcclusionMaskMesh();
            using var registry = new ObjectRegistryScope(new ObjectRegistry(root.transform));
            var context = CaptureRegionFilter(new BlendShapeMeshExclusion { Renderer = renderer, BlendShapes = new() { "Region A" } });
            ApplyRegionFilter(context);
            var target = renderer;
            if (replace)
            {
                target = PolicyChild("Registered replacement").AddComponent<SkinnedMeshRenderer>();
                ObjectRegistry.RegisterReplacedObject(renderer, target);
            }
            target.sharedMesh = source;
            BlendShapeExclusionPass.Apply(context); Track(target.sharedMesh);
            Assert.That(target.sharedMesh.GetIndices(0), Is.Empty);
            Assert.That(target.sharedMesh.GetIndices(1), Is.EqualTo(source.GetIndices(1)));
            Assert.That(source.GetIndices(0).Length, Is.EqualTo(3));
            var output = target.sharedMesh;
            BlendShapeExclusionPass.Apply(context);
            Assert.That(target.sharedMesh, Is.SameAs(output));
        }

        [Test]
        public void BlendShapeExclusionsDoNotRequireAlreadyOptimizedEmptyMorphsToSurvive()
        {
            MaterialDescriptor(); var source = renderer.sharedMesh = OcclusionMaskMesh();
            source.AddBlendShapeFrame("Empty", 100, new Vector3[source.vertexCount], null, null);
            var context = CaptureRegionFilter(new BlendShapeMeshExclusion { Renderer = renderer, BlendShapes = new() { "Empty" } });
            ApplyRegionFilter(context);
            source.ClearBlendShapes();
            Assert.DoesNotThrow(() => BlendShapeExclusionPass.Apply(context));
            Assert.That(renderer.sharedMesh, Is.SameAs(source));
        }

        [Test]
        public void AtlasRecipeAndSelectionSurviveAuthoringMarkerRemoval()
        {
            MaterialDescriptor(); var settings = root.AddComponent<ALTOMaterialAtlas>(); settings.ApplyOnPlatforms = OptimizationPlatforms.All;
            var material = Track(new Material(Shader.Find("Unlit/Texture")));
            settings.MergeGroups[0].ReferenceMaterial = material; settings.MaterialConversion = AtlasMaterialConversionTiming.AfterGenerators;
            var context = new BuildContext(root, null);
            var capture = MaterialAtlasBackend.Capture; var resolve = MaterialAtlasBackend.ResolveExisting;
            try
            {
                MaterialAtlasBackend.Capture = (_, _) => { }; MaterialAtlasBackend.ResolveExisting = null;
                MaterialAtlasPass.Capture(context);
            }
            finally { MaterialAtlasBackend.Capture = capture; MaterialAtlasBackend.ResolveExisting = resolve; }
            var state = context.GetState<MaterialAtlasState>();
            settings.MergeGroups[0].AtlasSize = 512;
            UnityEngine.Object.DestroyImmediate(settings);
            var late = PolicyChild("Late").AddComponent<MeshRenderer>();
            Assert.That(state.Recipe.MergeGroups[0].AtlasSize, Is.EqualTo(2048));
            Assert.That(state.Pending && !state.Prepared, Is.True);
            Assert.That(MaterialAtlasPass.SelectRenderers(root, state), Does.Contain(late));
        }
    }
}
