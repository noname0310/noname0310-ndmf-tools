using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using Descriptor = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor;
using Mode = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.ParameterBehaviorTargetMode;

namespace Noname.AvatarTools.Analysis
{
    // Runtime output graph, independent of artist parameter names. Set weights are
    // intermediate values: they can affect geometry, forwarded Animator inputs, or
    // other set weights. Cycles without an observable sink are not roots.
    // SDK semantics reviewed against AAO 1.9.21 ComponentInfos.VRCSDK.cs and
    // the VRChat Face Tracking documentation. This dataflow implementation is independent.
    internal sealed class NativeFaceTracking
    {
        internal sealed class Outputs
        {
            internal bool Geometry;
            internal readonly HashSet<string> Parameters = new(StringComparer.Ordinal);
        }

        internal readonly Descriptor Descriptor;
        internal readonly SkinnedMeshRenderer[] Meshes;
        private readonly Dictionary<string, Outputs> sets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> downstream = new(StringComparer.Ordinal);
        internal IEnumerable<Descriptor.FaceTrackingParameterBehavior> Behaviors => Descriptor != null && Descriptor.enableFaceTracking
            ? (Descriptor.faceTrackingParameterBehaviors ?? new()).Take(128).Where(b => b != null) : Enumerable.Empty<Descriptor.FaceTrackingParameterBehavior>();

        internal NativeFaceTracking(GameObject avatar) : this(avatar.GetComponent<Descriptor>()) { }

        internal NativeFaceTracking(Descriptor descriptor)
        {
            Descriptor = descriptor;
            Meshes = descriptor != null && descriptor.enableFaceTracking
                ? (descriptor.faceTrackingMeshes ?? new()).Where(m => m != null).Distinct().ToArray() : Array.Empty<SkinnedMeshRenderer>();
            if (descriptor == null || !descriptor.enableFaceTracking) return;
            foreach (var set in descriptor.faceTrackingBlendshapesSetList ?? new())
            {
                if (set == null || !ValidSetName(set.setName)) continue;
                if (!sets.TryGetValue(set.setName, out var output)) sets.Add(set.setName, output = new());
                output.Geometry |= SetShapes(set).Any(HasShape);
            }
            foreach (var behavior in Behaviors)
            {
                if (!sets.TryGetValue(behavior.weightBlendSetName ?? "", out var output)) continue;
                switch (behavior.targetMode)
                {
                    case Mode.ForwardToAnimator:
                        if (!string.IsNullOrEmpty(behavior.outputParameterName)) output.Parameters.Add(behavior.outputParameterName);
                        break;
                    case Mode.ApplyBlendSetWeight:
                        if (sets.ContainsKey(behavior.targetBlendSetName ?? ""))
                        {
                            if (!downstream.TryGetValue(behavior.weightBlendSetName, out var next)) downstream.Add(behavior.weightBlendSetName, next = new());
                            next.Add(behavior.targetBlendSetName);
                        }
                        break;
                    case Mode.ApplyToBlendshape: case Mode.LimitBlendshapeMax: case Mode.LimitBlendshapeMin:
                        output.Geometry |= HasShape(behavior.targetBlendshapeName);
                        break;
                    default:
                        // Unknown SDK operations can have outputs not described by this graph.
                        output.Geometry = true;
                        break;
                }
            }
        }

        private static bool ValidSetName(string name) => !string.IsNullOrEmpty(name) && name.Length <= 100;
        internal bool HasShape(string name) => !string.IsNullOrEmpty(name) && Meshes.Any(m => m.sharedMesh != null && m.sharedMesh.GetBlendShapeIndex(name) >= 0);

        internal bool KeyEnabled(int index)
        {
            if (index < 0 || index >= (int)Descriptor.FaceTrackingKey.Max) return false;
            var key = (Descriptor.FaceTrackingKey)index;
            if (!Descriptor.useAllTrackingKeys && !Descriptor.IsBasicFaceTrackingKey(key)) return false;
            if (!Descriptor.enableEyeLook) return true;
            var eyes = Descriptor.customEyeLookSettings;
            if (key >= Descriptor.FaceTrackingKey.EyeLookOutRight && key <= Descriptor.FaceTrackingKey.EyeLookDownLeft)
                return eyes.eyeGazeMode != Descriptor.CustomEyeLookSettings.EyeGazeMode.BoneRotation;
            if (key == Descriptor.FaceTrackingKey.EyeClosedLeft || key == Descriptor.FaceTrackingKey.EyeClosedRight)
                return eyes.eyelidType == Descriptor.EyelidType.None || eyes.eyelidType == Descriptor.EyelidType.FaceTracking;
            return true;
        }

        internal IEnumerable<string> SetShapes(Descriptor.FaceTrackingBlendshapesSet set)
        {
            var names = set.faceTrackingBlendshapeNames ?? Array.Empty<string>();
            for (int i = 0; i < names.Length; i++)
                if (KeyEnabled(i) && !string.IsNullOrEmpty(names[i])) yield return names[i];
            if (Descriptor.lipSync == VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShapeFaceTracking &&
                set.visemeBlendshapeNames?.Length == (int)VRC_AvatarDescriptor.Viseme.Count)
                foreach (string name in set.visemeBlendshapeNames.Where(n => !string.IsNullOrEmpty(n))) yield return name;
        }

