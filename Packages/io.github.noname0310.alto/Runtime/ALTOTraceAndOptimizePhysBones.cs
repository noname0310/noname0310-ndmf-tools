using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools
{
    [AddComponentMenu("Noname/ALTO Trace And Optimize PhysBones")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedTraceAndOptimizePhysBones")]
    public sealed class ALTOTraceAndOptimizePhysBones : ALTOOptimizationComponent
    {
        [Tooltip("Share always-active local colliders with the same effective root and exactly matching serialized settings. Animated/global/external colliders and collision multiplicity remain protected.")]
        public bool ShareEquivalentColliders = true;

        [Tooltip("Disable Is Animated only when the complete chain has no external transform writer and its ancestor scales are not animated.")]
        public bool OptimizeIsAnimated = true;

        [Tooltip("Replace unreferenced leaf bones with an exactly matching virtual endpoint. Referenced/animated leaves, branching endpoints, stretching and existing endpoints are retained.")]
        public bool ReplaceUnusedEndBones = true;

        [Tooltip("Combine simulations with identical constant settings and identity sibling roots. Grabs, output parameters, animation, curves, stretching and external readers prevent merging.")]
        public bool MergeCompatibleSimulations = true;

        public List<VRCPhysBone> PreservePhysBones = new();
        public List<VRCPhysBoneCollider> PreserveColliders = new();

        [NonSerialized] public Component[] BuildInputs = Array.Empty<Component>();
    }
}
