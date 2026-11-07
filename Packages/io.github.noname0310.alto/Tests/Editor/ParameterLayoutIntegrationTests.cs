using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.ParameterLayout;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(ParameterTransmissionMode.Guarded)] [TestCase(ParameterTransmissionMode.LowLatency)]
        public void SdkTransportPreparationMatchesDirectBakeAndPreservesGeneratorDeclarations(ParameterTransmissionMode transmission)
        {
            var descriptor = LayoutDescriptor();
            var source = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            source.parameters = MultiplexInputs(4, 4, 8); descriptor.expressionParameters = source;
            string original = EditorJsonUtility.ToJson(source);
            var settings = new ParameterLayoutSettings { Compression = new ParameterCompressionSettings
                { Multiplexing = ParameterMultiplexingMode.SelectedInputs, Budget = 32, HoldSeconds = .1f, Transmission = transmission } };
            settings.Compression.MultiplexNames.UnionWith(source.parameters.Select(p => p.name));
            using var fixture = new LayoutTestStore();
            ParameterLayoutPass.ApplyFinal(root, settings, ParameterLayoutPlatform.PC, new OptimizationBuildReport(),
                value => Track(value), fixture.Store, new BuildContext(root, null));
            var expected = descriptor.expressionParameters.parameters.Select(p =>
                (p.name, p.valueType, p.networkSynced, p.saved, p.defaultValue)).ToArray();
            foreach (var layer in descriptor.baseAnimationLayers)
                if (layer.animatorController is AnimatorController controller) TrackControllerAssets(controller);
            descriptor.expressionParameters = source;
            descriptor.baseAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();

            ParameterLayoutSdkSession.Begin(root);
            var report = new OptimizationBuildReport(); report.Initialize(root, "PC");
            var context = new BuildContext(root, null);
            Assert.That(ParameterLayoutSdkSession.Defer(context, settings, ParameterLayoutPlatform.PC, report), Is.True);
            Assert.That(descriptor.expressionParameters, Is.SameAs(source));
            Assert.That(source.parameters.All(p => p.networkSynced), Is.True,
                "Earlier SDK validation must still see native synchronized parameters, including Bools");
            Assert.That(descriptor.baseAnimationLayers, Is.Empty, "Deferral cannot install transport drivers");
            context.AssetSaver.Dispose();

            ParameterLayoutSdkSession.Prepare(root, value => Track(value), fixture.Store);
            var prepared = descriptor.expressionParameters;
            Assert.That(prepared.CalcTotalCost(), Is.LessThanOrEqualTo(32), "Later cost-based compressors must see the reduced budget");
            Assert.That(prepared.parameters.Select(p => (p.name, p.valueType, p.networkSynced, p.saved, p.defaultValue)), Is.EqualTo(expected));
            var fx = (AnimatorController)descriptor.baseAnimationLayers.Single(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController;
            TrackControllerAssets(fx);
            int layerCount = fx.layers.Length;
            Assert.That(fx.layers.SelectMany(l => l.stateMachine.states).SelectMany(s => s.state.behaviours)
                .OfType<VRCAvatarParameterDriver>().Any(), Is.True);
            ParameterLayoutSdkSession.Prepare(root, value => Track(value), fixture.Store);
            Assert.That(descriptor.expressionParameters, Is.SameAs(prepared));
            Assert.That(fx.layers.Length, Is.EqualTo(layerCount), "Repeated preparation cannot add a second transport");
            ParameterLayoutSdkSession.Complete(root, value => Track(value), fixture.Store);
            Assert.That(descriptor.expressionParameters, Is.SameAs(prepared));
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(original));
            Assert.That(fixture.Store.Read(LayoutId), Is.Null, "A build cannot confirm an upload contract");
        }

        [TestCase("Added")] [TestCase("Removed")] [TestCase("MadeLocal")]
        public void SdkTransportPreparationRejectsUnverifiedEarlierWireChanges(string change)
        {
            var descriptor = LayoutDescriptor();
            var source = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            source.parameters = MultiplexInputs(4, 4, 8); descriptor.expressionParameters = source;
            var settings = new ParameterLayoutSettings { Compression = new ParameterCompressionSettings
                { Multiplexing = ParameterMultiplexingMode.AllSynchronizedInputs, Budget = 32 } };
            ParameterLayoutSdkSession.Begin(root);
            var report = new OptimizationBuildReport(); report.Initialize(root, "PC");
            ParameterLayoutSdkSession.Defer(new BuildContext(root, null), settings, ParameterLayoutPlatform.PC, report);
            var parameters = source.parameters.Select(ParameterLayoutModel.Copy).ToList();
            if (change == "Added") parameters.Add(LayoutParameter("Unknown transport", ValueType.Int));
            if (change == "Removed") parameters.RemoveAt(0);
            if (change == "MadeLocal") parameters[0].networkSynced = false;
            source.parameters = parameters.ToArray();
            string before = EditorJsonUtility.ToJson(source);
            using var fixture = new LayoutTestStore();
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutSdkSession.Prepare(root, value => Track(value), fixture.Store));
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(before));
            Assert.That(descriptor.baseAnimationLayers, Is.Empty);
            Assert.That(fixture.Store.Read(LayoutId), Is.Null);
            Assert.That(report.Measurements.Single().Succeeded, Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void SdkLayoutPersistsAlignedAssetsAfterNdmfDisposalWithoutChangingItsReference(bool additionalLocal)
        {
            string folderName = "AdvancedLayoutSdkFixture_" + Guid.NewGuid().ToString("N");
            string folder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            try
            {
                var descriptor = LayoutDescriptor();
                var source = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
                source.parameters = new[] { LayoutParameter("Expression/Weight"), LayoutParameter("Local", synced: false) };
                if (additionalLocal) source.parameters = source.parameters.Concat(new[] { LayoutParameter("AdditionalInput") }).ToArray();
                descriptor.expressionParameters = source;
                var marker = PolicyChild("Layout settings").AddComponent<ALTOParameterLayout>();
                marker.AdditionalParameters = additionalLocal ? AdditionalSyncedParameterPolicy.KeepLocal : AdditionalSyncedParameterPolicy.Error;
                using var fixture = new LayoutTestStore();
                fixture.Store.Commit(LayoutRecord(LayoutParameter("Removed", ValueType.Bool), LayoutParameter("Expression/Weight")));
                string reference = File.ReadAllText(fixture.Store.PathFor(LayoutId));
                string original = EditorJsonUtility.ToJson(source);

                Assert.That(new AvatarOutputSdkBuildStart().OnPreprocessAvatar(root), Is.True);
                var context = new BuildContext(root, folder);
                ParameterLayoutPass.Capture(context);
                Assert.That(marker == null, Is.True, "The settings component must not reach the runtime avatar");
                var settings = context.GetState<ParameterLayoutState>().Settings;
                var report = context.GetState<OptimizationBuildReport>();
                Assert.That(ParameterLayoutSdkSession.Defer(context, settings, ParameterLayoutPlatform.Android,
                    report, fixture.Store), Is.True);
                Assert.That(descriptor.expressionParameters, Is.SameAs(source));
                context.AssetSaver.Dispose();
                ParameterLayoutSdkSession.Prepare(root, storeForTests: fixture.Store);

                // A late processor may replace the asset, reorder declarations and
                // add local inputs without introducing a different wire protocol.
                var late = Track(UnityEngine.Object.Instantiate(descriptor.expressionParameters));
                late.parameters = late.parameters.Reverse().Select(ParameterLayoutModel.Copy)
                    .Concat(new[] { LayoutParameter("LateLocal", synced: false) }).ToArray();
                descriptor.expressionParameters = late;
                string lateOriginal = EditorJsonUtility.ToJson(late);
                ParameterLayoutSdkSession.Complete(root, storeForTests: fixture.Store);
                var measurement = report.Measurements.Single(m => m.Name == "parameter layout");
                Assert.That(measurement.Name, Is.EqualTo("parameter layout"));
                Assert.That(measurement.Completed && measurement.Succeeded, Is.True);
                Assert.That(measurement.After, Is.Not.Null);

                var output = descriptor.expressionParameters;
                Assert.That(output, Is.Not.SameAs(source));
                Assert.That(output, Is.Not.SameAs(late));
                Assert.That(EditorUtility.IsPersistent(output), Is.True);
                string assetPath = AssetDatabase.GetAssetPath(output);
                Assert.That(assetPath, Does.StartWith(folder + "/"));
                Assert.That(AssetDatabase.LoadAllAssetsAtPath(assetPath), Does.Contain(output));
                Assert.That(output.parameters.Where(p => p.networkSynced).Select(p => p.valueType),
                    Is.EqualTo(new[] { ValueType.Bool, ValueType.Float }));
                Assert.That(output.parameters[1].name, Is.EqualTo("Expression/Weight"));
                Assert.That(output.parameters.Where(p => !p.networkSynced).Select(p => p.name),
                    Is.EquivalentTo(additionalLocal ? new[] { "Local", "LateLocal", "AdditionalInput" } : new[] { "Local", "LateLocal" }));
                Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(original));
                Assert.That(EditorJsonUtility.ToJson(late), Is.EqualTo(lateOriginal));
                Assert.That(File.ReadAllText(fixture.Store.PathFor(LayoutId)), Is.EqualTo(reference));
                Assert.That(new AvatarOutputSdkBuildFinish().OnPreprocessAvatar(root), Is.True,
                    "A finished session must not finalize a second time");
                Assert.That(descriptor.expressionParameters, Is.SameAs(output));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [TestCase("Added")] [TestCase("Removed")] [TestCase("Renamed")]
        [TestCase("Type")] [TestCase("MadeLocal")]
        public void SdkCallbackRejectsChangedTransportDeclarationsBeforeSavingAnAlignedAsset(string change)
        {
            var descriptor = LayoutDescriptor();
            var source = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            source.parameters = new[] { LayoutParameter("Expression/Weight"), LayoutParameter("Toggle", ValueType.Bool) };
            descriptor.expressionParameters = source;
            Assert.That(new AvatarOutputSdkBuildStart().OnPreprocessAvatar(root), Is.True);
            var context = new BuildContext(root, null);
            var report = context.GetState<OptimizationBuildReport>(); report.Initialize(root, "PC");
            ParameterLayoutSdkSession.Defer(context, new ParameterLayoutSettings(), ParameterLayoutPlatform.PC,
                report);
            var changed = source.parameters.Select(ParameterLayoutModel.Copy).ToList();
            if (change == "Added") changed.Add(LayoutParameter("Encoded channel", ValueType.Int));
            if (change == "Removed") changed.RemoveAt(0);
            if (change == "Renamed") changed[0].name = "Encoded channel";
            if (change == "Type") changed[0].valueType = ValueType.Int;
            if (change == "MadeLocal") changed[0].networkSynced = false;
            source.parameters = changed.ToArray();
            string before = EditorJsonUtility.ToJson(source);
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: A later build processor changed synchronized parameter declarations"));
            Assert.That(new AvatarOutputSdkBuildFinish().OnPreprocessAvatar(root), Is.False);
            var measurement = report.Measurements.Single();
            Assert.That(measurement.Completed, Is.True);
            Assert.That(measurement.Succeeded, Is.False);
            Assert.That(measurement.After, Is.Not.Null);
            Assert.That(descriptor.expressionParameters, Is.SameAs(source));
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(before));
        }

        [TestCase(false)] [TestCase(true)]
        public void AlignedDeclarationSlotsPreserveNativeFaceMotionAfterMobileInputsAreRemoved(bool optimize)
        {
            Mesh("Face", "Clothes");
            var source = TreeController(("Expression/Weight", StaticCurve(Binding("Face"), 100)),
                ("Clothes/Toggle", StaticCurve(Binding("Clothes"), 100)));
            source.AddParameter("Expression/Enabled", AnimatorControllerParameterType.Bool);
            source.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
            var machine = source.layers[0].stateMachine;
            var enabled = machine.defaultState;
            var disabled = Track(machine.AddState("Disabled"));
            disabled.motion = Clip("Face", "Clothes"); disabled.writeDefaultValues = true;
            Edge(enabled, disabled, "Expression/Enabled", 0, AnimatorConditionMode.IfNot).duration = 0;
            Edge(disabled, enabled, "Expression/Enabled", 0, AnimatorConditionMode.If).duration = 0;

            var reference = new[] { LayoutParameter("RemovedInt", ValueType.Int), LayoutParameter("Expression/Weight"),
                LayoutParameter("RemovedFlag", ValueType.Bool), LayoutParameter("Clothes/Toggle"),
                LayoutParameter("Expression/Enabled", ValueType.Bool) };
            var mobile = new[] { LayoutParameter("Expression/Enabled", ValueType.Bool),
                LayoutParameter("Clothes/Toggle", synced: false), LayoutParameter("Expression/Weight") };
            mobile[1].defaultValue = 0;
            using var fixture = new LayoutTestStore();
            var upload = new ParameterLayoutUploadTransaction();
            upload.Begin(LayoutId, ParameterLayoutPlatform.PC, true);
            upload.Stage(LayoutRecord(reference)); upload.BuildSucceeded();
            Assert.That(upload.Confirm(LayoutId, fixture.Store), Is.EqualTo(ParameterLayoutConfirmation.Recorded));
            var layout = ParameterLayoutModel.Align(fixture.Store.Read(LayoutId), mobile, AdditionalSyncedParameterPolicy.Error);
            Assert.That(layout.Padding, Is.EqualTo(3));

            // This models delivery by declaration slot, not the VRChat client's
            // byte encoding or transport timing. The receiver is Unity's native
            // Animator; no name-based remapping hides a broken slot order.
            var frames = Enumerable.Range(0, 12).Select(i => new[] { (float)(i % 4), (i % 5) / 4f,
                (float)(i % 2), 1f, i % 4 == 1 ? 0f : 1f }).ToArray();
            var expected = SampleFrozen(source, (p, i) =>
            {
                p.SetBool("IsLocal", false); p.SetFloat("Expression/Weight", frames[i][1]);
                p.SetBool("Expression/Enabled", frames[i][4] != 0); p.SetFloat("Clothes/Toggle", 0);
            }, frames.Length);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var preserved = new HashSet<string> { "Expression/Weight", "Expression/Enabled" };
            var constants = ParameterSpecialization.Prepare(new[] { controller },
                new Dictionary<string, float> { ["Clothes/Toggle"] = 0 }, preserved);
            ParameterSpecialization.Apply(new[] { controller }, constants);
            if (optimize) AnimatorOptimizer.Optimize(root, new[] { controller },
                new() { ConstantTrees = true, Unreachable = true }, preservedParameters: preserved);
            var output = CommitAnimator(controller, root);
            renderer.SetBlendShapeWeight(1, 0);
            var names = output.parameters.Select(p => p.name).ToHashSet();
            var slots = layout.Parameters.Where(p => p.networkSynced).ToArray();
            var actual = SampleFrozen(output, (p, i) =>
            {
                p.SetBool("IsLocal", false);
                for (int slot = 0; slot < slots.Length; slot++)
                {
                    var parameter = slots[slot];
                    if (!names.Contains(parameter.name)) continue;
                    if (parameter.valueType == ValueType.Float) p.SetFloat(parameter.name, frames[i][slot]);
                    else if (parameter.valueType == ValueType.Bool) p.SetBool(parameter.name, frames[i][slot] != 0);
                    else p.SetInteger(parameter.name, (int)frames[i][slot]);
                }
            }, frames.Length, () =>
            {
                Assert.That(renderer.GetBlendShapeWeight(1), Is.Zero.Within(0.0001f),
                    "A padding slot must not reactivate a removed clothing input");
                return renderer.GetBlendShapeWeight(0);
            });
            Assert.That(actual, Is.EqualTo(expected).Within(0.0001f));
            Assert.That(actual.Max() - actual.Min(), Is.GreaterThan(40), "The preserved face input must remain dynamic");
        }
    }
}
