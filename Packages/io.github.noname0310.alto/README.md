# ALTO — Avatar Link-Time Optimizer

Static analysis-based optimization for VRChat avatars.

ALTO uses constant propagation, reachability and dependency analysis to optimize avatar logic, meshes, materials and physics through NDMF.

The package provides **ALTO Trace And Optimize Animator**,
**ALTO Trace And Optimize BlendShapes**, **ALTO Animator Parameter
Policy**, **ALTO Parameter Layout**, **ALTO Parameter Compression**, **ALTO Build Exclusions**, **ALTO Rendering Filter**, **ALTO PhysBone Filter**, **ALTO Contact Filter**,
**ALTO Trace And Optimize Objects**, **ALTO Material Atlas**,
**ALTO Mesh Renderer Settings Override**, **ALTO Mesh Optimization Policy**, **ALTO Mesh Reduction**, **ALTO Remove Occluded Mesh**,
**ALTO Trace And Optimize Meshes**,
**ALTO Trace And Optimize Materials**, **ALTO Trace And Optimize PhysBones**, and
**ALTO Trace And Optimize Expressions**.
The optimizers share conservative parameter-value, dependency and
state-reachability analysis.

## Installation

Install **ALTO — Avatar Link-Time Optimizer** from the Noname0310 NDMF Tools
VCC repository. Its package ID is `io.github.noname0310.alto`.
The VPM manifest replaces the previous Advanced Avatar Optimizer and
Trace And Optimize BlendShapes packages to prevent duplicate scripts.
For a manual or local UPM installation, remove those previous package entries
before adding ALTO. Script GUIDs are retained from Advanced Avatar Optimizer;
component and assembly names now use ALTO.

### Avatar setup installer

Open **Tools > Noname > ALTO > Installer** and select an avatar instance in a scene
or Prefab Mode. **Simple** is the default mode: work through Setup, Features,
Physics, Meshes and Review. Choose whether AFK expressions may change, which
controls and gimmicks to retain, and which PhysBones matter to you. Body meshes,
full-detail face meshes and explicit overlay deletion regions remain editable,
including for avatars without a preset. **Advanced** exposes the complete Setup,
Parameters, Features, Meshes and Materials panels. Switching modes retains all choices.
**Review > Install on Avatar** adds a connected `LinkTimeOptimizationSettings`
prefab instance from the bundled template, then records your choices as prefab
overrides. Review shows missing references and dependencies
before installation. The entire addition can be undone in one step. Source
meshes, materials and animations are unchanged; optimization runs on build copies.

The instance keeps its link to **Editor/Installer/Templates/LinkTimeOptimizationSettings.prefab**
in the package. Avatar references, changed values and removed optional features
are instance overrides; the template contains reusable defaults without avatar
references. Unmodified properties continue to inherit the template. Adjust the
instance in the Inspector and save or apply it as a nested instance in your avatar
prefab as needed. An avatar with existing ALTO
settings cannot be installed again; **Select Existing Settings** locates its
current configuration. The installer does not overwrite or migrate that setup.

Each panel groups its options in **Common**, **Mobile** or **Cross-platform**
frames with explicit platform labels. **Optimize Mobile Only** limits general
optimization to Android and iOS. Mobile simplification separately chooses which
features, controls and geometry to keep. Parameter layout and automatic
compression still apply across platforms so PC uploads can establish their shared
synchronization contract. The installer uses a 256-bit synchronization target;
it does not expose a custom bit budget. General optimization is required for
mobile simplification's cleanup and mesh-merging passes. Choose **Optimize Mobile
Only** when PC should keep its original optimization behavior. Synchronization
settings are placed outside the Common platform filter and can be disabled
separately; compression requires parameter layout.

Every scoped setting explicitly targets the selected avatar root. Body-mesh
occlusion targets default to all BlendShapes as candidate regions; explicit
geometry deletions remain a separate selection. The installer suggests meshes
used for descriptor lip sync and eyelids as meshes to keep at full detail during
triangle reduction. Review this list for your avatar. Material atlasing requires
the supported TexTransTool integration; triangle reduction requires Meshia.
Installation itself does not build the avatar or predict a final performance rank.

Selecting the avatar automatically discovers AFK BlendShape clips and fills the
initial full-detail mesh list from the descriptor's lip-sync and eyelid references.
It also suggests a body mesh for hidden-region removal, with all BlendShapes
selected. Body discovery ranks renderer, mesh and material names and body-region
BlendShapes; facial references reduce confidence without excluding unified
face/body meshes. Clothing names reduce confidence. Only a clear leading match
is selected; ambiguous, unnamed or split-body setups may need manual selection.
The suggestion is an editable Installer heuristic, not an optimization proof.
These are initial suggestions: editing the lists is allowed and does not trigger
rediscovery. AFK discovery uses the built-in `AFK` parameter rather than clip or artist names.
It compares state-machine reachability with AFK variable and always false,
including intro, loop and exit paths, nested machines, overrides and supported
Blend Tree branches. Descriptor layers, child Animators and serialized controller
references in avatar components are inspected without modifying their assets.
Enable **Allow AFK expressions to change** to review and use the candidate
checkboxes before installing. **Refresh AFK Animations** updates them after
animation setup changes and retains your choices for unchanged candidates. Clips also reachable outside
AFK start unchecked because excluding them may change other expressions too.
Authoring discovery cannot see controllers generated only at build time or
guarantee indirect parameter-driven AFK paths; use manual animation exclusions
when needed. The installer rechecks candidates immediately before installation.

Known optional authoring components for Light Limit Changer, marshmallow PB,
Pudding and RealSweat are initially added to mobile component exclusions by their
full type names. Their packages are not dependencies of ALTO, and renamed objects
still match. Remove any suggestion you want to keep. This stops the selected
components' build processing; already-installed output can additionally be
excluded by object or processed by mobile rendering/dependency cleanup. Unrecognized
features can always be added manually. Explicit exclusions can reduce both surviving
content and analysis work. **Add Detected Setup Components** refreshes suggestions
on request; ordinary UI refreshes do not restore entries you removed.

PhysBones are never selected automatically. The Simple Physics panel provides a
searchable checklist and controlled-bone hierarchy selections for generated physics.
**Hide Inactive PhysBones** starts enabled, hiding disabled components and inactive
owners or controlled roots. This only filters the Editor list, never the keep list;
animations may change activity at runtime. With Keep Listed and no selections, all
mobile PhysBones are removed. The final resource budget is evaluated during builds.

**Presets/Shinano Face Overlays.asset** identifies the original Shinano face and its
nine overlay BlendShapes. It contains only names, a SHA-256 digest and a 256-bit
distribution signature: no vertex samples, triangles, UVs, morph deltas, bones or
avatar asset references. Base-position identification ignores vertex order, duplicate
positions from seams, BlendShapes and skinning. The original Shinano and the tested
Fermata face share this exact fingerprint despite different vertex counts. A unique
exact match initializes deletion regions. Similar geometry only produces an editable
suggestion that requires **Use These Preset Regions**; signature agreement is not
a percentage of allowable position error. All named regions must exist, ambiguous
exact matches require selection, and unmatched meshes remain available for manual setup.
Create additional presets with **Assets > Create > Noname > ALTO > Mesh Region Preset**:
enter region names and use **Add Reference Fingerprint**. The temporary reference
mesh is never saved in the preset. Presets are authoring suggestions, not evidence
that removing a region preserves appearance.

The package includes **Presets/Mobile Toon Standard.mat**, a reference material
using the SDK's VRChat/Mobile/Toon Standard shader, white tint and culling off.
It contains no avatar textures and can also be assigned directly to a Material
Atlas group's Reference Material. The existing per-group **Atlas Size** field
continues to control the output texture dimensions.

## Platform selection

Policies that intentionally remove features default to **Android**. The
Animator, BlendShape, object, mesh, material and expression optimizers do not expose a platform selector.
**Apply On Platforms: All** still respects MA Platform Filter on the settings
object and its ancestors. MA's filter selects VRSNS platforms such as VRChat;
Apply On Platforms selects PC/Android/iOS. Both conditions must permit the build.
The optional integration uses Modular Avatar 1.18.x's public component API;
Modular Avatar is not a required dependency.

**ALTO Optimization Platform Filter** limits ALTO settings on its object and
descendants to selected PC/Android/iOS platforms. Nested filters intersect and
disabled filters are ignored. It does not remove objects, hide meshes or disable
components from other packages. The installer uses an Android+iOS filter on
**Common** when **Optimize Mobile Only** is selected; it is separate from MA's
VRSNS filter and from each policy's own platform selection. All applicable
conditions must permit a setting to run.

## ALTO Build Exclusions

Add **Noname > ALTO Build Exclusions** inside an avatar. **Excluded Objects**
removes selected subtrees before NDMF generators run. **Excluded Components**
removes individual components while keeping their objects. An empty **Scope
Root** uses the component's subtree; assign the avatar root when placing the
settings on a separate empty object. Remaining skinning, constraint and
serialized component references are checked across the avatar before removal.
Conflicts with parameter-preservation sources are reported as build errors.
Freeze rules whose source object or component is excluded are skipped; they do
not prevent removal or become unscoped rules for a similarly named parameter.

**Build Failure Diagnostics** opens a separate panel, also available under
**Tools > Noname > ALTO**. **Analyze Build And Find Candidates** preprocesses a
fresh copy and searches captured stack signatures for build-only component
types. It does not infer candidates from error-message prose or asset names.
Multiple instances are shown separately because a stack rarely identifies an
instance. **Test Exclusion On Fresh Copy** adds the chosen candidate to the copied
filter and rebuilds from a fresh avatar; normal reference and preservation checks
still apply. A successful trial enables **Add To Filter**, the only action that
changes the source scene, with Undo. Nothing is added automatically. Tests use
the current platform and do not retry or resume an SDK upload. A passing trial
proves preprocessing completed, not that removing the feature preserves behavior
or passes asset-bundle and upload validation.

