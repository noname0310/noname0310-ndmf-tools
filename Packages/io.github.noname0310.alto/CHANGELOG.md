# Changelog

## 0.0.5

- Install a connected instance of the bundled default settings prefab. Record avatar references, selected values and optional removals as instance overrides, preserving the link through Undo/Redo and nested avatar-prefab saves without changing the package asset.
- Default the Installer to a Simple workflow with explicit AFK, feature, PhysBone and mesh choices. Keep the full UI in Advanced mode without resetting choices. Add a searchable PhysBone checklist with Hide Inactive PhysBones enabled by default; hidden entries retain their selections and no physics priorities are chosen automatically.
- Initialize mobile component exclusions for recognized Light Limit Changer, marshmallow PB, Pudding and RealSweat authoring types without package dependencies or object-name matching. Keep all suggestions editable.
- Add mesh-region presets and a Shinano original-face overlay preset containing only region names and geometry fingerprints. Ignore skinning, added BlendShapes, reordered vertices and duplicate positions. Apply unique exact matches initially; require selection for approximate distribution matches or ambiguous presets, without storing source geometry or asset references.
- Evaluate AFK animation candidates and initialize full-detail face mesh selections when assigning the Installer's avatar. Keep manual list edits, retain an AFK refresh action for later animation changes, and remove the redundant face-mesh button.
- Suggest a body mesh for occlusion removal from generic naming, material and BlendShape evidence when assigning an avatar. Select all of its BlendShapes initially, leave ambiguous cases for manual selection, and exclude nested avatars and EditorOnly meshes from discovery.
- Describe Installer choices by their effects and group controls in Common, Mobile and Cross-platform frames with explicit platform labels. Offer Optimize Mobile Only and use a fixed 256-bit synchronization target in the installer.
- Add an avatar setup installer and reusable settings template. Configure Common/mobile optimization, parameter preservation, feature filters, geometry and atlasing, then install one undoable hierarchy scoped to the avatar root. Validate references and optional dependencies; reject duplicate installations without overwriting existing settings.
- Discover AFK BlendShape clips from the built-in parameter across descriptor and component controllers, including transition paths, overrides and supported Blend Trees. Review shared clips separately and recheck candidates before installation. Default body-region candidates to all BlendShapes.
- Add ALTO Optimization Platform Filter for settings subtrees, without removing objects or affecting other packages. Keep cross-platform synchronization outside the installer's mobile-only Common group.
- Include a generic Mobile Toon Standard reference material with white tint and culling disabled for material atlases.
- Add ALTO Mesh Optimization Policy with one scope and platform selection for coordinate-space changes, rigid mesh conversion and material-slot reordering. Keep Trace And Optimize Meshes conservative by default. Remove its former per-option policy fields; configure the new component explicitly when upgrading.
- Preserve per-input mesh permissions through optional external renderer mapping, internal merges and late SDK optimization. Narrower scopes can disable broader permissions; conflicting policies at the same scope and platform are rejected.
- Split new filter setups into ALTO Build Exclusions for authoring objects, components and audio, and ALTO Rendering Filter for generated rendering output, material slots and BlendShape regions. Each component has an independent scope and platform selection. Existing combined Build Filter components remain supported.
- Preserve rendering rules across early exclusions without treating deliberately removed targets as missing generated output. Document platform-first settings hierarchies and shared Android/iOS policy selection.

## 0.0.4

- Persist all generated Animator dependencies after late SDK commits, including cloned Parameter Drivers and Tracking Controls. This fixes behaviours that worked in memory but were missing from saved build assets, affecting facial tracking and other state-driven features.
- Preserve higher-dimensional UV aliases and distinguish absent UV attributes from implicit component defaults when merging meshes. Keep original shader inputs for mixed 2D, 3D and 4D layouts.
- Retain unsupported zero-speed lilToon texture-sheet frame selections during baking. Independent static tone correction can still bake without changing the selected frame.
- Preserve explicitly mapped secondary textures, masks and UV transforms during early material conversion. Use a bake's generated texture only for its declared replacement property.
- Include animated material swaps when checking a texture bake's scope. Retain original baking inputs when consumers are outside the scope or cannot be resolved safely.
- Add serialized Animator reload regressions, native rendering comparisons, and deterministic fuzz coverage for state reachability, UV layouts, parameter upload records and triangle-budget allocation.

