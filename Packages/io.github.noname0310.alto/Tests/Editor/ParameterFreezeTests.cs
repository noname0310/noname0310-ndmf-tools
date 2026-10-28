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
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static Dictionary<string, ParameterConstant> Constants(VirtualAnimatorController controller, string name, float value) =>
            ParameterSpecialization.Prepare(new[] { controller }, new Dictionary<string, float> { [name] = value }, new HashSet<string>());

        private float[] SampleFrozen(AnimatorController controller, Action<AnimatorControllerPlayable, int> setInputs, int frames = 4, Func<float> sample = null)
        {
            renderer.SetBlendShapeWeight(0, 0);
            var animator = root.AddComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var graph = PlayableGraph.Create("Parameter specialization equivalence");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Avatar", animator).SetSourcePlayable(playable);
                graph.Play();
                var samples = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    setInputs(playable, i);
                    graph.Evaluate(1f / 60);
                    samples[i] = sample != null ? sample() : renderer.GetBlendShapeWeight(0);
                }
                return samples;
            }
            finally { graph.Destroy(); Object.DestroyImmediate(animator); }
        }

        [TestCase(AnimatorControllerParameterType.Bool, 0.5f)]
        [TestCase(AnimatorControllerParameterType.Int, 1.2f)]
        [TestCase(AnimatorControllerParameterType.Int, float.MaxValue)]
        [TestCase(AnimatorControllerParameterType.Trigger, 0)]
        [TestCase(AnimatorControllerParameterType.Float, float.NaN)]
        [TestCase(AnimatorControllerParameterType.Float, float.PositiveInfinity)]
        public void FreezeRejectsInvalidTypedConstants(AnimatorControllerParameterType type, float value)
        {
            var source = Controller(Clip()); source.AddParameter("Input", type);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            Assert.Throws<InvalidOperationException>(() => Constants(controller, "Input", value));
            Assert.That(controller.Parameters.Count, Is.EqualTo(1));
        }

        [Test]
        public void FreezeRejectsMissingMixedBuiltInAndPreservedParametersBeforeWriting()
        {
            var a = Controller(Clip()); a.AddParameter("Input", AnimatorControllerParameterType.Int);
            a.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
            var b = Controller(Clip()); b.AddParameter("Input", AnimatorControllerParameterType.Float);
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var ca = clone.Clone(a); var cb = clone.Clone(b);
            Assert.Throws<InvalidOperationException>(() => Constants(ca, "Missing", 0));
            Assert.Throws<InvalidOperationException>(() => Constants(ca, "IsLocal", 0));
            Assert.Throws<InvalidOperationException>(() => ParameterSpecialization.Prepare(new[] { ca, cb }, new Dictionary<string, float> { ["Input"] = 0.5f }, new HashSet<string>()));
            Assert.Throws<InvalidOperationException>(() => ParameterSpecialization.Prepare(new[] { ca }, new Dictionary<string, float> { ["Input"] = 0 }, new HashSet<string> { "Input" }));
            Assert.That(ca.Parameters.Count, Is.EqualTo(2));
        }

        [Test]
        public void FreezeRemovesAllDriverWritesAndRedirectsCopiesWithoutChangingConversionOrOrder()
        {
            var source = Controller(Clip());
            source.AddParameter("Input", AnimatorControllerParameterType.Float); source.AddParameter("Output", AnimatorControllerParameterType.Int);
            var state = source.layers[0].stateMachine.defaultState;
            var driver = Driver(state, "Input", 1);
            driver.localOnly = true;
            driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter { name = "Input", type = VRC_AvatarParameterDriver.ChangeType.Add, value = 2 });
            driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter { name = "Input", type = VRC_AvatarParameterDriver.ChangeType.Random, valueMin = 1, valueMax = 3 });
            driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter
            {
                name = "Output", type = VRC_AvatarParameterDriver.ChangeType.Copy, source = "Input",
                convertRange = true, sourceMin = -1, sourceMax = 1, destMin = 5, destMax = 25
            });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var constants = Constants(controller, "Input", 0.25f);
            var changes = ParameterSpecialization.Apply(new[] { controller }, constants);
            var result = ParameterSpecialization.Behaviours(controller.AllReachableNodes()).OfType<VRC_AvatarParameterDriver>().Single();
            Assert.That(changes.DriverWrites, Is.EqualTo(3));
            Assert.That(result.localOnly, Is.True);
            var copy = result.parameters.Single();
            Assert.That(copy.source, Is.EqualTo(constants["Input"].InternalName));
            Assert.That(copy.type, Is.EqualTo(VRC_AvatarParameterDriver.ChangeType.Copy));
            Assert.That(copy.convertRange && copy.sourceMin == -1 && copy.sourceMax == 1 && copy.destMin == 5 && copy.destMax == 25, Is.True);
            Assert.That(driver.parameters.Count, Is.EqualTo(4), "Authoring asset must stay unchanged");
        }

        [Test]
        public void FreezeRemovesParameterCurvesPreservesDurationAndDetectsLateWriters()
        {
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, ParameterBinding("Input"), AnimationCurve.Linear(0, 0, 3, 1));
            var source = Controller(clip); source.AddParameter("Input", AnimatorControllerParameterType.Float);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var constants = Constants(controller, "Input", 0.5f);
            var changes = ParameterSpecialization.Apply(new[] { controller }, constants);
            var output = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(changes.ParameterCurves, Is.EqualTo(1));
            Assert.That(output.GetFloatCurve(ParameterBinding("Input")), Is.Null);
            Assert.That(MotionAnalysis.EndTime(output), Is.EqualTo(3));
            Assert.That(AnimationUtility.GetEditorCurve(clip, ParameterBinding("Input")), Is.Not.Null);
            Assert.That(ParameterSpecialization.Apply(new[] { controller }, constants).ParameterCurves, Is.Zero);
            output.SetFloatCurve(ParameterBinding(constants["Input"].InternalName), AnimationCurve.Constant(0, 1, 1));
            Assert.Throws<InvalidOperationException>(() => ParameterSpecialization.Apply(new[] { controller }, constants));
        }

        [Test]
        public void FreezeRejectsUnknownBehavioursAndAudioParameterConsumers()
        {
            var source = Controller(Clip()); source.AddParameter("Input", AnimatorControllerParameterType.Int);
            var state = source.layers[0].stateMachine.defaultState;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var virtualState = controller.Layers.First().StateMachine.DefaultState;
            virtualState.Behaviours = virtualState.Behaviours.Add(Track(ScriptableObject.CreateInstance<UnsupportedReachabilityBehaviour>()));
            Assert.Throws<InvalidOperationException>(() => ParameterSpecialization.Apply(new[] { controller }, Constants(controller, "Input", 0)));
            state.behaviours = Array.Empty<StateMachineBehaviour>();
            var audio = Track(state.AddStateMachineBehaviour<VRCAnimatorPlayAudio>()); audio.ParameterName = "Input";
            controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            Assert.Throws<InvalidOperationException>(() => ParameterSpecialization.Apply(new[] { controller }, Constants(controller, "Input", 0)));
        }

        [TestCase(false)] [TestCase(true)]
        public void FreezeKeepsPartialTwoDimensionalTreeEquivalentWithChangingPublicInputs(bool optimize)
        {
            Mesh("a");
            var source = Controller(Clip()); source.AddParameter("P", AnimatorControllerParameterType.Float); source.AddParameter("Q", AnimatorControllerParameterType.Float);
            var tree = Track(new BlendTree { blendType = BlendTreeType.FreeformCartesian2D, blendParameter = "P", blendParameterY = "Q" });
            tree.children = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one }.Select(p => new ChildMotion
            { motion = StaticCurve(Binding("a"), p.x * 100 + p.y * 200), position = p, timeScale = 1 }).ToArray();
            source.layers[0].stateMachine.defaultState.motion = tree;
            var expected = SampleFrozen(source, (p, i) => { p.SetFloat("P", 0.3f); p.SetFloat("Q", i / 8f); }, 8);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            ParameterSpecialization.Apply(new[] { controller }, Constants(controller, "P", 0.3f));
            if (optimize) AnimatorOptimizer.Optimize(root, new[] { controller }, new() { ConstantTrees = true, Unreachable = true }, preservedParameters: new[] { "Q" });
            var output = CommitAnimator(controller, root);
            var actual = SampleFrozen(output, (p, i) => { p.SetFloat("P", i % 2); p.SetFloat("Q", i / 8f); }, 8);
            Assert.That(actual, Is.EqualTo(expected).Within(0.001f));
            Assert.That(actual.Last() - actual.First(), Is.GreaterThan(10), "Unfrozen input must remain dynamic");
        }

        [Test]
        public void FreezeKeepsDirectTreeNormalizationAndUnfrozenSiblingDynamic()
        {
            Mesh("a");
            var source = TreeController(("P", StaticCurve(Binding("a"), 100)), ("Q", StaticCurve(Binding("a"), 200)));
            var tree = (BlendTree)source.layers[0].stateMachine.defaultState.motion;
            using (var serialized = new SerializedObject(tree)) { serialized.FindProperty("m_NormalizedBlendValues").boolValue = true; serialized.ApplyModifiedPropertiesWithoutUndo(); }
            var expected = SampleFrozen(source, (p, i) => { p.SetFloat("P", 0.4f); p.SetFloat("Q", 0.1f + i * 0.2f); });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            ParameterSpecialization.Apply(new[] { controller }, Constants(controller, "P", 0.4f));
            var actual = SampleFrozen(CommitAnimator(controller, root), (p, i) => { p.SetFloat("P", 10); p.SetFloat("Q", 0.1f + i * 0.2f); });
            Assert.That(actual, Is.EqualTo(expected).Within(0.001f));
        }

        [Test]
        public void FreezeKeepsMotionTimeStoppedEvenWhenPublicInputChanges()
        {
            Mesh("a");
            var clip = Clip(); AnimationUtility.SetEditorCurve(clip, Binding("a"), AnimationCurve.Linear(0, 0, 1, 100));
            var source = Controller(clip); source.AddParameter("Input", AnimatorControllerParameterType.Float);
            var state = source.layers[0].stateMachine.defaultState; state.timeParameterActive = true; state.timeParameter = "Input";
            var expected = SampleFrozen(source, (p, i) => p.SetFloat("Input", 0.35f));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            ParameterSpecialization.Apply(new[] { controller }, Constants(controller, "Input", 0.35f));
            var actual = SampleFrozen(CommitAnimator(controller, root), (p, i) => p.SetFloat("Input", i % 2));
            Assert.That(actual, Is.EqualTo(expected).Within(0.001f));
            Assert.That(actual.Last(), Is.EqualTo(35).Within(0.001f));
        }

        [TestCase(false)] [TestCase(true)]
        public void FreezePreservesDefaultPoseAndExitTimeInsteadOfJumpingToFinalState(bool writeDefaults)
        {
            Mesh("a");
            var idle = Clip(); AnimationUtility.SetEditorCurve(idle, Binding("a"), AnimationCurve.Constant(0, 1, 0));
            var on = Clip(); AnimationUtility.SetEditorCurve(on, Binding("a"), AnimationCurve.Constant(0, 1, 100));
            var source = Controller(idle, on); source.AddParameter("Input", AnimatorControllerParameterType.Bool);
            var states = source.layers[0].stateMachine.states.Select(c => c.state).ToArray();
            foreach (var state in states) state.writeDefaultValues = writeDefaults;
            var transition = Edge(states[0], states[1], "Input", 0, AnimatorConditionMode.If);
            transition.hasExitTime = true; transition.exitTime = 0.5f; transition.duration = 0;
            var expected = SampleFrozen(source, (p, i) => p.SetBool("Input", true), 65);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            ParameterSpecialization.Apply(new[] { controller }, Constants(controller, "Input", 1));
            AnimatorOptimizer.Optimize(root, new[] { controller }, new() { Unreachable = true });
            var actual = SampleFrozen(CommitAnimator(controller, root), (p, i) => p.SetBool("Input", false), 65);
            Assert.That(actual, Is.EqualTo(expected).Within(0.001f));
            Assert.That(actual[10], Is.Zero); Assert.That(actual[60], Is.EqualTo(100));
        }

        [TestCase(0)] [TestCase(1)]
        public void FreezeKeepsGameObjectAndRendererActivationAtSelectedValue(int value)
        {
            Mesh("a");
            var clothing = PolicyChild("Clothing"); var meshRenderer = clothing.AddComponent<MeshRenderer>();
            var off = Clip(); var on = Clip();
            foreach (var pair in new[] { (clip: off, value: 0f), (clip: on, value: 1f) })
            {
                AnimationUtility.SetEditorCurve(pair.clip, EditorCurveBinding.FloatCurve("Clothing", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, pair.value));
                AnimationUtility.SetEditorCurve(pair.clip, EditorCurveBinding.FloatCurve("Clothing", typeof(MeshRenderer), "m_Enabled"), AnimationCurve.Constant(0, 1, pair.value));
            }
            var source = Controller(off, on); source.AddParameter("Input", AnimatorControllerParameterType.Bool);
            var states = source.layers[0].stateMachine.states.Select(s => s.state).ToArray();
            Edge(states[0], states[1], "Input", 0, AnimatorConditionMode.If).duration = 0;
            Edge(states[1], states[0], "Input", 0, AnimatorConditionMode.IfNot).duration = 0;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            ParameterSpecialization.Apply(new[] { controller }, Constants(controller, "Input", value));
            AnimatorOptimizer.Optimize(root, new[] { controller }, new() { Unreachable = true });
            var result = SampleFrozen(CommitAnimator(controller, root), (p, i) => p.SetBool("Input", i % 2 == 0), 6,
                () => (clothing.activeSelf ? 1 : 0) + (meshRenderer.enabled ? 2 : 0));
            Assert.That(result.Skip(2), Is.All.EqualTo(value * 3));
        }

        [Test]
        public void FreezeScopeFindsIndirectDriverAndCurveConsumersOutsideSubtree()
        {
            var container = PolicyChild("Scope"); var inside = PolicyChild("Inside", container.transform).AddComponent<SkinnedMeshRenderer>();
            var outside = PolicyChild("Outside").AddComponent<SkinnedMeshRenderer>();
            var policy = Policy(container); var scope = OptimizationBuildScope.Capture(policy, root);
            var writer = TreeController(("Input", StaticCurve(ParameterBinding("Computed"), 1))); writer.AddParameter("Computed", AnimatorControllerParameterType.Float);
            var consumer = TreeController(("Computed", StaticCurve(Binding("a", "Scope/Inside"), 100)));
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var a = clone.Clone(writer); var b = clone.Clone(consumer);
            var entries = new Dictionary<object, VirtualAnimatorController> { ["a"] = a, ["b"] = b };
            Assert.DoesNotThrow(() => ParameterFreezeScope.Validate(root, scope, entries, new[] { "Input" }));
            var output = b.AllReachableNodes().OfType<VirtualClip>().Single();
            output.SetFloatCurve(Binding("a", "Outside"), AnimationCurve.Constant(0, 1, 0));
            Assert.Throws<InvalidOperationException>(() => ParameterFreezeScope.Validate(root, scope, entries, new[] { "Input" }));
        }

        [Test]
        public void FreezeCaptureUsesSourceRemappingAndRejectsConflictingRules()
        {
            var policy = Policy();
            var owner = PolicyChild("Generator").AddComponent<ParameterPolicySourceFixture>();
            owner.From = "Local"; owner.To = "Global"; owner.Controller = Controller(Clip());
            ((AnimatorController)owner.Controller).AddParameter("Local", AnimatorControllerParameterType.Float);
            policy.FreezeParameters.Add(new AnimatorParameterConstant { Source = owner.gameObject, Name = "Local", Value = 0.2f });
            var context = new BuildContext(root, null); ParameterPolicyPass.Capture(context);
            Assert.That(context.GetState<ParameterPolicyState>().Frozen["Global"], Is.EqualTo(0.2f));
            policy.FreezeParameters.Add(new AnimatorParameterConstant { Name = "Global", Value = 0.8f });
            Assert.Throws<InvalidOperationException>(() => ParameterPolicyPass.Capture(new BuildContext(root, null)));
        }

        [Test]
        public void FreezeAvoidsExternalNameCollisionsAndLateDefaultChangesAreRejected()
        {
            var source = Controller(Clip(), Clip()); source.AddParameter("Input", AnimatorControllerParameterType.Float);
            Edge(source.layers[0].stateMachine.states[0].state, source.layers[0].stateMachine.states[1].state, "Input", 0.5f, AnimatorConditionMode.Greater);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var constants = ParameterSpecialization.Prepare(new[] { controller }, new Dictionary<string, float> { ["Input"] = 0.25f },
                new HashSet<string>(), new[] { "__AAO_Constant_0" });
            Assert.That(constants["Input"].InternalName, Is.Not.EqualTo("__AAO_Constant_0"));
            ParameterSpecialization.Apply(new[] { controller }, constants);
            var changed = constants["Input"].Declaration(); changed.defaultFloat = 1;
            controller.Parameters = controller.Parameters.SetItem(changed.name, changed);
            Assert.Throws<InvalidOperationException>(() => ParameterSpecialization.Apply(new[] { controller }, constants));
        }

        [Test]
        public void FreezeRejectsSynchronizedLayersAndLateDriverWrites()
        {
            var source = Controller(Clip()); source.AddParameter("Input", AnimatorControllerParameterType.Float);
            Driver(source.layers[0].stateMachine.defaultState, "Input", 1);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var constants = Constants(controller, "Input", 0);
            var layer = controller.Layers.First(); layer.SyncedLayerIndex = 0;
            Assert.Throws<InvalidOperationException>(() => ParameterSpecialization.Apply(new[] { controller }, constants));
            layer.SyncedLayerIndex = -1;
            ParameterSpecialization.Apply(new[] { controller }, constants);
            var driver = ParameterSpecialization.Behaviours(controller.AllReachableNodes()).OfType<VRC_AvatarParameterDriver>().Single();
            driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter { name = constants["Input"].InternalName, value = 1 });
            Assert.Throws<InvalidOperationException>(() => ParameterSpecialization.Apply(new[] { controller }, constants));
        }

        [Test]
        public void PreserveDependencyClosureIncludesDriverOutputsWithoutControllerDeclarations()
        {
            var source = Controller(Clip()); source.AddParameter("Input", AnimatorControllerParameterType.Float);
            Driver(source.layers[0].stateMachine.defaultState, "ExpressionOnly", 0, VRC_AvatarParameterDriver.ChangeType.Copy, "Input");
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var graph = AnimatorParameterDependencies.Collect(root, new[] { controller });
            Assert.That(graph.Expand(new[] { "ExpressionOnly" }), Does.Contain("Input"));
            Assert.That(graph.AffectedBy(new[] { "Input" }), Does.Contain("ExpressionOnly"));
        }
    }
}
