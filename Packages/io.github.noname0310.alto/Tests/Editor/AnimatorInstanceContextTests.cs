using Noname.AvatarTools.Build;
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private (AnimatorController source, AnimatorState initial, AnimatorState writer, AnimatorState reader)
            InstanceFixture(bool network = false, bool reverse = false)
        {
            Mesh("Visible");
            var parameters = new List<VRCExpressionParameters.Parameter> {
                new() { name = "Input", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = false }
            };
            if (network) parameters.Add(new() { name = "Buffer", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = true });
            FreezeExpressions(parameters.ToArray());
            var source = TreeController(("Output", StaticCurve(Binding("Visible"), 100)));
            source.AddParameter("Input", AnimatorControllerParameterType.Float);
            source.AddParameter("Buffer", AnimatorControllerParameterType.Float);
            source.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
            var machine = source.layers[0].stateMachine;
            var initial = machine.defaultState; initial.name = "Initial";
            var writer = Track(machine.AddState("Writer")); writer.motion = Clip();
            var reader = Track(machine.AddState("Reader")); reader.motion = initial.motion; initial.motion = Clip();
            foreach (var state in machine.states) state.state.writeDefaultValues = false;
            Track(writer.AddStateMachineBehaviour<VRCAvatarParameterDriver>()).parameters = new() { CopyOperation("Buffer", "Input") };
            Track(reader.AddStateMachineBehaviour<VRCAvatarParameterDriver>()).parameters = new() { CopyOperation("Output", "Buffer") };
            Edge(initial, writer, "IsLocal", 0, reverse ? AnimatorConditionMode.IfNot : AnimatorConditionMode.If);
            Edge(initial, reader, "IsLocal", 0, reverse ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot);
            return (source, initial, writer, reader);
        }

        private AnimatorParameterDependencies InstanceGraph(params AnimatorController[] sources)
        {
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            return AnimatorParameterDependencies.Collect(root, sources.Select(source => clone.Clone(source)).ToArray(), forExpressions: true);
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void InstanceDependenciesSeparatePrivateValuesAndBridgeOwnerNetworkWrites(bool network, bool reverse)
        {
            var fixture = InstanceFixture(network, reverse);
            var graph = InstanceGraph(fixture.source);
            bool dependent = network && !reverse;
            Assert.That(graph.Expand(new[] { "Output" }).Contains("Input"), Is.EqualTo(dependent));
            Assert.That(graph.AffectedBy(new[] { "Input" }).Contains("Output"), Is.EqualTo(dependent));
            Assert.That(graph.ExpandObservable().Contains("Input"), Is.EqualTo(dependent), "Observable roots must retain their execution context");
            Assert.That(graph.Expand(new[] { "Buffer" }), Does.Contain("Input"), "Explicit preservation observes both instances");
            Assert.That(fixture.writer.behaviours.OfType<VRC_AvatarParameterDriver>().Single().parameters.Single().source, Is.EqualTo("Input"));
        }

        [TestCase("InitialWrite")] [TestCase("SameInstancePath")] [TestCase("Interrupt")]
        public void InstanceDependenciesKeepPathsThatCanActuallyConnectTheValues(string path)
        {
            var fixture = InstanceFixture();
            if (path == "InitialWrite")
                Track(fixture.initial.AddStateMachineBehaviour<VRCAvatarParameterDriver>()).parameters = new() { CopyOperation("Buffer", "Input") };
            if (path == "SameInstancePath")
            {
                var edge = Track(fixture.writer.AddTransition(fixture.reader)); edge.hasExitTime = true; edge.exitTime = .1f;
            }
            if (path == "Interrupt")
            {
                fixture.source.AddParameter("Interrupt", AnimatorControllerParameterType.Bool);
                var edge = Track(fixture.source.layers[0].stateMachine.AddAnyStateTransition(fixture.reader));
                edge.AddCondition(AnimatorConditionMode.If, 0, "Interrupt");
            }
            var graph = InstanceGraph(fixture.source);
            Assert.That(graph.Expand(new[] { "Output" }), Does.Contain("Input"));
            Assert.That(graph.ExpandObservable(), Does.Contain("Input"));
        }

        [TestCase(false)] [TestCase(true)]
        public void InstanceDependenciesFollowSharedNamesAcrossControllersWithinEachInstance(bool network)
        {
            var fixture = InstanceFixture(network);
            var second = Controller(Clip());
            foreach (var p in fixture.source.parameters) second.AddParameter(p.name, p.type);
            var machine = second.layers[0].stateMachine;
            machine.defaultState.writeDefaultValues = false;
            var reader = Track(machine.AddState("Other controller reader")); reader.motion = fixture.reader.motion; reader.writeDefaultValues = false;
            Track(reader.AddStateMachineBehaviour<VRCAvatarParameterDriver>()).parameters = new() { CopyOperation("Output", "Buffer") };
            Edge(machine.defaultState, reader, "IsLocal", 0, AnimatorConditionMode.IfNot);
            fixture.reader.behaviours = Array.Empty<StateMachineBehaviour>(); fixture.reader.motion = Clip();
            Assert.That(InstanceGraph(fixture.source, second).Expand(new[] { "Output" }).Contains("Input"), Is.EqualTo(network));
        }

        [TestCase("MissingDeclaration")] [TestCase("WrongType")] [TestCase("DriverWrite")]
        [TestCase("CurveWrite")] [TestCase("UnknownBehaviour")] [TestCase("ExternalWrite")] [TestCase("NoDescriptor")] [TestCase("ChildAnimator")]
        [TestCase("UnknownRuntime")]
        public void InstanceDependenciesRetainTheOriginalGraphWhenIdentityCannotBeVerified(string reason)
        {
            var fixture = InstanceFixture();
            if (reason == "MissingDeclaration") fixture.source.parameters = fixture.source.parameters.Where(p => p.name != "IsLocal").ToArray();
            if (reason == "WrongType")
            {
                var parameters = fixture.source.parameters; parameters.Single(p => p.name == "IsLocal").type = AnimatorControllerParameterType.Trigger;
                fixture.source.parameters = parameters;
            }
            if (reason == "DriverWrite")
                fixture.writer.behaviours.OfType<VRC_AvatarParameterDriver>().Single().parameters.Add(SetOperation("IsLocal", 0));
            if (reason == "CurveWrite") fixture.initial.motion = StaticCurve(ParameterBinding("IsLocal"), 0);
            if (reason == "NoDescriptor") UnityEngine.Object.DestroyImmediate(root.GetComponent<VRCAvatarDescriptor>());
            if (reason == "ChildAnimator") PolicyChild("Separate animator").AddComponent<Animator>().runtimeAnimatorController = fixture.source;
            if (reason == "UnknownRuntime") root.AddComponent<ParameterPolicySourceFixture>();
            ExternalParameterAccess.Register("Instance context fixture", access => { if (reason == "ExternalWrite") access.Writers.Add(n => n == "IsLocal"); });
            try
            {
                var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
                if (reason == "UnknownBehaviour")
                {
                    // Generic NDMF cloning drops unsupported source behaviours. Inject into
                    // the final virtual graph, matching a later builder's unknown effect.
                    var state = controller.Layers.First().StateMachine.DefaultState;
                    state.Behaviours = state.Behaviours.Add(Track(ScriptableObject.CreateInstance<UnsupportedReachabilityBehaviour>()));
                }
                Assert.That(AnimatorParameterDependencies.Collect(root, new[] { controller }, true).Expand(new[] { "Output" }), Does.Contain("Input"));
            }
            finally { ExternalParameterAccess.Register("Instance context fixture", _ => { }); }
        }

        [TestCase(false)] [TestCase(true)]
        public void InstanceDependenciesUseRuntimeIdentityInsteadOfTheControllerDefault(bool initial)
        {
            var fixture = InstanceFixture();
            var parameters = fixture.source.parameters; parameters.Single(p => p.name == "IsLocal").defaultBool = initial;
            fixture.source.parameters = parameters;
            Assert.That(InstanceGraph(fixture.source).Expand(new[] { "Output" }), Does.Not.Contain("Input"));
            Assert.That(fixture.source.parameters.Single(p => p.name == "IsLocal").defaultBool, Is.EqualTo(initial));
        }

        [Test]
        public void InstanceDependenciesRespectLocalOnlyDriversWithoutFreezingOtherInputs()
        {
            var fixture = InstanceFixture();
            fixture.reader.behaviours.OfType<VRC_AvatarParameterDriver>().Single().localOnly = true;
            var graph = InstanceGraph(fixture.source);
            Assert.That(graph.Expand(new[] { "Output" }), Does.Not.Contain("Buffer"));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var contexts = AnimatorInstanceContexts.Analyze(root, new[] { controller });
            Assert.That(contexts, Is.Not.Null);
            Assert.That(contexts.All(c => !c.Avatar.Values("Input").TryGetConstant(out _)), Is.True, "Private defaults do not become invariant assumptions");
        }

        [TestCase(false)] [TestCase(true)]
        public void InstanceOwnershipSeparatesTheAvatarFromAnIndependentChild(bool childController)
        {
            var fixture = InstanceFixture();
            var child = PolicyChild("Independent Animator").AddComponent<Animator>();
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controller = clone.Clone(fixture.source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            if (childController)
            {
                // This IsLocal is an ordinary float in an independent controller.
                var source = TreeController(("IsLocal", StaticCurve(Binding("Visible"), 50)));
                child.runtimeAnimatorController = source; entries[child] = clone.Clone(source);
            }
            var controllers = entries.Values.Distinct().ToArray();
            var contexts = AnimatorInstanceContexts.Analyze(root, controllers, entries);
            Assert.That(contexts, Is.Not.Null);
            Assert.That(AnimatorParameterDependencies.Collect(root, controllers, true, controllerEntries: entries)
                .ExpandObservable(), Does.Not.Contain("Input"));
            if (childController)
            {
                var state = entries[child].Layers.First().StateMachine.DefaultState;
                Assert.That(contexts.All(c => c.IsReachable(state) && !c.Other.Values("IsLocal").TryGetConstant(out _)), Is.True);
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void InstanceOwnershipRetainsSharedControllersAndSameNamedChildConsumers(bool shared)
        {
            var fixture = InstanceFixture();
            var child = PolicyChild("Independent Animator").AddComponent<Animator>();
            var source = shared ? fixture.source : TreeController(("Buffer", StaticCurve(Binding("Visible"), 50)));
            child.runtimeAnimatorController = source;
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var entries = new Dictionary<object, VirtualAnimatorController> {
                [VRCAvatarDescriptor.AnimLayerType.FX] = clone.Clone(fixture.source), [child] = clone.Clone(source)
            };
            var controllers = entries.Values.Distinct().ToArray();
            var graph = AnimatorParameterDependencies.Collect(root, controllers, true, controllerEntries: entries);
            // Names across owners still conservatively alias. A shared controller cannot
            // inherit the SDK identity when it also runs on an independent Animator.
            Assert.That(graph.ExpandObservable(), Does.Contain("Input"));
        }

        [TestCase("MissingChild")] [TestCase("MissingController")] [TestCase("UnknownOwner")]
        [TestCase("ControllerCurve")] [TestCase("UnknownChildBehaviour")]
        public void InstanceOwnershipFallsBackWhenControllerOwnershipIsUnverified(string reason)
        {
            var fixture = InstanceFixture();
            var child = PolicyChild("Independent Animator").AddComponent<Animator>();
            var childSource = Controller(Clip()); child.runtimeAnimatorController = childSource;
            if (reason == "ControllerCurve")
            {
                var clip = Clip(); fixture.initial.motion = clip;
                UnityEditor.AnimationUtility.SetObjectReferenceCurve(clip,
                    UnityEditor.EditorCurveBinding.PPtrCurve("Independent Animator", typeof(Animator), "m_Controller"),
                    new[] { new UnityEditor.ObjectReferenceKeyframe { time = 0, value = childSource } });
            }
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controller = clone.Clone(fixture.source); var other = clone.Clone(childSource);
            var entries = new Dictionary<object, VirtualAnimatorController> {
                [VRCAvatarDescriptor.AnimLayerType.FX] = controller, [child] = other
            };
            if (reason == "MissingChild") entries.Remove(child);
            if (reason == "MissingController") entries.Remove(VRCAvatarDescriptor.AnimLayerType.FX);
            if (reason == "UnknownOwner") { entries.Remove(VRCAvatarDescriptor.AnimLayerType.FX); entries["Unverified owner"] = controller; }
            if (reason == "UnknownChildBehaviour") other.Layers.First().StateMachine.DefaultState.Behaviours =
                other.Layers.First().StateMachine.DefaultState.Behaviours.Add(Track(ScriptableObject.CreateInstance<UnsupportedReachabilityBehaviour>()));
            var controllers = new[] { controller, other };
            var diagnostics = new List<string>();
            Assert.That(AnimatorInstanceContexts.AnalyzeWithDiagnostics(root, controllers, entries, diagnostics.Add), Is.Null);
            Assert.That(diagnostics.Single(), Is.Not.Empty);
            Assert.That(AnimatorParameterDependencies.Collect(root, controllers, true, controllerEntries: entries)
                .Expand(new[] { "Output" }), Does.Contain("Input"));
        }

        [TestCase(false)] [TestCase(true)]
        public void InstanceOwnershipUsesEachWritersExecutionContextDuringPruning(bool network)
        {
            var fixture = InstanceFixture(network);
            var child = PolicyChild("Independent Animator").AddComponent<Animator>();
            var childSource = TreeController(("ChildOutput", StaticCurve(Binding("Visible"), 50)));
            childSource.AddParameter("ChildInput", AnimatorControllerParameterType.Float);
            var sourceDriver = Track(childSource.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
            sourceDriver.localOnly = true; sourceDriver.parameters = new() { CopyOperation("ChildOutput", "ChildInput") };
            child.runtimeAnimatorController = childSource;
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controller = clone.Clone(fixture.source); var other = clone.Clone(childSource);
            var entries = new Dictionary<object, VirtualAnimatorController> {
                [VRCAvatarDescriptor.AnimLayerType.FX] = controller, [child] = other
            };
            var controllers = entries.Values.Distinct().ToArray();
            var childDriver = other.AllReachableNodes().OfType<VirtualState>().SelectMany(s => s.Behaviours).OfType<VRC_AvatarParameterDriver>().Single();
            var contexts = AnimatorInstanceContexts.Analyze(root, controllers, entries);
            Assert.That(contexts.All(c => c.Executes(childDriver)), Is.True, "Unverified child execution remains conservative");
            ParameterLiveness.RemoveUnusedCurves(root, controllers, new AnimatorOptimizationReport(), removeDeadDriverOperations: true,
                followStateOutputs: true, controllerEntries: entries);
            Assert.That(controller.AllReachableNodes().OfType<VirtualState>().SelectMany(s => s.Behaviours)
                .OfType<VRC_AvatarParameterDriver>().SelectMany(d => d.parameters).Any(p => p.source == "Input"), Is.EqualTo(network));
            Assert.That(childDriver.parameters.Single().source, Is.EqualTo("ChildInput"));
            Assert.That(sourceDriver.parameters.Single().source, Is.EqualTo("ChildInput"));
        }

        [TestCase(AnimatorControllerParameterType.Bool, false)] [TestCase(AnimatorControllerParameterType.Bool, true)]
        [TestCase(AnimatorControllerParameterType.Int, false)] [TestCase(AnimatorControllerParameterType.Int, true)]
        [TestCase(AnimatorControllerParameterType.Float, false)] [TestCase(AnimatorControllerParameterType.Float, true)]
        public void InstanceOwnershipSupportsTheSdkIdentityTypeConversion(AnimatorControllerParameterType type, bool network)
        {
            var fixture = InstanceFixture(network);
            var parameters = fixture.source.parameters; var identity = parameters.Single(p => p.name == "IsLocal");
            identity.type = type; identity.defaultBool = true; identity.defaultInt = 7; identity.defaultFloat = .25f;
            fixture.source.parameters = parameters;
            if (type != AnimatorControllerParameterType.Bool)
                foreach (var transition in fixture.initial.transitions)
                {
                    bool writer = transition.destinationState == fixture.writer;
                    transition.conditions = new[] { new AnimatorCondition { parameter = "IsLocal",
                        mode = type == AnimatorControllerParameterType.Int ? AnimatorConditionMode.Equals :
                            writer ? AnimatorConditionMode.Greater : AnimatorConditionMode.Less,
                        threshold = type == AnimatorControllerParameterType.Int ? (writer ? 1 : 0) : .5f } };
                }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.source);
            var contexts = AnimatorInstanceContexts.Analyze(root, new[] { controller });
            Assert.That(contexts, Is.Not.Null);
            foreach (var context in contexts)
            {
                Assert.That(context.Avatar.Values("IsLocal").TryGetConstant(out var value), Is.True);
                Assert.That(value, Is.EqualTo(context.LocalInstance ? 1 : 0));
            }
            ParameterLiveness.RemoveUnusedCurves(root, new[] { controller }, new AnimatorOptimizationReport(),
                followStateOutputs: true, removeDeadDriverOperations: true);
            var built = CommitAnimator(controller, root);
            Assert.That(controller.AllReachableNodes().OfType<VirtualState>().SelectMany(s => s.Behaviours)
                .OfType<VRC_AvatarParameterDriver>().SelectMany(d => d.parameters).Any(p => p.source == "Input"), Is.EqualTo(network));
            foreach (bool local in new[] { false, true })
                foreach (float input in new[] { .2f, .8f })
                {
                    float received = network ? input : .35f;
                    Assert.That(SampleInstanceController(built, local, input, received),
                        Is.EqualTo(SampleInstanceController(fixture.source, local, input, received)).Within(.0001f));
                }
        }

        [TestCase(false, false, false)] [TestCase(false, true, false)] [TestCase(true, false, false)] [TestCase(true, true, false)]
        [TestCase(false, false, true)] [TestCase(false, true, true)] [TestCase(true, false, true)] [TestCase(true, true, true)]
        public void InstanceDependenciesReachMenuCleanupWithoutRemovingNetworkProducers(bool sdk, bool network, bool preserveOutput)
        {
            var fixture = InstanceFixture(network);
            var originalParameters = root.GetComponent<VRCAvatarDescriptor>().expressionParameters;
            var menu = ExpressionMenu(ExpressionControl("Feature", "Input"));
            root.AddComponent<ALTOTraceAndOptimizeExpressions>(); ObjectSettings(root);
            var context = ExpressionBuild(menu, fixture.source); ObjectOptimizationPass.Capture(context);
            context.GetState<AnimatorOptimizationState>().Settings = new() { StateLogic = true };
            if (preserveOutput) { var policy = context.GetState<ParameterPolicyState>(); policy.Enabled = true; policy.Names.Add("Output"); }
            if (sdk) AvatarOutputSdkFinalizer.Begin(root);
            ExpressionOptimizationPass.Apply(context);
            if (sdk)
            {
                TrackControllerAssets((AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController);
                AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            }
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            var built = (AnimatorController)descriptor.baseAnimationLayers.Single().animatorController;
            TrackControllerAssets(built); Track(descriptor.expressionParameters);
            if (descriptor.expressionsMenu != null) Track(descriptor.expressionsMenu);
            Assert.That(descriptor.expressionsMenu != null && descriptor.expressionsMenu.controls.Any(c => c.parameter.name == "Input"), Is.EqualTo(network));
            Assert.That(descriptor.expressionParameters.parameters.Any(p => p.name == "Input"), Is.EqualTo(network));
            Assert.That(originalParameters.parameters.Any(p => p.name == "Input"), Is.True);
            Assert.That(menu.controls.Count, Is.EqualTo(1));
            Assert.That(built.layers[0].stateMachine.states.SelectMany(s => s.state.behaviours).OfType<VRC_AvatarParameterDriver>()
                .SelectMany(d => d.parameters).Any(p => p.source == "Input"), Is.EqualTo(network), "Writer removal must use the same instance context as liveness");
            foreach (bool local in new[] { false, true })
                foreach (float input in new[] { .2f, .8f })
                {
                    float received = network ? input : .35f;
                    float expected = SampleInstanceController(fixture.source, local, input, received);
                    Assert.That(SampleInstanceController(built, local, input, received), Is.EqualTo(expected).Within(.0001f));
                    if (!local) Assert.That(expected, Is.EqualTo(received * 100).Within(.001f));
                }
        }

        // Native transitions and blend output; Copy/Set entry effects are modeled because
        // SDK behaviours are executed by the client. A supplied Buffer value represents
        // one delivered snapshot, not a network timing or client interoperability test.
        private float SampleInstanceController(AnimatorController controller, bool local, float input, float buffer)
        {
            var owner = Track(UnityEngine.Object.Instantiate(root));
            var animator = owner.GetComponent<Animator>();
            // Unity can return a missing-component sentinel that is not CLR null.
            if (animator == null) animator = owner.AddComponent<Animator>();
            var states = controller.layers[0].stateMachine.states.Select(s => s.state).ToDictionary(s => Animator.StringToHash(s.name));
            var graph = PlayableGraph.Create("Instance dependency regression");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Output", animator).SetSourcePlayable(playable);
                graph.Play();
                var identityType = controller.parameters.Single(p => p.name == "IsLocal").type;
                if (identityType == AnimatorControllerParameterType.Float) playable.SetFloat("IsLocal", local ? 1 : 0);
                else if (identityType == AnimatorControllerParameterType.Int) playable.SetInteger("IsLocal", local ? 1 : 0);
                else playable.SetBool("IsLocal", local);
                playable.SetFloat("Input", input); playable.SetFloat("Buffer", buffer);
                int previous = 0;
                for (int frame = 0; frame < 30; frame++)
                {
                    graph.Evaluate(1f / 60);
                    int hash = playable.GetCurrentAnimatorStateInfo(0).shortNameHash;
                    if (hash == previous) continue; previous = hash;
                    foreach (var driver in states[hash].behaviours.OfType<VRC_AvatarParameterDriver>())
                    {
                        if (driver.localOnly && !local) continue;
                        foreach (var operation in driver.parameters)
                            playable.SetFloat(operation.name, operation.type == VRC_AvatarParameterDriver.ChangeType.Copy ?
                                playable.GetFloat(operation.source) : operation.value);
                    }
                }
                return owner.GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0);
            }
            finally { graph.Destroy(); }
        }
    }
}
