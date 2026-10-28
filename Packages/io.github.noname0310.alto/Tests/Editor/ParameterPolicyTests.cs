using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ParameterPolicy;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private GameObject PolicyChild(string name, Transform parent = null)
        {
            var child = Track(new GameObject(name));
            child.transform.SetParent(parent != null ? parent : root.transform, false);
            return child;
        }

        private ALTOAnimatorParameterPolicy Policy(GameObject target = null)
        {
            if (root.GetComponent<VRCAvatarDescriptor>() == null) root.AddComponent<VRCAvatarDescriptor>();
            var policy = (target != null ? target : root).AddComponent<ALTOAnimatorParameterPolicy>();
            policy.ApplyOnPlatforms = OptimizationPlatforms.All;
            return policy;
        }

        [TestCase(AnimatorControllerParameterType.Bool, AnimatorControllerParameterType.Float, 1f, 1f, true)]
        [TestCase(AnimatorControllerParameterType.Bool, AnimatorControllerParameterType.Float, 0f, 0f, true)]
        [TestCase(AnimatorControllerParameterType.Int, AnimatorControllerParameterType.Float, 3f, 3f, true)]
        [TestCase(AnimatorControllerParameterType.Bool, AnimatorControllerParameterType.Float, 1f, 0f, false)]
        [TestCase(AnimatorControllerParameterType.Float, AnimatorControllerParameterType.Bool, .5f, 1f, false)]
        [TestCase(AnimatorControllerParameterType.Float, AnimatorControllerParameterType.Int, .5f, 0f, false)]
        [TestCase(AnimatorControllerParameterType.Bool, AnimatorControllerParameterType.Trigger, 1f, 1f, false)]
        public void PrivateParameterConstantAcceptsOnlyValuePreservingTypeChanges(
            AnimatorControllerParameterType original, AnimatorControllerParameterType rewritten, float expected, float actual, bool allowed)
        {
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip()));
            var constant = new ParameterConstant { Name = "Public input", InternalName = "Private constant", Type = original,
                Types = new HashSet<AnimatorControllerParameterType> { original }, Value = expected };
            var declaration = new AnimatorControllerParameter { name = constant.InternalName, type = rewritten,
                defaultFloat = actual, defaultInt = (int)actual, defaultBool = actual != 0 };
            controller.Parameters = controller.Parameters.Add(declaration.name, declaration);
            var constants = new Dictionary<string, ParameterConstant> { [constant.Name] = constant };
            if (allowed) Assert.DoesNotThrow(() => ParameterSpecialization.Validate(new[] { controller }, constants));
            else Assert.Throws<InvalidOperationException>(() => ParameterSpecialization.Validate(new[] { controller }, constants));
        }

        [Test]
        public void PolicyScopeUsesComponentSubtreeAndTracksReparentedObjects()
        {
            var container = PolicyChild("Scope");
            var child = PolicyChild("Child", container.transform);
            var policy = Policy(container);
            var scope = OptimizationBuildScope.Capture(policy, root);
            Assert.That(scope.Root, Is.EqualTo(container.transform));
            Assert.That(scope.Contains(root.transform), Is.False);
            child.transform.SetParent(root.transform, false);
            Assert.That(scope.Contains(child.transform), Is.True);
            policy.ScopeRoot = root.transform;
            Assert.That(OptimizationBuildScope.Capture(policy, root).Contains(root.transform), Is.True);
        }

        [Test]
        public void PolicyRejectsForeignAndNestedAvatarScopes()
        {
            var policy = Policy();
            var other = Track(new GameObject("Other avatar"));
            policy.ScopeRoot = other.transform;
            Assert.Throws<InvalidOperationException>(() => OptimizationBuildScope.Capture(policy, root));
            var nested = PolicyChild("Nested"); nested.AddComponent<VRCAvatarDescriptor>();
            policy.ScopeRoot = nested.transform;
            Assert.Throws<InvalidOperationException>(() => OptimizationBuildScope.Capture(policy, root));
            policy.ScopeRoot = root.transform;
            Assert.Throws<InvalidOperationException>(() => OptimizationBuildScope.Capture(policy, root));
        }

        [Test]
        public void NewPolicyIsAndroidOnlyAndIgnoresDisabledOrEditorOnlyComponents()
        {
            var policy = root.AddComponent<ALTOAnimatorParameterPolicy>();
            Assert.That(OptimizationBuildScope.Applies(policy, BuildTarget.Android), Is.True);
            Assert.That(OptimizationBuildScope.Applies(policy, BuildTarget.StandaloneWindows64), Is.False);
            policy.enabled = false;
            Assert.That(OptimizationBuildScope.Applies(policy, BuildTarget.Android), Is.False);
            policy.enabled = true; root.tag = "EditorOnly";
            Assert.That(OptimizationBuildScope.Applies(policy, BuildTarget.Android), Is.False);
        }

        [Test]
        public void ControllerSelectionRescansNewDeclarations()
        {
            var controller = Controller(Clip()); controller.AddParameter("A", AnimatorControllerParameterType.Float);
            var owner = PolicyChild("Source").AddComponent<Animator>(); owner.runtimeAnimatorController = controller;
            var source = new AnimatorParameterSource { Source = controller };
            Assert.That(ParameterSourceResolver.Resolve(source, root).Parameters.Select(p => p.Name), Is.EquivalentTo(new[] { "A" }));
            controller.AddParameter("B", AnimatorControllerParameterType.Bool);
            Assert.That(ParameterSourceResolver.Resolve(source, root).Parameters.Select(p => p.Name), Is.EquivalentTo(new[] { "A", "B" }));
        }

        [Test]
        public void ObjectSourceFindsSerializedControllersWithoutIncludingSiblingOrEditorOnlyTrees()
        {
            var container = PolicyChild("Source"); var child = PolicyChild("Child", container.transform);
            var hidden = PolicyChild("Hidden", container.transform); hidden.tag = "EditorOnly";
            var a = Controller(Clip()); a.AddParameter("A", AnimatorControllerParameterType.Float);
            var b = Controller(Clip()); b.AddParameter("B", AnimatorControllerParameterType.Float);
            var hiddenController = Controller(Clip()); hiddenController.AddParameter("Hidden", AnimatorControllerParameterType.Float);
            container.AddComponent<ParameterPolicySourceFixture>().Controller = a;
            child.AddComponent<Animator>().runtimeAnimatorController = b;
            hidden.AddComponent<Animator>().runtimeAnimatorController = hiddenController;
            PolicyChild("Sibling").AddComponent<Animator>().runtimeAnimatorController = hiddenController;
            var source = new AnimatorParameterSource { Source = container, IncludeChildren = false };
            Assert.That(ParameterSourceResolver.Resolve(source, root).Parameters.Select(p => p.Name), Is.EquivalentTo(new[] { "A" }));
            source.IncludeChildren = true;
            Assert.That(ParameterSourceResolver.Resolve(source, root).Parameters.Select(p => p.Name), Is.EquivalentTo(new[] { "A", "B" }));
        }

        [Test]
        public void ReferencedSelectionUsesOverrideClipsAndControllerConditions()
        {
            var original = StaticCurve(ParameterBinding("OriginalOnly"), 1);
            var replacement = StaticCurve(ParameterBinding("Replacement"), 1);
            var controller = Controller(original, Clip());
            foreach (string name in new[] { "OriginalOnly", "Replacement", "Condition", "Unused" })
                controller.AddParameter(name, AnimatorControllerParameterType.Float);
            Edge(controller.layers[0].stateMachine.states[0].state, controller.layers[0].stateMachine.states[1].state,
                "Condition", 0, AnimatorConditionMode.Greater);
            var wrapper = Track(new AnimatorOverrideController(controller));
            wrapper[original] = replacement;
            Assert.That(ParameterSourceResolver.ControllerParameters(wrapper, AnimatorParameterSelectionMode.Referenced),
                Is.EquivalentTo(new[] { "Replacement", "Condition" }));
            Assert.That(ParameterSourceResolver.ControllerParameters(wrapper, AnimatorParameterSelectionMode.Declared),
                Does.Contain("Unused"));
        }

        [Test]
        public void DescriptorSourceIncludesPlayableControllersAndExpressionParameters()
        {
            var policy = Policy();
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            var controller = Controller(Clip()); controller.AddParameter("Playable", AnimatorControllerParameterType.Float);
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = controller } };
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            descriptor.customExpressions = true;
            descriptor.expressionParameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            descriptor.expressionParameters.parameters = new[] { new VRCExpressionParameters.Parameter { name = "Expression" } };
            var result = ParameterSourceResolver.Resolve(new AnimatorParameterSource { Source = root, IncludeChildren = false }, root);
            Assert.That(result.Parameters.Select(p => p.Name), Is.EquivalentTo(new[] { "Playable", "Expression" }));
        }

        [Test]
        public void PreservedInputPreventsIncorrectReachabilityFreezing()
        {
            var fixture = ReachabilityFixture(); Edge(fixture.idle, fixture.face, "Mode");
            var virtualController = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.controller);
            Assert.That(AnimatorReachability.Analyze(root, new[] { virtualController }).Clips(virtualController).Any(c => c.Name == "Expression"), Is.False);
            Assert.That(AnimatorReachability.Analyze(root, new[] { virtualController }, new[] { "Mode" }).Clips(virtualController).Any(c => c.Name == "Expression"), Is.True);
        }

        [Test]
        public void PreservedComputedParameterKeepsItsUpstreamCurvesAcrossControllers()
        {
            var writer = TreeController(("Weight", StaticCurve(ParameterBinding("Input"), 1)));
            writer.AddParameter("Input", AnimatorControllerParameterType.Float);
            var output = Controller(Clip());
            output.AddParameter("Input", AnimatorControllerParameterType.Float);
            output.AddParameter("Output", AnimatorControllerParameterType.Float);
            Driver(output.layers[0].stateMachine.defaultState, "Output", 0, VRC_AvatarParameterDriver.ChangeType.Copy, "Input");
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controllers = new[] { clone.Clone(writer), clone.Clone(output) };
            var closure = AnimatorParameterDependencies.Collect(root, controllers).Expand(new[] { "Output" });
            Assert.That(closure, Is.SupersetOf(new[] { "Output", "Input", "Weight" }));
            var report = AnimatorOptimizer.Optimize(root, controllers, new() { UnusedParameters = true }, preservedParameters: closure);
            Assert.That(report.ParameterCurves, Is.Zero);
        }

        [Test]
        public void PreservedDriverOutputAlsoPreservesTransitionInputs()
        {
            var fixture = ReachabilityFixture();
            fixture.controller.AddParameter("Output", AnimatorControllerParameterType.Float);
            Edge(fixture.idle, fixture.face, "Mode");
            Driver(fixture.face, "Output", 1);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.controller);
            var closure = AnimatorParameterDependencies.Collect(root, new[] { controller }).Expand(new[] { "Output" });
            Assert.That(closure, Does.Contain("Mode"));
        }

        [Test]
        public void PolicyCapturesRemappedNamesAndSourceExclusions()
        {
            var policy = Policy();
            var owner = PolicyChild("Remapped").AddComponent<ParameterPolicySourceFixture>();
            owner.From = "Local"; owner.To = "Global";
            owner.Controller = Controller(Clip());
            ((AnimatorController)owner.Controller).AddParameter("Local", AnimatorControllerParameterType.Float);
            ((AnimatorController)owner.Controller).AddParameter("Excluded", AnimatorControllerParameterType.Float);
            policy.PreserveParameters.Sources.Add(new AnimatorParameterSource { Source = owner.gameObject, ExcludedNames = new() { "Excluded" } });
            var context = new BuildContext(root, null);
            ParameterPolicyPass.Capture(context);
            var captured = context.GetState<ParameterPolicyState>().Preserved;
            Assert.That(captured.Select(p => p.FinalName), Is.EquivalentTo(new[] { "Global" }));
            Assert.That(captured.Single().OriginalName, Is.EqualTo("Local"));
        }

        [Test]
        public void UnverifiedAssetMappingPreventsParameterDependentOptimization()
        {
            var policy = Policy();
            var source = Controller(StaticCurve(ParameterBinding("Output"), 1));
            source.AddParameter("Output", AnimatorControllerParameterType.Float);
            policy.PreserveParameters.Sources.Add(new AnimatorParameterSource { Source = source });
            var context = new BuildContext(root, null);
            ParameterPolicyPass.Capture(context);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            Assert.That(ParameterPolicyPass.Resolve(context, new[] { controller }).Uncertain, Is.True);
        }

        [Test]
        public void RemovedUnusedDeclarationDoesNotMakePolicyUncertain()
        {
            var policy = Policy();
            var owner = PolicyChild("Source").AddComponent<Animator>();
            var source = Controller(Clip()); source.AddParameter("Unused", AnimatorControllerParameterType.Float);
            owner.runtimeAnimatorController = source;
            policy.PreserveParameters.Sources.Add(new AnimatorParameterSource { Source = owner });
            var context = new BuildContext(root, null);
            ParameterPolicyPass.Capture(context);
            var empty = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip()));
            Assert.That(ParameterPolicyPass.Resolve(context, new[] { empty }).Uncertain, Is.False);
        }

        [Test]
        public void ControllerAssetSourceFollowsNdmfCloneProvenance()
        {
            var source = Controller(Clip()); source.AddParameter("Input", AnimatorControllerParameterType.Float);
            var replacement = Track(UnityEngine.Object.Instantiate(source));
            var owner = PolicyChild("Controller clone").AddComponent<Animator>();
            owner.runtimeAnimatorController = replacement;
            using (new ObjectRegistryScope(new ObjectRegistry(root.transform)))
            {
                ObjectRegistry.RegisterReplacedObject(source, replacement);
                var result = ParameterSourceResolver.Resolve(new AnimatorParameterSource { Source = source }, root);
                Assert.That(result.Parameters.Single().Owner, Is.EqualTo(owner));
                Assert.That(result.Diagnostics, Is.Empty);
            }
        }

        [Test]
        public void SourceRemovedByPlatformFilterIsNotAnUnresolvedPreservationRequest()
        {
            var policy = Policy();
            var source = PolicyChild("Filtered source");
            var controller = Controller(StaticCurve(ParameterBinding("Input"), 1));
            controller.AddParameter("Input", AnimatorControllerParameterType.Float);
            source.AddComponent<Animator>().runtimeAnimatorController = controller;
            policy.PreserveParameters.Sources.Add(new AnimatorParameterSource { Source = source });
            UnityEngine.Object.DestroyImmediate(source);
            var context = new BuildContext(root, null);
            ParameterPolicyPass.Capture(context);
            Assert.That(context.GetState<ParameterPolicyState>().Preserved, Is.Empty);
            Assert.That(ParameterPolicyPass.Resolve(context, Array.Empty<VirtualAnimatorController>()).Uncertain, Is.False);
        }

        [Test]
        public void ObjectSourceIncludesGeneratedDeclarationsAndHonorsChildSelection()
        {
            var owner = PolicyChild("Generator").AddComponent<ParameterPolicySourceFixture>();
            owner.Provided = "Generated";
            var child = PolicyChild("Child", owner.transform).AddComponent<ParameterPolicySourceFixture>();
            child.Provided = "ChildInput";
            var hidden = PolicyChild("Hidden", owner.transform).AddComponent<ParameterPolicySourceFixture>();
            hidden.Provided = "HiddenInput"; hidden.gameObject.tag = "EditorOnly";
            var selection = new AnimatorParameterSource { Source = owner.gameObject, IncludeChildren = false };
            Assert.That(ParameterSourceResolver.Resolve(selection, root).Parameters.Select(p => p.Name),
                Is.EquivalentTo(new[] { "Generated" }));
            selection.IncludeChildren = true;
            Assert.That(ParameterSourceResolver.Resolve(selection, root).Parameters.Select(p => p.Name),
                Is.EquivalentTo(new[] { "Generated", "ChildInput" }));
            selection.Source = owner;
            Assert.That(ParameterSourceResolver.Resolve(selection, root).Parameters.Select(p => p.Name),
                Is.EquivalentTo(new[] { "Generated" }));
        }

        [Test]
        public void GeneratedPhysBonePrefixPreservesExpandedRemappedParameters()
        {
            var policy = Policy();
            var owner = PolicyChild("Generator").AddComponent<ParameterPolicySourceFixture>();
            owner.Provided = owner.From = "Local"; owner.To = "Global"; owner.Prefix = true;
            policy.PreserveParameters.Sources.Add(new AnimatorParameterSource { Source = owner.gameObject });
            var context = new BuildContext(root, null);
            ParameterPolicyPass.Capture(context);
            Assert.That(context.GetState<ParameterPolicyState>().Preserved.Select(p => p.FinalName), Is.EquivalentTo(
                new[] { "Global_IsGrabbed", "Global_IsPosed", "Global_Angle", "Global_Stretch", "Global_Squish" }));
        }

        [Test]
        public void SourceWithoutVerifiableDeclarationsReportsConservativeFallback()
        {
            var policy = Policy();
            policy.PreserveParameters.Sources.Add(new AnimatorParameterSource { Source = PolicyChild("Unknown generator") });
            var context = new BuildContext(root, null);
            ParameterPolicyPass.Capture(context);
            Assert.That(context.GetState<ParameterPolicyState>().UnresolvedSource, Is.True);
            Assert.That(context.GetState<ParameterPolicyState>().Diagnostics, Is.Not.Empty);
        }
    }
}
