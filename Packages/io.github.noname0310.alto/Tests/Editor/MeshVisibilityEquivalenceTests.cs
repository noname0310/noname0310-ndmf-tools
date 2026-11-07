using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private AnimationClip VisibilityLeaf(string path, bool enabled, float value, bool discrete = false, float length = 1)
        {
            var clip = Clip();
            var type = enabled ? typeof(SkinnedMeshRenderer) : typeof(GameObject);
            var property = enabled ? "m_Enabled" : "m_IsActive";
            var binding = discrete ? EditorCurveBinding.DiscreteCurve(path, type, property) : EditorCurveBinding.FloatCurve(path, type, property);
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0, length, value));
            return clip;
        }

        private BlendTree VisibilityToggle(string path, bool enabled, string parameter = "Toggle", bool discrete = false, float length = 1)
        {
            var tree = Track(new BlendTree { name = "Toggle " + path, blendType = BlendTreeType.Simple1D,
                blendParameter = parameter, useAutomaticThresholds = false });
            tree.children = new[]
            {
                new ChildMotion { motion = VisibilityLeaf(path, enabled, 0, discrete, length), threshold = 0, timeScale = 1 },
                new ChildMotion { motion = VisibilityLeaf(path, enabled, 1, discrete, length), threshold = 1, timeScale = 1 }
            };
            return tree;
        }

        private AnimatorController DuplicateVisibilityController(bool enabled, bool discrete = false)
        {
            var source = TreeController(("Weight", VisibilityToggle("First", enabled, discrete: discrete)),
                ("Weight", VisibilityToggle("Second", enabled, discrete: discrete, length: 2)));
            source.AddParameter("Toggle", AnimatorControllerParameterType.Float);
            return source;
        }

        private bool[][] SampleVisibility(AnimatorController controller, IReadOnlyList<SkinnedMeshRenderer> parts,
            Action<AnimatorControllerPlayable, int> inputs, int frames = 120)
        {
            var animator = root.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var graph = PlayableGraph.Create("Mesh visibility equivalence");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                var mixer = AnimationLayerMixerPlayable.Create(graph, 1);
                graph.Connect(playable, 0, mixer, 0);
                AnimationPlayableOutput.Create(graph, "Avatar", animator).SetSourcePlayable(mixer);
                graph.Play();
                var samples = new bool[frames][];
                var weights = new[] { 1f, .2f, .8f, 0f };
                for (int i = 0; i < frames; i++)
                {
                    inputs(playable, i);
                    mixer.SetInputWeight(0, weights[i / 13 % weights.Length]);
                    graph.Evaluate(i % 3 == 0 ? .1f : 1f / 60);
                    samples[i] = parts.Select(r => r.enabled && r.gameObject.activeInHierarchy).ToArray();
                }
                return samples;
            }
            finally { graph.Destroy(); Object.DestroyImmediate(animator); }
        }

        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)] [TestCase(true, true)]
        public void SemanticMeshVisibilityMergesSeparateToggleTreesWithNativeEquivalentOutputs(bool enabled, bool initial)
        {
            var pair = MergePair(); var parts = new[] { pair.First, pair.Second };
            void Reset() { foreach (var part in parts.Where(p => p != null)) { part.gameObject.SetActive(enabled || initial); part.enabled = !enabled || initial; } }
            Reset();
            var source = DuplicateVisibilityController(enabled);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var candidates = MergeCandidates(parts, controller);
            Assert.That(MeshMergeAnalysis.SameVisibility(candidates[0], candidates[1], semantic: false), Is.False);
            var group = MeshMergeAnalysis.Group(candidates, true).Single();
            var weights = new[] { 0f, .25f, .5f, .75f, 1f, 1.5f, -.5f };
            void Inputs(AnimatorControllerPlayable p, int frame)
            { p.SetFloat("Toggle", weights[frame % weights.Length]); p.SetFloat("Weight", weights[frame / 7 % weights.Length]); }
            var before = SampleVisibility(source, parts, Inputs);
            Assert.That(before.All(f => f[0] == f[1]), Is.True, "The original native Animator must agree before merging.");
            Assert.That(before.Select(f => f[0]).Distinct().Count(), Is.EqualTo(2), "Exercise both visible and hidden outputs.");
            Reset(); MeshOptimizationPass.Merge(group, "First", true, mesh => Track(mesh));
            var built = CommitAnimator(controller, root);
            Reset();
            var after = SampleVisibility(built, new[] { pair.First }, Inputs);
            Assert.That(after.Select(f => f[0]), Is.EqualTo(before.Select(f => f[0])));
            Assert.That(source.animationClips.SelectMany(AnimationUtility.GetCurveBindings).Any(b => b.path == "Second"), Is.True);
        }

        [Test]
        public void SemanticMeshVisibilityPartitionsOverlappingToggleSets()
        {
            var pair = MergePair(); var parts = new List<SkinnedMeshRenderer> { pair.First, pair.Second };
            foreach (string name in new[] { "C", "D", "E", "F", "G" })
            {
                var part = AssemblyPart(name, pair.First.bones, Vector3.zero);
                part.sharedMaterials = pair.First.sharedMaterials;
                part.lightProbeUsage = pair.First.lightProbeUsage; part.reflectionProbeUsage = pair.First.reflectionProbeUsage;
                parts.Add(part);
            }
            var source = TreeController(parts.SelectMany((p, i) =>
                (i < 3 ? new[] { "X" } : i == 3 ? new[] { "X", "Y" } : new[] { "Y" })
                .Select(parameter => ("Weight", (Motion)VisibilityToggle(p.name, false, parameter)))).ToArray());
            source.AddParameter("X", AnimatorControllerParameterType.Float); source.AddParameter("Y", AnimatorControllerParameterType.Float);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var groups = MeshMergeAnalysis.Group(MergeCandidates(parts.ToArray(), controller), true);
            Assert.That(groups.Select(g => g.Count).OrderBy(n => n), Is.EqualTo(new[] { 1, 3, 3 }));
            Assert.That(groups.Single(g => g.Any(c => c.Renderer.name == "D")).Count, Is.EqualTo(1));
        }

        [TestCase("DifferentParameter")] [TestCase("DifferentWeight")] [TestCase("DifferentInitial")]
        [TestCase("MissingTrack")] [TestCase("AnimatedCurve")] [TestCase("WriteDefaultsOff")]
        [TestCase("Normalized")] [TestCase("Discrete")] [TestCase("Additive")]
        [TestCase("DifferentLayer")] [TestCase("DifferentMask")] [TestCase("DifferentThreshold")]
        [TestCase("RepeatedWriter")] [TestCase("SyncedLayer")]
        public void SemanticMeshVisibilityRetainsUnprovenEvaluationContexts(string reason)
        {
            var pair = MergePair(); var source = DuplicateVisibilityController(false, reason == "Discrete");
            var state = source.layers[0].stateMachine.defaultState;
            var direct = (BlendTree)state.motion; var first = (BlendTree)direct.children[0].motion;
            var second = (BlendTree)direct.children[1].motion;
            switch (reason)
            {
                case "DifferentParameter": second.blendParameter = "Other"; source.AddParameter("Other", AnimatorControllerParameterType.Float); break;
                case "DifferentWeight":
                    var children = direct.children; children[1].directBlendParameter = "Other"; direct.children = children;
                    source.AddParameter("Other", AnimatorControllerParameterType.Float); break;
                case "DifferentInitial": pair.Second.gameObject.SetActive(false); break;
                case "DifferentThreshold": var thresholds = second.children; thresholds[1].threshold = .7f; second.children = thresholds; break;
                case "RepeatedWriter": direct.children = direct.children.Concat(new[] { direct.children[1] }).ToArray(); break;
                case "MissingTrack": var missing = second.children; missing[0].motion = Clip(); second.children = missing; break;
                case "AnimatedCurve": AnimationUtility.SetEditorCurve((AnimationClip)second.children[0].motion,
                    EditorCurveBinding.FloatCurve("Second", typeof(GameObject), "m_IsActive"), AnimationCurve.Linear(0, 0, 1, 1)); break;
                case "WriteDefaultsOff": state.writeDefaultValues = false; break;
                case "Normalized": using (var so = new SerializedObject(direct))
                    { so.FindProperty("m_NormalizedBlendValues").boolValue = true; so.ApplyModifiedPropertiesWithoutUndo(); } break;
                case "Additive": var layers = source.layers; layers[0].blendingMode = AnimatorLayerBlendingMode.Additive; source.layers = layers; break;
                case "DifferentLayer":
                    state.motion = first;
                    var machine = Track(new AnimatorStateMachine()); var other = machine.AddState("Independent"); other.motion = second;
                    source.AddLayer(new AnimatorControllerLayer { name = "Independent", stateMachine = machine, defaultWeight = 1 }); break;
                case "SyncedLayer": source.AddLayer(new AnimatorControllerLayer { name = "Synced", syncedLayerIndex = 0, defaultWeight = 1 }); break;
                case "DifferentMask": var masked = source.layers; masked[0].avatarMask = DifferentRendererMask(); source.layers = masked; break;
            }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, controller), true).Count, Is.EqualTo(2));
        }

        [TestCase(false)] [TestCase(true)]
        public void SemanticMeshVisibilityMatchesEquivalentConstantsButKeepsAbsentStateTracks(bool missing)
        {
            var pair = MergePair(); var clip = VisibilityLeaf("First", true, 0, length: 1);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Second", typeof(SkinnedMeshRenderer), "m_Enabled"),
                new AnimationCurve(new Keyframe(0, 0)));
            var empty = Clip();
            if (missing) AnimationUtility.SetEditorCurve(empty,
                EditorCurveBinding.FloatCurve("First", typeof(SkinnedMeshRenderer), "m_Enabled"), AnimationCurve.Constant(0, 1, 1));
            var source = Controller(clip, empty); source.layers[0].stateMachine.states[1].state.writeDefaultValues = false;
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, controller), true).Count, Is.EqualTo(missing ? 2 : 1));
        }

        [Test]
        public void SemanticMeshVisibilityIgnoresAnUnrelatedAdditiveController()
        {
            var pair = MergePair(); var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var entries = new Dictionary<object, VirtualAnimatorController>
            {
                [VRCAvatarDescriptor.AnimLayerType.FX] = clone.Clone(DuplicateVisibilityController(false)),
                [VRCAvatarDescriptor.AnimLayerType.Additive] = clone.Clone(Controller(Clip()))
            };
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, entries: entries), true).Count, Is.EqualTo(1));
        }

        [TestCase(false)] [TestCase(true)]
        public void SemanticMeshVisibilityPreservesTransitionsLayerWeightsAndDefaultHistory(bool baseDefaults)
        {
            var pair = MergePair(); var parts = new[] { pair.First, pair.Second };
            var source = DuplicateVisibilityController(true);
            source.AddParameter("Gate", AnimatorControllerParameterType.Bool);
            var machine = source.layers[0].stateMachine;
            var toggle = machine.defaultState; toggle.name = "Toggle trees";
            var empty = machine.AddState("Hold previous"); empty.motion = Clip(); empty.writeDefaultValues = false;
            var off = toggle.AddTransition(empty); off.hasExitTime = false; off.duration = .2f;
            off.AddCondition(AnimatorConditionMode.If, 0, "Gate");
            var on = empty.AddTransition(toggle); on.hasExitTime = false; on.duration = .1f;
            on.AddCondition(AnimatorConditionMode.IfNot, 0, "Gate");
            var lowerClip = VisibilityLeaf("First", true, 0);
            AnimationUtility.SetEditorCurve(lowerClip, EditorCurveBinding.FloatCurve("Second", typeof(SkinnedMeshRenderer), "m_Enabled"),
                new AnimationCurve(new Keyframe(0, 0)));
            var lower = Track(new AnimatorStateMachine()); var lowerState = lower.AddState("Lower output");
            lowerState.motion = lowerClip; lowerState.writeDefaultValues = baseDefaults;
            var upper = source.layers[0]; upper.defaultWeight = 1;
            var mask = Track(new AvatarMask()); mask.transformCount = 2;
            mask.SetTransformPath(0, "First"); mask.SetTransformActive(0, true);
            mask.SetTransformPath(1, "Second"); mask.SetTransformActive(1, true); upper.avatarMask = mask;
            source.layers = new[] { new AnimatorControllerLayer { name = "Lower", stateMachine = lower, defaultWeight = 1 }, upper };
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var group = MeshMergeAnalysis.Group(MergeCandidates(parts, controller), true).Single();
            var random = new System.Random(8145); var inputs = Enumerable.Range(0, 240).Select(_ => (float)random.NextDouble() * 2 - .5f).ToArray();
            bool transitioned = false;
            void Inputs(AnimatorControllerPlayable p, int frame)
            {
                transitioned |= p.IsInTransition(1);
                p.SetBool("Gate", frame % 30 >= 15); p.SetFloat("Toggle", inputs[frame]);
                p.SetFloat("Weight", inputs[(frame + 31) % inputs.Length]); p.SetLayerWeight(1, (frame / 17 % 4) / 3f);
            }
            var before = SampleVisibility(source, parts, Inputs, inputs.Length);
            Assert.That(transitioned, Is.True, "Exercise actual transitions, not just isolated state samples.");
            Assert.That(before.All(f => f[0] == f[1]), Is.True);
            foreach (var part in parts) part.enabled = true;
            MeshOptimizationPass.Merge(group, "First", true, mesh => Track(mesh));
            var built = CommitAnimator(controller, root);
            Assert.That(built.layers[1].stateMachine.states.Length, Is.EqualTo(2));
            pair.First.enabled = true;
            var after = SampleVisibility(built, new[] { pair.First }, Inputs, inputs.Length);
            Assert.That(after.Select(f => f[0]), Is.EqualTo(before.Select(f => f[0])));
        }

        [TestCase(BlendTreeType.SimpleDirectional2D)] [TestCase(BlendTreeType.FreeformDirectional2D)]
        [TestCase(BlendTreeType.FreeformCartesian2D)]
        public void SemanticMeshVisibilityMatchesNativeTwoDimensionalToggleTrees(BlendTreeType kind)
        {
            var pair = MergePair(); var parts = new[] { pair.First, pair.Second };
            var positions = new[] { Vector2.zero, Vector2.up, Vector2.right, Vector2.down, Vector2.left };
            BlendTree Tree(string path)
            {
                var tree = Track(new BlendTree { name = path, blendType = kind, blendParameter = "X", blendParameterY = "Y" });
                tree.children = positions.Select((p, i) => new ChildMotion { position = p, timeScale = 1,
                    motion = VisibilityLeaf(path, true, i % 2, length: path == "First" ? 1 : 2) }).ToArray();
                return tree;
            }
            var source = TreeController(("Weight", Tree("First")), ("Weight", Tree("Second")));
            source.AddParameter("X", AnimatorControllerParameterType.Float); source.AddParameter("Y", AnimatorControllerParameterType.Float);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var group = MeshMergeAnalysis.Group(MergeCandidates(parts, controller), true).Single();
            void Inputs(AnimatorControllerPlayable p, int frame)
            { p.SetFloat("X", Mathf.Sin(frame * .2f)); p.SetFloat("Y", Mathf.Cos(frame * .2f)); p.SetFloat("Weight", frame % 3 / 2f); }
            var before = SampleVisibility(source, parts, Inputs);
            Assert.That(before.All(f => f[0] == f[1]), Is.True);
            foreach (var part in parts) part.enabled = true;
            MeshOptimizationPass.Merge(group, "First", true, mesh => Track(mesh));
            var built = CommitAnimator(controller, root); pair.First.enabled = true;
            Assert.That(SampleVisibility(built, new[] { pair.First }, Inputs).Select(f => f[0]), Is.EqualTo(before.Select(f => f[0])));
        }

        [Test]
        public void SemanticMeshVisibilityDoesNotSynchronizeIndependentControllers()
        {
            var pair = MergePair(); var clone = new CloneContext(GenericPlatformAnimatorBindings.Instance);
            var first = Controller(Clip()); first.layers[0].stateMachine.defaultState.motion = VisibilityToggle("First", false);
            var second = Controller(Clip()); second.layers[0].stateMachine.defaultState.motion = VisibilityToggle("Second", false);
            first.AddParameter("Toggle", AnimatorControllerParameterType.Float); second.AddParameter("Toggle", AnimatorControllerParameterType.Float);
            var entries = new Dictionary<object, VirtualAnimatorController>
            { [VRCAvatarDescriptor.AnimLayerType.FX] = clone.Clone(first), [VRCAvatarDescriptor.AnimLayerType.Gesture] = clone.Clone(second) };
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, entries: entries), true).Count, Is.EqualTo(2));
        }

        [TestCase(false)] [TestCase(true)]
        public void SemanticMeshVisibilityCombinesAncestorConjunctionsWithoutDroppingIndependentGates(bool independent)
        {
            var pair = MergePair(); var firstParent = PolicyChild("First parent"); var secondParent = PolicyChild("Second parent");
            pair.First.transform.SetParent(firstParent.transform, true); pair.Second.transform.SetParent(secondParent.transform, true);
            var source = TreeController(("Weight", VisibilityToggle("First parent", false, "Parent")),
                ("Weight", VisibilityToggle("Second parent", false, "Parent")),
                ("Weight", VisibilityToggle("First parent/First", false, "Child")),
                ("Weight", VisibilityToggle("Second parent/Second", false, independent ? "Other" : "Child")));
            foreach (string name in new[] { "Parent", "Child", "Other" }) source.AddParameter(name, AnimatorControllerParameterType.Float);
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, controller), true).Count, Is.EqualTo(independent ? 2 : 1));
        }

        [Test]
        public void SemanticMeshVisibilityBoundsRecursiveMotionAnalysis()
        {
            var pair = MergePair(); var source = DuplicateVisibilityController(false);
            for (int i = 0; i < 135; i++)
            {
                var tree = Track(new BlendTree { blendType = BlendTreeType.Simple1D, blendParameter = "Toggle" });
                tree.children = new[] { new ChildMotion { motion = source.layers[0].stateMachine.defaultState.motion, timeScale = 1 } };
                source.layers[0].stateMachine.defaultState.motion = tree;
            }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            Assert.That(MeshMergeAnalysis.Group(MergeCandidates(new[] { pair.First, pair.Second }, controller), true).Count, Is.EqualTo(2));
        }

        [TestCase(false)] [TestCase(true)]
        public void SemanticMeshVisibilityKeepsExactWriterProofForUnsupportedTrees(bool normalized)
        {
            var pair = MergePair(); var clip = VisibilityLeaf("First", false, 0);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Second", typeof(GameObject), "m_IsActive"),
                AnimationCurve.Constant(0, 1, 0));
            var source = TreeController(("Weight", clip));
            source.layers[0].stateMachine.defaultState.writeDefaultValues = false;
            using (var so = new SerializedObject(source.layers[0].stateMachine.defaultState.motion))
            { so.FindProperty("m_NormalizedBlendValues").boolValue = normalized; so.ApplyModifiedPropertiesWithoutUndo(); }
            var controller = new CloneContext(GenericPlatformAnimatorBindings.Instance).Clone(source);
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, controller);
            Assert.That(MeshMergeAnalysis.SameVisibility(candidates[0], candidates[1], semantic: false), Is.True);
            Assert.That(MeshMergeAnalysis.Group(candidates, true).Count, Is.EqualTo(1));
        }
    }
}
