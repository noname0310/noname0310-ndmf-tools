# Trace And Optimize BlendShapes

Reduce blendshape data and animation bindings during an NDMF avatar build.

Add **Noname > Trace And Optimize BlendShapes** inside a VRChat avatar. An empty
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

**Preserve MMD BlendShapes** protects recognized Japanese and English MMD names
from Avatar Optimizer's compatibility table. Descriptor eyelids, visemes, and
mouth-open shapes are always protected. For additional names, add **Noname >
BlendShape Optimization Exclude** to the renderer's object and check the shapes
to preserve. Selections survive changes in mesh ordering and missing shapes.
The optional Avatar Optimizer integration also prevents automatic freezing of
these protected names. Explicit destructive mesh tools can still remove them.

The pass runs in **Optimizing**, after Transforming passes such as Modular Avatar
and VRCFury, and before Avatar Optimizer. Other plugins which introduce animation
later must run before `noname.trace-and-optimize-blendshapes`. Scope membership
captures existing renderers before reparenting and includes new renderers under
the scope at optimization time. If an earlier tool merges a renderer into a
different object outside the scope, include the merge destination in the scope
and put exclusions on that destination. The most specific overlapping scope
wins; identical scopes must have matching settings.

Source assets are unchanged. The component has no continuous editor preview and
does not analyze avatars that lack an enabled optimizer component. GameObject
activation does not disable build processing; disable the component instead.
Authoring components are removed from the built avatar.

## Conservative cases

- Animated shapes with nonlinear/multiple frames, frames other than a single
  frame at 100, additive animation contexts, or immutable/ambiguous bindings are
  preserved. Static shapes with positive frame weights support interpolation.
- Defaults outside 0..100 and shapes with nonpositive frame weights are kept.
- Renderers with Cloth or an animated mesh reference are skipped.
- External worlds and arbitrary scripts cannot be inferred from Animator
  references. Preserve their shapes with the exclusion marker.

## Requirements

Unity 2022.3, NDMF 1.14.8+, and VRChat Avatars SDK 3.10.5. Avatar Optimizer 1.8/1.9
integration is optional. Tests are in `Noname.TraceAndOptimizeBlendShapes.Tests`.

## License

MIT OR Apache-2.0. See the included license files. MMD compatibility names are
adapted from Avatar Optimizer under MIT; see `ThirdPartyNotices.txt`.
