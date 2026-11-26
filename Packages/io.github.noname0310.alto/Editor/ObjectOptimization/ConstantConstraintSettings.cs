using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.ObjectOptimization
{
    // These SDK configuration values are consumed on each solve. Changing their
    // starting values additionally requires a stateless, exclusively visual solve.
    // Lifecycle switches, source counts and references are never rewritten here.
    internal static class ConstantConstraintSettings
    {
        private sealed class Change
        {
            internal VRCConstraintBase Constraint;
            internal Transform Target;
            internal readonly List<(string Name, ConstantPropertyProof Proof)> Properties = new();
            internal readonly HashSet<string> ChangedGroups = new();
        }

        internal static int Apply(ObjectAnalysisSnapshot snapshot, ObjectOptimizationState settings,
            Func<Transform, bool> allowInitialChanges, ICollection<string> messages)
        {
            using var cost = AnalysisCost.Measure("Constant output consumer/constraint settings");
            var index = snapshot.Index; var avatar = index.Avatar;
            string unsafeReason = index.UnsafeReason ?? ObjectOptimizationSafety.UnsupportedRuntime(avatar);
            if (unsafeReason != null) return 0;
            var plans = new List<Change>();
            InitialPoseReaders.Inputs readers = null;
            InvariantComponentSettings invariant = null;
            var nativeBindings = new NativeAnimationBindings(avatar);
            foreach (var constraint in avatar.GetComponentsInChildren<VRCConstraintBase>(true))
            {
                if (!Known(constraint.GetType()) || !settings.Scope.Contains(constraint.transform) || settings.Protected(constraint)) continue;
                var target = constraint.TargetTransform != null ? constraint.TargetTransform : constraint.transform;
                if (target == avatar.transform || !settings.Scope.Contains(target) || settings.Protected(target)) continue;
                var uses = index.ForObject(constraint);
                if (uses.Count == 0) continue;
                invariant ??= new InvariantComponentSettings(index, index.Reachability, index.ControllerEntries,
                    sharedConstants: index.ConstantOutputs);
                var plan = new Change { Constraint = constraint, Target = target };
                using var serialized = new SerializedObject(constraint);
                foreach (var fields in Groups(constraint))
                {
                    // Unlocked editing and unfreezing can rebake these fields.
                    // An Animator curve equal to the authored value still writes
                    // over that native result, so equality alone is insufficient.
                    bool weight = fields[0].EndsWith("Weight", StringComparison.Ordinal);
                    if (!weight && (!constraint.Locked || constraint.FreezeToWorld ||
                        !invariant.IsConstantProperty(constraint, "Locked") ||
                        !invariant.IsConstantProperty(constraint, "FreezeToWorld"))) continue;
                    var queries = new ConstantPropertyQuery[fields.Length]; bool complete = true;
                    for (int i = 0; i < fields.Length; i++)
                    {
                        if (!index.Writes(constraint, fields[i]) ||
                            !ConstantOutputValue.TryRead(serialized.FindProperty(fields[i]), out var initial) ||
                            initial.Kind != ConstantOutputKind.Scalar) { complete = false; break; }
                        queries[i] = new ConstantPropertyQuery(fields[i], initial);
                    }
                    if (!complete || !index.ConstantOutputs.TryProveGroup(constraint, queries, out var proofs, out _)) continue;
                    if (weight && proofs.Any(p => p.Value.Number < 0 || p.Value.Number > 1)) continue;
                    if (proofs.Any(p => p.ChangesInitialValue)) plan.ChangedGroups.UnionWith(fields);
                    for (int i = 0; i < fields.Length; i++) plan.Properties.Add((fields[i], proofs[i]));
                }
                if (plan.Properties.Count == 0) continue;
                if (plan.Properties.Any(p => p.Proof.ChangesInitialValue))
                {
                    readers ??= snapshot.InitialReaders;
                    string reason = ChangedInitializationReason(snapshot, settings, plan, readers, allowInitialChanges, invariant);
                    if (reason == null && !nativeBindings.Supports(constraint, plan.Properties.SelectMany(p => p.Proof.Writers), out var nativeReason))
                        reason = nativeReason;
                    if (reason != null)
                    {
                        messages.Add("Retained changed constraint configuration on " +
                            AnimationUtility.CalculateTransformPath(constraint.transform, avatar.transform) + ": " + reason + ".");
                        // Equivalent defaults remain useful even if another field
                        // needs the original Animator write and initialization.
                        plan.Properties.RemoveAll(p => plan.ChangedGroups.Contains(p.Name));
                    }
                }
                if (plan.Properties.Count != 0) plans.Add(plan);
            }
            var removals = new Dictionary<VirtualClip, HashSet<EditorCurveBinding>>();
            int changedInitial = 0;
            var appliedProofs = new AppliedProofReport();
            foreach (var plan in plans)
            {
                bool changed = plan.Properties.Any(p => p.Proof.ChangesInitialValue);
                if (changed)
                {
                    var position = plan.Target.localPosition; var rotation = plan.Target.localRotation; var scale = plan.Target.localScale;
                    using var serialized = new SerializedObject(plan.Constraint);
                    foreach (var property in plan.Properties.Where(p => p.Proof.ChangesInitialValue))
                    { serialized.FindProperty(property.Name).floatValue = property.Proof.Value.Number; changedInitial++; }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    plan.Constraint.ApplyConfigurationChanges();
                    // Editor solver registration may restore a cached pose. The
                    // configuration rewrite must keep the authored target storage.
                    plan.Target.localPosition = position; plan.Target.localRotation = rotation; plan.Target.localScale = scale;
                }
                foreach (var writer in plan.Properties.SelectMany(p => p.Proof.Writers))
                {
                    if (!removals.TryGetValue(writer.Clip, out var bindings)) removals.Add(writer.Clip, bindings = new());
                    bindings.Add(writer.Binding);
                }
                foreach (var property in plan.Properties) appliedProofs.Record(initializationChanged: property.Proof.ChangesInitialValue);
            }
            int count = 0;
            foreach (var removal in removals) { MotionAnalysis.RemoveCurves(removal.Key, removal.Value); count += removal.Value.Count; }
            if (count != 0) messages.Add("Normalized " + count + " constant constraint configuration curves (" + changedInitial +
                " changed initial values); source ordering, zero-weight rest solves and single-source normalization were retained.");
            appliedProofs.AppendTo(messages, "constant constraint configuration");
            return count;
        }

        private static string ChangedInitializationReason(ObjectAnalysisSnapshot snapshot, ObjectOptimizationState settings,
            Change plan, InitialPoseReaders.Inputs readers, Func<Transform, bool> allowInitialChanges,
            InvariantComponentSettings invariant)
        {
            var index = snapshot.Index; var constraint = plan.Constraint; var target = plan.Target;
            if (allowInitialChanges?.Invoke(constraint.transform) != true || allowInitialChanges?.Invoke(target) != true)
                return "the output initialization policy does not permit this scope";
            if (plan.Properties.Any(p => p.Proof.Writers.Any(w => w.PreservedInput))) return "a preserved input controls the configuration";
            if (!constraint.enabled || !constraint.IsActive || !constraint.Locked || constraint.FreezeToWorld || constraint.SmoothingFactor != 0)
                return "disabled, unlocked, frozen or smoothed solvers require a lifecycle proof";
            if (constraint is VRCAimConstraint || constraint is VRCLookAtConstraint)
                return "aim and look-at degeneracies need an independent solver-state proof";
            var planned = plan.Properties.Select(p => p.Name).ToHashSet();
            foreach (var use in index.ForObject(constraint))
                if (use.Binding.isPPtrCurve || !planned.Contains(use.Binding.propertyName) &&
                    !invariant.IsConstantProperty(constraint, use.Binding.propertyName))
                    return "a source reference, lifecycle switch or other solver setting remains dynamic or unsupported";
            foreach (string name in new[] { "m_Enabled", "IsActive", "Locked", "FreezeToWorld", "SmoothingFactor", "SolveInLocalSpace", "RebakeOffsetsWhenUnfrozen" })
                if (!invariant.IsConstantProperty(constraint, name)) return "solver lifecycle setting " + name + " is not invariant";
            using (var serialized = new SerializedObject(constraint))
                foreach (string group in constraint is VRCParentConstraint ? new[] { "Position", "Rotation" } :
                    constraint is VRCPositionConstraint ? new[] { "Position" } :
                    constraint is VRCRotationConstraint ? new[] { "Rotation" } : new[] { "Scale" })
                    foreach (char axis in "XYZ")
                    {
                        string name = "Affects" + group + axis;
                        if (serialized.FindProperty(name)?.boolValue != true || !invariant.IsConstantProperty(constraint, name))
                            return "partial or animated solve axes require a pose-feedback proof";
                    }
            var visibility = VisibilityMutationAnalysis.NativeWriters(index.Avatar);
            foreach (var start in new[] { constraint.transform, target })
                for (var current = start; current != null && current.IsChildOf(index.Avatar.transform); current = current.parent)
                    if (!current.gameObject.activeSelf || index.Writes(current.gameObject, "m_IsActive") || visibility.Contains(current.gameObject))
                        return "solver or target activation can change";
            if (readers.CullingFrames.Contains(target)) return "the target is an Animator culling input";
            var mutations = snapshot.Mutations;
            if (mutations.Uncertain != null || mutations.WrittenByOther(target, constraint)) return "the target has another or unknown native/animation writer";
            var graph = snapshot.Dependencies(false);
            if (graph.UnsafeReason != null || graph.ReferenceOwners.ContainsKey(constraint)) return "a runtime reference can observe the solver configuration";
            if (constraint.Sources.Count == 0) return "an empty-source solver has no explicit output proof";
            foreach (var source in constraint.Sources)
                if (source.SourceTransform == null || !source.SourceTransform.IsChildOf(index.Avatar.transform) ||
                    graph.DependsOn(source.SourceTransform, target)) return "a missing, external or feedback source has no initialization proof";
            foreach (var component in graph.Components)
            {
                if (component == constraint || component is Transform || component is INDMFEditorOnly || component is IEditorOnly ||
                    ObjectOptimizationSafety.IsParentChangeDetector(component)) continue;
                bool preserved = settings.Protected(component) || !settings.Scope.Contains(component.transform);
                if (!preserved && (component is Renderer || component is MeshFilter || component is Light)) continue;
                if (graph.DependsOn(component, target)) return "the target feeds " + (preserved ? "a preserved/out-of-scope consumer" : component.GetType().Name);
            }
            return null;
        }

        private static bool Known(Type type) => type == typeof(VRCParentConstraint) || type == typeof(VRCPositionConstraint) ||
            type == typeof(VRCRotationConstraint) || type == typeof(VRCScaleConstraint) || type == typeof(VRCAimConstraint) ||
            type == typeof(VRCLookAtConstraint);

        private static string[] Vector(string name) => new[] { name + ".x", name + ".y", name + ".z" };

        internal static bool ContinuousConfigurationProperty(VRCConstraintBase constraint, string property) =>
            Known(constraint.GetType()) && Groups(constraint).Any(group => group.Contains(property));

        private static IEnumerable<string[]> Groups(VRCConstraintBase constraint)
        {
            yield return new[] { "GlobalWeight" };
            for (int i = 0; i < Math.Min(16, constraint.Sources.Count); i++)
            {
                string source = "Sources.source" + i;
                yield return new[] { source + ".Weight" };
                if (constraint is VRCParentConstraint)
                { yield return Vector(source + ".ParentPositionOffset"); yield return Vector(source + ".ParentRotationOffset"); }
            }
            if (constraint is VRCParentConstraint || constraint is VRCPositionConstraint) yield return Vector("PositionAtRest");
            if (constraint is VRCPositionConstraint) yield return Vector("PositionOffset");
            if (constraint is VRCParentConstraint || constraint is VRCRotationConstraint || constraint is VRCAimConstraint || constraint is VRCLookAtConstraint)
                yield return Vector("RotationAtRest");
            if (constraint is VRCRotationConstraint || constraint is VRCAimConstraint || constraint is VRCLookAtConstraint) yield return Vector("RotationOffset");
            if (constraint is VRCScaleConstraint) { yield return Vector("ScaleAtRest"); yield return Vector("ScaleOffset"); }
            if (constraint is VRCAimConstraint)
            { yield return Vector("AimAxis"); yield return Vector("UpAxis"); yield return Vector("WorldUpVector"); }
            if (constraint is VRCLookAtConstraint) yield return new[] { "Roll" };
        }
    }
}