- Add opt-in lilToon main-texture baking to Trace And Optimize Materials on PC and mobile. Use lilToon's public baker on build copies, preserve animated/unsupported composition, and bake selected inputs before early atlas shader conversion.
- Allow Build Filter to remove a feature referenced only by a parameter Freeze source. Skip the removed source's rules without applying them to another namespace; preservation and runtime dependency conflicts still stop the build.
- Disable Meshia Smart Link for skinned and morph-bearing reduction inputs. Prevent overlapping undeformed geometry with different skinning from being connected across the avatar, including merged pairs of earrings.
- Preserve an all-Write-Defaults-On controller when generating integer compression and time-multiplexing layers. This prevents unrelated expressions from sticking after playback and avoids changing Direct Blend Tree facial outputs; WD-Off and mixed controllers retain their existing mode.
- Model the SDK's hidden PhysBone parent-change listener as a dependency of its PhysBone instead of an unknown global runtime reader. Keep live listeners and collect orphans without blocking Android atlasing.
- Accept value-preserving Bool/Int/Float conversions of private parameter constants by late build optimizers, while continuing to reject changed values, writers, unsupported types and public contract changes.
- Merge skinned renderers with equivalent animated `updateWhenOffscreen` signals. Treat proven default-valued outputs as static; changing signals still require matching writers, masks and expanded bounds. Ignore dummy renderer bindings only when neither serialization nor Unity's native animation lookup finds the property.
- Evaluate initial visibility from reachable motion outputs even when unreachable clips remain for Write Defaults binding coverage. This allows filtered PhysBone inputs to eliminate their inactive Grab meshes without manual value overrides.
- Collect permanently disabled global PhysBone colliders when no live animation or remaining consumer needs them. Preserve dynamic, explicitly protected and referenced colliders.

## 0.0.3

- Publish the expanded optimizer as ALTO. VPM replaces the previous Advanced Avatar Optimizer and Trace And Optimize BlendShapes packages to prevent duplicate scripts.

- Add explicit Preserve, Replace Fully Covered and Chain policies for existing TexTransTool atlases. Protect shared consumers before early conversion, require complete coverage before takeover, and repack completed UV0 atlas outputs without invoking private TTT build APIs. Preserve skinning, vertex attributes and all BlendShape frames when splitting shared vertices.
- Capture build error stacks and add Build Failure Diagnostics to Build Filter and the Tools menu. Identify participating build-only component types from stack signatures, show ambiguous instances, test individual exclusions on fresh build copies, and add only user-selected candidates to the source filter with Undo. Preprocessing success is not a guarantee of equivalent behavior or successful upload validation.

- Default new compression components to Automatic By Priority: try persistent menus, internal computations, runtime-driven values and inferred external inputs in that order. Follow external input dependencies without artist-specific names, trim the first feasible tier, protect momentary paths and explicit preservation, and report decisions and any external-input multiplexing. Existing explicit selection modes and recorded cross-platform contracts remain intact.
- Honor visible cross-platform Parameter Policy preservation while choosing the automatic reference contract. Keep platform-specific freezing disabled on other platforms and reject preservation conflicts with already recorded contracts.
- Restore a known local input if a late layout tool re-enables synchronization at its original reference padding slot. Require exact slot provenance, type, default and Saved metadata; retain the local declaration and consumers while restoring the anonymous wire. Unknown aliases and changed logical declarations still fail validation.
- Remove proven empty Write Defaults On override layers after SDK generation. Preserve additive/WD-Off pose effects, masks, synchronization targets and same- or cross-playable layer controls. Count completed removals for stable dependency convergence.
- Reconsider mesh merging at the final SDK boundary after late authoring readers disappear. Reuse scope, preservation, shader and visibility checks, current native animation paths, and dependency convergence.
- Accept externally renamed anonymous padding only when it replaces this build's own unused padding at the same wire position and type. Prepare non-reference layouts even when already within budget; retain strict checks for real transport changes.
- Add explicit audio-source exclusion before generators, preserving selected sources and retained listener effects. Remove unneeded filters and SDK spatializers before their required sources and validate remaining readers before mutation.
- Wait for delayed Editor initialization before build analysis. Reject premature synchronous analysis and atlas setup before optional backends can fail with uninitialized compute resources.

