using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ParameterLayout;
using Noname.AvatarTools.ParameterPolicy;
using Noname.AvatarTools.Build;
using nadena.dev.ndmf;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDKBase;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(48, 0)] [TestCase(40, 3)] [TestCase(32, 4)]
        public void MultiplexPriorityPreservesLaterTiersAndTrimsOnlyTheBoundary(int budget, int externalCount)
        {
            var input = MultiplexInputs(10);
            var ranking = new MultiplexPriorities();
            for (int i = 0; i < input.Length; i++) ranking.Candidates[input[i].name] = i < 2 ? MultiplexPriority.PersistentMenu :
                i < 4 ? MultiplexPriority.InternalComputed : i < 6 ? MultiplexPriority.RuntimeDriven : MultiplexPriority.ExternalInput;
            var messages = new List<string>(); var warnings = new List<string>();
            var plan = ranking.Plan(input, budget, .1f, new HashSet<string>(), messages, warnings);
            var selected = plan.Inputs.Select(p => p.Name).ToHashSet();
            Assert.That(plan.Inputs.Count(p => ranking.Candidates[p.Name] == MultiplexPriority.ExternalInput), Is.EqualTo(externalCount));
            Assert.That(selected, Is.SupersetOf(input.Take(6).Select(p => p.name)));
            Assert.That(plan.WireCost + input.Where(p => !selected.Contains(p.name)).Sum(p => MultiplexEncoding.Cost(p.valueType)), Is.LessThanOrEqualTo(budget));
            foreach (var name in selected.Where(n => ranking.Candidates[n] == (externalCount == 0 ? MultiplexPriority.RuntimeDriven : MultiplexPriority.ExternalInput)))
                Assert.That(MultiplexEncoding.CanFit(input, selected.Where(n => n != name).ToHashSet(), budget, plan.Batches), Is.False);
            Assert.That(warnings.Count, Is.EqualTo(externalCount > 0 ? 1 : 0));
            Assert.That(messages.Count(m => m.StartsWith("Priority multiplexing kept native:") || m.StartsWith("Priority multiplexing selected:")), Is.EqualTo(10));
            var reversed = ranking.Plan(input.Reverse().ToArray(), budget, .1f, new HashSet<string>(), new List<string>(), new List<string>());
            Assert.That(JsonUtility.ToJson(reversed), Is.EqualTo(JsonUtility.ToJson(plan)));
        }

        [Test]
        public void MultiplexPriorityPrefersLongerMenuScheduleOverCompressingExternalInputs()
        {
            var input = MultiplexInputs(8);
            var ranking = new MultiplexPriorities();
            for (int i = 0; i < input.Length; i++) ranking.Candidates[input[i].name] = i < 6 ? MultiplexPriority.PersistentMenu : MultiplexPriority.ExternalInput;
            var plan = ranking.Plan(input, 32, .15f, new HashSet<string>(), new List<string>(), new List<string>());
            Assert.That(plan.Inputs.Select(p => p.Name), Is.EquivalentTo(input.Take(6).Select(p => p.name)));
            Assert.That(plan.Batches, Is.EqualTo(6));
            Assert.That(MultiplexPlan(input, 32).Batches, Is.LessThan(plan.Batches), "Priority precedes shortest-cycle optimization.");
        }

        [TestCase(4, 0, 0, 16)] [TestCase(15, 5, 33, 64)] [TestCase(0, 0, 60, 16)]
        [TestCase(128, 0, 0, 16)] [TestCase(127, 0, 0, 16)] [TestCase(0, 129, 15, 32)]
        public void MultiplexPriorityFeasibilityMatchesActualTypedProtocol(int floats, int ints, int bools, int budget)
        {
            var input = MultiplexInputs(floats, ints, bools);
            foreach (int skip in new[] { 0, 1, 3, input.Length })
            {
                var candidates = input.Skip(skip).Select(p => p.name).ToHashSet();
                bool fits = MultiplexEncoding.CanFit(input, candidates, budget);
                var occupied = input.Select(p => p.name).ToHashSet();
                if (fits) Assert.DoesNotThrow(() => MultiplexEncoding.Plan(input, candidates, budget, .1f, occupied));
                else
                {
                    Assert.Throws<InvalidOperationException>(() => MultiplexEncoding.Plan(input, candidates, budget, .1f, occupied));
                    Assert.That(occupied, Is.EquivalentTo(input.Select(p => p.name)), "Failed feasibility must not reserve wire names.");
                }
            }
        }

        [Test]
        public void MultiplexPriorityNeverOverridesHardProtectionOrChangesAnAvatarAlreadyWithinBudget()
        {
            var input = MultiplexInputs(4);
            var ranking = new MultiplexPriorities();
            foreach (var p in input) ranking.Protected[p.name] = "MomentaryDependency";
            var occupied = new HashSet<string> { "Existing" };
            Assert.That(ranking.Plan(input, 32, .1f, occupied, new List<string>(), new List<string>()), Is.Null);
            var error = Assert.Throws<InvalidOperationException>(() => ranking.Plan(input, 16, .1f, occupied, new List<string>(), new List<string>()));
            Assert.That(error.Message, Does.Contain("MomentaryDependency=32 bits"));
            Assert.That(occupied, Is.EquivalentTo(new[] { "Existing" }));
            Assert.That(input.All(p => p.networkSynced && p.saved), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void MultiplexPriorityInfersRenamedExternalInputsAndFollowsTheirComputations(bool syncedInput)
        {
            var descriptor = LayoutDescriptor();
            var names = new[] { "UserToggle", "Computed", "RawInput", "DerivedInput", "Sensor", "SensorOutput", "Button", "ButtonOutput", "InitializeOnly", "GestureLeft", "Protected" };
            descriptor.expressionParameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            descriptor.expressionParameters.parameters = names.Select(n => LayoutParameter(n, ValueType.Float)).ToArray();
            descriptor.expressionParameters.parameters.Single(p => p.name == "RawInput").networkSynced = syncedInput;
            var button = ExpressionControl("Pulse", "Button"); button.type = VRCExpressionsMenu.Control.ControlType.Button;
            descriptor.expressionsMenu = ExpressionMenu(ExpressionControl("Toggle", "UserToggle"), ExpressionControl("External alias", "DerivedInput"), button);
            PolicyChild("Receiver").AddComponent<VRCContactReceiver>().parameter = "Sensor";
            var source = Controller(Clip());
            foreach (var name in names) source.AddParameter(name, name == "GestureLeft" ? AnimatorControllerParameterType.Int : AnimatorControllerParameterType.Float);
            Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>()).parameters = new()
            {
                CopyOperation("Computed", "UserToggle"), CopyOperation("DerivedInput", "RawInput"),
                CopyOperation("SensorOutput", "Sensor"), CopyOperation("ButtonOutput", "Button"),
                new VRC_AvatarParameterDriver.Parameter { name = "InitializeOnly", type = VRC_AvatarParameterDriver.ChangeType.Set, value = 0 }
            };
            string before = EditorJsonUtility.ToJson(descriptor.expressionParameters);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var result = MultiplexPriorities.Analyze(root, new[] { controller }, new HashSet<string> { "Protected" });
            Assert.That(result.Candidates["UserToggle"], Is.EqualTo(MultiplexPriority.PersistentMenu));
            Assert.That(result.Candidates["Computed"], Is.EqualTo(MultiplexPriority.InternalComputed));
            Assert.That(result.Candidates["RawInput"], Is.EqualTo(MultiplexPriority.ExternalInput));
            Assert.That(result.Candidates["DerivedInput"], Is.EqualTo(MultiplexPriority.ExternalInput));
            Assert.That(result.Candidates["InitializeOnly"], Is.EqualTo(MultiplexPriority.ExternalInput));
            Assert.That(result.Candidates["Sensor"], Is.EqualTo(MultiplexPriority.RuntimeDriven));
            Assert.That(result.Candidates["SensorOutput"], Is.EqualTo(MultiplexPriority.RuntimeDriven));
            Assert.That(result.Protected["Button"], Is.EqualTo("MomentaryDependency"));
            Assert.That(result.Protected["ButtonOutput"], Is.EqualTo("MomentaryDependency"));
            Assert.That(result.Protected["GestureLeft"], Is.EqualTo("BuiltIn"));
            Assert.That(result.Protected["Protected"], Is.EqualTo("Preserved"));
            Assert.That(EditorJsonUtility.ToJson(descriptor.expressionParameters), Is.EqualTo(before));
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void MultiplexPriorityRespectsOwnerToRemoteDependencyDirection(bool network, bool reverse)
        {
            var fixture = InstanceFixture(network, reverse);
            MultiplexContextMenu();
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var result = MultiplexPriorities.Analyze(root, new[] { controller }, new HashSet<string>());
            Assert.That(result.Candidates["Output"], Is.EqualTo(network && !reverse ? MultiplexPriority.ExternalInput : MultiplexPriority.PersistentMenu));
        }

        [TestCase("External")] [TestCase("Event")] [TestCase("Uncertain")]
        public void MultiplexPriorityHonorsExternalProvidersAndUnknownAccess(string kind)
        {
            const string provider = "Priority selection fixture";
            var descriptor = LayoutDescriptor();
            descriptor.expressionParameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            descriptor.expressionParameters.parameters = new[] { LayoutParameter("Toggle") };
            descriptor.expressionsMenu = ExpressionMenu(ExpressionControl("Toggle", "Toggle"));
            var clip = Clip();
            if (kind == "Event") AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { functionName = "UnknownReader" } });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            ExternalParameterAccess.Register(provider, access =>
            {
                if (kind == "Uncertain") throw new InvalidOperationException("Cannot identify external consumers");
                if (kind == "External") access.Readers.Add(n => n == "Toggle");
            });
            try
            {
                var result = MultiplexPriorities.Analyze(root, new[] { controller }, new HashSet<string>());
                if (kind == "External") Assert.That(result.Candidates["Toggle"], Is.EqualTo(MultiplexPriority.ExternalInput));
                else Assert.That(result.Protected["Toggle"], Does.StartWith("Uncertain input access:"));
            }
            finally { ExternalParameterAccess.Register(provider, _ => { }); }
        }

        [TestCase(false)] [TestCase(true)]
        public void MultiplexPriorityPrivateConstantsDoNotPromoteOrdinaryComputations(bool externalRead)
        {
            const string provider = "Priority constant fixture";
            var descriptor = LayoutDescriptor();
            descriptor.expressionParameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            descriptor.expressionParameters.parameters = new[] { LayoutParameter("Output", ValueType.Float) };
            var source = Controller(Clip());
            source.AddParameter("UnitWeight", AnimatorControllerParameterType.Float);
            source.AddParameter("Output", AnimatorControllerParameterType.Float);
            Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>()).parameters = new() { CopyOperation("Output", "UnitWeight") };
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            ExternalParameterAccess.Register(provider, a => { if (externalRead) a.Readers.Add(n => n == "UnitWeight"); });
            try
            {
                var result = MultiplexPriorities.Analyze(root, new[] { controller }, new HashSet<string>());
                Assert.That(result.Candidates["Output"], Is.EqualTo(externalRead ? MultiplexPriority.ExternalInput : MultiplexPriority.InternalComputed));
            }
            finally { ExternalParameterAccess.Register(provider, _ => { }); }
        }

        [Test]
        public void MultiplexPriorityDoesNotTradeCycleLatencyForUnnecessaryNativeInputsWithinATier()
        {
            var input = MultiplexInputs(0, 0, 60);
            var ranking = new MultiplexPriorities();
            foreach (var p in input) ranking.Candidates[p.name] = MultiplexPriority.PersistentMenu;
            var plan = ranking.Plan(input, 32, .15f, new HashSet<string>(), new List<string>(), new List<string>());
            // Minimizing only candidate count leaves 23 native bits and a single
            // payload bit: 37 batches. More same-tier candidates fit in 3 batches.
            Assert.That(plan.Batches, Is.EqualTo(3));
            Assert.That(plan.Inputs.Count, Is.GreaterThan(37));
            Assert.That(plan.WireCost + input.Length - plan.Inputs.Count, Is.LessThanOrEqualTo(32));
        }

        [TestCase("Active", true)] [TestCase("Disabled", false)] [TestCase("NoPlatforms", false)]
        [TestCase("EditorOnly", false)] [TestCase("Filtered", false)] [TestCase("StrictMenu", false)]
        public void MultiplexPrioritySharesVisiblePlatformPreservationWithoutApplyingItsFreezePolicy(string setting, bool preserved)
        {
            LayoutDescriptor();
            var compression = root.AddComponent<ALTOParameterCompression>();
            if (setting == "StrictMenu") compression.TimeMultiplexing = ParameterMultiplexingMode.AutomaticMenuInputs;
            var policy = PolicyChild("Policy").AddComponent<ALTOAnimatorParameterPolicy>();
            var context = new BuildContext(root, null);
            var current = OptimizationBuildScope.Platform(context.GetState<OptimizationBuildEnvironment>().Target);
            policy.ApplyOnPlatforms = current == OptimizationPlatforms.Android ? OptimizationPlatforms.PC : OptimizationPlatforms.Android;
            policy.PreserveParameters.Names.Add("ExternalAlias");
            policy.PreserveParameters.Prefixes.Add("Sensor/");
            var controller = Controller(Clip()); controller.AddParameter("SourceInput", AnimatorControllerParameterType.Float);
            PolicyChild("Source").AddComponent<Animator>().runtimeAnimatorController = controller;
            policy.PreserveParameters.Sources.Add(new AnimatorParameterSource { Source = controller });
            policy.FreezeParameters.Add(new AnimatorParameterConstant { Name = "FrozenOnOtherPlatform", Value = 0 });
            if (setting == "Disabled") policy.enabled = false;
            if (setting == "NoPlatforms") policy.ApplyOnPlatforms = OptimizationPlatforms.None;
            if (setting == "EditorOnly") policy.gameObject.tag = "EditorOnly";
            const string filter = "Priority policy fixture";
            var policyTransform = policy.transform;
            BuildPlatformFilters.Register(filter, (t, _) => setting != "Filtered" || t != policyTransform);
            try
            {
                ParameterCompressionPass.CapturePolicyPreservation(context);
                ParameterPolicyPass.Capture(context);
                ParameterPolicyPass.RemoveMarkers(context);
                var settings = ParameterCompressionPass.Capture(context, true);
                Assert.That(settings.KeepNative.Contains("ExternalAlias"), Is.EqualTo(preserved));
                Assert.That(settings.KeepNative.Contains("SourceInput"), Is.EqualTo(preserved));
                Assert.That(settings.KeepPrefixes.Contains("Sensor/"), Is.EqualTo(preserved));
                Assert.That(context.GetState<ParameterPolicyState>().Enabled, Is.False);
                Assert.That(context.GetState<ParameterPolicyState>().Frozen, Is.Empty);
            }
            finally { BuildPlatformFilters.Register(filter, (_, _) => true); }
        }
    }
}
