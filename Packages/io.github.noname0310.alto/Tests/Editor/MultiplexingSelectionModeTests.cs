using System;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ParameterLayout;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(ParameterMultiplexingMode.AutomaticMenuInputs)]
        [TestCase(ParameterMultiplexingMode.SelectedInputs)]
        [TestCase(ParameterMultiplexingMode.AllSynchronizedInputs)]
        public void MultiplexingAllInputsRequiresExplicitSelectionAndRetainsPreservation(ParameterMultiplexingMode mode)
        {
            var descriptor = LayoutDescriptor();
            var source = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            source.parameters = MultiplexInputs(8).Concat(new[] { LayoutParameter("GestureLeft", ValueType.Int) }).ToArray();
            descriptor.expressionParameters = source;
            string original = EditorJsonUtility.ToJson(source);
            var button = ExpressionControl("Momentary", "Float3"); button.type = VRCExpressionsMenu.Control.ControlType.Button;
            descriptor.expressionsMenu = ExpressionMenu(button);
            PolicyChild("Receiver").AddComponent<VRCContactReceiver>().parameter = "Float2";
            var context = new BuildContext(root, null);
            var policy = context.GetState<ParameterPolicyState>(); policy.Enabled = true; policy.Names.Add("Float1");
            var settings = new ParameterCompressionSettings { Multiplexing = mode, Budget = 40 };
            settings.KeepNative.Add("Float0");
            var report = new OptimizationBuildReport();
            if (mode == ParameterMultiplexingMode.AllSynchronizedInputs)
            {
                var result = ParameterCompressionPass.Apply(context, settings, null, report, o => Track(o));
                Assert.That(result.Multiplexing.Inputs.Select(p => p.Name),
                    Is.EquivalentTo(Enumerable.Range(2, 6).Select(i => "Float" + i)));
                Assert.That(descriptor.expressionParameters.CalcTotalCost(), Is.EqualTo(40));
                foreach (var logical in source.parameters)
                {
                    var output = descriptor.expressionParameters.parameters.Single(p => p.name == logical.name);
                    Assert.That(output.networkSynced, Is.EqualTo(new[] { "Float0", "Float1", "GestureLeft" }.Contains(logical.name)));
                    Assert.That((output.valueType, output.defaultValue, output.saved),
                        Is.EqualTo((logical.valueType, logical.defaultValue, logical.saved)));
                }
                Assert.That(report.Messages.Any(m => m.StartsWith("All Synchronized Inputs", StringComparison.Ordinal)), Is.True);
                TrackControllerAssets((UnityEditor.Animations.AnimatorController)descriptor.baseAnimationLayers
                    .Single(l => l.type == VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType.FX).animatorController);
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => ParameterCompressionPass.Apply(context, settings, null, report,
                    _ => Assert.Fail("Automatic or empty Selected Inputs must not silently opt into all inputs.")));
                Assert.That(descriptor.expressionParameters, Is.SameAs(source));
                Assert.That(descriptor.baseAnimationLayers.All(l => l.animatorController == null), Is.True);
            }
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(original));
        }

        [TestCase(ParameterMultiplexingMode.AutomaticMenuInputs)]
        [TestCase(ParameterMultiplexingMode.SelectedInputs)]
        [TestCase(ParameterMultiplexingMode.AllSynchronizedInputs)]
        [TestCase(ParameterMultiplexingMode.AutomaticByPriority)]
        public void MultiplexingCaptureUsesOnlyTheExplicitSelectionMode(ParameterMultiplexingMode mode)
        {
            LayoutDescriptor();
            var component = root.AddComponent<ALTOParameterCompression>();
            Assert.That(component.TimeMultiplexing, Is.EqualTo(ParameterMultiplexingMode.AutomaticByPriority));
            component.TimeMultiplexing = mode;
            component.MultiplexedParameters.Names.Add("Selected");
            component.KeepNativeParameters.Names.Add("Protected");
            var settings = ParameterCompressionPass.Capture(new BuildContext(root, null), true);
            Assert.That(settings.Multiplexing, Is.EqualTo(mode));
            Assert.That(settings.MultiplexNames, Is.EquivalentTo(mode == ParameterMultiplexingMode.SelectedInputs ? new[] { "Selected" } : Array.Empty<string>()));
            Assert.That(settings.KeepNative, Does.Contain("Protected"));
        }
    }
}
