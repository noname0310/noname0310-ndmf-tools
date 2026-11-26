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
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private BuildContext LateShapeContext(bool permitInitialization = true, bool permitNumericalDifferences = true,
            NumericalOptimization? numericalPermissions = null)
        {
            if (root.GetComponent<VRCAvatarDescriptor>() == null) root.AddComponent<VRCAvatarDescriptor>();
            if (root.GetComponent<ALTOTraceAndOptimizeBlendShapes>() == null)
            {
                var options = root.AddComponent<ALTOTraceAndOptimizeBlendShapes>();
                options.PreserveMmdBlendShapes = false;
                options.OptimizeZeroResetAnimations = false;
            }
            var policy = root.AddComponent<ALTOOptimizationPolicy>();
            policy.AllowInitialValueChanges = permitInitialization;
            policy.AllowedNumericalDifferences = numericalPermissions ??
                (permitNumericalDifferences ? NumericalOptimization.All : NumericalOptimization.None);
            var context = new BuildContext(root, null);
            OptimizationPolicy.Capture(context); ALTOPlugin.Capture(context);
            return context;
        }

        private static Dictionary<object, VirtualAnimatorController> LateShapeEntries(AnimatorController controller)
            => new() { [VRCAvatarDescriptor.AnimLayerType.FX] = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(controller) };

        private int LateShapes(BuildContext context, IDictionary<object, VirtualAnimatorController> entries)
            => ALTOPlugin.ApplyFinal(context, entries, new ResolvedParameterPolicy(), value => Track(value));

        [TestCase(false)] [TestCase(true)]
        public void LateBlendShapes_CurrentControllersBakeConstantsAndPreserveNativeSkinning(bool writeDefaults)
        {
            var original = Mesh("Constant", "Dynamic");
            var early = Clip("Constant", "Dynamic");
            foreach (var binding in AnimationUtility.GetCurveBindings(early))
                AnimationUtility.SetEditorCurve(early, binding, AnimationCurve.Linear(0, 5, .3f, 75));
            renderer.SetBlendShapeWeight(0, 20);
            var context = LateShapeContext();
            Assert.That(LateShapes(context, LateShapeEntries(Controller(early))), Is.Zero);
            foreach (var marker in root.GetComponentsInChildren<ALTOTraceAndOptimizeBlendShapes>(true)) Object.DestroyImmediate(marker);

            var late = StaticCurve(Binding("Constant"), 70);
            AnimationUtility.SetEditorCurve(late, Binding("Dynamic"), AnimationCurve.Linear(0, 5, .3f, 75));
            var source = Controller(late); source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var baked = Track(new Mesh()); var before = new List<Vector3[]>();
            SampleFrozen(source, (p, i) => { }, 12, () => { renderer.BakeMesh(baked); before.Add(baked.vertices); return 0; }, 20);
            renderer.SetBlendShapeWeight(0, 20); renderer.SetBlendShapeWeight(1, 0);
            var entries = LateShapeEntries(source);
            Assert.That(LateShapes(context, entries), Is.EqualTo(1));
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("Constant"), Is.EqualTo(-1));
            int frame = 0;
            SampleFrozen(CommitAnimator(entries.Values.Single(), root), (p, i) => { }, 12, () =>
            {
                renderer.BakeMesh(baked);
                Assert.That(baked.vertices.Zip(before[frame++], Vector3.Distance).Max(), Is.LessThan(.00001f));
                return 0;
            });
            var completed = renderer.sharedMesh;
            Assert.That(LateShapes(context, entries), Is.Zero);
            Assert.That(renderer.sharedMesh, Is.SameAs(completed));
            Assert.That(original.blendShapeCount, Is.EqualTo(2));
            Assert.That(AnimationUtility.GetEditorCurve(late, Binding("Constant")), Is.Not.Null);
        }

        [TestCase(false)] [TestCase(true)]
        public void LateBlendShapes_KnownGeometryRewriteExposesIneffectiveMorph(bool nativeReader)
        {
            var original = Mesh("Affected");
            var clip = Clip("Affected");
            AnimationUtility.SetEditorCurve(clip, Binding("Affected"), AnimationCurve.Linear(0, 0, 1, 100));
            if (nativeReader)
            {
                var particle = PolicyChild("Emitter").AddComponent<ParticleSystem>();
                var shape = particle.shape; shape.shapeType = ParticleSystemShapeType.SkinnedMeshRenderer;
                shape.skinnedMeshRenderer = renderer;
            }
            var context = LateShapeContext(); var entries = LateShapeEntries(Controller(clip));
            Assert.That(LateShapes(context, entries), Is.Zero);
            var filtered = Track(Object.Instantiate(original)); filtered.SetIndices(Array.Empty<int>(), MeshTopology.Triangles, 0);
            context.GetState<OptimizationState>().Provenance.RecordMeshRewrite(renderer, original, filtered);
            renderer.sharedMesh = filtered;
            Assert.That(LateShapes(context, entries), Is.EqualTo(nativeReader ? 0 : 1));
            Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(nativeReader ? 1 : 0));
            Assert.That(renderer.sharedMesh.GetIndices(0), Is.Empty);
            Assert.That(original.GetIndices(0), Has.Length.EqualTo(3));
            Assert.That(LateShapes(context, entries), Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void LateBlendShapes_AnimatedNativeReaderProtectsUnindexedMorphWithEitherConstantOption(bool freezeConstants)
        {
            var original = Mesh("Affected"); original.SetIndices(Array.Empty<int>(), MeshTopology.Triangles, 0);
            var particle = PolicyChild("Emitter").AddComponent<ParticleSystem>();
            var other = PolicyChild("Original target").AddComponent<SkinnedMeshRenderer>(); other.sharedMesh = original;
            var shape = particle.shape; shape.shapeType = ParticleSystemShapeType.SkinnedMeshRenderer;
            shape.skinnedMeshRenderer = other;
            var options = root.AddComponent<ALTOTraceAndOptimizeBlendShapes>();
            options.FreezeConstantAnimatedBlendShapes = freezeConstants; options.OptimizeZeroResetAnimations = false;
            var context = LateShapeContext();
            var clip = Clip("Affected");
            AnimationUtility.SetEditorCurve(clip, Binding("Affected"), AnimationCurve.Linear(0, 0, 1, 100));
            AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("Emitter", typeof(ParticleSystem), "ShapeModule.m_SkinnedMeshRenderer"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = other }, new ObjectReferenceKeyframe { time = .5f, value = renderer } });
            LateShapes(context, LateShapeEntries(Controller(clip)));
            Assert.That(renderer.sharedMesh, Is.SameAs(original));
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("Affected"), Is.EqualTo(0));
        }

        [TestCase("Clone")] [TestCase("Retarget")] [TestCase("VertexSubset")]
        [TestCase("NewMorph")] [TestCase("ChangedMorph")]
        public void LateBlendShapes_VerifiedEarlyGeneratorCopiesKeepOriginalShapePermissions(string generator)
        {
            Object.DestroyImmediate(renderer);
            renderer = PolicyChild("Scoped").AddComponent<SkinnedMeshRenderer>();
            var original = Mesh();
            if (generator == "VertexSubset")
            {
                original.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, Vector3.right * 2, Vector3.up * 2 };
                original.normals = Enumerable.Repeat(Vector3.forward, 6).ToArray();
                original.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, -1), 6).ToArray();
                original.uv = Enumerable.Repeat(Vector2.zero, 6).ToArray(); original.triangles = new[] { 0, 1, 2, 3, 4, 5 };
            }
            Frame(original, "Original", 100, Vector3.right); renderer.SetBlendShapeWeight(0, 20);
            var settings = root.AddComponent<ALTOTraceAndOptimizeBlendShapes>();
            settings.ScopeRoot = renderer.transform; settings.OptimizeZeroResetAnimations = false;
            var context = LateShapeContext();
            Mesh output;
            if (generator == "VertexSubset")
            {
                output = Track(new Mesh { name = "Generator compacted mesh" });
                output.vertices = original.vertices.Take(3).ToArray(); output.normals = original.normals.Take(3).ToArray();
                output.tangents = original.tangents.Take(3).ToArray(); output.triangles = new[] { 0, 1, 2 };
                Frame(output, "Original", 100, Vector3.right);
            }
            else output = Track(Object.Instantiate(original));
            if (generator == "Retarget")
            {
                var bone = PolicyChild("Generated skin frame").transform;
                output.bindposes = new[] { Matrix4x4.identity };
                output.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, output.vertexCount).ToArray();
                renderer.bones = new[] { bone };
            }
            if (generator == "NewMorph") Frame(output, "Generated", 100, Vector3.up);
            if (generator == "ChangedMorph") { output.ClearBlendShapes(); Frame(output, "Original", 100, Vector3.up); }
            renderer.sharedMesh = output; renderer.SetBlendShapeWeight(0, 20);
            var clip = StaticCurve(Binding("Original", "Scoped"), 20);
            clip.SampleAnimation(root, .1f); var expected = AssemblyWorldVertices(renderer);
            ALTOPlugin.PrepareInputs(context);
            int changes = LateShapes(context, LateShapeEntries(Controller(clip)));
            Assert.That(changes, Is.EqualTo(generator == "ChangedMorph" ? 0 : 1));
            Assert.That(AssemblyWorldVertices(renderer).Zip(expected, Vector3.Distance).Max(), Is.LessThan(.00001f));
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("Generated") >= 0, Is.EqualTo(generator == "NewMorph"));
            Assert.That(original.blendShapeCount, Is.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void LateBlendShapes_EarlyCoincidentForeignContributorCannotAcquireDestinationScope(bool destroyForeign)
        {
            Object.DestroyImmediate(renderer);
            renderer = PolicyChild("Scoped").AddComponent<SkinnedMeshRenderer>();
            var original = Mesh("Shape"); renderer.SetBlendShapeWeight(0, 20);
            var foreign = PolicyChild("Foreign").AddComponent<SkinnedMeshRenderer>();
            foreign.sharedMesh = Track(Object.Instantiate(original));
            var settings = root.AddComponent<ALTOTraceAndOptimizeBlendShapes>();
            settings.ScopeRoot = renderer.transform; settings.OptimizeZeroResetAnimations = false;
            var context = LateShapeContext();
            renderer.sharedMesh = Track(Object.Instantiate(original)); renderer.SetBlendShapeWeight(0, 20);
            if (destroyForeign) Object.DestroyImmediate(foreign);
            ALTOPlugin.PrepareInputs(context);
            var entries = LateShapeEntries(Controller(StaticCurve(Binding("Shape", "Scoped"), 20)));
            Assert.That(LateShapes(context, entries), Is.Zero);
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("Shape"), Is.EqualTo(0));
        }

        [TestCase("OutOfScope", false, false)] [TestCase("OutOfScope", true, false)]
        [TestCase("OutOfScope", false, true)] [TestCase("OutOfScope", true, true)]
        [TestCase("ExcludedName", false, true)] [TestCase("ExcludedName", true, true)]
        [TestCase("Mmd", false, true)] [TestCase("Mmd", true, true)]
        public void LateBlendShapes_RebasedOrRenamedForeignSourcesKeepTheirPermissions(string restriction, bool removeForeign, bool rename)
        {
            Object.DestroyImmediate(renderer);
            renderer = PolicyChild("Scoped").AddComponent<SkinnedMeshRenderer>();
            var original = Mesh("Allowed"); renderer.SetBlendShapeWeight(0, 20);
            var foreign = PolicyChild("Foreign").AddComponent<SkinnedMeshRenderer>();
            var foreignMesh = Track(Object.Instantiate(original));
            foreignMesh.vertices = foreignMesh.vertices.Select(v => v + Vector3.right * 100).ToArray();
            string sourceName = rename ? restriction == "Mmd" ? "まばたき" : "Protected source" : "Allowed";
            foreignMesh.ClearBlendShapes(); Frame(foreignMesh, sourceName, 100, Vector3.right);
            foreign.sharedMesh = foreignMesh; foreign.transform.localPosition = Vector3.left * 100;
            var settings = root.AddComponent<ALTOTraceAndOptimizeBlendShapes>(); settings.OptimizeZeroResetAnimations = false;
            if (restriction == "OutOfScope") settings.ScopeRoot = renderer.transform;
            if (restriction == "ExcludedName") foreign.gameObject.AddComponent<BlendShapeOptimizationExclude>().BlendShapes.Add(sourceName);
            var context = LateShapeContext();

            // The generator copies foreign data into the existing renderer and
            // rebases it. Final vertex and morph bytes equal the allowed source,
            // but neither that equality nor the surviving name proves origin.
            var replacement = Track(Object.Instantiate(foreignMesh));
            replacement.vertices = replacement.vertices.Select(v => v - Vector3.right * 100).ToArray();
            var delta = new ShapeDelta(replacement.vertexCount); delta.Read(replacement, 0, 0);
            replacement.ClearBlendShapes(); replacement.AddBlendShapeFrame("Allowed", 100, delta.Vertices, delta.Normals, delta.Tangents);
            renderer.sharedMesh = replacement; renderer.SetBlendShapeWeight(0, 20);
            if (removeForeign) Object.DestroyImmediate(foreign);
            ALTOPlugin.PrepareInputs(context);
            var clip = StaticCurve(Binding("Allowed", "Scoped"), 20);
            var entries = LateShapeEntries(Controller(clip));
            int changes = LateShapes(context, entries);
            var outputCurve = entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurve(Binding("Allowed", "Scoped"));
            if (restriction == "Mmd")
            {
                // MMD observes recognized current names. Renaming a source to
                // an ordinary output does not create an authored exclusion.
                Assert.That(changes, Is.EqualTo(1));
                Assert.That(renderer.sharedMesh.GetBlendShapeIndex("Allowed"), Is.EqualTo(-1));
                Assert.That(outputCurve, Is.Null);
            }
            else
            {
                Assert.That(renderer.sharedMesh, Is.SameAs(replacement));
                Assert.That(outputCurve, Is.Not.Null);
                Assert.That(context.GetState<OptimizationBuildReport>().Messages.Any(m => m.Contains("contributor/name map")), Is.True);
            }
        }

        [Test]
        public void LateBlendShapes_UniformPermissionsStillAllowVerifiedEarlyCopies()
        {
            Object.DestroyImmediate(renderer);
            renderer = PolicyChild("First").AddComponent<SkinnedMeshRenderer>();
            var original = Mesh("Allowed"); renderer.SetBlendShapeWeight(0, 20);
            var foreign = PolicyChild("Second").AddComponent<SkinnedMeshRenderer>();
            foreign.sharedMesh = Track(Object.Instantiate(original));
            var context = LateShapeContext(false);
            renderer.sharedMesh = Track(Object.Instantiate(original)); renderer.SetBlendShapeWeight(0, 20);
            ALTOPlugin.PrepareInputs(context);
            var clip = StaticCurve(Binding("Allowed", "First"), 20);
            AnimationUtility.SetEditorCurve(clip, Binding("Allowed", "Second"), AnimationCurve.Linear(0, 5, 1, 80));
            clip.SampleAnimation(root, .25f);
            var before = AssemblyWorldVertices(renderer).Concat(AssemblyWorldVertices(foreign)).ToArray();
            var entries = LateShapeEntries(Controller(clip));
            Assert.That(LateShapes(context, entries), Is.EqualTo(1));
            CommitAnimator(entries.Values.Single(), root).animationClips.Single().SampleAnimation(root, .25f);
            var after = AssemblyWorldVertices(renderer).Concat(AssemblyWorldVertices(foreign)).ToArray();
            Assert.That(after.Zip(before, Vector3.Distance).Max(), Is.LessThan(.00001f));
            Assert.That(foreign.sharedMesh.GetBlendShapeIndex("Allowed"), Is.EqualTo(0));
        }

        [TestCase(false)] [TestCase(true)]
        public void LateBlendShapes_CurrentMmdNamesDoNotProtectUnrelatedCopiedShapes(bool ownedMapping)
        {
            var original = Mesh("Allowed", "まばたき");
            root.AddComponent<ALTOTraceAndOptimizeBlendShapes>().OptimizeZeroResetAnimations = false;
            var context = LateShapeContext(false);
            var copy = Track(Object.Instantiate(original));
            if (ownedMapping) context.GetState<OptimizationState>().Provenance.RecordMeshRewrite(renderer, original, copy);
            renderer.sharedMesh = copy;
            ALTOPlugin.PrepareInputs(context);
            Assert.That(LateShapes(context, LateShapeEntries(Controller(Clip("Allowed", "まばたき")))), Is.EqualTo(1));
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("まばたき"), Is.GreaterThanOrEqualTo(0));
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("Allowed"), Is.EqualTo(-1));
        }

        [Test]
        public void LateBlendShapes_RemovedProtectedSourceStillConstrainsLaterAmbiguousCopies()
        {
            var original = Mesh("Allowed");
            var foreign = PolicyChild("Foreign").AddComponent<SkinnedMeshRenderer>();
            var foreignMesh = Track(Object.Instantiate(original));
            foreignMesh.ClearBlendShapes(); Frame(foreignMesh, "Protected source", 100, Vector3.right);
            foreign.sharedMesh = foreignMesh;
            foreign.gameObject.AddComponent<BlendShapeOptimizationExclude>().BlendShapes.Add("Protected source");
            var context = LateShapeContext(false);
            ALTOPlugin.PrepareInputs(context);

            // An audited region rewrite removes the protected current shape.
            // A later generator still holds the previous source mesh and can
            // copy and rename it into the otherwise eligible renderer.
            var removed = Track(Object.Instantiate(foreignMesh)); removed.ClearBlendShapes();
            context.GetState<OptimizationState>().Provenance.RecordMeshRewrite(foreign, foreignMesh, removed);
            foreign.sharedMesh = removed;
            var replacement = Track(Object.Instantiate(foreignMesh));
            var delta = new ShapeDelta(replacement.vertexCount); delta.Read(replacement, 0, 0);
            replacement.ClearBlendShapes(); replacement.AddBlendShapeFrame("Allowed", 100, delta.Vertices, delta.Normals, delta.Tangents);
            renderer.sharedMesh = replacement;
            ALTOPlugin.PrepareInputs(context);
            var entries = LateShapeEntries(Controller(Clip("Allowed")));
            Assert.That(LateShapes(context, entries), Is.Zero);
            Assert.That(renderer.sharedMesh, Is.SameAs(replacement));
            Assert.That(entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurve(Binding("Allowed")), Is.Not.Null);
        }

        [Test]
        public void LateBlendShapes_DeferredOwnRewriteFingerprintsValidateTheNextUntrustedBoundary()
        {
            var original = Mesh("Retained"); var context = LateShapeContext();
            var provenance = context.GetState<OptimizationState>().Provenance;
            // These package-owned rewrites run after the first optimization
            // boundary, when universal early authorization is already closed.
            provenance.LockEarly();
            var messages = context.GetState<OptimizationBuildReport>().Messages;
            provenance.ReportSignatures(messages); int previousMessages = messages.Count;
            long validated;
            using (provenance.BeginBoundary())
            {
                Assert.That(provenance.TryGet(renderer, out _), Is.True);
                validated = provenance.SignatureComputations;
                var first = Track(Object.Instantiate(original));
                first.vertices = first.vertices.Select(v => v + Vector3.right).ToArray();
                provenance.RecordMeshRewrite(renderer, original, first); renderer.sharedMesh = first;
                var second = Track(Object.Instantiate(first));
                second.vertices = second.vertices.Select(v => v + Vector3.up).ToArray();
                provenance.RecordMeshRewrite(renderer, first, second); renderer.sharedMesh = second;
                Assert.That(provenance.TryGet(renderer, out _), Is.True);
                Assert.That(provenance.SignatureComputations, Is.EqualTo(validated), "Owned intermediate copies require no full-frame hash");
            }
            Assert.That(provenance.SignatureComputations, Is.EqualTo(validated + 1), "The final baseline must exist before returning to external code");
            Assert.That(messages.Count, Is.GreaterThan(previousMessages), "Disposal must publish the deferred hashing cost");
            using (provenance.BeginBoundary()) Assert.That(provenance.TryGet(renderer, out _), Is.True);
            Assert.That(provenance.SignatureComputations, Is.EqualTo(validated + 2), "An untrusted boundary validates the final native bytes");
            renderer.sharedMesh.ClearBlendShapes(); Frame(renderer.sharedMesh, "Retained", 100, Vector3.forward);
            using (provenance.BeginBoundary()) Assert.That(provenance.TryGet(renderer, out _), Is.False, "An in-place external edit must invalidate the proof");
        }

        [Test]
        public void LateBlendShapes_EmptyScopeDoesNotReadForeignMeshFrames()
        {
            Mesh("Foreign");
            var settings = root.AddComponent<ALTOTraceAndOptimizeBlendShapes>(); settings.ScopeRoot = PolicyChild("Empty scope").transform;
            var context = LateShapeContext();
            Assert.That(context.GetState<OptimizationState>().Provenance.SignatureComputations, Is.Zero);
            Assert.That(LateShapes(context, LateShapeEntries(Controller(Clip("Foreign")))), Is.Zero);
            Assert.That(context.GetState<OptimizationState>().Provenance.SignatureComputations, Is.Zero);
        }

        [Test]
        public void LateBlendShapes_AuthoredExclusionsAndDescriptorNamesSurviveMarkerRemoval()
        {
            var original = Mesh("Free", "Custom", "まばたき", "Viseme", "Eyelid");
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.VisemeSkinnedMesh = renderer; descriptor.VisemeBlendShapes = new[] { "Viseme" };
            var eyes = descriptor.customEyeLookSettings; eyes.eyelidsSkinnedMesh = renderer;
            eyes.eyelidsBlendshapes = new[] { 4, -1, -1 }; descriptor.customEyeLookSettings = eyes;
            var marker = root.AddComponent<BlendShapeOptimizationExclude>(); marker.BlendShapes.Add("Custom");
            root.AddComponent<ALTOTraceAndOptimizeBlendShapes>().OptimizeZeroResetAnimations = false;
            var context = LateShapeContext(false);
            Object.DestroyImmediate(marker); Object.DestroyImmediate(root.GetComponent<ALTOTraceAndOptimizeBlendShapes>());
            var clip = Clip("Free", "Custom", "まばたき", "Viseme", "Eyelid");
            var entries = LateShapeEntries(Controller(clip));
            Assert.That(LateShapes(context, entries), Is.EqualTo(1));
            Assert.That(renderer.sharedMesh.blendShapeCount, Is.EqualTo(4));
            foreach (string name in new[] { "Custom", "まばたき", "Viseme", "Eyelid" })
                Assert.That(renderer.sharedMesh.GetBlendShapeIndex(name), Is.GreaterThanOrEqualTo(0));
            Assert.That(descriptor.customEyeLookSettings.eyelidsBlendshapes[0], Is.EqualTo(3));
            Assert.That(original.blendShapeCount, Is.EqualTo(5));
        }

        [TestCase(false)] [TestCase(true)]
        public void LateBlendShapes_ObservedAuthoredMarkersSurviveLaterPasses(bool beforeEarlyPass)
        {
            var original = Mesh("Free", "Protected");
            renderer.SetBlendShapeWeight(0, 70); renderer.SetBlendShapeWeight(1, 70);
            var expected = AssemblyWorldVertices(renderer);
            renderer.SetBlendShapeWeight(0, 0); renderer.SetBlendShapeWeight(1, 0);
            var context = LateShapeContext();
            var clip = StaticCurve(Binding("Free"), 70);
            AnimationUtility.SetEditorCurve(clip, Binding("Protected"), beforeEarlyPass
                ? AnimationCurve.Constant(0, 1, 70) : AnimationCurve.Linear(0, 0, 1, 100));
            var authoringValues = AnimationUtility.GetEditorCurve(clip, Binding("Protected")).keys.Select(k => k.value).ToArray();
            var entries = LateShapeEntries(Controller(clip));
            BlendShapeOptimizationExclude marker = null;
            if (beforeEarlyPass)
            {
                marker = root.AddComponent<BlendShapeOptimizationExclude>(); marker.BlendShapes.Add("Protected");
            }
            Assert.That(ALTOPlugin.ApplyCurrent(root, entries, null, new ResolvedParameterPolicy(),
                context.GetState<OptimizationState>(), context.GetState<OptimizationPolicy>(),
                context.GetState<OptimizationBuildReport>(), value => Track(value), false), Is.EqualTo(1));
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("Free"), Is.EqualTo(-1), "An unrelated name remains optimizable");
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("Protected"), Is.GreaterThanOrEqualTo(0));
            if (!beforeEarlyPass)
            {
                marker = root.AddComponent<BlendShapeOptimizationExclude>(); marker.BlendShapes.Add("Protected");
                entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single()
                    .SetFloatCurve(Binding("Protected"), AnimationCurve.Constant(0, 1, 70));
            }
            Assert.That(LateShapes(context, entries), Is.Zero);
            Object.DestroyImmediate(marker);
            var retained = renderer.sharedMesh;
            Assert.That(LateShapes(context, entries), Is.Zero, "An observed authored exclusion survives marker consumption");
            Assert.That(renderer.sharedMesh, Is.SameAs(retained));
            Assert.That(entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single()
                .GetFloatCurve(Binding("Protected")), Is.Not.Null);
            CommitAnimator(entries.Values.Single(), root).animationClips.Single().SampleAnimation(root, .25f);
            Assert.That(AssemblyWorldVertices(renderer).Zip(expected, Vector3.Distance).Max(), Is.LessThan(.00001f));
            Assert.That(original.blendShapeCount, Is.EqualTo(2));
            Assert.That(AnimationUtility.GetEditorCurve(clip, Binding("Protected")).keys.Select(k => k.value), Is.EqualTo(authoringValues));
        }

        [TestCase(false)] [TestCase(true)]
        public void LateBlendShapes_ExportOwnershipBelongsToTheWrittenMarker(bool replaceMarker)
        {
            var original = Mesh("Free", "Native", "Ordinary");
            renderer.SetBlendShapeWeight(1, 70); renderer.SetBlendShapeWeight(2, 30);
            var expected = AssemblyWorldVertices(renderer);
            renderer.SetBlendShapeWeight(1, 0); renderer.SetBlendShapeWeight(2, 0);
            var descriptor = NativeFixture("Native");
            root.AddComponent<ALTOTraceAndOptimizeBlendShapes>().OptimizeZeroResetAnimations = false;
            var context = LateShapeContext();
            var clip = StaticCurve(Binding("Free"), 0);
            AnimationUtility.SetEditorCurve(clip, Binding("Native"), AnimationCurve.Constant(0, 1, 70));
            AnimationUtility.SetEditorCurve(clip, Binding("Ordinary"), AnimationCurve.Linear(0, 0, 1, 100));
            var entries = LateShapeEntries(Controller(clip));
            Assert.That(ALTOPlugin.ApplyCurrent(root, entries, null, new ResolvedParameterPolicy(),
                context.GetState<OptimizationState>(), context.GetState<OptimizationPolicy>(),
                context.GetState<OptimizationBuildReport>(), value => Track(value), false), Is.EqualTo(1));
            var marker = root.GetComponent<BlendShapeOptimizationExclude>();
            Assert.That(marker, Is.Not.Null);
            Assert.That(marker.BlendShapes, Is.EquivalentTo(new[] { "Native" }));

            // MMD lookup also considered this absent name, but ALTO never wrote
            // it into the marker. Adding it now is an authored selector.
            marker.BlendShapes.Add("まばたき");
            Assert.That(context.GetState<OptimizationState>().Provenance.CaptureAdditionalProtections(renderer),
                Is.EquivalentTo(new[] { "まばたき" }));
            marker.BlendShapes.Remove("まばたき");
            if (replaceMarker)
            {
                Object.DestroyImmediate(marker);
                marker = root.AddComponent<BlendShapeOptimizationExclude>(); marker.BlendShapes.Add("Native");
            }
            descriptor.enableFaceTracking = false;
            entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single()
                .SetFloatCurve(Binding("Ordinary"), AnimationCurve.Constant(0, 1, 30));
            Assert.That(LateShapes(context, entries), Is.EqualTo(replaceMarker ? 1 : 2));
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("Ordinary"), Is.EqualTo(-1), "The explicit selector protects only its chosen name");
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("Native") >= 0, Is.EqualTo(replaceMarker));
            Object.DestroyImmediate(marker);
            var retained = renderer.sharedMesh;
            Assert.That(LateShapes(context, entries), Is.Zero);
            Assert.That(renderer.sharedMesh, Is.SameAs(retained));
            CommitAnimator(entries.Values.Single(), root).animationClips.Single().SampleAnimation(root, .25f);
            Assert.That(AssemblyWorldVertices(renderer).Zip(expected, Vector3.Distance).Max(), Is.LessThan(.00001f));
            Assert.That(original.blendShapeCount, Is.EqualTo(3));
            Assert.That(AnimationUtility.GetEditorCurve(clip, Binding("Ordinary")).keys.Select(k => k.value), Is.EqualTo(new[] { 0f, 100f }));
        }

        [TestCase("Replacement")] [TestCase("InPlace")] [TestCase("RewriteAfterUnknown")]
        public void LateBlendShapes_UnknownMeshMutationCannotBorrowDestinationScope(string mutation)
        {
            var original = Mesh("Shape"); renderer.SetBlendShapeWeight(0, 25);
            var context = LateShapeContext();
            context.GetState<OptimizationState>().Provenance.LockEarly();
            var current = mutation == "InPlace" ? original : Track(Object.Instantiate(original));
            current.ClearBlendShapes(); Frame(current, "Shape", 100, Vector3.forward);
            renderer.sharedMesh = current;
            if (mutation == "RewriteAfterUnknown")
            {
                var rewritten = Track(Object.Instantiate(current));
                context.GetState<OptimizationState>().Provenance.RecordMeshRewrite(renderer, current, rewritten);
                renderer.sharedMesh = current = rewritten;
            }
            var entries = LateShapeEntries(Controller(StaticCurve(Binding("Shape"), 25)));
            Assert.That(LateShapes(context, entries), Is.Zero);
            Assert.That(renderer.sharedMesh, Is.SameAs(current));
            Assert.That(entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single().GetFloatCurve(Binding("Shape")), Is.Not.Null);
        }

        [TestCase("OutOfScope")] [TestCase("Mmd")] [TestCase("Marker")]
        [TestCase("OptionOff")] [TestCase("NoInitialization")]
        public void LateBlendShapes_KnownMergeKeepsPerSourcePermissionsAndRenamedProtections(string restriction)
        {
            var pair = MergePair(); string name = restriction == "Mmd" ? "まばたき" : "Shape";
            foreach (var part in new[] { pair.First, pair.Second })
            { Frame(part.sharedMesh, name, 100, Vector3.up); part.SetBlendShapeWeight(0, 20); }
            var firstMesh = pair.First.sharedMesh; var secondMesh = pair.Second.sharedMesh;
            var settings = root.AddComponent<ALTOTraceAndOptimizeBlendShapes>(); settings.PreserveMmdBlendShapes = false;
            settings.OptimizeZeroResetAnimations = false;
            if (restriction == "OutOfScope") settings.ScopeRoot = pair.First.transform;
            if (restriction == "Marker") pair.Second.gameObject.AddComponent<BlendShapeOptimizationExclude>().BlendShapes.Add(name);
            if (restriction == "OptionOff" || restriction == "Mmd")
            {
                var inner = pair.Second.gameObject.AddComponent<ALTOTraceAndOptimizeBlendShapes>();
                inner.OptimizeZeroResetAnimations = false;
                inner.FreezeConstantAnimatedBlendShapes = restriction != "OptionOff";
            }
            if (restriction == "NoInitialization") pair.Second.gameObject.AddComponent<ALTOOptimizationPolicy>().AllowInitialValueChanges = false;
            var context = LateShapeContext(); var clip = Clip();
            foreach (var part in new[] { pair.First, pair.Second })
                AnimationUtility.SetEditorCurve(clip, Binding(name, part.name), AnimationCurve.Constant(0, 1, 70));
            clip.SampleAnimation(root, .5f);
            var expected = AssemblyWorldVertices(pair.First).Concat(AssemblyWorldVertices(pair.Second)).ToArray();
            pair.First.SetBlendShapeWeight(0, 20); pair.Second.SetBlendShapeWeight(0, 20);
            var entries = LateShapeEntries(Controller(clip)); var controller = entries.Values.Single();
            foreach (var marker in root.GetComponentsInChildren<BlendShapeOptimizationExclude>(true)) Object.DestroyImmediate(marker);
            foreach (var marker in root.GetComponentsInChildren<ALTOTraceAndOptimizeBlendShapes>(true)) Object.DestroyImmediate(marker);
            MeshOptimizationPass.Merge(MergeCandidates(new[] { pair.First, pair.Second }, controller), "First", true,
                value => Track(value), shapeProvenance: context.GetState<OptimizationState>().Provenance);
            Assert.That(pair.Second == null, Is.True);
            string retained = pair.First.sharedMesh.GetBlendShapeName(1);
            Assert.That(retained, Is.Not.EqualTo(name), "The source name was relocated");
            bool runtimeNameWasRenamed = restriction == "Mmd";
            Assert.That(LateShapes(context, entries), Is.EqualTo(runtimeNameWasRenamed ? 2 : 1));
            Assert.That(pair.First.sharedMesh.blendShapeCount, Is.EqualTo(runtimeNameWasRenamed ? 0 : 1));
            if (!runtimeNameWasRenamed) Assert.That(pair.First.sharedMesh.GetBlendShapeName(0), Is.EqualTo(retained));
            CommitAnimator(controller, root).animationClips.Single().SampleAnimation(root, .5f);
            Assert.That(AssemblyWorldVertices(pair.First).Zip(expected, Vector3.Distance).Max(), Is.LessThan(.00001f));
            Assert.That(firstMesh.blendShapeCount, Is.EqualTo(1)); Assert.That(secondMesh.blendShapeCount, Is.EqualTo(1));
        }

        [TestCase("Allowed")] [TestCase("OutOfScope")] [TestCase("OptionOff")]
        [TestCase("Marker")] [TestCase("NoInitialization")] [TestCase("ExcludedAnimation")]
        public void LateBlendShapes_SharedMappedShapeIntersectsEveryContributor(string restriction)
        {
            var pair = MergePair();
            foreach (var part in new[] { pair.First, pair.Second })
            { Frame(part.sharedMesh, "Shared", 100, Vector3.up); part.SetBlendShapeWeight(0, 20); }
            var options = root.AddComponent<ALTOTraceAndOptimizeBlendShapes>(); options.PreserveMmdBlendShapes = false;
            options.OptimizeZeroResetAnimations = false;
            if (restriction == "OutOfScope") options.ScopeRoot = pair.First.transform;
            if (restriction == "Marker") pair.Second.gameObject.AddComponent<BlendShapeOptimizationExclude>().BlendShapes.Add("Shared");
            if (restriction == "NoInitialization") pair.Second.gameObject.AddComponent<ALTOOptimizationPolicy>().AllowInitialValueChanges = false;
            var clip = Clip();
            foreach (var part in new[] { pair.First, pair.Second })
                AnimationUtility.SetEditorCurve(clip, Binding("Shared", part.name), restriction == "ExcludedAnimation"
                    ? AnimationCurve.Linear(0, 10, 1, 70) : AnimationCurve.Constant(0, 1, 70));
            if (restriction == "OptionOff" || restriction == "ExcludedAnimation")
            {
                var inner = pair.Second.gameObject.AddComponent<ALTOTraceAndOptimizeBlendShapes>();
                inner.OptimizeZeroResetAnimations = false;
                inner.FreezeConstantAnimatedBlendShapes = restriction != "OptionOff";
                if (restriction == "ExcludedAnimation") options.ExcludedAnimations.Add(clip);
            }
            var context = LateShapeContext();
            using var assembly = SkinnedMeshAssembler.Create(new[] { pair.First, pair.Second }, true,
                nativeFaceShapes: new Dictionary<SkinnedMeshRenderer, HashSet<string>> {
                    [pair.First] = new() { "Shared" }, [pair.Second] = new() { "Shared" } });
            context.GetState<OptimizationState>().Provenance.RecordMerge(pair.First, assembly.Mesh, assembly.ShapeNames);
            pair.First.sharedMesh = Track(assembly.Mesh); pair.First.bones = assembly.Bones;
            pair.First.SetBlendShapeWeight(0, 20); assembly.Mesh = null;
            Object.DestroyImmediate(pair.Second);
            var entries = LateShapeEntries(Controller(clip));
            entries.Values.Single().AllReachableNodes().OfType<VirtualClip>().Single().SetFloatCurve(Binding("Shared", "Second"), null);
            Assert.That(LateShapes(context, entries), Is.EqualTo(restriction == "Allowed" ? 1 : 0));
            Assert.That(pair.First.sharedMesh.blendShapeCount, Is.EqualTo(restriction == "Allowed" ? 0 : 1));
        }

        [Test]
        public void LateBlendShapes_ZeroResetWorklistStrictlyReducesShapesAndStops()
        {
            Mesh("First", "Second"); renderer.SetBlendShapeWeight(0, 20); renderer.SetBlendShapeWeight(1, 40);
            var options = root.AddComponent<ALTOTraceAndOptimizeBlendShapes>();
            options.FreezeConstantAnimatedBlendShapes = false; options.RemoveIneffectiveBlendShapes = false;
            options.IgnoreUnreachableStates = false;
            var context = LateShapeContext(); var entries = LateShapeEntries(Controller(Clip("First", "Second")));
            Assert.That(LateShapes(context, entries), Is.EqualTo(1));
            var output = renderer.sharedMesh;
            Assert.That(output.blendShapeCount, Is.EqualTo(1));
            Assert.That(LateShapes(context, entries), Is.Zero);
            Assert.That(renderer.sharedMesh, Is.SameAs(output));
        }

        [Test]
        public void LateBlendShapes_CancellationDoesNotEnableAnotherScopesIneffectiveOption()
        {
            var pair = MergePair();
            foreach (var part in new[] { pair.First, pair.Second })
            { Frame(part.sharedMesh, "A", 100, Vector3.up); Frame(part.sharedMesh, "B", 100, Vector3.right); }
            var options = root.AddComponent<ALTOTraceAndOptimizeBlendShapes>(); options.FreezeConstantAnimatedBlendShapes = false;
            var inner = pair.Second.gameObject.AddComponent<ALTOTraceAndOptimizeBlendShapes>();
            inner.FreezeConstantAnimatedBlendShapes = false; inner.RemoveIneffectiveBlendShapes = false;
            var context = LateShapeContext(); var clip = Clip();
            foreach (var part in new[] { pair.First, pair.Second })
                foreach (string name in new[] { "A", "B" }) AnimationUtility.SetEditorCurve(clip, Binding(name, part.name), AnimationCurve.Constant(0, 1, 0));
            var entries = LateShapeEntries(Controller(clip));
            MeshOptimizationPass.Merge(MergeCandidates(new[] { pair.First, pair.Second }, entries.Values.Single()), "First", true,
                value => Track(value), shapeProvenance: context.GetState<OptimizationState>().Provenance);
            Assert.That(LateShapes(context, entries), Is.EqualTo(3));
            Assert.That(pair.First.sharedMesh.blendShapeCount, Is.EqualTo(1));
            Assert.That(LateShapes(context, entries), Is.Zero);
        }
    }
}
