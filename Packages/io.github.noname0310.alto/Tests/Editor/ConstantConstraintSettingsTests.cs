using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private void InNativeConstraintScene(Action run)
        {
            var previous = SceneManager.GetActiveScene();
            string path = AssetDatabase.GUIDToAssetPath("4e3531870ac74a99b7804047f9db1d96");
            Assert.That(SceneManager.GetSceneByPath(path).isLoaded, Is.False);
            var native = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            SceneManager.MoveGameObjectToScene(root, native);
            try { WithIsolatedNativeConstraints(run); }
            finally
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(native, true);
            }
        }

        private AnimationClip ConstantConstraintClip(VRCConstraintBase constraint, string field, Vector3 value)
        {
            var clip = Clip();
            bool vector = field != "GlobalWeight" && !field.EndsWith(".Weight", StringComparison.Ordinal);
            for (int i = 0; i < (vector ? 3 : 1); i++)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(
                    AnimationUtility.CalculateTransformPath(constraint.transform, root.transform), constraint.GetType(),
                    field + (vector ? "." + "xyz"[i] : "")), AnimationCurve.Constant(0, .3f, value[i]));
            return clip;
        }

        [TestCase(typeof(VRCParentConstraint), "GlobalWeight")]
        [TestCase(typeof(VRCPositionConstraint), "GlobalWeight")]
        [TestCase(typeof(VRCRotationConstraint), "GlobalWeight")]
        [TestCase(typeof(VRCScaleConstraint), "GlobalWeight")]
        [TestCase(typeof(VRCParentConstraint), "Sources.source0.Weight")]
        [TestCase(typeof(VRCParentConstraint), "Sources.source1.Weight")]
        [TestCase(typeof(VRCParentConstraint), "Sources.source1.ParentPositionOffset")]
        [TestCase(typeof(VRCParentConstraint), "Sources.source1.ParentRotationOffset")]
        [TestCase(typeof(VRCPositionConstraint), "PositionOffset")]
        [TestCase(typeof(VRCRotationConstraint), "RotationOffset")]
        [TestCase(typeof(VRCScaleConstraint), "ScaleOffset")]
        [TestCase(typeof(VRCPositionConstraint), "PositionAtRest")]
        [TestCase(typeof(VRCRotationConstraint), "RotationAtRest")]
        [TestCase(typeof(VRCScaleConstraint), "ScaleAtRest")]
        public void ConstantConstraintSettings_NativeMovingSolverMatchesAnimatedConfiguration(Type type, string field)
        {
            InNativeConstraintScene(() =>
            {
                var settings = ObjectSettings(); var input = PolicyChild("Input").transform;
                var unused = PolicyChild("Other input").transform;
                var output = PolicyChild("Output").transform; var control = PolicyChild("Control").transform;
                input.localPosition = new Vector3(1, 2, 3); input.localRotation = Quaternion.Euler(20, 30, 40);
                input.localScale = new Vector3(2, 3, 4); unused.localPosition = new Vector3(-2, 3, 4);
                var constraint = ConstraintWithZeroSources(type, input, unused, output, "Writer");
                var reference = ConstraintWithZeroSources(type, input, unused, control, "Reference");
                settings.ProtectedComponents.Add(ObjectRegistry.GetReference(reference));
                if (field == "Sources.source0.Weight")
                    foreach (var c in new[] { constraint, reference })
                    { var source = c.Sources[0]; source.Weight = .5f; c.Sources[0] = source; c.ApplyConfigurationChanges(); }
                if (field.EndsWith("AtRest", StringComparison.Ordinal))
                    foreach (var c in new[] { constraint, reference }) { c.GlobalWeight = 0; c.ApplyConfigurationChanges(); }
                var value = field == "GlobalWeight" ? new Vector3(.75f, 0, 0) :
                    field == "Sources.source0.Weight" ? Vector3.zero : field == "Sources.source1.Weight" ? new Vector3(.5f, 0, 0) :
                    new Vector3(.2f, .3f, .4f);
                var clip = ConstantConstraintClip(constraint, field, value);
                var referenceClip = ConstantConstraintClip(reference, field, value);
                var index = ConstantOutputIndex(Controller(clip), out var controller);
                var snapshot = new ObjectAnalysisSnapshot(root, index.ControllerEntries, null, settings, null, null);
                int expected = field == "GlobalWeight" || field.EndsWith(".Weight") ? 1 : 3;
                Assert.That(snapshot.Rewrite(() => ConstantConstraintSettings.Apply(snapshot, settings, _ => true, new List<string>())), Is.EqualTo(expected));
                if (field.EndsWith(".Weight", StringComparison.Ordinal))
                {
                    Assert.That(ConstraintSourcePruning.Apply(root, index.ControllerEntries, null, settings, null,
                        new List<string>(), snapshot: snapshot), Is.GreaterThan(0));
                    Assert.That(constraint.Sources.Count, Is.EqualTo(field == "Sources.source0.Weight" ? 1 : 2),
                        "A fractional single survivor must keep multi-source normalization after constant baking");
                }
                referenceClip.SampleAnimation(root, 0); reference.ApplyConfigurationChanges();
                EvaluateNativeConstraints(constraint, reference);
                Assert.That(control.localToWorldMatrix, Is.Not.EqualTo(Matrix4x4.identity), "The SDK solver must affect the control fixture");
                foreach (var scale in new[] { Vector3.one, Vector3.one * 2.5f, new Vector3(2, 3, 4) })
                {
                    root.transform.localScale = scale; root.transform.localRotation = Quaternion.Euler(15, 25, 35);
                    input.localPosition += Vector3.one; input.localRotation *= Quaternion.Euler(10, 20, 30);
                    input.localScale += Vector3.one; unused.localPosition -= Vector3.one;
                    referenceClip.SampleAnimation(root, .1f); reference.ApplyConfigurationChanges();
                    EvaluateNativeConstraints();
                    for (int i = 0; i < 16; i++) Assert.That(output.localToWorldMatrix[i], Is.EqualTo(control.localToWorldMatrix[i]).Within(.00001f));
                }
                Assert.That(ConstantConstraintSettings.Apply(snapshot, settings, _ => true, new List<string>()), Is.Zero);
                Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(expected), "Source clips stay immutable");
            });
        }

        [TestCase("Contact")] [TestCase("PhysBone")] [TestCase("Audio")]
        [TestCase("Particle")] [TestCase("Culling")] [TestCase("Smoothing")]
        [TestCase("Frozen")] [TestCase("Unlocked")] [TestCase("DynamicLifecycle")]
        [TestCase("PartialAxes")] [TestCase("PreservedTarget")] [TestCase("NoPolicy")]
        public void ConstantConstraintSettings_ChangedInitializationKeepsPersistentConsumers(string reason)
        {
            var settings = ObjectSettings(); var input = PolicyChild("Input").transform;
            var target = PolicyChild("Output").transform;
            var constraint = ConstraintWithZeroSources(typeof(VRCParentConstraint), input, input, target, "Writer");
            var clip = ConstantConstraintClip(constraint, "GlobalWeight", new Vector3(.5f, 0, 0));
            if (reason == "Contact") PolicyChild("Reader", target).AddComponent<VRCContactReceiver>();
            if (reason == "PhysBone") PolicyChild("Reader", target).AddComponent<VRCPhysBone>();
            if (reason == "Audio") PolicyChild("Reader", target).AddComponent<AudioSource>();
            if (reason == "Particle") PolicyChild("Reader", target).AddComponent<ParticleSystem>();
            if (reason == "Culling")
            {
                target.gameObject.AddComponent<MeshRenderer>();
                root.AddComponent<Animator>().cullingMode = AnimatorCullingMode.CullCompletely;
            }
            if (reason == "Smoothing") constraint.SmoothingFactor = .5f;
            if (reason == "Frozen") constraint.FreezeToWorld = true;
            if (reason == "Unlocked") constraint.Locked = false;
            if (reason == "PartialAxes") ((VRCParentConstraint)constraint).AffectsRotationY = false;
            if (reason == "PreservedTarget") settings.ProtectedComponents.Add(ObjectRegistry.GetReference(target));
            if (reason == "DynamicLifecycle") AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(constraint.name, typeof(VRCParentConstraint), "m_Enabled"), AnimationCurve.Linear(0, 0, 1, 1));
            var index = ConstantOutputIndex(Controller(clip), out _);
            var snapshot = new ObjectAnalysisSnapshot(root, index.ControllerEntries, null, settings, null, null);
            Assert.That(ConstantConstraintSettings.Apply(snapshot, settings, _ => reason != "NoPolicy", new List<string>()), Is.Zero);
            Assert.That(constraint.GlobalWeight, Is.EqualTo(1));
        }

        [Test]
        public void ConstantConstraintSettings_EqualConfigurationUnlocksRedundantScaleRemoval()
        {
            var settings = ObjectSettings(); var input = PolicyChild("Input").transform; var target = PolicyChild("Output").transform;
            var constraint = LocalScaleConstraint(input, target);
            var index = ConstantOutputIndex(Controller(ConstantConstraintClip(constraint, "GlobalWeight", Vector3.right)), out _);
            var snapshot = new ObjectAnalysisSnapshot(root, index.ControllerEntries, null, settings, null, null);
            Assert.That(snapshot.Rewrite(() => ConstantConstraintSettings.Apply(snapshot, settings, _ => false, new List<string>())), Is.EqualTo(1));
            Assert.That(RedundantConstraints.Apply(root, index.ControllerEntries, null, settings, null, new List<string>(), snapshot: snapshot), Is.EqualTo(1));
            Assert.That(constraint == null, Is.True);
        }
    }
}