**Remove Audio Sources** explicitly removes sources present before generators
run, along with Unity audio filters and SDK spatializers whose outputs are all
removed. It is disabled by default. **Preserve Audio Sources** keeps selected
sources; explicit exclusions still apply. Dependants are removed first, and a
remaining reader or required-component conflict stops removal before mutation.
Audio created by a later generator remains that generator's responsibility.

## ALTO Rendering Filter

Add **Noname > ALTO Rendering Filter** inside the avatar. Its scope and platform
selection are independent of Build Exclusions. Rules are captured before early
exclusions and evaluated after generators. Explicit targets removed by Build
Exclusions are skipped. Rendering Filter does not remove authoring components.

**Material Exclusions** runs after generators, before texture atlasing and
mesh optimization. Select a Renderer, a Material, a slot, or their intersection.
Slot `-1` selects all matching slots; a completely empty rule does nothing.

**BlendShape Exclusions** is a central list of Skinned Mesh Renderers and named
morph regions. Use **Select BlendShape Regions** to search and check morphs.
A triangle is removed when any of its vertices moves beyond the displacement
threshold in any frame of a selected morph. Current morph weights and visibility
do not affect selection. Empty selections remove nothing. Overlapping rows and
morphs form a union; shared source meshes remain unchanged on other renderers.
Filtering runs after generators, before morph freezing, atlas packing and
reduction. Vertex indices, remaining morphs and submesh slots initially remain
stable; ALTO Trace And Optimize Meshes can compact unused vertex data later.
Missing targets/morphs, Cloth and animated mesh references stop the build.

**Automatic Rendering Rules** can discard extra material passes or selected
Particle System, Line and Trail renderers throughout the scope. These are
explicit visual simplifications, disabled by default. They do not mean that
these renderer types are universally unsupported on Android. Referenced
components are retained; **Preserve Automatic Renderers** exempts individual
renderers from automatic rules. Explicit exclusions still apply to them.

When excluding a generated effect, also exclude its authoring postprocessor if
that processor requires the removed output in a later build phase. Use
**Excluded Components** for this configuration; arbitrary editor code cannot be
inferred from runtime Animator parameter analysis.

- **Remove Material Pass** removes extra slots beyond the mesh's submesh count,
  such as an overlay drawing the final submesh again.
- **Remove SubMesh Geometry** removes the selected submesh and every material
  pass drawing it, including overlays. Other submeshes and BlendShapes remain.

Remaining material-slot animation bindings are remapped. A material-only rule
with animated swaps to different materials requires an explicit slot selection.
Animated mesh references and shared clips needing incompatible remappings are
reported instead of being rewritten ambiguously. If another generator merges a
selected renderer before this pass, select its destination renderer.

This component changes build copies and has no continuous preview. Use ALTO
Trace And Optimize Expressions for subsequent menu/local-parameter cleanup.

## Organizing settings

Settings may be grouped under `Common` and `Mobile`, with child objects
for exclusions, policies, filters, materials, geometry, optimization and parameters.
Add a `PC` group only when desktop-specific settings are needed.
These names organize the hierarchy; they do not select a platform or control
NDMF execution order. Set **Apply On Platforms** to **Android + IOS** for shared
mobile policies, **PC** for desktop policies, and **All** for common policies.
Keep whole-avatar optimizers and the cross-platform Parameter Layout and
Parameter Compression together under Common.

Assign **Scope Root** explicitly before moving a scoped component: an empty
scope follows its containing object's subtree. Each component retains its own
platform and scope selection. Some components participate in more than one
build phase, so the hierarchy represents configuration categories rather than
an executable list. Material Atlas can also convert materials before generators
when **Material Conversion** selects that stage.

Older **ALTO Build Filter** components remain supported with both sets of rules.
New setups use the two separate components. Use only one active early exclusion
policy and one active rendering filter per avatar and platform; a legacy Build
Filter occupies both roles. PhysBone Filter and Contact Filter remain separate
because they filter generated simulation/input components, not authoring objects
or rendering output. Later optimization removes objects only when no retained
consumer needs them.

## ALTO Parameter Layout

Add **Noname > ALTO Parameter Layout** anywhere inside an avatar. It always
controls the whole avatar's synchronized Expression Parameters. **Reference
Platform** defaults to PC. A successful SDK upload on that platform records the
final wire parameters, types, order and any integer or time multiplexing encoding. Other platforms use the matching
Blueprint ID's record, filling missing slots with unused padding declarations.
Padding never reuses the removed logical input's name or adds Animator bindings.
Surviving parameters retain their own defaults, Saved settings and local behavior.

**Additional Parameters** defaults to **Error** when another platform introduces
synchronized names absent from the reference upload. **Keep Local** explicitly
disables synchronization for those additional inputs. Type conflicts, duplicate
names, missing records and changed reference platforms are errors. An empty
confirmed layout is a valid empty contract, not an instruction to select all inputs.

Records live in `ProjectSettings/ALTO/ParameterLayouts`, one
JSON file per Blueprint ID. Keep them with the project when switching machines;
**Show Recorded Layout** opens their location. Analysis, manual baking, build
failures and cancelled/failed uploads do not replace confirmed records. The
record is committed only after the SDK confirms the reference upload succeeded.
If that platform is successfully uploaded without an enabled layout component,
its old record is invalidated. Other platforms must wait for a new reference
upload with the component enabled. Uploading another platform leaves it intact.

This implementation uses NDMF and public VRChat SDK callbacks. It does not depend
on another tool's parameter alignment or cache. It understands ordinary named
declarations and this package's recorded integer and time multiplexing encodings. Other encoded network
protocols are not inferred. A late processor that changes real synchronized
declarations is rejected. A replacement for this build's own padding is accepted
only at the same wire position and type, with no Animator, menu, sensor or external
reader. Rebuild the reference upload with matching transport settings when real
declarations conflict. Generated
names must be stable between platforms; name changes are not guessed from prefixes.

Layout alignment preserves the reference platform's synchronization cost, including
padding slots. Layout alone does not compress parameters. When disabling another tool's
compression, check that the reference avatar still fits the SDK parameter budget
before recording a new upload.

## ALTO Parameter Compression

Add **Noname > ALTO Parameter Compression** together with **ALTO Parameter
Layout**. Both components control the whole avatar. The reference-platform upload
records the wire contract; other platforms reuse it, including the exact generated
names and bit order. No VRCFury, MA or AAO implementation is required.

**Integers** explicitly selects synchronized Int inputs and an inclusive transmitted
range within 0..255. An optional source resolves names through NDMF remapping. For
example, 0..7 uses three Bool slots instead of eight bits. Ranges may contain 2 to
128 values; larger ranges cannot save bits. Empty lists select no integers. Menu
values are never taken as proof that OSC cannot supply other values.

The local logical parameter keeps its name, Int type, default and Saved setting,
so menus and OSC continue to use it. Generated Bool slots are not Saved. The owner
encodes the local value and the remote Animator reconstructs it. Values outside
the selected range are clamped **only in the transmitted representation**. Each
integer adds one FX layer. Independent bit delivery can temporarily decode an
intermediate value, and state evaluation adds latency; this is not atomic Int
synchronization. The integer codec does not reduce Float precision.

**Time Multiplexing** defaults to **Automatic By Priority**. On the reference
platform, including PC, it runs only when the remaining synchronized cost exceeds
**Target Bit Budget** (256 by default). It considers persistent menu inputs first,
then internal computations, runtime-driven values, and inferred external inputs
last. External influence propagates to derived parameters, including controller
inputs that are not Expression Parameters. Known momentary controls and their
dependent computations remain native, as do explicit preservation and built-ins.
An initialization-only Parameter Driver Set does not establish an internal input.

Selection stops at the first priority tier that can fit the budget. Within that
tier, it first finds the shortest supported cycle, then removes candidates only
when that cycle still fits. The resulting subset is inclusion-minimal at that
cycle length, rather than a global subset optimum. It does not
compress a later tier merely to shorten the transmission cycle. Inputs already
selected from earlier tiers stay selected. The build report records each decision
and warns if inferred external inputs need multiplexing.

The classification uses final menus, sensors and Animator dataflow, without facial
asset names or parameter-prefix tables. It is an inference, not a guarantee about
OSC: an external application can also drive an ordinary menu input. **Keep Native
Parameters** remains available when an input must never incur multiplexing delay.
Native inputs use ordinary platform synchronization; they are not delay-free.

**Automatic Menu Inputs** is a stricter alternative that selects only persistent
Toggle and Radial Puppet inputs while excluding momentary controls, activation
inputs, sensor-driven computations and protected parameters. Existing serialized
selection modes remain unchanged. **Selected Inputs** accepts explicit names,
prefixes and source references; an empty selection selects nothing.
**All Synchronized Inputs** explicitly selects all synchronized user inputs,
including inputs without menus, sensor-driven values and momentary controls.
This mode permits snapshot delay and missed brief changes; it is never enabled
automatically to resolve a budget error. **Disabled** keeps only the integer and
unused-synchronization features. Selection never
overrides preservation. If the eligible inputs cannot meet the budget, the build
reports an error instead of compressing protected inputs.

The build report lists exclusion reasons per synchronized input and groups the
remaining bit cost by its primary reason. These diagnostics remain available when
budget validation fails, including preservation, momentary controls, dynamic
computations, external access and inputs without a persistent menu control.
Writer influence follows each verified owner/remote execution context. A private
curve or Driver in another instance cannot affect a menu input merely because
both instances use the same temporary parameter name. Uncertain ownership falls
back to conservative analysis.

