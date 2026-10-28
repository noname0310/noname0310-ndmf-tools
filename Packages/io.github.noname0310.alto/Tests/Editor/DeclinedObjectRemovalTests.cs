using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDK3.Dynamics.Constraint.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private ObjectOptimizationState DeclinedRemovalSettings()
        {
            var settings = ObjectSettings();
            Object.DestroyImmediate(settings.Component);
            settings.RuntimeDependenciesOnly = true;
            settings.DestroyObject = _ => { };
            return settings;
        }

        [Test]
        public void DeclinedRemovalDoesNotReportProgressOrEraseComponentCurves()
        {
            var settings = DeclinedRemovalSettings();
            var marker = root.AddComponent<DependencyAuthoringFixture>();
            var binding = EditorCurveBinding.FloatCurve("", typeof(DependencyAuthoringFixture), "m_Enabled");
            var source = StaticCurve(binding, 1);
            var entries = ConstraintControllers(source);
            var clip = entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single();
            for (int iteration = 0; iteration < 3; iteration++)
            {
                Assert.That(UnusedObjectCollection.Apply(root, entries, null, settings, null, new List<string>()), Is.Zero);
                Assert.That(marker != null, Is.True);
                Assert.That(clip.GetFloatCurveBindings(), Does.Contain(binding));
            }
            Assert.That(AnimationUtility.GetCurveBindings(source), Does.Contain(binding));
        }

        [TestCase(false)] [TestCase(true)]
        public void DeclinedRemovalRetainsSubtreeCurvesWhenDeletionIsRejectedOrThrows(bool throws)
        {
            var settings = DeclinedRemovalSettings();
            var parent = PolicyChild("Retained subtree"); var child = PolicyChild("Child", parent.transform);
            var source = InitialTransformClip(child.transform); var entries = ConstraintControllers(source);
            var clip = entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single();
            var bindings = clip.GetFloatCurveBindings().ToArray();
            if (throws) settings.DestroyObject = _ => throw new InvalidOperationException("Deletion rejected.");
            int Run() => UnusedObjectCollection.Apply(root, entries, null, settings, null, new List<string>());
            if (throws) Assert.Throws<InvalidOperationException>(() => Run()); else Assert.That(Run(), Is.Zero);
            Assert.That(parent != null && child != null, Is.True);
            Assert.That(clip.GetFloatCurveBindings(), Is.EquivalentTo(bindings));
            Assert.That(MotionAnalysis.EndTime(clip), Is.EqualTo(.3f));
        }

        [Test]
        public void DeclinedRemovalCountsOnlySuccessfulSubtreesAndTheirBindings()
        {
            var settings = DeclinedRemovalSettings();
            var retained = PolicyChild("Retained"); var removed = PolicyChild("Removed");
            var a = EditorCurveBinding.FloatCurve(retained.name, typeof(Transform), "m_LocalPosition.x");
            var b = EditorCurveBinding.FloatCurve(removed.name, typeof(Transform), "m_LocalPosition.x");
            var source = StaticCurve(a, 0); AnimationUtility.SetEditorCurve(source, b, AnimationCurve.Constant(0, 1, 0));
            var entries = ConstraintControllers(source); var messages = new List<string>();
            settings.DestroyObject = target => { if (target == removed) Object.DestroyImmediate(target); };
            Assert.That(UnusedObjectCollection.Apply(root, entries, null, settings, null, messages), Is.EqualTo(2));
            Assert.That(retained != null && removed == null, Is.True);
            var clip = entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(clip.GetFloatCurveBindings(), Does.Contain(a));
            Assert.That(clip.GetFloatCurveBindings(), Has.No.Member(b));
            Assert.That(UnusedObjectCollection.Apply(root, entries, null, settings, null, messages), Is.Zero);
        }

        [Test]
        public void DeclinedRemovalRetainsDependenciesOfAComponentThatSurvives()
        {
            var settings = DeclinedRemovalSettings();
            var owner = PolicyChild("Retained authoring owner");
            var marker = owner.AddComponent<RequiredSdkAuthoringFixture>();
            var dependency = owner.GetComponent<PositionConstraint>();
            var attempted = new List<Object>();
            settings.DestroyObject = target =>
            {
                attempted.Add(target);
                if (target != marker && target != owner) Object.DestroyImmediate(target);
            };
            Assert.That(UnusedObjectCollection.Apply(root, ConstraintControllers(), null, settings, null, new List<string>()), Is.Zero);
            Assert.That(marker != null && dependency != null, Is.True);
            Assert.That(attempted, Does.Contain(marker));
            Assert.That(attempted, Does.Contain(owner));
            Assert.That(attempted, Has.No.Member(dependency));
        }

        [Test]
        public void DeclinedRemovalOfAComponentStillAllowsAnUnobservedSubtreeToBeRemoved()
        {
            var settings = DeclinedRemovalSettings();
            var owner = PolicyChild("Unobserved"); var marker = owner.AddComponent<DependencyAuthoringFixture>();
            var binding = EditorCurveBinding.FloatCurve(owner.name, typeof(DependencyAuthoringFixture), "m_Enabled");
            var entries = ConstraintControllers(StaticCurve(binding, 1));
            settings.DestroyObject = target => { if (target is GameObject) Object.DestroyImmediate(target); };
            Assert.That(UnusedObjectCollection.Apply(root, entries, null, settings, null, new List<string>()), Is.EqualTo(3));
            Assert.That(owner == null && marker == null, Is.True);
            Assert.That(entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurveBindings(), Has.No.Member(binding));
        }

        [Test]
        public void DeclinedRemovalOfAnUnusedConstraintKeepsItsAnimation()
        {
            var settings = DeclinedRemovalSettings();
            var owner = PolicyChild("Unused constraint"); var constraint = owner.AddComponent<PositionConstraint>();
            var binding = EditorCurveBinding.FloatCurve(owner.name, typeof(PositionConstraint), "m_Weight");
            var entries = ConstraintControllers(StaticCurve(binding, 1));
            Assert.That(UnusedTransformOutputs.Apply(root, entries, null, settings, null, new List<string>()), Is.Zero);
            Assert.That(constraint != null, Is.True);
            Assert.That(entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurveBindings(), Does.Contain(binding));
        }

        [Test]
        public void DeclinedRemovalOfRedundantScaleDoesNotReportProgress()
        {
            var settings = DeclinedRemovalSettings();
            var input = PolicyChild("Input").transform; var output = PolicyChild("Output").transform;
            var constraint = LocalScaleConstraint(input, output);
            Assert.That(RedundantConstraints.Apply(root, ConstraintControllers(), null, settings, null, new List<string>()), Is.Zero);
            Assert.That(constraint != null, Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void DeclinedRemovalOfParentConstraintKeepsParentPoseAndConstantCurves(bool atRest)
        {
            var settings = DeclinedRemovalSettings();
            var input = PolicyChild("Input").transform; var output = PolicyChild("Output").transform;
            var constraint = FollowingParent(input, output);
            if (atRest) constraint.GlobalWeight = 0;
            var binding = EditorCurveBinding.FloatCurve(constraint.name, typeof(VRCParentConstraint), "GlobalWeight");
            var entries = ConstraintControllers(StaticCurve(binding, constraint.GlobalWeight));
            var parent = output.parent; var pose = output.localToWorldMatrix;
            Assert.That(ParentConstraintOptimization.Apply(root, entries, null, settings, null, new List<string>()), Is.Zero);
            Assert.That(constraint != null, Is.True); Assert.That(output.parent, Is.SameAs(parent));
            Assert.That(output.localToWorldMatrix, Is.EqualTo(pose));
            Assert.That(entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurveBindings(), Does.Contain(binding));
        }

        [Test]
        public void DeclinedRemovalDoesNotRepeatTheFinalizerUntilADeletionSucceeds()
        {
            var settings = DeclinedRemovalSettings(); settings.RemoveUnusedObjects = true;
            var marker = root.AddComponent<DependencyAuthoringFixture>();
            var entries = ConstraintControllers(); int attempts = 0;
            settings.DestroyObject = _ =>
            {
                if (++attempts > 10) throw new InvalidOperationException("The finalizer retried an unchanged graph.");
            };
            var report = new OptimizationBuildReport();
            FinalOutputOptimization.Apply(root, entries, new ResolvedParameterPolicy(), new HashSet<string>(),
                settings, null, null, report, _ => { }, null);
            Assert.That(marker != null, Is.True);
            Assert.That(attempts, Is.EqualTo(1));
        }

        [Test]
        public void DeclinedRemovalHandlesADeletionCallbackThatRemovesItsWholeSubtree()
        {
            var settings = DeclinedRemovalSettings();
            var owner = PolicyChild("Unobserved"); var marker = owner.AddComponent<DependencyAuthoringFixture>();
            var binding = EditorCurveBinding.FloatCurve(owner.name, typeof(DependencyAuthoringFixture), "m_Enabled");
            var entries = ConstraintControllers(StaticCurve(binding, 1));
            settings.DestroyObject = target =>
            {
                if (target == marker) Object.DestroyImmediate(owner); else Object.DestroyImmediate(target);
            };
            Assert.That(UnusedObjectCollection.Apply(root, entries, null, settings, null, new List<string>()), Is.EqualTo(3));
            Assert.That(owner == null && marker == null, Is.True);
        }

        [Test]
        public void DeclinedRemovalKeepsSharedBindingsWhileAnyAnimationOwnerStillHasATarget()
        {
            var settings = DeclinedRemovalSettings();
            var ownerA = PolicyChild("Owner A"); var ownerB = PolicyChild("Owner B");
            var targetA = PolicyChild("Target", ownerA.transform); var targetB = PolicyChild("Target", ownerB.transform);
            var binding = EditorCurveBinding.FloatCurve("Target", typeof(Transform), "m_LocalPosition.x");
            var entries = ConstraintControllers(StaticCurve(binding, 0)); var shared = entries.Values.Single(); entries.Clear();
            entries.Add(ownerA.AddComponent<Animator>(), shared); entries.Add(ownerB.AddComponent<Animator>(), shared);
            settings.DestroyObject = target => { if (target == targetA) Object.DestroyImmediate(target); };
            Assert.That(UnusedObjectCollection.Apply(root, entries, null, settings, null, new List<string>()), Is.EqualTo(1));
            Assert.That(targetA == null && targetB != null, Is.True);
            Assert.That(shared.AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurveBindings(), Does.Contain(binding));
            Assert.That(UnusedObjectCollection.Apply(root, entries, null, settings, null, new List<string>()), Is.Zero);
        }
    }
}
