using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("Mutation")] [TestCase("Exception")] [TestCase("DeclinedDeletion")]
        public void AnalysisSnapshotReuse_ReaderClosureExpiresAtEveryMutationBoundary(string boundary)
        {
            var settings = ObjectSettings();
            var first = PolicyChild("First pose").transform;
            var second = PolicyChild("Second pose").transform;
            var bone = PolicyChild("Simulation").AddComponent<VRCPhysBone>();
            bone.rootTransform = first;
            var snapshot = new ObjectAnalysisSnapshot(root, new Dictionary<object, VirtualAnimatorController>(), null,
                settings, Array.Empty<string>(), new ShaderPropertyUsage());
            // Populate the shared serialized-reference records before the reader query.
            snapshot.Dependencies(false);
            var readers = snapshot.InitialReaders;
            Assert.That(readers.Frames, Does.Contain(first));
            Assert.That(readers.Frames.Contains(second), Is.False);
            snapshot.Rewrite(() => 0);
            Assert.That(snapshot.InitialReaders, Is.SameAs(readers));

            if (boundary == "Exception")
                Assert.Throws<InvalidOperationException>(() => snapshot.Rewrite(() =>
                {
                    bone.rootTransform = second;
                    throw new InvalidOperationException("Changed a native reference before failing");
                }));
            else if (boundary == "DeclinedDeletion")
            {
                settings.DestroyObject = _ => bone.rootTransform = second;
                Assert.That(snapshot.Rewrite(() => settings.TryDestroy(first.gameObject) ? 1 : 0), Is.Zero);
            }
            else snapshot.Rewrite(() => { bone.rootTransform = second; return 1; });

            var after = snapshot.InitialReaders;
            Assert.That(after, Is.Not.SameAs(readers));
            Assert.That(after.Frames, Does.Contain(second));
            Assert.That(after.Frames.Contains(first), Is.False);
            Assert.That(after.Frames, Is.EquivalentTo(InitialPoseReaders.Analyze(root, animations: snapshot.Index).Frames));
        }

        [Test]
        public void AnalysisSnapshotReuse_CullingClosureTracksAnimatorAndBoneChanges()
        {
            var settings = ObjectSettings();
            var animator = root.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var first = PolicyChild("First bone").transform;
            var second = PolicyChild("Second bone").transform;
            renderer.bones = new[] { first };
            var snapshot = new ObjectAnalysisSnapshot(root, new Dictionary<object, VirtualAnimatorController>(), null,
                settings, Array.Empty<string>(), new ShaderPropertyUsage());
            Assert.That(snapshot.InitialReaders.CullingComponents.Contains(renderer), Is.False);
            snapshot.Rewrite(() => { animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms; return 1; });
            Assert.That(snapshot.InitialReaders.CullingComponents, Does.Contain(renderer));
            Assert.That(snapshot.InitialReaders.CullingFrames, Does.Contain(first));
            snapshot.Rewrite(() => { renderer.bones = new[] { second }; return 1; });
            Assert.That(snapshot.InitialReaders.CullingFrames, Does.Contain(second));
            Assert.That(snapshot.InitialReaders.CullingFrames.Contains(first), Is.False);
        }

        [Test]
        public void AnalysisSnapshotReuse_ExistingReaderRecordsDoNotReplaceFreshNativeWriterReferences()
        {
            var first = PolicyChild("Old simulation frame").transform;
            var second = PolicyChild("New simulation frame").transform;
            var particles = PolicyChild("Particles").AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particles.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Custom;
            main.customSimulationSpace = first;
            var records = new SerializedReferenceSnapshot();
            Assert.That(records.Get(particles).Any(p => p.Value == first), Is.True);
            main.customSimulationSpace = second;

            // Static-frame callers can retain reader records across their own
            // batch. Their native-writer subscan has always required fresh refs.
            var readers = InitialPoseReaders.Analyze(root, records);
            Assert.That(readers.Frames, Does.Contain(second));
            var fresh = InitialPoseReaders.Analyze(root);
            Assert.That(fresh.Frames, Does.Contain(second));
            Assert.That(fresh.Frames.Contains(first), Is.False);
        }

        [Test]
        public void AnalysisSnapshotReuse_AnimatedReferenceChangesInvalidateTheReaderClosure()
        {
            var settings = ObjectSettings();
            var first = PolicyChild("First animated frame").transform;
            var second = PolicyChild("Second animated frame").transform;
            var firstChild = PolicyChild("First descendant", first).transform;
            var secondChild = PolicyChild("Second descendant", second).transform;
            var particles = PolicyChild("Particles").AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var binding = EditorCurveBinding.PPtrCurve(particles.name, typeof(ParticleSystem), "InitialModule.customSimulationSpace");
            var clip = Clip();
            AnimationUtility.SetObjectReferenceCurve(clip, binding,
                new[] { new ObjectReferenceKeyframe { time = 0, value = first } });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var snapshot = new ObjectAnalysisSnapshot(root, entries, null, settings, Array.Empty<string>(), new ShaderPropertyUsage());
            Assert.That(snapshot.InitialReaders.Frames, Does.Contain(firstChild));
            Assert.That(snapshot.InitialReaders.Frames.Contains(secondChild), Is.False);
            snapshot.Rewrite(() =>
            {
                controller.AllReachableNodes().OfType<VirtualClip>().Single().SetObjectCurve(binding,
                    new[] { new ObjectReferenceKeyframe { time = 0, value = second } });
                return 1;
            });
            Assert.That(snapshot.InitialReaders.Frames, Does.Contain(secondChild));
            Assert.That(snapshot.InitialReaders.Frames.Contains(firstChild), Is.False);
        }

        [Test]
        public void AnalysisSnapshotReuse_BindingTargetsAreResolvedAgainAfterComponentReplacement()
        {
            var original = root.AddComponent<AudioSource>(); original.volume = .5f;
            var binding = EditorCurveBinding.FloatCurve("", typeof(AudioSource), "m_Volume");
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(StaticCurve(binding, .5f)));
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var index = AvatarAnimationIndex.Create(root, entries, null);
            var writers = index.ForObject(original).ToArray();
            var proof = new InvariantComponentSettings(index, index.Reachability, entries);
            Assert.That(proof.IsConstantProperty(original, "m_Volume"), Is.True);
            var second = root.AddComponent<AudioSource>(); second.volume = .5f;
            Assert.That(proof.IsConstantProperty(original, "m_Volume"), Is.False, "The binding now targets two components");
            UnityEngine.Object.DestroyImmediate(original);
            Assert.That(index.ConstantOutputs.MatchesInitialValue(second, writers, .5f, false), Is.True,
                "Current component resolution must not be cached with the old BindingUse");
            Assert.That(writers.Single().Objects().Single(), Is.SameAs(second));
        }

        [Test]
        public void AnalysisSnapshotReuse_InvariantSharingKeepsTheReachabilityDomain()
        {
            var light = root.AddComponent<Light>(); light.intensity = 0;
            var binding = EditorCurveBinding.FloatCurve("", typeof(Light), "m_Intensity");
            var source = FixedOneDimensionalController(0,
                new[] { StaticCurve(binding, 0), StaticCurve(binding, 1) }, new[] { 0f, 1f });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var index = AvatarAnimationIndex.Create(root, entries, null);
            var exact = index.ConstantOutputs;
            var fixedProof = new InvariantComponentSettings(index, index.Reachability, entries, sharedConstants: exact);
            Assert.That(fixedProof.IsConstantProperty(light, "m_Intensity"), Is.True);
            var external = AnimatorReachability.Analyze(root, new[] { controller }, new[] { "Selection" });
            Assert.That(external.Values("Selection").TryGetConstant(out _), Is.False);
            var dynamicProof = new InvariantComponentSettings(index, external, entries, sharedConstants: exact);
            Assert.That(dynamicProof.IsConstantProperty(light, "m_Intensity"), Is.False,
                "An optional shared index cannot replace the requested parameter domain");
        }

        [TestCase(false)] [TestCase(true)]
        public void AnalysisSnapshotReuse_OrdinaryInvariantQueriesRefreshFactsAfterCallbacks(bool transitionChanged)
        {
            var light = root.AddComponent<Light>(); light.intensity = .5f;
            var binding = EditorCurveBinding.FloatCurve("", typeof(Light), "m_Intensity");
            var source = CounterexampleTwoDimensionalController(BlendTreeType.FreeformCartesian2D,
                Enumerable.Range(0, 5).Select(_ => StaticCurve(binding, .5f)).ToArray());
            DeclareFiniteBlendInputs(source);
            var sourceState = source.layers[0].stateMachine.defaultState;
            var edge = Track(sourceState.AddTransition(sourceState)); edge.duration = 0; edge.hasExitTime = false;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var state = controller.Layers.Single().StateMachine.DefaultState;
            var control = Track(ScriptableObject.CreateInstance<VRCPlayableLayerControl>());
            control.layer = VRC_PlayableLayerControl.BlendableLayer.FX; control.goalWeight = 1;
            state.Behaviours = state.Behaviours.Add(control);
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var index = AvatarAnimationIndex.Create(root, entries, null);
            var invariant = new InvariantComponentSettings(index, index.Reachability, entries,
                numericalFeature: NumericalOptimization.Constraints, permitsRoundoff: (_, _) => true);
            Assert.That(invariant.IsConstantProperty(light, "m_Intensity"), Is.True);
            // A caller's deletion callback can modify already-reachable behaviours
            // before a later candidate is checked against the same invariant object.
            if (transitionChanged) state.Transitions.Single().Duration = float.NaN;
            else control.goalWeight = 0;
            Assert.That(invariant.IsConstantProperty(light, "m_Intensity"), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void AnalysisSnapshotReuse_WeightInventoryPreservesOwnerAndControllerIndices(bool crossPlayable)
        {
            var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            clone.Clone(Controller(Clip())); // Keep virtual indices distinct from physical indices.
            var fx = clone.Clone(Controller(Clip()));
            var writer = clone.Clone(Controller(Clip())).Layers.Single();
            fx.AddLayer(new LayerPriority(0), writer);
            var action = clone.Clone(Controller(Clip()));
            action.AddLayer(new LayerPriority(-100), writer);
            Assert.That(writer.VirtualLayerIndex, Is.Not.EqualTo(1));
            var control = Track(ScriptableObject.CreateInstance<VRCAnimatorLayerControl>());
            control.playable = VRC_AnimatorLayerControl.BlendableLayer.FX; control.goalWeight = 0;
            control.layer = crossPlayable ? 1 : writer.VirtualLayerIndex;
            var owner = (crossPlayable ? action : fx).Layers.Last().StateMachine.DefaultState;
            owner.Behaviours = owner.Behaviours.Add(control);
            var entries = new Dictionary<object, VirtualAnimatorController> {
                [VRCAvatarDescriptor.AnimLayerType.FX] = fx, [VRCAvatarDescriptor.AnimLayerType.Action] = action
            };
            var analysis = AnimatorReachability.Analyze(root, entries.Values);
            var proof = new FxPropertyOutput(entries, analysis);
            Assert.That(proof.WeightCanChange(fx, writer), Is.True);
            Assert.That(proof.WeightCanChange(action, writer), Is.False,
                "A shared VirtualLayer has different physical ownership in the other controller");
            Assert.That(proof.WeightCanChange(fx, fx.Layers.First()), Is.False);
        }

        [TestCase("Reachable", true)] [TestCase("Unreachable", false)]
        [TestCase("Machine", true)] [TestCase("UnitWeight", false)]
        [TestCase("NonfiniteDuration", true)] [TestCase("OtherPlayable", false)]
        public void AnalysisSnapshotReuse_WeightInventoryRetainsAllReachableCallbackRules(string mode, bool expected)
        {
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip(), Clip()));
            var layer = controller.Layers.Single();
            var control = Track(ScriptableObject.CreateInstance<VRCPlayableLayerControl>());
            control.layer = mode == "OtherPlayable" ? VRC_PlayableLayerControl.BlendableLayer.Action : VRC_PlayableLayerControl.BlendableLayer.FX;
            control.goalWeight = mode == "UnitWeight" || mode == "NonfiniteDuration" ? 1 : 0;
            control.blendDuration = mode == "NonfiniteDuration" ? float.NaN : 0;
            if (mode == "Machine") layer.StateMachine.Behaviours = layer.StateMachine.Behaviours.Add(control);
            else
            {
                var owner = mode == "Unreachable" ? layer.AllReachableNodes().OfType<VirtualState>()
                    .Single(s => s != layer.StateMachine.DefaultState) : layer.StateMachine.DefaultState;
                owner.Behaviours = owner.Behaviours.Add(control);
            }
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var proof = new FxPropertyOutput(entries, AnimatorReachability.Analyze(root, entries.Values));
            Assert.That(proof.WeightCanChange(controller, layer), Is.EqualTo(expected));
            Assert.That(proof.WeightCanChange(controller, layer), Is.EqualTo(expected));
        }

        [Test]
        public void AnalysisSnapshotReuse_StateAndWeightFactsExpireWithTheAnimationSnapshot()
        {
            Mesh("Shape");
            var settings = ObjectSettings();
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(StaticCurve(Binding("Shape"), 70)));
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var snapshot = new ObjectAnalysisSnapshot(root, entries, null, settings, Array.Empty<string>(), new ShaderPropertyUsage());
            Assert.That(snapshot.Index.ConstantOutputs.TryProve(renderer, "blendShape.Shape", ConstantOutputValue.Scalar(0), out _, out _), Is.True);
            snapshot.Rewrite(() =>
            {
                var control = Track(ScriptableObject.CreateInstance<VRCPlayableLayerControl>());
                control.layer = VRC_PlayableLayerControl.BlendableLayer.FX; control.goalWeight = 0;
                var state = controller.Layers.Single().StateMachine.DefaultState;
                state.Behaviours = state.Behaviours.Add(control);
                return 1;
            });
            Assert.That(snapshot.Index.ConstantOutputs.TryProve(renderer, "blendShape.Shape", ConstantOutputValue.Scalar(0), out _, out var reason), Is.False);
            Assert.That(reason, Does.Contain("weight can change"));
        }

        [Test]
        public void AnalysisSnapshotReuse_InitialAndUnreachableStateContractsStaySeparate()
        {
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(Clip(), Clip()));
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var layer = controller.Layers.Single();
            var proof = new FxPropertyOutput(entries, AnimatorReachability.Analyze(root, entries.Values));
            var writers = new[] { ((object)VRCAvatarDescriptor.AnimLayerType.FX, layer) };
            Assert.That(proof.TryGetFinalOutputStates(writers, out var all, out _, includeUnreachableOutputs: true), Is.True);
            Assert.That(all.Length, Is.EqualTo(2));
            Assert.That(proof.TryGetFinalOutputStates(writers, out var initial, out _, includeInitialOutputs: true), Is.True);
            Assert.That(initial, Is.EqualTo(new[] { layer.StateMachine.DefaultState }));
            Assert.That(proof.TryGetFinalOutputStates(writers, out var final, out _), Is.True);
            Assert.That(final, Is.EqualTo(initial));
        }

        [Test]
        public void AnalysisSnapshotReuse_RepeatedQueriesReuseFactsWithoutCrossingContracts()
        {
            var light = root.AddComponent<Light>(); light.intensity = .5f;
            var binding = EditorCurveBinding.FloatCurve("", typeof(Light), "m_Intensity");
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(StaticCurve(binding, .5f)));
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var index = AvatarAnimationIndex.Create(root, entries, null);
            var previous = AnalysisCost.Observer;
            var counts = new Dictionary<string, int>();
            try
            {
                AnalysisCost.Observer = (name, _) => counts[name] = counts.TryGetValue(name, out int n) ? n + 1 : 1;
                var constants = index.ConstantOutputs;
                for (int i = 0; i < 3; i++)
                {
                    var invariant = new InvariantComponentSettings(index, index.Reachability, entries, sharedConstants: constants);
                    Assert.That(invariant.IsConstantProperty(light, "m_Intensity"), Is.True);
                    Assert.That(constants.TryProve(light, "m_Intensity", ConstantOutputValue.Scalar(.5f), out var proof, out _), Is.True);
                    Assert.That(proof.Value.Number, Is.EqualTo(.5f));
                    Assert.That(constants.MatchesInitialValue(light, index.ForObject(light), .5f, false,
                        allowSmallNumericalDifferences: true), Is.True);
                    Assert.That(constants.MatchesInitialValue(light, index.ForObject(light), 1, false), Is.False);
                }
                Assert.That(counts["Constant output writer index"], Is.EqualTo(1));
                Assert.That(counts["FX layer/all-controller weight scan"], Is.EqualTo(1));
                Assert.That(counts["FX layer/reachable state enumeration"], Is.EqualTo(1));
            }
            finally { AnalysisCost.Observer = previous; }
        }
    }
}