The owner snapshots selected logical values and sends batches through shared
Float, Int and Bool slots. A single generated FX layer buffers received batches
and applies a complete cycle together. Float payloads use native Float slots,
without conversion to Int. Logical names, menu/OSC bindings and Saved values stay
local. Both codecs retain existing Bool, Int or Float `IsLocal` declarations and
route the SDK's 0/1 identity accordingly; unsupported Trigger identity is rejected
before controller changes. Missing batches restart reception, and interrupted partial cycles expire.
**Packet Hold Seconds** controls the preparation and publication intervals; the
reported cycle time also needs Animator frames and network delivery time.

This deliberately trades remote response time for network bits. Brief changes
between snapshots may not be observed remotely. Preparation holds tolerate
staggered delivery; arbitrary packet delays, reordering and client scheduling
cannot be treated as atomic transport. Keep event-like or responsive inputs native.
Native Unity regression tests model Parameter Drivers and packet delivery; they
do not replace VRChat client networking validation.

**Keep Native Parameters** accepts names, prefixes and controller/object sources,
and protects their upstream computations. Resolved Animator Parameter Policy
preservation also takes priority; selecting the same input for compression is an
error. This list is optional in Automatic By Priority; use it for inputs whose
latency requirements cannot be inferred or which must stay native even if the
budget would otherwise require external-input compression.

Automatic By Priority also captures Preserve selections from visible enabled
Animator Parameter Policies, including policies whose Apply On Platforms excludes
the reference build. The network representation is shared: an Android native-input
requirement must be respected when PC chooses the contract. This does not enable
that policy's freezing or object changes on PC. Policies removed by platform
filters or other generators cannot be discovered there; replay still rejects
conflicting preservation rather than changing the recorded wire protocol.

**Remove Unused Synchronization** runs after final hierarchy/controller cleanup.
It makes declarations local only when the dependency graph has no surviving
consumer. Explicit preservation, sensors and registered external observers remain
live; uncertain runtime behavior prevents pruning. Logical declarations remain
available locally, including OSC outputs. A menu writer alone is not a consumer.

When an integer-encoded feature is absent or local-only on another platform, that
platform receives anonymous wire padding without a decoder. Surviving integers
use their own defaults to initialize the bits. Multiplexed platforms reuse the
recorded batches and slots, decode only surviving inputs, and still transmit
frozen/default values for omitted features that a remote PC avatar can observe.
Confirmed records are never changed by analysis or failed uploads.

If a later layout tool re-enables a known local input at its original reference
padding slot, finalization restores both the local declaration and the anonymous
wire. This requires matching slot provenance, type, default and Saved metadata.
Local consumers are retained. Unknown aliases or changed local declarations are
not treated as harmless padding.

An over-budget transport is prepared before late SDK processors inspect the cost.
The final SDK pass still rejects external synchronized-declaration rewrites;
disable conflicting compression/layout features in other tools. No private tool
API, cached protocol or dependency is used. A smaller declared network budget does
not imply lower Animator CPU cost: multiplexing adds states, buffers and Drivers.

## ALTO PhysBone Filter

Add **Noname > ALTO PhysBone Filter** inside an avatar. It defaults to
Android and **Keep Listed** with an empty list. An empty keep list removes all
scoped PhysBones; existing serialized filter modes remain unchanged. Select individual **PhysBones**
or the **Bone Roots** they control. Bone-root selections follow existing bones
through reparenting and also match PhysBones created during the build. They do
not infer ownership from arbitrary generator prefabs. Only components inside
Scope Root can be removed.

**Keep Listed** removes every other scoped PhysBone after generators run;
an empty selection removes all scoped PhysBones. The filter keeps bone
Transforms and skinning references. Remaining PhysBones retain their nested
Ignore Other PhysBones boundaries. Unsupported animated hierarchy boundaries,
preserved parameter outputs and component-reference conflicts stop the build.

**Remove Unreferenced Colliders** protects colliders used outside the scope,
global colliders, animated global settings, animation object references and
**Preserve Colliders**. The **Budget** uses avatar-wide SDK component, transform,
collider and collision-check counts after optimization. Set any limit to `-1`
to disable it; choose a warning or build error when a limit is exceeded.
These checks do not replace the SDK's final upload validation.

## ALTO Contact Filter

Add **Noname > ALTO Contact Filter** inside an avatar. It defaults to Android
and **Keep Listed**. Empty **Senders**, **Receivers** and **Object Roots** lists
remove every scoped Contact Sender and Receiver. **Remove Listed** instead
removes only selected Contacts; empty lists remove nothing.

Individual selections follow NDMF-registered component replacements. Object
Roots selects the objects holding Contacts, including their descendants when
**Include Children** is enabled. It follows reparented objects and newly generated
Contacts under them; it does not select by a Contact's Root Transform anchor or
infer arbitrary prefab ownership. An empty Scope Root uses the component's subtree.

Filtering runs after generators, is rechecked after external optimizers and late
SDK processors, and removes exclusively orphaned animation bindings while
preserving clip timing and shared bindings to retained Contacts. Preserved
Receiver parameter inputs, incoming component/object-curve references and
required-component conflicts stop the build. The filter deliberately removes
selected Senders even when another avatar could receive their signals.

Transforms and parameter declarations are left to the separate object, Animator
and Expression optimizers. Removing Contacts does not promise to remove a whole
feature: surviving meshes, audio, animation outputs or public parameters can still
keep it alive. This component has no continuous preview.

## ALTO Trace And Optimize Objects

Add **Noname > ALTO Trace And Optimize Objects** inside an avatar. An empty
**Scope Root** uses that object's subtree; analysis covers all avatar controllers.
The component simplifies redundant animation outputs and traces dependencies
backward from observable runtime outputs. Object/component collection works
without AAO Trace And Optimize. Static bone merging is still separate from
collection; the optional AAO integration respects existing AAO settings.

After external optimizers and mesh changes, object and Animator cleanup repeat
until a complete iteration removes nothing. For example, removing an unused
material output can make a state machine, its Contact input and a supporting
constraint chain unobservable in successive iterations. The selected options,
scope and preservation rules still apply on every iteration, including when an
external optimizer has removed the settings component. The build report records
convergence; generators, atlasing, occlusion sampling and mesh reduction run only
at their normal stages.
Final SDK cleanup also treats removed menus and local declarations as dependency
changes. It revisits converged Expression analysis only when subsequent object or
Animator edits invalidate it, while still collecting multi-step dependency chains.
Avatar-owned readers introduced by late SDK generators also establish sensor
usage history before cleanup. Same-named private child Animator inputs and
unknown controller owners do not establish a former avatar sensor consumer;
external-only sensors still require explicit input freezing.

- **Prune Unused Sensor Parameters** removes controller declarations for Contact,
  PhysBone and Raycast outputs whose previously observed local readers became
  unused, or whose inputs were explicitly frozen by the parameter policy.
  Current readers or writers, synchronized Expression Parameters, menus,
  Preserve selections and known external observers keep the declarations.
  Local Expression declarations are handled jointly when **Prune Unused Local
  Parameters** is also enabled. Otherwise they retain the sensor. Sensors without
  a former local reader or a freeze policy remain available to external tools.
- **Bake Constant Visibility** removes redundant Active/Enabled curves only when
  all possible outputs equal the existing initial value. It does not replace an
  initially active object with its eventual inactive state.
  Tracks under proven zero-weight Direct Blend Tree inputs can also be removed
  without changing the tree's other outputs or clip durations. LOD groups,
  particle stop actions and auto-destroying trails retain their visibility targets.
  A full-weight FX override can prove the final visibility even when lower layers
  write different values. Known immediate transitions from Any State or the
  initial state are considered, including root Exit/Entry transitions. States,
  callbacks and clip timing remain intact. Missing upper-layer tracks can expose
  lower outputs even with Write Defaults enabled; they are not assumed constant.
  Delayed, masked, weight-controlled or uncertain paths remain unchanged.
  Use the explicit **Bake Initial Visibility** policy below to allow that change.
- **Bake Constant Transforms** applies the same rule to complete local position,
  rotation or scale groups. Humanoid bones, physics and constraint targets,
  runtime transform references and ambiguous bindings are retained.
  Invariant-output analysis also considers the contributing motions of a 1D
  Blend Tree whose input is proven constant. Unselected motions do not prevent
  removing redundant curves. Fixed 1D interpolation can also prove that distinct
  scalar position/scale curves produce the original value, including a single
  full-weight writer inside a Direct tree. Quaternion and discrete properties
  do not use scalar interpolation. Near-identity scales are not rounded to identity.
  Dynamic or preserved inputs and ambiguous child settings remain conservative.
- **Remove Unused Transform Outputs** traces runtime consumers back through
  parent transforms, skin bones, physics chains, serialized references and
  Unity/VRC constraint sources. Remove unobserved constraints and their unused
  transform/activation tracks. Animation writes alone do not keep a target alive.
  An empty or missing SDK constraint target means its own Transform. Its actual
  consumers determine whether the constraint survives; an empty serialized
  reference does not make it an external output that must always remain.
  Shared clips, animated object references and preserved inputs are considered;
  currently disabled renderers and contacts still count as potential consumers.
  A final pass also checks dependencies after external optimizers.
