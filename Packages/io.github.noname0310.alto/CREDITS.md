# Credits and implementation references

## AAO: Avatar Optimizer

[Avatar Optimizer](https://github.com/anatawa12/AvatarOptimizer) by **anatawa12
and contributors** is an important implementation and design reference for this
package. The reviewed source is version **1.9.20**, commit
[`439a56aae2cc744c2a1590e79067353879496b8e`](https://github.com/anatawa12/AvatarOptimizer/tree/439a56aae2cc744c2a1590e79067353879496b8e).
AAO is licensed under MIT. Its copyright and permission notice are reproduced
in [ThirdPartyNotices.txt](ThirdPartyNotices.txt).

The following table distinguishes adapted data, interoperability, and design
references. A design reference does not mean that AAO's implementation was
copied, that behavior is identical, or that feature parity has been completed.

| Area in this package | AAO reference | Nature of use |
| --- | --- | --- |
| `Editor/MmdMorphNames.cs` | MMD morph compatibility table | Adapted compatibility names; MIT notice retained. |
| `Integrations/AvatarOptimizer/` | Public component information, object mapping and UV compatibility APIs; Asset Description schema | Optional interoperability. No internal optimizer is invoked to perform our optimization. |
| `Editor/ObjectOptimization/` | `Editor/Processors/TraceAndOptimize/FindUnusedObjectsProcessor.cs`, component dependency analysis | Design reference for dependency preservation, unused component collection and bone merging boundaries. Our implementation also uses parameter specialization and Animator reachability. |
| SDK PhysBone hierarchy-listener lifetime in `ObjectOptimizationSafety` and `AvatarDependencyGraph` | `Editor/APIInternal/ComponentInfos.VRCSDK.cs` | Dependency modeling reviewed against AAO: a live PhysBone retains the parent-change listener on its target; the listener has no independent output. Independently implemented exact SDK type recognition and graph edges. |
| `Editor/Build/MaterialSlotPlan.cs`, `MaterialFilterPass.cs`, `AutomaticRenderingRules.cs` | `RemoveSubMeshesWithoutMaterial.cs`, `MergeMaterialSlots.cs` | Rendering and material-slot semantics reviewed against AAO. Explicit lossy exclusions are separate from equivalence-preserving cleanup. |
| `Editor/AnimatorOptimization/`, `Editor/Analysis/` | `Editor/Processors/AnimatorOptimizer/`, Animator dependency analysis | Reference for transformation constraints and compatibility. Our shared reachability, constant propagation and output dependency analyses are independently implemented. |
| `Editor/Meshes/`, `Editor/Analysis/TransformMutationAnalysis.cs` and geometry optimization design | `AutoMergeSkinnedMesh.cs`, `Internal/MeshInfo2/MeshInfo2.cs`, `MergeSkinnedMeshProcessor.cs`, `ConfigureMergeBone`, `MergeBoneProcessor.cs`, `AnimatorParserV2/AnimatorParser.cs`, `AutoMergeBlendShape.cs`, `ConfigureRemoveZeroSizedPolygon.cs` | Design reference for merge eligibility, vertex defaults, skinning, bone dependencies and stored bounds. Independently implemented assembly, grouping, animation remapping and static skin-bone substitution. Full hierarchy/physics/geometry parity remains separate work. |
| `Editor/Physics/`, shared transform-writer provenance and physics optimization design | `OptimizePhysBone.cs`, `ReplaceEndBoneWithEndpointPositionProcessor.cs`, `AutoMergeCompatiblePhysBone.cs`, `MergePhysBoneProcessor.cs` | Design reference for collider sharing, animated-transform hazards, endpoints and compatible simulation merging. Independently implemented exact collider sharing with multiplicity protection, external-writer analysis, unreferenced endpoint replacement and a conservative simulation merger for identical constant settings. Unsupported chains remain separate. |
| `Editor/Materials/` and material/texture optimization design | `RemoveUnusedMaterialProperties.cs`, `OptimizeTexture.cs` | Reference for saved-property cleanup and texture/UV constraints. Independently implemented source-absence proof, copied-asset cleanup, exact serialized equivalence and safe clip remapping. Texture/UV reduction is separate work; atlas conversion uses TexTransTool. |
| Build pipeline architecture | `Editor/OptimizerPlugin.cs`, `Internal/TraceAndOptimizeBase/TraceAndOptimizeProcessor.cs` | Reference for staged execution, re-analysis and coexistence boundaries. |

`Editor/AnimatorOptimization/UnusedLayers.cs` was reviewed against AAO 1.9.20
`Internal/AnimatorOptimizer/RemoveMeaninglessLayer.cs` and NDMF 1.14.8
`Editor/API/Util/GlobalTransformations.cs`. The final-boundary empty-state proof,
native Write Defaults/additive counterexamples and cross-playable index guards
are independently implemented. It uses NDMF's public layer-removal and
serialization APIs, without calling AAO's implementation.

Material-slot reordering was reviewed against AAO 1.9.20
`Editor/Processors/TraceAndOptimize/MergeMaterialSlots.cs` and
`AutoMergeSkinnedMesh.cs`, including the `allowShuffleMaterialSlots` option,
first-material preservation and animated-slot protection. Our first-occurrence
grouping, platform policy and shared animation checks are independently implemented.

The shared `Editor/Meshes/BlendShapeRegionMasks.cs` implementation, used by
explicit Build Filter exclusions and occlusion removal, references AAO Remove Mesh By
BlendShape's rule that a primitive is selected when any vertex is affected.
Multi-frame masks, GPU coverage/visibility sampling, pose copies and index
filtering are independently implemented. AAO does not perform this package's
visibility analysis.

New implementations that use these references should also identify the relevant
upstream files in their source comments. Any adapted implementation must retain
AAO's MIT notice and be identified separately from independently written code.
Performance or optimization improvements are reported only when supported by
tests or measurements; consulting AAO does not establish that this package is
faster, safer, or more capable in every case.

`Editor/Analysis/AudioOutputDependencies.cs` was reviewed against AAO 1.9.20
`Editor/APIInternal/ComponentInfos.VRCSDK.cs`, Unity 2022.3's
[audio filter contract](https://docs.unity3d.com/2022.3/Documentation/Manual/class-AudioEffect.html)
and the installed SDK's `ONSPAudioSource.Reset` / `LateUpdate` behavior.
Its independently written output-to-effect dependency model allows orphan effects
to be collected. No Oculus spatializer source implementation is copied or distributed.

`Editor/Meshes/SkinningBoundaryDependencies.cs` and the optional mesh information
adapter were reviewed against AAO 1.9.20 `ConfigureMergeBone` and
`MergeBoneProcessor.DoBoneMap2`. They independently declare temporary public API
dependencies for singular skinning inputs so our later static-bone substitution
can consume the original coordinate systems. No AAO internal implementation or
private configuration API is used.

`Editor/Analysis/VrcFallbackPropertyContract.cs` follows the public
[VRChat shader fallback contract](https://creators.vrchat.com/avatars/shader-fallback-system/)
(checked 2026-10-26). Fallback shader names there are platform interfaces, not
artist-specific optimization rules. Properties are read from the actual fallback
shader declarations; unavailable declarations cause conservative preservation.

## VRCFury behavior reference

VRCFury by Senky and contributors, version **1.1433.0**, was reviewed for automatic
network parameter budget selection, snapshot buffering, cyclic transmission and
resynchronization after missed batches. Reviewed files under
`Editor-Avatars/Service/Compressor`: `ParameterCompressorService.cs`,
`ParameterCompressorSolverService.cs`, `ParameterCompressorLayerService.cs`,
`ParameterCompressorLegacyLayerService.cs`, and `OptimizationDecision.cs`.
[VRCFury project](https://github.com/VRCFury/VRCFury),
[Parameter Compressor documentation](https://vrcfury.com/components/other/#parameter-compressor-beta).

The multiplexing implementation here is independently written using NDMF and
public VRChat SDK APIs. It uses separate typed payloads, an Int tag with alternating
epochs, preparation/publication phases and its own budget model and serialized
contract. VRCFury source is not copied, redistributed, linked or called. VRCFury is
not a dependency. Its custom license is not the MIT/Apache-2.0 license of this package.

Automatic priority selection is independently designed around this package's final
Animator dependency analysis. It propagates inferred external influence, protects
momentary dependencies and stops at the first feasible priority tier. It does not
copy VRCFury's selection rules or use facial-asset-specific parameter tables.

## VRChat SDK APIs

VRCFury 1.1433.0 `Editor-Avatars/Service/FixAudioService.cs` was inspected when
an Android build tried to remove an AudioSource before its required spatializers.
The explicit audio exclusion uses this package's existing audio-output model and
generic required-component validation. It does not call or modify VRCFury.

Parameter alignment interoperability was checked against VRCFury 1.1433.0.
Anonymous-padding reconciliation is independently implemented from the final
native declarations and their observable uses. It does not read another tool's
private cache or infer its compression protocol.

Final output cleanup uses the SDK preprocess callback and `IEditorOnly` contracts
alongside NDMF's `INDMFEditorOnly` and public controller serialization APIs.
The installed VRCFury `KeepEditorOnlyComponentsLongerHook.cs` was inspected to
diagnose metadata surviving past NDMF. The implementation does not change the
SDK callback list or call VRCFury's stripping code. Runtime-only dependency roots,
option-independent finalization and fixed-point cleanup are independently written.

VRCFury 1.1433.0 `Editor-Common/Hooks/PreventComponentDeletionHook.cs` was also
inspected when a bundle-only SDK build retained debugging components. The general
removal postcondition checks and dependency rechecks are independently implemented;
they do not identify VRCFury component types or call or bypass its hooks.

`Editor/ObjectOptimization/RedundantConstraints.cs`, `ParentConstraintOptimization.cs`, and
`ConstraintSourcePruning.cs` use the public
[VRChat Constraints API](https://creators.vrchat.com/common-components/constraints/constraints-api/)
and [Scale Constraint](https://creators.vrchat.com/common-components/constraints/vrc-scale-constraint/)
and [Parent Constraint semantics](https://creators.vrchat.com/common-components/constraints/vrc-parent-constraint/)
(checked 2026-10-26). Local-scale equality, rest-pose equality, hierarchy substitution
and writer/reader separation are independently implemented and checked against native SDK evaluation;
no SDK solver or artist-specific code is copied.
Per-source invariant proofs and animation-index compaction were independently
implemented against the documented sixteen-slot animation limit and source-owned
parent offsets (rechecked 2026-10-27). Native tests exercise multiple animated
sources and overflow entries rather than replacing the SDK solver with a model.

`Editor/Analysis/TransformScaleSignAnalysis.cs` uses the documented
[Head Chop scale-factor contract](https://creators.vrchat.com/avatars/avatar-components/vrc-headchop/)
(checked 2026-10-26). Scale-writer retention, invariant-setting proof and mesh
grouping are independently implemented. GPU fixtures compare the documented
nonnegative scale domain, including collapse; they do not emulate the VRChat client.

`Editor/Analysis/ConstantScalarOutput.cs` independently implements the documented
[Unity 2022.3 1D interpolation rules](https://docs.unity3d.com/2022.3/Documentation/Manual/BlendTree-1DBlending.html)
(checked 2026-10-27). Native Animator fixtures compare fixed weighted position,
scale and SDK component-float outputs, other animated outputs and clip timing.
Quaternion and discrete animation are not modeled as independent scalar channels.

## lilToon texture baking

[lilToon](https://github.com/lilxyzw/lilToon), by **lilxyzw**, provides the optional
main-texture baking backend under the MIT license. Reviewed version: **2.3.4**;
`lilEditorTextureBaker.cs`, `ltspass_baker.shader`, `lil_common_frag.hlsl`,
`lil_common_functions.hlsl` and `lil_common_macro.hlsl`.
ALTO calls the public `lilToonInspector.RunBake` API and the installed baker shader.
It does not copy the shader or interactive texture-saving UI. Animation and scope
analysis, build-copy handling, eligibility checks and native rendering tests are
independently implemented.

## TexTransTool

[TexTransTool](https://github.com/ReinaS-64892/TexTransTool) **1.0.2** and
[TexTransCore](https://github.com/ReinaS-64892/TexTransCore) **0.2.0** by
**ReinaSakiria** provide the optional texture atlas backend.
`Editor/TTTInitializer.cs`, `TTCE-Unity/ComputeObjectUtility.cs`, and
`RenderTextureOperator.ColorFill` were reviewed to diagnose initialization order
after script reload. ALTO's Editor initialization boundary is independently
implemented; it does not invoke or copy their private initialization code.

## Prefabulous Universal

**PA Edit All Mesh Anchor Override** by **Haï~**, reviewed in Prefabulous Universal
**2.2.0**, is the behavior reference for the anchor option in
**ALTO Mesh Renderer Settings Override**.
Reviewed files: `Scripts/Common/PrefabulousEditAllMeshAnchorOverride.cs` and
`Scripts/Shared/Editor/PrefabulousEditAllMeshAnchorOverridePlugin.cs`.
Our scoped selection, automatic anchor choice, preservation checks and build
staging are independently implemented; no Prefabulous code is copied and the
package is not required. [Prefabulous documentation](https://docs.hai-vr.dev/docs/products/prefabulous/universal).