- Prove implicit Write Defaults independently of ordinary Direct/1D tree weights. Remove dead dynamic calculation chains and replace effect-free trees with an existing leaf while retaining layer indices, mixed/default-sensitive states, preserved parameter values, events and SDK behaviour effects. Shared empty clips no longer make input preservation retain unrelated effect-free state logic.
- Add independent unused Animator parameter declaration cleanup at the final SDK boundary. Keep public synchronization/layout contracts, menus, sensors, platform/external inputs and names used by any controller; leave declarations available to late generators during earlier NDMF passes.

- Add ALTO Contact Filter, defaulting to Android and Keep Listed. Filter Senders and Receivers after generation and late SDK processing, track explicit selections through replacement/reparenting, validate preserved inputs and references, and remove exclusively orphaned animation bindings without changing shared clips or timing.
- Trace Head Chop through its controlled bones instead of treating it as an unconditional output. Collect unused Head Chop components and their orphan target hierarchies while retaining live skin/child outputs, external targets, animated references and explicit preservation.

- Rename the package to ALTO (Avatar Link-Time Optimizer), using static analysis to optimize VRChat avatars. Rename all Advanced component prefixes to ALTO, preserve script GUIDs, and update package IDs, assembly names and editor integration.

- Count only completed object and constraint removals during dependency convergence. If an editor integration declines deletion, retain the surviving target's bindings and parenting, recheck required components, and stop retrying an unchanged graph.

- Separate dynamic parameter preservation from object liveness. Collect proven unobserved transform/constraint chains and orphan components even when their mutable clips also depend on preserved inputs. Retain the inputs, live outputs, timing, expression metadata and explicit object/component preservation.

- Evaluate continuous scalar outputs of fixed 1D Blend Trees, including single-writer Direct wrappers. Remove redundant position/scale and component-float curves when their interpolated output equals the existing value; retain quaternion, discrete, dynamic, ambiguous and non-finite cases without changing initialization poses or clip timing.

- Prove zero-weight constraint sources independently of other animated sources. Compact their weight and parent-offset bindings together, retain animation duration, and preserve shared bindings, unsupported source layouts and dynamic lock/freeze behavior.
- Preserve multi-source SDK weight normalization when compaction would leave one fractional or animated source. Use a zero-weight alias to the surviving source instead of retaining an unused hierarchy.

- Recheck final runtime object and Animator dependencies after SDK generators independently of Expression cleanup settings. Stop treating completed editor-only metadata as runtime output, while retaining explicit preservation, scope boundaries, late consumers and indirect references. Respect required-component removal order and preserve cumulative Expression reports across convergence.
- Avoid repeating converged Expression cleanup unless subsequent object or Animator edits invalidate it. Count menu and local-declaration removals as dependency changes, and retain iterative cleanup of cascading sensors and their controllers.
- Include late SDK-generated local readers in sensor dependency history before final cleanup. Their sensors can be collected when those readers disappear; external-only sensors still require explicit input freezing.
- Track controller ownership when recording sensor readers. A same-named private child Animator input no longer supplies evidence for removing an external-only avatar sensor, in either NDMF or final SDK cleanup.

- Trace audio filters and SDK spatializers through their actual audio outputs. Collect orphan effects and unused placement constraints after source removal, while retaining source/listener filter chains, component ordering and observed or preserved effects.

- Resolve empty SDK constraint targets as self-targets in object dependency collection. Remove unobserved constraint subtrees while retaining driven skin bones, external targets and preserved references, including Unity missing-object wrappers.

- Collect controller-less child Animator subtrees when no runtime output observes them. Retain root Animators, generated controller owners, bound Playables, live descendant animation boundaries and indirect or preserved references.

- Add explicit All Synchronized Inputs selection for time multiplexing, including non-menu and continuous inputs, while retaining Keep Native, policy and built-in protection. Retain strict Automatic Menu Inputs as a separate selection mode; report the snapshot delay and possible loss of brief changes.
- Track automatic compression writer influence per verified owner/remote context instead of combining private temporary writers across instances. Retain conservative fallbacks for uncertain controller ownership.
- Support existing Bool, Int and Float IsLocal declarations in both transport codecs. Validate unsupported identity types before changing controllers.

- Reapply resolved parameter constants after late SDK controller generation, independently of expression cleanup. Validate current paths, preserved dependencies, scope and private constants before committing; retain the original logical selection, defaults and network declarations.

