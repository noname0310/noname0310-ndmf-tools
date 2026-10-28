using System;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void DependencyFixedPointCollectsCascadingSensorsAndRespectsObservers(
            [Values("None", "Network", "Menu", "PreserveInput", "PreserveComponent", "DisabledPruning")] string observer,
            [Values("Ndmf", "Sdk", "SdkExpressions")] string pipeline)
        {
            Mesh("face");
            var settings = ObjectSettings().Component;
            var face = StaticCurve(Binding("face"), 70); face.name = "Face";
            var source = Controller(face);
            var receivers = Enumerable.Range(0, 3).Select(i =>
            {
                var receiver = PolicyChild("Receiver " + i).AddComponent<VRCContactReceiver>();
                receiver.parameter = "Input" + i;
                return receiver;
            }).ToArray();
            var constraintSource = PolicyChild("Constraint source");
            var constraint = receivers[0].gameObject.AddComponent<PositionConstraint>();
            constraint.AddSource(new ConstraintSource { sourceTransform = constraintSource.transform, weight = 1 });
            constraint.constraintActive = true;
            for (int i = 0; i < receivers.Length; i++)
            {
                source.AddParameter("Input" + i, AnimatorControllerParameterType.Float);
                var machine = Track(new AnimatorStateMachine());
                var first = Track(machine.AddState("Idle"));
                var second = Track(machine.AddState("Active"));
                machine.defaultState = first;
                first.writeDefaultValues = second.writeDefaultValues = false;
                first.motion = i == 0 ? Clip() : StaticCurve(EditorCurveBinding.FloatCurve(receivers[i - 1].name,
                    typeof(Transform), "m_LocalPosition.y"), 0);
                second.motion = i == 0 ? Clip() : StaticCurve(EditorCurveBinding.FloatCurve(receivers[i - 1].name,
                    typeof(Transform), "m_LocalPosition.y"), 1);
                Edge(first, second, "Input" + i, 0.5f, AnimatorConditionMode.Greater);
                Edge(second, first, "Input" + i, 0.5f, AnimatorConditionMode.Less);
                source.layers = source.layers.Concat(new[] { new AnimatorControllerLayer
                    { name = "Sensor logic " + i, stateMachine = machine, defaultWeight = 1 } }).ToArray();
            }
            var expected = SampleFrozen(source, (p, frame) =>
            {
                for (int i = 0; i < receivers.Length; i++) p.SetFloat("Input" + i, frame % 8 > 3 ? 1 : 0);
            }, 32);
            foreach (var receiver in receivers) receiver.transform.localPosition = Vector3.zero;
            var menu = observer == "Menu" ? ExpressionMenu(ExpressionControl("Input", "Input0")) : null;
            if (pipeline == "SdkExpressions")
            {
                var expressions = root.AddComponent<ALTOTraceAndOptimizeExpressions>();
                expressions.RemoveDeadControls = expressions.RemoveFrozenControls = expressions.RemoveEmptySubMenus = false;
            }
            var context = ExpressionBuild(menu, source);
            if (pipeline == "SdkExpressions") FreezeExpressions(receivers.Select((r, i) => new VRCExpressionParameters.Parameter
                { name = "Input" + i, valueType = VRCExpressionParameters.ValueType.Float, networkSynced = observer == "Network" && i == 0 }).ToArray());
            else if (observer == "Network") FreezeExpressions(new VRCExpressionParameters.Parameter
                { name = "Input0", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = true });
            if (observer == "PreserveComponent") settings.PreserveComponents.Add(receivers[0]);
            settings.PruneUnusedSensorParameters = observer != "DisabledPruning";
            ObjectOptimizationPass.Capture(context);
            context.GetState<AnimatorOptimizationState>().Settings = new AnimatorOptimizationSettings
                { StateLogic = true, UnusedParameters = true };
            if (observer == "PreserveInput")
            {
                var policy = context.GetState<ParameterPolicyState>();
                policy.Enabled = true; policy.Names.Add("Input0");
            }
            // External optimizers may have removed authoring components already.
            UnityEngine.Object.DestroyImmediate(settings);
            int rounds = 0;
            if (pipeline == "Ndmf")
                OptimizationSession.RunBoundary(context, "cascade fixture", () => rounds = OptimizationFixedPoint.Apply(context));
            else
            {
                // Earlier NDMF observation did not include these later-generated readers.
                var objects = context.GetState<ObjectOptimizationState>();
                objects.ConsumersCaptured = true; objects.InitialReaders.Add("EarlierInput");
                AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
                Noname.AvatarTools.Expressions.ExpressionOptimizationPass.Apply(context);
                AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            }
            var built = (AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController;
            TrackControllerAssets(built);
            bool removed = observer == "None";
            Assert.That(receivers.All(r => r == null), Is.EqualTo(removed));
            Assert.That(constraint == null && constraintSource == null, Is.EqualTo(removed));
            Assert.That(built.layers.Length, Is.EqualTo(source.layers.Length), "Layer-control indices remain stable");
            if (removed)
            {
                if (pipeline == "Ndmf") Assert.That(rounds, Is.GreaterThanOrEqualTo(4), "Removal of each sensor exposes the next dead output");
                Assert.That(built.parameters.Select(p => p.name), Does.Not.Contain("Input2"));
                if (pipeline == "SdkExpressions")
                    Assert.That(root.GetComponent<VRCAvatarDescriptor>().expressionParameters.parameters, Is.Empty);
            }
            else Assert.That(built.parameters.Select(p => p.name), Does.Contain("Input0"));
            renderer.SetBlendShapeWeight(0, 0);
            UnityEngine.Object.DestroyImmediate(root.GetComponent<Animator>());
            var actual = SampleFrozen(built, (p, frame) =>
            {
                foreach (var parameter in built.parameters.Where(p => p.name.StartsWith("Input", StringComparison.Ordinal)))
                    p.SetFloat(parameter.name, frame % 8 > 3 ? 1 : 0);
            }, 32);
            root.AddComponent<Animator>();
            Assert.That(actual, Is.EqualTo(expected).Within(1e-5), "Observable face output is unchanged");
            OptimizationSession.RunBoundary(context, "converged fixture", () =>
                Assert.That(OptimizationFixedPoint.Apply(context), Is.EqualTo(1), "Reanalysis makes no additional changes"));
            Assert.That(source.parameters.Length, Is.EqualTo(3));
            Assert.That(source.layers.Skip(1).All(l => l.stateMachine.states.Length == 2), Is.True, "Source controllers remain intact");
            TrackControllerAssets((AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController);
        }

        [Test]
        public void EmptyDriverRemovalCountsAsProgressEvenWithoutStatesOrCurvesToRemove()
        {
            var source = Controller(Clip());
            Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAvatarParameterDriver>());
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var settings = new AnimatorOptimizationSettings { StateLogic = true };
            Assert.That(AnimatorOptimizer.Optimize(root, new[] { controller }, settings).Changes, Is.EqualTo(1));
            Assert.That(AnimatorOptimizer.Optimize(root, new[] { controller }, settings).Changes, Is.Zero);
            Assert.That(source.layers[0].stateMachine.defaultState.behaviours.Length, Is.EqualTo(1));
        }
    }
}
