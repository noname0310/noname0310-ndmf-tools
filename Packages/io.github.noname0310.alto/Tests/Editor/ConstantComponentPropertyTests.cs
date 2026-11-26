using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.AnimatorOptimization;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.ParameterPolicy;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.Physics;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false)] [TestCase(true)]
        public void ConstantComponentProperties_OffscreenOutputMatchesAnimator(bool allowInitialChanges)
        {
            Mesh("Shape"); var settings = ObjectSettings(); renderer.updateWhenOffscreen = false;
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "m_UpdateWhenOffscreen");
            var source = Controller(StaticCurve(binding, 1));
            var expected = SampleFrozen(source, (p, i) => { }, 8, () => renderer.updateWhenOffscreen ? 1 : 0);
            renderer.updateWhenOffscreen = false;
            var index = ConstantOutputIndex(source, out var controller);
            Assert.That(ConstantComponentProperties.Apply(index, settings, _ => allowInitialChanges, new List<string>()),
                Is.EqualTo(allowInitialChanges ? 1 : 0));
            var actual = SampleFrozen(CommitAnimator(controller, root), (p, i) => { }, 8, () => renderer.updateWhenOffscreen ? 1 : 0);
            Assert.That(actual, Is.EqualTo(expected));
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantComponentProperties_ContactInitializationRequiresExactDefault(bool equalInitial)
        {
            var settings = ObjectSettings(); var receiver = PolicyChild("Receiver").AddComponent<VRCContactReceiver>();
            receiver.radius = .3f;
            var binding = EditorCurveBinding.FloatCurve(receiver.name, typeof(VRCContactReceiver), "radius");
            var index = ConstantOutputIndex(Controller(StaticCurve(binding, equalInitial ? .3f : .5f)), out _);
            Assert.That(ConstantComponentProperties.Apply(index, settings, _ => true, new List<string>()), Is.EqualTo(equalInitial ? 1 : 0));
            Assert.That(receiver.radius, Is.EqualTo(.3f), "A visual-initialization permission must not change sensor initialization");
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantComponentProperties_OffscreenInitializationKeepsSimulationAndCullingInputs(bool particleReader)
        {
            Mesh("Shape"); var settings = ObjectSettings(); renderer.updateWhenOffscreen = false;
            if (particleReader)
            {
                var particles = PolicyChild("Emitter").AddComponent<ParticleSystem>();
                var shape = particles.shape; shape.shapeType = ParticleSystemShapeType.SkinnedMeshRenderer;
                shape.skinnedMeshRenderer = renderer;
            }
            else root.AddComponent<Animator>().cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            var binding = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "m_UpdateWhenOffscreen");
            var index = ConstantOutputIndex(Controller(StaticCurve(binding, 1)), out _);
            Assert.That(ConstantComponentProperties.Apply(index, settings, _ => true, new List<string>()), Is.Zero);
            Assert.That(renderer.updateWhenOffscreen, Is.False);
        }

        [TestCase("m_LocalPosition")] [TestCase("m_LocalScale")]
        public void ConstantTransformOutputs_VisualPoseMatchesNativeAnimator(string group)
        {
            Mesh("Shape"); var settings = ObjectSettings(); var target = PolicyChild("Visual adjustment").transform;
            var output = new Vector3(.2f, .3f, .4f);
            if (group == "m_LocalPosition") target.localPosition = output; else target.localScale = output;
            var source = Controller(InitialTransformClip(target, group));
            var before = new List<Vector3>();
            SampleFrozen(source, (p, i) => { }, 8, () =>
            { before.Add(group == "m_LocalPosition" ? target.localPosition : target.localScale); return 0; });
            if (group == "m_LocalPosition") target.localPosition = Vector3.zero; else target.localScale = Vector3.one;
            var index = ConstantOutputIndex(source, out var controller);
            Assert.That(ConstantTransformOutputs.Apply(index, settings, _ => true, new List<string>()), Is.EqualTo(3));
            var after = new List<Vector3>();
            SampleFrozen(CommitAnimator(controller, root), (p, i) => { }, 8, () =>
            { after.Add(group == "m_LocalPosition" ? target.localPosition : target.localScale); return 0; });
            for (int i = 0; i < before.Count; i++) Assert.That(Vector3.Distance(before[i], after[i]), Is.LessThan(.000001f));
        }

        [TestCase("NoPermission")] [TestCase("SensorDescendant")] [TestCase("ConstraintSource")]
        [TestCase("ParticleSkinReader")] [TestCase("Preserved")]
        public void ConstantTransformOutputs_KeepInitialPoseReadersAndPreservation(string reason)
        {
            Mesh("Shape"); var settings = ObjectSettings(); var target = PolicyChild("Adjustment").transform;
            target.localPosition = Vector3.one; var clip = InitialTransformClip(target); target.localPosition = Vector3.zero;
            if (reason == "SensorDescendant")
            { var child = PolicyChild("Sensor"); child.transform.SetParent(target, false); child.AddComponent<VRCContactReceiver>(); }
            if (reason == "ConstraintSource")
            {
                var constraint = PolicyChild("Follower").AddComponent<PositionConstraint>();
                constraint.AddSource(new ConstraintSource { sourceTransform = target, weight = 1 });
            }
            if (reason == "ParticleSkinReader")
            {
                renderer.rootBone = target; renderer.bones = new[] { target };
                var particles = PolicyChild("Particles").AddComponent<ParticleSystem>();
                var shape = particles.shape; shape.shapeType = ParticleSystemShapeType.SkinnedMeshRenderer; shape.skinnedMeshRenderer = renderer;
            }
            if (reason == "Preserved") settings.ProtectedComponents.Add(ObjectRegistry.GetReference(target));
            var index = ConstantOutputIndex(Controller(clip), out _);
            Assert.That(ConstantTransformOutputs.Apply(index, settings, _ => reason != "NoPermission", new List<string>()), Is.Zero);
            Assert.That(target.localPosition, Is.EqualTo(Vector3.zero));
        }

        [TestCase(OptimizationPlatforms.None, false)] [TestCase(OptimizationPlatforms.All, true)]
        public void ConstantOutputPolicy_ExplicitScopeAndPlatform(OptimizationPlatforms platforms, bool allowed)
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var child = PolicyChild("Scoped"); var nested = PolicyChild("Strict"); nested.transform.SetParent(child.transform, false);
            var policy = child.AddComponent<ALTOOptimizationPolicy>(); policy.ApplyOnPlatforms = platforms;
            Assert.That(policy.AllowInitialValueChanges, Is.True);
            var strict = nested.AddComponent<ALTOOptimizationPolicy>(); strict.AllowInitialValueChanges = false;
            var context = new BuildContext(root, null); OptimizationPolicy.Capture(context);
            var state = context.GetState<OptimizationPolicy>();
            Assert.That(state.AllowsInitialValueChanges(root.transform), Is.False);
            Assert.That(state.AllowsInitialValueChanges(child.transform), Is.EqualTo(allowed));
            Assert.That(state.AllowsInitialValueChanges(nested.transform), Is.False);
            Assert.That(root.GetComponentsInChildren<ALTOOptimizationPolicy>(true), Is.Empty);
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantOutputs_FinalSdkWorklistUsesTheSameProofAndPolicy(bool allowInitialChanges)
        {
            Mesh("Shape"); root.AddComponent<VRCAvatarDescriptor>();
            var initial = Track(new Material(Shader.Find("Hidden/InternalErrorShader")));
            var replacement = Track(new Material(initial)); renderer.sharedMaterial = initial;
            var clip = ConstantMaterialClip(replacement);
            var offscreen = EditorCurveBinding.FloatCurve("", typeof(SkinnedMeshRenderer), "m_UpdateWhenOffscreen");
            AnimationUtility.SetEditorCurve(clip, offscreen, AnimationCurve.Constant(0, .3f, 1));
            renderer.updateWhenOffscreen = false;
            var index = ConstantOutputIndex(Controller(clip), out var controller);
            var settings = ObjectSettings(); settings.BakeConstantComponentProperties = true;
            var policy = root.AddComponent<ALTOOptimizationPolicy>(); policy.AllowInitialValueChanges = allowInitialChanges;
            var context = new BuildContext(root, null); OptimizationPolicy.Capture(context);
            var report = new OptimizationBuildReport();
            void Apply() => FinalOutputOptimization.Apply(root, index.ControllerEntries, new ResolvedParameterPolicy(), new HashSet<string>(),
                settings, new AnimatorOptimizationSettings { MaterialSlots = true }, null, report, _ => { }, null,
                initialization: context.GetState<OptimizationPolicy>());
            Apply();
            Assert.That(renderer.sharedMaterial, Is.SameAs(allowInitialChanges ? replacement : initial));
            Assert.That(renderer.updateWhenOffscreen, Is.EqualTo(allowInitialChanges));
            var clips = controller.AllReachableNodes().OfType<nadena.dev.ndmf.animator.VirtualClip>().ToArray();
            Assert.That(clips.SelectMany(c => c.GetObjectCurveBindings()).Any(), Is.EqualTo(!allowInitialChanges));
            Assert.That(clips.SelectMany(c => c.GetFloatCurveBindings()).Contains(offscreen), Is.EqualTo(!allowInitialChanges));
            Apply();
            Assert.That(renderer.sharedMaterial, Is.SameAs(allowInitialChanges ? replacement : initial), "The worklist must be idempotent");
        }

        private ALTOTraceAndOptimizeObjects ConstantComponentPipelineOptions(bool enabled)
        {
            var options = ObjectSettings().Component;
            options.BakeConstantComponentProperties = enabled;
            options.BakeConstantTransforms = options.BakeConstantVisibility = false;
            options.RemoveUnusedTransformOutputs = options.RemoveUnusedObjects = options.RemoveRedundantConstraints = false;
            options.PruneUnusedSensorParameters = options.MergeStaticObjects = options.HoistComponents = options.FoldStaticTransformsIntoComponents = false;
            return options;
        }

        private AnimatorController RunConstantComponentPipeline(BuildContext context, bool sdk, bool meshes = false)
        {
            if (sdk)
            {
                AvatarOutputSdkFinalizer.Begin(root); AvatarOutputSdkFinalizer.Capture(context);
                AvatarOutputSdkFinalizer.Complete(root, value => Track(value));
            }
            else OptimizationSession.RunBoundary(context, "constant component downstream fixture",
                () => OptimizationFixedPoint.Apply(context, includeMeshes: meshes));
            var controller = (AnimatorController)root.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers.Single().animatorController;
            TrackControllerAssets(controller); return controller;
        }

        [TestCase("Enabled", false)] [TestCase("Enabled", true)]
        [TestCase("OptionOff", false)] [TestCase("OptionOff", true)]
        [TestCase("PolicyOff", false)] [TestCase("PolicyOff", true)]
        public void ConstantComponentPipeline_RendererNormalizationUnlocksRealMeshMerge(string control, bool sdk)
        {
            var pair = MergePair(); var originalMeshes = new[] { pair.First.sharedMesh, pair.Second.sharedMesh };
            var meshBefore = originalMeshes.Select(mesh => EditorJsonUtility.ToJson(mesh)).ToArray();
            pair.First.updateWhenOffscreen = false; pair.Second.updateWhenOffscreen = true;
            var clip = StaticCurve(EditorCurveBinding.FloatCurve("First", typeof(SkinnedMeshRenderer), "m_UpdateWhenOffscreen"), 1);
            string sourceBefore = EditorJsonUtility.ToJson(clip);
            clip.SampleAnimation(root, .1f);
            var expected = new[] { pair.First, pair.Second }.SelectMany(AssemblyWorldVertices).OrderBy(v => v.x).ThenBy(v => v.y).ThenBy(v => v.z).ToArray();
            pair.First.updateWhenOffscreen = false;
            ConstantComponentPipelineOptions(control != "OptionOff");
            root.AddComponent<ALTOOptimizationPolicy>().AllowInitialValueChanges = control != "PolicyOff";
            var meshes = root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            meshes.MergeIdenticalMaterialSlots = meshes.RemoveUnusedMeshData = meshes.RebindStaticBones = meshes.RemovePermanentlyHiddenGeometry = false;
            var context = ExpressionBuild(null, Controller(clip)); root.GetComponent<Animator>().cullingMode = AnimatorCullingMode.AlwaysAnimate;
            ObjectOptimizationPass.Capture(context); OptimizationPolicy.Capture(context);
            MeshOptimizationPass.Capture(context); MeshOptimizationPass.Prepare(context);
            var built = RunConstantComponentPipeline(context, sdk, true);
            var outputs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToArray();
            Assert.That(outputs.Length, Is.EqualTo(control == "Enabled" ? 1 : 2), "Only permitted setting normalization makes the renderer signals compatible");
            foreach (var output in outputs) if (!originalMeshes.Contains(output.sharedMesh)) Track(output.sharedMesh);
            Assert.That(built.animationClips.SelectMany(AnimationUtility.GetCurveBindings).Any(b => b.propertyName == "m_UpdateWhenOffscreen"),
                Is.EqualTo(control != "Enabled"));
            foreach (var animation in built.animationClips) animation.SampleAnimation(root, .1f);
            var actual = outputs.SelectMany(AssemblyWorldVertices).OrderBy(v => v.x).ThenBy(v => v.y).ThenBy(v => v.z).ToArray();
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int i = 0; i < actual.Length; i++) Assert.That(Vector3.Distance(actual[i], expected[i]), Is.LessThan(1e-5f));
            for (int i = 0; i < originalMeshes.Length; i++) Assert.That(EditorJsonUtility.ToJson(originalMeshes[i]), Is.EqualTo(meshBefore[i]));
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(sourceBefore));
        }

        [TestCase("Enabled", false)] [TestCase("Enabled", true)]
        [TestCase("OptionOff", false)] [TestCase("OptionOff", true)]
        [TestCase("Dynamic", false)] [TestCase("Dynamic", true)]
        public void ConstantComponentPipeline_InvariantColliderCurvesUnlockSharingOnTheNextRound(string control, bool sdk)
        {
            ConstantComponentPipelineOptions(control != "OptionOff");
            var target = PolicyChild("Collider frame").transform;
            var first = LocalCollider("First collider", target); var second = LocalCollider("Second collider", target);
            first.radius = second.radius = .3f;
            var firstSimulation = PhysBone("First simulation"); var secondSimulation = PhysBone("Second simulation");
            firstSimulation.colliders.Add(first); secondSimulation.colliders.AddRange(new[] { second, null, second });
            var clip = Clip(); var binding = EditorCurveBinding.FloatCurve(first.name, typeof(VRCPhysBoneCollider), "radius");
            AnimationUtility.SetEditorCurve(clip, binding, control == "Dynamic" ? AnimationCurve.Linear(0, .3f, .3f, .5f) : AnimationCurve.Constant(0, .3f, .3f));
            string before = EditorJsonUtility.ToJson(clip);
            var physics = root.AddComponent<ALTOTraceAndOptimizePhysBones>();
            physics.OptimizeIsAnimated = physics.ReplaceUnusedEndBones = physics.MergeCompatibleSimulations = physics.RebaseStaticColliderRoots = false;
            var context = ExpressionBuild(null, Controller(clip)); ObjectOptimizationPass.Capture(context);
            PhysBoneOptimizationPass.Capture(context); PhysBoneOptimizationPass.Prepare(context);
            var built = RunConstantComponentPipeline(context, sdk);
            bool shared = control == "Enabled";
            Assert.That(root.GetComponentsInChildren<VRCPhysBoneCollider>(true).Length, Is.EqualTo(shared ? 1 : 2));
            Assert.That(firstSimulation.colliders.Single() == secondSimulation.colliders[0], Is.EqualTo(shared));
            Assert.That(secondSimulation.colliders.Count, Is.EqualTo(3)); Assert.That(secondSimulation.colliders[1], Is.Null);
            Assert.That(secondSimulation.colliders[2], Is.SameAs(secondSimulation.colliders[0]), "Collision multiplicity stays intact");
            Assert.That(firstSimulation.colliders[0].radius, Is.EqualTo(.3f));
            Assert.That(built.animationClips.SelectMany(AnimationUtility.GetCurveBindings).Any(b => b.propertyName == "radius"), Is.EqualTo(!shared));
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(before));
        }

        [TestCase("Enabled", false)] [TestCase("Enabled", true)]
        [TestCase("OptionOff", false)] [TestCase("OptionOff", true)]
        [TestCase("Dynamic", false)] [TestCase("Dynamic", true)]
        [TestCase("NativeWriter", false)] [TestCase("NativeWriter", true)]
        public void ConstantComponentPipeline_ContactHoistingKeepsDynamicAndNativePoseWriters(string control, bool sdk)
        {
            var options = ConstantComponentPipelineOptions(control != "OptionOff"); options.HoistComponents = true;
            root.AddComponent<VRCContactSender>().radius = .6f;
            var frame = PolicyChild("Contact owner"); var contact = frame.AddComponent<VRCContactSender>();
            contact.radius = .3f; contact.position = new Vector3(.1f, .2f, .3f); contact.collisionTags = new List<string> { "PipelineContact" };
            if (control == "NativeWriter")
            {
                var constraint = frame.AddComponent<PositionConstraint>();
                constraint.AddSource(new ConstraintSource { sourceTransform = PolicyChild("Native source").transform, weight = 1 });
                constraint.weight = 1; constraint.locked = true; constraint.constraintActive = true;
            }
            float Radius(ContactBase value)
            {
                // CollisionScene.Dispose leaves the old Shape's native ID assigned.
                // Each disposable scene needs a fresh shape even when the component
                // survived the optimization; InitShape(false) would reuse that ID.
                typeof(ContactBase).GetMethod("InitShape", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(value, new object[] { true });
                using var collision = new CollisionScene(); collision.AddShape(value.shape);
                collision.UpdateAndSchedule(1f / 60, true, default).Complete();
                return collision.GetShapeData(value.shape).outRadius;
            }
            float beforeRadius = Radius(contact); var beforeCenter = contact.GetRootTransform().TransformPoint(contact.position);
            var clip = Clip(); var binding = EditorCurveBinding.FloatCurve(frame.name, typeof(VRCContactSender), "radius");
            AnimationUtility.SetEditorCurve(clip, binding, control == "Dynamic" ? AnimationCurve.Linear(0, .3f, .3f, .5f) : AnimationCurve.Constant(0, .3f, .3f));
            string sourceBefore = EditorJsonUtility.ToJson(clip);
            var context = ExpressionBuild(null, Controller(clip)); ObjectOptimizationPass.Capture(context);
            var built = RunConstantComponentPipeline(context, sdk);
            var output = root.GetComponentsInChildren<VRCContactSender>(true).Single(c => c.collisionTags.Contains("PipelineContact"));
            Assert.That(output.transform == root.transform, Is.EqualTo(control == "Enabled"));
            Assert.That(output.radius, Is.EqualTo(.3f), "Normalization and relocation preserve the authored spatial setting");
            Assert.That(Radius(output), Is.EqualTo(beforeRadius));
            Assert.That(Vector3.Distance(output.GetRootTransform().TransformPoint(output.position), beforeCenter), Is.LessThan(1e-6f));
            Assert.That(built.animationClips.SelectMany(AnimationUtility.GetCurveBindings).Any(b => b.propertyName == "radius"),
                Is.EqualTo(control == "Dynamic" || control == "OptionOff"));
            Assert.That(EditorJsonUtility.ToJson(clip), Is.EqualTo(sourceBefore));
        }
    }
}
