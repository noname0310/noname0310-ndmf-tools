using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.Meshes;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private ALTOMeshOptimizationPolicy MeshPolicy(bool coordinates = true, bool reorder = false, Transform scope = null)
        {
            if (root.GetComponent<VRCAvatarDescriptor>() == null) root.AddComponent<VRCAvatarDescriptor>();
            var policy = root.AddComponent<ALTOMeshOptimizationPolicy>();
            policy.ScopeRoot = scope;
            policy.ApplyOnPlatforms = OptimizationPlatforms.All;
            policy.AllowCoordinateSpaceChanges = coordinates;
            policy.AllowMaterialSlotReordering = reorder;
            return policy;
        }

        private BuildContext CaptureMeshPolicy(BuildTarget target = BuildTarget.StandaloneWindows64)
        {
            var context = new BuildContext(root, null);
            typeof(OptimizationBuildEnvironment).GetField("Target", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(context.GetState<OptimizationBuildEnvironment>(), target);
            MeshOptimizationPass.Capture(context);
            return context;
        }

        [Test]
        public void FreshMeshOptimizerUsesConservativePermissionsWithoutPlatformFields()
        {
            MaterialDescriptor(); root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            Assert.That(typeof(ALTOTraceAndOptimizeMeshes).GetField("CoordinateSpaceChangesOnPlatforms"), Is.Null);
            Assert.That(typeof(ALTOTraceAndOptimizeMeshes).GetField("MaterialSlotReorderingOnPlatforms"), Is.Null);
            var state = CaptureMeshPolicy(BuildTarget.Android).GetState<MeshOptimizationState>();
            Assert.That(state.PermissionsFor(renderer), Is.EqualTo(MeshOptimizationPermissions.None));
            Assert.That(state.MergeRenderers && state.MergeSlots && state.RemoveUnusedData && state.RebindStaticBones, Is.True);
        }

        [TestCase(BuildTarget.Android, true)]
        [TestCase(BuildTarget.iOS, true)]
        [TestCase(BuildTarget.StandaloneWindows64, false)]
        public void SharedMobileMeshPolicyLeavesPcConservative(BuildTarget target, bool expected)
        {
            MaterialDescriptor(); root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            var policy = MeshPolicy(reorder: true);
            policy.ApplyOnPlatforms = OptimizationPlatforms.Android | OptimizationPlatforms.IOS;
            var state = CaptureMeshPolicy(target).GetState<MeshOptimizationState>();
            Assert.That(state.PermissionsFor(renderer), Is.EqualTo(expected ? MeshOptimizationPermissions.All : MeshOptimizationPermissions.None));
            Assert.That(root.GetComponentsInChildren<ALTOMeshOptimizationPolicy>(true), Is.Empty);
            Assert.That(state.Enabled, Is.True, "Platform policy selection does not disable the common optimizer.");
        }

        [TestCase(false)] [TestCase(true)]
        public void ScopedMeshPoliciesDoNotBroadenOptimizerScopeOrPreservation(bool preserved)
        {
            var pair = MergePair(); MaterialDescriptor();
            var optimizer = root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            optimizer.ScopeRoot = pair.First.transform;
            if (preserved) optimizer.PreserveRenderers.Add(pair.First);
            MeshPolicy(reorder: true);
            var context = CaptureMeshPolicy(); MeshOptimizationPass.Prepare(context);
            var state = context.GetState<MeshOptimizationState>();
            Assert.That(MeshOptimizationPass.InputProtected(state, pair.Second, out _), Is.True);
            Assert.That(MeshOptimizationPass.InputProtected(state, pair.First, out _), Is.EqualTo(preserved));
        }

        [Test]
        public void MoreSpecificMeshPolicyCanDisableBroaderPermissions()
        {
            var pair = MergePair(); MaterialDescriptor(); root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            MeshPolicy(reorder: true);
            MeshPolicy(coordinates: false, scope: pair.First.transform);
            var state = CaptureMeshPolicy().GetState<MeshOptimizationState>();
            Assert.That(state.PermissionsFor(pair.First), Is.EqualTo(MeshOptimizationPermissions.None));
            Assert.That(state.PermissionsFor(pair.Second), Is.EqualTo(MeshOptimizationPermissions.All));
        }

        [Test]
        public void ConflictingMeshPoliciesRejectSameScopeWithoutMutatingMeshes()
        {
            var pair = MergePair(); MaterialDescriptor(); root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            var source = pair.First.sharedMesh;
            MeshPolicy(); MeshPolicy(coordinates: false);
            Assert.Throws<InvalidOperationException>(() => CaptureMeshPolicy());
            Assert.That(pair.First.sharedMesh, Is.SameAs(source));
            Assert.That(root.GetComponents<ALTOMeshOptimizationPolicy>().Length, Is.EqualTo(2));
        }

        [Test]
        public void DisjointPlatformPoliciesCanShareAScope()
        {
            MaterialDescriptor(); root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            MeshPolicy().ApplyOnPlatforms = OptimizationPlatforms.Android;
            MeshPolicy(coordinates: false, reorder: true).ApplyOnPlatforms = OptimizationPlatforms.PC;
            var state = CaptureMeshPolicy().GetState<MeshOptimizationState>();
            Assert.That(state.PermissionsFor(renderer), Is.EqualTo(MeshOptimizationPermissions.MaterialSlotReordering));
        }

        [TestCase("disabled")] [TestCase("editorOnly")] [TestCase("none")]
        public void InactiveMeshPolicyDoesNotGrantPermissions(string reason)
        {
            MaterialDescriptor(); root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            var holder = PolicyChild("Policy"); var policy = holder.AddComponent<ALTOMeshOptimizationPolicy>();
            policy.ScopeRoot = root.transform; policy.ApplyOnPlatforms = OptimizationPlatforms.All;
            policy.AllowCoordinateSpaceChanges = policy.AllowMaterialSlotReordering = true;
            if (reason == "disabled") policy.enabled = false;
            if (reason == "editorOnly") holder.tag = "EditorOnly";
            if (reason == "none") policy.ApplyOnPlatforms = OptimizationPlatforms.None;
            Assert.That(CaptureMeshPolicy().GetState<MeshOptimizationState>().PermissionsFor(renderer), Is.EqualTo(MeshOptimizationPermissions.None));
        }

        [Test]
        public void MeshPolicyAloneDoesNotEnableOptimization()
        {
            MeshPolicy(reorder: true);
            Assert.That(CaptureMeshPolicy().GetState<MeshOptimizationState>().Enabled, Is.False);
            Assert.That(root.GetComponents<ALTOMeshOptimizationPolicy>(), Is.Empty);
        }

        [Test]
        public void MeshPolicyFindsGeneratedRenderersWithinItsCapturedScope()
        {
            MaterialDescriptor(); root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            var scope = PolicyChild("Generated scope"); MeshPolicy(reorder: true, scope: scope.transform);
            var state = CaptureMeshPolicy().GetState<MeshOptimizationState>();
            var generated = PolicyChild("Generated", scope.transform).AddComponent<SkinnedMeshRenderer>();
            Assert.That(state.PermissionsFor(generated), Is.EqualTo(MeshOptimizationPermissions.All));
            Assert.That(state.PermissionsFor(renderer), Is.EqualTo(MeshOptimizationPermissions.None));
        }

        [TestCase(false)] [TestCase(true)]
        public void MappedMeshesKeepTheIntersectionOfOriginalPermissionsAfterMarkerRemoval(bool removeMarker)
        {
            var pair = MergePair(); MaterialDescriptor(); var optimizer = root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            MeshPolicy(reorder: true, scope: pair.First.transform);
            var context = CaptureMeshPolicy(); MeshOptimizationPass.Prepare(context);
            var state = context.GetState<MeshOptimizationState>();
            optimizer.BuildInputRenderers = optimizer.BuildInputRenderers.Select(r => r == pair.Second ? pair.First : r).ToArray();
            if (removeMarker) { MeshOptimizationPass.CaptureMappedInputs(state); Object.DestroyImmediate(optimizer); }
            Object.DestroyImmediate(pair.Second);
            Assert.That(state.PermissionsFor(pair.First), Is.EqualTo(MeshOptimizationPermissions.None));
        }

        [TestCase(false)] [TestCase(true)]
        public void MergedSlotOrderRequiresEveryInputPermissionAndRemainsStableInTheLatePass(bool allowBoth)
        {
            var pair = MergePair(); MaterialDescriptor(); var optimizer = root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            optimizer.RebindStaticBones = optimizer.RemoveUnusedMeshData = false;
            var first = pair.First.sharedMaterial; var second = Track(new Material(first));
            RepeatMaterialSlots(pair.First, new[] { first, second, first });
            RepeatMaterialSlots(pair.Second, new[] { first, second, first });
            MeshPolicy(coordinates: false, reorder: true, scope: allowBoth ? root.transform : pair.First.transform);
            var context = CaptureMeshPolicy(); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            Track(pair.First.sharedMesh);
            Assert.That(pair.Second == null, Is.True, "Conservative-compatible meshes may still merge across policy boundaries.");
            Assert.That(pair.First.sharedMaterials.Length, Is.EqualTo(allowBoth ? 2 : 5));
            MeshOptimizationPass.ApplyFinal(context, new Dictionary<object, VirtualAnimatorController>(), new ResolvedParameterPolicy(), value => Track(value));
            Assert.That(pair.First.sharedMaterials.Length, Is.EqualTo(allowBoth ? 2 : 5), "The destination's policy must not broaden a merged output in a later pass.");
        }

        [Test]
        public void ScopedMeshPolicyConvertsOnlyItsEligibleRigidRenderers()
        {
            MaterialDescriptor(); var optimizer = root.AddComponent<ALTOTraceAndOptimizeMeshes>();
            optimizer.RebindStaticBones = optimizer.RemoveUnusedMeshData = false;
            var inside = RigidPart(); var insideObject = inside.gameObject;
            var outside = RigidPart(); outside.name = "Outside";
            foreach (var part in new[] { inside, outside })
            { part.lightProbeUsage = LightProbeUsage.Off; part.reflectionProbeUsage = ReflectionProbeUsage.Off; }
            MeshPolicy(scope: inside.transform);
            var context = CaptureMeshPolicy(); MeshOptimizationPass.Prepare(context); MeshOptimizationPass.Apply(context);
            Assert.That(inside == null && outside != null, Is.True);
            var converted = insideObject.GetComponent<SkinnedMeshRenderer>();
            Assert.That(converted, Is.Not.Null); Track(converted.sharedMesh);
            Assert.That((context.GetState<MeshOptimizationState>().PermissionsFor(converted) & MeshOptimizationPermissions.RigidConversion) != 0, Is.True);
        }

        [Test]
        public void MixedPermissionsRetainConservativeCompatibleMergesButPreventCoordinateRelaxation()
        {
            var pair = MergePair(); var candidates = MergeCandidates(new[] { pair.First, pair.Second });
            var policy = MergePolicy(coordinates: true);
            policy.PermissionsFor = r => r == pair.First ? MeshOptimizationPermissions.All : MeshOptimizationPermissions.None;
            Assert.That(MeshMergeAnalysis.Group(candidates, true, policy).Count, Is.EqualTo(1));
            pair.Second.rootBone = PolicyChild("Other coordinates").transform; pair.Second.rootBone.localPosition = Vector3.right;
            Assert.That(MeshMergeAnalysis.Group(candidates, true, policy).Count, Is.EqualTo(2));
        }
    }
}
