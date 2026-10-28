using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private sealed class ReachabilityFuzzInput
        {
            internal const string Name = "FuzzInput", Gate = "EnableFuzzTransitions";
            internal AnimatorControllerParameterType Type;
            internal bool External;
            internal float On, Off;
            internal float[] Samples;

            internal void Set(AnimatorControllerPlayable playable, int frame)
            {
                playable.SetBool(Gate, frame >= 4);
                // Never drive private inputs from the test harness: their defaults
                // are the only values the production analysis is entitled to assume.
                if (!External) return;
                if (Type == AnimatorControllerParameterType.Bool) playable.SetBool(Name, Samples[frame] != 0);
                else if (Type == AnimatorControllerParameterType.Int) playable.SetInteger(Name, (int)Samples[frame]);
                else playable.SetFloat(Name, Samples[frame]);
            }
        }

        private static void ConfigureFuzzTransition(AnimatorStateTransition transition)
        {
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0;
            transition.offset = 0;
            transition.canTransitionToSelf = false;
        }

        private static void AddFuzzCondition(AnimatorStateTransition transition, ReachabilityFuzzInput input, System.Random random)
        {
            if (input.Type == AnimatorControllerParameterType.Bool)
                transition.AddCondition(random.Next(2) == 0 ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot,
                    0, ReachabilityFuzzInput.Name);
            else if (input.Type == AnimatorControllerParameterType.Int)
            {
                var modes = new[] { AnimatorConditionMode.Equals, AnimatorConditionMode.NotEqual,
                    AnimatorConditionMode.Greater, AnimatorConditionMode.Less };
                transition.AddCondition(modes[random.Next(modes.Length)], random.Next(-1, 5), ReachabilityFuzzInput.Name);
            }
            else
                transition.AddCondition(random.Next(2) == 0 ? AnimatorConditionMode.Greater : AnimatorConditionMode.Less,
                    random.Next(-2, 4) * .25f, ReachabilityFuzzInput.Name);
        }

        private (AnimatorController Controller, ReachabilityFuzzInput Input) CreateReachabilityFuzzCase(
            System.Random random, int sample, VRCAvatarDescriptor descriptor)
        {
            var types = new[] { AnimatorControllerParameterType.Bool, AnimatorControllerParameterType.Int,
                AnimatorControllerParameterType.Float };
            var input = new ReachabilityFuzzInput { Type = types[sample % types.Length], External = sample % 6 < 3 };
            input.On = input.Type == AnimatorControllerParameterType.Bool ? random.Next(2) :
                input.Type == AnimatorControllerParameterType.Int ? random.Next(4) : (random.Next(2) + 1) * .25f;
            input.Off = input.Type == AnimatorControllerParameterType.Bool ? 1 - input.On :
                input.Type == AnimatorControllerParameterType.Int ? ((int)input.On + 1) % 4 : input.On - .5f;
            input.Samples = Enumerable.Range(0, 8).Select(frame => frame < 2 ? input.Off : frame < 4 ? input.On :
                input.Type == AnimatorControllerParameterType.Bool ? random.Next(2) :
                input.Type == AnimatorControllerParameterType.Int ? random.Next(4) : random.Next(-3, 4) * .25f).ToArray();
            var clips = Enumerable.Range(0, 5).Select(i =>
            {
                var clip = Clip("FuzzOutput"); clip.name = "State" + i;
                AnimationUtility.SetEditorCurve(clip, Binding("FuzzOutput"), AnimationCurve.Constant(0, 1, 10 + 20 * i));
                return clip;
            }).ToArray();
            var controller = Controller(clips);
            var machine = controller.layers[0].stateMachine;
            // The shared helper leaves the root machine unnamed. Native full paths
            // use that machine name (".State0" when empty), not the layer label.
            machine.name = controller.layers[0].name;
            var states = machine.states.Select(s => s.state).ToArray();
            bool writeDefaults = sample % 2 == 0;
            foreach (var state in states) state.writeDefaultValues = writeDefaults;
            float initial = input.External ? input.Off : input.On;
            controller.parameters = new[]
            {
                new AnimatorControllerParameter { name = ReachabilityFuzzInput.Name, type = input.Type,
                    defaultBool = initial != 0, defaultInt = (int)initial, defaultFloat = initial },
                new AnimatorControllerParameter { name = ReachabilityFuzzInput.Gate, type = AnimatorControllerParameterType.Bool }
            };
            var expressions = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            var declarations = new List<VRCExpressionParameters.Parameter>
            {
                new() { name = ReachabilityFuzzInput.Gate, valueType = VRCExpressionParameters.ValueType.Bool,
                    saved = false, networkSynced = true }
            };
            if (input.External)
                declarations.Add(new VRCExpressionParameters.Parameter { name = ReachabilityFuzzInput.Name,
                    valueType = input.Type == AnimatorControllerParameterType.Bool ? VRCExpressionParameters.ValueType.Bool :
                        input.Type == AnimatorControllerParameterType.Int ? VRCExpressionParameters.ValueType.Int : VRCExpressionParameters.ValueType.Float,
                    defaultValue = initial, saved = false, networkSynced = true });
            expressions.parameters = declarations.ToArray(); descriptor.expressionParameters = expressions;

            var control = Track(states[0].AddTransition(states[1])); ConfigureFuzzTransition(control);
            control.AddCondition(input.Type == AnimatorControllerParameterType.Bool ?
                    (input.On != 0 ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot) :
                    input.Type == AnimatorControllerParameterType.Int ? AnimatorConditionMode.Equals : AnimatorConditionMode.Greater,
                input.Type == AnimatorControllerParameterType.Float ? input.On - .125f : input.On, ReachabilityFuzzInput.Name);
            for (int edge = 0; edge < 5; edge++)
            {
                int from = random.Next(1, 4), to = 1 + (from - 1 + random.Next(1, 3)) % 3;
                var transition = Track(edge == 4 && sample % 2 == 0 ? machine.AddAnyStateTransition(states[to]) : states[from].AddTransition(states[to]));
                ConfigureFuzzTransition(transition);
                transition.mute = random.Next(4) == 0;
                transition.AddCondition(AnimatorConditionMode.If, 0, ReachabilityFuzzInput.Gate);
                AddFuzzCondition(transition, input, random);
                if (random.Next(2) == 0) AddFuzzCondition(transition, input, random);
            }
            // This state's only incoming edge is muted. All clips cover the same
            // observable binding, so pruning it cannot alter Write Defaults coverage.
            var dead = Track(states[0].AddTransition(states[4])); ConfigureFuzzTransition(dead); dead.mute = true;
            return (controller, input);
        }

        private (int[] States, float[] Output) SampleReachabilityFuzz(AnimatorController controller,
            ReachabilityFuzzInput input, IReadOnlyDictionary<int, VirtualState> originalStates,
            AnimatorReachability analysis, string replay)
        {
            renderer.SetBlendShapeWeight(0, 0);
            var animator = root.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var graph = PlayableGraph.Create("ALTO reachability differential fuzz");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Avatar", animator).SetSourcePlayable(playable);
                graph.Play(); graph.Evaluate(0);
                var states = new int[input.Samples.Length]; var values = new float[input.Samples.Length];
                for (int frame = 0; frame < states.Length; frame++)
                {
                    input.Set(playable, frame); graph.Evaluate(1f / 60);
                    states[frame] = playable.GetCurrentAnimatorStateInfo(0).fullPathHash;
                    Check(states[frame], frame, "current");
                    if (playable.IsInTransition(0)) Check(playable.GetNextAnimatorStateInfo(0).fullPathHash, frame, "next");
                    values[frame] = renderer.GetBlendShapeWeight(0);
                }
                return (states, values);
            }
            finally { graph.Destroy(); Object.DestroyImmediate(animator); }

            void Check(int hash, int frame, string role)
            {
                Assert.That(originalStates.TryGetValue(hash, out var state), Is.True,
                    replay + ", frame=" + frame + ", " + role + " fullPathHash=" + hash + " is not an authored flat state");
                Assert.That(analysis.IsReachable(state), Is.True,
                    replay + ", frame=" + frame + ", native " + role + " state=" + state.Name + " was classified unreachable");
            }
        }

        [TestCase(310)] [TestCase(20261029)]
        public void ReachabilityAndPruningMatchNativeStateMachineFuzz(int seed)
        {
            Mesh("FuzzOutput");
            var descriptor = root.AddComponent<VRCAvatarDescriptor>(); descriptor.customExpressions = true;
            descriptor.baseAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            var random = new System.Random(seed);
            // 12 graphs per seed, 8 native samples before and after pruning. Each
            // parameter type occurs with both private defaults and external writes.
            for (int sample = 0; sample < 12; sample++)
            {
                int firstOwned = objects.Count;
                try
                {
                    var (source, input) = CreateReachabilityFuzzCase(random, sample, descriptor);
                    var machine = source.layers[0].stateMachine;
                    var sourceStates = machine.states.Select(s => s.state).ToArray();
                    string GraphDescription() => string.Join(";", sourceStates.SelectMany(s => s.transitions.Select(t => (From: s.name, Transition: t)))
                        .Concat(machine.anyStateTransitions.Select(t => (From: "AnyState", Transition: t))).Select(e =>
                            e.From + "->" + e.Transition.destinationState.name + (e.Transition.mute ? "[muted]" : "") + ":" +
                            string.Join("&", e.Transition.conditions.Select(c => c.parameter + " " + c.mode + " " + c.threshold.ToString("R", CultureInfo.InvariantCulture)))));
                    string authoredGraph = GraphDescription();
                    string replay = "seed=" + seed + ", sample=" + sample + ", type=" + input.Type + ", external=" + input.External +
                        ", on=" + input.On.ToString("R", CultureInfo.InvariantCulture) + ", inputs=" +
                        string.Join(",", input.Samples.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + ", graph=" + authoredGraph;
                    var virtualController = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
                    var analysis = AnimatorReachability.Analyze(root, new[] { virtualController });
                    Assert.That(analysis.FallbackReason, Is.Null, replay);
                    var states = virtualController.Layers.Single().StateMachine.States.Select(s => s.State).ToArray();
                    // Resolve full paths from the authored machine. Short state-name
                    // hashes are not identities even though this fixture is flat.
                    var hashes = states.ToDictionary(s => Animator.StringToHash(machine.name + "." + s.Name));
                    Assert.That(analysis.IsReachable(states.Single(s => s.Name == "State4")), Is.False, replay);
                    var before = SampleReachabilityFuzz(source, input, hashes, analysis, replay + ", original");
                    int idle = Animator.StringToHash(machine.name + ".State0");
                    int active = Animator.StringToHash(machine.name + ".State1");
                    if (input.External) Assert.That(before.States[1], Is.EqualTo(idle), replay + ", external off positive control");
                    Assert.That(before.States[3], Is.EqualTo(active), replay + ", on/default positive control");
                    Assert.That(before.Output[3], Is.EqualTo(30).Within(.001f), replay + ", observable output positive control");

                    var report = AnimatorOptimizer.Optimize(root, new[] { virtualController },
                        new AnimatorOptimizationSettings { Unreachable = true }, analysis: analysis);
                    Assert.That(report.States, Is.GreaterThanOrEqualTo(1), replay + ", muted sentinel must be removed");
                    // Serialize in a generic scratch root so this small fixture does
                    // not load and clone the SDK's unrelated default playable layers.
                    var optimized = CommitAnimator(virtualController, PolicyChild("Fuzz serialization target"));
                    var after = SampleReachabilityFuzz(optimized, input, hashes, analysis, replay + ", optimized");
                    Assert.That(after.States, Is.EqualTo(before.States), replay + ", native state trace");
                    for (int frame = 0; frame < before.Output.Length; frame++)
                        Assert.That(after.Output[frame], Is.EqualTo(before.Output[frame]).Within(.001f), replay + ", output frame=" + frame);
                    Assert.That(machine.states.Select(s => s.state), Is.EqualTo(sourceStates), replay + ", source states changed");
                    Assert.That(GraphDescription(), Is.EqualTo(authoredGraph), replay + ", source transitions changed");
                    for (int state = 0; state < sourceStates.Length; state++)
                        Assert.That(AnimationUtility.GetEditorCurve((AnimationClip)sourceStates[state].motion, Binding("FuzzOutput")).keys.Select(k => k.value),
                            Is.All.EqualTo(10f + 20 * state), replay + ", source output curve changed");
                }
                finally
                {
                    descriptor.expressionParameters = null;
                    for (int i = objects.Count - 1; i >= firstOwned; i--) if (objects[i] != null) Object.DestroyImmediate(objects[i]);
                    objects.RemoveRange(firstOwned, objects.Count - firstOwned);
                }
            }
        }
    }
}
