using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ObjectOptimization;
using Noname.AvatarTools.Physics;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private OptimizationPolicy GeometryPolicy(bool allow, Transform strictDescendant = null)
            => GeometryPolicy(allow ? NumericalOptimization.All : NumericalOptimization.None, strictDescendant);

        private OptimizationPolicy GeometryPolicy(NumericalOptimization allowed, Transform strictDescendant = null)
        {
            if (root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() == null)
                root.AddComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            root.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences = allowed;
            if (strictDescendant != null)
                strictDescendant.gameObject.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences = NumericalOptimization.None;
            var context = new BuildContext(root, null);
            OptimizationPolicy.Capture(context);
            return context.GetState<OptimizationPolicy>();
        }

        [TestCase(false)] [TestCase(true)]
        public void GeometricNumericalPolicy_ReconstructionUsesTheSharedContract(bool allow)
        {
            var near = Matrix4x4.identity; near.m03 = 0.0000002f;
            Assert.That(StaticTransformProof.Near(near, Matrix4x4.identity, allow), Is.EqualTo(allow));
            near.m03 = .001f;
            Assert.That(StaticTransformProof.Near(near, Matrix4x4.identity, allow), Is.False);
            near.m03 = float.NaN;
            Assert.That(StaticTransformProof.Near(near, near, allow), Is.False);
        }

        [TestCase(false, 1e-7f)] [TestCase(true, 1e-7f)]
        [TestCase(false, 1f)] [TestCase(true, 1f)]
        [TestCase(false, 1e7f)] [TestCase(true, 1e7f)]
        public void GeometricNumericalPolicy_UniformScaleKeepsRelativeAndStructuralChecks(bool allow, float scale)
        {
            Assert.That(SpatialFrameProof.UniformPositiveScale(new Vector3(scale, scale * 1.0000002f, scale), allow), Is.EqualTo(allow));
            Assert.That(SpatialFrameProof.UniformPositiveScale(Vector3.one * scale, allow), Is.True);
            Assert.That(SpatialFrameProof.UniformPositiveScale(new Vector3(scale, 2 * scale, scale), allow), Is.False);
            Assert.That(SpatialFrameProof.UniformPositiveScale(new Vector3(scale, 0, scale), allow), Is.False);
            Assert.That(SpatialFrameProof.UniformPositiveScale(new Vector3(scale, -scale, scale), allow), Is.False);
        }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(true, true)]
        public void GeometricNumericalPolicy_FlatteningMeetsEveryAffectedDescendant(bool allow, bool nestedStrict)
        {
            var frame = PolicyChild("Frame").transform; frame.localPosition = Vector3.right * .0000002f;
            var child = PolicyChild("Animated", frame).transform;
            var strict = PolicyChild("Nested output", child).transform; strict.gameObject.AddComponent<Light>();
            var policy = GeometryPolicy(allow, nestedStrict ? strict : null);
            var settings = ObjectSettings(); settings.MergeStaticObjects = true;
            var clip = Clip(); clip.SetCurve("Frame/Animated", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 0, 1, 1));
            var index = FrameIndex(clip); var before = child.localPosition;
            int count = StaticObjectMerging.Apply(root, settings, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings, numericalPolicy: policy), new List<string>());
            bool moved = allow && !nestedStrict;
            Assert.That(count, Is.EqualTo(moved ? 1 : 0));
            Assert.That(child.parent, Is.SameAs(moved ? root.transform : frame));
            Assert.That(child.localPosition, Is.EqualTo(before), "An identity rewrite must not change animation defaults.");
        }

        [Test]
        public void GeometricNumericalPolicy_StrictFlatteningKeepsAnExactIdentityOpportunity()
        {
            var frame = PolicyChild("Frame").transform;
            var child = PolicyChild("Output", frame).transform; child.gameObject.AddComponent<Light>();
            child.localPosition = new Vector3(.17f, -.21f, .33f);
            var policy = GeometryPolicy(false); var settings = ObjectSettings(); settings.MergeStaticObjects = true;
            var index = FrameIndex(); var before = child.localToWorldMatrix;
            Assert.That(StaticObjectMerging.Apply(root, settings, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings, numericalPolicy: policy), new List<string>()), Is.EqualTo(1));
            Assert.That(child.parent, Is.SameAs(root.transform));
            Assert.That(NumericalEquivalence.Same(before, child.localToWorldMatrix, false), Is.True);
        }

        [TestCase(false, "QuaternionSign")] [TestCase(true, "QuaternionSign")]
        [TestCase(false, "SignedZero")] [TestCase(true, "SignedZero")]
        public void GeometricNumericalPolicy_StrictIdentityRetainsAuthoredChannelRepresentations(bool allow, string representation)
        {
            var frame = PolicyChild("Frame").transform;
            if (representation == "QuaternionSign") frame.localRotation = new Quaternion(0, 0, 0, -1);
            else frame.localPosition = new Vector3(BitConverter.Int32BitsToSingle(int.MinValue), 0, 0);
            Assert.That(StaticTransformProof.Near(StaticTransformProof.Local(frame), Matrix4x4.identity), Is.True);
            Assert.That(StaticTransformProof.ExactLocalIdentity(frame), Is.False,
                "The native Transform must retain the authored representation for this fixture.");
            var child = PolicyChild("Output", frame).transform; child.gameObject.AddComponent<Light>();
            var policy = GeometryPolicy(allow); var settings = ObjectSettings(); settings.MergeStaticObjects = true; var index = FrameIndex();
            Assert.That(StaticObjectMerging.Apply(root, settings, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings, numericalPolicy: policy), new List<string>()), Is.EqualTo(allow ? 1 : 0));
            Assert.That(child.parent, Is.SameAs(allow ? root.transform : frame));
        }

        [TestCase(false)] [TestCase(true)]
        public void GeometricNumericalPolicy_HoistingHonorsTheCapturedPolicy(bool allow)
        {
            var owner = PolicyChild("Sound").transform; owner.localPosition = Vector3.right * .0000002f;
            var source = owner.gameObject.AddComponent<AudioSource>();
            root.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences = allow ? NumericalOptimization.All : NumericalOptimization.None;
            var context = ExpressionBuild(null, Controller(Clip())); ObjectSettings();
            OptimizationPolicy.Capture(context); ObjectOptimizationPass.Capture(context);
            var settings = context.GetState<ObjectOptimizationState>(); var index = FrameIndex();
            int count = ComponentHoisting.Apply(context, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings, numericalPolicy: context.GetState<OptimizationPolicy>()),
                t => AnimationUtility.CalculateTransformPath(t, root.transform));
            Assert.That(count, Is.EqualTo(allow ? 1 : 0));
            Assert.That(source == null, Is.EqualTo(allow));
            Assert.That(root.GetComponent<AudioSource>() != null, Is.EqualTo(allow));
        }

        [TestCase(false)] [TestCase(true)]
        public void GeometricNumericalPolicy_ContactOriginRemainsAnExactCallbackGuard(bool allow)
        {
            var owner = PolicyChild("Sender").transform; owner.localPosition = Vector3.right * .0000002f;
            var source = owner.gameObject.AddComponent<VRCContactSender>();
            root.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences = allow ? NumericalOptimization.All : NumericalOptimization.None;
            var context = ExpressionBuild(null, Controller(Clip())); ObjectSettings();
            OptimizationPolicy.Capture(context); ObjectOptimizationPass.Capture(context);
            var settings = context.GetState<ObjectOptimizationState>(); settings.FoldStaticTransformsIntoComponents = true;
            var index = FrameIndex();
            Assert.That(ComponentHoisting.Apply(context, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings, numericalPolicy: context.GetState<OptimizationPolicy>()),
                t => AnimationUtility.CalculateTransformPath(t, root.transform)), Is.Zero);
            Assert.That(source != null, Is.True); Assert.That(source.transform, Is.SameAs(owner));
        }

        [TestCase(false)] [TestCase(true)]
        public void GeometricNumericalPolicy_FlatteningCannotDiscardAContactBoundaryOffset(bool allow)
        {
            var frame = PolicyChild("Frame").transform; frame.localPosition = Vector3.right * .0000005f;
            var child = PolicyChild("Receiver", frame).transform;
            var receiver = child.gameObject.AddComponent<VRCContactReceiver>(); receiver.parameter = "Boundary";
            var policy = GeometryPolicy(allow); var settings = ObjectSettings(); settings.MergeStaticObjects = true;
            var index = FrameIndex(); var before = receiver.GetRootTransform().position;
            // A contact boundary between zero and the authored offset witnesses
            // a persistent Boolean change if the approximate identity shortcut
            // simply drops that offset instead of composing the child's pose.
            float boundary = .00000025f;
            Assert.That(before.x > boundary, Is.True);
            Assert.That(StaticObjectMerging.Apply(root, settings, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings, numericalPolicy: policy), new List<string>()), Is.EqualTo(allow ? 1 : 0));
            Assert.That(receiver.GetRootTransform().position.x > boundary, Is.True);
            for (int axis = 0; axis < 3; axis++)
                Assert.That(NumericalEquivalence.Same(before[axis], receiver.GetRootTransform().position[axis], false), Is.True);
            if (allow) Assert.That(child.localPosition.x, Is.EqualTo(before.x));
        }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)]
        public void GeometricNumericalPolicy_SkinningRetainsUnprovedArithmeticAndKeepsIdentity(bool allow, bool identity)
        {
            var parent = PolicyChild("Moving parent").transform;
            var bone = PolicyChild("Helper", parent).transform;
            if (!identity) { bone.localPosition = new Vector3(.17f, -.21f, .33f); bone.localRotation = Quaternion.Euler(12, 25, -7); }
            var source = AssemblyPart("Source", new[] { bone }, Vector3.zero);
            var original = source.sharedMesh; var poses = original.bindposes; var before = AssemblyWorldVertices(source);
            var policy = GeometryPolicy(allow); var objects = ObjectSettings();
            var index = FrameIndex(StaticCurve(EditorCurveBinding.FloatCurve("Moving parent", typeof(Transform), "m_LocalPosition.x"), 1));
            var settings = new MeshOptimizationState { Scope = objects.Scope, RebindStaticBones = true };
            int count = StaticSkinningOptimization.Apply(root, settings, objects, index, AvatarDependencyGraph.Create(root, index, objects, false),
                new StaticTransformProof(root, index, objects, numericalPolicy: policy), m => Track(m));
            bool rebound = allow || identity;
            Assert.That(count, Is.EqualTo(rebound ? 1 : 0));
            Assert.That(source.bones.Single(), Is.SameAs(rebound ? parent : bone));
            Assert.That(original.bindposes, Is.EqualTo(poses), "Source assets remain immutable.");
            if (!allow) Assert.That(source.sharedMesh.bindposes, Is.EqualTo(poses));
            var after = AssemblyWorldVertices(source);
            for (int i = 0; i < before.Length; i++)
                for (int axis = 0; axis < 3; axis++)
                    Assert.That(NumericalEquivalence.Same(before[i][axis], after[i][axis], allow), Is.True);
        }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)]
        public void GeometricNumericalPolicy_ColliderRootsKeepIdentityWithoutRegroupingGeometry(bool allow, bool identity)
        {
            var frame = PolicyChild("Frame").transform;
            if (!identity) frame.localPosition = Vector3.right * .2f;
            var collider = PolicyChild("Collider").AddComponent<VRCPhysBoneCollider>(); collider.rootTransform = frame;
            collider.position = new Vector3(.17f, -.21f, .33f); collider.radius = .2f;
            var before = collider.position; var policy = GeometryPolicy(allow); var objects = ObjectSettings(); var index = FrameIndex();
            var settings = new PhysBoneOptimizationState { Scope = objects.Scope, RebaseColliderRoots = true };
            int count = StaticColliderRoots.Apply(root, settings, objects, index, AvatarDependencyGraph.Create(root, index, objects, false),
                new StaticTransformProof(root, index, objects, numericalPolicy: policy), new List<string>());
            Assert.That(count, Is.EqualTo(allow || identity ? 1 : 0));
            Assert.That(collider.GetRootTransform(), Is.SameAs(allow || identity ? root.transform : frame));
            if (!allow) Assert.That(collider.position, Is.EqualTo(before));
        }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void GeometricNumericalPolicy_ParentConstraintPoseToleranceRequiresPermission(bool allow, bool atRest)
        {
            var input = PolicyChild("Input").transform; var output = PolicyChild("Output").transform;
            output.localPosition = Vector3.right * .0000002f;
            var constraint = FollowingParent(input, output); if (atRest) constraint.GlobalWeight = 0;
            var policy = GeometryPolicy(allow); var settings = ObjectSettings();
            Assert.That(ParentConstraintOptimization.Apply(root, ConstraintControllers(), null, settings, null, new List<string>(),
                numericPolicy: policy), Is.EqualTo(allow ? 1 : 0));
            Assert.That(constraint == null, Is.EqualTo(allow));
        }

        [TestCase(false, "QuaternionSign")] [TestCase(true, "QuaternionSign")]
        [TestCase(false, "SignedZero")] [TestCase(true, "SignedZero")]
        public void GeometricNumericalPolicy_StrictRestConstraintMatchesChannelsAsWellAsGeometry(bool allow, string representation)
        {
            var input = PolicyChild("Input").transform; var output = PolicyChild("Output").transform;
            if (representation == "QuaternionSign") output.localRotation = new Quaternion(0, 0, 0, -1);
            else output.localPosition = new Vector3(BitConverter.Int32BitsToSingle(int.MinValue), 0, 0);
            var constraint = FollowingParent(input, output); constraint.GlobalWeight = 0;
            Assert.That(StaticTransformProof.ExactLocalIdentity(output), Is.False);
            var policy = GeometryPolicy(allow); var settings = ObjectSettings();
            Assert.That(ParentConstraintOptimization.Apply(root, ConstraintControllers(), null, settings, null, new List<string>(),
                numericPolicy: policy), Is.EqualTo(allow ? 1 : 0));
            Assert.That(constraint == null, Is.EqualTo(allow));
        }

        [TestCase(false)] [TestCase(true)]
        public void GeometricNumericalPolicy_MeshFramesAndCoordinateChangesHonorStrictInputs(bool allow)
        {
            var pair = MergePair(); pair.First.transform.localPosition = pair.Second.transform.localPosition = Vector3.zero;
            pair.Second.transform.localScale = Vector3.one * .99999994f;
            var policy = new MeshMergePolicy { NumericalPolicy = GeometryPolicy(allow), Mutations = new TransformMutationAnalysis(),
                ExpandBounds = true, AllowCoordinateSpaceChanges = true, ConvertRigidMeshes = true };
            Assert.That(policy.RendererFramesMatch(pair.First, pair.Second), Is.EqualTo(allow));
            Assert.That(policy.ConvertsRigid(pair.First), Is.EqualTo(allow));
            pair.Second.transform.localScale = Vector3.one;
            Assert.That(policy.RendererFramesMatch(pair.First, pair.Second), Is.True);
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, policy: policy);
            pair.First.rootBone = pair.First.transform; pair.Second.rootBone = pair.Second.transform;
            pair.Second.transform.localPosition = Vector3.right;
            Assert.That(policy.CoordinatesMatch(candidates[0], candidates[1]), Is.EqualTo(allow));
        }

        [Test]
        public void GeometricNumericalPolicy_ExactMeshMergeCarriesStrictInputsIntoTheNextPass()
        {
            var pair = MergePair(); var bone = PolicyChild("Helper").transform; bone.localPosition = Vector3.right * .2f;
            pair.First.bones = pair.Second.bones = new[] { bone };
            var policy = GeometryPolicy(true, pair.Second.transform);
            var candidates = MergeCandidates(new[] { pair.First, pair.Second });
            MeshOptimizationPass.Merge(candidates, pair.First.name, false, m => Track(m), numericalPolicy: policy);
            Assert.That(pair.Second == null, Is.True);
            Assert.That(policy.AllowsSmallNumericalDifferences(pair.First.transform, NumericalOptimization.Skinning), Is.False);
            var objects = ObjectSettings(); var index = FrameIndex();
            var settings = new MeshOptimizationState { Scope = objects.Scope, RebindStaticBones = true };
            Assert.That(StaticSkinningOptimization.Apply(root, settings, objects, index, AvatarDependencyGraph.Create(root, index, objects, false),
                new StaticTransformProof(root, index, objects, numericalPolicy: policy), m => Track(m)), Is.Zero);
            Assert.That(pair.First.bones, Is.All.EqualTo(bone));
        }

        [Test]
        public void GeometricNumericalPolicy_ExactHoistingRetainsItsSourceContract()
        {
            var owner = PolicyChild("Sound").transform; owner.gameObject.AddComponent<AudioSource>();
            owner.gameObject.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences = NumericalOptimization.None;
            var context = ExpressionBuild(null, Controller(Clip())); ObjectSettings();
            OptimizationPolicy.Capture(context); ObjectOptimizationPass.Capture(context);
            var policy = context.GetState<OptimizationPolicy>(); var settings = context.GetState<ObjectOptimizationState>(); var index = FrameIndex();
            Assert.That(policy.AllowsSmallNumericalDifferences(root.transform, NumericalOptimization.ComponentHoisting), Is.True);
            Assert.That(ComponentHoisting.Apply(context, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings, numericalPolicy: policy),
                t => AnimationUtility.CalculateTransformPath(t, root.transform)), Is.EqualTo(1));
            Assert.That(root.GetComponent<AudioSource>(), Is.Not.Null);
            Assert.That(policy.AllowsSmallNumericalDifferences(root.transform, NumericalOptimization.ComponentHoisting), Is.False);
        }

        [Test]
        public void GeometricNumericalPolicy_IdentityRebindingCannotForgetAnIntermediateStrictScope()
        {
            var parent = PolicyChild("Translated parent").transform; parent.localPosition = Vector3.right * .2f;
            var bone = PolicyChild("Identity helper", parent).transform;
            var source = AssemblyPart("Source", new[] { bone }, Vector3.zero);
            var policy = GeometryPolicy(true, bone); var objects = ObjectSettings(); var index = FrameIndex();
            var settings = new MeshOptimizationState { Scope = objects.Scope, RebindStaticBones = true };
            var references = AvatarDependencyGraph.Create(root, index, objects, false);
            var proof = new StaticTransformProof(root, index, objects, numericalPolicy: policy);
            Assert.That(StaticSkinningOptimization.Apply(root, settings, objects, index, references, proof, m => Track(m)), Is.EqualTo(1));
            Assert.That(source.bones.Single(), Is.SameAs(parent));
            Assert.That(policy.AllowsSmallNumericalDifferences(source.transform, NumericalOptimization.Skinning), Is.False);
            Assert.That(StaticSkinningOptimization.Apply(root, settings, objects, index, references, proof, m => Track(m)), Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void GeometricNumericalPolicy_ContactFrameCompositionPreservesNativeExtremeTranslations(bool allow)
        {
            root.transform.localPosition = Vector3.right * 33554432f;
            var frame = PolicyChild("Cancelling frame").transform; frame.localPosition = Vector3.left * 33554432f;
            var child = PolicyChild("Contact output", frame).transform; child.localPosition = Vector3.right;
            var receiver = child.gameObject.AddComponent<VRCContactReceiver>(); receiver.parameter = "Boundary";
            var before = receiver.GetRootTransform().position;
            // Native Transform evaluation is not a left-to-right sequence of
            // position additions: the installed Unity version reports zero for
            // this original hierarchy, not the mathematically expected one.
            // Compare its actual observation rather than assuming that grouping.
            Assert.That(StaticTransformProof.TryCompose(frame, child, out var position, out var rotation, out var scale), Is.True);
            var regrouped = PolicyChild("Proposed regrouping").transform;
            regrouped.localPosition = position; regrouped.localRotation = rotation; regrouped.localScale = scale;
            Assert.That(StaticTransformProof.Finite(before), Is.True);
            Assert.That(StaticTransformProof.Finite(regrouped.position), Is.True);
            var policy = GeometryPolicy(allow); var settings = ObjectSettings(); settings.MergeStaticObjects = true; var index = FrameIndex();
            StaticObjectMerging.Apply(root, settings, index, AvatarDependencyGraph.Create(root, index, settings, false),
                new StaticTransformProof(root, index, settings, numericalPolicy: policy), new List<string>());
            var after = receiver.GetRootTransform().position;
            Assert.That(after.x > .5f, Is.EqualTo(before.x > .5f), "Numerical permission cannot change the persistent Contact boundary signal.");
            for (int axis = 0; axis < 3; axis++)
                Assert.That(NumericalEquivalence.Same(before[axis], after[axis], allow), Is.True,
                    "A locally small reconstruction error must not become a large observed output error.");
        }

        [Test]
        public void GeometricNumericalPolicy_StrictCoordinatesCompareNativeOperationOrder()
        {
            root.transform.localPosition = Vector3.right * 33554432f;
            var frame = PolicyChild("Cancelling frame").transform; frame.localPosition = Vector3.left * 33554432f;
            var first = PolicyChild("First", frame).AddComponent<SkinnedMeshRenderer>(); first.transform.localPosition = Vector3.right;
            var second = PolicyChild("Second").AddComponent<SkinnedMeshRenderer>(); second.transform.localPosition = Vector3.left * 33554432f;
            first.rootBone = first.transform; second.rootBone = second.transform;
            first.probeAnchor = first.transform; second.probeAnchor = second.transform;
            first.lightProbeUsage = second.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.BlendProbes;
            // Current native positions can coincide despite different authored
            // operations. That observation is not the strict structural proof.
            Assert.That(StaticTransformProof.Finite(first.transform.position), Is.True);
            Assert.That(StaticTransformProof.Finite(second.transform.position), Is.True);
            var policy = new MeshMergePolicy { NumericalPolicy = GeometryPolicy(false), Mutations = new TransformMutationAnalysis() };
            Assert.That(policy.ExactCoordinatesMatch(first, second), Is.False);
            Assert.That(policy.RendererFramesMatch(first, second), Is.False);
            Assert.That(policy.ProbeAnchorsMatch(first, second), Is.False);
            Assert.That(policy.CoordinatesMatch(new MeshMergeCandidate { Renderer = first }, new MeshMergeCandidate { Renderer = second }), Is.False);
            // Identical fixed operations, including exact identity helpers,
            // remain a structural proof without collapsing their products.
            var identity = PolicyChild("Identity", frame).transform;
            second.transform.SetParent(identity, false); second.transform.localPosition = Vector3.right;
            Assert.That(policy.ExactCoordinatesMatch(first, second), Is.True);
            Assert.That(policy.RendererFramesMatch(first, second), Is.True);
            Assert.That(policy.ProbeAnchorsMatch(first, second), Is.True);
        }

        [TestCase(NumericalOptimization.TransformMerging, false)] [TestCase(NumericalOptimization.TransformMerging, true)]
        [TestCase(NumericalOptimization.ComponentHoisting, false)] [TestCase(NumericalOptimization.ComponentHoisting, true)]
        [TestCase(NumericalOptimization.Constraints, false)] [TestCase(NumericalOptimization.Constraints, true)]
        [TestCase(NumericalOptimization.MeshMerging, false)] [TestCase(NumericalOptimization.MeshMerging, true)]
        [TestCase(NumericalOptimization.Skinning, false)] [TestCase(NumericalOptimization.Skinning, true)]
        public void GeometricNumericalPolicy_EachFeatureUsesOnlyItsOwnCheckbox(NumericalOptimization feature, bool allow)
        {
            // The positive enables only this feature; the negative enables every
            // other feature. Either accidental use of another checkbox is caught.
            var mask = allow ? feature : NumericalOptimization.All & ~feature;
            switch (feature)
            {
                case NumericalOptimization.TransformMerging:
                {
                    var frame = PolicyChild("Frame").transform; frame.localPosition = Vector3.right * .0000002f;
                    var child = PolicyChild("Animated", frame).transform; child.gameObject.AddComponent<Light>();
                    var policy = GeometryPolicy(mask); var settings = ObjectSettings(); settings.MergeStaticObjects = true;
                    var clip = Clip(); clip.SetCurve("Frame/Animated", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 0, 1, 1));
                    var index = FrameIndex(clip);
                    Assert.That(StaticObjectMerging.Apply(root, settings, index, AvatarDependencyGraph.Create(root, index, settings, false),
                        new StaticTransformProof(root, index, settings, numericalPolicy: policy), new List<string>()), Is.EqualTo(allow ? 1 : 0));
                    Assert.That(child.parent, Is.SameAs(allow ? root.transform : frame));
                    break;
                }
                case NumericalOptimization.ComponentHoisting:
                {
                    var owner = PolicyChild("Sound").transform; owner.localPosition = Vector3.right * .0000002f;
                    var source = owner.gameObject.AddComponent<AudioSource>();
                    root.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences = mask;
                    var context = ExpressionBuild(null, Controller(Clip())); ObjectSettings();
                    OptimizationPolicy.Capture(context); ObjectOptimizationPass.Capture(context);
                    var settings = context.GetState<ObjectOptimizationState>(); var index = FrameIndex();
                    Assert.That(ComponentHoisting.Apply(context, index, AvatarDependencyGraph.Create(root, index, settings, false),
                        new StaticTransformProof(root, index, settings, numericalPolicy: context.GetState<OptimizationPolicy>()),
                        t => AnimationUtility.CalculateTransformPath(t, root.transform)), Is.EqualTo(allow ? 1 : 0));
                    Assert.That(source == null, Is.EqualTo(allow));
                    break;
                }
                case NumericalOptimization.Constraints:
                {
                    var input = PolicyChild("Input").transform; var output = PolicyChild("Output").transform;
                    output.localPosition = Vector3.right * .0000002f;
                    var constraint = FollowingParent(input, output); var policy = GeometryPolicy(mask); var settings = ObjectSettings();
                    Assert.That(ParentConstraintOptimization.Apply(root, ConstraintControllers(), null, settings, null, new List<string>(),
                        numericPolicy: policy), Is.EqualTo(allow ? 1 : 0));
                    Assert.That(constraint == null, Is.EqualTo(allow));
                    break;
                }
                case NumericalOptimization.MeshMerging:
                {
                    var first = PolicyChild("First").AddComponent<SkinnedMeshRenderer>();
                    var second = PolicyChild("Second").AddComponent<SkinnedMeshRenderer>();
                    second.transform.localScale = Vector3.one * .99999994f;
                    var policy = new MeshMergePolicy { NumericalPolicy = GeometryPolicy(mask), Mutations = new TransformMutationAnalysis() };
                    Assert.That(policy.RendererFramesMatch(first, second), Is.EqualTo(allow));
                    break;
                }
                case NumericalOptimization.Skinning:
                {
                    var bone = PolicyChild("Helper").transform; bone.localPosition = Vector3.right * .2f;
                    var source = AssemblyPart("Source", new[] { bone }, Vector3.zero);
                    var policy = GeometryPolicy(mask); var objects = ObjectSettings(); var index = FrameIndex();
                    var settings = new MeshOptimizationState { Scope = objects.Scope, RebindStaticBones = true };
                    Assert.That(StaticSkinningOptimization.Apply(root, settings, objects, index, AvatarDependencyGraph.Create(root, index, objects, false),
                        new StaticTransformProof(root, index, objects, numericalPolicy: policy), m => Track(m)), Is.EqualTo(allow ? 1 : 0));
                    Assert.That(source.bones.Single(), Is.SameAs(allow ? root.transform : bone));
                    break;
                }
                default: Assert.Fail("Unexpected geometry feature."); break;
            }
        }

        [Test]
        public void GeometricNumericalPolicy_ExactMergeInheritsOtherFeatureRestrictionsWithoutDisablingItsOwn()
        {
            var pair = MergePair(); var bone = PolicyChild("Helper").transform; bone.localPosition = Vector3.right * .2f;
            pair.First.bones = pair.Second.bones = new[] { bone };
            pair.Second.gameObject.AddComponent<ALTOOptimizationPolicy>().AllowedNumericalDifferences = NumericalOptimization.All & ~NumericalOptimization.Skinning;
            var policy = GeometryPolicy(NumericalOptimization.All);
            MeshOptimizationPass.Merge(MergeCandidates(new[] { pair.First, pair.Second }), pair.First.name, false, m => Track(m), numericalPolicy: policy);
            Assert.That(pair.Second == null, Is.True);
            Assert.That(policy.NumericalPermissionsFor(pair.First.transform), Is.EqualTo(NumericalOptimization.All & ~NumericalOptimization.Skinning));
            var other = PolicyChild("Other").AddComponent<SkinnedMeshRenderer>(); other.transform.localPosition = pair.First.transform.localPosition;
            other.transform.localScale = pair.First.transform.localScale * .99999994f;
            var mergePolicy = new MeshMergePolicy { NumericalPolicy = policy, Mutations = new TransformMutationAnalysis(), ConvertRigidMeshes = true };
            Assert.That(mergePolicy.RendererFramesMatch(pair.First, other), Is.True, "Mesh rounding remains independently enabled.");
            Assert.That(mergePolicy.ConvertsRigid(pair.First), Is.False, "Rigid conversion belongs to skinning.");
            var objects = ObjectSettings(); var index = FrameIndex();
            var settings = new MeshOptimizationState { Scope = objects.Scope, RebindStaticBones = true };
            Assert.That(StaticSkinningOptimization.Apply(root, settings, objects, index, AvatarDependencyGraph.Create(root, index, objects, false),
                new StaticTransformProof(root, index, objects, numericalPolicy: policy), m => Track(m)), Is.Zero);
            Assert.That(pair.First.bones, Is.All.EqualTo(bone));
        }
    }
}