- Report automatic multiplexing exclusions per input and their bit costs. Publish selection diagnostics even when budget validation fails, so preservation, dynamic dependencies and unsupported menu inputs can be distinguished without weakening the policy.

- Add ALTO Parameter Compression: explicitly encode bounded Int inputs as N Bool slots, retain local OSC names and Saved values, and record the encoding in ALTO Parameter Layout for other platforms. Add final dependency-based unused synchronization removal and native-representation preservation. The integer codec clamps remote out-of-range values and does not promise atomic multi-bit updates.

- Add optional automatic or explicitly selected time multiplexing on reference platforms including PC. Plan typed payload slots against a configurable network budget, buffer full cycles, recover from missing batches and expire partial snapshots. Keep protected inputs native and preserve logical local names, Saved values and native Float payload quantization. Record the protocol for other platforms and prepare over-budget transport before late SDK processors; reject external protocol rewrites. This trades response time and Animator work for network bits and is not reliable delivery of brief events.

- Apply automatic parameter freezing consistently to computed Expression Parameters. Protect the resolved Preserve dependencies and VRChat built-ins without treating Driver/curve-written outputs as a separate implicit preservation category. Keep internal-only calculations outside automatic input selection.

- Trace ordered Parameter Driver reads and overwrites. Remove unobserved internal writes replaced by a valid Set before use, while preserving intermediate network, sensor, external and explicit Preserve values. Keep dependencies feeding Random with Prevent Repeats, including when only its shared random side effect remains observable.
- Resolve value dependencies at each read within a Parameter Driver, so later writes do not create false dependencies for an earlier constant-derived output. Share the analysis with parameter policy and menu cleanup, preserve all externally observed writes, and retain the original dependency graph when SDK operations cannot be verified.
- Separate verified owner and remote-instance dependencies using IsLocal and local-only Drivers. Follow synchronized values from owner to remote consumers without connecting private temporary values across instances. Carry the same execution context into parameter-curve, Driver and menu cleanup, and retain explicit preservation roots separately from their derived dependencies. Fall back when instance identity or child Animator ownership is uncertain.
- Carry NDMF controller ownership through parameter policy, binding safety, expression cleanup and compression analysis. Analyze independent child controllers with variable inputs and retain all possible executions for shared controllers and behaviours. Report why identity contexts cannot be established when requested by diagnostics.
- Accept avatar-owned IsLocal declarations using Bool, Int or Float according to VRChat's documented runtime conversion. Verify typed transition and output equivalence, while retaining missing declarations, Trigger types, identity writers and runtime controller replacements.

- Share a unit-weight Write Defaults proof across unused state, parameter and menu analysis. Collapse output-free state machines containing proven constant Blend Trees while retaining their original default motion, layer index and implicit defaults; preserve unknown, varying, negative and ambiguous weight configurations.

- Treat missing SDK constraint targets as self-targets using Unity object-null semantics. Preserve the driven skin bone when the optional target contains an empty or destroyed Unity reference, including constraints on generated avatar hierarchies.

- Share constant 1D Blend Tree branch selection between Animator simplification and invariant-output analysis. Ignore proven zero-influence motions while retaining clip timing, dynamic inputs, ambiguous thresholds and non-neutral outputs that differ from the authored pose.

- Record nested optimization timings and Editor memory samples in build analysis, including failed stages and SDK finalizers. Preserve parent/child relationships to prevent double counting and document the limits of endpoint memory sampling.

- Distinguish scale mutation from possible winding reversal when merging coordinate roots. Proven nonnegative Head Chop settings can share a mesh when the whole input follows the affected frame, while retaining dynamic skinning, negative/unknown factors and independently controlled geometry.

- Prune constant zero-weight SDK constraint sources after proving their component settings invariant. Remove obsolete source-index curves safely and let dependency cleanup collect unobserved source hierarchies.

- Simplify constant Parent Constraints by preserving unchanged At Rest poses or replacing equivalent full following with hierarchy parenting. Preserve child animation paths, activation, scaling semantics, competing writers, references and explicit scopes.

- Track native constraint outputs separately from their source references. Add optional redundant local scale constraint removal to ALTO Trace And Optimize Objects, preserving authored scales and dynamic, referenced, out-of-scope or explicitly protected outputs.

- Accept early-converted material inputs transferred to generated renderers. Validate their provenance across selected geometry outputs, while still rejecting discarded or out-of-selection inputs and reporting the full source renderer path.

