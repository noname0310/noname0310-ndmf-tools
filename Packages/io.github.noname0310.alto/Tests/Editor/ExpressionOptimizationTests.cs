using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using Menu = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu;
using Control = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private Menu ExpressionMenu(params Control[] controls)
        {
            var menu = Track(ScriptableObject.CreateInstance<Menu>()); menu.controls = controls.ToList(); return menu;
        }
        private static Control ExpressionControl(string name, string parameter = null, Menu child = null) => new()
        {
            name = name, type = child == null ? Control.ControlType.Toggle : Control.ControlType.SubMenu,
            parameter = new Control.Parameter { name = parameter }, subParameters = Array.Empty<Control.Parameter>(), subMenu = child
        };
        private static ExpressionSettings AllExpressionOptions() => new() { Frozen = true, Dead = true, Empty = true, LocalParameters = true };
        private Menu PruneMenu(Menu source, ExpressionSettings settings, ExpressionOptimizationReport report,
            IEnumerable<string> live = null, IEnumerable<string> frozen = null) => ExpressionMenuOptimizer.Optimize(source, settings,
            new HashSet<string>(live ?? Array.Empty<string>()), new HashSet<string>(frozen ?? Array.Empty<string>()), report, o => Track(o));

        private HashSet<string> ExpressionLive(params AnimatorController[] sources)
        {
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controllers = sources.Select(source => clone.Clone(source)).ToArray();
            var entries = controllers.Select((c, i) => (c, key: (object)i)).ToDictionary(v => v.key, v => v.c);
            var outputs = new ExpressionOutputUsage(root, entries, null);
            var graph = AnimatorParameterDependencies.Collect(root, controllers, true, outputs.IsObservable);
            return graph.ExpandObservable();
        }

        [Test]
        public void ExpressionsIgnoreDeclarationsAndDeadFeedbackButFollowCrossControllerConsumers()
        {
            Mesh("Visible");
            var dead = TreeController(("B", StaticCurve(ParameterBinding("A"), 1)), ("A", StaticCurve(ParameterBinding("B"), 1)));
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.expressionParameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            descriptor.expressionParameters.parameters = new[] { new VRCExpressionParameters.Parameter { name = "A", networkSynced = true } };
            Assert.That(ExpressionLive(dead).Contains("A"), Is.False);
            Assert.That(ExpressionLive(dead).Contains("B"), Is.False);
            var visible = TreeController(("A", StaticCurve(Binding("Visible"), 100)));
            var live = ExpressionLive(dead, visible);
            Assert.That(live, Does.Contain("A")); Assert.That(live, Does.Contain("B"));
        }

        [Test]
        public void ExpressionsKeepNormalizedSiblingInputsAndProvenShaderOutputs()
        {
            Mesh();
            var visible = StaticCurve(EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "material._CustomUniform"), 1);
            var source = TreeController(("Visible", visible), ("Denominator", StaticCurve(ParameterBinding("Unused"), 1)));
            source.AddParameter("Unused", AnimatorControllerParameterType.Float);
            var tree = (BlendTree)source.layers[0].stateMachine.defaultState.motion;
            using (var serialized = new SerializedObject(tree))
            { serialized.FindProperty("m_NormalizedBlendValues").boolValue = true; serialized.ApplyModifiedPropertiesWithoutUndo(); }
            var live = ExpressionLive(source);
            Assert.That(live, Does.Contain("Visible")); Assert.That(live, Does.Contain("Denominator"));
        }

        [Test]
        public void ExpressionsDoNotRootTransitionOnlyLayersButKeepSdkSideEffects()
        {
            var first = Clip(); first.name = "Idle"; var second = Clip(); second.name = "Changed";
            var controller = Controller(first, second); controller.AddParameter("Toggle", AnimatorControllerParameterType.Bool);
            var states = controller.layers[0].stateMachine.states;
            Edge(states[0].state, states[1].state, "Toggle", 0, AnimatorConditionMode.If);
            Assert.That(ExpressionLive(controller).Contains("Toggle"), Is.False);
            Track(states[1].state.AddStateMachineBehaviour<VRCAnimatorTrackingControl>());
            Assert.That(ExpressionLive(controller), Does.Contain("Toggle"));
        }

        [Test]
        public void EmptyMixedWriteDefaultsLayerCanResetAnotherLayersPose()
        {
            Mesh("Visible");
            var controller = Controller(StaticCurve(Binding("Visible"), 100));
            var empty = Controller(Clip(), Clip());
            var states = empty.layers[0].stateMachine.states;
            states[0].state.writeDefaultValues = false; states[1].state.writeDefaultValues = true;
            controller.AddParameter("Toggle", AnimatorControllerParameterType.Bool);
            Edge(states[0].state, states[1].state, "Toggle", 0, AnimatorConditionMode.If);
            controller.layers = controller.layers.Concat(empty.layers).ToArray();
            var samples = SampleFrozen(controller, (p, i) => p.SetBool("Toggle", i >= 4), 8);
            Assert.That(samples[0], Is.EqualTo(100).Within(0.001f));
            Assert.That(samples.Last(), Is.LessThan(75));
            Assert.That(ExpressionLive(controller), Does.Contain("Toggle"));
            states[0].state.writeDefaultValues = true;
            samples = SampleFrozen(controller, (p, i) => p.SetBool("Toggle", i >= 4), 8);
            Assert.That(samples, Is.All.EqualTo(samples[0]).Within(0.001f));
            Assert.That(ExpressionLive(controller).Contains("Toggle"), Is.False);
            // Explicit bindings also make an otherwise uniform WD layer observable.
            states[0].state.motion = StaticCurve(Binding("Visible"), 50);
            Assert.That(ExpressionLive(controller), Does.Contain("Toggle"));
        }

        [Test]
        public void ExpressionsTrackParameterDriverDependenciesToObservableOutputs()
        {
            Mesh("Visible");
            var writer = Controller(Clip()); writer.AddParameter("Input", AnimatorControllerParameterType.Float);
            writer.AddParameter("Copied", AnimatorControllerParameterType.Float);
            var driver = Track(writer.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
            driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter { name = "Copied", source = "Input", type = VRC_AvatarParameterDriver.ChangeType.Copy });
            Assert.That(ExpressionLive(writer).Contains("Input"), Is.False);
            var consumer = TreeController(("Copied", StaticCurve(Binding("Visible"), 100)));
            Assert.That(ExpressionLive(writer, consumer), Does.Contain("Input"));
        }

        [Test]
        public void ExpressionsIgnoreMissingDurationTargetsButRetainRealSiblingPaths()
        {
            var missing = StaticCurve(EditorCurveBinding.FloatCurve("Missing", typeof(GameObject), "m_IsActive"), 0);
            var controller = TreeController(("Control", missing));
            Assert.That(ExpressionLive(controller).Contains("Control"), Is.False);
            PolicyChild("Missing"); Assert.That(ExpressionLive(controller), Does.Contain("Control"));
        }

        [Test]
        public void FrozenDeadAndPuppetControlsRespectIndependentOptions()
        {
            var puppet = ExpressionControl("Puppet", "Gate"); puppet.type = Control.ControlType.TwoAxisPuppet;
            puppet.subParameters = new[] { new Control.Parameter { name = "LiveX" }, new Control.Parameter { name = "FrozenY" } };
            var source = ExpressionMenu(ExpressionControl("Frozen", "Frozen"), ExpressionControl("Dead", "Dead"), puppet);
            var settings = AllExpressionOptions(); settings.Frozen = false;
            var result = PruneMenu(source, settings, new(), new[] { "LiveX" }, new[] { "Frozen", "FrozenY" });
            Assert.That(result.controls.Select(c => c.name), Is.EqualTo(new[] { "Frozen", "Puppet" }));
            settings.Frozen = true; settings.Dead = false;
            result = PruneMenu(source, settings, new(), new[] { "LiveX" }, new[] { "Frozen", "FrozenY" });
            Assert.That(result.controls.Select(c => c.name), Is.EqualTo(new[] { "Dead", "Puppet" }));
            Assert.That(source.controls.Count, Is.EqualTo(3));
        }

        [Test]
        public void EmptyMenuCyclesDisappearWhileLiveActivationAndDecorativeControlsRemain()
        {
            var a = ExpressionMenu(); var b = ExpressionMenu();
            a.controls.Add(ExpressionControl("To B", child: b)); b.controls.Add(ExpressionControl("To A", child: a));
            var empty = ExpressionMenu(); var source = ExpressionMenu(ExpressionControl("Cycle", child: a),
                ExpressionControl("Activation", "Action", empty), ExpressionControl("Label"));
            var result = PruneMenu(source, AllExpressionOptions(), new(), new[] { "Action" });
            Assert.That(result.controls.Select(c => c.name), Is.EqualTo(new[] { "Activation", "Label" }));
            Assert.That(a.controls.Single().subMenu, Is.SameAs(b));
            a.controls.Add(ExpressionControl("Useful", "Action"));
            result = PruneMenu(source, AllExpressionOptions(), new(), new[] { "Action" });
            Assert.That(result, Is.SameAs(source));
        }

        [Test]
        public void MenuCopiesPreserveSharedSubmenusCyclesAndSourceAssets()
        {
            var child = ExpressionMenu(ExpressionControl("Live", "Live"), ExpressionControl("Dead", "Dead"));
            var source = ExpressionMenu(ExpressionControl("First", child: child), ExpressionControl("Second", child: child));
            child.controls.Add(ExpressionControl("Back", child: source));
            string original = JsonUtility.ToJson(child);
            var result = PruneMenu(source, AllExpressionOptions(), new(), new[] { "Live" });
            Assert.That(result, Is.Not.SameAs(source));
            Assert.That(result.controls[0].subMenu, Is.SameAs(result.controls[1].subMenu));
            Assert.That(result.controls[0].subMenu.controls.Single(c => c.name == "Back").subMenu, Is.SameAs(result));
            Assert.That(JsonUtility.ToJson(child), Is.EqualTo(original));
            Assert.That(result.controls[0].subMenu.controls.Select(c => c.name), Does.Not.Contain("Dead"));
        }

        [Test]
        public void ProtectedControlsMatchAfterRenameAndConservativelyPreserveDuplicates()
        {
            var menu = ExpressionMenu(ExpressionControl("Keep", "Original"));
            var settingsComponent = root.AddComponent<ALTOTraceAndOptimizeExpressions>();
            settingsComponent.PreserveControls.Add(new ExpressionControlPreservation
                { Menu = menu, Selected = true, ControlName = "Keep", ControlType = Control.ControlType.Toggle });
            var settings = ExpressionSettings.Capture(settingsComponent);
            var generated = ExpressionMenu(ExpressionControl("Keep", "Renamed A"), ExpressionControl("Keep", "Renamed B"), ExpressionControl("Remove", "Dead"));
            var result = PruneMenu(generated, settings, new());
            Assert.That(result.controls.Select(c => c.parameter.name), Is.EqualTo(new[] { "Renamed A", "Renamed B" }));
            menu.controls[0].name = "Changed";
            Assert.That(ExpressionSettings.Capture(settingsComponent).InvalidProtection, Is.Not.Null);
        }

        [Test]
        public void LocalParameterPruningKeepsSynchronizedSequenceSettingsAndSource()
        {
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            var source = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>()); descriptor.expressionParameters = source;
            source.parameters = new[] {
                new VRCExpressionParameters.Parameter { name = "SyncA", networkSynced = true, valueType = VRCExpressionParameters.ValueType.Int, defaultValue = 3, saved = true },
                new VRCExpressionParameters.Parameter { name = "Dead", networkSynced = false },
                new VRCExpressionParameters.Parameter { name = "Live", networkSynced = false },
                new VRCExpressionParameters.Parameter { name = "SyncB", networkSynced = true, valueType = VRCExpressionParameters.ValueType.Bool }
            };
            string original = JsonUtility.ToJson(source);
            var report = new ExpressionOptimizationReport();
            ExpressionOptimizationPass.PruneLocalParameters(descriptor, new HashSet<string> { "Live" }, report, o => Track(o));
            Assert.That(descriptor.expressionParameters.parameters.Select(p => p.name), Is.EqualTo(new[] { "SyncA", "Live", "SyncB" }));
            Assert.That(descriptor.expressionParameters.parameters.Where(p => p.networkSynced).Select(JsonUtility.ToJson),
                Is.EqualTo(source.parameters.Where(p => p.networkSynced).Select(JsonUtility.ToJson)));
            Assert.That(JsonUtility.ToJson(source), Is.EqualTo(original));
            Assert.That(report.RemovedLocalParameters, Is.EqualTo(new[] { "Dead" }));
        }

        private BuildContext ExpressionBuild(Menu menu, AnimatorController controller = null)
        {
            var descriptor = root.GetComponent<VRCAvatarDescriptor>() ?? root.AddComponent<VRCAvatarDescriptor>();
            root.AddComponent<Animator>(); descriptor.expressionsMenu = menu; descriptor.customExpressions = true;
            descriptor.customizeAnimationLayers = true;
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            descriptor.baseAnimationLayers = controller == null ? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>() : new[] {
                new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = controller, isDefault = false }
            };
            var context = new BuildContext(root, null); ExpressionOptimizationPass.Capture(context); return context;
        }

        [Test]
        public void ExpressionPassUsesPolicyPreservationAndReportsUnmatchedProtection()
        {
            var controller = Controller(Clip()); controller.AddParameter("FT", AnimatorControllerParameterType.Float);
            var settings = PolicyChild("Settings").AddComponent<ALTOTraceAndOptimizeExpressions>();
            var menu = ExpressionMenu(ExpressionControl("Face", "FT"), ExpressionControl("Dead", "Dead"));
            var context = ExpressionBuild(menu, controller);
            var policy = context.GetState<ParameterPolicyState>(); policy.Enabled = true; policy.Names.Add("FT");
            ExpressionOptimizationPass.Apply(context);
            Assert.That(root.GetComponent<VRCAvatarDescriptor>().expressionsMenu.controls.Select(c => c.name), Is.EqualTo(new[] { "Face" }));
            var report = context.GetState<OptimizationBuildReport>().Expressions;
            Assert.That(report.Applied, Is.True); Assert.That(report.InputControls, Is.EqualTo(2));
            Track(root.GetComponent<VRCAvatarDescriptor>().expressionsMenu);
            Assert.That(settings == null, Is.True);
            context.GetState<ExpressionOptimizationState>().Settings.Protected.Add(("Missing", Control.ControlType.Toggle));
            ExpressionOptimizationPass.Apply(context);
            Assert.That(context.GetState<OptimizationBuildReport>().Expressions.SkippedReason, Does.Contain("matched"));
            foreach (var layer in root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers)
                if (layer.animatorController is AnimatorController built) TrackControllerAssets(built);
        }

        [Test]
        public void ExpressionPassRetainsUnknownRuntimeAndExternalConsumers()
        {
            var menu = ExpressionMenu(ExpressionControl("External", "External"), ExpressionControl("Dead", "Dead"));
            root.AddComponent<ALTOTraceAndOptimizeExpressions>();
            var context = ExpressionBuild(menu);
            ExternalParameterAccess.Register("Expression tests", access => access.Readers.Add(n => n == "External"));
            try
            {
                ExpressionOptimizationPass.Apply(context);
                var descriptor = root.GetComponent<VRCAvatarDescriptor>(); Track(descriptor.expressionsMenu);
                Assert.That(descriptor.expressionsMenu.controls.Single().name, Is.EqualTo("External"));
                root.AddComponent<ParameterPolicySourceFixture>();
                ExpressionOptimizationPass.Apply(context);
                Assert.That(context.GetState<OptimizationBuildReport>().Expressions.SkippedReason, Does.Contain("unsupported runtime"));
            }
            finally { ExternalParameterAccess.Register("Expression tests", _ => { }); }
        }

        [Test]
        public void SdkCleanupWaitsForLateConsumersAndKeepsCompressionDeclarations()
        {
            Mesh("Visible");
            root.AddComponent<ALTOTraceAndOptimizeExpressions>();
            var menu = ExpressionMenu(ExpressionControl("Dead synced", "OriginalSync"),
                ExpressionControl("Dead local", "DeadLocal"), ExpressionControl("Late output", "LateInput"));
            var context = ExpressionBuild(menu, Controller(Clip()));
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            var parameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            parameters.parameters = new[] {
                new VRCExpressionParameters.Parameter { name = "OriginalSync", networkSynced = true },
                new VRCExpressionParameters.Parameter { name = "DeadLocal", networkSynced = false },
                new VRCExpressionParameters.Parameter { name = "LateInput", networkSynced = false },
                // Late preprocessors can also deduplicate a mixed local/synced declaration.
                new VRCExpressionParameters.Parameter { name = "OriginalSync", networkSynced = false }
            };
            descriptor.expressionParameters = parameters;
            AvatarOutputSdkFinalizer.Begin(root);
            ExpressionOptimizationPass.Apply(context);
            Assert.That(descriptor.expressionsMenu, Is.SameAs(menu));
            Assert.That(descriptor.expressionParameters, Is.SameAs(parameters));

            var layers = descriptor.baseAnimationLayers;
            TrackControllerAssets((AnimatorController)layers[0].animatorController);
            layers[0].animatorController = TreeController(("LateInput", StaticCurve(Binding("Visible"), 100)));
            descriptor.baseAnimationLayers = layers;
            var compressed = Track(UnityEngine.Object.Instantiate(parameters));
            compressed.parameters[0].networkSynced = false;
            compressed.parameters = compressed.parameters.Take(3).Concat(new[] {
                new VRCExpressionParameters.Parameter { name = "TransportSync", networkSynced = true, saved = true, defaultValue = 2 },
                new VRCExpressionParameters.Parameter { name = "TransportLocal", networkSynced = false }
            }).ToArray();
            descriptor.expressionParameters = compressed;
            AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            Assert.That(descriptor.expressionsMenu.controls.Select(c => c.name), Is.EqualTo(new[] { "Late output" }));
            Assert.That(descriptor.expressionParameters.parameters.Select(p => p.name),
                Is.EqualTo(new[] { "OriginalSync", "LateInput", "TransportSync", "TransportLocal" }));
            Assert.That(descriptor.expressionParameters.parameters.Where(p => p.networkSynced).Select(JsonUtility.ToJson),
                Is.EqualTo(compressed.parameters.Where(p => p.networkSynced).Select(JsonUtility.ToJson)));
            Assert.That(parameters.parameters[0].networkSynced, Is.True);
            Assert.That(menu.controls.Count, Is.EqualTo(3));
            var report = context.GetState<OptimizationBuildReport>().Expressions;
            Assert.That(report.Applied, Is.True);
            Assert.That(report.RemovedLocalParameters, Is.EqualTo(new[] { "DeadLocal" }));
            // Completing twice must not replay a stale build session.
            var resultMenu = descriptor.expressionsMenu;
            AvatarOutputSdkFinalizer.Complete(root, _ => Assert.Fail("No pending cleanup should remain."));
            Assert.That(descriptor.expressionsMenu, Is.SameAs(resultMenu));
        }
    }
}