        internal HashSet<string> ShapeNames()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (Descriptor == null || !Descriptor.enableFaceTracking) return result;
            foreach (var set in Descriptor.faceTrackingBlendshapesSetList ?? new())
                if (set != null) result.UnionWith(SetShapes(set));
            foreach (var behavior in Behaviors)
                if (behavior.targetMode is Mode.ApplyToBlendshape or Mode.LimitBlendshapeMax or Mode.LimitBlendshapeMin)
                    if (!string.IsNullOrEmpty(behavior.targetBlendshapeName)) result.Add(behavior.targetBlendshapeName);
            return result;
        }

        internal IEnumerable<string> ForwardedParameters() => Behaviors.Where(b => b.targetMode == Mode.ForwardToAnimator)
            .Select(b => b.outputParameterName).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal);

        internal Outputs SetOutputs(string name)
        {
            var result = new Outputs();
            if (string.IsNullOrEmpty(name)) return result;
            var seen = new HashSet<string>(StringComparer.Ordinal); var queue = new Queue<string>(); queue.Enqueue(name);
            while (queue.Count > 0)
            {
                string current = queue.Dequeue();
                if (!seen.Add(current) || !sets.TryGetValue(current, out var output)) continue;
                result.Geometry |= output.Geometry; result.Parameters.UnionWith(output.Parameters);
                if (downstream.TryGetValue(current, out var next)) foreach (string target in next) queue.Enqueue(target);
            }
            return result;
        }

        internal bool SetObserved(string name, ISet<string> parameters)
        {
            var output = SetOutputs(name);
            return output.Geometry || output.Parameters.Overlaps(parameters);
        }

        internal static IEnumerable<VRC_AnimatorFaceTrackingBlendshapesSet.BlendSet> Controls(VRC_AnimatorFaceTrackingBlendshapesSet behavior)
            => (behavior.blendSets ?? new()).Take(64).Where(s => s != null);

        internal static IEnumerable<string> Reads(VRC_AnimatorFaceTrackingBlendshapesSet behavior)
            => Controls(behavior).Where(s => s.type == VRC_AnimatorFaceTrackingBlendshapesSet.SourceType.Parameter)
                .Select(s => s.sourceParameter).Where(n => !string.IsNullOrEmpty(n));

        // Removal is driven by the same backwards liveness solution as Animator
        // optimization. Keep array positions when pruning Descriptor operations so
        // entries past the runtime limit never move into the evaluated prefix.
        internal int Prune(ISet<string> live, IEnumerable<VRC_AnimatorFaceTrackingBlendshapesSet> controls)
        {
            int removed = 0;
            foreach (var control in controls.Distinct())
            {
                var before = control.blendSets ?? new();
                var after = before.Take(64).Where(s => s != null && SetObserved(s.blendshapesSetName, live)).ToList();
                removed += before.Count - after.Count;
                if (after.Count != before.Count) control.blendSets = after;
            }
            foreach (var behavior in Behaviors)
            {
                bool observed = behavior.targetMode switch {
                    Mode.ForwardToAnimator => live.Contains(behavior.outputParameterName ?? ""),
                    Mode.ApplyBlendSetWeight => SetObserved(behavior.targetBlendSetName, live),
                    Mode.ApplyToBlendshape or Mode.LimitBlendshapeMax or Mode.LimitBlendshapeMin => HasShape(behavior.targetBlendshapeName),
                    _ => true
                };
                if (observed) continue;
                if (!string.IsNullOrEmpty(behavior.outputParameterName) || !string.IsNullOrEmpty(behavior.targetBlendSetName) ||
                    !string.IsNullOrEmpty(behavior.targetBlendshapeName)) removed++;
                behavior.outputParameterName = behavior.targetBlendSetName = behavior.targetBlendshapeName = "";
            }
            return removed;
        }

        // An ineffective morph can be removed even if the runtime writes it. Clear
        // its native name only after the mesh rewrite, and only when every target
        // mesh lost that name. Other meshes may still provide the same output.
        internal int ClearMissingShapes(ISet<string> removedNames = null)
        {
            if (Descriptor == null || !Descriptor.enableFaceTracking) return 0;
            int cleared = 0;
            void Clear(string[] names)
            {
                if (names == null) return;
                for (int i = 0; i < names.Length; i++)
                    if (!string.IsNullOrEmpty(names[i]) && (removedNames == null || removedNames.Contains(names[i])) && !HasShape(names[i])) { names[i] = ""; cleared++; }
            }
            foreach (var set in Descriptor.faceTrackingBlendshapesSetList ?? new())
                if (set != null) { Clear(set.faceTrackingBlendshapeNames); Clear(set.visemeBlendshapeNames); }
            foreach (var behavior in Behaviors)
                if (behavior.targetMode is Mode.ApplyToBlendshape or Mode.LimitBlendshapeMax or Mode.LimitBlendshapeMin)
                    if (!string.IsNullOrEmpty(behavior.targetBlendshapeName) && (removedNames == null || removedNames.Contains(behavior.targetBlendshapeName)) && !HasShape(behavior.targetBlendshapeName))
                    { behavior.targetBlendshapeName = ""; cleared++; }
            return cleared;
        }

        internal int CleanupRemovedOutputs()
        {
            int changes = ClearMissingShapes();
            if (Descriptor?.faceTrackingMeshes != null)
                changes += Descriptor.faceTrackingMeshes.RemoveAll(m => m == null);
            return changes;
        }
    }
}
