using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false)] [TestCase(true)]
        public void MeshMergingRetainsNativeOutputOfIndependentPreservedMorphInputs(bool writeDefaults)
        {
            Mesh("Sample"); var pair = MergePair();
            Frame(pair.First.sharedMesh, "Custom", 100, Vector3.up * 0.3f);
            Frame(pair.Second.sharedMesh, "Custom", 100, Vector3.right * 0.2f);
            var firstClip = StaticCurve(Binding("Custom", "First"), 100);
            var secondClip = StaticCurve(Binding("Custom", "Second"), 80);
            var firstKeys = AnimationUtility.GetEditorCurve(firstClip, Binding("Custom", "First")).keys;
            var secondKeys = AnimationUtility.GetEditorCurve(secondClip, Binding("Custom", "Second")).keys;
            var source = TreeController(("Preserved A", firstClip), ("Preserved B", secondClip));
            source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var preserved = new[] { "Preserved A", "Preserved B" };
            var entries = new Dictionary<object, VirtualAnimatorController> { [VRCAvatarDescriptor.AnimLayerType.FX] = controller };
            var index = AvatarAnimationIndex.Create(root, entries, null, preserved);
            Assert.That(index.Bindings.All(u => u.Protected && u.PreservedInput && !u.Immutable), Is.True);
            var group = MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, controller,
                preservedParameters: preserved), true).Single();
            Assert.That(group.All(c => !c.Pinned), Is.True);

            var before = new List<Vector3[]>(); var values = new[] { 0f, 0.2f, 1, 0.7f, 1.4f, 0 };
            SampleFrozen(source, (p, i) => { p.SetFloat("Preserved A", values[i]); p.SetFloat("Preserved B", 1 - values[i] * 0.5f); }, values.Length,
                () => { before.Add(AssemblyWorldVertices(pair.First).Concat(AssemblyWorldVertices(pair.Second)).ToArray()); return 0; });
            Assert.That(Vector3.Distance(before[0][0], before[2][0]), Is.GreaterThan(0.1f), "The fixture must exercise a changing input.");
            pair.First.SetBlendShapeWeight(0, 0); pair.Second.SetBlendShapeWeight(0, 0);
            MeshOptimizationPass.Merge(group, "First", true, mesh => Track(mesh));
            var after = new List<Vector3[]>();
            SampleFrozen(CommitAnimator(controller, root), (p, i) => { p.SetFloat("Preserved A", values[i]); p.SetFloat("Preserved B", 1 - values[i] * 0.5f); }, values.Length,
                () => { after.Add(AssemblyWorldVertices(pair.First)); return 0; });
            for (int frame = 0; frame < values.Length; frame++)
                for (int vertex = 0; vertex < before[frame].Length; vertex++)
                    Assert.That(Vector3.Distance(after[frame][vertex], before[frame][vertex]), Is.LessThan(0.00001f), "Frame " + frame + ", vertex " + vertex);
            Assert.That(AnimationUtility.GetEditorCurve(firstClip, Binding("Custom", "First")).keys, Is.EqualTo(firstKeys));
            Assert.That(AnimationUtility.GetEditorCurve(secondClip, Binding("Custom", "Second")).keys, Is.EqualTo(secondKeys));
        }

        [TestCase("m_Enabled", false)] [TestCase("m_Enabled", true)] [TestCase("material._Color.r", false)]
        public void PreservedInputsRequireMatchingRendererSignalsToMerge(string property, bool different)
        {
            var pair = MergePair(); var clip = Clip();
            foreach (var part in new[] { pair.First, pair.Second }) AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(part.name, typeof(SkinnedMeshRenderer), property), AnimationCurve.Constant(0, 1, different && part == pair.Second ? 0 : 1));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(TreeController(("Preserved", clip)));
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller, preservedParameters: new[] { "Preserved" });
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(different ? 2 : 1));
        }

        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)] [TestCase(true, true)]
        public void ImmutableMorphBindingsStillPinTheirOriginalRendererPaths(bool legacy, bool missing)
        {
            var pair = MergePair(); var clip = Clip();
            foreach (var part in new[] { pair.First, pair.Second })
            {
                if (!missing) Frame(part.sharedMesh, "Custom", 100, Vector3.up);
                AnimationUtility.SetEditorCurve(clip, Binding("Custom", part.name), AnimationCurve.Constant(0, 1, 100));
            }
            var context = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var controller = context.Clone(Controller(Clip()));
            var motion = legacy ? context.Clone(clip) : VirtualClip.FromMarker(clip);
            if (legacy) motion.Legacy = true;
            controller.Layers.First().StateMachine.DefaultState.Motion = motion;
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(candidates.All(c => c.Pinned && c.PinReasons.Any(r => r.StartsWith("immutable animation:"))), Is.True);
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(2));
        }

        [TestCase(false)] [TestCase(true)]
        public void MissingMorphTracksDoNotPreventEquivalentMerging(bool reverseOrder)
        {
            Mesh("Sample"); var pair = MergePair();
            Frame(pair.First.sharedMesh, "First shape", 100, Vector3.up * 0.3f);
            Frame(pair.Second.sharedMesh, "Second shape", 100, Vector3.right * 0.2f);
            var clip = Clip();
            AnimationUtility.SetEditorCurve(clip, Binding("First shape", "First"), AnimationCurve.Constant(0, 1, 80));
            AnimationUtility.SetEditorCurve(clip, Binding("Second shape", "Second"), AnimationCurve.Constant(0, 1, 60));
            AnimationUtility.SetEditorCurve(clip, Binding("Second shape", "First"), AnimationCurve.Constant(0, 1, 100));
            AnimationUtility.SetEditorCurve(clip, Binding("First shape", "Second"), AnimationCurve.Constant(0, 1, 100));
            var source = TreeController(("Preserved", clip));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var parts = reverseOrder ? new[] { pair.Second, pair.First } : new[] { pair.First, pair.Second };
            var group = MeshMergeAnalysis.Group(MergeCandidates(parts, controller, preservedParameters: new[] { "Preserved" }), true).Single();
            var before = new List<Vector3[]>(); var values = new[] { 0f, 0.3f, 1, 1.4f, 0 };
            SampleFrozen(source, (p, i) => p.SetFloat("Preserved", values[i]), values.Length,
                () => { before.Add(parts.SelectMany(AssemblyWorldVertices).ToArray()); return 0; });
            Assert.That(Vector3.Distance(before[0][0], before[2][0]), Is.GreaterThan(0.1f));
            foreach (var part in parts) part.SetBlendShapeWeight(0, 0);
            var destination = parts[0]; var removedPath = parts[1].name;
            MeshOptimizationPass.Merge(group, destination.name, true, mesh => Track(mesh));
            var output = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(output.GetFloatCurveBindings().Any(b => b.path == removedPath), Is.False);
            Assert.That(output.GetFloatCurveBindings().Count(b => b.propertyName.StartsWith("blendShape.")), Is.EqualTo(3),
                "Keep the ineffective destination track; remove only the missing source track.");
            var after = new List<Vector3[]>();
            SampleFrozen(CommitAnimator(controller, root), (p, i) => p.SetFloat("Preserved", values[i]), values.Length,
                () => { after.Add(AssemblyWorldVertices(destination)); return 0; });
            for (int frame = 0; frame < values.Length; frame++)
                for (int vertex = 0; vertex < before[frame].Length; vertex++)
                    Assert.That(Vector3.Distance(after[frame][vertex], before[frame][vertex]), Is.LessThan(0.00001f));
            Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.EqualTo(4));
        }

        [Test]
        public void RemovingTheLastMissingSourceTrackRetainsClipDuration()
        {
            var pair = MergePair(); var clip = Clip();
            var binding = Binding("Missing", "Second");
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, 3, 100));
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            var group = MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, controller), true).Single();
            MeshOptimizationPass.Merge(group, "First", true, mesh => Track(mesh));
            var output = controller.AllReachableNodes().OfType<VirtualClip>().Single();
            Assert.That(output.GetFloatCurve(binding), Is.Null);
            Assert.That(MotionAnalysis.EndTime(output), Is.EqualTo(3));
            Assert.That(AnimationUtility.GetEditorCurve(clip, binding), Is.Not.Null);
        }

        [Test]
        public void MissingMorphTracksRemainProtectedWhenAClipCanReplaceTheMesh()
        {
            var pair = MergePair(); var clip = Clip();
            var alternate = Track(Object.Instantiate(pair.Second.sharedMesh));
            Frame(alternate, "Missing", 100, Vector3.up);
            AnimationUtility.SetEditorCurve(clip, Binding("Missing", "Second"), AnimationCurve.Constant(0, 1, 100));
            AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("Second", typeof(SkinnedMeshRenderer), "m_Mesh"),
                new[] { new ObjectReferenceKeyframe { value = alternate } });
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(Controller(clip));
            Assert.That(MergeCandidates(new[] { pair.Second }, controller, assertEligible: false).Single(), Is.Null);
            Assert.That(pair.Second.sharedMesh.GetBlendShapeIndex("Missing"), Is.EqualTo(-1));
        }
    }
}