- Clarify Inspector selection lists and per-item overrides. Label automatic occluder and parameter overrides explicitly, hide inactive atlas selections and dependent settings, and keep mesh policies visible when they still affect other optimization operations. Changing display modes retains saved values.

- Resolve mutual retention between unused sensors and local Expression Parameter declarations at the final cleanup boundary. Recheck late SDK consumers and result transforms, retain synchronized/compressed transport contracts, and converge selected object/Animator cleanup without rerunning generators or mesh processing.

- Detach the depth render target before GPU occlusion sampling and select its color aspect explicitly. Reading an active target during build preprocessing could return zero and incorrectly delete exposed regions.

- Add ALTO Parameter Layout with reference-platform upload records, independent cross-platform padding and explicit handling of additional inputs. Use only NDMF and public VRChat SDK APIs; failed uploads and analysis never replace confirmed records. Invalidate stale records when the reference platform is successfully uploaded without a new layout. Reject late synchronized-declaration rewrites instead of depending on an external compressor's protocol.
- Add centralized BlendShape Exclusions to ALTO Build Filter, with searchable morph selection, multi-frame region unions, source-mesh isolation and validation of missing targets, scope and animated mesh swaps. Share mask extraction and selection UI with occlusion filtering.

- Preserve singular skinning inputs across AAO's automatic bone merging through public dependency declarations, then perform static-bone substitution without inverting collapsed transforms. Keep AAO settings and authored bone-merge instructions unchanged.

- Remove proven unused shader-property tracks even when their clips depend on preserved parameters. Keep those inputs, live material and morph outputs, source assets, clip durations and event safeguards intact.

- Prove invariant visibility across full-weight FX override layers and known immediate startup or Any State transitions. Handle root Exit/Entry selection without deleting states or callbacks, and preserve missing-track, delayed-transition, mask and layer-weight behavior. Share SDK layer-weight analysis with the initial-visibility policy.

- Avoid treating SDK Contact, PhysBone collider and Descriptor frame references as independent scale writers during mesh grouping. Keep referenced transforms and actual animation, physics or unknown-script writers protected.

- Fold inactive property tracks under proven zero-weight Direct Blend Tree inputs while preserving initial values, unrelated outputs and clip timing. Share native visibility-writer safeguards between object optimization and the initial-visibility policy.
- Collect permanently inactive renderers referenced by mutable clips driven by preserved parameters. Keep dynamic visible outputs, target Transforms, immutable clips and runtime references intact.

- Add an explicit Bake Initial Visibility option to Animator Parameter Policy. Initialize proven constant FX Active/Enabled outputs before occlusion and dependency cleanup, while protecting dynamic outputs, native visibility writers, scope boundaries and preserved parameters. The lossless object optimizer continues to preserve the original initial state.

- Add material-slot reordering to ALTO Trace And Optimize Meshes, enabled for Android by default, with platform selection and first-material preservation. Combine nonadjacent equal slots while retaining animated slots, reference boundaries and unsupported inputs.
- Recognize equal material-animation defaults when only adjacent repetitions of the same material differ.

- Add ALTO Mesh Renderer Settings Override with independently selected anchor, light/reflection probe and shadow settings. Support multiple scoped platform policies; reject conflicting assignments before writing renderer properties. Automatic and Explicit anchor selection are distinct modes, and explicit missing references fail validation.
- Make atlas group material selection explicit: All Unassigned Materials or Selected Materials. Empty selected lists select nothing. Parameter Freeze Mode remains explicit, with empty-list behavior explained in the Inspector.
- Recognize equivalent fixed lighting-anchor positions and unused probe anchors during mesh grouping without relaxing probe modes or shadow settings.
- Distinguish immutable clips from preserved parameter inputs when merging meshes. Retarget equivalent morph and renderer outputs while retaining independent controls and immutable animation paths.
- Allow missing morph tracks on fixed meshes to participate in merging. Remove ineffective source tracks without shortening clips, reserve missing destination names, and retain mesh swaps or external readers that could change the result.
- Exclude masks from controllers with no relevant binding types during mesh grouping. Retain all masks within a renderer-writing controller and independently check object-activation masks.

