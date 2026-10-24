# Changelog

## 0.0.2

- Add the optional Ignore Unreachable States analysis for Mute/Solo, transition conditions, and parameter dependencies.
- Preserve runtime parameter inputs and Write Defaults layer coverage when ignoring unreachable animation references.
- Add per-scope animation exclusions for freezing and zero-reset grouping.
- Remove excluded clips' bindings to optimized shapes while retaining other tracks and clip durations.
- Match build copies using source provenance or verified names and BlendShape curves.

## 0.0.1

- Freeze unanimated blendshapes using their current customization weights.
- Merge constant-zero reset curves by shared clip usage with weighted positive morphs.
- Preserve MMD morphs, Descriptor shapes, and renderer-specific exclusions.
- Analyze generated avatar controllers during the NDMF optimization phase.
- Provide optional Avatar Optimizer integration for preserved blendshapes.
