using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.Installer
{
    internal enum InstallerRegionSelection { AllBlendShapes, SelectedBlendShapes }

    [Serializable]
    internal sealed class InstallerOcclusionTarget
    {
        public SkinnedMeshRenderer Renderer;
        public InstallerRegionSelection RegionSelection;
        public List<string> BlendShapes = new();
    }

    // Window-local authoring choices, applied as overrides on the bundled prefab instance.
    [Serializable]
    internal sealed class InstallerDraft
    {
        public VRCAvatarDescriptor Avatar;
        public bool IncludeCommon = true;
        public bool CommonMobileOnly;
        public bool IncludeMobile = true;
        public bool NormalizeProbeAnchors = true;
        public bool PreserveMmdBlendShapes = true;
        public bool ExcludeAfkAnimations;
        public bool AfkScanComplete;
        public VRCAvatarDescriptor AfkScannedAvatar;
        public List<AfkClipChoice> AfkCandidates = new();
        public List<string> AfkDiagnostics = new();
        public List<AnimationClip> AdditionalExcludedAnimations = new();
        public bool BakeLilToonMainTextures;

        public bool ParameterLayout = true;
        public bool ParameterCompression = true;
        public AnimatorParameterFreezeMode FreezeMode = AnimatorParameterFreezeMode.AllExternalInputsExceptPreserved;
        public AnimatorParameterSelection PreserveParameters = new();
        public List<AnimatorParameterConstant> FrozenValueOverrides = new();

        public List<GameObject> ExcludedObjects = new();
        public List<Component> ExcludedComponents = new();
        public bool RemoveAudioSources = true;
        public RendererFilterTypes ExcludedRendererTypes = RendererFilterTypes.ParticleSystems | RendererFilterTypes.Lines | RendererFilterTypes.Trails;
        public bool RemoveAdditionalMaterialPasses = true;
        public List<BlendShapeMeshExclusion> BlendShapeExclusions = new();
        public PhysBoneFilterMode PhysBoneFilterMode = PhysBoneFilterMode.KeepListed;
        public List<VRCPhysBone> PhysBones = new();
        public List<PhysBoneRootSelection> PhysBoneRoots = new();
        public bool RemoveUnreferencedColliders = true;
        public ContactFilterMode ContactFilterMode = ContactFilterMode.KeepListed;
        public List<ContactObjectSelection> ContactRoots = new();

        public bool RemoveOccludedMesh = true;
        public List<InstallerOcclusionTarget> OcclusionTargets = new();
        public bool ReduceMeshes = true;
        public int TriangleBudget = 60000;
        public bool PreserveBorderEdges;
        public List<Renderer> PreserveRenderers = new();
        public bool NormalizeRendererSettings = true;
        public bool AllowCoordinateSpaceChanges = true;
        public bool AllowMaterialSlotReordering = true;

        public bool CreateMaterialAtlas = true;
        public Material ReferenceMaterial;
        public int AtlasSize = 2048;
        public AtlasCompression AtlasCompression = AtlasCompression.Normal;
        public ExistingAtlasPolicy ExistingAtlases = ExistingAtlasPolicy.ReplaceFullyCovered;
    }
}
