using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false, 25f)] [TestCase(true, 25f)] [TestCase(false, 70f)] [TestCase(true, 70f)]
        public void ConstantBlendShapes_MultiframeBakeMatchesNativeSkinning(bool writeDefaults, float value)
        {
            var original = Mesh();
            Frame(original, "Constant", 50, Vector3.right); Frame(original, "Constant", 100, Vector3.up * 2);
            Frame(original, "Dynamic", 100, Vector3.forward);
            renderer.SetBlendShapeWeight(0, 20);
            var clip = StaticCurve(Binding("Constant"), value);
            AnimationUtility.SetEditorCurve(clip, Binding("Dynamic"), AnimationCurve.Linear(0, 10, .3f, 80));
            var source = Controller(clip); source.layers[0].stateMachine.defaultState.writeDefaultValues = writeDefaults;
            var baked = Track(new Mesh());
            var before = new List<(Vector3[] Positions, Vector3[] Normals, Vector4[] Tangents)>();
            SampleFrozen(source, (p, i) => { }, 24, () =>
            { renderer.BakeMesh(baked); before.Add((baked.vertices, baked.normals, baked.tangents)); return 0; }, 20);
            renderer.SetBlendShapeWeight(0, 20); renderer.SetBlendShapeWeight(1, 0);
            var index = ConstantOutputIndex(source, out var controller);
            var usage = new AnimationUsage(); usage.AddController(root.transform, root.transform, controller);
            var plan = OptimizationPlan.Create(renderer, usage, true, new HashSet<string>(),
                constants: index.ConstantOutputs, allowInitialValueChanges: true);
            Assert.That(plan.Frozen.Select(s => s.Name), Is.EqualTo(new[] { "Constant" }));
            Assert.That(plan.Frozen[0].Weight, Is.EqualTo(value)); Assert.That(plan.Frozen[0].ChangedInitialValue, Is.True);
            var result = Track(MeshOptimization.Generate(plan, MeshOptimization.CreateRecipe(plan)));
            MeshOptimization.Apply(plan, result); plan.RewriteCurves();
            Assert.That(result.blendShapeCount, Is.EqualTo(1)); Assert.That(result.GetBlendShapeName(0), Is.EqualTo("Dynamic"));
            Assert.That(result.uv, Is.EqualTo(original.uv)); Assert.That(result.triangles, Is.EqualTo(original.triangles));
            int frame = 0;
            SampleFrozen(CommitAnimator(controller, root), (p, i) => { }, 24, () =>
            {
                renderer.BakeMesh(baked); var expected = before[frame++];
                for (int vertex = 0; vertex < baked.vertexCount; vertex++)
                {
                    Assert.That(Vector3.Distance(baked.vertices[vertex], expected.Positions[vertex]), Is.LessThan(.00001f));
                    Assert.That(Vector3.Distance(baked.normals[vertex], expected.Normals[vertex]), Is.LessThan(.00001f));
                    Assert.That(Vector4.Distance(baked.tangents[vertex], expected.Tangents[vertex]), Is.LessThan(.00001f));
                }
                return 0;
            });
            Assert.That(original.blendShapeCount, Is.EqualTo(2), "The source mesh stays untouched");
            Assert.That(AnimationUtility.GetEditorCurve(clip, Binding("Constant")), Is.Not.Null);
        }

        [TestCase("NoPermission")] [TestCase("OptionOff")] [TestCase("ProtectedShape")]
        [TestCase("NativeReader")] [TestCase("Dynamic")]
        public void ConstantBlendShapes_RetainsUnprovedOrProtectedOutput(string reason)
        {
            Mesh("Constant"); renderer.SetBlendShapeWeight(0, 20);
            var clip = StaticCurve(Binding("Constant"), 70);
            if (reason == "Dynamic") AnimationUtility.SetEditorCurve(clip, Binding("Constant"), AnimationCurve.Linear(0, 0, 1, 70));
            var index = ConstantOutputIndex(Controller(clip), out var controller);
            var usage = new AnimationUsage(); usage.AddController(root.transform, root.transform, controller);
            var preserve = new HashSet<string>(); if (reason == "ProtectedShape") preserve.Add("Constant");
            var plan = OptimizationPlan.Create(renderer, usage, true, preserve, observeAllVertices: reason == "NativeReader",
                constants: reason == "OptionOff" ? null : index.ConstantOutputs, allowInitialValueChanges: reason != "NoPermission");
            Assert.That(plan.HasChanges, Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantBlendShapes_RespectUnreachableStateOption(bool ignoreUnreachable)
        {
            Mesh("Constant"); renderer.SetBlendShapeWeight(0, 70);
            var index = ConstantOutputIndex(Controller(StaticCurve(Binding("Constant"), 70),
                StaticCurve(Binding("Constant"), 25)), out var controller);
            var usage = new AnimationUsage(); usage.AddController(root.transform, root.transform, controller);
            var plan = OptimizationPlan.Create(renderer, usage, true, new HashSet<string>(), ignoreUnreachableStates: ignoreUnreachable,
                constants: index.ConstantOutputs);
            Assert.That(plan.Frozen.Count, Is.EqualTo(ignoreUnreachable ? 1 : 0));
        }
    }
}
