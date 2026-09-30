# BlendShape Pose Override

An NDMF component that replaces selected blendshapes with poses authored in animation clips. It supports any skinned mesh in the avatar; correcting customized eyelids is one use case.

## Installation

Install `io.github.noname0310.blendshape-pose-override` from the
[Noname0310 NDMF Tools VPM repository](https://noname0310.github.io/noname0310-ndmf-tools/).
Requires Unity 2022.3, NDMF 1.14.8 or newer within 1.x, Modular Avatar 1.18.7 or newer
within 1.x, and VRChat Avatars SDK 3.10.5 or newer within 3.10.x.
For local development, see the [source repository](https://github.com/noname0310/noname0310-ndmf-tools).
The source repository includes instructions for enabling the package's EditMode tests.

## Setup

1. Add **Noname > BlendShape Pose Override** to the avatar root or a child object.
2. Choose **Target Mesh**. Adding or resetting the component initializes this field from the nearest ancestor VRC Avatar Descriptor's eyelid renderer. This is an editable default, not a permanent connection to the descriptor. The override list starts empty.
3. Set the renderer's current blendshape values to the intended basis, such as the customized face with its eyes open.
4. Add a row for each blendshape to replace using **+**, or use an optional eye preset below. Select its name on the left and assign its override animations on the right. Use **+ Add Animation** to combine clips, such as separate left-eye and right-eye poses, in one row.
5. In each clip, key the blendshape values that produce the desired pose. The value at time **0** is used. Clips longer than one frame show a warning. Unkeyed shapes retain their current values. When multiple clips key the same shape, the values must agree; conflicting values produce a configuration error. Values are combined, not summed or applied in clip order.
6. Select the path mode. **Absolute** resolves paths from the nearest avatar descriptor. **Relative** resolves from this component's object, or from **Relative Path Root** when assigned, matching MA Merge Animator's path convention.
7. Enable NDMF Preview and choose a **Preview Entry** and **Preview Weight** to inspect the result in the Scene view. These controls have no effect on build defaults.

Changing Target Mesh does not rewrite the list, animation paths, or the avatar descriptor. Select valid names and clips for the new renderer. For automatic VRChat blinking, the descriptor must still reference the intended renderer and shape.

## Optional eye presets

- **Add VRC Blink** resolves the Blink index from the nearest descriptor's Eyelids Mesh. It does not search for a literal name. Target Mesh must match that descriptor renderer; otherwise the button is disabled with an explanatory tooltip.
- **Add MMD Eye Morphs** looks up a fixed array of seven exact Japanese names: blink, smile with closed eyes, wink, right wink, wink 2, right wink 2, and relaxed closed eyes. Their exact spellings, including full-width and half-width characters, match the current scene mesh. No descriptor or MMD separator is used for name resolution. Only names present on Target Mesh are added.

The buttons append missing rows and attempt to fill empty animation fields. Existing manual assignments are preserved, and repeated clicks do not create duplicates. An empty override list is a build-time no-op.

### Automatic animation assignment

Both preset buttons search every MA Merge Animator under the avatar, including inactive objects, plus the descriptor's base and special animator controllers. Nested state machines, nested blend trees, synced layers, and Animator Override Controllers are supported. Clip and tree names are never used to identify expressions.

The search follows `v2/EyeLidLeft`, `v2/EyeLidRight`, or `v2/EyeLid` to the closed pose at 0. Nested branches or 2D axes using `v2/SmileSad*`, `v2/SmileFrown*`, `v2/MouthSmile*`, or `v2/EyeSquint*` distinguish ordinary closed eyes from joyful or tightly closed eyes. Namespaced parameters such as `FT/v2/EyeLidLeft` and `OSCm/Proxy/FT/v2/EyeLidLeft` are recognized by their standard suffix. These meanings follow the [VRCFaceTracking parameter definitions](https://docs.vrcft.io/docs/tutorial-avatars/tutorial-avatars-extras/parameters).

VRC Blink, MMD blink, and relaxed closed eyes use the ordinary both-eyes-closed pose. MMD smile uses the joyful both-eyes pose. Wink and right wink use the corresponding joyful one-eye pose; wink 2 and right wink 2 use ordinary one-eye poses.

Only clips with blendshape curves that resolve to Target Mesh through the controller's animation root are candidates. MA Relative, Absolute, and Relative Path Root settings are respected. If needed, the component's path settings are adjusted to a common source root, provided existing manual assignments still resolve correctly.

The search selects existing clips at exact child poses in 1D or 2D trees. It prefers branches that distinguish closed-eye expressions over auxiliary eyelid gates for gaze or brow corrections, and excludes wide-eye branches that do not include the closed endpoint. If no equally strong bilateral pose exists, compatible and unambiguous left-eye and right-eye clips from the same animation root are assigned together. No new animation assets are created.

The search does not approximate blended poses or guess between different matching alternatives. Smoothing clips that only animate parameters are excluded. Unknown parameter meanings, missing poses, ambiguous candidates, and incompatible clip combinations are left empty with an Inspector message for manual assignment. Existing single-clip settings remain valid.

## Basis and output

For each row, the new delta is `original mesh at the combined keyed values minus original mesh at current renderer values`. The row's clips are combined before calculating this displacement once. At weight 0, the generated mesh preserves the current appearance. At weight 100, the row produces the authored pose. The current customization is not applied twice.

All rows are calculated from the same original mesh and weights. A clip may reference another shape that is also overridden; it always samples that shape's original definition. Reordering rows does not change geometry. Activating multiple generated shapes combines their deltas additively; individual target poses are guaranteed when the other generated shapes are at 0.

If an overridden shape already has a nonzero contribution in the current basis, that original contribution is folded into the generated neutral mesh and its default weight is reset to 0. Other weights are retained. Names and indices remain unchanged. Replaced shapes get one frame at weight 100; other shapes retain all original frames. Vertex positions, normals, and tangents are sampled using Unity's blendshape evaluation, including original multi-frame shapes and nonstandard frame weights.

## Build behavior and limits

- Runs in NDMF's Transforming phase after EreMorph and before Modular Avatar, while original blendshapes and animation paths still exist.
- Clones the mesh and strips the component at build time. Source meshes and animation assets are not edited.
- Processes enabled components even on inactive objects. Disabled components are stripped without processing. Only one enabled component may modify a given renderer; use multiple rows for that renderer.
- The basis is the renderer's current serialized blendshape values at this build step. Values introduced by later processors, animator states, or runtime customization are not part of that basis.
- Only blendshape curves resolving to Target Mesh are used. Bone, transform, material, object-reference, and other-renderer curves are ignored with an Inspector warning. This component does not assign the override clips to an animator.
- Invalid targets, duplicate rows, missing clips, or clips with no matching curves stop the build with a configuration error.
- A plugin that later deletes, bakes, or replaces an overridden shape can prevent it from being animated. Keep those shapes available in subsequent mesh processing.

The path conventions follow [MA Merge Animator](https://modular-avatar.nadena.dev/docs/reference/merge-animator).

## License

Licensed under MIT OR Apache-2.0, at your option. See [LICENSE.md](LICENSE.md).
