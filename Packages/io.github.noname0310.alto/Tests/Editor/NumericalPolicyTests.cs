using System;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(NumericalOptimization.AnimationOutputs)]
        [TestCase(NumericalOptimization.BlendShapes)]
        [TestCase(NumericalOptimization.TransformMerging)]
        [TestCase(NumericalOptimization.ComponentHoisting)]
        [TestCase(NumericalOptimization.Constraints)]
        [TestCase(NumericalOptimization.MeshMerging)]
        [TestCase(NumericalOptimization.Skinning)]
        public void NumericalPolicy_FeatureChoicesAreIndependent(NumericalOptimization strictFeature)
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var component = root.AddComponent<ALTOOptimizationPolicy>();
            Assert.That(component.AllowedNumericalDifferences, Is.EqualTo(NumericalOptimization.All));
            component.AllowedNumericalDifferences &= ~strictFeature;
            var context = new BuildContext(root, null); OptimizationPolicy.Capture(context);
            var policy = context.GetState<OptimizationPolicy>();
            Assert.That(policy.AllowsSmallNumericalDifferences(renderer.transform, strictFeature), Is.False);
            Assert.That(policy.AllowsSmallNumericalDifferences(renderer.transform, NumericalOptimization.All & ~strictFeature), Is.True);
            Assert.That(policy.HasStrictNumericalConstraintsFor(strictFeature), Is.True);
            Assert.That(policy.HasStrictNumericalConstraintsFor(NumericalOptimization.All & ~strictFeature), Is.False);
        }

        [Test]
        public void NumericalPolicy_SharedOutputsIntersectFeaturesWithoutClearingUnrelatedPermissions()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var first = PolicyChild("First");
            first.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences &= ~NumericalOptimization.Skinning;
            var second = PolicyChild("Second");
            second.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences &= ~NumericalOptimization.Constraints;
            var merged = PolicyChild("Merged");
            var final = PolicyChild("Final");
            var context = new BuildContext(root, null); OptimizationPolicy.Capture(context);
            var policy = context.GetState<OptimizationPolicy>();
            policy.InheritNumericalConstraints(merged.transform, new[] { first.transform, second.transform });
            UnityEngine.Object.DestroyImmediate(first); UnityEngine.Object.DestroyImmediate(second);
            policy.InheritNumericalConstraints(final.transform, new[] { merged.transform });
            UnityEngine.Object.DestroyImmediate(merged);
            var expected = NumericalOptimization.All & ~(NumericalOptimization.Skinning | NumericalOptimization.Constraints);
            Assert.That(policy.NumericalPermissionsFor(final.transform), Is.EqualTo(expected));
            Assert.That(policy.AllowsSmallNumericalDifferences(final.transform, NumericalOptimization.AnimationOutputs | NumericalOptimization.BlendShapes), Is.True);
            Assert.That(policy.AllowsSmallNumericalDifferences(final.transform, NumericalOptimization.Skinning), Is.False);
            Assert.That(policy.AllowsSmallNumericalDifferences(final.transform, NumericalOptimization.Constraints), Is.False);
        }

        [Test]
        public void NumericalPolicy_DefaultContractIsIndependentOfInitializationPermission()
        {
            var policy = new OptimizationPolicy();
            Assert.That(policy.AllowsSmallNumericalDifferences(root.transform, NumericalOptimization.All), Is.True);
            Assert.That(policy.AllowsInitialValueChanges(root.transform), Is.False);
            float next = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(1f) + 1);
            Assert.That(NumericalEquivalence.Same(1, next), Is.True);
            Assert.That(NumericalEquivalence.Same(1, next, false), Is.False);
            Assert.That(NumericalEquivalence.Same(1, 1.0001f), Is.False);
            Assert.That(NumericalEquivalence.Same(1000000, 1000000.5f), Is.True);
            Assert.That(NumericalEquivalence.Same(1000000, 1000002f), Is.False);
            Assert.That(NumericalEquivalence.Same(0, BitConverter.Int32BitsToSingle(int.MinValue)), Is.True);
            Assert.That(NumericalEquivalence.Same(0, BitConverter.Int32BitsToSingle(int.MinValue), false), Is.False);
        }

        [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(float.NegativeInfinity)]
        public void NumericalPolicy_NonFiniteValuesNeverBecomeEquivalent(float value)
        {
            Assert.That(NumericalEquivalence.Same(value, value), Is.False);
            Assert.That(NumericalEquivalence.Same(value, value, false), Is.False);
            Assert.That(NumericalEquivalence.AcceptsError(value, 0), Is.False);
        }

        [Test]
        public void NumericalPolicy_ComposedErrorBudgetIsBounded()
        {
            Assert.That(NumericalEquivalence.AcceptsError(1, 0.5e-6), Is.True);
            Assert.That(NumericalEquivalence.AcceptsError(1, 1.1e-6), Is.False);
            Assert.That(NumericalEquivalence.AcceptsError(1, 0.5e-6, false), Is.False);
            Assert.That(NumericalEquivalence.AcceptsError(1, 0, false), Is.True);
            Assert.That(NumericalEquivalence.AcceptsError(1, -1), Is.False);
            Assert.That(NumericalEquivalence.AcceptsError(1, double.NaN), Is.False);
            Assert.That(NumericalEquivalence.AcceptsError(1, double.PositiveInfinity), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void NumericalPolicy_NearestScopeSurvivesHierarchyRelocation(bool nestedAllows)
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var outer = PolicyChild("Strict");
            var nested = PolicyChild("Nested"); nested.transform.SetParent(outer.transform, false);
            var leaf = PolicyChild("Leaf"); leaf.transform.SetParent(nested.transform, false);
            outer.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences = NumericalOptimization.None;
            nested.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences = nestedAllows ? NumericalOptimization.All : NumericalOptimization.None;
            var context = new BuildContext(root, null); OptimizationPolicy.Capture(context);
            var policy = context.GetState<OptimizationPolicy>();
            Assert.That(policy.AllowsSmallNumericalDifferences(root.transform, NumericalOptimization.All), Is.True);
            Assert.That(policy.AllowsSmallNumericalDifferences(outer.transform, NumericalOptimization.All), Is.False);
            Assert.That(policy.AllowsSmallNumericalDifferences(leaf.transform, NumericalOptimization.All), Is.EqualTo(nestedAllows));
            leaf.transform.SetParent(root.transform, false);
            Assert.That(policy.AllowsSmallNumericalDifferences(leaf.transform, NumericalOptimization.All), Is.EqualTo(nestedAllows));
            Assert.That(root.GetComponentsInChildren<ALTOOptimizationPolicy>(true), Is.Empty);
        }

        [TestCase(false)] [TestCase(true)]
        public void NumericalPolicy_InapplicableSettingsDoNotChangeBaseline(bool enabled)
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var component = root.AddComponent<ALTOOptimizationPolicy>();
            component.AllowedNumericalDifferences = NumericalOptimization.None;
            component.enabled = enabled;
            component.ApplyOnPlatforms = enabled ? OptimizationPlatforms.None : OptimizationPlatforms.All;
            var context = new BuildContext(root, null); OptimizationPolicy.Capture(context);
            Assert.That(context.GetState<OptimizationPolicy>().AllowsSmallNumericalDifferences(renderer.transform, NumericalOptimization.All), Is.True);
        }

        [Test]
        public void NumericalPolicy_ConflictingScopesFailInsteadOfDependingOnComponentOrder()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var first = PolicyChild("First").AddComponent<ALTOOptimizationPolicy>(); first.ScopeRoot = root.transform;
            var second = PolicyChild("Second").AddComponent<ALTOOptimizationPolicy>(); second.ScopeRoot = root.transform;
            second.AllowedNumericalDifferences = NumericalOptimization.None;
            Assert.Throws<InvalidOperationException>(() => OptimizationPolicy.Capture(new BuildContext(root, null)));
        }

        [Test]
        public void NumericalPolicy_SharedOutputsRetainStrictContributorAfterSourceRemoval()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var strict = PolicyChild("StrictInput");
            strict.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences = NumericalOptimization.None;
            var relaxed = PolicyChild("RelaxedInput");
            var merged = PolicyChild("Merged");
            var final = PolicyChild("Final");
            var unrelated = PolicyChild("Unrelated");
            var context = new BuildContext(root, null); OptimizationPolicy.Capture(context);
            var policy = context.GetState<OptimizationPolicy>();
            policy.InheritNumericalConstraints(merged.transform, new[] { relaxed.transform, strict.transform });
            UnityEngine.Object.DestroyImmediate(strict);
            Assert.That(policy.AllowsSmallNumericalDifferences(merged.transform, NumericalOptimization.All), Is.False);
            policy.InheritNumericalConstraints(final.transform, new[] { merged.transform });
            UnityEngine.Object.DestroyImmediate(merged);
            Assert.That(policy.AllowsSmallNumericalDifferences(final.transform, NumericalOptimization.All), Is.False);
            Assert.That(policy.AllowsSmallNumericalDifferences(relaxed.transform, NumericalOptimization.All), Is.True);
            Assert.That(policy.AllowsSmallNumericalDifferences(unrelated.transform, NumericalOptimization.All), Is.True);
        }

        [Test]
        public void NumericalPolicy_AuthoredStrictScopeCannotBeReplacedByDeeperDestinationScope()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var strict = PolicyChild("Strict");
            var strictPolicy = strict.AddComponent<ALTOOptimizationPolicy>();
            strictPolicy.AllowedNumericalDifferences = NumericalOptimization.None; strictPolicy.AllowInitialValueChanges = false;
            var leaf = PolicyChild("Leaf"); leaf.transform.SetParent(strict.transform, false);
            var container = PolicyChild("Container");
            var relaxed = PolicyChild("DeepRelaxed"); relaxed.transform.SetParent(container.transform, false);
            relaxed.AddComponent<ALTOOptimizationPolicy>();
            var context = new BuildContext(root, null); OptimizationPolicy.Capture(context);
            var policy = context.GetState<OptimizationPolicy>();
            leaf.transform.SetParent(relaxed.transform, false);
            Assert.That(policy.AllowsSmallNumericalDifferences(leaf.transform, NumericalOptimization.All), Is.False);
            Assert.That(policy.AllowsInitialValueChanges(leaf.transform), Is.False);
            Assert.That(policy.AllowsSmallNumericalDifferences(relaxed.transform, NumericalOptimization.All), Is.True);
        }

        [Test]
        public void NumericalPolicy_GeneratedOutputLatchesItsOwnStrictContractBeforeRelocation()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var strict = PolicyChild("Strict");
            strict.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences = NumericalOptimization.None;
            var input = PolicyChild("RelaxedInput");
            var context = new BuildContext(root, null); OptimizationPolicy.Capture(context);
            var policy = context.GetState<OptimizationPolicy>();
            var generated = PolicyChild("GeneratedAfterCapture"); generated.transform.SetParent(strict.transform, false);
            policy.InheritNumericalConstraints(generated.transform, new[] { input.transform });
            generated.transform.SetParent(root.transform, false);
            Assert.That(policy.AllowsSmallNumericalDifferences(generated.transform, NumericalOptimization.All), Is.False);
            Assert.That(policy.AllowsSmallNumericalDifferences(input.transform, NumericalOptimization.All), Is.True);
            Assert.That(policy.HasStrictNumericalConstraints, Is.True);
        }

        [Test]
        public void NumericalPolicy_GeneratedOutputsUseLiveNearestScopeAfterScopeRootRelocation()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var strict = PolicyChild("ShallowStrict");
            var strictPolicy = strict.AddComponent<ALTOOptimizationPolicy>();
            strictPolicy.AllowInitialValueChanges = false; strictPolicy.AllowedNumericalDifferences = NumericalOptimization.None;
            var container = PolicyChild("Container");
            var relaxed = PolicyChild("DeeperRelaxed"); relaxed.transform.SetParent(container.transform, false);
            relaxed.AddComponent<ALTOOptimizationPolicy>();
            var context = new BuildContext(root, null); OptimizationPolicy.Capture(context);
            strict.transform.SetParent(relaxed.transform, false);
            var generated = PolicyChild("Generated"); generated.transform.SetParent(strict.transform, false);
            var policy = context.GetState<OptimizationPolicy>();
            Assert.That(policy.AllowsSmallNumericalDifferences(generated.transform, NumericalOptimization.All), Is.False);
            Assert.That(policy.AllowsInitialValueChanges(generated.transform), Is.False);
        }
    }
}
