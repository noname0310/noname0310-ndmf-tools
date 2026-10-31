using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private OptimizationPlan IneffectivePlan(AnimationUsage usage, bool enabled = true, ISet<string> preserve = null,
            bool observeAllVertices = false) => OptimizationPlan.Create(renderer, usage, false,
            preserve ?? new HashSet<string>(), removeIneffective: enabled, observeAllVertices: observeAllVertices);

        private AnimationClip AnimatedShapes(Mesh mesh)
        {
            var clip = Clip();
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                AnimationUtility.SetEditorCurve(clip, Binding(mesh.GetBlendShapeName(shape)), AnimationCurve.Linear(0, -25, 4, 150));
            return clip;
        }

        [TestCase(false)] [TestCase(true)]
        public void IneffectiveRemovalIsOptionalAndDeletesAnimatedEmptySeparators(bool enabled)
        {
            var source = Mesh(); Frame(source, "------No-bra------", 100, Vector3.zero);
            Frame(source, "Working", 100, Vector3.right);
            renderer.SetBlendShapeWeight(0, 175); renderer.SetBlendShapeWeight(1, 35);
            var clip = AnimatedShapes(source); string original = EditorJsonUtility.ToJson(clip);
            var (usage, controller) = Usage(clip);
            var plan = IneffectivePlan(usage, enabled);
            Assert.That(plan.HasChanges, Is.EqualTo(enabled));
            Assert.That(plan.Ineffective.Select(s => s.Name), Is.EqualTo(enabled ? new[] { "------No-bra------" } : Array.Empty<string>()));
            if (enabled)
            {
                var output = Track(MeshOptimization.Generate(plan, MeshOptimization.CreateRecipe(plan)));
                MeshOptimization.Apply(plan, output); plan.RewriteCurves();
                Assert.That(output.blendShapeCount, Is.EqualTo(1));
                Assert.That(output.GetBlendShapeName(0), Is.EqualTo("Working"));
                Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(35));
                Assert.That(output.vertices, Is.EqualTo(source.vertices), "Removing an ineffective shape does not bake unused deltas");
                var resultClip = controller.AllReachableNodes().OfType<VirtualClip>().Single();
                Assert.That(resultClip.GetFloatCurve(Binding("------No-bra------")), Is.Null);
                Assert.That(resultClip.GetFloatCurve(Binding("Working")).Evaluate(4), Is.EqualTo(150));
            }
            Assert.That(source.blendShapeCount, Is.EqualTo(2));
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(original));
        }

        [Test]
        public void IneffectiveRemovalFindsShapesAfterRegionFilteringBeforeVertexCompaction()
        {
            var source = renderer.sharedMesh = OcclusionMaskMesh();
            var mask = BlendShapeRegionMasks.Create(source, new[] { "Region A" }, 0);
            renderer.sharedMesh = Track(mask.Remove(source, new[] { true, false }));
            renderer.SetBlendShapeWeight(0, 60);
            var plan = IneffectivePlan(Usage(AnimatedShapes(source)).usage);
            Assert.That(plan.Ineffective.Select(s => s.Name), Is.EqualTo(new[] { "Region A" }));
            var output = Track(MeshOptimization.Generate(plan, MeshOptimization.CreateRecipe(plan)));
            MeshOptimization.Apply(plan, output);
            Assert.That(output.GetBlendShapeIndex("Region A"), Is.EqualTo(-1));
            Assert.That(output.GetBlendShapeIndex("Region B"), Is.GreaterThanOrEqualTo(0));
            Assert.That(output.GetBlendShapeIndex("Overlapping"), Is.GreaterThanOrEqualTo(0));
            Assert.That(output.GetBlendShapeFrameCount(output.GetBlendShapeIndex("Multiple frames")), Is.EqualTo(2));
            Assert.That(output.vertexCount, Is.EqualTo(source.vertexCount));
            Assert.That(output.vertices, Is.EqualTo(source.vertices));
            Assert.That(output.GetIndices(0), Is.Empty);
            Assert.That(output.GetIndices(1), Is.EqualTo(source.GetIndices(1)));
            Assert.That(source.GetIndices(0), Is.Not.Empty);
        }

        [TestCase("Position", false)] [TestCase("Normal", false)] [TestCase("Tangent", false)]
        [TestCase("Position", true)] [TestCase("Normal", true)] [TestCase("Tangent", true)]
        public void IneffectiveRemovalRetainsTinyDeltasInEveryChannelAndFrame(string channel, bool laterFrame)
        {
            var source = Mesh(); var zero = new Vector3[source.vertexCount];
            // Unity drops deltas below its native mesh import threshold. Exercise
            // retained mesh data here, and smaller raw deltas separately below.
            var small = new Vector3[source.vertexCount]; small[0] = Vector3.right * .0001f;
            if (laterFrame) source.AddBlendShapeFrame("Tiny", 50, zero, zero, zero);
            source.AddBlendShapeFrame("Tiny", 100, channel == "Position" ? small : zero,
                channel == "Normal" ? small : zero, channel == "Tangent" ? small : zero);
            var stored = new ShapeDelta(source.vertexCount); stored.Read(source, 0, laterFrame ? 1 : 0);
            Assert.That((channel == "Position" ? stored.Vertices : channel == "Normal" ? stored.Normals : stored.Tangents)[0].x,
                Is.Not.Zero, "Unity must retain the small fixture delta");
            Assert.That(renderer.GetBlendShapeWeight(0), Is.Zero);
            Assert.That(IneffectivePlan(Usage(AnimatedShapes(source)).usage).HasChanges, Is.False);
        }

        [TestCase(MeshTopology.Triangles)] [TestCase(MeshTopology.Lines)] [TestCase(MeshTopology.Points)]
        public void IneffectiveRemovalChecksEverySubmeshAndBaseVertex(MeshTopology topology)
        {
            var source = renderer.sharedMesh = OcclusionMaskMesh();
            source.SetIndices(Array.Empty<int>(), MeshTopology.Triangles, 0);
            int[] indices = topology == MeshTopology.Triangles ? new[] { 0, 1, 2 } : topology == MeshTopology.Lines ? new[] { 0, 1 } : new[] { 1 };
            source.SetIndices(indices, topology, 1, calculateBounds: false, baseVertex: 3);
            var plan = IneffectivePlan(Usage(AnimatedShapes(source)).usage);
            Assert.That(plan.Ineffective.Select(s => s.Name), Is.EqualTo(new[] { "Region A" }));
        }

        [Test]
        public void IneffectiveRemovalKeepsClipDurationWhenTheLastRealCurveDisappears()
        {
            var source = Mesh(); Frame(source, "Empty", 100, Vector3.zero);
            var clip = AnimatedShapes(source); var (usage, controller) = Usage(clip);
            var virtualClip = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            var use = usage.Get(renderer, "Empty").Single(); use.Additive = true;
            var plan = IneffectivePlan(usage); plan.RewriteCurves();
            Assert.That(plan.Ineffective, Has.Count.EqualTo(1), "An additive weight still cannot deform an ineffective shape");
            Assert.That(virtualClip.GetFloatCurve(Binding("Empty")), Is.Null);
            Assert.That(virtualClip.GetFloatCurveBindings().SelectMany(b => virtualClip.GetFloatCurve(b).keys).Max(k => k.time), Is.EqualTo(4));
            Assert.That(AnimationUtility.GetEditorCurve(clip, Binding("Empty")).Evaluate(4), Is.EqualTo(150));
        }

        [TestCase("Cloth")] [TestCase("MeshSwap")] [TestCase("ReadOnly")]
        public void IneffectiveRemovalRetainsUnsupportedOrSharedContexts(string reason)
        {
            var source = Mesh(); Frame(source, "Empty", 100, Vector3.zero);
            var (usage, _) = Usage(AnimatedShapes(source));
            if (reason == "Cloth") root.AddComponent<Cloth>();
            if (reason == "MeshSwap") usage.MeshSwaps.Add(renderer);
            if (reason == "ReadOnly") usage.Get(renderer, "Empty").Single().ReadOnly = true;
            Assert.That(IneffectivePlan(usage).HasChanges, Is.False);
        }

        [Test]
        public void IneffectiveRemovalKeepsUnindexedVerticesWhenAnotherComponentReadsTheRenderer()
        {
            var source = renderer.sharedMesh = OcclusionMaskMesh();
            source.SetIndices(Array.Empty<int>(), MeshTopology.Triangles, 0);
            var emitter = PolicyChild("Emitter").AddComponent<ParticleSystem>();
            var shape = emitter.shape; shape.shapeType = ParticleSystemShapeType.SkinnedMeshRenderer; shape.skinnedMeshRenderer = renderer;
            Assert.That(BlendShapeEffectAnalysis.FindFullVertexReaders(root), Does.Contain(renderer));
            Assert.That(IneffectivePlan(Usage(AnimatedShapes(source)).usage, observeAllVertices: true).HasChanges, Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void IneffectiveBuildPassHonorsProtectionAndRemapsEyelidIndices(bool enabled)
        {
            var source = Mesh();
            foreach (var name in new[] { "Separator", "まばたき", "Custom", "Eyelid", "Viseme" }) Frame(source, name, 100, Vector3.zero);
            var descriptor = root.AddComponent<VRCAvatarDescriptor>(); root.AddComponent<Animator>();
            descriptor.VisemeSkinnedMesh = renderer; descriptor.VisemeBlendShapes = new[] { "Viseme" };
            var eyes = descriptor.customEyeLookSettings;
            eyes.eyelidsSkinnedMesh = renderer; eyes.eyelidsBlendshapes = new[] { 3, -1, -1 }; descriptor.customEyeLookSettings = eyes;
            root.AddComponent<BlendShapeOptimizationExclude>().BlendShapes.Add("Custom");
            var optimizer = root.AddComponent<ALTOTraceAndOptimizeBlendShapes>();
            optimizer.RemoveIneffectiveBlendShapes = enabled; optimizer.OptimizeZeroResetAnimations = false;
            var clip = AnimatedShapes(source);
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer {
                type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = Controller(clip) } };
            descriptor.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            for (int i = 0; i < source.blendShapeCount; i++) renderer.SetBlendShapeWeight(i, 10 + i);
            var context = new BuildContext(root, null); ALTOPlugin.Capture(context);
            Assert.That(context.GetState<OptimizationState>().Scopes.Single().RemoveIneffective, Is.EqualTo(enabled));
            ALTOPlugin.Apply(context); Track(renderer.sharedMesh);
            var outputController = (AnimatorController)descriptor.baseAnimationLayers[0].animatorController;
            TrackControllerAssets(outputController);
            Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(enabled ? 4 : 5));
            Assert.That(descriptor.customEyeLookSettings.eyelidsBlendshapes, Is.EqualTo(new[] { enabled ? 2 : 3, -1, -1 }));
            foreach (var name in new[] { "まばたき", "Custom", "Eyelid", "Viseme" })
            {
                int index = renderer.sharedMesh.GetBlendShapeIndex(name);
                Assert.That(index, Is.GreaterThanOrEqualTo(0));
                Assert.That(renderer.GetBlendShapeWeight(index), Is.EqualTo(10 + source.GetBlendShapeIndex(name)));
            }
            Assert.That(AnimationUtility.GetEditorCurve(outputController.animationClips.Single(), Binding("Separator")) == null, Is.EqualTo(enabled));
            Assert.That(source.blendShapeCount, Is.EqualTo(5));
            Assert.That(AnimationUtility.GetCurveBindings(clip), Has.Length.EqualTo(5));
        }

        [Test]
        public void IneffectiveRemovalRejectsConflictingEqualScopeOptions()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var first = PolicyChild("First").AddComponent<ALTOTraceAndOptimizeBlendShapes>(); first.ScopeRoot = root.transform;
            var second = PolicyChild("Second").AddComponent<ALTOTraceAndOptimizeBlendShapes>(); second.ScopeRoot = root.transform;
            second.RemoveIneffectiveBlendShapes = false;
            Assert.Throws<InvalidOperationException>(() => ALTOPlugin.Capture(new BuildContext(root, null)));
        }

        [TestCase(float.Epsilon)] [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
        public void IneffectiveRemovalDoesNotRoundSmallOrInvalidRawDeltasToZero(float value)
        {
            var mesh = Mesh(); var effects = new BlendShapeEffectAnalysis(mesh);
            foreach (int channel in new[] { 0, 1, 2 })
            {
                var delta = new ShapeDelta(mesh.vertexCount);
                (channel == 0 ? delta.Vertices : channel == 1 ? delta.Normals : delta.Tangents)[0] = new Vector3(value, 0, 0);
                Assert.That(effects.HasEffect(delta), Is.True);
            }
        }

        [TestCase(false, "ZeroDefault")] [TestCase(true, "ZeroDefault")]
        [TestCase(true, "Cancellation")] [TestCase(true, "Residual")]
        public void IneffectiveRemovalEliminatesEmptyGeneratedGroups(bool enabled, string kind)
        {
            var source = Mesh();
            Frame(source, "Positive", 100, Vector3.right);
            Frame(source, "Negative", 100, Vector3.left);
            if (kind != "ZeroDefault")
            {
                renderer.SetBlendShapeWeight(0, 50);
                renderer.SetBlendShapeWeight(1, kind == "Residual" ? 49.999f : 50);
            }
            var clip = Clip("Positive", "Negative");
            var (usage, controller) = Usage(clip);
            var plan = OptimizationPlan.Create(renderer, usage, true, new HashSet<string>(), removeIneffective: enabled);
            Assert.That(plan.Groups.Count, Is.EqualTo(1));
            var recipe = MeshOptimization.CreateRecipe(plan);
            bool removed = enabled && kind != "Residual";
            Assert.That(plan.Groups.Count, Is.EqualTo(removed ? 0 : 1));
            Assert.That(recipe.Groups.Length, Is.EqualTo(plan.Groups.Count));
            Assert.That(plan.Ineffective.Count, Is.EqualTo(removed ? 2 : 0));
            var output = Track(MeshOptimization.Generate(plan, recipe));
            MeshOptimization.Apply(plan, output); plan.RewriteCurves();
            Assert.That(output.blendShapeCount, Is.EqualTo(removed ? 0 : 1));
            var result = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(result.GetFloatCurveBindings().Count(b => b.propertyName.StartsWith("blendShape.")), Is.EqualTo(removed ? 0 : 1));
            Assert.That(result.GetFloatCurveBindings().SelectMany(b => result.GetFloatCurve(b).keys).Max(k => k.time), Is.EqualTo(1));
        }

        [TestCase(false, false, true)] [TestCase(true, false, true)]
        [TestCase(false, true, true)] [TestCase(false, false, false)]
        public void IneffectiveRemovalFeedsWholeAvatarOutputAnalysis(bool additionalOutput, bool normalized, bool writeDefaults)
        {
            var mesh = Mesh(); Frame(mesh, "Empty", 100, Vector3.zero); Frame(mesh, "Working", 100, Vector3.right);
            var clip = StaticCurve(Binding("Empty"), 100); clip.name = "Empty output";
            if (additionalOutput) AnimationUtility.SetEditorCurve(clip, Binding("Working"), AnimationCurve.Constant(0, 1, 20));
            var visible = StaticCurve(Binding("Working"), 100); visible.name = "Visible output";
            var source = TreeController(("EmptyInput", clip), ("LiveInput", visible));
            var state = source.layers[0].stateMachine.defaultState; state.writeDefaultValues = writeDefaults;
            using (var serialized = new SerializedObject(state.motion))
            {
                serialized.FindProperty("m_NormalizedBlendValues").boolValue = normalized;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            bool retained = additionalOutput || normalized || !writeDefaults;
            bool inputObservable = additionalOutput || normalized;
            var expected = SampleFrozen(source, (p, i) => { p.SetFloat("EmptyInput", i % 2); p.SetFloat("LiveInput", i / 20f); },
                20, () => renderer.GetBlendShapeWeight(mesh.GetBlendShapeIndex("Working")));
            renderer.SetBlendShapeWeight(1, 0);
            root.AddComponent<ALTOTraceAndOptimizeBlendShapes>(); ObjectSettings(); root.AddComponent<ALTOTraceAndOptimizeExpressions>();
            var menu = ExpressionMenu(ExpressionControl("Empty", "EmptyInput"), ExpressionControl("Live", "LiveInput"));
            var context = ExpressionBuild(menu, source);
            FreezeExpressions(new VRCExpressionParameters.Parameter { name = "EmptyInput", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = false },
                new VRCExpressionParameters.Parameter { name = "LiveInput", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = false });
            ObjectOptimizationPass.Capture(context);
            context.GetState<AnimatorOptimizationState>().Settings = new AnimatorOptimizationSettings
                { StateLogic = true, UnusedParameters = true, ParameterDeclarations = true };
            ALTOPlugin.Capture(context); ALTOPlugin.Apply(context); Track(renderer.sharedMesh);
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("Empty"), Is.EqualTo(-1));
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            ExpressionOptimizationPass.Apply(context); AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            var built = (AnimatorController)descriptor.baseAnimationLayers.Single().animatorController; TrackControllerAssets(built);
            Assert.That(built.parameters.Any(p => p.name == "EmptyInput"), Is.EqualTo(retained), "Unused controller declaration");
            Assert.That(built.parameters.Any(p => p.name == "LiveInput"), Is.True);
            Assert.That(descriptor.expressionsMenu.controls.Any(c => c.name == "Empty"), Is.EqualTo(inputObservable), "Unused menu control");
            Assert.That(descriptor.expressionParameters.parameters.Any(p => p.name == "EmptyInput"), Is.EqualTo(inputObservable), "Unused local expression parameter");
            if (!retained) Assert.That(built.animationClips.Select(c => c.name), Does.Not.Contain("Empty output"));
            Assert.That(built.animationClips.SelectMany(AnimationUtility.GetCurveBindings).Any(b => b.propertyName == "blendShape.Empty"), Is.False);
            UnityEngine.Object.DestroyImmediate(root.GetComponent<Animator>());
            renderer.SetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex("Working"), 0);
            var actual = SampleFrozen(built, (p, i) =>
            {
                if (retained) p.SetFloat("EmptyInput", i % 2);
                p.SetFloat("LiveInput", i / 20f);
            }, 20, () => renderer.GetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex("Working")));
            Assert.That(actual, Is.EqualTo(expected).Within(1e-5));
            Assert.That(source.parameters.Length, Is.EqualTo(2));
            Assert.That(AnimationUtility.GetEditorCurve(clip, Binding("Empty")), Is.Not.Null);
        }

        [TestCase(false)] [TestCase(true)]
        public void IneffectiveRemovalCollectsAnEmptyLayerButRetainsAnimationEvents(bool events)
        {
            var mesh = Mesh(); Frame(mesh, "Empty", 100, Vector3.zero); Frame(mesh, "Working", 100, Vector3.right);
            var visible = StaticCurve(Binding("Working"), 100); visible.name = "Visible output";
            var source = TreeController(("LiveInput", visible));
            source.AddParameter("EmptyInput", AnimatorControllerParameterType.Bool);
            var machine = Track(new AnimatorStateMachine { name = "Empty feature" });
            var first = Track(machine.AddState("First")); var second = Track(machine.AddState("Second"));
            machine.defaultState = first; first.writeDefaultValues = second.writeDefaultValues = true;
            var firstClip = Clip(); firstClip.name = "First empty output";
            AnimationUtility.SetEditorCurve(firstClip, Binding("Empty"), AnimationCurve.Constant(0, 4, 100));
            var secondClip = Clip(); secondClip.name = "Second empty output";
            AnimationUtility.SetEditorCurve(secondClip, Binding("Empty"), AnimationCurve.Constant(0, 2, 20));
            first.motion = firstClip; second.motion = secondClip;
            var edge = Track(first.AddTransition(second)); edge.AddCondition(AnimatorConditionMode.If, 0, "EmptyInput");
            source.layers = source.layers.Concat(new[] { new AnimatorControllerLayer
                { name = "Empty feature", stateMachine = machine, defaultWeight = 1 } }).ToArray();
            root.AddComponent<ALTOTraceAndOptimizeBlendShapes>(); ObjectSettings(); root.AddComponent<ALTOTraceAndOptimizeExpressions>();
            var context = ExpressionBuild(ExpressionMenu(ExpressionControl("Empty", "EmptyInput"), ExpressionControl("Live", "LiveInput")), source);
            FreezeExpressions(new VRCExpressionParameters.Parameter { name = "EmptyInput", valueType = VRCExpressionParameters.ValueType.Bool, networkSynced = false },
                new VRCExpressionParameters.Parameter { name = "LiveInput", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = false });
            ObjectOptimizationPass.Capture(context);
            context.GetState<AnimatorOptimizationState>().Settings = new AnimatorOptimizationSettings
                { StateLogic = true, UnusedParameters = true, ParameterDeclarations = true };
            ALTOPlugin.Capture(context); ALTOPlugin.Apply(context); Track(renderer.sharedMesh);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            ExpressionOptimizationPass.Apply(context);
            // NDMF removes authoring events while cloning. Exercise events introduced
            // by a later SDK generator, which the final output pass must preserve.
            if (events)
            {
                var current = (AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController;
                AnimationUtility.SetAnimationEvents(current.animationClips.Single(c => c.name == firstClip.name),
                    new[] { new AnimationEvent { time = .3f, functionName = "ExternalCallback" } });
            }
            AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            var built = (AnimatorController)descriptor.baseAnimationLayers.Single().animatorController; TrackControllerAssets(built);
            Assert.That(built.layers.Any(l => l.name == "Empty feature"), Is.EqualTo(events));
            Assert.That(built.parameters.Any(p => p.name == "EmptyInput"), Is.EqualTo(events));
            Assert.That(descriptor.expressionsMenu.controls.Any(c => c.name == "Empty"), Is.EqualTo(events));
            Assert.That(built.animationClips.Any(c => c.name == firstClip.name), Is.EqualTo(events));
            if (events) Assert.That(built.animationClips.SelectMany(AnimationUtility.GetAnimationEvents).Select(e => e.functionName), Does.Contain("ExternalCallback"));
            Assert.That(source.layers.Length, Is.EqualTo(2));
            Assert.That(AnimationUtility.GetEditorCurve(firstClip, Binding("Empty")), Is.Not.Null);
        }

        [TestCase(false)] [TestCase(true)]
        public void IneffectiveRemovalPreservesExitTimingAndObservableDrivers(bool driver)
        {
            var mesh = Mesh(); Frame(mesh, "Empty", 100, Vector3.zero); Frame(mesh, "Working", 100, Vector3.right);
            var empty = Clip(); AnimationUtility.SetEditorCurve(empty, Binding("Empty"), AnimationCurve.Constant(0, 1, 100));
            var visible = StaticCurve(Binding("Working"), 70); visible.name = "Visible output";
            var source = Controller(empty, visible);
            var states = source.layers[0].stateMachine.states.Select(s => s.state).ToArray();
            var transition = Track(states[0].AddTransition(states[1]));
            transition.hasExitTime = true; transition.exitTime = .5f; transition.duration = 0;
            if (driver)
            {
                source.AddParameter("Output", AnimatorControllerParameterType.Float);
                var behaviour = Track(states[0].AddStateMachineBehaviour<VRCAvatarParameterDriver>());
                behaviour.parameters.Add(new VRC_AvatarParameterDriver.Parameter { name = "Output", value = 1 });
                AnimationUtility.SetEditorCurve(empty, ParameterBinding("Output"), AnimationCurve.Constant(0, 1, 1));
            }
            var expected = SampleFrozen(source, (_, __) => { }, 90, () => renderer.GetBlendShapeWeight(1));
            renderer.SetBlendShapeWeight(1, 0);
            root.AddComponent<ALTOTraceAndOptimizeBlendShapes>(); ObjectSettings(); root.AddComponent<ALTOTraceAndOptimizeExpressions>();
            var context = ExpressionBuild(null, source); ObjectOptimizationPass.Capture(context);
            if (driver) FreezeExpressions(new VRCExpressionParameters.Parameter { name = "Output", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = true });
            context.GetState<AnimatorOptimizationState>().Settings = new AnimatorOptimizationSettings { StateLogic = true, UnusedParameters = true };
            ALTOPlugin.Capture(context); ALTOPlugin.Apply(context); Track(renderer.sharedMesh);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            ExpressionOptimizationPass.Apply(context); AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            var built = (AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController;
            TrackControllerAssets(built);
            Assert.That(built.layers[0].stateMachine.states.Length, Is.EqualTo(2));
            Assert.That(built.layers[0].stateMachine.defaultState.motion.averageDuration, Is.EqualTo(1).Within(1e-6));
            if (driver) Assert.That(built.layers[0].stateMachine.defaultState.behaviours.OfType<VRCAvatarParameterDriver>().Single().parameters,
                Has.Some.Matches<VRC_AvatarParameterDriver.Parameter>(p => p.name == "Output"));
            UnityEngine.Object.DestroyImmediate(root.GetComponent<Animator>());
            renderer.SetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex("Working"), 0);
            var actual = SampleFrozen(built, (_, __) => { }, 90,
                () => renderer.GetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex("Working")));
            Assert.That(actual, Is.EqualTo(expected).Within(1e-5));
        }
    }
}
