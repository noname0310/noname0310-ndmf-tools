# Advanced Avatar Optimizer

Build-time Animator and BlendShape optimization for VRChat avatars.

The package provides **Advanced Trace And Optimize Animator** and
**Advanced Trace And Optimize BlendShapes**. Both use the same conservative
parameter-value and state-reachability analysis. Each component can be used
independently.

## Advanced Trace And Optimize BlendShapes

Add **Noname > Advanced Trace And Optimize BlendShapes** inside a VRChat avatar. An empty
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
parameters are treated as runtime inputs. Add operations and range-converting
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
later must run before `noname.advanced-avatar-optimizer`. Scope membership
captures existing renderers before reparenting and includes new renderers under
the scope at optimization time. If an earlier tool merges a renderer into a
different object outside the scope, include the merge destination in the scope
and put exclusions on that destination. The most specific overlapping scope
wins; identical scopes must have matching settings.

Source assets are unchanged. The component has no continuous editor preview and
does not analyze avatars that lack an enabled optimizer component. GameObject
activation does not disable build processing; disable the component instead.
Authoring components are removed from the built avatar.

## Advanced Trace And Optimize Animator

Add **Noname > Advanced Trace And Optimize Animator** anywhere inside the avatar.
Its scope is always the entire avatar, including Descriptor controllers and child
Animators. Disable individual optimizations in the Inspector:

| Option | Behavior |
| --- | --- |
| Remove Unreachable States | Remove impossible transitions and unreachable states when the layer's Write Defaults binding coverage survives. Default states and layer indices are retained. |
| Remove Unused Parameter Curves | Trace backward from visible output, state conditions, expression/platform parameters and Parameter Driver consumers. Remove unobserved parameter computations, including feedback cycles with no observable consumer. |
| Simplify Constant Blend Trees | Replace static 1D or Direct trees with a selected child when parameter analysis proves its weight is one and all binding coverage is preserved. |
| Merge Same Weight Clips | Combine disjoint static clips driven by the same parameter in a non-normalized Direct tree. |
| Bake Constant Material Properties | Bake invariant Float/Range material properties into renderer-specific build copies. Color and Vector channels remain animated because Unity writes their combined property-block value. Animated material slots, ambiguous targets and unproven weights are preserved. |

Graph rewriting runs before Avatar Optimizer and again after its layer merging.
It sees controllers generated during Transforming. Plugins which introduce new
controllers later must order their work appropriately. The analysis does not
assume that runtime inputs retain their initial values. Parameter declarations
and driver operations remain available to platform and later build callbacks.

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

## Requirements

Unity 2022.3, NDMF 1.14.8+, and VRChat Avatars SDK 3.10.5. Avatar Optimizer 1.8/1.9
integration is optional. Tests are in `Noname.AdvancedAvatarOptimizer.Tests`.

## License

MIT OR Apache-2.0. See the included license files. MMD compatibility names are
adapted from Avatar Optimizer under MIT; see `ThirdPartyNotices.txt`.
