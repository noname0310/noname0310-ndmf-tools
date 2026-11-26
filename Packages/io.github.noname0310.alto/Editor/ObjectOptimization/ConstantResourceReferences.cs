using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.ObjectOptimization
{
    // Reference selection is discrete. Static MeshFilter references have a native
    // animation contract. Skins additionally carry a bone/morph interface and
    // currently permit only removal of an identical mesh reference. Light cookies
    // are serialized but native sampling does not apply m_Cookie object curves;
    // retain changed cookie values rather than bake a nonfunctional track.
    // No mesh, texture, material or other referenced asset is modified.
    internal static class ConstantResourceReferences
    {
        internal static int Apply(ObjectAnalysisSnapshot snapshot, ObjectOptimizationState settings,
            Func<Transform, bool> allowInitialChanges, ICollection<string> messages, MeshOptimizationState meshSettings = null)
        {
            using var cost = AnalysisCost.Measure("Constant output consumer/resource references");
            var index = snapshot.Index;
            if (index.UnsafeReason != null || ObjectOptimizationSafety.UnsupportedRuntime(index.Avatar) != null) return 0;
            var plans = new List<(Component Target, string Property, ConstantPropertyProof Proof)>();
            InitialPoseReaders.Inputs readers = null;
            var nativeBindings = new NativeAnimationBindings(index.Avatar);
            foreach (var component in index.Avatar.GetComponentsInChildren<Component>(true))
            {
                if (component == null || !settings.Scope.Contains(component.transform) || settings.Protected(component)) continue;
                string property; Object initial; Renderer renderer = null;
                if (component.GetType() == typeof(MeshFilter))
                { property = "m_Mesh"; initial = ((MeshFilter)component).sharedMesh; renderer = component.GetComponent<MeshRenderer>(); }
                else if (component.GetType() == typeof(SkinnedMeshRenderer))
                { property = "m_Mesh"; initial = ((SkinnedMeshRenderer)component).sharedMesh; renderer = (Renderer)component; }
                else if (component.GetType() == typeof(Light))
                { property = "m_Cookie"; initial = ((Light)component).cookie; }
                else continue;
                if (!index.Writes(component, property)) continue;
                if (property == "m_Mesh" && (renderer == null || settings.Protected(renderer) ||
                    meshSettings != null && (meshSettings.Preserved.Contains(ObjectRegistry.GetReference(renderer)) ||
                        MeshOptimizationPass.InputProtected(meshSettings, renderer, out _)))) continue;
                if (!index.ConstantOutputs.TryProve(component, property, ConstantOutputValue.Object(initial), out var proof, out _)) continue;
                if (proof.Value.Reference != null && (property == "m_Mesh" ? !(proof.Value.Reference is Mesh) : !(proof.Value.Reference is Texture))) continue;
                if (component.GetComponent<Cloth>() != null)
                { Retain("Cloth owns the mesh lifetime"); continue; }
                if (proof.ChangesInitialValue)
                {
                    if (component is Light)
                    { Retain("native Animator sampling does not apply this serialized cookie reference binding"); continue; }
                    if (allowInitialChanges?.Invoke(component.transform) != true || proof.Writers.Any(w => w.PreservedInput))
                    { Retain("initialization permission is absent or a preserved input controls the reference"); continue; }
                    if (component is SkinnedMeshRenderer)
                    { Retain("a changed skin mesh requires a bone-palette, topology and BlendShape interface proof"); continue; }
                    if (component is MeshFilter && (component.GetComponent<MeshCollider>() != null || component.GetComponent<ParticleSystem>() != null))
                    { Retain("a colocated collision or emission component may initialize from the mesh"); continue; }
                    readers ??= snapshot.InitialReaders;
                    if (readers.CullingComponents.Contains(renderer != null ? (Component)renderer : component))
                    { Retain("the resource can change Animator visibility or shadow culling"); continue; }
                    if (readers.Components.Contains(component) || renderer != null && readers.Components.Contains(renderer))
                    { Retain("a native sensor or simulation reads this resource owner"); continue; }
                    var graph = snapshot.Dependencies(false);
                    if (graph.UnsafeReason != null || graph.ReferenceOwners.ContainsKey(component) ||
                        renderer != null && graph.ReferenceOwners.ContainsKey(renderer))
                    { Retain("a referenced component requires its authored resource initialization"); continue; }
                    if (renderer != null && renderer.GetComponents<Component>().Any(c => c != null && c != renderer &&
                        c != component && BuildFilterPass.Requires(c, renderer)))
                    { Retain("another native component requires the renderer"); continue; }
                    if (!nativeBindings.Supports(component, proof.Writers, out var nativeReason))
                    { Retain(nativeReason); continue; }
                }
                plans.Add((component, property, proof));

                void Retain(string reason) => messages.Add("Retained " + component.GetType().Name + "." + property + ": " + reason + ".");
            }
            var removals = new Dictionary<VirtualClip, HashSet<EditorCurveBinding>>();
            var appliedProofs = new AppliedProofReport();
            foreach (var plan in plans)
            {
                if (plan.Proof.ChangesInitialValue)
                {
                    using var serialized = new SerializedObject(plan.Target);
                    serialized.FindProperty(plan.Property).objectReferenceValue = plan.Proof.Value.Reference;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                foreach (var writer in plan.Proof.Writers)
                {
                    if (!removals.TryGetValue(writer.Clip, out var bindings)) removals.Add(writer.Clip, bindings = new());
                    bindings.Add(writer.Binding);
                }
                appliedProofs.Record(initializationChanged: plan.Proof.ChangesInitialValue);
            }
            int count = 0;
            foreach (var removal in removals) { MotionAnalysis.RemoveCurves(removal.Key, removal.Value); count += removal.Value.Count; }
            if (count != 0) messages.Add("Baked " + count + " constant mesh/cookie reference curves (" +
                plans.Count(p => p.Proof.ChangesInitialValue) + " changed initial references); source assets and skin interfaces were retained.");
            appliedProofs.AppendTo(messages, "constant resource references");
            return count;
        }
    }
}
