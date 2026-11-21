using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using Noname.AvatarTools.Physics;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private AvatarAnimationIndex FrameIndex(params AnimationClip[] clips) => AvatarAnimationIndex.Create(root,
            new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] =
                new CloneContext(VRChatPlatformAnimatorBindings.Instance).Clone(Controller(clips.Length == 0 ? new[] { Clip() } : clips)) }, null);

        private int Flatten(ObjectOptimizationState settings, AvatarAnimationIndex index) => StaticObjectMerging.Apply(root, settings, index,
            AvatarDependencyGraph.Create(root, index, settings, false), new StaticTransformProof(root, index, settings), new List<string>());

        [TestCase("Shear")] [TestCase("Physics")] [TestCase("Animation")]
        public void StaticFramePartialFlattenMovesIndependentBranchesAndRetainsBlockedSiblings(string boundary)
        {
            var frame = PolicyChild("Frame").transform;
            frame.localPosition = new Vector3(.2f, .3f, .4f); frame.localScale = new Vector3(1, 2, 1);
            var stable = PolicyChild("Lamp", frame).AddComponent<Light>();
            var blocked = PolicyChild("Blocked", frame).transform;
            if (boundary == "Shear") blocked.localRotation = Quaternion.Euler(30, 50, 0);
            if (boundary == "Physics") { blocked.gameObject.AddComponent<VRCPhysBone>(); PolicyChild("Tip", blocked); }
            var clip = StaticCurve(EditorCurveBinding.FloatCurve("Frame/Lamp", typeof(Light), "m_Intensity"), 2);
            if (boundary == "Animation") clip.SetCurve("Frame/Blocked", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 0, 1, 1));
            var index = FrameIndex(clip); var settings = ObjectSettings(); settings.MergeStaticObjects = true;
            var before = stable.transform.localToWorldMatrix; var blockedBefore = blocked.localToWorldMatrix;
            Assert.That(Flatten(settings, index), Is.EqualTo(1));
            Assert.That(frame != null, Is.True); Assert.That(blocked.parent, Is.SameAs(frame));
            Assert.That(stable.transform.parent, Is.SameAs(root.transform));
            Assert.That(StaticTransformProof.Near(before, stable.transform.localToWorldMatrix), Is.True);
            Assert.That(StaticTransformProof.Near(blockedBefore, blocked.localToWorldMatrix), Is.True);
            var builtClip = index.Controllers.Single().AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(builtClip.GetFloatCurveBindings().Single(b => b.type == typeof(Light)).path, Is.EqualTo("Lamp"));
            if (boundary == "Animation") Assert.That(builtClip.GetFloatCurveBindings().Where(b => b.type == typeof(Transform)).Select(b => b.path), Has.All.EqualTo("Frame/Blocked"));
            Assert.That(Flatten(settings, AvatarAnimationIndex.Create(root, index.ControllerEntries, null)), Is.Zero);
            Assert.That(AnimationUtility.GetCurveBindings(clip).Single(b => b.type == typeof(Light)).path, Is.EqualTo("Frame/Lamp"));
        }

        [Test]
        public void StaticFramePartialFlattenPreservesInheritedMaskBoundaries()
        {
            var frame = PolicyChild("Masked frame").transform; frame.localScale = new Vector3(1, 2, 1);
            var stable = PolicyChild("Lamp", frame).AddComponent<Light>();
            var blocked = PolicyChild("Sheared", frame).transform; blocked.localRotation = Quaternion.Euler(30, 50, 0);
            var index = FrameIndex(); var mask = Track(new AvatarMask());
            mask.transformCount = 1; mask.SetTransformPath(0, "Masked frame"); mask.SetTransformActive(0, false);
            index.Controllers.Single().Layers.First().AvatarMask = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(mask);
            Assert.That(Flatten(ObjectSettings(), index), Is.Zero);
            Assert.That(stable.transform.parent, Is.SameAs(frame));
        }

        [Test]
        public void StaticFrameRebindingDoesNotRequireBatchableMaterials()
        {
            var bone = PolicyChild("Static helper").transform;
            bone.localPosition = new Vector3(.2f, -.3f, .4f); bone.localScale = new Vector3(1.2f, .8f, 0);
            var part = AssemblyPart("Transparent renderer", new[] { bone }, Vector3.zero);
            part.sharedMaterials = Array.Empty<Material>(); // Ineligible for assembly, eligible for palette substitution.
            var original = part.sharedMesh; var weights = original.boneWeights; var indices = original.triangles;
            var before = AssemblyWorldVertices(part);
            var settings = new MeshOptimizationState { Scope = new OptimizationBuildScope { Root = root.transform }, RebindStaticBones = true };
            var objects = ObjectSettings(); var index = FrameIndex(); var references = AvatarDependencyGraph.Create(root, index, objects, false);
            Assert.That(StaticSkinningOptimization.Apply(root, settings, objects, index, references,
                new StaticTransformProof(root, index, objects), m => Track(m)), Is.EqualTo(1));
            Assert.That(part.bones, Is.EqualTo(new[] { root.transform }));
            Assert.That(part.sharedMesh.boneWeights, Is.EqualTo(weights)); Assert.That(part.sharedMesh.triangles, Is.EqualTo(indices));
            var after = AssemblyWorldVertices(part);
            for (int i = 0; i < before.Length; i++) Assert.That(Vector3.Distance(before[i], after[i]), Is.LessThan(1e-5));
            Assert.That(StaticSkinningOptimization.Apply(root, settings, objects, index, references,
                new StaticTransformProof(root, index, objects), m => Track(m)), Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void StaticFrameColliderRebasePreservesShapeAndToggleIdentity(bool nonuniform)
        {
            var frame = PolicyChild("Geometry frame").transform;
            frame.localPosition = new Vector3(.2f, .3f, .4f); frame.localScale = nonuniform ? new Vector3(1, 2, 1) : Vector3.one * 2;
            var collider = PolicyChild("Toggle owner").AddComponent<VRCPhysBoneCollider>();
            collider.rootTransform = frame; collider.position = new Vector3(.1f, .2f, .3f); collider.radius = .2f; collider.height = .6f;
            var bone = PolicyChild("Simulation").AddComponent<VRCPhysBone>(); bone.colliders.Add(collider);
            var center = frame.TransformPoint(collider.position); var radius = collider.radius * frame.lossyScale.x;
            var index = FrameIndex(StaticCurve(EditorCurveBinding.FloatCurve("Toggle owner", typeof(VRCPhysBoneCollider), "m_Enabled"), 0));
            var objects = ObjectSettings();
            var settings = new PhysBoneOptimizationState { Scope = objects.Scope, RebaseColliderRoots = true };
            int count = StaticColliderRoots.Apply(root, settings, objects, index, AvatarDependencyGraph.Create(root, index, objects, false),
                new StaticTransformProof(root, index, objects), new List<string>());
            Assert.That(count, Is.EqualTo(nonuniform ? 0 : 1));
            Assert.That(collider.GetRootTransform(), Is.EqualTo(nonuniform ? frame : root.transform));
            Assert.That(Vector3.Distance(center, collider.GetRootTransform().TransformPoint(collider.position)), Is.LessThan(1e-6));
            Assert.That(collider.radius * collider.GetRootTransform().lossyScale.x, Is.EqualTo(radius).Within(1e-6));
            Assert.That(bone.colliders.Single(), Is.SameAs(collider)); Assert.That(collider.gameObject.name, Is.EqualTo("Toggle owner"));
            Assert.That(index.ForObject(collider).Single().Binding.path, Is.EqualTo("Toggle owner"));
        }

        [TestCase(false)] [TestCase(true)]
        public void StaticFrameFlattenRelocatesAnimatedDescendantsAtBothBoundaries(bool sdk)
        {
            var grouping = PolicyChild("Container"); var child = PolicyChild("Animated output", grouping.transform);
            child.AddComponent<Light>();
            var binding = EditorCurveBinding.FloatCurve("Container/Animated output", typeof(Transform), "m_LocalPosition.x");
            var clip = StaticCurve(binding, .75f);
            var component = ObjectSettings().Component;
            component.HoistComponents = false; component.RemoveUnusedObjects = false;
            var context = ExpressionBuild(null, Controller(clip)); ObjectOptimizationPass.Capture(context);
            if (sdk)
            {
                AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
                AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            }
            else OptimizationSession.RunBoundary(context, "static frame fixture", () => StaticTransformOptimizationPass.Apply(context));
            Assert.That(grouping == null, Is.True); Assert.That(child.transform.parent, Is.EqualTo(root.transform));
            var built = (UnityEditor.Animations.AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController;
            TrackControllerAssets(built);
            var output = built.animationClips.First(c => AnimationUtility.GetCurveBindings(c).Any(b => b.type == typeof(Transform)));
            var relocated = AnimationUtility.GetCurveBindings(output).Single(b => b.type == typeof(Transform));
            Assert.That(relocated.path, Is.EqualTo("Animated output"));
            output.SampleAnimation(root, 0);
            Assert.That(child.transform.position.x, Is.EqualTo(.75f).Within(1e-6));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Single().path, Is.EqualTo(binding.path), "Source clip is unchanged");
        }

        [TestCase("None")] [TestCase("Active")] [TestCase("Preserved")] [TestCase("Collision")]
        [TestCase("MissingBinding")] [TestCase("GroupMask")] [TestCase("Shear")] [TestCase("DeleteVeto")]
        public void StaticFrameFlattenRespectsObservableBoundaries(string boundary)
        {
            var grouping = PolicyChild("Container"); var child = PolicyChild("Leaf", grouping.transform);
            child.AddComponent<Light>();
            grouping.transform.localRotation = Quaternion.Euler(0, 0, 90); grouping.transform.localPosition = new Vector3(1, 2, 3);
            child.transform.localPosition = Vector3.right;
            var before = child.transform.localToWorldMatrix;
            var settings = ObjectSettings(); settings.MergeStaticObjects = true;
            var clip = boundary == "MissingBinding" ? StaticCurve(EditorCurveBinding.FloatCurve("Leaf", typeof(Light), "m_Intensity"), 2) : Clip();
            if (boundary == "Active") clip = StaticCurve(EditorCurveBinding.FloatCurve("Container", typeof(GameObject), "m_IsActive"), 0);
            if (boundary == "Collision") PolicyChild("Leaf");
            if (boundary == "Preserved") settings.ProtectedComponents.Add(ObjectRegistry.GetReference(grouping.transform));
            if (boundary == "Shear") { grouping.transform.localScale = new Vector3(1, 2, 1); child.transform.localRotation = Quaternion.Euler(30, 50, 0); }
            if (boundary == "DeleteVeto") settings.DestroyObject = _ => { };
            var index = FrameIndex(clip);
            if (boundary == "GroupMask")
            {
                var mask = Track(new AvatarMask()); mask.transformCount = 1; mask.SetTransformPath(0, "Container"); mask.SetTransformActive(0, true);
                index.Controllers.Single().Layers.First().AvatarMask = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(mask);
            }
            var oldParent = child.transform.parent; var oldPosition = child.transform.localPosition;
            int count = Flatten(settings, index);
            Assert.That(count, Is.EqualTo(boundary is "None" or "Collision" ? 1 : 0), boundary);
            if (boundary == "Collision") Assert.That(child.name, Does.StartWith("Leaf$ALTO$"));
            if (count == 0) { Assert.That(child.transform.parent, Is.EqualTo(oldParent)); Assert.That(child.transform.localPosition, Is.EqualTo(oldPosition)); }
            else Assert.That(StaticTransformProof.Near(before, child.transform.localToWorldMatrix), Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void StaticFrameHoistingPreservesContactAndRewritesEnableBindings(bool occupiedAnimatedType)
        {
            var holder = PolicyChild("Contact owner"); var source = holder.AddComponent<VRCContactSender>();
            source.position = new Vector3(.1f, .2f, .3f); source.radius = .25f; source.collisionTags = new List<string> { "Hand" };
            var clip = StaticCurve(EditorCurveBinding.FloatCurve("Contact owner", typeof(VRCContactSender), "m_Enabled"), 0);
            if (occupiedAnimatedType) root.AddComponent<VRCContactSender>();
            var context = ExpressionBuild(null, Controller(clip)); ObjectSettings(); ObjectOptimizationPass.Capture(context);
            var index = FrameIndex(clip); var settings = context.GetState<ObjectOptimizationState>();
            int count = ComponentHoisting.Apply(context, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings), t => AnimationUtility.CalculateTransformPath(t, root.transform));
            Assert.That(count, Is.EqualTo(occupiedAnimatedType ? 0 : 1));
            if (occupiedAnimatedType) { Assert.That(source != null, Is.True); return; }
            Assert.That(source == null, Is.True);
            var moved = root.GetComponent<VRCContactSender>();
            Assert.That(moved.rootTransform, Is.EqualTo(root.transform)); Assert.That(moved.radius, Is.EqualTo(.25f));
            Assert.That(moved.position, Is.EqualTo(new Vector3(.1f, .2f, .3f))); Assert.That(moved.collisionTags, Is.EqualTo(new[] { "Hand" }));
            Assert.That(index.Controllers.Single().AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurveBindings().Single().path, Is.Empty);
        }

        [Test]
        public void StaticFrameHoistingDistinguishesNativeUniquenessFromUnanimatedMultiplicity()
        {
            var index = FrameIndex();
            var contact = root.AddComponent<VRCContactSender>();
            Assert.That(ComponentHoisting.CanCoexist(typeof(VRCContactSender), new[] { contact }, false, index), Is.True);
            Assert.That(ComponentHoisting.CanCoexist(typeof(VRCContactSender), new[] { contact }, true, index), Is.False);
            Assert.That(ComponentHoisting.CanCoexist(typeof(MeshRenderer), new[] { renderer }, false, index), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void StaticFrameHoistingBatchesIndependentContactsAndReservesNewBindingDestinations(bool animated)
        {
            var senders = Enumerable.Range(0, 8).Select(i => PolicyChild("Sender " + i).AddComponent<VRCContactSender>()).ToArray();
            var clip = animated ? StaticCurve(EditorCurveBinding.FloatCurve("Sender 0", typeof(VRCContactSender), "m_Enabled"), 0) : Clip();
            var context = ExpressionBuild(null, Controller(clip)); ObjectSettings(); ObjectOptimizationPass.Capture(context);
            var index = FrameIndex(clip); var settings = context.GetState<ObjectOptimizationState>();
            int count = ComponentHoisting.Apply(context, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings), t => AnimationUtility.CalculateTransformPath(t, root.transform));
            Assert.That(count, Is.EqualTo(animated ? 1 : 8));
            Assert.That(root.GetComponents<VRCContactSender>().Length, Is.EqualTo(count));
            Assert.That(senders.Count(s => s != null), Is.EqualTo(8 - count));
        }

        [Test]
        public void StaticFrameHoistingUpdatesReadersThatWereAlreadyMovedInTheSameBatch()
        {
            var simulation = PolicyChild("Simulation root").transform;
            var bone = PolicyChild("Physics owner").AddComponent<VRCPhysBone>(); bone.rootTransform = simulation;
            var collider = PolicyChild("Collider owner").AddComponent<VRCPhysBoneCollider>(); bone.colliders.Add(collider);
            var context = ExpressionBuild(null, Controller(Clip())); ObjectSettings(); ObjectOptimizationPass.Capture(context);
            var index = FrameIndex(); var settings = context.GetState<ObjectOptimizationState>();
            Assert.That(ComponentHoisting.Apply(context, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings), t => AnimationUtility.CalculateTransformPath(t, root.transform)), Is.EqualTo(2));
            var movedBone = root.GetComponent<VRCPhysBone>(); var movedCollider = root.GetComponent<VRCPhysBoneCollider>();
            Assert.That(movedBone.rootTransform, Is.EqualTo(simulation));
            Assert.That(movedBone.colliders.Single(), Is.SameAs(movedCollider));
            Assert.That(bone == null && collider == null, Is.True);
        }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void StaticFrameHoistingPreservesCompetingSensorOutputOrder(bool shared, bool material)
        {
            var first = PolicyChild("First receiver").AddComponent<VRCContactReceiver>();
            var second = PolicyChild("Second receiver").AddComponent<VRCContactReceiver>();
            first.parameter = "First"; second.parameter = shared && !material ? "First" : "Second";
            if (material)
            {
                first.writeToMaterials = second.writeToMaterials = true;
                first.shaderTargetRenderers = second.shaderTargetRenderers = new Renderer[] { renderer };
                first.separateShaderParameter = "Surface"; second.separateShaderParameter = shared ? "Surface" : "OtherSurface";
            }
            var context = ExpressionBuild(null, Controller(Clip())); ObjectSettings(); ObjectOptimizationPass.Capture(context);
            var index = FrameIndex(); var settings = context.GetState<ObjectOptimizationState>();
            Assert.That(ComponentHoisting.Apply(context, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings), t => AnimationUtility.CalculateTransformPath(t, root.transform)), Is.EqualTo(shared ? 0 : 2));
            Assert.That(root.GetComponents<VRCContactReceiver>().Length, Is.EqualTo(shared ? 0 : 2));
        }

        [TestCase(false)] [TestCase(true)]
        public void StaticFrameHoistingPreservesCompetingConstraintWriterOrder(bool shared)
        {
            var input = PolicyChild("Input").transform; var unused = PolicyChild("Unused").transform;
            var firstTarget = PolicyChild("First target").transform;
            var secondTarget = shared ? firstTarget : PolicyChild("Second target").transform;
            var first = ConstraintWithZeroSources(typeof(VRCPositionConstraint), input, unused, firstTarget, "First writer");
            var second = ConstraintWithZeroSources(typeof(VRCPositionConstraint), input, unused, secondTarget, "Second writer");
            var context = ExpressionBuild(null, Controller(Clip())); ObjectSettings(); ObjectOptimizationPass.Capture(context);
            var index = FrameIndex(); var settings = context.GetState<ObjectOptimizationState>();
            Assert.That(ComponentHoisting.Apply(context, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings), t => AnimationUtility.CalculateTransformPath(t, root.transform)), Is.EqualTo(shared ? 0 : 2));
            if (!shared) Assert.That(root.GetComponents<VRCPositionConstraint>().Select(c => c.TargetTransform), Is.EquivalentTo(new[] { firstTarget, secondTarget }));
            else Assert.That(first != null && second != null, Is.True);
        }

        [TestCase(false)] [TestCase(true)]
        public void StaticFrameHoistingHonorsMasterAndPerTypeSwitches(bool master)
        {
            var source = PolicyChild("Sender").AddComponent<VRCContactSender>();
            var context = ExpressionBuild(null, Controller(Clip())); ObjectSettings(); ObjectOptimizationPass.Capture(context);
            var settings = context.GetState<ObjectOptimizationState>();
            if (master) settings.HoistComponents = false; else settings.ComponentHoisting.ContactSenders = false;
            var index = FrameIndex();
            Assert.That(ComponentHoisting.Apply(context, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings), t => AnimationUtility.CalculateTransformPath(t, root.transform)), Is.Zero);
            Assert.That(source != null, Is.True);
        }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void StaticFrameClosureConvergesWithFreshPathsAcrossDeepHierarchies(bool sdk, bool hoist)
        {
            var parent = root.transform;
            for (int i = 0; i < 6; i++) parent = PolicyChild("Group " + i, parent).transform;
            var leaf = PolicyChild("Output", parent);
            if (hoist) leaf.AddComponent<VRCContactSender>(); else leaf.AddComponent<Light>();
            var binding = EditorCurveBinding.FloatCurve(AnimationUtility.CalculateTransformPath(leaf.transform, root.transform),
                hoist ? typeof(VRCContactSender) : typeof(Light), hoist ? "m_Enabled" : "m_Intensity");
            var clip = StaticCurve(binding, .75f);
            var component = ObjectSettings().Component;
            component.HoistComponents = hoist; component.RemoveUnusedObjects = false;
            var context = ExpressionBuild(null, Controller(clip)); ObjectOptimizationPass.Capture(context);
            if (sdk)
            {
                AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
                AvatarOutputSdkFinalizer.Complete(root, o => Track(o));
            }
            else OptimizationSession.RunBoundary(context, "deep static frame fixture", () => StaticTransformOptimizationPass.Apply(context));
            var built = (UnityEditor.Animations.AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController;
            TrackControllerAssets(built);
            var output = built.animationClips.First(c => AnimationUtility.GetCurveBindings(c).Any(b => b.type == binding.type));
            var relocated = AnimationUtility.GetCurveBindings(output).Single(b => b.type == binding.type);
            Assert.That(relocated.path, Is.EqualTo(hoist ? "" : "Output"));
            if (hoist) Assert.That(root.GetComponent<VRCContactSender>() != null, Is.True);
            else { output.SampleAnimation(root, 0); Assert.That(leaf.GetComponent<Light>().intensity, Is.EqualTo(.75f)); }
            Assert.That(AnimationUtility.GetCurveBindings(clip).Single().path, Is.EqualTo(binding.path));
        }

        [TestCase(null)] [TestCase("")] [TestCase("Container/Speaker")]
        public void StaticFrameRelocatesAudioBehaviourPathsIncludingEmptyDefaults(string sourcePath)
        {
            var container = PolicyChild("Container"); PolicyChild("Speaker", container.transform).AddComponent<AudioSource>();
            var controller = Controller(Clip());
            var audio = Track(controller.layers[0].stateMachine.states[0].state.AddStateMachineBehaviour<VRCAnimatorPlayAudio>());
            audio.SourcePath = sourcePath; string originalPath = audio.SourcePath;
            var index = AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController> {
                [VRCAvatarDescriptor.AnimLayerType.FX] = new CloneContext(VRChatPlatformAnimatorBindings.Instance).Clone(controller) }, null);
            var settings = ObjectSettings(); settings.MergeStaticObjects = true;
            Assert.That(Flatten(settings, index), Is.EqualTo(1));
            var moved = ParameterSpecialization.Behaviours(index.Controllers.Single().AllReachableNodes()).OfType<VRCAnimatorPlayAudio>().Single();
            Assert.That(moved.SourcePath, Is.EqualTo(string.IsNullOrEmpty(sourcePath) ? "" : "Speaker"));
            Assert.That(audio.SourcePath, Is.EqualTo(originalPath));
        }

        [TestCase(null)] [TestCase("")] [TestCase("Speaker")]
        public void StaticFrameAudioHoistingDoesNotActivateMissingBehaviourTargets(string sourcePath)
        {
            var speaker = PolicyChild("Speaker").AddComponent<AudioSource>();
            var controller = Controller(Clip());
            var audio = Track(controller.layers[0].stateMachine.states[0].state.AddStateMachineBehaviour<VRCAnimatorPlayAudio>());
            audio.SourcePath = sourcePath;
            var context = ExpressionBuild(null, controller); ObjectSettings(); ObjectOptimizationPass.Capture(context);
            var index = AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController> {
                [VRCAvatarDescriptor.AnimLayerType.FX] = new CloneContext(VRChatPlatformAnimatorBindings.Instance).Clone(controller) }, null);
            var settings = context.GetState<ObjectOptimizationState>();
            int count = ComponentHoisting.Apply(context, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings), t => AnimationUtility.CalculateTransformPath(t, root.transform));
            Assert.That(count, Is.EqualTo(string.IsNullOrEmpty(sourcePath) ? 0 : 1));
            Assert.That(speaker != null, Is.EqualTo(string.IsNullOrEmpty(sourcePath)));
        }

        [Test]
        public void StaticFrameWorklistRevisitsDependenciesAndInvalidatesOnFailure()
        {
            int state = 0, objectRuns = 0, physicsRuns = 0, shaderRuns = 0;
            var invalidated = new List<AnalysisDomain>();
            int rounds = OptimizationWorklist.Run(new[]
            {
                new OptimizationWorklist.Stage("objects", AnalysisDomain.Physics, AnalysisDomain.Hierarchy, () => { objectRuns++; if (state != 1) return 0; state = 2; return 1; }),
                new OptimizationWorklist.Stage("physics", AnalysisDomain.Hierarchy, AnalysisDomain.Physics, () => { physicsRuns++; if (state != 0) return 0; state = 1; return 1; }),
                new OptimizationWorklist.Stage("shader source", AnalysisDomain.ShaderSources, AnalysisDomain.None, () => { shaderRuns++; return 0; })
            }, invalidated.Add);
            Assert.That(rounds, Is.EqualTo(2)); Assert.That(state, Is.EqualTo(2)); Assert.That(shaderRuns, Is.EqualTo(1));
            Assert.That(objectRuns, Is.EqualTo(2)); Assert.That(physicsRuns, Is.EqualTo(2));
            Assert.Throws<InvalidOperationException>(() => OptimizationWorklist.Run(new[] {
                new OptimizationWorklist.Stage("failure", AnalysisDomain.All, AnalysisDomain.Meshes, () => throw new InvalidOperationException()) }, invalidated.Add));
            Assert.That(invalidated.Last(), Is.EqualTo(AnalysisDomain.Meshes));
        }
    }
}
