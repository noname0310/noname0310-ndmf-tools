using System;
using System.Linq;
using Noname.AvatarTools.Build;
using Noname.AvatarTools.ParameterPolicy;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void EarlyBlendShapes_EmptyFirstBoundaryStillLocksUnknownLateInputs()
        {
            Mesh(); var context = LateShapeContext();
            var entries = LateShapeEntries(Controller(Clip()));
            Assert.That(ALTOPlugin.ApplyCurrent(root, entries, null, new ResolvedParameterPolicy(),
                context.GetState<OptimizationState>(), context.GetState<OptimizationPolicy>(),
                context.GetState<OptimizationBuildReport>(), value => Track(value), false), Is.Zero);
            var generated = Mesh("Late generated");
            Assert.That(LateShapes(context, entries), Is.Zero);
            Assert.That(renderer.sharedMesh, Is.SameAs(generated));
            Assert.That(generated.blendShapeCount, Is.EqualTo(1));
        }

        [TestCase("Replacement")] [TestCase("InPlace")]
        [TestCase("NewRenderer")] [TestCase("InitiallyEmpty")] [TestCase("ExistingEmptyRenderer")]
        public void EarlyBlendShapes_WholeAvatarPermissionIncludesGeneratedInputs(string mutation)
        {
            var original = mutation == "InitiallyEmpty" ? Mesh() : Mesh("Original");
            SkinnedMeshRenderer empty = null;
            if (mutation == "ExistingEmptyRenderer")
            {
                empty = PolicyChild("Generated renderer").AddComponent<SkinnedMeshRenderer>();
                empty.sharedMesh = Track(Object.Instantiate(original)); empty.sharedMesh.ClearBlendShapes();
            }
            var context = LateShapeContext(false);
            Assert.That(context.GetState<OptimizationState>().Provenance.HasScopedShapes, Is.True,
                "An explicit avatar-root scope covers future generated morphs too");
            var generated = mutation == "InPlace" ? original : Track(Object.Instantiate(original));
            generated.vertices = generated.vertices.Select(v => v + Vector3.up * 3).ToArray();
            generated.ClearBlendShapes(); Frame(generated, "Generated", 100, Vector3.zero);
            if (mutation == "NewRenderer" || mutation == "ExistingEmptyRenderer")
            {
                renderer.sharedMesh = null;
                renderer = empty != null ? empty : PolicyChild("Generated renderer").AddComponent<SkinnedMeshRenderer>();
            }
            renderer.sharedMesh = generated;
            string path = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform);
            var clip = StaticCurve(Binding("Generated", path), 0);
            AnimationUtility.SetEditorCurve(clip, Binding("Generated", path), AnimationCurve.Linear(0, 0, 1, 100));
            var entries = LateShapeEntries(Controller(clip));
            ALTOPlugin.PrepareInputs(context);
            context.GetState<OptimizationState>().Provenance.LockEarly();
            Assert.That(LateShapes(context, entries), Is.EqualTo(1));
            Assert.That(renderer.sharedMesh.blendShapeCount, Is.Zero);
            Assert.That(generated.blendShapeCount, Is.EqualTo(1), "Optimization leaves its generated input immutable");
            if (mutation != "InPlace") Assert.That(original.blendShapeCount, Is.EqualTo(mutation == "InitiallyEmpty" ? 0 : 1));
        }

        [TestCase("Allowed", false)] [TestCase("Allowed", true)]
        [TestCase("OptionOff", false)] [TestCase("OptionOff", true)]
        [TestCase("InitializationOff", false)] [TestCase("InitializationOff", true)]
        public void EarlyBlendShapes_WholeAvatarPermissionMeetsEmptyPolicyRegions(string restriction, bool removeRegion)
        {
            var original = Mesh("Original");
            var empty = PolicyChild("Empty policy region");
            if (restriction == "OptionOff")
            {
                var nested = empty.AddComponent<ALTOTraceAndOptimizeBlendShapes>();
                nested.FreezeConstantAnimatedBlendShapes = false;
            }
            if (restriction == "InitializationOff") empty.AddComponent<ALTOOptimizationPolicy>().AllowInitialValueChanges = false;
            var context = LateShapeContext();
            renderer.sharedMesh = null;
            renderer = PolicyChild("Generated renderer").AddComponent<SkinnedMeshRenderer>();
            renderer.transform.SetParent(empty.transform, false);
            var generated = Track(Object.Instantiate(original)); generated.ClearBlendShapes();
            Frame(generated, "Generated", 100, Vector3.up); renderer.sharedMesh = generated;
            renderer.SetBlendShapeWeight(0, 20);
            renderer.transform.SetParent(root.transform, false);
            if (removeRegion) Object.DestroyImmediate(empty);
            var clip = StaticCurve(Binding("Generated", "Generated renderer"), 70);
            clip.SampleAnimation(root, .25f); var expected = AssemblyWorldVertices(renderer);
            renderer.SetBlendShapeWeight(0, 20);
            var entries = LateShapeEntries(Controller(clip));
            ALTOPlugin.PrepareInputs(context); context.GetState<OptimizationState>().Provenance.LockEarly();
            Assert.That(LateShapes(context, entries), Is.EqualTo(restriction == "Allowed" ? 1 : 0));
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("Generated") < 0, Is.EqualTo(restriction == "Allowed"));
            CommitAnimator(entries.Values.Single(), root).animationClips.Single().SampleAnimation(root, .25f);
            Assert.That(AssemblyWorldVertices(renderer).Zip(expected, Vector3.Distance).Max(), Is.LessThan(.00001f));
            Assert.That(original.blendShapeCount, Is.EqualTo(1));
            Assert.That(generated.blendShapeCount, Is.EqualTo(1));
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void EarlyBlendShapes_MissingNameExclusionsPreventUniversalPermission(bool removeRegion, bool changedGeometry)
        {
            var original = Mesh("Original");
            var region = PolicyChild("Authored exclusions");
            region.AddComponent<BlendShapeOptimizationExclude>().BlendShapes.Add("Future name");
            var context = LateShapeContext();
            if (removeRegion) Object.DestroyImmediate(region);
            var replacement = Track(Object.Instantiate(original));
            if (changedGeometry) replacement.vertices = replacement.vertices.Select(v => v + Vector3.up).ToArray();
            renderer.sharedMesh = replacement;
            var entries = LateShapeEntries(Controller(StaticCurve(Binding("Original"), 70)));
            ALTOPlugin.PrepareInputs(context);
            Assert.That(LateShapes(context, entries), Is.Zero);
            Assert.That(renderer.sharedMesh, Is.SameAs(replacement));
        }

        [Test]
        public void EarlyBlendShapes_GeneratedOutputsProtectCurrentRuntimeInterfaces()
        {
            var original = Mesh("Old native name"); NativeFixture("Old native name");
            root.AddComponent<ALTOTraceAndOptimizeBlendShapes>().OptimizeZeroResetAnimations = false;
            var context = LateShapeContext(false);
            var generated = Track(Object.Instantiate(original)); generated.ClearBlendShapes();
            foreach (string name in new[] { "Free", "まばたき", "Viseme", "Eyelid", "Current native name" })
                Frame(generated, name, 100, Vector3.right);
            generated.vertices = generated.vertices.Select(v => v + Vector3.up).ToArray();
            renderer.sharedMesh = generated;
            var descriptor = NativeFixture("Current native name");
            descriptor.VisemeSkinnedMesh = renderer; descriptor.VisemeBlendShapes = new[] { "Viseme" };
            var eyes = descriptor.customEyeLookSettings; eyes.eyelidsSkinnedMesh = renderer;
            eyes.eyelidsBlendshapes = new[] { 3, -1, -1 }; descriptor.customEyeLookSettings = eyes;
            var clip = Clip("Free", "まばたき", "Viseme", "Eyelid", "Current native name");
            ALTOPlugin.PrepareInputs(context); context.GetState<OptimizationState>().Provenance.LockEarly();
            Assert.That(LateShapes(context, LateShapeEntries(Controller(clip))), Is.EqualTo(1));
            Assert.That(renderer.sharedMesh.GetBlendShapeIndex("Free"), Is.EqualTo(-1));
            foreach (string name in new[] { "まばたき", "Viseme", "Eyelid", "Current native name" })
                Assert.That(renderer.sharedMesh.GetBlendShapeIndex(name), Is.GreaterThanOrEqualTo(0), name);
            Assert.That(descriptor.customEyeLookSettings.eyelidsBlendshapes[0], Is.EqualTo(2));
            Assert.That(generated.blendShapeCount, Is.EqualTo(5));
            Assert.That(original.blendShapeCount, Is.EqualTo(1));
        }
    }
}