- Add ALTO Remove Occluded Mesh: searchable BlendShape region selection, explicit/automatic scoped occluders, conservative GPU coverage and visibility sampling, silhouette margins and multiple pose clips. Preserve uncertain regions and source assets.
- Add a current-setup removal preview and an isolated build-analysis entry point. Separate sampled geometry assumptions from runtime guarantees, honor animation/LOD visibility and shader-state uncertainty, and remove regions before morph freezing and reduction. When material conversion is requested, inspect its actual output before freezing region morphs.

- Add ALTO Trace And Optimize PhysBones with exact local collider sharing, external-writer analysis for Is Animated, unreferenced endpoint replacement and per-component preservation. Collision multiplicity, animation, external readers, scope boundaries and optional AAO mapping remain protected.
- Merge independent PhysBone simulations with identical constant settings and identity sibling roots, retaining original bone objects and skinning. Keep interaction/output parameters, curves, animated chains, unsupported roots and external readers separate; partition groups within 128 real transforms.

- Add ALTO Trace And Optimize Materials: remove provably unused saved data from generated copies, share exactly equivalent materials, and remap safe swap clips while preserving scope boundaries, variants, external references and source assets.
- Preserve VRChat Safety Fallback inputs in material-output analysis as well as saved-property cleanup; query the fallback shader declarations rather than infer material roles from artist shader names.
- Add ALTO Trace And Optimize Meshes after reduction: compatible skinned renderer groups, adjacent material-slot coalescing, morph animation remapping and Descriptor/MMD destination preservation. Keep skinning influences, collapsed bones, stored culling bounds and source assets intact; report incompatible inputs.
- Group meshes by runtime compatibility, with union bounds and an optional platform-specific coordinate-space relaxation. Preserve independent toggles, correct mirrored winding and tangent handedness, and retain independently animated scale-sign boundaries.
- Remove unreferenced vertices and unused or duplicate bone-palette entries with the same shader, reference and animation safeguards. Preserve full skinning influences, morph frames and retained vertex attributes.
- Rebind static skin bones through a shared runtime transform-writer analysis, without reparenting objects or inverting collapsed transforms. Re-run object and Animator cleanup after mesh changes, then clean expressions against surviving consumers.
- Add shared animation binding and runtime dependency graphs, plus optional unused object/component collection independent of AAO. Preserve skin bones, constraints, global colliders, immutable/shared animation targets and explicit scopes.
- Iterate final object and Animator dependency cleanup until no graph or hierarchy data is removed. Recheck previously observed sensor inputs after their consumers disappear, including after external marker removal; preserve network, menu, external and explicitly protected inputs. Report convergence without repeating generators or mesh processing.
- Trace Raycast result consumers and parameter outputs independently during object collection. Remove unused producers without dropping observable skinning, constraint, contact, external or animated result targets.
- Default new PhysBone Filter components to Keep Listed, preserving existing serialized modes.

