# Noname0310 NDMF Tools

NDMF tools for various avatar modifications.

| Package | Purpose | Direct dependencies |
| --- | --- | --- |
| [Normal Field Offset](Packages/io.github.noname0310.normal-field-offset/README.md) | Offset clothing along a body's surface normal field. | NDMF, Unity Collections |
| [BlendShape Pose Override](Packages/io.github.noname0310.blendshape-pose-override/README.md) | Replace blendshapes with poses authored in animation clips. | NDMF, Modular Avatar, VRChat Avatars SDK |

## Development

Install the dependencies in a Unity 2022.3 avatar project using VCC. NDMF and
Modular Avatar are available from <https://vpm.nadena.dev/vpm.json>. Local UPM
references do not install `vpmDependencies`; install those dependencies with VCC
before connecting the local packages.

Clone this repository and reference its package folders from the host Unity
project's `Packages/manifest.json`. For the following sibling checkout layout:

```text
Projects/
  noname0310-ndmf-tools/Packages/...
  vrc-shinano-2/VrcShinano2/Packages/manifest.json
```

Add these entries to the existing `dependencies` object:

```json
"io.github.noname0310.normal-field-offset": "file:../../../noname0310-ndmf-tools/Packages/io.github.noname0310.normal-field-offset",
"io.github.noname0310.blendshape-pose-override": "file:../../../noname0310-ndmf-tools/Packages/io.github.noname0310.blendshape-pose-override"
```

Paths are relative to the directory containing `manifest.json`. For a different
layout, use Unity's Package Manager **Add package from disk** and select each
`package.json`. Edit the files in this repository; the host Unity project compiles
and runs them directly. Preserve all `.meta` files when moving or renaming files.
Keep a single installed copy of each package in the host project.

To expose the package tests, merge these names into the host manifest's top-level
`testables` array:

```json
"testables": [
  "io.github.noname0310.normal-field-offset",
  "io.github.noname0310.blendshape-pose-override"
]
```

Run the `Noname.NormalFieldOffset.Tests` and
`Noname.BlendShapePoseOverride.Tests` assemblies in Unity Test Runner's EditMode.
Both test assemblies require a VRChat Avatars SDK project.

The initial dependency baseline is Unity 2022.3.22f1, NDMF 1.14.8, Modular Avatar
1.18.7, VRChat Avatars SDK 3.10.5, and Unity Collections 2.1.4. Older combinations
have not been validated.

## License

Copyright (c) 2026 noname0310. Available under **MIT OR Apache-2.0**, at your option.
See [LICENSE.md](LICENSE.md), [LICENSE-MIT](LICENSE-MIT), and
[LICENSE-APACHE](LICENSE-APACHE). Each package also contains these files.
