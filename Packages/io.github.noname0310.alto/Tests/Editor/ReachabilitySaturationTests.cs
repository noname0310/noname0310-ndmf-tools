using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void ReachabilitySaturationPreservesTypedUniversesAndIncompleteCells()
        {
            var fixture = ReachabilityFixture("Input");
            fixture.controller.parameters = new[] {
                new AnimatorControllerParameter { name = "Input", type = AnimatorControllerParameterType.Bool },
                new AnimatorControllerParameter { name = "Counter", type = AnimatorControllerParameterType.Int }
            };
            Edge(fixture.idle, fixture.face, "Input", 2);
            Driver(fixture.idle, "Input", 100);
            Driver(fixture.idle, "Counter", 3);
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controllers = new[] { clone.Clone(fixture.controller) };
            var actual = AnimatorReachability.Analyze(root, controllers, new[] { "Input" });
            Assert.That(actual.Values("Input").Same(ParameterValues.Constant(0).Union(ParameterValues.Constant(1))), Is.True);
            Assert.That(actual.Values("Counter").Same(ParameterValues.Constant(0).Union(ParameterValues.Constant(3))), Is.True);
            Assert.That(actual.Clips(controllers[0]).Any(c => c.Name == "Expression"), Is.False);
            AssertSameSaturatedReachability(actual, AnimatorReachability.Analyze(root, controllers, new[] { "Input" }, false), controllers);
        }

        [Test]
        public void ReachabilitySaturationMatchesPropagationAcrossSharedTypesAndCyclicDrivers()
        {
            var random = new System.Random(182904);
            var types = new[] { AnimatorControllerParameterType.Bool, AnimatorControllerParameterType.Int,
                AnimatorControllerParameterType.Float, AnimatorControllerParameterType.Trigger };
            var modes = new[] { AnimatorConditionMode.If, AnimatorConditionMode.IfNot, AnimatorConditionMode.Equals,
                AnimatorConditionMode.NotEqual, AnimatorConditionMode.Greater, AnimatorConditionMode.Less };
            var operations = new[] { VRC_AvatarParameterDriver.ChangeType.Set, VRC_AvatarParameterDriver.ChangeType.Add,
                VRC_AvatarParameterDriver.ChangeType.Copy, VRC_AvatarParameterDriver.ChangeType.Random };
            string[] names = { "A", "B", "C", "IsLocal", "Missing" };
            for (int sample = 0; sample < 64; sample++)
            {
                var controllers = new List<AnimatorController>();
                for (int owner = 0; owner < 2; owner++)
                {
                    var clips = new[] { Clip(), Clip(), Clip(), Clip() };
                    for (int i = 0; i < clips.Length; i++)
                        AnimationUtility.SetEditorCurve(clips[i], EditorCurveBinding.FloatCurve("", typeof(Animator), names[(i + sample) % names.Length]),
                            AnimationCurve.Constant(0, 1, random.Next(-3, 4)));
                    var controller = Controller(clips);
                    controller.parameters = names.Take(4).Select((name, i) => new AnimatorControllerParameter {
                        name = name, type = types[(i + owner + sample) % types.Length], defaultBool = random.Next(2) == 0,
                        defaultInt = random.Next(-2, 3), defaultFloat = random.Next(-2, 3) * .5f
                    }).ToArray();
                    var states = controller.layers[0].stateMachine.states.Select(s => s.state).ToArray();
                    foreach (var state in states)
                    {
                        for (int edge = 0; edge < 2; edge++)
                        {
                            var transition = Edge(state, states[random.Next(states.Length)], names[random.Next(names.Length)],
                                random.Next(-2, 4), modes[random.Next(modes.Length)]);
                            transition.mute = random.Next(5) == 0; transition.solo = random.Next(8) == 0;
                        }
                        var driver = Driver(state, names[random.Next(names.Length)], random.Next(-3, 4),
                            operations[random.Next(operations.Length)], names[random.Next(names.Length)]);
                        driver.localOnly = random.Next(2) == 0;
                        driver.parameters[0].valueMin = -2; driver.parameters[0].valueMax = 3;
                        driver.parameters[0].convertRange = random.Next(2) == 0;
                        if (sample % 9 == 0) driver.parameters[0].value = float.NaN;
                    }
                    controllers.Add(controller);
                }
                var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
                var virtuals = controllers.Select(c => clone.Clone(c)).ToArray();
                var preserve = names.Where((_, i) => (i + sample) % 3 == 0).ToArray();
                AssertSameSaturatedReachability(AnimatorReachability.Analyze(root, virtuals, preserve),
                    AnimatorReachability.Analyze(root, virtuals, preserve, false), virtuals);
                foreach (bool local in new[] { true, false })
                    AssertSameSaturatedReachability(AnimatorReachability.AnalyzeInstance(root, virtuals, local),
                        AnimatorReachability.AnalyzeInstance(root, virtuals, local, false), virtuals);
            }
        }

        private static void AssertSameSaturatedReachability(AnimatorReachability actual, AnimatorReachability expected,
            VirtualAnimatorController[] controllers)
        {
            Assert.That(actual.FallbackReason, Is.EqualTo(expected.FallbackReason));
            Assert.That(actual.StateCount, Is.EqualTo(expected.StateCount));
            Assert.That(actual.ReachableStateCount, Is.EqualTo(expected.ReachableStateCount));
            foreach (string name in controllers.SelectMany(c => c.Parameters.Keys).Concat(new[] { "Missing" }).Distinct())
                Assert.That(actual.Values(name).Same(expected.Values(name)), Is.True, name);
            foreach (var controller in controllers)
            {
                Assert.That(actual.Clips(controller), Is.EquivalentTo(expected.Clips(controller)));
                foreach (var node in controller.AllReachableNodes())
                    if (node is VirtualState state) Assert.That(actual.IsReachable(state), Is.EqualTo(expected.IsReachable(state)));
                    else if (node is VirtualStateMachine machine) Assert.That(actual.IsReachable(machine), Is.EqualTo(expected.IsReachable(machine)));
                    else if (node is VirtualTransitionBase transition) Assert.That(actual.CanTake(transition), Is.EqualTo(expected.CanTake(transition)));
            }
        }
    }
}
