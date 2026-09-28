# Normal Field Offset

An NDMF component that offsets clothing along the surface normals of a body mesh. It operates on cloned meshes for Scene previews, Play mode, and avatar builds, preserving the original FBX and mesh assets.

## Installation

Install `io.github.noname0310.normal-field-offset` from the
[Noname0310 NDMF Tools VPM repository](https://noname0310.github.io/noname0310-ndmf-tools/).
Requires Unity 2022.3, NDMF 1.14.8 or newer within 1.x, and Unity Collections 2.1.4.
For local development, see the [source repository](https://github.com/noname0310/noname0310-ndmf-tools).
The source repository includes instructions for enabling the package's EditMode tests.

## Usage

1. Add `Noname > Normal Field Offset` to the **GameObject containing the Skinned Mesh Renderer of the clothing you want to offset**, such as an outer cheongsam.
2. Assign the avatar's body Skinned Mesh Renderer to `Target Body`. It must be a different renderer within the same avatar.
3. Adjust `Offset (m)`. The default value, `0.002`, moves vertices outward by 2 mm. Negative values move them inward.
4. Enable NDMF Preview and inspect the result in the Scene view. If the clothing uses multiple renderers, add the component to each renderer you want to offset.
5. Optionally set `Max Distance (m)` to affect only vertices near the body. `0` means unlimited distance. The effect fades smoothly over the final 20% of the distance limit.

For full-body tights layered under a cheongsam, start with a small positive offset on the cheongsam. To offset both garments, assign the same body and choose an offset for each garment. The offset is **a displacement added to the current clothing position**, not a minimum clearance from the body.

## Behavior and scope

- For each clothing vertex, the component finds the closest triangle on the body in its current bone pose and blendshape state, then interpolates the vertex normals at that point. Positive offsets follow the outward normal even for vertices inside the body.
- A BVH accelerates triangle searches. Garments targeting the same body share a computed normal field.
- Inverse skinning matrices convert the displacement into the clothing's original mesh coordinates. Bones, bind poses, variable-count bone weights, submeshes, UVs, materials, blendshape frames, and authored normals and tangents are preserved.
- The component runs in NDMF's Transforming phase after EreMorph and before Modular Avatar deletes mesh regions or merges bones. Body regions that MA will hide can still supply normals. **Body shape changes applied later by MA Shape Changer or animators are not included in the field calculation.** Set the fitting shape on the body renderer's blendshapes first.
- Disabled components are skipped. Inactive clothing objects are processed during builds because an avatar menu may enable them later. The configuration components are removed after processing.
- Distances are measured in world-space meters in the setup pose. Preview changes only proxy meshes and recomputes when settings, bones, or blendshapes are edited through the Inspector or Undo system.

This tool bakes a static offset; it does not perform real-time clothing collision handling. Different bone weights, PhysBones, or body shape animations can still cause intersections during movement. Normal directions can change abruptly where the closest body surface switches, such as around the armpits or between the legs, or at skirt hems far from the body. Start with a small offset and inspect the result. Authored normals are preserved rather than recalculated for large deformations.

## Validation

Run `Noname.NormalFieldOffset.Tests` in Unity Test Runner's EditMode. Tests cover normal interpolation, vertices inside the body, positive and negative offsets, scale, bone rotation, skinning quality, blendshape and mesh data preservation, distance limits, shared source asset protection, inactive clothing during builds, and proxy previews.

Reference implementations and APIs: the installed MA `MeshDeleterPreview` and `RemoveVertexColorPass`, NDMF `IRenderFilter`, and NDMF plugin phases. The component uses CPU snapshots from Unity's [`SkinnedMeshRenderer.BakeMesh`](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/SkinnedMeshRenderer.BakeMesh.html).

## License

Licensed under MIT OR Apache-2.0, at your option. See [LICENSE.md](LICENSE.md).
