using System;
using System.Linq;
using System.Collections.Generic;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false)] [TestCase(true)]
        public void SpatialFramePhysBoneExclusionsRefineWritersAndLivenessWithoutErasingBoundaries(bool animated)
        {
            var settings = ObjectSettings(); var simulation = PolicyChild("Simulation").AddComponent<VRCPhysBone>();
            var excluded = PolicyChild("Excluded", simulation.transform).transform;
            var tip = PolicyChild("Tip", excluded).transform;
            var included = PolicyChild("Included", simulation.transform).transform;
            simulation.ignoreTransforms.Add(excluded); simulation.ignoreTransforms.Add(simulation.transform);
            var clip = Clip();
            if (animated) AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("Simulation", typeof(VRCPhysBone), "ignoreTransforms.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = included } });
            var index = FrameIndex(clip); var mutations = TransformMutationAnalysis.Create(root, index);
            Assert.That(mutations.Mutable.Contains(simulation.transform), Is.True, "The SDK always includes its root.");
            Assert.That(mutations.Mutable.Contains(included), Is.True);
            Assert.That(mutations.Mutable.Contains(excluded), Is.EqualTo(animated));
            Assert.That(mutations.Mutable.Contains(tip), Is.EqualTo(animated));
            var graph = AvatarDependencyGraph.Create(root, index, settings, true);
            Assert.That(graph.ReferenceOwners.ContainsKey(excluded), Is.True, "Keep a live exclusion boundary from flattening.");
            Assert.That(graph.DependsOn(simulation, tip), Is.EqualTo(animated));
            Assert.That(graph.IsLive(tip), Is.EqualTo(animated));
        }

        [Test]
        public void SpatialFrameParticleSurfaceReaderAllowsPaletteSubstitutionWithoutChangingGeometry()
        {
            var settings = ObjectSettings(); var parent = PolicyChild("Moving parent").transform;
            var bone = PolicyChild("Static helper", parent).transform;
            bone.localPosition = new Vector3(.2f, .4f, -.3f); bone.localRotation = Quaternion.Euler(25, 42, 13);
            var part = AssemblyPart("Surface", new[] { bone }, Vector3.zero);
            var original = part.sharedMesh;
            var particles = PolicyChild("Emitter").AddComponent<ParticleSystem>();
            var shape = particles.shape; shape.shapeType = ParticleSystemShapeType.SkinnedMeshRenderer; shape.skinnedMeshRenderer = part;
            var clip = StaticCurve(EditorCurveBinding.FloatCurve("Moving parent", typeof(Transform), "m_LocalPosition.x"), 1);
            var index = FrameIndex(clip); var graph = AvatarDependencyGraph.Create(root, index, settings, false);
            var expected = AssemblyWorldVertices(part);
            var meshes = new MeshOptimizationState { Scope = settings.Scope, RebindStaticBones = true };
            Assert.That(StaticSkinningOptimization.Apply(root, meshes, settings, index, graph,
                new StaticTransformProof(root, index, settings), m => Track(m)), Is.EqualTo(1));
            Assert.That(shape.skinnedMeshRenderer, Is.SameAs(part)); Assert.That(part.bones.Single(), Is.SameAs(parent));
            Assert.That(part.sharedMesh.vertices, Is.EqualTo(original.vertices));
            Assert.That(part.sharedMesh.triangles, Is.EqualTo(original.triangles));
            Assert.That(part.sharedMesh.boneWeights, Is.EqualTo(original.boneWeights));
            Assert.That(part.sharedMesh.blendShapeCount, Is.EqualTo(original.blendShapeCount));
            var reference = AssemblyPart("Reference", new[] { bone }, Vector3.zero); reference.sharedMesh = original;
            foreach (var scale in new[] { Vector3.one, new Vector3(2, 3, 4), Vector3.one * .2f })
            {
                parent.localScale = scale; parent.localRotation *= Quaternion.Euler(15, 55, -5); parent.localPosition += Vector3.one;
                var before = AssemblyWorldVertices(reference); var after = AssemblyWorldVertices(part);
                for (int i = 0; i < after.Length; i++) Assert.That(Vector3.Distance(before[i], after[i]), Is.LessThan(1e-5));
            }
        }

        private int HoistSpatial(AvatarAnimationIndex index, bool enabled = true)
        {
            var component = ObjectSettings().Component; component.FoldStaticTransformsIntoComponents = enabled;
            var context = ExpressionBuild(null, Controller(Clip())); ObjectOptimizationPass.Capture(context);
            var settings = context.GetState<ObjectOptimizationState>();
            return ComponentHoisting.Apply(context, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings), t => AnimationUtility.CalculateTransformPath(t, root.transform));
        }

        [TestCase(1e-7f, false)] [TestCase(1e-7f, true)]
        [TestCase(1f, false)] [TestCase(1f, true)]
        [TestCase(1e7f, false)] [TestCase(1e7f, true)]
        public void SpatialFrameReceiverHoistingPreservesNativeRadiusAtEveryScale(float scale, bool nonuniform)
        {
            var frame = PolicyChild("Frame").transform;
            frame.localScale = new Vector3(scale, nonuniform ? scale * 9 : scale, scale);
            var receiver = frame.gameObject.AddComponent<VRCContactReceiver>(); receiver.parameter = "Input";
            receiver.radius = .1f / scale;
            float NativeRadius(ContactBase contact)
            {
                typeof(ContactBase).GetMethod("InitShape", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(contact, new object[] { false });
                using var collision = new CollisionScene();
                collision.AddShape(contact.shape);
                collision.UpdateAndSchedule(1f / 60, true, default).Complete();
                return collision.GetShapeData(contact.shape).outRadius;
            }
            float before = NativeRadius(receiver);
            Assert.That(HoistSpatial(FrameIndex()), Is.EqualTo(1), "Moving the owner remains safe with an explicit shape root.");
            var moved = root.GetComponent<VRCContactReceiver>();
            Assert.That(moved.GetRootTransform(), Is.SameAs(nonuniform ? frame : root.transform));
            Assert.That(before, Is.EqualTo(nonuniform ? .9f : .1f).Within(1e-6));
            Assert.That(NativeRadius(moved), Is.EqualTo(before).Within(1e-6));
        }

        [TestCase(1e-7f, false)] [TestCase(1e-7f, true)]
        [TestCase(1f, false)] [TestCase(1f, true)]
        [TestCase(1e7f, false)] [TestCase(1e7f, true)]
        public void SpatialFrameRotatedShapesUseRelativeUniformityForAncestors(float scale, bool nonuniform)
        {
            var parent = PolicyChild("Parent").transform;
            parent.localScale = new Vector3(scale, nonuniform ? scale * 9 : scale, scale);
            var frame = PolicyChild("Frame", parent).transform; frame.localRotation = Quaternion.Euler(20, 45, 15);
            var index = FrameIndex(); var mutations = TransformMutationAnalysis.Create(root, index);
            Assert.That(SpatialFrameProof.UniformShapeScale(frame, root.transform, mutations, out _), Is.EqualTo(!nonuniform));
        }

        [TestCase(1e-7f)] [TestCase(1f)] [TestCase(1e7f)]
        public void SpatialFrameRelativeUniformityStillAcceptsFloatingPointRoundoff(float scale)
        {
            Assert.That(SpatialFrameProof.UniformPositiveScale(new Vector3(scale, scale * 1.0000002f, scale * .9999999f)), Is.True);
            Assert.That(SpatialFrameProof.UniformPositiveScale(new Vector3(scale, 0, scale)), Is.False);
            Assert.That(SpatialFrameProof.UniformPositiveScale(new Vector3(scale, -scale, scale)), Is.False);
            Assert.That(SpatialFrameProof.UniformPositiveScale(new Vector3(scale, float.NaN, scale)), Is.False);
            Assert.That(SpatialFrameProof.UniformPositiveScale(new Vector3(scale, float.PositiveInfinity, scale)), Is.False);
        }

        [TestCase("Sphere")] [TestCase("Capsule")] [TestCase("Box")]
        public void SpatialFrameReceiverFoldsUniformTransformAndRetainsGeometryAndEnableCurve(string shapeName)
        {
            var owner = PolicyChild("Receiver").transform;
            owner.localPosition = new Vector3(.2f, -.3f, .1f); owner.localRotation = Quaternion.Euler(30, 55, 20); owner.localScale = Vector3.one * .75f;
            var contact = owner.gameObject.AddComponent<VRCContactReceiver>(); contact.parameter = "Contact";
            contact.shapeType = (ContactBase.ShapeType)Enum.Parse(typeof(ContactBase.ShapeType), shapeName);
            contact.position = new Vector3(.1f, .2f, .3f); contact.rotation = Quaternion.Euler(15, 22, 7);
            contact.radius = .2f; contact.height = .9f; contact.size = new Vector3(.2f, .6f, .4f);
            var expectedPosition = owner.TransformPoint(contact.position); var expectedRotation = owner.rotation * contact.rotation;
            var expectedSize = contact.size * .75f; float expectedRadius = contact.radius * .75f;
            var clip = StaticCurve(EditorCurveBinding.FloatCurve("Receiver", typeof(VRCContactReceiver), "m_Enabled"), 0);
            var index = FrameIndex(clip); Assert.That(HoistSpatial(index), Is.EqualTo(1));
            var moved = root.GetComponent<VRCContactReceiver>();
            Assert.That(moved.rootTransform, Is.SameAs(root.transform));
            Assert.That(Vector3.Distance(moved.GetRootTransform().TransformPoint(moved.position), expectedPosition), Is.LessThan(1e-6));
            Assert.That(Quaternion.Angle(moved.GetRootTransform().rotation * moved.rotation, expectedRotation), Is.LessThan(.04f));
            Assert.That(moved.radius, Is.EqualTo(expectedRadius).Within(1e-6)); Assert.That(moved.size, Is.EqualTo(expectedSize));
            Assert.That(index.Controllers.Single().AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurveBindings().Single().path, Is.Empty);
            Assert.That(AnimationUtility.GetCurveBindings(clip).Single().path, Is.EqualTo("Receiver"));
        }

        [TestCase("OptionOff")] [TestCase("AnimatedGeometry")] [TestCase("Velocity")]
        [TestCase("AdvancedOutputs")] [TestCase("MaterialOutputs")] [TestCase("Sender")]
        public void SpatialFrameContactRetainsObservableFrames(string boundary)
        {
            var owner = PolicyChild("Contact").transform; owner.localPosition = Vector3.one; owner.localScale = Vector3.one * 2;
            ContactBase contact;
            if (boundary == "Sender") contact = owner.gameObject.AddComponent<VRCContactSender>();
            else
            {
                var receiver = owner.gameObject.AddComponent<VRCContactReceiver>(); contact = receiver;
                if (boundary == "Velocity") receiver.receiverType = ContactReceiver.ReceiverType.OnEnter;
                if (boundary == "AdvancedOutputs") receiver.advancedParameters = ContactReceiver.AdvancedParameter.SenderVelocity;
                if (boundary == "MaterialOutputs") receiver.writeToMaterials = true;
            }
            var clip = boundary == "AnimatedGeometry" ? StaticCurve(EditorCurveBinding.FloatCurve("Contact", contact.GetType(), "radius"), .3f) : Clip();
            Assert.That(HoistSpatial(FrameIndex(clip), boundary != "OptionOff"), Is.Zero);
            Assert.That(contact != null, Is.True); Assert.That(contact.GetRootTransform(), Is.SameAs(owner));
        }

        [TestCase(false)] [TestCase(true)]
        public void SpatialFrameRotatedReceiverKeepsRootUnderNonuniformOrAnimatedAncestorScale(bool animated)
        {
            var parent = PolicyChild("Parent").transform; if (!animated) parent.localScale = new Vector3(1, 2, 1);
            var owner = PolicyChild("Receiver", parent).transform; owner.localRotation = Quaternion.Euler(20, 35, 10); owner.localScale = Vector3.one * .75f;
            owner.gameObject.AddComponent<VRCContactReceiver>().parameter = "Input";
            var index = FrameIndex(animated ? StaticCurve(EditorCurveBinding.FloatCurve("Parent", typeof(Transform), "m_LocalScale.x"), 2) : Clip());
            Assert.That(HoistSpatial(index), Is.EqualTo(1));
            Assert.That(parent.GetComponent<VRCContactReceiver>().GetRootTransform(), Is.SameAs(owner));
        }

        [Test]
        public void SpatialFrameSenderKeepsNativeMatrixAndVelocityRootWhenOwnerIsColocated()
        {
            var owner = PolicyChild("Sender").transform; owner.localScale = Vector3.one * 2; owner.localRotation = Quaternion.Euler(15, 25, 35);
            var sender = owner.gameObject.AddComponent<VRCContactSender>(); sender.position = Vector3.right;
            var matrix = owner.localToWorldMatrix * Matrix4x4.TRS(sender.position, sender.rotation, Vector3.one);
            Assert.That(HoistSpatial(FrameIndex()), Is.EqualTo(1));
            var moved = root.GetComponent<VRCContactSender>(); Assert.That(moved.GetRootTransform(), Is.SameAs(owner));
            Assert.That(StaticTransformProof.Near(matrix, moved.GetRootTransform().localToWorldMatrix * Matrix4x4.TRS(moved.position, moved.rotation, Vector3.one)), Is.True);
        }

        [TestCase("Parent")] [TestCase("Rotation")]
        public void SpatialFrameConstraintSourceOffsetsMatchNativeSolveAcrossMotionAndScale(string kind)
        {
            var originalScene = SceneManager.GetActiveScene();
            var nativeScene = EditorSceneManager.OpenScene(AssetDatabase.GUIDToAssetPath("4e3531870ac74a99b7804047f9db1d96"), OpenSceneMode.Additive);
            SceneManager.MoveGameObjectToScene(root, nativeScene);
            try
            {
                var settings = ObjectSettings(); settings.FoldStaticTransformsIntoComponents = true;
                var parent = PolicyChild("Source parent").transform;
                var marker = PolicyChild("Source marker", parent).transform;
                marker.localRotation = Quaternion.Euler(15, 30, 20); marker.localScale = new Vector3(.5f, 2, 3);
                if (kind == "Rotation") marker.localPosition = Vector3.one;
                var output = PolicyChild("Output"); var control = PolicyChild("Control");
                var type = kind == "Parent" ? typeof(VRCParentConstraint) : typeof(VRCRotationConstraint);
                var c = (VRCConstraintBase)output.AddComponent(type); var reference = (VRCConstraintBase)control.AddComponent(type);
                c.IsActive = true; c.Locked = true; c.GlobalWeight = 1;
                c.Sources.Add(new VRCConstraintSource(marker, 1) { ParentPositionOffset = new Vector3(.2f, .3f, .1f), ParentRotationOffset = new Vector3(10, 20, 30) });
                if (c is VRCRotationConstraint rotation) rotation.RotationOffset = new Vector3(10, 20, 30);
                EditorUtility.CopySerialized(c, reference); settings.ProtectedComponents.Add(ObjectRegistry.GetReference(reference));
                EvaluateNativeConstraints(c, reference);
                var index = FrameIndex(); var graph = AvatarDependencyGraph.Create(root, index, settings, false);
                Assert.That(StaticConstraintSources.Apply(root, settings, index, graph, new StaticTransformProof(root, index, settings), new List<string>()), Is.EqualTo(1));
                Assert.That(c.Sources[0].SourceTransform, Is.SameAs(parent)); Assert.That(reference.Sources[0].SourceTransform, Is.SameAs(marker));
                foreach (var scale in new[] { Vector3.one, Vector3.one * 2, new Vector3(2, 3, 4) })
                {
                    root.transform.localScale = scale; parent.localRotation *= Quaternion.Euler(20, 55, 70); parent.localPosition += Vector3.one;
                    EvaluateNativeConstraints();
                    Assert.That(Vector3.Distance(output.transform.position, control.transform.position), Is.LessThan(1e-5));
                    Assert.That(Quaternion.Angle(output.transform.rotation, control.transform.rotation), Is.LessThan(.05f));
                }
            }
            finally
            {
                SceneManager.MoveGameObjectToScene(root, scene);
                if (originalScene.IsValid() && originalScene.isLoaded) SceneManager.SetActiveScene(originalScene);
                EditorSceneManager.CloseScene(nativeScene, true);
            }
        }

        [TestCase("Position")] [TestCase("LocalSolve")] [TestCase("AnimatedOffset")]
        [TestCase("TinyWeight")] [TestCase("OptionOff")]
        public void SpatialFrameConstraintSourcesRetainUnsupportedCoordinates(string boundary)
        {
            var marker = PolicyChild("Marker").transform; marker.localRotation = Quaternion.Euler(20, 30, 40);
            var c = PolicyChild("Writer").AddComponent<VRCParentConstraint>(); c.Locked = true;
            c.Sources.Add(new VRCConstraintSource(marker, boundary == "TinyWeight" ? 1e-8f : 1));
            if (boundary == "Position") marker.localPosition = Vector3.one;
            if (boundary == "LocalSolve") c.SolveInLocalSpace = true;
            var index = FrameIndex(boundary == "AnimatedOffset" ? StaticCurve(EditorCurveBinding.FloatCurve("Writer", typeof(VRCParentConstraint), "Sources.source0.ParentRotationOffset.x"), 5) : Clip());
            var settings = ObjectSettings(); settings.FoldStaticTransformsIntoComponents = boundary != "OptionOff";
            Assert.That(StaticConstraintSources.Apply(root, settings, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings), new List<string>()), Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void SpatialFrameTranslatedContainerRelocatesAnimatedTrsWithoutChangingCurveShape(bool rotationFrame)
        {
            var frame = PolicyChild("Frame").transform; frame.localPosition = new Vector3(.5f, .3f, -.2f);
            if (rotationFrame) frame.localRotation = Quaternion.Euler(15, 25, 35);
            var child = PolicyChild("Output", frame).transform; child.gameObject.AddComponent<Light>();
            var clip = Clip(); var curve = AnimationCurve.EaseInOut(0, -.1f, 1, .4f); curve.postWrapMode = WrapMode.PingPong;
            clip.SetCurve("Frame/Output", typeof(Transform), "m_LocalPosition.x", curve);
            clip.SetCurve("Frame/Output", typeof(Transform), "m_LocalPosition.y", AnimationCurve.Constant(0, 1, .2f));
            clip.SetCurve("Frame/Output", typeof(Transform), "m_LocalPosition.z", AnimationCurve.Constant(0, 1, .4f));
            clip.SetCurve("Frame/Output", typeof(Transform), "m_LocalScale.x", AnimationCurve.Linear(0, .5f, 1, 2));
            var index = FrameIndex(clip); var settings = ObjectSettings(); settings.MergeStaticObjects = true;
            Assert.That(Flatten(settings, index), Is.EqualTo(rotationFrame ? 0 : 1));
            if (rotationFrame) return;
            var output = index.Controllers.Single().AllReachableNodes().OfType<VirtualClip>().Single();
            var binding = output.GetFloatCurveBindings().Single(b => b.propertyName == "m_LocalPosition.x");
            Assert.That(binding.path, Is.EqualTo("Output")); var rewritten = output.GetFloatCurve(binding);
            Assert.That(rewritten.postWrapMode, Is.EqualTo(curve.postWrapMode));
            foreach (float time in new[] { 0f, .17f, .5f, .83f, 1f, 1.3f })
                Assert.That(rewritten.Evaluate(time), Is.EqualTo(curve.Evaluate(time) + .5f).Within(1e-6));
            Assert.That(child.localPosition, Is.EqualTo(new Vector3(.5f, .3f, -.2f)));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Select(b => b.path), Has.All.EqualTo("Frame/Output"));
        }

        [TestCase("Additive")] [TestCase("Direct")] [TestCase("NormalizedDirect")] [TestCase("MissingAxis")]
        public void SpatialFrameTranslationRetainsNonAffineOrImplicitAnimationOutputs(string boundary)
        {
            var frame = PolicyChild("Frame").transform; frame.localPosition = Vector3.one;
            PolicyChild("Output", frame).AddComponent<Light>(); var clip = Clip();
            foreach (string axis in boundary == "MissingAxis" ? new[] { "x" } : new[] { "x", "y", "z" })
                clip.SetCurve("Frame/Output", typeof(Transform), "m_LocalPosition." + axis, AnimationCurve.Linear(0, 0, 1, 1));
            var index = FrameIndex(clip); var controller = index.Controllers.Single(); var layer = controller.Layers.First();
            if (boundary == "MissingAxis")
            {
                // Unity synthesizes Y/Z when SetCurve writes position X. Exercise
                // a genuinely sparse virtual clip produced by a prior build pass.
                var motion = controller.AllReachableNodes().OfType<VirtualClip>().Single();
                foreach (var binding in motion.GetFloatCurveBindings().Where(b => !b.propertyName.EndsWith(".x", StringComparison.Ordinal)).ToArray())
                    motion.SetFloatCurve(binding, null);
                index = AvatarAnimationIndex.Create(root, index.ControllerEntries, null);
            }
            if (boundary == "Additive") layer.BlendingMode = AnimatorLayerBlendingMode.Additive;
            if (boundary.EndsWith("Direct", StringComparison.Ordinal))
            {
                var tree = Track(new BlendTree { blendType = BlendTreeType.Direct, children = new[] { new ChildMotion { motion = clip, directBlendParameter = "Amount", timeScale = 1 } } });
                var virtualTree = (VirtualBlendTree)new CloneContext(VRChatPlatformAnimatorBindings.Instance).Clone(tree);
                virtualTree.NormalizedBlendValues = boundary == "NormalizedDirect";
                layer.AllReachableNodes().OfType<VirtualState>().First().Motion = virtualTree;
                index = AvatarAnimationIndex.Create(root, index.ControllerEntries, null);
            }
            var settings = ObjectSettings(); settings.MergeStaticObjects = true;
            Assert.That(Flatten(settings, index), Is.Zero); Assert.That(frame != null, Is.True);
        }

        [TestCase("Box")] [TestCase("Sphere")] [TestCase("Capsule")]
        public void SpatialFrameUnityColliderCentersAndUniformDimensionsPreserveBounds(string kind)
        {
            var owner = PolicyChild("Collider").transform; owner.localPosition = new Vector3(.3f, .2f, .4f); owner.localScale = Vector3.one * 2;
            var type = kind == "Box" ? typeof(BoxCollider) : kind == "Sphere" ? typeof(SphereCollider) : typeof(CapsuleCollider);
            var collider = (Collider)owner.gameObject.AddComponent(type);
            using (var serialized = new SerializedObject(collider))
            { serialized.FindProperty("m_Center").vector3Value = new Vector3(.1f, .2f, .3f); serialized.ApplyModifiedPropertiesWithoutUndo(); }
            UnityEngine.Physics.SyncTransforms(); var before = collider.bounds;
            Assert.That(before.size.sqrMagnitude, Is.GreaterThan(0));
            Assert.That(HoistSpatial(FrameIndex()), Is.EqualTo(1));
            var moved = (Collider)root.GetComponent(type); UnityEngine.Physics.SyncTransforms();
            Assert.That(Vector3.Distance(before.center, moved.bounds.center), Is.LessThan(1e-5));
            Assert.That(Vector3.Distance(before.size, moved.bounds.size), Is.LessThan(1e-5));
        }

        [TestCase(false, 0f)] [TestCase(false, .5f)] [TestCase(false, 1f)]
        [TestCase(true, 0f)] [TestCase(true, .5f)] [TestCase(true, 1f)]
        public void SpatialFrameTranslatedCurvesMatchNativeAnimatorAtPartialLayerWeights(bool writeDefaults, float weight)
        {
            var frame = PolicyChild("Frame").transform; frame.localPosition = new Vector3(.5f, .3f, -.2f);
            var child = PolicyChild("Output", frame).transform; child.gameObject.AddComponent<Light>();
            var initial = new Vector3(.1f, .2f, .3f); child.localPosition = initial;
            var clip = Clip();
            foreach (char axis in "xyz") clip.SetCurve("Frame/Output", typeof(Transform), "m_LocalPosition." + axis,
                AnimationCurve.EaseInOut(0, -.2f, 1, .8f));
            AnimatorController Layers(AnimationClip motion)
            {
                var controller = Controller(Clip()); var layer = Controller(motion).layers[0];
                layer.name = "Motion"; layer.defaultWeight = weight; layer.stateMachine.defaultState.writeDefaultValues = writeDefaults;
                controller.layers = new[] { controller.layers[0], layer }; return controller;
            }
            Vector3[] Sample(AnimatorController controller)
            {
                var animator = root.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                var graph = PlayableGraph.Create("Static frame coordinate equivalence");
                try
                {
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    var playable = AnimatorControllerPlayable.Create(graph, controller);
                    AnimationPlayableOutput.Create(graph, "Output", animator).SetSourcePlayable(playable); graph.Play();
                    return Enumerable.Range(0, 5).Select(_ => { graph.Evaluate(.15f); return child.position; }).ToArray();
                }
                finally { graph.Destroy(); UnityEngine.Object.DestroyImmediate(animator); }
            }
            var before = Sample(Layers(clip)); child.localPosition = initial;
            var settings = ObjectSettings(); settings.MergeStaticObjects = true; var index = FrameIndex(clip);
            Assert.That(Flatten(settings, index), Is.EqualTo(1));
            var rewritten = Clip(); var virtualClip = index.Controllers.Single().AllReachableNodes().OfType<VirtualClip>().Single();
            foreach (var binding in virtualClip.GetFloatCurveBindings()) AnimationUtility.SetEditorCurve(rewritten, binding, virtualClip.GetFloatCurve(binding));
            var after = Sample(Layers(rewritten));
            for (int i = 0; i < before.Length; i++) Assert.That(Vector3.Distance(before[i], after[i]), Is.LessThan(1e-5), "Sample " + i);
            if (weight > 0) Assert.That(Vector3.Distance(before[0], before[4]), Is.GreaterThan(.01f), "The native Animator must actually evaluate the curve.");
        }

        [Test]
        public void SpatialFrameSettingsDefaultOnAndTemplateAgree()
        {
            Assert.That(ObjectSettings().Component.FoldStaticTransformsIntoComponents, Is.True);
            Assert.That(root.GetComponent<ALTOTraceAndOptimizeObjects>().AssumeFixedHumanoidBoneScale, Is.False);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/io.github.noname0310.alto/Editor/Installer/Templates/LinkTimeOptimizationSettings.prefab");
            Assert.That(prefab.GetComponentInChildren<ALTOTraceAndOptimizeObjects>(true).FoldStaticTransformsIntoComponents, Is.True);
            Assert.That(prefab.GetComponentInChildren<ALTOTraceAndOptimizeObjects>(true).AssumeFixedHumanoidBoneScale, Is.False);
        }

        private Animator SpatialHumanoid()
        {
            var mapping = new Dictionary<HumanBodyBones, Transform>();
            Transform Add(HumanBodyBones slot, Transform parent, Vector3 position)
            {
                var bone = PolicyChild(slot.ToString(), parent).transform; bone.localPosition = position;
                mapping.Add(slot, bone); return bone;
            }
            var hips = Add(HumanBodyBones.Hips, root.transform, Vector3.up);
            var spacer = PolicyChild("Intermediate", hips).transform; spacer.localPosition = Vector3.up * .1f;
            var spine = Add(HumanBodyBones.Spine, spacer, Vector3.up * .1f);
            var chest = Add(HumanBodyBones.Chest, spine, Vector3.up * .2f);
            var neck = Add(HumanBodyBones.Neck, chest, Vector3.up * .15f);
            Add(HumanBodyBones.Head, neck, Vector3.up * .1f);
            foreach (bool left in new[] { true, false })
            {
                float sign = left ? 1 : -1;
                var arm = Add(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm, chest, new Vector3(sign * .2f, .1f, 0));
                var forearm = Add(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm, arm, new Vector3(sign * .25f, 0, 0));
                Add(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand, forearm, new Vector3(sign * .22f, 0, 0));
                var leg = Add(left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg, hips, new Vector3(sign * .1f, -.1f, 0));
                var lowerLeg = Add(left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg, leg, new Vector3(0, -.4f, 0));
                Add(left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot, lowerLeg, new Vector3(0, -.4f, .03f));
            }
            var description = new HumanDescription
            {
                human = mapping.Select(p => new HumanBone { boneName = p.Value.name, humanName = HumanTrait.BoneName[(int)p.Key], limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
                skeleton = root.GetComponentsInChildren<Transform>().Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
                armStretch = .05f, legStretch = .05f, upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f
            };
            var avatar = Track(AvatarBuilder.BuildHumanAvatar(root, description));
            Assert.That(avatar.isValid && avatar.isHuman, Is.True);
            var animator = root.AddComponent<Animator>(); animator.avatar = avatar; return animator;
        }

        [TestCase(false, "None")] [TestCase(true, "None")] [TestCase(true, "Clip")]
        [TestCase(true, "Constraint")] [TestCase(true, "PhysBone")]
        public void SpatialFrameHumanoidScaleAssumptionNeverHidesKnownWriters(bool assumeFixedScale, string explicitWriter)
        {
            var animator = SpatialHumanoid(); var bone = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var settings = ObjectSettings(); settings.AssumeFixedHumanoidBoneScale = assumeFixedScale;
            var clip = Clip();
            if (explicitWriter == "Clip") clip.SetCurve(AnimationUtility.CalculateTransformPath(bone, root.transform), typeof(Transform),
                "m_LocalScale.x", AnimationCurve.Linear(0, 1, 1, 2));
            if (explicitWriter == "Constraint") bone.gameObject.AddComponent<VRCScaleConstraint>();
            if (explicitWriter == "PhysBone") bone.gameObject.AddComponent<VRCPhysBone>();
            var index = FrameIndex(clip); var proof = new StaticTransformProof(root, index, settings);
            Assert.That(proof.Mutations.Mutable.Contains(bone), Is.True, "External humanoid position/rotation always remains dynamic.");
            Assert.That(proof.CanSubstitute(bone, settings.Scope), Is.False);
            Assert.That(proof.Mutations.MayChangeScale(bone), Is.EqualTo(!assumeFixedScale || explicitWriter != "None"));
            var intermediate = root.transform.Find("Hips/Intermediate");
            Assert.That(proof.Mutations.Mutable.Contains(intermediate), Is.True);
            Assert.That(proof.Mutations.MayChangeScale(intermediate), Is.EqualTo(!assumeFixedScale));
            var helper = PolicyChild("Receiver frame", bone).transform; helper.localRotation = Quaternion.Euler(10, 25, 35);
            Assert.That(SpatialFrameProof.UniformShapeScale(helper, root.transform, proof.Mutations, out _), Is.EqualTo(assumeFixedScale && explicitWriter == "None"));
        }
    }
}