- **Remove Redundant Constraints** removes a full-weight local scale constraint
  when its static source and identity offset reproduce the target's existing
  scale on every affected axis. Non-neutral and collapsed scales are preserved.
  Animated settings, other scale writers, external sources, world-space solving,
  component references, scope boundaries and explicit preservation retain it.
  It also removes a zero-weight parent constraint whose At Rest pose equals the
  existing local pose, and replaces full single-source parent following with
  actual parenting when the source-to-target relationship is equivalent.
  Sources may keep moving. Fixed positive uniform scales on the separate ancestor
  branches, zero positional offset, unchanged settings, activation and initial
  pose, and no competing target writer are required. Child animation paths are
  remapped by NDMF. Nonuniform branch scales and world-distance offsets remain
  constrained; ordinary parenting would otherwise change their behavior.
  Constant zero-weight sources are also removed from the six SDK constraint
  types even when other source weights or offsets are animated. Each removed
  source must have a proven constant zero weight. Surviving weight and parent
  offset tracks are remapped together, retaining curve timing and interpolation.
  Shared or unresolved bindings, immutable clips, animated source references or
  list structure, and unproven lock/freeze settings prevent compaction. At least
  one valid initially positive-weight source is kept. Overflow sources follow
  the SDK's sixteen-slot animation limit; unsupported bindings are not activated
  by compaction. Unused source hierarchies can then be collected by
  **Remove Unused Objects**.
  When one remaining source has a fractional or animated weight, a zero-weight
  alias to that same source preserves the SDK's multi-source normalization.
  This avoids retaining an unused bone solely to keep the source count above one.
- **Remove Unused Objects** collects unused components and object subtrees after
  external generators finish. Permanently inactive renderers can be removed;
  animation that might enable them prevents removal. An inactive object used as
  a skin bone or constraint source survives even when its renderer is removed.
  Referenced PhysBone colliders, potentially active global colliders, immutable
  clips and indirect references remain valid. A disabled global collider can be
  removed when reachable Animator outputs cannot enable it and no other consumer
  references it. Contact receivers require a previously observed parameter to
  become unobservable before their components can be removed.
  Preserved parameters remain dynamic; they do not keep a permanently inactive
  renderer alive solely because a mutable clip also animates that renderer.
  The target Transform and any still-observable outputs remain available.
  Raycasts are collected only when their result Transform has no surviving
  consumer and every named parameter output was proven unused. Skin bones,
  visible children, constraints, external result targets and animated result
  references retain the Raycast. An unproven parameter output retains it too.
  A child Animator without a controller or bound Playable can be collected with
  an otherwise unused subtree. The avatar's root Animator, generated controller
  owners and animation boundaries with any surviving descendant remain intact.
  Controller swaps, indirect references and explicit preservation retain it too.
  Unity audio filters depend on their AudioSource or AudioListener outputs;
  SDK spatializers depend on AudioSource outputs. Unreferenced orphan effects
  and their unused placement hierarchies can be collected after a generator
  removes the source. Audio outputs, filter ordering, independent reverb zones
  and explicitly observed or preserved effects remain intact.
  SDK builds recheck final objects and Animator outputs independently of menu,
  local-parameter and sensor cleanup options. Completed `IEditorOnly` /
  `INDMFEditorOnly` metadata does not keep an unused runtime hierarchy alive.
  Explicit preservation, out-of-scope references and runtime consumers still
  retain their dependencies. Required components are removed in dependency order.
  Parameter preservation retains the parameter's dynamic values, declarations,
  required computations and observable outputs. It does not keep otherwise unobserved
  transform writers or orphan components alive. A shared clip can retain its
  visible face output while losing bindings to a collected sensor hierarchy;
  clip duration remains unchanged. Use object/component preservation for a
  hierarchy that must remain even without a runtime consumer.
- **Preserve Objects** protects selected subtrees, including children moved by
  build generators. **Preserve Components** protects individual components.
  The optional AAO integration also protects these targets from its automatic
  collection and bone merging. Preserving a Renderer also keeps it separate
  during AAO automatic renderer merging; texture atlasing can still include it.

The SDK output boundary runs at preprocess order `int.MaxValue - 1`, after
generators and before runtime initialization. Integrations which generate outputs
must finish before that boundary. Direct NDMF bakes keep external authoring
references for subsequent build processors. The final SDK pass removes outputs;
hierarchy reparenting stays in the NDMF stage with its animation path remapper.

Unknown runtime scripts, unindexed playback or unverified parameter mappings
prevent destructive analysis. Editor-only build components are not runtime
writers. Additive or masked animation, unsupported blend weights and animation
events retain the affected outputs. Clip durations, initial transforms and
visibility remain unchanged. Contact Senders are not removed merely because
no local Animator reads them.

The optional AAO integration reads external parameter declarations from Asset
Description assets. Unsupported declaration formats retain outputs and produce
a diagnostic. Arbitrary external code cannot be inferred; use Preserve for
additional interactions. Expressions handles menu/local-parameter cleanup.
Network layout changes and removal of paths made dead by material conversion
are separate stages. In particular, deleting a PhysBone
does not prove that its entire generator's remaining hierarchy is unnecessary.

## ALTO Mesh Renderer Settings Override

Add **Noname > ALTO Mesh Renderer Settings Override** to deliberately unify
selected renderer settings before atlas generation, reduction and compatible
mesh merging. All override checkboxes start disabled.

- **Apply On Platforms** defaults to **Android**; PC, iOS and All are also
  supported. Ancestor Platform Filters apply even when All is selected.
- **Scope Root** selects the subtree. Empty uses the component's object.
  Inactive and build-generated MeshRenderers and SkinnedMeshRenderers are included.
- **Override Probe Anchor** enables **Automatic** or **Explicit** selection.
  Automatic chooses the most common retained anchor; ties use avatar-relative
  path order. If none exists, it creates one at the combined bounds center,
  attached to the avatar root. Explicit requires a retained transform and never
  falls back when the reference is missing.
- **Override Light Probe Usage**, **Override Light Probe Proxy Volume** and
  **Override Reflection Probe Usage** independently select probe settings.
  Enabling the proxy-volume override with an empty reference explicitly clears
  that reference; automatic volume lookup can still prevent merging.
- **Override Shadow Casting**, **Override Receive Shadows** and
  **Override Static Shadow Caster** independently select shadow settings.
- **Preserve Renderers** leaves listed renderers unchanged by this component.
  An empty list preserves none. Object optimizer preservation is also respected.

Multiple instances can assign different fields, scopes or platforms. For example,
one can unify anchors on All platforms while another changes shadows and probes
on Android. Overlapping identical assignments are accepted; conflicting values
stop the build before renderer properties are written. Existing animation remains
active. These explicit changes can affect lighting and shadows. Mesh merging
still checks all remaining compatibility requirements and does not require AAO.

## ALTO Trace And Optimize Meshes

Add **Noname > ALTO Trace And Optimize Meshes** inside the avatar. Empty
**Scope Root** uses that object's subtree. All skinned renderers in scope are
considered, including inactive and generated renderers; analysis covers the
whole avatar. This pass runs after mesh reduction and external optimizers.
SDK builds also reconsider compatible groups after late generators have finished
and unused authoring references have been collected. This final pass uses current
native Animator paths and the same scope, preservation, visibility, shader and
renderer compatibility checks. NDMF-only manual bakes cannot include later SDK
processors.

- **Merge Compatible Skinned Meshes** combines groups with matching activation
  curves, Animator mask coverage and rendering settings. Coordinate systems follow Mesh Optimization Policy and
  culling volumes follow Expand Bounds; this is not a forced renderer count.
  Descriptor and MMD renderers remain the destination so their object paths,
  morph names and indices survive. Other animated morphs receive unique names
  and their clips are retargeted. Missing morph tracks remain ineffective.
  Animated **Update When Offscreen** is supported when its initial value and
  changing signals match. Proven default-valued outputs match unanimated inputs;
  changing outputs require **Expand Bounds**. Other missing renderer properties
  do not prevent merging when both serialized and native binding lookups confirm
  their absence. Existing unsupported properties still keep renderers separate.
  Different anchors can also match when static analysis proves their positions
  remain identical, or when both probe systems are off. This never substitutes
  anchors at different sampling positions; use Mesh Renderer Settings Override
  when that lighting change is intended.
- **Expand Bounds** contains all input culling volumes in the destination root's
  coordinates at build time. Enabled by default. Disabling it requires matching
  bounds. A common fixed coordinate system can be proven through static helper
  transforms even when the root-bone objects are different.
- **Merge Identical Material Slots** coalesces adjacent equal material slots
  without moving them across intervening materials. It can run independently
  of renderer merging.
- **Remove Unused Mesh Data** removes vertices referenced by no submesh and
  unused or duplicate bone-palette entries. Retained vertices keep their full
  skinning influences and every morph frame. It can also run without merging.
- **Rebind Static Bones** absorbs fixed local bone transforms into bind poses.
  Runtime animation, humanoid retargeting, physics, constraints and explicit
  preservation prevent substitution. A constraint with an empty or missing
  optional target still protects the Transform it controls on its own object.
  The original hierarchy stays intact;
  Objects can subsequently collect helpers with no remaining consumers.
- **Preserve Renderers** excludes selected renderers from this pass. The Objects
  component's renderer/subtree preservation also applies.

Full bone influences, multi-frame morphs and current weights are retained,
including zero-scale bones. Static-bone substitution adjusts bind poses without
inverting collapsed transforms. Source meshes and clips are never overwritten.
With Rebind Static Bones enabled, singular skinning inputs keep the scoped bone
coordinate systems alive across AAO's automatic bone merging, using its public
dependency API. Our later substitution and collection passes then process them.
This temporary dependency does not change AAO settings or authored Merge Bone
instructions, and is unnecessary when all scoped skinning inputs are invertible.
Unknown shader inputs, vertex/primitive/bone indices, transparent sorting, Cloth,
material property blocks, animated object references, relative/shared animation
roots and other renderer readers can prevent merging. The build report records
preserved inputs, the reason groups remain separate, and merge
counts. The component does not force different visibility or lighting settings to
match just to reach a renderer target.

