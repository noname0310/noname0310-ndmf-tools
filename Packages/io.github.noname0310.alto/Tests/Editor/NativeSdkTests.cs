using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Expressions;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterLayout;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDKBase;
using Parameter = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;
using TargetMode = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.ParameterBehaviorTargetMode;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private VRCAvatarDescriptor NativeFixture(string shape = "NativeSmile")
        {
            var descriptor = root.GetComponent<VRCAvatarDescriptor>() ?? root.AddComponent<VRCAvatarDescriptor>();
            descriptor.baseAnimationLayers ??= Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            descriptor.specialAnimationLayers ??= Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            descriptor.enableFaceTracking = true; descriptor.useAllTrackingKeys = true;
            descriptor.faceTrackingMeshes = new() { renderer };
            var set = new VRCAvatarDescriptor.FaceTrackingBlendshapesSet { setName = "Face", setWeight = 1 };
            set.faceTrackingBlendshapeNames = new string[(int)VRCAvatarDescriptor.FaceTrackingKey.Max];
            set.faceTrackingBlendshapeNames[(int)VRCAvatarDescriptor.FaceTrackingKey.JawOpen] = shape;
            descriptor.faceTrackingBlendshapesSetList = new() { set };
            descriptor.faceTrackingParameterBehaviors = new();
            return descriptor;
        }

        [Test]
        public void NativeSdkProtectsOnlyRuntimeDrivenMorphsWithoutAnimationCurves()
        {
            Mesh("NativeSmile", "Customization"); var descriptor = NativeFixture();
            var protectedNames = ALTOPlugin.ProtectedNames(renderer, descriptor, false);
            var plan = Plan(Usage(Clip()).usage, protectedNames.ToArray());
            Assert.That(plan.Frozen.Select(s => s.Name), Is.EqualTo(new[] { "Customization" }));
            descriptor.enableFaceTracking = false;
            Assert.That(ALTOPlugin.ProtectedNames(renderer, descriptor, false), Does.Not.Contain("NativeSmile"));
        }

        [TestCase("UserEyeTracking")] [TestCase("UserFaceTracking")]
        public void NativeSdkTrackingAvailabilityRemainsVolatile(string name)
        {
            var fixture = ReachabilityFixture(name); Edge(fixture.idle, fixture.face, name);
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.True);
        }

        [Test]
        public void NativeSdkForwardedValueIsAnExternalWriterWithoutAnExpressionDeclaration()
        {
            Mesh("NativeSmile"); var descriptor = NativeFixture();
            descriptor.faceTrackingParameterBehaviors.Add(new() { targetMode = TargetMode.ForwardToAnimator, outputParameterName = "FaceInput" });
            var fixture = ReachabilityFixture("FaceInput"); Edge(fixture.idle, fixture.face, "FaceInput");
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.True);
            Assert.That(ParameterFreezeSelection.DirectInputs(root), Does.Contain("FaceInput"));
            Assert.That(new ParameterPaddingUsage(root).IsUnobserved("FaceInput"), Is.False);
        }

        [Test]
        public void NativeSdkSetControlParticipatesInLivenessWithoutDisablingReachability()
        {
            Mesh("NativeSmile"); NativeFixture();
            var controller = Controller(Clip()); controller.AddParameter("Weight", AnimatorControllerParameterType.Float);
            var control = Track(controller.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAnimatorFaceTrackingBlendshapesSet>());
            control.blendSets ??= new();
            control.blendSets.Add(new() { type = VRC_AnimatorFaceTrackingBlendshapesSet.SourceType.Parameter,
                sourceParameter = "Weight", blendshapesSetName = "Face" });
            var analyzed = Analyze(controller);
            Assert.That(analyzed.analysis.FallbackReason, Is.Null);
            var graph = AnimatorParameterDependencies.Collect(root, new[] { analyzed.controller }, forExpressions: true);
            Assert.That(graph.ExpandObservable(), Does.Contain("Weight"));
            renderer.sharedMesh = Mesh();
            graph = AnimatorParameterDependencies.Collect(root, new[] { analyzed.controller }, forExpressions: true);
            Assert.That(graph.ExpandObservable(), Does.Not.Contain("Weight"));
            var native = new NativeFaceTracking(root);
            var clonedControl = analyzed.controller.AllReachableNodes().OfType<VirtualState>().Single().Behaviours.OfType<VRC_AnimatorFaceTrackingBlendshapesSet>().Single();
            Assert.That(native.Prune(graph.ExpandObservable(), new[] { clonedControl }), Is.EqualTo(1));
            Assert.That(clonedControl.blendSets, Is.Empty);
            Assert.That(control.blendSets.Count, Is.EqualTo(1), "The source controller must not be modified.");
        }

        [Test]
        public void NativeSdkSetWeightDependenciesReachForwardedInputsAndEliminateDeadCycles()
        {
            Mesh(); var descriptor = NativeFixture();
            descriptor.faceTrackingBlendshapesSetList.Add(new() { setName = "Intermediate" });
            descriptor.faceTrackingParameterBehaviors.Add(new() { targetMode = TargetMode.ApplyBlendSetWeight,
                weightBlendSetName = "Face", targetBlendSetName = "Intermediate" });
            descriptor.faceTrackingParameterBehaviors.Add(new() { targetMode = TargetMode.ForwardToAnimator,
                weightBlendSetName = "Intermediate", outputParameterName = "Output" });
            var native = new NativeFaceTracking(root);
            Assert.That(native.SetOutputs("Face").Parameters, Does.Contain("Output"));
            Assert.That(native.SetObserved("Face", new HashSet<string>()), Is.False);
            Assert.That(native.SetObserved("Face", new HashSet<string> { "Output" }), Is.True);
            descriptor.faceTrackingParameterBehaviors[1] = new() { targetMode = TargetMode.ApplyBlendSetWeight,
                weightBlendSetName = "Intermediate", targetBlendSetName = "Face" };
            Assert.That(new NativeFaceTracking(root).SetObserved("Face", new HashSet<string>()), Is.False);
        }

        [Test]
        public void NativeSdkEmptyMorphRemovalClearsNativeNamesAfterTheLastTargetDisappears()
        {
            var mesh = Mesh(); Frame(mesh, "NativeSmile", 100, Vector3.zero); var descriptor = NativeFixture();
            Assert.That(ALTOPlugin.ProtectedNames(renderer, descriptor, false, true), Does.Not.Contain("NativeSmile"));
            var other = PolicyChild("Second face").AddComponent<SkinnedMeshRenderer>(); other.sharedMesh = mesh;
            descriptor.faceTrackingMeshes.Add(other);
            renderer.sharedMesh = Track(new UnityEngine.Mesh());
            Assert.That(new NativeFaceTracking(root).ClearMissingShapes(new HashSet<string> { "NativeSmile" }), Is.Zero);
            other.sharedMesh = renderer.sharedMesh;
            Assert.That(new NativeFaceTracking(root).ClearMissingShapes(new HashSet<string> { "NativeSmile" }), Is.EqualTo(1));
            Assert.That(descriptor.faceTrackingBlendshapesSetList[0].faceTrackingBlendshapeNames, Does.Not.Contain("NativeSmile"));
        }

        [Test]
        public void NativeSdkAdvancedContactInputsKeepReachableStatesAndRespectEnabledFlags()
        {
            var receiver = PolicyChild("Contact").AddComponent<VRCContactReceiver>(); receiver.parameter = "Touch";
            receiver.receiverType = ContactReceiver.ReceiverType.Proximity;
            receiver.advancedParameters = ContactReceiver.AdvancedParameter.SenderVelocity | ContactReceiver.AdvancedParameter.CollisionTags;
            receiver.collisionTags = new() { "Finger", "Palm" };
            var names = ContactOutputs.Parameters(receiver).ToArray();
            Assert.That(names, Does.Contain("Touch" + ContactReceiver.PARAM_SENDER_VELOCITY_X));
            Assert.That(names, Does.Contain("Touch" + ContactReceiver.PARAM_COLLISION_TAG_PREFIX + "Palm"));
            Assert.That(names, Does.Not.Contain("Touch" + ContactReceiver.PARAM_POINT_SEPARATION));
            var fixture = ReachabilityFixture("Touch" + ContactReceiver.PARAM_SENDER_VELOCITY_MAGNITUDE);
            Edge(fixture.idle, fixture.face, "Touch" + ContactReceiver.PARAM_SENDER_VELOCITY_MAGNITUDE);
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.True);
            receiver.advancedParameters = ContactReceiver.AdvancedParameter.None;
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.False);
        }

        [Test]
        public void NativeSdkMaterialContactUsesShaderConsumersEvenWithoutAnimatorParameters()
        {
            Mesh(); var receiver = PolicyChild("Material contact").AddComponent<VRCContactReceiver>();
            receiver.parameter = ""; receiver.separateShaderParameter = "Touch"; receiver.writeToMaterials = true;
            receiver.shaderTargetRenderers = new Renderer[] { renderer };
            renderer.sharedMaterial = Track(new Material(Shader.Find("Hidden/ALTO Tests/Material Contact")));
            var index = AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController>(), null);
            var settings = ObjectSettings();
            Assert.That(ContactOutputs.MaterialReads(receiver, renderer, new ShaderPropertyUsage(), index), Is.True);
            Assert.That(AvatarDependencyGraph.Create(root, index, settings, true).IsLive(receiver), Is.True);
            renderer.sharedMaterial = Track(new Material(Shader.Find("Unlit/Color")));
            Assert.That(ContactOutputs.MaterialReads(receiver, renderer, new ShaderPropertyUsage(), index), Is.False);
            Assert.That(AvatarDependencyGraph.Create(root, index, settings, true).IsLive(receiver), Is.False);
        }

        [TestCase(VRCRaycast.RaycastComponentVersion.Version_1_0, false)]
        [TestCase(VRCRaycast.RaycastComponentVersion.Version_1_1, true)]
        public void NativeSdkRaycastVersionControlsParameterOnlyOperation(VRCRaycast.RaycastComponentVersion version, bool expected)
        {
            var ray = PolicyChild("Ray").AddComponent<VRCRaycast>(); ray.Version = version; ray.Parameter = "Ray"; ray.ResultTransform = null;
            var fixture = ReachabilityFixture("Ray_Hit"); Edge(fixture.idle, fixture.face, "Ray_Hit");
            Assert.That(HasExpression(Analyze(fixture.controller)), Is.EqualTo(expected));
        }

        [TestCase(0f, 1)] [TestCase(.25f, 0)]
        public void NativeSdkConstraintSmoothingIsPartOfTheParentingEquivalenceProof(float smoothing, int expected)
        {
            var settings = ObjectSettings(); var input = PolicyChild("Input").transform; var output = PolicyChild("Output").transform;
            var constraint = FollowingParent(input, output); constraint.SmoothingFactor = smoothing;
            Assert.That(SimplifyParents(settings), Is.EqualTo(expected));
        }

        [Test]
        public void NativeSdkExpressionEncodingSurvivesCopyPaddingAndLateValidation()
        {
            var parameter = LayoutParameter("Signed", ValueType.Int); parameter.bitWidth = 4; parameter.flippedRepresentation = true;
            parameter.useCustomLerpDuration = true; parameter.customLerpDuration = .35f;
            var copied = ParameterLayoutModel.Copy(parameter);
            Assert.That(JsonUtility.ToJson(copied), Is.EqualTo(JsonUtility.ToJson(parameter)));
            var record = LayoutRecord(parameter);
            record = JsonUtility.FromJson<ParameterLayoutRecord>(JsonUtility.ToJson(record));
            var padding = ParameterLayoutModel.Align(record, Array.Empty<Parameter>(), AdditionalSyncedParameterPolicy.Error).Parameters.Single();
            Assert.That(padding.GetBitWidthByType(), Is.EqualTo(4)); Assert.That(padding.flippedRepresentation, Is.True);
            Assert.That(MultiplexEncoding.Cost(new[] { copied, padding }), Is.EqualTo(8));
            copied.bitWidth = 5;
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutModel.Align(record, new[] { copied }, AdditionalSyncedParameterPolicy.Error));
            Assert.Throws<InvalidOperationException>(() => ParameterLayoutModel.ValidateLateDeclarations(record.Parameters, new[] { copied }));
        }

        [Test]
        public void NativeSdkMultiplexingKeepsDifferentWireEncodingsInDifferentSlots()
        {
            var parameters = Enumerable.Range(0, 32).Select(i => new Parameter { name = "P" + i, valueType = ValueType.Int,
                bitWidth = (byte)(i % 2 == 0 ? 3 : 5), flippedRepresentation = i % 3 == 0, networkSynced = true }).ToArray();
            var names = parameters.Select(p => p.name).ToHashSet();
            var plan = MultiplexEncoding.Plan(parameters, names, 32, .15f, new HashSet<string>(names));
            plan.Validate();
            foreach (var input in plan.Inputs)
            {
                Assert.That(input.SameEncoding(parameters.Single(p => p.name == input.Name)), Is.True);
                Assert.That(input.SameEncoding(plan.Slots[input.Slot]), Is.True);
            }
            Assert.That(plan.WireCost, Is.EqualTo(MultiplexEncoding.Cost(plan.WireDeclarations())));
            Assert.That(plan.WireCost, Is.LessThanOrEqualTo(32));
            Assert.That(plan.WireDeclarations().All(p => p.useCustomLerpDuration && p.customLerpDuration == 0), Is.True);
        }

        [Test]
        public void NativeSdkSharedFaceMorphsMergeUsingTheUnionOfInterpolationFrames()
        {
            var first = AssemblyPart("Face A", new[] { root.transform }, Vector3.zero);
            var second = AssemblyPart("Face B", new[] { root.transform }, Vector3.zero);
            first.probeAnchor = second.probeAnchor = root.transform;
            second.sharedMaterial = first.sharedMaterial;
            Frame(first.sharedMesh, "NativeSmile", 25, Vector3.right * .2f);
            Frame(first.sharedMesh, "NativeSmile", 75, Vector3.right * .8f);
            Frame(first.sharedMesh, "NativeSmile", 100, Vector3.right);
            Frame(second.sharedMesh, "NativeSmile", 50, Vector3.up * .25f);
            Frame(second.sharedMesh, "NativeSmile", 75, Vector3.up * .5f);
            Frame(second.sharedMesh, "NativeSmile", 100, Vector3.up * 2);
            var descriptor = NativeFixture(); descriptor.faceTrackingMeshes = new() { first, second };
            var samples = new[] { -20f, 0, 13, 25, 49, 100, 130 };
            var expected = new List<Vector3[]>();
            foreach (float weight in samples)
            {
                first.SetBlendShapeWeight(0, weight); second.SetBlendShapeWeight(0, weight);
                expected.Add(AssemblyWorldVertices(first).Concat(AssemblyWorldVertices(second)).ToArray());
            }
            first.SetBlendShapeWeight(0, 0); second.SetBlendShapeWeight(0, 0);
            var settings = root.AddComponent<ALTOTraceAndOptimizeMeshes>(); settings.RebindStaticBones = false;
            var context = new BuildContext(root, null); MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            var output = root.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r => r.sharedMesh != null).ToArray();
            Assert.That(output.Length, Is.EqualTo(1), string.Join("\n", context.GetState<OptimizationBuildReport>().Messages)); var merged = output[0]; Track(merged.sharedMesh);
            Assert.That(descriptor.faceTrackingMeshes, Is.EqualTo(new[] { merged }));
            Assert.That(merged.sharedMesh.blendShapeCount, Is.EqualTo(1));
            Assert.That(merged.sharedMesh.GetBlendShapeFrameCount(0), Is.EqualTo(4));
            Assert.That(merged.sharedMesh.GetBlendShapeName(0), Is.EqualTo("NativeSmile"));
            for (int i = 0; i < samples.Length; i++)
            {
                merged.SetBlendShapeWeight(0, samples[i]); var actual = AssemblyWorldVertices(merged);
                for (int vertex = 0; vertex < actual.Length; vertex++)
                    Assert.That(Vector3.Distance(actual[vertex], expected[i][vertex]), Is.LessThan(0.00002f), "sample " + samples[i]);
            }
        }

        [TestCase(false, 1)] [TestCase(true, 2)]
        public void NativeSdkMaterialContactsMergeOnlyIdenticalRuntimeWriters(bool different, int expected)
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var first = AssemblyPart("A", new[] { root.transform }, Vector3.zero);
            var second = AssemblyPart("B", new[] { root.transform }, Vector3.zero);
            first.probeAnchor = second.probeAnchor = root.transform;
            first.sharedMaterial = second.sharedMaterial = Track(new Material(Shader.Find("Hidden/ALTO Tests/Material Contact")));
            var contact = PolicyChild("Contact").AddComponent<VRCContactReceiver>(); contact.writeToMaterials = true; contact.parameter = "Touch";
            contact.shaderTargetRenderers = different ? new Renderer[] { first } : new Renderer[] { first, second };
            var settings = root.AddComponent<ALTOTraceAndOptimizeMeshes>(); settings.RebindStaticBones = false;
            var context = new BuildContext(root, null); MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            var output = root.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r => r.sharedMesh != null).ToArray();
            foreach (var skin in output) Track(skin.sharedMesh);
            Assert.That(output.Length, Is.EqualTo(expected), string.Join("\n", context.GetState<OptimizationBuildReport>().Messages));
            Assert.That(contact.shaderTargetRenderers.All(r => r != null), Is.True);
            if (!different) Assert.That(contact.shaderTargetRenderers.Distinct().Single(), Is.SameAs(output[0]));
        }

        [Test]
        public void NativeSdkParameterPolicySpecializesSetWeightReadersWithoutChangingSetNames()
        {
            Mesh("NativeSmile"); NativeFixture(); var controller = Controller(Clip());
            controller.AddParameter("Weight", AnimatorControllerParameterType.Float);
            var behavior = Track(controller.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAnimatorFaceTrackingBlendshapesSet>());
            behavior.blendSets ??= new();
            behavior.blendSets.Add(new() { type = VRC_AnimatorFaceTrackingBlendshapesSet.SourceType.Parameter,
                sourceParameter = "Weight", blendshapesSetName = "Face", convertRange = true, sourceMax = 1, destMax = .5f, lerpSpeed = .2f });
            var clone = Analyze(controller).controller;
            var constants = ParameterSpecialization.Prepare(new[] { clone }, new Dictionary<string, float> { ["Weight"] = .4f }, new HashSet<string>());
            ParameterSpecialization.Validate(new[] { clone }, constants); ParameterSpecialization.Apply(new[] { clone }, constants);
            var control = clone.AllReachableNodes().OfType<VirtualState>().Single().Behaviours.OfType<VRC_AnimatorFaceTrackingBlendshapesSet>().Single().blendSets.Single();
            Assert.That(control.sourceParameter, Is.EqualTo(constants["Weight"].InternalName));
            Assert.That(control.blendshapesSetName, Is.EqualTo("Face")); Assert.That(control.lerpSpeed, Is.EqualTo(.2f));
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeSdkIntegerCompressionUsesNativeBitsAndRetainsInterpolation(bool signed)
        {
            var descriptor = LayoutDescriptor();
            var parameters = Track(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            var input = LayoutParameter("Mode", ValueType.Int); input.defaultValue = signed ? -2 : 5;
            input.flippedRepresentation = signed; input.useCustomLerpDuration = true; input.customLerpDuration = .25f;
            parameters.parameters = new[] { input }; descriptor.expressionParameters = parameters;
            string before = EditorJsonUtility.ToJson(parameters);
            var settings = new ParameterCompressionSettings { RemoveUnused = false };
            settings.Integers.Add(new() { Name = "Mode", Minimum = signed ? -4 : 0, Maximum = signed ? 3 : 7 });
            var result = ParameterCompressionPass.Apply(new BuildContext(root, null), settings, null, new OptimizationBuildReport(), o => Track(o));
            var output = descriptor.expressionParameters.parameters.Single();
            Assert.That(output.name, Is.EqualTo("Mode")); Assert.That(output.GetBitWidthByType(), Is.EqualTo(3));
            Assert.That(output.flippedRepresentation, Is.EqualTo(signed)); Assert.That(output.customLerpDuration, Is.EqualTo(.25f));
            Assert.That(output.useCustomLerpDuration && output.saved && output.networkSynced, Is.True);
            Assert.That(descriptor.baseAnimationLayers.All(l => l.animatorController == null), Is.True, "Native encoding needs no Animator codec.");
            var record = LayoutRecord(output); record.IntegerEncodings = result.Integers; ParameterLayoutModel.Validate(record);
            var roundTrip = JsonUtility.FromJson<ParameterLayoutRecord>(JsonUtility.ToJson(record)); ParameterLayoutModel.Validate(roundTrip);
            Assert.That(roundTrip.IntegerEncodings.Single().IsNative, Is.True);
            var padding = ParameterLayoutModel.Align(roundTrip, Array.Empty<Parameter>(), AdditionalSyncedParameterPolicy.Error).Parameters.Single();
            Assert.That(padding.GetBitWidthByType(), Is.EqualTo(3)); Assert.That(padding.flippedRepresentation, Is.EqualTo(signed));
            Assert.That(EditorJsonUtility.ToJson(parameters), Is.EqualTo(before));
        }

        [TestCase(2, 5)] [TestCase(-3, 1)]
        public void NativeSdkNonPowerOfTwoIntegerRangesKeepThePackedDecoder(int minimum, int maximum)
        {
            var input = LayoutParameter("Mode", ValueType.Int); input.flippedRepresentation = minimum < 0; input.defaultValue = minimum;
            var codec = IntegerEncoding.CreateForSdk(input, minimum, maximum, new HashSet<string>());
            Assert.That(codec.IsNative, Is.False); Assert.That(codec.Declarations(input).Last().networkSynced, Is.False);
            for (int value = minimum - 3; value <= maximum + 3; value++)
                Assert.That(codec.Decode(codec.Encode(value)), Is.EqualTo(Math.Max(minimum, Math.Min(maximum, value))));
            input.useCustomLerpDuration = true; input.customLerpDuration = .2f;
            Assert.Throws<InvalidOperationException>(() => codec.Declarations(input));
        }

        [TestCase(-20f)] [TestCase(13f)] [TestCase(75f)] [TestCase(100f)] [TestCase(130f)]
        public void NativeSdkMorphSamplingMatchesUnityIncludingExtrapolation(float weight)
        {
            var part = AssemblyPart("Face", new[] { root.transform }, Vector3.zero);
            Frame(part.sharedMesh, "Face", 25, Vector3.right * .3f); Frame(part.sharedMesh, "Face", 100, Vector3.right);
            part.SetBlendShapeWeight(0, 0); var before = AssemblyWorldVertices(part);
            part.SetBlendShapeWeight(0, weight); var expected = AssemblyWorldVertices(part);
            var delta = new ShapeDelta(part.sharedMesh.vertexCount);
            MeshOptimization.AddAtWeight(part.sharedMesh, 0, weight, delta, new ShapeDelta(part.sharedMesh.vertexCount));
            for (int i = 0; i < before.Length; i++) Assert.That(Vector3.Distance(before[i] + delta.Vertices[i], expected[i]), Is.LessThan(1e-5f));
        }

        [TestCase("extrapolation")] [TestCase("default")] [TestCase("writer")]
        public void NativeSdkIndependentNativeMorphChannelsRemainSeparate(string difference)
        {
            var first = AssemblyPart("A", new[] { root.transform }, Vector3.zero);
            var second = AssemblyPart("B", new[] { root.transform }, Vector3.zero);
            first.probeAnchor = second.probeAnchor = root.transform; second.sharedMaterial = first.sharedMaterial;
            Frame(first.sharedMesh, "NativeSmile", 100, Vector3.right);
            if (difference == "extrapolation") Frame(second.sharedMesh, "NativeSmile", 25, Vector3.up * .25f);
            Frame(second.sharedMesh, "NativeSmile", 100, Vector3.up);
            var descriptor = NativeFixture(); descriptor.faceTrackingMeshes = new() { first, second };
            if (difference == "default") second.SetBlendShapeWeight(0, 10);
            if (difference == "writer")
            {
                var clip = StaticCurve(EditorCurveBinding.FloatCurve("A", typeof(SkinnedMeshRenderer), "blendShape.NativeSmile"), 50);
                descriptor.customizeAnimationLayers = true;
                descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX,
                    animatorController = Controller(clip), isDefault = false } };
            }
            var settings = root.AddComponent<ALTOTraceAndOptimizeMeshes>(); settings.RebindStaticBones = false;
            var context = new BuildContext(root, null); MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            Assert.That(root.GetComponentsInChildren<SkinnedMeshRenderer>().Count(r => r.sharedMesh != null), Is.EqualTo(2));
        }

        [TestCase(false, BuildTarget.StandaloneWindows64)] [TestCase(true, BuildTarget.StandaloneWindows64)]
        [TestCase(false, BuildTarget.Android)] [TestCase(true, BuildTarget.Android)]
        [TestCase(false, BuildTarget.iOS)] [TestCase(true, BuildTarget.iOS)]
        public void NativeSdkIneffectiveMorphRemovalReachesSetControlsParametersAndMenus(bool ineffective, BuildTarget target)
        {
            var mesh = Mesh(); Frame(mesh, "NativeSmile", 100, ineffective ? Vector3.zero : Vector3.right); NativeFixture();
            var controller = Controller(Clip()); controller.AddParameter("Weight", AnimatorControllerParameterType.Float);
            var control = Track(controller.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAnimatorFaceTrackingBlendshapesSet>());
            control.blendSets = new() { new() { type = VRC_AnimatorFaceTrackingBlendshapesSet.SourceType.Parameter,
                sourceParameter = "Weight", blendshapesSetName = "Face" } };
            root.AddComponent<ALTOTraceAndOptimizeBlendShapes>(); ObjectSettings(); root.AddComponent<ALTOTraceAndOptimizeExpressions>();
            var context = ExpressionBuild(ExpressionMenu(ExpressionControl("Native strength", "Weight")), controller);
            typeof(OptimizationBuildEnvironment).GetField("Target", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(context.GetState<OptimizationBuildEnvironment>(), target);
            FreezeExpressions(new Parameter { name = "Weight", valueType = ValueType.Float, networkSynced = false });
            ObjectOptimizationPass.Capture(context);
            context.GetState<AnimatorOptimizationState>().Settings = new AnimatorOptimizationSettings
                { StateLogic = true, UnusedParameters = true, ParameterDeclarations = true };
            ALTOPlugin.Capture(context); ALTOPlugin.Apply(context); Track(renderer.sharedMesh);
            AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
            ExpressionOptimizationPass.Apply(context); AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            var output = (AnimatorController)descriptor.baseAnimationLayers.Single().animatorController; TrackControllerAssets(output);
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("NativeSmile") >= 0, Is.EqualTo(!ineffective));
            Assert.That(output.parameters.Any(p => p.name == "Weight"), Is.EqualTo(!ineffective));
            Assert.That(output.layers.SelectMany(l => l.stateMachine.states).SelectMany(s => s.state.behaviours)
                .OfType<VRCAnimatorFaceTrackingBlendshapesSet>().Any(), Is.EqualTo(!ineffective));
            Assert.That(descriptor.expressionsMenu.controls.Count, Is.EqualTo(ineffective ? 0 : 1));
            Assert.That(descriptor.expressionParameters.parameters.Any(p => p.name == "Weight"), Is.EqualTo(!ineffective));
            Assert.That(control.blendSets.Count, Is.EqualTo(1), "The authoring controller stays unchanged.");
        }

        [Test]
        public void NativeSdkMaterialAndNativeMeshReferencesDoNotInventTransformWriters()
        {
            var mesh = AssemblyPart("Face", new[] { root.transform }, Vector3.zero);
            var descriptor = NativeFixture(); descriptor.faceTrackingMeshes = new() { mesh };
            var eye = PolicyChild("Eye").transform; descriptor.enableEyeLook = true;
            descriptor.customEyeLookSettings.leftEye = eye;
            descriptor.customEyeLookSettings.eyeGazeMode = VRCAvatarDescriptor.CustomEyeLookSettings.EyeGazeMode.FaceTracking;
            var receiver = PolicyChild("Contact").AddComponent<VRCContactReceiver>();
            receiver.rootTransform = mesh.transform; receiver.writeToMaterials = true; receiver.parameter = "Touch";
            receiver.shaderTargetRenderers = new Renderer[] { mesh };
            var index = AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController>(), null);
            var writers = TransformMutationAnalysis.Create(root, index);
            Assert.That(writers.Mutable.Contains(mesh.transform), Is.False); Assert.That(writers.Mutable.Contains(eye), Is.False);
            descriptor.customEyeLookSettings.eyeGazeMode = VRCAvatarDescriptor.CustomEyeLookSettings.EyeGazeMode.Both;
            writers = TransformMutationAnalysis.Create(root, index);
            Assert.That(writers.Mutable, Does.Contain(eye)); Assert.That(writers.MayChangeScale(eye), Is.False);
        }

        [Test]
        public void NativeSdkHiddenNativeMeshCanBeCollectedAndItsReferencesAreCleaned()
        {
            var mesh = AssemblyPart("Unused face", new[] { root.transform }, Vector3.zero);
            Frame(mesh.sharedMesh, "NativeSmile", 100, Vector3.right);
            var descriptor = NativeFixture(); descriptor.faceTrackingMeshes = new() { mesh };
            mesh.gameObject.SetActive(false); var settings = ObjectSettings();
            UnusedObjectCollection.Apply(root, new Dictionary<object, VirtualAnimatorController>(), null, settings, new HashSet<string>(), new List<string>());
            Assert.That(mesh == null, Is.True); Assert.That(descriptor.faceTrackingMeshes, Is.Empty);
            Assert.That(new NativeFaceTracking(root).ShapeNames(), Is.Empty);
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeSdkMergedNativeMorphKeepsItsAnimatorBindingsAndOrdinaryNamesIndependent(bool secondIsNative)
        {
            var first = AssemblyPart("A", new[] { root.transform }, Vector3.zero);
            var second = AssemblyPart("B", new[] { root.transform }, Vector3.zero);
            first.probeAnchor = second.probeAnchor = root.transform; second.sharedMaterial = first.sharedMaterial;
            Frame(first.sharedMesh, "NativeSmile", 100, Vector3.right); Frame(second.sharedMesh, "NativeSmile", 100, Vector3.up);
            var descriptor = NativeFixture(); descriptor.faceTrackingMeshes = secondIsNative ? new() { first, second } : new() { second };
            var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("A", typeof(SkinnedMeshRenderer), "blendShape.NativeSmile"), AnimationCurve.Constant(0, 1, 50));
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("B", typeof(SkinnedMeshRenderer), "blendShape.NativeSmile"), AnimationCurve.Constant(0, 1, secondIsNative ? 50 : 25));
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX,
                animatorController = Controller(clip), isDefault = false } };
            var settings = root.AddComponent<ALTOTraceAndOptimizeMeshes>(); settings.RebindStaticBones = false;
            var context = new BuildContext(root, null); MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            var output = root.GetComponentsInChildren<SkinnedMeshRenderer>().Single(r => r.sharedMesh != null); Track(output.sharedMesh);
            Assert.That(output.sharedMesh.blendShapeCount, Is.EqualTo(secondIsNative ? 1 : 2));
            Assert.That(descriptor.faceTrackingMeshes.Single(), Is.SameAs(output));
            var controller = (AnimatorController)descriptor.baseAnimationLayers.Single().animatorController; TrackControllerAssets(controller);
            var curves = controller.animationClips.SelectMany(c => AnimationUtility.GetCurveBindings(c).Select(b => (Clip: c, Binding: b)))
                .Where(p => p.Binding.propertyName.StartsWith("blendShape.")).ToArray();
            Assert.That(curves.Length, Is.EqualTo(secondIsNative ? 1 : 2));
            Assert.That(curves.All(p => p.Binding.path == AnimationUtility.CalculateTransformPath(output.transform, root.transform)), Is.True);
            foreach (var curve in curves)
            {
                int index = output.sharedMesh.GetBlendShapeIndex(curve.Binding.propertyName.Substring("blendShape.".Length));
                Assert.That(index, Is.GreaterThanOrEqualTo(0));
                Assert.That(AnimationUtility.GetEditorCurve(curve.Clip, curve.Binding).Evaluate(0),
                    Is.EqualTo(secondIsNative || output.sharedMesh.GetBlendShapeName(index) != "NativeSmile" ? 50 : 25));
            }
            Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(2));
        }

        [Test]
        public void NativeSdkMaterialContactRemapDoesNotPromoteTargetsPastTheSdkLimit()
        {
            var parts = Enumerable.Range(0, 6).Select(i => PolicyChild("Part" + i).AddComponent<SkinnedMeshRenderer>()).ToArray();
            var contact = PolicyChild("Contact").AddComponent<VRCContactReceiver>();
            contact.parameter = "Touch"; contact.writeToMaterials = true; contact.shaderTargetRenderers = parts.Cast<Renderer>().ToArray();
            ContactOutputs.Remap(root, parts.Take(3), parts[0]);
            Assert.That(contact.shaderTargetRenderers.Length, Is.EqualTo(6));
            Assert.That(ContactOutputs.Targets(contact), Is.EquivalentTo(new[] { parts[0], parts[3] }));
        }

        [Test]
        public void NativeSdkMaterialContactOutputDoesNotKeepAnOtherwiseUnusedRendererAlive()
        {
            var output = AssemblyPart("Hidden output", new[] { root.transform }, Vector3.zero); output.gameObject.SetActive(false);
            var receiver = PolicyChild("Contact").AddComponent<VRCContactReceiver>(); receiver.parameter = "Touch";
            receiver.writeToMaterials = true; receiver.shaderTargetRenderers = new Renderer[] { output };
            var settings = ObjectSettings();
            var index = AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController>(), null);
            var graph = AvatarDependencyGraph.Create(root, index, settings, true);
            Assert.That(graph.IsLive(receiver), Is.True, "The parameter output remains external.");
            Assert.That(graph.IsLive(output), Is.False, "A write destination is not an input dependency.");
        }

        [TestCase(false)] [TestCase(true)]
        public void NativeSdkMergeDoesNotActivateAnOriginallyMissingMorphBinding(bool destinationIsNative)
        {
            var first = AssemblyPart("A", new[] { root.transform }, Vector3.zero);
            var second = AssemblyPart("B", new[] { root.transform }, Vector3.zero);
            first.probeAnchor = second.probeAnchor = root.transform; second.sharedMaterial = first.sharedMaterial;
            Frame(second.sharedMesh, "NativeSmile", 100, Vector3.up);
            var descriptor = NativeFixture(); descriptor.faceTrackingMeshes = destinationIsNative ? new() { first, second } : new() { second };
            var clip = StaticCurve(EditorCurveBinding.FloatCurve("A", typeof(SkinnedMeshRenderer), "blendShape.NativeSmile"), 50);
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX,
                animatorController = Controller(clip), isDefault = false } };
            var settings = root.AddComponent<ALTOTraceAndOptimizeMeshes>(); settings.RebindStaticBones = false;
            var context = new BuildContext(root, null); MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            var output = root.GetComponentsInChildren<SkinnedMeshRenderer>().Single(r => r.sharedMesh != null); Track(output.sharedMesh);
            Assert.That(output.sharedMesh.GetBlendShapeIndex("NativeSmile"), Is.GreaterThanOrEqualTo(0));
            var controller = (AnimatorController)descriptor.baseAnimationLayers.Single().animatorController; TrackControllerAssets(controller);
            Assert.That(controller.animationClips.SelectMany(AnimationUtility.GetCurveBindings).Any(b => b.propertyName == "blendShape.NativeSmile"), Is.False);
            Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(1));
        }
    }
}