- Show explicit All Renderers in Scope / Selected Renderers modes in Material Atlas and Mesh Reduction. Display the effective scope and build-generated renderer rule; an empty manual selection selects nothing, and preserved/unselected geometry still counts toward reduction budgets.
- Add Simplify Unused State Logic: trace condition/timing dependencies from observable outputs, remove dead internal calculations and collapse safe empty state machines while retaining default states, layer indices, implicit Write Defaults and random side effects.
- Add Remove Unused Transform Outputs to the object optimizer. Follow renderers, skinning, physics, contacts and serialized references through parent transforms and Unity/VRC constraint chains; remove unobserved constraints and animation writes without changing source assets.
- Add Remove Unused Material Properties to ALTO Trace And Optimize Animator. Prove absence across shader sources, generated passes, includes, macros, fallback shaders and material swaps; retain uncertain or preserved outputs and complete live Color/Vector properties.
- Reanalyze calculations after atlas conversion before AAO cleanup and after final merging. Remove unused internal Set/Add/Copy Driver operations while preserving network/external destinations and random side effects. Keep clip duration and source assets intact, and report removed outputs and analysis cost.
- Add ALTO Trace And Optimize Expressions with independent frozen/dead control, empty submenu and unused local parameter cleanup options.
- Trace menu inputs backward from observable outputs, including Parameter Drivers, normalized tree weights and SDK side effects. Preserve shared Parameter Policy dependencies, synchronized declarations, unknown shader consumers and explicit menu/control selections.
- Handle shared and cyclic menu graphs using build-owned copies, and report removed controls, local parameters and conservative fallbacks.
- Defer SDK expression cleanup until after late parameter compression, recheck final controller consumers, and retain original synchronized and newly generated protocol declarations. Account for implicit Write Defaults outputs.
- Add ALTO Animator Parameter Policy with platform selection, source references, per-source exclusions and transitive parameter preservation.
- Extract shared Animator parameter-dependency analysis, including transition inputs of computed parameters.
- Resolve supported generator parameter renames through NDMF and report unverified sources conservatively.
- Add single-avatar SDK preprocessing analysis with JSON resource and policy reports.
- Add ALTO Build Filter for explicit object/component exclusions and material-pass or submesh removal, with reference checks and animation-slot remapping.
- Default behavior-changing policies to Android and keep platform selectors out of equivalent-result optimizers.
- Respect MA Platform Filter on settings objects and ancestors, including early exclusions with Apply On Platforms set to All.
- Add ALTO PhysBone Filter with generated-component selection, shared/global collider protection, nested-chain boundary preservation and SDK resource budgets.
- Add explicit parameter freezing with source-name remapping, internal constant consumers, Driver/AAP writer removal, scope and Preserve conflict checks, and post-optimizer validation. Retain network slots and menu controls for later cleanup stages.
- Add build-default freezing and All External Inputs Except Preserved, retaining internal-only calculations and explicit Preserve dependencies. Support compatible constants shared across differently typed controllers.
- Add ALTO Trace And Optimize Objects to prune unobserved sensor declarations and redundant initial-value visibility/transform curves before AAO component collection and bone merging.
- Add object/component preservation through AAO's dependency API. Retain external-only sensor outputs, network/menu consumers, runtime transform writers and unsupported playback.
- Include VRCRaycast outputs and AAO Asset Description external parameter access in shared analysis.
- Add ALTO Material Atlas with Reference Material groups, explicit texture mappings, resolution priorities and optional TexTransTool 1.0.2 integration.
- Separate early reference-material conversion from final texture packing. Convert existing renderer inputs and dependent material-swap assets before generators discard incompatible shaders, preserve source identity through generated copies, and validate final animation and texture inputs. Provide explicit Before Generators / After Generators timing; source materials and controllers remain unchanged.
- Sample texture tiling and offsets into build-owned atlas inputs, isolate shared materials outside the selected scope, and preserve AAO removal-mask UVs through its public API.
- Preserve compatible numeric shader animations during atlas conversion, including complete Color/Vector channel bindings. Report texture-coordinate animation, lost protected outputs and unsupported input formats.
- Ignore the unused serialized Y parameter of Simple1D Blend Trees in dependency analysis. Proven constant inputs do not spread dynamic-output protection to unrelated material conversions.
- Isolate unsaved Animator graphs during build analysis using native graph constructors and serialized reference remapping.
- Add ALTO Mesh Reduction with optional Meshia 3.2/3.x integration, scope-wide triangle budgets, editable Descriptor-based preservation, priorities, minima and border preservation.
- Budget surviving geometry after AAO cleanup and follow renderer replacements through its public mapping API. Conservatively retain merged protected or out-of-scope inputs and report actual counts when a target cannot be reached.
- Preserve skinning and BlendShape layouts during reduction, retain unsupported mesh inputs, and reject overlapping Meshia reduction policies.

## 0.0.2

- Preserve Color and Vector animation channels during material constant baking. Removing individual channels could zero brightness, gamma, or alpha in Unity's material property blocks, producing black or invisible clothing.
- Add regression tests that compare evaluated Animator material output before and after optimization.

## 0.0.1

- Introduce Advanced Avatar Optimizer with independent Advanced Trace And Optimize Animator and Advanced Trace And Optimize BlendShapes components.
- Extract shared parameter-value, state-reachability and animation-curve analysis.
- Remove unreachable states and transitions while preserving Write Defaults binding coverage.
- Eliminate unused Animator parameter curves, including unobserved feedback cycles.
- Simplify provably constant static Blend Trees and merge compatible same-weight clips.
- Bake proven invariant material properties into build-owned material copies.
- Retain BlendShape freezing, zero-reset grouping, MMD protection, renderer exclusions and per-scope animation exclusions.
- Allow Avatar Optimizer to merge renderers carrying exclusion markers without a RequireComponent removal conflict.
- Use the new package ID io.github.noname0310.advanced-avatar-optimizer and renamed component types and assemblies. No legacy package or type aliases are provided.
