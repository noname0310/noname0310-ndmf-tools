using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.ParameterLayout;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false, true, true)]
        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(false, false, true)]
        [TestCase(true, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(true, false, true)]
        public void ParameterTransportPreservesNativePoseAndDefaultRestoration(bool multiplex, bool treeDefaults, bool poseDefaults)
        {
            Mesh("tracking", "expression");
            var source = TreeController(("P", StaticCurve(Binding("tracking"), 100)),
                ("Q", StaticCurve(Binding("tracking"), 20)));
            source.layers[0].stateMachine.defaultState.writeDefaultValues = treeDefaults;
            source.AddParameter("Expression", AnimatorControllerParameterType.Bool);
            var machine = Track(new AnimatorStateMachine());
            var idle = Track(machine.AddState("Idle")); idle.motion = Clip(); idle.writeDefaultValues = poseDefaults;
            var pose = Track(machine.AddState("Expression")); pose.motion = StaticCurve(Binding("expression"), 80);
            pose.writeDefaultValues = poseDefaults; machine.defaultState = idle;
            var enter = Track(idle.AddTransition(pose)); enter.hasExitTime = false; enter.duration = 0;
            enter.AddCondition(AnimatorConditionMode.If, 0, "Expression");
            var exit = Track(pose.AddTransition(idle)); exit.hasExitTime = false; exit.duration = 0;
            exit.AddCondition(AnimatorConditionMode.IfNot, 0, "Expression");
            source.layers = source.layers.Concat(new[] { new AnimatorControllerLayer
                { name = "Expression", defaultWeight = 1, stateMachine = machine } }).ToArray();

            var expected = SampleTransportPose(source);
            var clone = new CloneContext(VRChatPlatformAnimatorBindings.Instance);
            var controller = clone.Clone(source);
            if (multiplex)
            {
                var inputs = MultiplexInputs(); var plan = MultiplexPlan(inputs);
                MultiplexAnimator.Append(clone, controller, plan, inputs.ToDictionary(p => p.name),
                    inputs.Select(p => p.name).ToHashSet(), new Dictionary<string, float>(),
                    new HashSet<string>(controller.Parameters.Keys.Concat(plan.WireDeclarations().Select(p => p.name))), null);
            }
            else IntegerEncodingAnimator.Append(clone, controller, IntegerContract(), 0);
            var actual = SampleTransportPose(CommitAnimator(controller, root));
            for (int i = 0; i < expected.Length; i++)
                Assert.That(Vector2.Distance(actual[i], expected[i]), Is.LessThan(1e-4),
                    "Transport changed native pose at frame " + i + ": expected " + expected[i] + ", actual " + actual[i]);
            if (treeDefaults && poseDefaults)
                Assert.That(actual.Last().y, Is.EqualTo(expected[0].y).Within(1e-4), "Expression must restore its authored default.");
        }

        private Vector2[] SampleTransportPose(AnimatorController controller)
        {
            renderer.SetBlendShapeWeight(0, 17); renderer.SetBlendShapeWeight(1, 23);
            var animator = root.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var graph = PlayableGraph.Create("Transport pose equivalence");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Avatar", animator).SetSourcePlayable(playable);
                graph.Play();
                if (controller.parameters.Any(p => p.name == "IsLocal")) playable.SetBool("IsLocal", true);
                var values = new Vector2[120];
                for (int i = 0; i < values.Length; i++)
                {
                    playable.SetFloat("P", (i / 10 % 3 + 1) * .2f);
                    playable.SetFloat("Q", .7f);
                    playable.SetBool("Expression", i >= 40 && i < 80);
                    graph.Evaluate(1f / 60);
                    values[i] = new Vector2(renderer.GetBlendShapeWeight(0), renderer.GetBlendShapeWeight(1));
                }
                return values;
            }
            finally { graph.Destroy(); Object.DestroyImmediate(animator); }
        }
    }
}
