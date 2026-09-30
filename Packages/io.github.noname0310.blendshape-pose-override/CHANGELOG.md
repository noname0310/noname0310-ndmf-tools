# Changelog

## 0.0.3

- Support multiple animation clips per blendshape override, with add/remove controls and Scene preview support.
- Combine first-frame keyed values before calculating a single displacement from the current mesh basis. Report conflicting values in the Inspector and during builds.
- Extend eye preset discovery to EyeSquint parameters and namespaced 2D eye trees, including controllers assigned directly to the avatar descriptor.
- Automatically combine compatible left-eye and right-eye clips when no matching bilateral pose is available, without generating new animation assets.
- Prefer expression branches over auxiliary eyelid gates, exclude wide-eye-only branches, and leave ambiguous or incompatible candidates for manual assignment.
- Preserve existing single-clip settings, prefab overrides, manual assignments, and compatible animation path settings.
- Add regression coverage for both animation layouts, path inference, conflicting candidates, multi-clip generation, and previews.

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
