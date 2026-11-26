#if NONAME_MODULAR_AVATAR_1_19_OR_NEWER
using System.Linq;
using nadena.dev.modular_avatar.core;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Noname.AvatarTools.ModularAvatarIntegration.Tests
{
    public sealed partial class GeneratedOutputTests
    {
        private (GameObject Avatar, SkinnedMeshRenderer Renderer, Mesh Source) ReactiveMesh(bool optimize, bool selected)
        {
            var avatar = Avatar(false);
            if (optimize)
            {
                avatar.AddComponent<ALTOTraceAndOptimizeAnimator>();
                avatar.AddComponent<ALTOTraceAndOptimizeBlendShapes>();
                avatar.AddComponent<ALTOOptimizationPolicy>();
                if (selected)
                {
                    var policy = avatar.AddComponent<ALTOAnimatorParameterPolicy>();
                    policy.ApplyOnPlatforms = OptimizationPlatforms.All;
                    policy.FreezeMode = AnimatorParameterFreezeMode.SelectedParameters;
                    policy.FreezeParameters.Add(new AnimatorParameterConstant { Name = "ReactiveChoice", Value = 1 });
                }
            }
            var target = Child(avatar, "Reactive target");
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up,
                Vector3.forward, Vector3.forward + Vector3.right, Vector3.forward + Vector3.up },
                normals = Enumerable.Repeat(Vector3.forward, 6).ToArray(), triangles = new[] { 0, 1, 2, 3, 4, 5 },
                bindposes = new[] { Matrix4x4.identity },
                boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 6).ToArray() };
            var deltas = new Vector3[6]; deltas[0] = Vector3.right * .3f;
            mesh.AddBlendShapeFrame("Pose", 100, deltas, new Vector3[6], new Vector3[6]);
            deltas = new Vector3[6]; deltas[0] = deltas[1] = deltas[2] = Vector3.forward;
            mesh.AddBlendShapeFrame("Region", 100, deltas, new Vector3[6], new Vector3[6]);
            assets.Add(mesh);
            var renderer = target.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh;
            renderer.rootBone = avatar.transform; renderer.bones = new[] { avatar.transform };
            var material = new Material(Shader.Find("Unlit/Color")) { color = Color.red }; assets.Add(material);
            renderer.sharedMaterial = material;
            return (avatar, renderer, mesh);
        }

        private GameObject ReactiveChoice(GameObject avatar)
        {
            avatar.AddComponent<ModularAvatarParameters>().parameters.Add(new ParameterConfig
                { nameOrPrefix = "ReactiveChoice", syncType = ParameterSyncType.Bool, defaultValue = 1, hasExplicitDefaultValue = true });
            var control = Child(avatar, "Reactive choice");
            control.AddComponent<ModularAvatarMenuItem>().Control = new VRCExpressionsMenu.Control
            {
                name = "Choice", type = VRCExpressionsMenu.Control.ControlType.Toggle, value = 1,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = "ReactiveChoice" }
            };
            return control;
        }

        private static AvatarObjectReference Ref(GameObject target)
        { var reference = new AvatarObjectReference(); reference.Set(target); return reference; }

        private Vector3[] SampleReactiveMesh(GameObject avatar, SkinnedMeshRenderer renderer)
        {
            var controller = avatar.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers
                .Single(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController as AnimatorController;
            Assert.That(controller, Is.Not.Null);
            var graph = PlayableGraph.Create("MA constant output equivalence"); var baked = new Mesh();
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "FX", avatar.GetComponent<Animator>()).SetSourcePlayable(playable);
                foreach (var parameter in controller.parameters)
                {
                    if (parameter.name != "ReactiveChoice" && parameter.name != "IsLocal") continue;
                    if (parameter.type == AnimatorControllerParameterType.Float) playable.SetFloat(parameter.name, 1);
                    else if (parameter.type == AnimatorControllerParameterType.Bool) playable.SetBool(parameter.name, true);
                }
                graph.Play(); for (int frame = 0; frame < 12; frame++) graph.Evaluate(1f / 60);
                renderer.BakeMesh(baked); return baked.vertices;
            }
            finally { graph.Destroy(); Object.DestroyImmediate(baked); }
        }

        [TestCase("Material")] [TestCase("Shape")]
        public void ConstantReactiveOutput_GeneratedChoiceMatchesNativeAfterSpecialization(string kind)
        {
            var before = ReactiveMesh(false, true); var after = ReactiveMesh(true, true);
            foreach (var fixture in new[] { before, after })
            {
                var control = ReactiveChoice(fixture.Avatar);
                if (kind == "Material")
                {
                    var material = new Material(fixture.Renderer.sharedMaterial) { color = Color.green }; assets.Add(material);
                    control.AddComponent<ModularAvatarMaterialSetter>().Objects.Add(new MaterialSwitchObject
                        { Object = Ref(fixture.Renderer.gameObject), MaterialIndex = 0, Material = material });
                }
                else control.AddComponent<ModularAvatarShapeChanger>().Shapes.Add(new ChangedShape
                    { Object = Ref(fixture.Renderer.gameObject), ShapeName = "Pose", ChangeType = ShapeChangeType.Set, Value = 65 });
                Build(fixture.Avatar);
            }
            var expected = SampleReactiveMesh(before.Avatar, before.Renderer);
            var actual = SampleReactiveMesh(after.Avatar, after.Renderer);
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int i = 0; i < expected.Length; i++) Assert.That(Vector3.Distance(actual[i], expected[i]), Is.LessThan(1e-6f));
            if (kind == "Material")
            {
                Assert.That(before.Renderer.sharedMaterial.color, Is.EqualTo(Color.green));
                Assert.That(after.Renderer.sharedMaterial.color, Is.EqualTo(before.Renderer.sharedMaterial.color));
            }
            else
            {
                Assert.That(expected[0].x, Is.EqualTo(.195f).Within(1e-6f));
                Assert.That(after.Renderer.sharedMesh.GetBlendShapeIndex("Pose"), Is.LessThan(0), "The late generated constant must reach the ordinary freeze path.");
            }
            Assert.That(before.Source.GetBlendShapeIndex("Pose"), Is.GreaterThanOrEqualTo(0));
            Assert.That(after.Source.vertices[0], Is.EqualTo(Vector3.zero), "Source meshes remain immutable.");
        }

        [Test]
        public void ConstantReactiveOutput_StaticRegionDeletionPreservesRemainingGeometry()
        {
            var before = ReactiveMesh(false, false); var after = ReactiveMesh(true, false);
            foreach (var fixture in new[] { before, after })
            {
                var control = Child(fixture.Avatar, "Always applied region removal");
                control.AddComponent<ModularAvatarShapeChanger>().Shapes.Add(new ChangedShape
                    { Object = Ref(fixture.Renderer.gameObject), ShapeName = "Region", ChangeType = ShapeChangeType.Delete });
                Build(fixture.Avatar);
            }
            Assert.That(before.Renderer.sharedMesh.triangles.Length, Is.EqualTo(3));
            Assert.That(after.Renderer.sharedMesh.triangles.Length, Is.EqualTo(3));
            Assert.That(after.Renderer.sharedMesh.vertices, Is.EqualTo(before.Renderer.sharedMesh.vertices));
            Assert.That(before.Source.triangles.Length, Is.EqualTo(6));
            Assert.That(after.Source.triangles.Length, Is.EqualTo(6));
        }
    }
}
#endif