Preserved parameters retain their behavior while mutable animation bindings can
move with a merged mesh. Immutable clips keep their original renderer paths.
Tracks addressing absent morphs on a fixed source mesh can be removed during
merging without shortening the clip. Missing names on the destination remain
reserved so appended morphs cannot accidentally activate those tracks.
Mask comparison ignores separate controllers that never animate the relevant
object type. All masks within a relevant controller remain significant, including
layers that can affect other bindings through Write Defaults. Object-activation
masks are checked independently from renderer masks.

## ALTO Mesh Optimization Policy

Add **Noname > ALTO Mesh Optimization Policy** to permit selected visual
changes during Trace And Optimize Meshes. It does not execute a separate
optimization pass. **Scope Root** selects the affected subtree (empty uses the
component's subtree), and **Apply On Platforms** controls all options together.
Set it to **Android + IOS** when sharing a Mobile configuration. Policies can
also apply on PC. Scope and platform selection still respect ancestor filters.

Without a matching policy, the mesh optimizer uses conservative coordinate and
material-order rules. A more-specific scope replaces a broader policy, including
when every option in the local policy is off. Equally specific policies must
agree on the active platform. A policy does not broaden the optimizer's scope,
activate a disabled optimizer, or bypass Preserve Renderers and animation,
shader, lighting, skinning or external-reference checks.

Generated renderers in scope are included. When an external optimizer combines
inputs from different policy scopes, the output receives only permissions shared
by every original input. ALTO also keeps that intersection after its own merges
and into the late SDK pass, so the destination object's location cannot grant
new permissions to geometry from another scope.

- **Allow Coordinate Space Changes** permits different coordinate roots, with
  **Expand Bounds** enabled. It is off by default and uses this policy
  component's shared platform selection. Object-space shader effects can
  change. Mirrored face winding and world tangent handedness are corrected;
  independent animated scale signs, toggles and rendering settings still split
  groups. Matching groups can grow after parameter specialization removes toggles.
  SDK Contact, PhysBone collider and Descriptor frame references do not by
  themselves imply scale changes. Their referenced transforms remain protected;
  actual animation, physics and unknown runtime writers are still considered.
  Head Chop remains a scale writer, but proven constant nonnegative settings do
  not imply a winding reversal. Such coordinate roots can merge when every skin
  bone follows the affected frame, including its collapsed pose. Animated or
  unverified settings, independent scale writers and outside skin bones retain
  separate groups. This does not make Head Chop bones eligible for static baking.
- **Convert Rigid Meshes** lets eligible MeshRenderers and unweighted skinned
  meshes join these groups, using their original transform as a rigid bone.
  Requires renderer merging and the coordinate-space relaxation on that platform.
  Mesh swaps, external readers and bindings that would become newly active remain
  protected. MeshRenderer morphs remain unevaluated; existing SMR morphs survive.

**Allow Material Slot Reordering** optionally merges equal materials across
intervening slots: `[A, B, A]` becomes `[A, B]`. It is disabled by default and uses the same **Apply On Platforms** selection
as the other options on this policy.
It works with **Merge Identical Material Slots**, including on a single renderer.
The first material stays first. Reordering can change overlapping surfaces,
stencil and depth effects. Animated material slots, unsupported shaders and
preserved renderers still prevent this transformation. Without this option,
only adjacent equal slots coalesce. Repeated adjacent slots do not by themselves
make numeric material-animation defaults incompatible.

The former per-option platform fields on Trace And Optimize Meshes have been
removed. Move previously configured coordinate, rigid-conversion and slot-order
permissions to this component when upgrading; there is no automatic compatibility
fallback. Common optimization settings remain on Trace And Optimize Meshes.

## ALTO Trace And Optimize Materials

Add **Noname > ALTO Trace And Optimize Materials** to process mesh and skinned
renderers in its scope, including generated renderers and safely resolved material
swap clips. It runs after external optimizers and before final mesh merging.

- **Bake LilToon Main Textures** optionally uses the installed lilToon baker to
  combine static main-texture color correction and supported secondary layers.
  It applies on PC and mobile, before atlas packing, and again for materials
  produced by external optimizers. Before Generators atlas conversion bakes its
  selected inputs before replacing the shader. Requires lilToon 2.3.4 or a
  compatible 2.x version; no hard lilToon dependency is added.
- **Remove Unused Saved Properties** removes obsolete serialized values and texture
  references from generated copies. All shader branches, includes, fallbacks and
  VRChat's Safety Fallback properties contribute to the usage analysis.
- **Share Equivalent Materials** shares materials with identical serialized render
  state, shader, keywords, tags, values and texture references. Names may differ.
- **Preserve Materials** retains selected material assets. Renderer/subtree
  preservation from Objects and external optimizer input mapping also apply.

Source materials and clips are never overwritten. Material Variants retain their
inheritance. Unknown shader inputs, external asset readers, immutable swap clips
and shared clips spanning the scope boundary are retained.

Baking is disabled by default. The build report identifies baked or retained
inputs. Main-texture and correction animation prevents baking; animated layer
settings can still leave an independent static color-correction bake possible.
Layer baking requires a compatible UV0 domain, static lighting participation,
LDR inputs no larger than the main texture, and no layer alpha replacement,
decal, dissolve, distance fade, AudioLink or view-dependent effects. Custom and
injected shader variants are retained. The generated texture uses the main
texture's resolution, wrapping, filtering and mipmap presence; original assets
are never saved over. As with lilToon's editor bake, finite texture sampling and
mipmap filtering are not pixel-identical to runtime composition. VRChat Safety
Fallbacks also receive the baked main texture, so their appearance can change.

## ALTO Trace And Optimize PhysBones

Add **Noname > ALTO Trace And Optimize PhysBones** to optimize physics in its
scope after generators and external optimizers. Empty **Scope Root** selects the
component's subtree. It runs on all platforms; intentional simulation removal is
configured separately with ALTO PhysBone Filter.

- **Share Equivalent Colliders** shares always-active local colliders with the same
  effective root and exactly matching serialized settings. Collision list order
  and repeated applications remain intact. Global or animated colliders and
  external/protected consumers prevent sharing.
- **Optimize Is Animated** disables rest-pose updates only when the complete chain
  has no external transform writers. Component and root ancestor scales are also
  checked. Overlapping physics, constraints and unknown runtime writers retain it.
- **Replace Unused End Bones** replaces unreferenced leaf transforms with an exactly
  matching virtual endpoint. Animated/skinned leaves, distinct offsets, changed
  forks, leaf rotation/scale, stretching and existing endpoints are retained.
- **Merge Compatible Simulations** combines independent simulations with identical
  constant settings under an identity parent. Original bone objects and skinning
  references remain. Currently requires identity sibling roots, Ignore multi-child
  mode and no grabs, posing, output parameters, curves, stretching, external readers
  or animated interfaces in the moved chains. Each group contains at most 128 real
  transforms, including its new root.
- **Preserve PhysBones / Preserve Colliders** retain selected components. Objects
  preservation and protected inputs mapped by an external optimizer also apply.

Only the build copy is modified. The report records changed flags, shared colliders,
replaced leaf transforms and combined simulations. Unsupported chains remain separate.

## ALTO Material Atlas

Add **Noname > ALTO Material Atlas** inside an avatar. It defaults to
Android. Empty **Scope Root** uses the component's subtree. **Target Selection**
defaults to **All Renderers in Scope**, including inactive objects and renderers
generated before the atlas pass. **Selected Renderers** uses **Target Renderers**;
an empty list in that mode selects nothing. The Inspector displays the effective
scope and selection rule. This stage
requires the optional TexTransTool **1.0.2** integration. Its public shader
texture-usage API is version-specific, so the integration is pinned to that
version.

Each **Merge Group** selects source materials and a **Reference Material**.
**Material Selection** explicitly chooses **All Unassigned Materials** (the
default) or **Selected Materials**. At most one group can select all unassigned
materials. Explicit selections take priority over that group. An empty Selected
Materials list selects nothing; adding or removing entries never changes the mode.
The output copies the reference shader and settings. **Texture Mappings**
transfer selected source textures, with `_MainTex` to `_MainTex` as the default;
other static source shader settings are discarded. Compatible numeric shader
animations keep their bindings and per-renderer values. Configure atlas size, padding,
compression, mipmaps and relative texture-resolution priorities per group.
Texture scale, offset and repeated UVs are sampled into build-owned inputs.

**Material Conversion** defaults to **Before Generators**. It creates reference
material copies and rewrites their dependent build assets before generators can
discard unsupported input shaders. Material-swap clips and Animator graphs are
copied; source assets remain unchanged. Texture mappings preserve source scale
and offset. Generator copies retain their source identity for material selection,
exclusions and resolution priorities. This timing changes shader-dependent
generator behavior. **After Generators** leaves source shaders available to those
tools, but cannot recover material references that they remove.

Texture packing always runs after generators, against their final meshes and
UVs. Materials first introduced by a generator are evaluated at that stage;
conversion cannot anticipate materials created by arbitrary generator code.
Before Generators rejects shared inputs that would also change unselected or
out-of-scope consumers. Material animations in that mode require the whole
avatar with All Renderers in Scope.

**Unsupported Inputs** retains the renderer by default, or stops the build when
set to **Error**. Animated texture/material swaps, mapped texture-coordinate
animation, incompatible shader property types, lost protected dynamic outputs,
Cloth, overlay slots and unsupported texture inputs are reported. Destinations
must be 2D textures. Normal-map import encodings and HDR inputs are not converted
by the 8-bit atlas path.
Inputs already converted before generators stop the build on a final conflict,
even with Preserve Renderer selected: retaining a partly converted renderer
would not restore its original shader behavior.
AAO mask removal retains access to original UVs through its public API.

Atlas generation runs during builds, with no continuous scene preview.
ALTO Trace And Optimize Meshes performs compatible final renderer and
material-slot merging; AAO is not required. A single atlas does not guarantee a
single renderer. **Existing Atlases** explicitly selects one of three policies:

- **Preserve** (default) retains overlapping TTT AtlasTexture components and keeps
  their shared material consumers out of ALTO conversion, including early shader
  conversion. This can leave multiple materials and shaders in the result.
- **Replace Fully Covered** removes an existing UV0 atlas from the build copy only
  when all its material consumers are selected. ALTO's group settings take over;
  this intentionally discards the original atlas settings. Partial overlaps and
  shared out-of-scope consumers are preserved. An incompatible surviving target
  stops the build rather than leaving a partially replaced atlas.
- **Chain** runs existing TTT processing first, then packs the completed textures
  and UV0 coordinates into ALTO's final atlas. It defers material conversion even
  when Before Generators is selected. Inputs discarded by earlier generators
  cannot be recovered. The final stage uses Unity's public rectangle packer,
  repacking complete intermediate images rather than their islands. Double
  resampling and unused intermediate image space can reduce texture quality and
  packing efficiency. Existing overlapping atlases must target UV0. Existing
  material merges must not collapse different ALTO groups or selected/unselected
  inputs into one material; such ambiguous mappings stop the build.

All policies operate on build copies. Inspect the build report for preserved
renderers, replaced atlases and final slot counts.
If an earlier generator clears every material from a surviving input renderer,
Preserve and Chain stop with an input-loss diagnostic before object cleanup can
hide the problem. They cannot promise mobile shader compatibility while retaining
an existing PC shader. Choose Replace Fully Covered with Before Generators when
the final reference shader must already be available to earlier tools.

## ALTO Mesh Reduction

Add **Noname > ALTO Mesh Reduction** inside an avatar. It defaults to
Android and requires the optional Meshia Mesh Simplification **3.2.x / 3.x**
integration (validated with 3.2.0). Empty **Scope Root** uses the component's
subtree. Use one enabled reduction policy per avatar and platform.

**Triangle Budget** counts all surviving mesh renderers in scope, including
preserved, unselected and disabled renderers. The default 40,000 is a configurable
target, not an upload limit. **Target Selection** defaults to **All Renderers in
Scope**. **Selected Renderers** uses **Reduction Targets**; an empty list selects
no geometry for reduction, while all scoped meshes still count toward the budget.
**Preserve Renderers** excludes geometry from reduction;
adding the component initially selects Descriptor eyelid and viseme renderers
within scope. This list remains editable.

**Reduction Priorities** assigns positive relative weights and minimum triangle
counts. Equal weights share the available budget proportionally. A nonempty
renderer always keeps at least one triangle. **Preserve Border Edges** defaults
to enabled; disabling it permits stronger reduction at the cost of boundary
geometry. **On Budget Unreachable** chooses a warning or a build error. The
report contains actual input/output counts and retention reasons per renderer.

Skinned and morph-bearing meshes are reduced along existing edges. Meshia's
position-based Smart Link is disabled for them because coincident undeformed
vertices can use different bones, bind poses or morph displacements. This keeps
separate accessories from being connected during reduction; seams may limit how
far the triangle count can be reduced.

Reduction runs after AAO cleanup so deleted geometry does not spend the budget.
Its public mapping API follows merged renderers without keeping dead inputs
alive. If a merge mixes protected, unselected or out-of-scope inputs, the whole
renderer is retained conservatively. Merged priorities use the highest weight
and sum their minima. Keep a protected face separate until reduction finishes;
this component does not force a final face/body merge.

Cloth, mesh swaps, additional vertex streams, more than four skinning influences
per vertex and unsupported topology are retained. Explicitly preserve meshes
whose shaders depend on vertex indices or custom vertex data. An overlapping
Meshia simplifier is an error. **Build and Analyze Avatar** evaluates a full
avatar copy and reports the result; it is not a continuous scene preview.

## ALTO Trace And Optimize Expressions

Add **Noname > ALTO Trace And Optimize Expressions** anywhere inside an
avatar. Like Animator optimization, it analyzes the complete avatar; its
placement does not restrict it to a subtree. Use one enabled component per
avatar. MA Platform Filter still applies to its settings object.

- **Remove Frozen Controls** removes unused controls whose inputs were frozen
  by ALTO Animator Parameter Policy.
- **Remove Dead Controls** removes controls that cannot affect observable
  avatar outputs. A parameter declaration, transition-only state machine or
  closed loop of unused calculations does not by itself keep a control alive.
- **Remove Empty SubMenus** removes empty navigation recursively, including
  cycles without useful contents. A submenu's live activation parameter keeps
  it usable even when its child menu is empty.
- **Prune Unused Local Parameters** removes unused, non-networked Expression
  Parameters. Synchronized declarations retain their order, types and settings.

All four options default to enabled. A multi-channel Puppet remains intact
when any channel has a consumer. Controls without parameters remain as
decorative content. Parameter Policy's Preserve dependencies, SDK side effects,
declared external access and remaining menu references are respected. Sensor
output declarations are retained for the object-optimization stage.

**Preserve Menus** retains the controls in selected menu assets and their
submenus. **Preserve Controls** accepts a menu asset and provides a control
picker, with optional subtree preservation. Selections use display names and
control types instead of numeric indices, so parameter renaming by generators
does not invalidate them. All matching duplicates are retained conservatively;
an unmatched selection skips cleanup and records the reason. Empty source menus
cannot be identified reliably after arbitrary generation. Preserving a control
does not undo an explicit parameter freeze.

This stage writes build-owned menu/parameter copies after Animator optimization
and AAO. SDK builds defer cleanup until late parameter compression has consumed
the original menus, then analyze the final controllers. Originally synchronized
parameters and new compressor declarations remain intact, including parameters
that compression makes local. Direct NDMF bakes apply cleanup in NDMF instead.
When local-parameter and sensor cleanup are both enabled, this final stage also
collects proven unused sensors and their dependent objects, then repeats selected
Animator/object cleanup until no further graph or hierarchy data can be removed.
Deletion attempts count as progress only after the target actually disappears.
If an editor integration declines deletion, the surviving target retains its
bindings and parenting, and required components are checked again before removal.
Late readers, writers and result-transform consumers are rechecked. Network
declarations and new transport parameters are never treated as disposable locals.
Missing hierarchy/component bindings are not treated as consumers. Mixed Write
Defaults and blend-tree layers retain inputs that may change implicit resets.
Shared transport calculations can conservatively keep controls whose inputs
have no direct pose consumer; they are not deleted without a liveness proof.
Material bindings remain consumers when shader usage has not been proven;
ShaderLab Properties alone are not a complete shader-uniform inventory.
Unsupported runtime behaviour or unverifiable preservation skips cleanup.
**Build and Analyze Avatar** records each control's removal/retention reason,
removed local parameters and before/after counts. Original assets are unchanged.

## ALTO Animator Parameter Policy

**Bake Initial Visibility** is disabled by default. Enable it to initialize
objects and renderers to proven constant FX Active/Enabled outputs after
parameter specialization. It uses this policy's scope and platform selection.
This changes initialization callbacks and visibility before the first Animator
evaluation. All reachable motions must guarantee the same boolean output at
full layer weight; dynamic outputs, masks, ambiguous bindings, native visibility
writers and explicit preservation prevent baking. Occlusion and object cleanup
then use the resulting static visibility. The option does not pick one state
from an otherwise dynamic Animator.
Unreachable clips retained for Write Defaults binding coverage do not contribute
motion values to this proof. Their bindings remain subject to the same safety
checks before a proven constant output is baked.
Preserved parameter inputs remain dynamic. A constant visibility track sharing
their clip or tree may still be removed if its output is proven independent of
those inputs; other tracks retain their animation.

Add **Noname > ALTO Animator Parameter Policy** inside an avatar to preserve
dynamic parameters and the computations required to produce them. New policies
apply to **Android** by default; choose **Apply On Platforms** to change this.
An empty **Scope Root** uses the component's subtree. Parameter dependencies are
analyzed across the entire avatar, including dependencies outside that scope.

**Preserve Parameters** accepts Controller and Override Controller assets,
Animators, GameObjects, components with serialized controller references, and
Expression Parameters assets. GameObject sources include Descriptor playable
layers and controllers referenced by generators such as MA Merge Animator.
NDMF parameter providers also supply declarations from components such as MA
Parameters, menu controls, Contacts and PhysBones. Hover over a listed name to
see its source objects. Expression and provider declarations are included in
both selection modes; Declared/Referenced filters controller parameters.
Select **Declared** or **Referenced** parameters, enable child traversal, and
uncheck individual names under **Source Parameters**. Sources remain references:
parameters added later are included in subsequent builds. **Additional Names**
and **Additional Prefixes** select names in the final avatar namespace.

The Inspector shows authoring inputs. During a build, NDMF's parameter-remapping
API resolves supported generators' final names. Unverifiable sources or missing
referenced parameters cause this package's parameter-dependent optimizations to
be skipped conservatively, with diagnostics in the build report. Unused
declarations removed by a generator do not trigger this fallback.

Preservation keeps required animated-parameter and Driver inputs, including
control-flow dependencies. It does not change third-party optimizers' own settings.

**Freeze Parameters** accepts explicit constant values. Assign an
optional source object/controller and choose one of its original names, or
leave Source empty and enter a final avatar name. The **Choose...** button lists
authoring-time declarations; Build and Analyze verifies final names. Bool values
must be `0` or `1`, and Int values must be whole numbers within the Int range.
**Use Default Value** reads the final Expression Parameters default after
generators run, falling back to a consistent controller default. VRChat's
Expression-to-Animator type conversion is applied. Mixed controller types are
supported when every type represents the same numeric constant. Conflicting
defaults, differing numeric interpretations or lossy conversions affecting
Driver Copy require an explicit value or preservation.
Trigger parameters, built-ins, ambiguous types, missing names and preserved
dependencies stop the build rather than receiving guessed values.

**All External Inputs Except Preserved** selects menu controls, Contact outputs,
PhysBone/Raycast outputs, and external Expression Parameters. Controller-only internal
calculations are not bulk-frozen. Expression Parameters written by Animator curves
or Drivers are still external inputs and are included in this mode. Preserve
protects selected parameters and the upstream computations required to produce
their values; built-in VRChat inputs always remain dynamic. This selection is an
explicit behavior-changing policy, not an inference that OSC or network inputs
cannot change. Explicit rows override automatic default values.

Freezing rewrites transition conditions, Blend Tree inputs, state time/speed/
cycle/mirror inputs, and Driver Copy sources to private controller constants.
Driver writes and animated-parameter curves targeting selected inputs are
removed. Copy conversion settings, clip duration, initial states, Write Defaults
coverage and transition timing are retained. A constant motion-time input stays
constant; it is not replaced with a freely advancing state clock. The existing
Animator and BlendShape optimizers then analyze the specialized graph.

Original Expression Parameters, menu controls and late network packing remain.
Changing the original menu/network/Contact/PhysBone input no longer changes the
specialized Animator readers. Internal constants are not Expression Parameters
and must not be targeted by external tools. SDK builds reapply the resolved
logical constants to controllers added or changed by later preprocessors. This
also works without an Expressions optimizer. The final check uses current
controller paths, the original scope and newly resolved Preserve dependencies;
conflicting preservation, exposed constants and changed internal defaults stop
the build. It does not select newly generated transport parameters for automatic
freezing or recapture defaults after network packing. Add ALTO Trace And
Optimize Expressions to clean up unused controls and local parameters afterward.
Reports include each frozen name,
value, type, internal name and whether its value came from the build default.

Analysis covers all controllers. A subtree scope accepts proven local renderer
outputs and renderer-only object activation; outputs outside that scope or
unproven bone, physics and state-behaviour effects require the avatar root scope.
Unknown StateMachineBehaviours, animation events, synchronized-layer behaviour
overrides and selected Play Audio parameter consumers currently stop freezing.
An unresolved Preserve mapping also prevents freezing. New generators must run
before this package's optimization passes.

## Build analysis

Select one scene avatar or a child, then use **Tools > Noname > ALTO
> Build and Analyze Selected Avatar**. The tool preprocesses a copy
in a temporary scene through the SDK callbacks, including NDMF and late
parameter compression, then discards the copy. It does not enter Play Mode.

Reports under `Library/ALTO/BuildReports` contain resource
counts, preprocessing duration, policy mappings and diagnostics. Counts include
disabled renderers. This is preprocessing analysis, not asset-bundle compilation,
SDK upload validation or an upload. Original scene objects and source assets are
not intentionally modified; third-party preprocessors still run their normal
build hooks.
The installed MA parameter-sequence record is restored after analysis, and the
tool rejects execution while the SDK is building or uploading.
The menu waits for delayed Editor initialization after script reload. Direct
API calls made before initialization finishes are rejected before cloning; retry
after the Editor has processed its initialization callbacks. This avoids invoking
optional texture backends before their compute resources are initialized.

The JSON report also includes stage timings and memory samples before and after
instrumented work, including SDK expression and parameter-layout finalization.
`ParentIndex` identifies nested measurements: a parent's duration includes its
children, so do not add both. Only this package's instrumented stages are
attributed; the SDK total also includes other processors and dialog waits.
Failed stages retain their measurements when a report has been initialized.
Memory counters cover the entire Editor, including managed objects awaiting
collection. No forced GC is performed. Samples are not transient peaks, total
allocations, or the runtime avatar's memory usage; the counters are not additive.

## ALTO Trace And Optimize BlendShapes

Add **Noname > ALTO Trace And Optimize BlendShapes** inside a VRChat avatar. An empty
**Scope Root** targets Skinned Mesh Renderers in the component's subtree. Assign
another object in the same avatar to override that scope, or use the avatar root
to optimize the whole avatar. Animation analysis always covers all avatar
Descriptor controllers and child Animators, including generated controllers.

Unanimated shapes are baked at their current weights. **Optimize Zero Reset
Animations** also merges shapes whose animation curves are all constant zero.
Shapes with identical clip usage are combined in their current default-weight
proportions: defaults of 20, 60, and 80 become one shape at 100, with deformation
`0.2 A + 0.6 B + 0.8 C`. Reset clips animate that shape to zero. Overlapping sets
such as `abc`, `abcd`, and `abce` produce three independent groups: `abc`, `d`, and
`e`. A group whose defaults are all zero retains an empty channel at zero so clip
timing and state transitions remain intact.

**Ignore Unreachable States** (enabled by default) analyzes the final state
machines before deciding which BlendShapes are animated. It follows default,
entry, exit, nested-machine, and Any State paths, respects Mute/Solo, and rejects
conditions contradicted by known parameter values. Reachable Parameter Drivers
propagate Set/Copy values and Random ranges across the avatar until the result
stabilizes. Built-in, expression/OSC/saved, Contact, PhysBone, and animated
parameters are treated as runtime inputs, including Raycast outputs and known
external writers declared through AAO Asset Description. Add operations and range-converting
copies are conservative. Every Blend Tree child remains included.

This option only changes BlendShape analysis; it does not delete states or
transitions. References in unreachable clips are removed or remapped only for
optimized shapes. Clip duration and layer binding coverage for Write Defaults
are preserved. Unknown reachable behaviours or synchronized layers retain all
controller references. Disable the option to analyze every referenced state.
External animations still require the MMD protection or explicit shape exclusions.

Use **Excluded Animations** to accept reduced facial motion in selected clips,
such as AFK animations. Assign their original clip assets. These clips do not
prevent freezing or zero-reset grouping in this component's mesh scope. The
build removes their bindings to shapes that were actually optimized; other
tracks, protected shapes, source assets, and clip durations are retained.
Exclusions follow NDMF provenance when available. For tools that discard it,
matching requires a compatible clip name and matching BlendShape curves,
allowing rebased paths and removed bindings. A warning reports exclusions that
could not be matched; unrecognized clips remain part of the analysis.

**Preserve MMD BlendShapes** protects recognized Japanese and English MMD names
from Avatar Optimizer's compatibility table. Descriptor eyelids, visemes, and
mouth-open shapes are always protected. For additional names, add **Noname >
BlendShape Optimization Exclude** to the renderer's object and check the shapes
to preserve. Selections survive changes in mesh ordering and missing shapes.
The optional Avatar Optimizer integration also prevents automatic freezing of
these protected names. Explicit destructive mesh tools can still remove them.

The pass runs in **Optimizing**, after Transforming passes such as Modular Avatar
and VRCFury, and before Avatar Optimizer. Other plugins which introduce animation
later must run before `noname.alto`. Scope membership
captures existing renderers before reparenting and includes new renderers under
the scope at optimization time. If an earlier tool merges a renderer into a
different object outside the scope, include the merge destination in the scope
and put exclusions on that destination. The most specific overlapping scope
wins; identical scopes must have matching settings.

Source assets are unchanged. The component has no continuous editor preview and
does not analyze avatars that lack an enabled optimizer component. GameObject
activation does not disable build processing; disable the component instead.
Authoring components are removed from the built avatar.

## ALTO Trace And Optimize Animator

Add **Noname > ALTO Trace And Optimize Animator** anywhere inside the avatar.
Its scope is always the entire avatar, including Descriptor controllers and child
Animators. Disable individual optimizations in the Inspector:

| Option | Behavior |
| --- | --- |
| Remove Unreachable States | Remove impossible transitions and unreachable states when the layer's Write Defaults binding coverage survives. Default states and layer indices are retained. |
| Remove Unused Parameter Curves | Trace backward from visible output, state conditions, expression/platform parameters and Parameter Driver consumers. Remove unobserved parameter computations, including feedback cycles with no observable consumer. With unused material analysis enabled, also remove unused internal Set/Add/Copy Driver operations. |
| Remove Unused Parameter Declarations | After SDK generators finish, remove internal declarations with no remaining controller references. Keep Expression, menu, sensor, platform, external and preserved names, including defaults shared across controllers. Early NDMF passes leave declarations available to later generators. |
| Simplify Unused State Logic | Follow state conditions and timing back from actual outputs instead of treating every transition as a consumer. Remove unused internal AAP/Set/Add/Copy calculations and collapse empty state machines to their original default state. Replace dead ordinary Direct/1D trees with an existing empty leaf while retaining implicit Write Defaults effects. At the final SDK boundary, remove proven empty Write Defaults On override layers. Keep additive, masked, synchronized, default-sensitive and referenced layers; preserve cross-playable layer-control indices. Preserved input values and their observable consumers survive; input preservation does not retain effect-free state logic. |
| Simplify Constant Blend Trees | Replace static 1D or Direct trees with a selected child when parameter analysis proves its weight is one and all binding coverage is preserved. |
| Merge Same Weight Clips | Combine disjoint static clips driven by the same parameter in a non-normalized Direct tree. |
| Bake Constant Material Properties | Bake invariant Float/Range material properties into renderer-specific build copies. Color and Vector channels remain animated because Unity writes their combined property-block value. Animated material slots, ambiguous targets and unproven weights are preserved. |
| Remove Unused Material Properties | Remove complete material-property tracks only when every possible material shader is proven not to use the property. Preserved inputs and their live outputs remain dynamic; they do not retain unused properties in the same clip. Reanalyze dependent computations after material conversion. Preserve active Color/Vector channels and unknown shader consumers. |

Graph rewriting runs before Avatar Optimizer and again after its layer merging.
Material analysis runs after atlas conversion and after optimizer merging; an
extra graph/object cleanup before AAO runs only when atlas preparation ran.
It sees controllers generated during Transforming. Plugins which introduce new
controllers later must order their work appropriately. The analysis does not
assume that runtime inputs retain their initial values. Parameter declarations
remain available until the final SDK runtime-output boundary; declaration removal
does not run in NDMF-only manual bakes. Public synchronized declarations and their
order are retained, including padding and shared transport slots created by other
processors. Network, menu, platform,
external and Preserve destinations retain their Driver operations. Random
operations remain because they can advance a shared random sequence.

Within one Parameter Driver, operations are analyzed in execution order. An
internal write can be discarded when a later valid Set overwrites it before
any intervening read. Copies use the source value at their position in the
list. Dependency tracing also resolves earlier writes within that driver:
`Scratch = 0; Output = Scratch; Scratch = Input` does not make `Output`
depend on `Input`. Preserving `Scratch` still retains its observable writes.
Separate drivers and states are not treated as one execution block.
Expression, sensor, external and Preserve destinations retain intermediate
writes. Unknown operations, unavailable declarations and invalid Set values
prevent this proof. Random with Prevent Repeats also keeps inputs supplying
the previous destination value, even if its result is otherwise unused.
Dependency substitution falls back to the original graph for SDK built-in
reads/writes and operations without a definite-assignment proof, including
integer additions, float-to-int copies and invalid random/range settings.
The existing fixed-point cleanup then reanalyzes newly unused calculations
and menu inputs without assuming a particular parameter compression scheme.

When runtime ownership is verified, dependency tracing analyzes the local
owner and remote avatar separately using `IsLocal` and local-only Drivers.
NDMF ownership entries distinguish playable controllers from independent
child Animators. Their inputs remain variable, including their own `IsLocal`;
shared controllers and behaviours retain every possible owner execution.
Avatar-owned `IsLocal` accepts Bool, Int and Float declarations, following
[VRChat's built-in type conversion](https://creators.vrchat.com/avatars/animator-parameters/#mismatched-parameter-type-conversion)
to the same runtime values of zero and one.
A synchronized value connects its local producer to its remote consumers;
a private temporary value does not cross that boundary. Other runtime inputs
remain variable. Parameter-curve and Driver cleanup use the same execution
contexts, while explicit Preserve selections protect both instances. The
original selections remain distinct from dependencies discovered by analysis.
Uncertain identity, missing ownership, runtime controller replacement and
unknown runtime effects retain the conservative graph. Private names across
different controller owners still conservatively share dependencies.
This analysis does not identify individual
messages inside a shared network payload or validate client synchronization.

Write Defaults analysis is shared with parameter and menu liveness. Ordinary
states with Write Defaults On supply the same implicit defaults independently
of ordinary Direct/1D motion weights. This can eliminate unused control flow
and its calculations, including dynamic weights, without removing explicit
pose outputs. Mixed defaults, null motions, mirrored children, events and
unsupported motions remain conservative. Separate, stricter proofs apply to
weighted pose and visibility calculations.

After SDK generators finish, a single ordinary empty clip in an unreferenced
Write Defaults On override layer can be removed. The first layer, masks,
additive layers, synchronization targets and behaviour effects are retained.
Empty Write Defaults Off layers can affect a previous pose and are retained.
NDMF remaps controls within the same playable. Controls from another playable
keep their original physical target index by retaining the preceding layers.

Shader analysis checks all inspectable source branches, includes, generated
passes, UsePass and fallback dependencies, including hidden uniforms and macro
token pasting. A property missing from ShaderLab Properties alone is not enough
to remove its animation. Unresolved includes, unsupported generated sources,
missing targets and ambiguous material swaps retain the output. Results are
cached only within each analysis; source assets are never rewritten. This does
not by itself guarantee removal of an entire feature's hierarchy or menus:
remaining transitions, constraints, network consumers and Write Defaults may
still be observable. Referenced layer indices remain valid.

Structural tree rewrites require zero-duration clips, Write Defaults On,
compatible child settings, no Avatar Mask and no additive or synchronized
context. Removing a curve preserves the clip's duration. A binding used only by
unreachable states can still be needed by Write Defaults; such a state is kept.
Unsupported reachable state machine behaviours or synchronized layers disable
the Animator graph optimizations. A Console report lists each pass's changes
and any conservative fallback. No persistent analysis cache or editor preview
is used.

These optimizations preserve the current controller graph's behavior. World
animations or external code that access arbitrary private Animator parameters
are outside that graph. Exposed VRChat parameters are always retained.

## BlendShape conservative cases

- Animated shapes with nonlinear/multiple frames, frames other than a single
  frame at 100, additive animation contexts, or immutable/ambiguous bindings are
  preserved. Static shapes with positive frame weights support interpolation.
- Defaults outside 0..100 and shapes with nonpositive frame weights are kept.
- Renderers with Cloth or an animated mesh reference are skipped.
- External worlds and arbitrary scripts cannot be inferred from Animator
  references. Preserve their shapes with the exclusion marker.

## ALTO Remove Occluded Mesh

Add **Noname > ALTO Remove Occluded Mesh** inside the avatar. This is an
explicitly lossy optimization and defaults to Android. An empty **Scope Root**
uses the component's subtree. Add target SkinnedMeshRenderers and use
**Select BlendShape Regions** to check the morphs that define removable areas.
Any triangle with a displaced vertex in any frame belongs to that region;
current morph weights do not select regions. Candidate weights are temporarily
zeroed for inspection, while other customization weights remain applied.

**All Renderers In Scope** discovers eligible MeshRenderers and
SkinnedMeshRenderers, including generated ones. **Selected Renderers** uses only
the occluder list. In All mode the list supplies per-renderer overrides.
**Excluded Occluders** and all targets are always excluded from occlusion.
Automatic opacity requires an opaque render tag, an opaque queue, readable
ShaderLab states and compatible culling/stencil/depth/color/blending metadata.
Unknown shaders, alpha-cutout materials, animated visibility/materials and
property blocks are not automatically trusted. **Assume Opaque** explicitly
accepts a geometric opacity approximation; a culling override can support shaders
whose source cannot be analyzed. These settings do not simulate arbitrary shader
displacement, alpha programs or stencil effects.
Together, **Assume Opaque** and explicit **Culling** also replace animated numeric
shader values and property blocks with the chosen geometric assumption. Mesh or
material-reference swaps, renderer enable changes, active-state animation and
LOD selection still exclude the occluder.

The default samples 64 directions at 512 pixels, including both poles. A region
is removed only if every triangle was rasterized and hidden in every sampled
pose. Subpixel/degenerate triangles remain when coverage cannot be established.
The boundary margin erodes occluder silhouettes to retain geometry near edges
and holes. Depth tolerance is in world units. Higher settings cost more GPU time.

An empty **Reference Pose** uses the current build pose. Additional pose clips
use avatar-relative paths and a sample time. Pose inspection uses temporary
copies and leaves source transforms, meshes, materials and clips untouched.
**Analyze / Preview Current Setup** shows retained geometry in blue and removal
regions in red. This does not evaluate generators or Animator state machines;
**Analyze Build (Active Platform)** runs isolated SDK preprocessing and reports
the generated result, without uploading. Removal runs after constant visibility
analysis and before BlendShape freezing and mesh reduction. With an active
ALTO Material Atlas, removal and morph freezing wait until actual conversion
and material-output cleanup finish. This evaluates the resulting opacity and
surviving animation instead of predicting a conversion that might be skipped.

Sampling cannot guarantee that a region stays hidden through all runtime motion.
Inspect the preview and include representative poses. The editor needs a GPU
supporting SM5 fragment buffers, compute shaders and R32 float render/blend
targets. Unsupported GPU capabilities preserve the mesh and report the reason.

## Requirements

Unity 2022.3, NDMF 1.14.8 to before 2.0, and VRChat Avatars SDK 3.10.5 to before
3.11 are required by the package manifest.

Optional integrations load within these version ranges:

| Package | Integration version range | Used for |
| --- | --- | --- |
| TexTransTool | 1.0.2 | Texture packing in ALTO Material Atlas |
| Meshia Mesh Simplification | 3.2.0 to before 4.0 | Triangle reduction in ALTO Mesh Reduction |
| Modular Avatar | 1.18.0 to before 2.0 | Ancestor platform filters |
| Avatar Optimizer | 1.8.0 to before 2.0 | Dependency protection and generated renderer/UV mapping |

Meshia's cascading-reduction conflict check also uses Modular Avatar 1.9.0 to
before 2.0 when both packages are installed. Atlas packing and triangle reduction
require their respective optional packages; the other optimizers run without them.
Avatar Optimizer, Modular Avatar and VRCFury are not required for the core analysis
or parameter layout. Regression tests are under `Tests`, with separate assemblies
for the optional integrations.

## License

MIT OR Apache-2.0. See the included license files. MMD compatibility names are
adapted from Avatar Optimizer under MIT; see `ThirdPartyNotices.txt`.

AAO by anatawa12 and contributors is also a design and implementation reference.
See [CREDITS.md](CREDITS.md) for the reviewed revision, source-file references,
and the distinction between adapted data and independently implemented analyses.
