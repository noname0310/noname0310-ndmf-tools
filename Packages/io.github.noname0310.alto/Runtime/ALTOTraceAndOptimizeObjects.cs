using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noname.AvatarTools
{
    [AddComponentMenu("Noname/ALTO Trace And Optimize Objects")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedTraceAndOptimizeObjects")]
    public sealed class ALTOTraceAndOptimizeObjects : ALTOOptimizationComponent
    {
        [Tooltip("Remove unused sensor parameter declarations with a former local consumer or an explicit input-freeze policy. Network, menu, preserved and external outputs are retained. Local Expression declarations are handled jointly when Prune Unused Local Parameters is enabled.")]
        public bool PruneUnusedSensorParameters = true;

        [Tooltip("Remove redundant Active/Enabled curves only when every reachable output equals the initial state. Initial activation and initialization side effects remain unchanged. Occlusion and mesh grouping can use the resulting constant visibility.")]
        public bool BakeConstantVisibility = true;

        [Tooltip("Remove redundant local transform curves only when they equal the initial transform and no humanoid, physics or constraint writer can change it. The mesh optimizer can then rebind eligible static skin bones.")]
        public bool BakeConstantTransforms = true;

        [Tooltip("Remove constraints and transform/activation curves whose outputs cannot reach a renderer, bone, physics object, contact or other runtime consumer. References, scope boundaries and explicit preservation are respected.")]
        public bool RemoveUnusedTransformOutputs = true;

        [Tooltip("Simplify constraints whose behavior is proved redundant: constant zero-weight sources, unchanged local scale, unchanged parent-constraint rest poses, and full parent following representable by hierarchy parenting. Dynamic settings, scale differences, references, scope boundaries and explicit preservation are respected.")]
        public bool RemoveRedundantConstraints = true;

        [Tooltip("Remove objects and components with no observable runtime consumer after other build generators have finished. Animation writers alone do not preserve an unused object. Bones, constraints, references, external inputs and explicit preservation remain protected.")]
        public bool RemoveUnusedObjects = true;

        [Tooltip("Protect these objects and their descendants from this component and Avatar Optimizer's automatic removal or bone merging.")]
        public List<GameObject> PreserveObjects = new();

        [Tooltip("Protect individual components. Preserved renderers remain separate during AAO automatic renderer merging; texture atlasing can still include them.")]
        public List<Component> PreserveComponents = new();

        // Build-only dependencies for the optional Avatar Optimizer integration. Never serialized.
        [NonSerialized] public Component[] BuildPreservedComponents = Array.Empty<Component>();
    }
}
