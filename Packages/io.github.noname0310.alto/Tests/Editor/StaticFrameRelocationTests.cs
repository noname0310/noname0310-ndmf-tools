using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private ObjectOptimizationState RelocationSettings()
        {
            var settings = ObjectSettings(); settings.MergeStaticObjects = true;
            return settings;
        }

        [TestCase(false)] [TestCase(true)]
        public void StaticFrameCollisionRelocatesDistinctAnimatedOutputsAtBothBoundaries(bool sdk)
        {
            var frame = PolicyChild("Container");
            var original = PolicyChild("Lamp").AddComponent<Light>();
            var moved = PolicyChild("Lamp", frame.transform).AddComponent<Light>();
            var clip = Clip();
            clip.SetCurve("Lamp", typeof(Light), "m_Intensity", AnimationCurve.Constant(0, 1, 2));
            clip.SetCurve("Container/Lamp", typeof(Light), "m_Intensity", AnimationCurve.Constant(0, 1, 7));
            var component = ObjectSettings().Component;
            component.HoistComponents = false; component.RemoveUnusedObjects = false;
            var context = ExpressionBuild(null, Controller(clip)); ObjectOptimizationPass.Capture(context);
            if (sdk)
            {
                AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
                AvatarOutputSdkFinalizer.Complete(root, value => Track(value));
            }
            else OptimizationSession.RunBoundary(context, "collision fixture", () => StaticTransformOptimizationPass.Apply(context));
            Assert.That(frame == null, Is.True);
            Assert.That(original.name, Is.EqualTo("Lamp"));
            Assert.That(moved.name, Does.StartWith("Lamp$ALTO$"));
            Assert.That(moved.transform.parent, Is.SameAs(root.transform));
            var controller = (AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController;
            TrackControllerAssets(controller);
            var built = controller.animationClips.Single(c => AnimationUtility.GetCurveBindings(c).Any(b => b.type == typeof(Light)));
            built.SampleAnimation(root, 0);
            Assert.That(original.intensity, Is.EqualTo(2)); Assert.That(moved.intensity, Is.EqualTo(7));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Select(b => b.path), Is.EquivalentTo(new[] { "Lamp", "Container/Lamp" }));
        }

        [TestCase("Binding")] [TestCase("Mask")] [TestCase("Behaviour")] [TestCase("Existing")]
        public void StaticFrameCollisionNamesAvoidExistingAndUnresolvedInterfaces(string reader)
        {
            var frame = PolicyChild("Container").transform;
            var moved = PolicyChild("Lamp", frame).AddComponent<Light>(); PolicyChild("Lamp");
            var first = FrameIndex();
            Assert.That(new HierarchyRelocation(first).CanFlatten(frame, new[] { moved.transform }, true, out var initial), Is.True);
            string occupiedName = initial.Names[moved.transform];
            string missing = occupiedName + "/Missing";
            var clip = reader == "Binding" ? StaticCurve(EditorCurveBinding.FloatCurve(missing, typeof(Light), "m_Intensity"), 3) : Clip();
            var source = Controller(clip);
            if (reader == "Behaviour") Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAnimatorPlayAudio>()).SourcePath = missing;
            if (reader == "Existing") PolicyChild(occupiedName);
            var index = AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController>
                { [VRCAvatarDescriptor.AnimLayerType.FX] = new CloneContext(VRChatPlatformAnimatorBindings.Instance).Clone(source) }, null);
            if (reader == "Mask")
            {
                var mask = Track(new AvatarMask()); mask.transformCount = 1; mask.SetTransformPath(0, missing); mask.SetTransformActive(0, false);
                index.Controllers.Single().Layers.First().AvatarMask = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(mask);
            }
            var relocation = new HierarchyRelocation(index);
            Assert.That(relocation.CanFlatten(frame, new[] { moved.transform }, true, out var plan), Is.True);
            Assert.That(plan.Names[moved.transform], Is.EqualTo(occupiedName + "$1"));
            Assert.That(relocation.CanFlatten(frame, new[] { moved.transform }, true, out var again), Is.True);
            Assert.That(again.Names[moved.transform], Is.EqualTo(plan.Names[moved.transform]), "Planning cannot consume names or depend on instance IDs");
            Assert.That(moved.name, Is.EqualTo("Lamp"), "Planning is read-only");
            Assert.That(Flatten(RelocationSettings(), index), Is.EqualTo(1));
            Assert.That(moved.name, Is.EqualTo(occupiedName + "$1"));
            Assert.That(root.transform.Find(missing), Is.Null);
        }

        [Test]
        public void StaticFrameCollisionRelocatesMasksAndBehaviourPaths()
        {
            var frame = PolicyChild("Container").transform;
            var moved = PolicyChild("Speaker", frame).AddComponent<AudioSource>(); PolicyChild("Speaker");
            var source = Controller(Clip());
            var behaviour = Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAnimatorPlayAudio>());
            behaviour.SourcePath = "Container/Speaker";
            var index = AvatarAnimationIndex.Create(root, new Dictionary<object, VirtualAnimatorController>
                { [VRCAvatarDescriptor.AnimLayerType.FX] = new CloneContext(VRChatPlatformAnimatorBindings.Instance).Clone(source) }, null);
            var mask = Track(new AvatarMask()); mask.transformCount = 1; mask.SetTransformPath(0, "Container/Speaker"); mask.SetTransformActive(0, false);
            index.Controllers.Single().Layers.First().AvatarMask = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(mask);
            Assert.That(Flatten(RelocationSettings(), index), Is.EqualTo(1));
            Assert.That(index.Controllers.Single().Layers.First().AvatarMask.Elements.Keys, Does.Contain(moved.name));
            Assert.That(ParameterSpecialization.Behaviours(index.Controllers.Single().AllReachableNodes()).OfType<VRCAnimatorPlayAudio>().Single().SourcePath,
                Is.EqualTo(moved.name));
            Assert.That(behaviour.SourcePath, Is.EqualTo("Container/Speaker"));
            Assert.That(mask.GetTransformPath(0), Is.EqualTo("Container/Speaker"));
        }

        [Test]
        public void StaticFrameCollisionRollsBackNamesWhenRemovalIsVetoed()
        {
            var frame = PolicyChild("Container").transform; frame.localPosition = new Vector3(.1f, .2f, .3f);
            var child = PolicyChild("Lamp", frame).AddComponent<Light>(); PolicyChild("Lamp");
            var index = FrameIndex(StaticCurve(EditorCurveBinding.FloatCurve("Container/Lamp", typeof(Light), "m_Intensity"), 4));
            var settings = RelocationSettings(); settings.DestroyObject = _ => { };
            var before = child.transform.localToWorldMatrix;
            var order = root.GetComponentsInChildren<Transform>(true);
            Assert.That(Flatten(settings, index), Is.Zero);
            Assert.That(root.GetComponentsInChildren<Transform>(true), Is.EqualTo(order));
            Assert.That(child.name, Is.EqualTo("Lamp")); Assert.That(child.transform.parent, Is.SameAs(frame));
            Assert.That(StaticTransformProof.Near(before, child.transform.localToWorldMatrix), Is.True);
            Assert.That(index.Controllers.Single().AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurveBindings().Single().path,
                Is.EqualTo("Container/Lamp"));
        }

        [Test]
        public void StaticFrameCollisionRetainsAmbiguousSourcePaths()
        {
            var frame = PolicyChild("Container").transform;
            PolicyChild("Lamp", frame).AddComponent<Light>(); PolicyChild("Lamp", frame).AddComponent<Light>();
            Assert.That(Flatten(RelocationSettings(), FrameIndex()), Is.Zero);
            Assert.That(frame.childCount, Is.EqualTo(2));
        }

        [TestCase("Uniform")] [TestCase("Offset")] [TestCase("Negative")]
        [TestCase("Nonuniform")] [TestCase("TransformTrack")] [TestCase("AnimatedSettings")]
        [TestCase("NestedBone")] [TestCase("UnknownVersion")] [TestCase("EnabledToggle")] [TestCase("PolarLimit")]
        public void PhysBoneFrameRelocationRequiresAnEquivalentSimulationFrame(string boundary)
        {
            var frame = PolicyChild("Frame").transform; frame.localRotation = Quaternion.Euler(30, 60, 10); frame.localScale = Vector3.one * 1.5f;
            var child = PolicyChild("Root", frame).transform; PolicyChild("Tip", child).transform.localPosition = Vector3.down * .1f;
            var bone = child.gameObject.AddComponent<VRCPhysBone>(); bone.version = VRCPhysBoneBase.Version.Version_1_1; bone.rootTransform = child;
            var clip = Clip();
            if (boundary == "Offset") frame.localPosition = Vector3.right * .1f;
            if (boundary == "Negative") frame.localScale = -Vector3.one;
            if (boundary == "Nonuniform") frame.localScale = new Vector3(1, 2, 1);
            if (boundary == "TransformTrack") clip.SetCurve("Frame/Root", typeof(Transform), "m_LocalRotation.x", AnimationCurve.Constant(0, 1, .1f));
            if (boundary == "AnimatedSettings") clip.SetCurve("Frame/Root", typeof(VRCPhysBone), "pull", AnimationCurve.Constant(0, 1, .5f));
            if (boundary == "NestedBone") bone.rootTransform = frame;
            if (boundary == "UnknownVersion") bone.version = (VRCPhysBoneBase.Version)123;
            if (boundary == "PolarLimit") bone.limitType = VRCPhysBoneBase.LimitType.Polar;
            if (boundary == "EnabledToggle") clip.SetCurve("Frame/Root", typeof(VRCPhysBone), "m_Enabled", AnimationCurve.Linear(0, 0, 1, 1));
            var index = FrameIndex(clip); var before = child.localToWorldMatrix;
            bool expected = boundary is "Uniform" or "EnabledToggle";
            Assert.That(Flatten(RelocationSettings(), index), Is.EqualTo(expected ? 1 : 0));
            Assert.That(frame == null, Is.EqualTo(expected));
            Assert.That(StaticTransformProof.Near(before, child.localToWorldMatrix), Is.True);
            Assert.That(bone.GetRootTransform(), Is.EqualTo(boundary == "NestedBone" ? frame : child));
        }

        [Test]
        public void PhysBoneFrameRelocationMatchesNativeSdkSimulation(
            [Values(10, 20, 60)] int fps,
            [Values(VRCPhysBoneBase.Version.Version_1_0, VRCPhysBoneBase.Version.Version_1_1)] VRCPhysBoneBase.Version version,
            [Values(VRCPhysBoneBase.LimitType.None, VRCPhysBoneBase.LimitType.Angle,
                VRCPhysBoneBase.LimitType.Hinge)] VRCPhysBoneBase.LimitType limit)
        {
            var result = ComparePhysBoneFrames(fps, version, limit, Vector3.zero);
            Assert.That(result.Motion, Is.GreaterThan(.02f), "The fixture must exercise a moving simulation");
            Assert.That(result.Position, Is.LessThan(2e-5f));
            Assert.That(result.Rotation, Is.LessThan(2e-6f));
        }

        [TestCase("SmallScale")] [TestCase("AncestorScale")] [TestCase("ImmobileWorld")]
        [TestCase("ImmobileAll")] [TestCase("Advanced")] [TestCase("Animated")]
        public void PhysBoneFrameRelocationMatchesNativeSdkWithAdditionalSettings(string configuration)
        {
            var result = ComparePhysBoneFrames(20, VRCPhysBoneBase.Version.Version_1_1, VRCPhysBoneBase.LimitType.Angle,
                Vector3.zero, configuration);
            Assert.That(result.Motion, Is.GreaterThan(.02f));
            Assert.That(result.Position, Is.LessThan(2e-5f));
            Assert.That(result.Rotation, Is.LessThan(2e-6f));
        }

        [TestCase(10)] [TestCase(20)]
        public void PhysBoneTranslatedParentChangesNativeSubstepInterpolation(int fps)
        {
            var result = ComparePhysBoneFrames(fps, VRCPhysBoneBase.Version.Version_1_1, VRCPhysBoneBase.LimitType.Angle,
                new Vector3(.06f, .01f, .026f));
            Assert.That(result.Position, Is.GreaterThan(5e-5f), "Equal rest matrices alone do not prove equivalent dynamics");
        }

        private (float Position, float Rotation, float Motion) ComparePhysBoneFrames(int fps,
            VRCPhysBoneBase.Version version, VRCPhysBoneBase.LimitType limit, Vector3 offset, string configuration = null)
        {
            Assert.That(Application.isPlaying, Is.False);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var schedule = typeof(PhysBoneManager).GetMethod("ScheduleExecutionJob", flags);
            var establish = typeof(VRCPhysBoneBase).GetMethod("ReEstablishPhysBoneRoot", flags);
            var previous = PhysBoneManager.Inst; bool timing = PhysBoneManager.DisableTiming; float step = PhysBoneManager.DebugTimeElapsed;
            var owner = new GameObject("PhysBone comparison manager") { hideFlags = HideFlags.HideAndDontSave };
            var holder = new GameObject("PhysBone comparison") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(owner, scene);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(holder, scene);
            var manager = owner.AddComponent<PhysBoneManager>(); manager.enabled = false; manager.IsSDK = true;
            PhysBoneManager.Inst = manager; manager.Init();
            PhysBoneManager.DisableTiming = true; PhysBoneManager.DebugTimeElapsed = 1f / fps;
            try
            {
                var pair = new Transform[8]; var bones = new List<VRCPhysBone>();
                for (int variant = 0; variant < 2; variant++)
                {
                    var head = new GameObject("Head " + variant).transform; head.SetParent(holder.transform, false);
                    var frame = new GameObject("Frame").transform; frame.SetParent(head, false);
                    frame.localPosition = offset; frame.localRotation = Quaternion.Euler(25, 60, 15); frame.localScale = Vector3.one * 1.7f;
                    if (configuration == "SmallScale") frame.localScale = Vector3.one * .0044f;
                    var first = new GameObject("Root").transform; first.SetParent(frame, false);
                    first.localPosition = new Vector3(.02f, 0, 0); first.localRotation = Quaternion.Euler(10, 20, 15);
                    var middle = new GameObject("Middle").transform; middle.SetParent(first, false); middle.localPosition = Vector3.down * .07f;
                    var tip = new GameObject("Tip").transform; tip.SetParent(middle, false); tip.localPosition = Vector3.down * .05f;
                    if (configuration == "SmallScale")
                    {
                        first.localPosition *= 1.7f / .0044f; middle.localPosition *= 1.7f / .0044f; tip.localPosition *= 1.7f / .0044f;
                    }
                    if (variant == 1)
                    {
                        Assert.That(StaticTransformProof.TryCompose(frame, first, out var position, out var rotation, out var scale), Is.True);
                        first.SetParent(head, false); first.localPosition = position; first.localRotation = rotation; first.localScale = scale;
                        Object.DestroyImmediate(frame.gameObject);
                    }
                    var bone = first.gameObject.AddComponent<VRCPhysBone>(); bone.version = version; bone.rootTransform = first;
                    bone.gravity = .3f; bone.pull = .2f; bone.spring = .35f; bone.stiffness = .2f; bone.immobile = 0;
                    bone.allowGrabbing = bone.allowPosing = bone.allowCollision = VRCPhysBoneBase.AdvancedBool.False;
                    bone.limitType = limit; bone.maxAngleX = 35;
                    if (configuration == "ImmobileWorld") { bone.immobileType = VRCPhysBoneBase.ImmobileType.World; bone.immobile = .5f; }
                    if (configuration == "ImmobileAll") bone.immobile = .5f;
                    if (configuration == "Advanced") bone.integrationType = VRCPhysBoneBase.IntegrationType.Advanced;
                    if (configuration == "Animated") bone.isAnimated = true;
                    establish.Invoke(bone, null); bone.Init(); bones.Add(bone);
                    pair[variant * 4] = head; pair[variant * 4 + 1] = first; pair[variant * 4 + 2] = middle; pair[variant * 4 + 3] = tip;
                }
                float positionError = 0, rotationError = 0, motion = 0; var start = pair[3].position;
                for (int frame = 0; frame < fps * 6; frame++)
                {
                    float time = (float)frame / fps;
                    for (int variant = 0; variant < 2; variant++)
                    {
                        pair[variant * 4].localPosition = new Vector3(Mathf.Sin(time * 4) * .04f, Mathf.Cos(time * 3) * .02f, 0);
                        pair[variant * 4].localRotation = Quaternion.Euler(Mathf.Sin(time * 3) * 40, Mathf.Sin(time * 4.7f) * 55, Mathf.Cos(time * 2) * 20);
                        if (configuration == "AncestorScale") pair[variant * 4].localScale = new Vector3(1 + Mathf.Sin(time) * .2f, .8f, 1.3f);
                    }
                    ((Unity.Jobs.JobHandle)schedule.Invoke(manager, new object[] { default(Unity.Jobs.JobHandle) })).Complete();
                    for (int i = 1; i < 4; i++)
                    {
                        positionError = Mathf.Max(positionError, Vector3.Distance(pair[i].position, pair[i + 4].position));
                        rotationError = Mathf.Max(rotationError, 1 - Mathf.Abs(Quaternion.Dot(pair[i].rotation, pair[i + 4].rotation)));
                    }
                    motion = Mathf.Max(motion, Vector3.Distance(pair[3].position, start));
                }
                Assert.That(bones.All(manager.HasPhysBone), Is.True, "Both chains must be registered in the native solver");
                TestContext.WriteLine("Native PhysBone comparison: {0} FPS, {1}, {2}, {3}; max position error {4:R} m, rotation dot error {5:R}",
                    fps, version, limit, configuration ?? "default", positionError, rotationError);
                return (positionError, rotationError, motion);
            }
            finally
            {
                Object.DestroyImmediate(holder); manager.Dispose(); Object.DestroyImmediate(owner);
                PhysBoneManager.Inst = previous; PhysBoneManager.DisableTiming = timing; PhysBoneManager.DebugTimeElapsed = step;
            }
        }
    }
}
