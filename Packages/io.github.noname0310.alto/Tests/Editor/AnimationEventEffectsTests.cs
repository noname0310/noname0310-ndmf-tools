using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ParameterPolicy;
using Noname.AvatarTools.TestFixtures;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(null, false)] [TestCase("", false)] [TestCase(" ", true)]
        [TestCase("\t\r\n", true)] [TestCase("MissingReceiver", true)]
        [TestCase(nameof(AnimationEventReceiverFixture.ObserveAnimationEvent), true)]
        public void AnimationEventEffects_OnlyExactlyEmptyNamesHaveNoCallback(string name, bool callback)
        {
            foreach (var options in new[] { SendMessageOptions.RequireReceiver, SendMessageOptions.DontRequireReceiver })
                Assert.That(AnimationEventEffects.MayInvokeCallback(new AnimationEvent {
                    functionName = name, messageOptions = options
                }), Is.EqualTo(callback));
            Assert.That(AnimationEventEffects.MayInvokeCallback((AnimationEvent)null), Is.True);
            Assert.That(AnimationEventEffects.MayInvokeCallback((AnimationClip)null), Is.True);
        }

        [Test]
        public void AnimationEventEffects_MixedEventListRetainsTheNamedCallback()
        {
            var clip = Clip();
            AnimationUtility.SetAnimationEvents(clip, new[] {
                new AnimationEvent { functionName = "" },
                new AnimationEvent { functionName = "Observe", messageOptions = SendMessageOptions.DontRequireReceiver }
            });
            string before = EditorJsonUtility.ToJson(clip);
            Assert.That(AnimationEventEffects.MayInvokeCallback(clip), Is.True);
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(before));
        }

        [Test]
        public void AnimationEventEffects_NativeDispatchUsesExactFunctionNames(
            [Values("Empty", "Null", "Whitespace", "Missing", "Receiver", "Mixed")] string kind,
            [Values(false, true)] bool requireReceiver, [Values(false, true)] bool fireEvents)
        {
            Mesh("EventValue");
            const string fixtureName = "ALTO AnimationEvent dispatch fixture";
            root.name = fixtureName;
            var receiver = root.AddComponent<AnimationEventReceiverFixture>();
            var payload = PolicyChild("Event payload");
            var clip = Track(new AnimationClip { name = fixtureName });
            AnimationUtility.SetEditorCurve(clip, Binding("EventValue"), AnimationCurve.Linear(0, 10, .3f, 30));
            var events = new List<AnimationEvent>();
            string functionName = kind == "Null" ? null : kind == "Whitespace" ? " " :
                kind == "Missing" ? "MissingAnimationEventReceiver" :
                kind == "Receiver" ? nameof(AnimationEventReceiverFixture.ObserveAnimationEvent) : "";
            AnimationEvent Event(string name, float time) => new() {
                functionName = name, time = time, floatParameter = 1.25f, intParameter = 17,
                stringParameter = "event payload", objectReferenceParameter = payload,
                messageOptions = requireReceiver ? SendMessageOptions.RequireReceiver : SendMessageOptions.DontRequireReceiver
            };
            events.Add(Event(functionName, .05f));
            if (kind == "Mixed") events.Add(Event(nameof(AnimationEventReceiverFixture.ObserveAnimationEvent), .09f));
            AnimationUtility.SetAnimationEvents(clip, events.ToArray());
            string before = EditorJsonUtility.ToJson(clip);
            var diagnostics = new List<string>();
            void ObserveLog(string message, string stack, LogType type)
            {
                // Record only this fixture's native event diagnostics. The exact empty-name
                // diagnostic/count is evidence from the run, not part of the callback premise.
                // Unrelated errors remain unexpected and fail the native test normally.
                if (!message.Contains(fixtureName) || !message.Contains("AnimationEvent") ||
                    !(message.Contains("has no function name specified") || message.Contains("has no receiver"))) return;
                diagnostics.Add(type + ": " + message);
                if (type == LogType.Error || type == LogType.Assert || type == LogType.Exception)
                    LogAssert.Expect(type, message);
            }
            Application.logMessageReceived += ObserveLog;
            try
            {
                var samples = SampleNativeAnimationEvents(Controller(clip), fireEvents);
                Assert.That(samples.Last(), Is.GreaterThan(samples.First()), "The native clip must advance across both event times.");
            }
            finally { Application.logMessageReceived -= ObserveLog; }
            TestContext.WriteLine("Native event diagnostics: " + (diagnostics.Count == 0 ? "none" : string.Join("\n", diagnostics)));
            if (!fireEvents) Assert.That(diagnostics, Is.Empty, "Disabled event dispatch must not send callback diagnostics.");
            if (!requireReceiver && (kind == "Missing" || kind == "Whitespace"))
                Assert.That(diagnostics.Any(d => d.Contains("has no receiver")), Is.False,
                    "DontRequireReceiver suppresses missing-receiver diagnostics without making named callbacks effect-free.");

            bool dispatched = fireEvents && (kind == "Receiver" || kind == "Mixed");
            Assert.That(receiver.Observations.Count, Is.EqualTo(dispatched ? 1 : 0));
            if (dispatched)
            {
                var observed = receiver.Observations.Single();
                Assert.That(observed.FunctionName, Is.EqualTo(nameof(AnimationEventReceiverFixture.ObserveAnimationEvent)));
                Assert.That(observed.Time, Is.EqualTo(kind == "Mixed" ? .09f : .05f));
                Assert.That(observed.FloatParameter, Is.EqualTo(1.25f));
                Assert.That(observed.IntParameter, Is.EqualTo(17));
                Assert.That(observed.StringParameter, Is.EqualTo("event payload"));
                Assert.That(observed.ObjectParameter, Is.SameAs(payload));
                Assert.That(observed.FiredByAnimator, Is.True);
            }
            Assert.That(AnimationEventEffects.MayInvokeCallback(clip), Is.EqualTo(kind != "Empty" && kind != "Null"));
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(before), "Native sampling and classification must not edit the source event or curve.");
        }

        // Preview scenes suppress AnimationEvents even while Animator curves and
        // state time advance. A normal additive scene provides the measured
        // Edit Mode dispatch contract without entering project Play Mode.
        private float[] SampleNativeAnimationEvents(AnimatorController controller, bool fireEvents, Action afterSampling = null)
        {
            var previous = SceneManager.GetActiveScene();
            var originalScene = root.scene;
            var originalScenes = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt)
                .Select(s => (Scene: s, Loaded: s.isLoaded, Dirty: s.isDirty)).ToArray();
            Scene native = default;
            string temporaryFolder = null, nativeScenePath = null;
            Animator animator = null;
            var samples = new float[12];
            try
            {
                // NewScene(Additive) refuses a dirty untitled scene, including
                // the isolated test runner's normal scene. Open an owned copy
                // instead; neither the template nor any loaded scene is saved.
                string folderName = "ALTOAnimationEventTests_" + Guid.NewGuid().ToString("N");
                string folderGuid = AssetDatabase.CreateFolder("Assets", folderName);
                Assert.That(folderGuid, Is.Not.Null.And.Not.Empty);
                temporaryFolder = "Assets/" + folderName;
                Assert.That(AssetDatabase.GUIDToAssetPath(folderGuid), Is.EqualTo(temporaryFolder));
                nativeScenePath = temporaryFolder + "/NativeAnimationEvents.unity";
                Assert.That(AssetDatabase.CopyAsset("Packages/io.github.noname0310.alto/Editor/Build/AnalysisScene.unity",
                    nativeScenePath), Is.True);
                native = EditorSceneManager.OpenScene(nativeScenePath, OpenSceneMode.Additive);
                Assert.That(native.IsValid() && native.isLoaded, Is.True);
                Assert.That(EditorSceneManager.IsPreviewScene(native), Is.False);
                if (SceneManager.GetActiveScene().handle != native.handle) SceneManager.SetActiveScene(native);
                Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(native));
                SceneManager.MoveGameObjectToScene(root, native);
                renderer.SetBlendShapeWeight(0, 0);
                animator = root.AddComponent<Animator>();
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.fireEvents = fireEvents;
                animator.runtimeAnimatorController = controller;
                animator.Rebind();
                animator.Play("Base." + controller.layers[0].stateMachine.defaultState.name, 0, 0);
                animator.Update(0);
                for (int i = 0; i < samples.Length; i++)
                {
                    animator.Update(1f / 60);
                    samples[i] = renderer.GetBlendShapeWeight(0);
                }
                Assert.That(animator.isInitialized, Is.True);
                Assert.That(animator.GetCurrentAnimatorStateInfo(0).normalizedTime, Is.GreaterThan(.5f),
                    "The non-looping .3-second clip must cross the .05/.09-second event times.");
                afterSampling?.Invoke();
            }
            finally
            {
                try
                {
                    if (animator != null)
                    {
                        animator.runtimeAnimatorController = null;
                        UnityEngine.Object.DestroyImmediate(animator);
                    }
                }
                finally
                {
                    try { if (root != null) SceneManager.MoveGameObjectToScene(root, originalScene); }
                    finally
                    {
                        try
                        {
                            if (previous.IsValid() && previous.isLoaded && !EditorSceneManager.IsPreviewScene(previous) &&
                                SceneManager.GetActiveScene().handle != previous.handle) SceneManager.SetActiveScene(previous);
                        }
                        finally
                        {
                            try
                            {
                                if (!native.IsValid() && nativeScenePath != null) native = SceneManager.GetSceneByPath(nativeScenePath);
                                if (native.IsValid() && native.isLoaded)
                                    Assert.That(EditorSceneManager.CloseScene(native, true), Is.True);
                            }
                            finally
                            {
                                if (temporaryFolder != null)
                                {
                                    Assert.That(native.IsValid() && native.isLoaded, Is.False,
                                        "Do not remove an owned scene asset while its scene remains loaded.");
                                    Assert.That(AssetDatabase.DeleteAsset(temporaryFolder), Is.True);
                                    Assert.That(AssetDatabase.IsValidFolder(temporaryFolder), Is.False);
                                }
                            }
                        }
                    }
                }
                Assert.That(root.scene, Is.EqualTo(originalScene));
                Assert.That(SceneManager.GetActiveScene(), Is.EqualTo(previous));
                Assert.That(SceneManager.sceneCount, Is.EqualTo(originalScenes.Length));
                foreach (var original in originalScenes)
                {
                    Assert.That(original.Scene.isLoaded, Is.EqualTo(original.Loaded));
                    Assert.That(original.Scene.isDirty, Is.EqualTo(original.Dirty));
                }
            }
            return samples;
        }

        [Test]
        public void AnimationEventEffects_NativeDispatchRestoresScenesAfterSamplingFailure()
        {
            Mesh("EventValue");
            var receiver = root.AddComponent<AnimationEventReceiverFixture>();
            var clip = Track(new AnimationClip { name = "Native event cleanup fixture" });
            AnimationUtility.SetEditorCurve(clip, Binding("EventValue"), AnimationCurve.Linear(0, 10, .3f, 30));
            AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent {
                functionName = nameof(AnimationEventReceiverFixture.ObserveAnimationEvent), time = .05f
            } });
            string source = EditorJsonUtility.ToJson(clip);
            Assert.Throws<InvalidOperationException>(() => SampleNativeAnimationEvents(Controller(clip), true,
                () => throw new InvalidOperationException("Injected native event fixture failure")));
            Assert.That(receiver.Observations.Count, Is.EqualTo(1), "The positive native dispatch occurs before the injected failure.");
            Assert.That(receiver.Observations[0].FiredByAnimator, Is.True);
            Assert.That(root.GetComponent<Animator>(), Is.Null);
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(source));
        }

        [TestCase(null)] [TestCase("")]
        public void AnimationEventEffects_FreezingMatchesNdmfBaselineCurvesDurationAndTransitions(string functionName)
        {
            Mesh("EventValue");
            var clip = Track(new AnimationClip { name = "Event source", frameRate = 30 });
            AnimationUtility.SetEditorCurve(clip, Binding("EventValue"), AnimationCurve.Linear(0, 20, .4f, 80));
            AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent {
                time = .1f, functionName = functionName, intParameter = 37, stringParameter = "retain source metadata",
                objectReferenceParameter = PolicyChild("Source event reference")
            } });
            string before = EditorJsonUtility.ToJson(clip);
            var source = TreeController(("Input", clip));
            var parameters = source.parameters; parameters.Single(p => p.name == "Input").defaultFloat = .35f;
            source.parameters = parameters;
            var machine = source.layers[0].stateMachine;
            var first = machine.defaultState; first.name = "Before exit time";
            var secondClip = StaticCurve(Binding("EventValue"), 90); secondClip.name = "After transition";
            var second = Track(machine.AddState("After exit time")); second.motion = secondClip; second.writeDefaultValues = true;
            var transition = Track(first.AddTransition(second));
            transition.hasExitTime = true; transition.exitTime = .5f;
            transition.hasFixedDuration = true; transition.duration = 0;
            transition.AddCondition(AnimatorConditionMode.Less, .5f, "Input");

            // NDMF already reconstructs event-bearing clips without events. Compare with that
            // ordinary build baseline, while separately proving the source asset was untouched.
            var baseline = CommitAnimator(new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source), root);
            var virtualController = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var constants = Constants(virtualController, "Input", .35f);
            var report = ParameterSpecialization.Apply(new[] { virtualController }, constants);
            Assert.That(report.Reads, Is.GreaterThanOrEqualTo(2), "Both Direct weight and transition condition must be specialized.");
            var specialized = CommitAnimator(virtualController, root);
            var baselineStates = new List<int>(); var specializedStates = new List<int>();
            AnimatorControllerPlayable current = default;
            var expected = SampleFrozen(baseline, (playable, _) => { current = playable; playable.SetFloat("Input", .35f); }, 48,
                () => { baselineStates.Add(current.GetCurrentAnimatorStateInfo(0).fullPathHash); return renderer.GetBlendShapeWeight(0); });
            var actual = SampleFrozen(specialized, (playable, frame) => { current = playable; playable.SetFloat("Input", frame % 2); }, 48,
                () => { specializedStates.Add(current.GetCurrentAnimatorStateInfo(0).fullPathHash); return renderer.GetBlendShapeWeight(0); });
            Assert.That(baselineStates.Distinct().Count(), Is.EqualTo(2), "The native control must actually take the exit-time transition.");
            Assert.That(specializedStates, Is.EqualTo(baselineStates));
            Assert.That(actual, Is.EqualTo(expected));
            var baselineClips = baseline.animationClips.OrderBy(c => c.name).ToArray();
            var specializedClips = specialized.animationClips.OrderBy(c => c.name).ToArray();
            Assert.That(specializedClips.Length, Is.EqualTo(baselineClips.Length));
            for (int i = 0; i < baselineClips.Length; i++)
            {
                Assert.That(specializedClips[i].length, Is.EqualTo(baselineClips[i].length));
                Assert.That(specializedClips[i].frameRate, Is.EqualTo(baselineClips[i].frameRate));
                Assert.That(AnimationUtility.GetAnimationClipSettings(specializedClips[i]).stopTime,
                    Is.EqualTo(AnimationUtility.GetAnimationClipSettings(baselineClips[i]).stopTime));
                var bindings = AnimationUtility.GetCurveBindings(baselineClips[i]);
                Assert.That(AnimationUtility.GetCurveBindings(specializedClips[i]), Is.EquivalentTo(bindings));
                foreach (var binding in bindings)
                    Assert.That(AnimationUtility.GetEditorCurve(specializedClips[i], binding).keys,
                        Is.EqualTo(AnimationUtility.GetEditorCurve(baselineClips[i], binding).keys));
            }
            Assert.That(baselineClips.Sum(c => c.events.Length), Is.Zero, "This fixture records the installed NDMF baseline.");
            Assert.That(specializedClips.Sum(c => c.events.Length), Is.Zero);
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(before));
            Assert.That(transition.conditions.Single().parameter, Is.EqualTo("Input"));
            Assert.That(((BlendTree)first.motion).children.Single().directBlendParameter, Is.EqualTo("Input"));
        }

        [TestCase(" ")] [TestCase("MissingReceiver")] [TestCase(nameof(AnimationEventReceiverFixture.ObserveAnimationEvent))]
        public void AnimationEventEffects_FreezingRetainsEveryNonemptyNameBeforeMutation(string functionName)
        {
            var clip = StaticCurve(Binding("EventValue"), 10);
            AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent {
                functionName = functionName, messageOptions = SendMessageOptions.DontRequireReceiver
            } });
            var source = TreeController(("Input", clip));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var constants = Constants(controller, "Input", .35f);
            string before = EditorJsonUtility.ToJson(clip);
            Assert.Throws<InvalidOperationException>(() => ParameterSpecialization.Apply(new[] { controller }, constants));
            Assert.That(controller.Parameters.Keys, Does.Not.Contain(constants["Input"].InternalName));
            Assert.That(controller.AllReachableNodes().OfType<VirtualBlendTree>().Single().Children.Single().DirectBlendParameter,
                Is.EqualTo("Input"));
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(before));
        }

        [TestCase(null, false)] [TestCase("", false)] [TestCase(" ", true)] [TestCase("Observe", true)]
        public void AnimationEventEffects_ScopedFreezeStillChecksTargetsAndCallableEffects(string functionName, bool callback)
        {
            var container = PolicyChild("Scope");
            PolicyChild("Inside", container.transform).AddComponent<SkinnedMeshRenderer>();
            var scope = OptimizationBuildScope.Capture(Policy(container), root);
            var clip = StaticCurve(Binding("EventValue", "Scope/Inside"), 10);
            AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { functionName = functionName } });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(TreeController(("Input", clip)));
            var entries = new Dictionary<object, VirtualAnimatorController> { ["FX"] = controller };
            if (callback) Assert.Throws<InvalidOperationException>(() => ParameterFreezeScope.Validate(root, scope, entries, new[] { "Input" }));
            else
            {
                Assert.DoesNotThrow(() => ParameterFreezeScope.Validate(root, scope, entries, new[] { "Input" }));
                PolicyChild("Outside").AddComponent<SkinnedMeshRenderer>();
                controller.AllReachableNodes().OfType<VirtualClip>().Single().SetFloatCurve(Binding("EventValue", "Outside"),
                    AnimationCurve.Constant(0, 1, 20));
                Assert.Throws<InvalidOperationException>(() => ParameterFreezeScope.Validate(root, scope, entries, new[] { "Input" }));
            }
        }

        [TestCase(null, false)] [TestCase("", false)] [TestCase("Observe", true)]
        public void AnimationEventEffects_ParameterDependenciesDistinguishCallbackEffects(string functionName, bool callback)
        {
            var clip = StaticCurve(ParameterBinding("Dead"), 1);
            AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { functionName = functionName } });
            var source = TreeController(("Input", clip)); source.AddParameter("Dead", AnimatorControllerParameterType.Float);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var graph = AnimatorParameterDependencies.Collect(root, new[] { controller });
            Assert.That(graph.ExpandObservable().Contains("Input"), Is.EqualTo(callback));
        }

        [TestCase(null)] [TestCase("")]
        public void AnimationEventEffects_EmptyEventBeyondLastKeyRetainsStructuralTimingGuards(string functionName)
        {
            Mesh("a");
            var fixture = RepeatedOneDimensionalClip(duration: .1f);
            AnimationUtility.SetAnimationEvents(fixture.clip, new[] { new AnimationEvent {
                functionName = functionName, time = 1, objectReferenceParameter = PolicyChild("Timing event reference")
            } });
            string before = EditorJsonUtility.ToJson(fixture.clip);
            Assert.That(fixture.clip.events.Single().time,
                Is.GreaterThan(AnimationUtility.GetEditorCurve(fixture.clip, Binding("a")).keys.Last().time));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(fixture.controller);
            var clip = controller.AllReachableNodes().OfType<VirtualClip>()
                .Single(c => MotionAnalysis.Source(c) == fixture.clip);
            Assert.That(AnimationEventEffects.MayInvokeCallback(fixture.clip), Is.False);
            Assert.That(MotionAnalysis.ConstantPoseClip(clip), Is.False);
            Assert.That(new MotionTimeAnalysis(new[] { controller }).CanChangeDuration(clip), Is.False);
            var report = AnimatorOptimizer.Optimize(root, new[] { controller }, new() { ConstantTrees = true, MergeClips = true },
                preservedParameters: new[] { "Selection", "Weight", "Other" });
            Assert.That(report.Trees, Is.Zero);
            Assert.That(report.MergedClips, Is.Zero);
            Assert.That(controller.AllReachableNodes().OfType<VirtualBlendTree>().Any(t => t.Name == fixture.tree.name), Is.True);
            Assert.That(EditorJsonUtility.ToJson(fixture.clip), Is.EqualTo(before));
        }
    }
}
