using System;
using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private static IEnumerable<TestCaseData> DynamicDefaultCases()
        {
            foreach (string kind in new[] { "1D", "Direct", "Normalized", "Nested" })
            foreach (string output in new[] { "BlendShape", "Scale", "Material", "Parameter" })
            foreach (bool baseDefaults in new[] { false, true })
                yield return new TestCaseData(kind, output, baseDefaults);
        }

        [TestCaseSource(nameof(DynamicDefaultCases))]
        public void DynamicTreeDefaultsMatchOrdinaryClipWithDeadParameterOutputs(string kind, string output, bool baseDefaults)
        {
            var fixture = DefaultTreeFixture(kind, .5f, .5f, output);
            root.AddComponent<VRCAvatarDescriptor>();
            FreezeExpressions(new[] { "WeightA", "WeightB", "Position", "Gate", "Output" }
                .Select(n => new VRCExpressionParameters.Parameter { name = n, valueType = VRCExpressionParameters.ValueType.Float, networkSynced = n == "Output" }).ToArray());
            fixture.source.layers = fixture.source.layers.Take(2).ToArray();
            fixture.source.layers[0].stateMachine.defaultState.writeDefaultValues = baseDefaults;
            fixture.source.AddParameter("Unused", AnimatorControllerParameterType.Float);
            var leaves = fixture.tree.children.Select(c => c.motion).OfType<AnimationClip>().ToArray();
            if (kind == "Nested") leaves = ((BlendTree)fixture.tree.children[0].motion).children.Select(c => (AnimationClip)c.motion).ToArray();
            for (int i = 0; i < leaves.Length; i++)
                AnimationUtility.SetEditorCurve(leaves[i], ParameterBinding("Unused"), AnimationCurve.Linear(0, i, 1, i + 1));
            Material first = null, second = null;
            var writer = (AnimationClip)fixture.source.layers[0].stateMachine.defaultState.motion;
            if (output == "Material")
            {
                var shader = Shader.Find("Hidden/InternalErrorShader");
                first = Track(new Material(shader)); second = Track(new Material(shader));
                AnimationUtility.SetObjectReferenceCurve(writer,
                    EditorCurveBinding.PPtrCurve("", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
                    new[] { new ObjectReferenceKeyframe { time = 0, value = second } });
            }
            if (output == "Parameter")
            {
                fixture.source.AddParameter(new AnimatorControllerParameter { name = "Output", type = AnimatorControllerParameterType.Float, defaultFloat = .2f });
                AnimationUtility.SetEditorCurve(writer, ParameterBinding("Output"), AnimationCurve.Linear(0, .1f, 2, .9f));
            }
            bool visitedTree = false;
            float parameter = 0;
            var weights = new[] { 0f, .2f, .8f, 1f, 2f, -.5f, float.MaxValue, float.PositiveInfinity, float.NaN };
            void Inputs(AnimatorControllerPlayable playable, int frame)
            {
                visitedTree |= playable.GetCurrentAnimatorStateInfo(1).shortNameHash == Animator.StringToHash("Tree defaults");
                parameter = output == "Parameter" ? playable.GetFloat("Output") : 0;
                playable.SetFloat("Gate", frame % 30 < 15 ? 0 : 1);
                playable.SetFloat("Position", weights[frame % weights.Length]);
                playable.SetFloat("WeightA", weights[frame % weights.Length]);
                playable.SetFloat("WeightB", weights[(frame / weights.Length) % weights.Length]);
            }
            float Sample() => output == "Scale" ? root.transform.localScale.x :
                output == "Material" ? (renderer.sharedMaterial == second ? 1 : 0) :
                output == "Parameter" ? parameter : renderer.GetBlendShapeWeight(0);
            root.transform.localScale = Vector3.one;
            renderer.sharedMaterial = first;
            var expected = SampleFrozen(fixture.source, Inputs, 180, Sample);
            Assert.That(visitedTree, Is.True);
            fixture.second.motion = fixture.first.motion;
            root.transform.localScale = Vector3.one;
            renderer.sharedMaterial = first;
            var actual = SampleFrozen(fixture.source, Inputs, 180, Sample);
            Assert.That(actual, Is.EqualTo(expected).Within(1e-5));
            fixture.second.motion = fixture.tree;
            var optimized = OptimizeDefaultTree(fixture.source);
            Assert.That(optimized.report.ParameterCurves, Is.EqualTo(2));
            Assert.That(optimized.report.StateMachines, Is.EqualTo(1));
            root.transform.localScale = Vector3.one;
            renderer.sharedMaterial = first;
            var rewritten = SampleFrozen(CommitAnimator(optimized.controller, root), Inputs, 180, Sample);
            Assert.That(rewritten, Is.EqualTo(expected).Within(1e-5));
        }

        [TestCase("1D", 0f, 0f, true)]
        [TestCase("Direct", .25f, .75f, true)]
        [TestCase("Direct", .25f, .25f, true)]
        [TestCase("Direct", 1.5f, .5f, true)]
        [TestCase("Direct", -.5f, .5f, true)]
        [TestCase("Normalized", 0f, 0f, true)]
        [TestCase("Normalized", .25f, .25f, true)]
        [TestCase("Normalized", 1.5f, .5f, true)]
        public void NativeDefaultContributionMatchesUnitMotion(string kind, float firstWeight, float secondWeight, bool equivalent)
        {
            var fixture = DefaultTreeFixture(kind, firstWeight, secondWeight, "Scale");
            fixture.source.layers = fixture.source.layers.Take(2).ToArray();
            bool visitedTree = false;
            void Inputs(AnimatorControllerPlayable playable, int frame)
            {
                visitedTree |= playable.GetCurrentAnimatorStateInfo(1).shortNameHash == Animator.StringToHash("Tree defaults");
                playable.SetFloat("Gate", frame % 30 < 15 ? 0 : 1);
                playable.SetFloat("Position", frame % 4 == 0 ? -2 : frame % 4 == 1 ? .2f : frame % 4 == 2 ? .8f : 2);
            }
            root.transform.localScale = Vector3.one;
            var actual = SampleFrozen(fixture.source, Inputs, 120, () => root.transform.localScale.x);
            Assert.That(visitedTree, Is.True, "Exercise the tree state and transitions in the native baseline");
            fixture.second.motion = fixture.first.motion;
            root.transform.localScale = Vector3.one;
            var unit = SampleFrozen(fixture.source, Inputs, 120, () => root.transform.localScale.x);
            TestContext.WriteLine("Native output: " + string.Join(", ", actual.Take(8)));
            TestContext.WriteLine("Unit output: " + string.Join(", ", unit.Take(8)));
            bool equal = actual.Zip(unit, (a, b) => Math.Abs(a - b) < 1e-5).All(v => v);
            Assert.That(equal, Is.EqualTo(equivalent));
        }
    }
}
