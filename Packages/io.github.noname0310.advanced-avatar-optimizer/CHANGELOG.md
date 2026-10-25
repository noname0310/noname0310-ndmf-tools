# Changelog

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
