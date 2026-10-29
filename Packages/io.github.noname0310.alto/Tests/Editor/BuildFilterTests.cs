using System;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ParameterPolicy;
using nadena.dev.ndmf;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private ALTOBuildExclusions Filter()
        {
            if (root.GetComponent<VRCAvatarDescriptor>() == null) root.AddComponent<VRCAvatarDescriptor>();
            var filter = root.AddComponent<ALTOBuildExclusions>();
            filter.ApplyOnPlatforms = OptimizationPlatforms.All;
            return filter;
        }

        [Test]
        public void BuildFilterRemovesSelectedSubtreesAndIndividualComponents()
        {
            var filter = Filter();
            var feature = PolicyChild("Feature");
            var child = PolicyChild("Nested generator", feature.transform);
            child.AddComponent<ParameterPolicySourceFixture>().Provided = "FeatureInput";
            var kept = PolicyChild("Kept");
            var collider = kept.AddComponent<BoxCollider>();
            filter.ExcludedObjects.Add(feature);
            filter.ExcludedObjects.Add(child);
            filter.ExcludedComponents.Add(collider);
            BuildFilterPass.RemoveEarly(new BuildContext(root, null));
            Assert.That(feature == null && child == null && collider == null, Is.True);
            Assert.That(kept != null, Is.True);
            Assert.That(root.GetComponent<ALTOBuildExclusions>(), Is.Null);
        }

        [Test]
        public void BuildFilterRejectsRootTransformAndForeignScopeWithoutDeletingAnything()
        {
            var filter = Filter();
            var feature = PolicyChild("Feature");
            filter.ExcludedObjects.Add(feature);
            filter.ExcludedObjects.Add(root);
            Assert.Throws<InvalidOperationException>(() => BuildFilterPass.RemoveEarly(new BuildContext(root, null)));
            Assert.That(feature != null, Is.True);
            filter.ExcludedObjects.Clear();
            filter.ExcludedComponents.Add(feature.transform);
            Assert.Throws<InvalidOperationException>(() => BuildFilterPass.RemoveEarly(new BuildContext(root, null)));
            filter.ExcludedComponents.Clear();
            filter.ScopeRoot = feature.transform;
            filter.ExcludedObjects.Add(PolicyChild("Outside scope"));
            Assert.Throws<InvalidOperationException>(() => BuildFilterPass.RemoveEarly(new BuildContext(root, null)));
        }

        [Test]
        public void ExplicitBoneExclusionCannotBreakRemainingSkinning()
        {
            var filter = Filter();
            var unused = PolicyChild("Unreferenced");
            var bone = PolicyChild("Referenced bone");
            renderer.bones = new[] { bone.transform };
            filter.ExcludedObjects.Add(unused);
            filter.ExcludedObjects.Add(bone);
            Assert.Throws<InvalidOperationException>(() => BuildFilterPass.RemoveEarly(new BuildContext(root, null)));
            Assert.That(unused != null && bone != null, Is.True);
        }

        [Test]
        public void ExplicitExclusionChecksConstraintSourcesOutsideItsScope()
        {
            var filter = Filter();
            var source = PolicyChild("Source");
            var target = PolicyChild("Target");
            var constraint = target.AddComponent<ParentConstraint>();
            constraint.AddSource(new ConstraintSource { sourceTransform = source.transform, weight = 1 });
            filter.ScopeRoot = source.transform;
            filter.ExcludedObjects.Add(source);
            Assert.Throws<InvalidOperationException>(() => BuildFilterPass.RemoveEarly(new BuildContext(root, null)));
            Assert.That(source != null, Is.True);
            filter.ScopeRoot = null;
            filter.ExcludedComponents.Add(constraint);
            BuildFilterPass.RemoveEarly(new BuildContext(root, null));
            Assert.That(source == null && constraint == null && target != null, Is.True);
        }

        [Test]
        public void BuildFilterValidatesRequireComponentAndRemovesDependantsFirst()
        {
            var filter = Filter();
            var feature = PolicyChild("Physics feature");
            var joint = feature.AddComponent<FixedJoint>();
            var body = feature.GetComponent<Rigidbody>();
            filter.ExcludedComponents.Add(body);
            Assert.Throws<InvalidOperationException>(() => BuildFilterPass.RemoveEarly(new BuildContext(root, null)));
            Assert.That(body != null && joint != null, Is.True);
            filter.ExcludedComponents.Add(joint);
            BuildFilterPass.RemoveEarly(new BuildContext(root, null));
            Assert.That(body == null && joint == null && feature != null, Is.True);
        }

        [Test]
        public void BuildFilterRejectsPreservedControllerSourceExclusion()
        {
            var policy = Policy();
            var filter = Filter();
            var feature = PolicyChild("Preserved feature");
            var controller = Controller(Clip()); controller.AddParameter("Preserved", AnimatorControllerParameterType.Float);
            feature.AddComponent<ParameterPolicySourceFixture>().Controller = controller;
            policy.PreserveParameters.Sources.Add(new AnimatorParameterSource { Source = controller });
            filter.ExcludedObjects.Add(feature);
            Assert.Throws<InvalidOperationException>(() => BuildFilterPass.RemoveEarly(new BuildContext(root, null)));
            Assert.That(feature != null, Is.True);
        }

        [Test]
        public void BuildFilterChecksManuallyPreservedNamesAfterSourceRemapping()
        {
            var policy = Policy();
            var filter = Filter();
            var feature = PolicyChild("Generator").AddComponent<ParameterPolicySourceFixture>();
            feature.Provided = feature.From = "Local"; feature.To = "Final";
            policy.PreserveParameters.Names.Add("Final");
            filter.ExcludedObjects.Add(feature.gameObject);
            Assert.Throws<InvalidOperationException>(() => BuildFilterPass.RemoveEarly(new BuildContext(root, null)));
        }

        [Test]
        public void BuildFilterAllowsSourceWhenEveryParameterWasExplicitlyExcludedFromPreservation()
        {
            var policy = Policy();
            var filter = Filter();
            var feature = PolicyChild("Excluded source").AddComponent<ParameterPolicySourceFixture>();
            feature.Provided = "Input";
            policy.PreserveParameters.Sources.Add(new AnimatorParameterSource { Source = feature.gameObject, ExcludedNames = new() { "Input" } });
            filter.ExcludedObjects.Add(feature.gameObject);
            BuildFilterPass.RemoveEarly(new BuildContext(root, null));
            Assert.That(feature == null, Is.True);
        }

        [Test]
        public void DisabledBuildFilterLeavesItsTargetsAndRemovesItsMarker()
        {
            var filter = Filter(); filter.enabled = false;
            var feature = PolicyChild("Feature");
            filter.ExcludedObjects.Add(feature);
            BuildFilterPass.RemoveEarly(new BuildContext(root, null));
            Assert.That(feature != null && root.GetComponent<ALTOBuildExclusions>() == null, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BuildFilterSkipsFreezeRulesFromRemovedSourcesWithoutFreezingAnotherNamespace(bool componentOnly)
        {
            var policy = Policy();
            var filter = Filter();
            var feature = PolicyChild("Removed source").AddComponent<ParameterPolicySourceFixture>();
            feature.Provided = feature.From = "Input"; feature.To = "Removed/Input";
            var survivor = PolicyChild("Surviving source").AddComponent<ParameterPolicySourceFixture>();
            survivor.Provided = "Input";
            policy.FreezeParameters.Add(new AnimatorParameterConstant
            { Source = componentOnly ? feature : feature.gameObject, Name = "Input", Value = 1 });
            policy.FreezeParameters.Add(new AnimatorParameterConstant { Name = "Unrelated", Value = 0 });
            if (componentOnly) filter.ExcludedComponents.Add(feature);
            else filter.ExcludedObjects.Add(feature.gameObject);
            var context = new BuildContext(root, null);
            BuildFilterPass.RemoveEarly(context);
            ParameterPolicyPass.Capture(context);
            Assert.That(feature == null && survivor != null, Is.True);
            Assert.That(context.GetState<ParameterPolicyState>().Frozen.Keys, Is.EquivalentTo(new[] { "Unrelated" }));
            Assert.That(context.GetState<ParameterPolicyState>().Diagnostics,
                Does.Contain("Skipped frozen parameter from a removed source: Input"));
        }
    }
}
