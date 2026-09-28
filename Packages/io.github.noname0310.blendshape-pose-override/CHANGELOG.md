# Changelog

## 0.0.2

- Reduce build and preview generation cost by replacing only the configured blendshapes while preserving other morph data and indices.
- Fall back to rebuilding blendshapes when the expected Unity mesh serialization layout is unavailable.
- Verify partial replacement, GPU buffer updates, and asset serialization with regression tests.

## 0.0.1

- Initial standalone package for replacing blendshapes with animation-authored poses.
- Uses current renderer blendshape values as the basis and supports relative or absolute animation paths.
- Includes descriptor-based VRC Blink and named MMD eye presets with automatic v2 parameter-based clip discovery.
- Includes build processing, Scene previews, and EditMode tests.
- Preserves script GUIDs and serialized component settings from the original Assets-based version.
