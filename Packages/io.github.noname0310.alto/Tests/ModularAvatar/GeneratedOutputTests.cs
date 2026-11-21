#if NONAME_MODULAR_AVATAR_1_19_OR_NEWER
using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using nadena.dev.ndmf;
using nadena.dev.ndmf.platform;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.ModularAvatarIntegration.Tests
{
    public sealed class GeneratedOutputTests
    {
        private Scene scene;
        private string outputRoot;
        private readonly List<Object> assets = new();

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            outputRoot = "Assets/__ALTO_MA_Test_" + System.Guid.NewGuid().ToString("N");
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(scene);
            foreach (var asset in assets)
                if (asset != null && !EditorUtility.IsPersistent(asset)) Object.DestroyImmediate(asset);
            assets.Clear();
            if (AssetDatabase.IsValidFolder(outputRoot)) AssetDatabase.DeleteAsset(outputRoot);
        }

        private GameObject Avatar(bool optimize)
        {
            var avatar = new GameObject("MA compatibility fixture");
            SceneManager.MoveGameObjectToScene(avatar, scene);
            avatar.AddComponent<Animator>().cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var descriptor = avatar.AddComponent<VRCAvatarDescriptor>();
            descriptor.customizeAnimationLayers = true;
            descriptor.customExpressions = true;
            if (optimize)
            {
                avatar.AddComponent<ALTOTraceAndOptimizeAnimator>();
                avatar.AddComponent<ALTOTraceAndOptimizeObjects>();
                avatar.AddComponent<ALTOTraceAndOptimizePhysBones>();
                avatar.AddComponent<ALTOParameterLayout>().ReferencePlatform =
                    ParameterLayout.ParameterLayoutPass.Platform(EditorUserBuildSettings.activeBuildTarget);
                avatar.AddComponent<ALTOParameterCompression>();
            }
            return avatar;
        }

        private GameObject Child(GameObject parent, string name)
        {
            var child = new GameObject(name); child.transform.SetParent(parent.transform, false);
            return child;
        }

        private void Build(GameObject avatar)
        {
            // Installed generators can allocate assets even without their own
            // authoring marker. Exercise a real build container and clean it up.
            using var outputScope = new OverrideTemporaryDirectoryScope(outputRoot + "/Build_" + System.Guid.NewGuid().ToString("N"));
            var context = AvatarProcessor.ProcessAvatar(avatar,
                PlatformRegistry.PlatformProviders["nadena.dev.ndmf.vrchat.avatar3"]);
            Assert.That(context.Successful, Is.True, string.Join("\n", context.ErrorReport.Errors.Select(e => e.TheError)));
        }

        [Test]
        public void NativeGlobalColliderGeneratedByMaRemainsAnObservableOutput()
        {
            var avatar = Avatar(true);
            var origin = Child(avatar, "Global collider origin");
            origin.transform.localPosition = new Vector3(.1f, .2f, .3f);
            var expectedCenter = origin.transform.position;
            var collider = origin.AddComponent<ModularAvatarGlobalCollider>();
            collider.RootTransform = origin.transform; collider.Radius = .15f;
            Build(avatar);
            var output = avatar.GetComponentsInChildren<VRCPhysBoneCollider>(true).Single();
            Assert.That(output.globalCollision, Is.EqualTo(VRCPhysBoneBase.AdvancedBool.True));
            // A static origin can be merged or its collider hoisted. The
            // observable collision geometry, not the authoring host, must survive.
            var effectiveRoot = output.rootTransform != null ? output.rootTransform : output.transform;
            Assert.That(Vector3.Distance(effectiveRoot.TransformPoint(output.position), expectedCenter), Is.LessThan(1e-6f));
            Assert.That(output.enabled && output.gameObject.activeInHierarchy, Is.True);
            Assert.That(output.radius, Is.EqualTo(.15f));
        }

        [TestCase(false)] [TestCase(true)]
        public void MaParameterSourcePreservationRetainsAdvancedEncodingAndExplicitDefaults(bool precisionOverride)
        {
            var avatar = Avatar(true);
            var parameters = avatar.AddComponent<ModularAvatarParameters>();
            parameters.parameters.Add(new ParameterConfig
            {
                nameOrPrefix = "AuthoredSigned", remapTo = "Signed", syncType = ParameterSyncType.Int, bitWidth = 5,
                representation = ParameterRepresentation.Signed, defaultValue = -7,
                hasExplicitDefaultValue = true, saved = true,
                useCustomLerpDuration = true, customLerpDuration = .25f
            });
            parameters.parameters.Add(new ParameterConfig
            {
                nameOrPrefix = "Unsigned", syncType = ParameterSyncType.Float, bitWidth = 6,
                representation = ParameterRepresentation.Unsigned, defaultValue = .75f,
                hasExplicitDefaultValue = true
            });
            // Model externally observed inputs; declarations without any output can
            // otherwise legitimately lose synchronization during whole-avatar analysis.
            var policy = avatar.AddComponent<ALTOAnimatorParameterPolicy>(); policy.ApplyOnPlatforms = OptimizationPlatforms.None;
            policy.PreserveParameters.Sources.Add(new AnimatorParameterSource { Source = parameters });
            if (precisionOverride)
            {
                var rule = new ParameterPrecisionSourceOverride { Floats = new() { Bits = 5 },
                    Integers = new() { Range = IntegerRangeMode.LimitRange, Minimum = -8, Maximum = 7 } };
                rule.Parameters.Sources.Add(new AnimatorParameterSource { Source = parameters });
                avatar.GetComponent<ALTOParameterCompression>().PrecisionSourceOverrides.Add(rule);
            }
            Build(avatar);
            var output = avatar.GetComponent<VRCAvatarDescriptor>().expressionParameters.parameters;
            var integer = output.Single(p => p.name == "Signed");
            Assert.That(integer.bitWidth, Is.EqualTo(precisionOverride ? 4 : 5)); Assert.That(integer.flippedRepresentation, Is.True);
            Assert.That(integer.defaultValue, Is.EqualTo(-7)); Assert.That(integer.saved, Is.True);
            Assert.That(integer.networkSynced, Is.True);
            Assert.That(integer.useCustomLerpDuration, Is.True); Assert.That(integer.customLerpDuration, Is.EqualTo(.25f));
            var fraction = output.Single(p => p.name == "Unsigned");
            Assert.That(fraction.bitWidth, Is.EqualTo(precisionOverride ? 5 : 6)); Assert.That(fraction.flippedRepresentation, Is.True);
            Assert.That(fraction.defaultValue, Is.EqualTo(.75f));
        }

        [TestCase(false)] [TestCase(true)]
        public void ReactiveBlendTreeToggleMatchesNativeAnimatorBeforeAndAfterAlto(bool initiallyActive)
        {
            var baseline = BuildToggle(false, initiallyActive);
            var optimized = BuildToggle(true, initiallyActive);
            var expected = ToggleSequence(baseline.Avatar, baseline.Target);
            var actual = ToggleSequence(optimized.Avatar, optimized.Target);
            Assert.That(expected.Distinct().Count(), Is.EqualTo(2), "The fixture must exercise both toggle outputs.");
            Assert.That(actual, Is.EqualTo(expected));
        }

        private (GameObject Avatar, GameObject Target) BuildToggle(bool optimize, bool initiallyActive)
        {
            var avatar = Avatar(optimize);
            var target = Child(avatar, "Rendered toggle target"); target.SetActive(initiallyActive);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }, triangles = new[] { 0, 1, 2 } };
            assets.Add(mesh); target.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            avatar.AddComponent<ModularAvatarParameters>().parameters.Add(new ParameterConfig
                { nameOrPrefix = "Toggle", syncType = ParameterSyncType.Bool, hasExplicitDefaultValue = true });
            var control = Child(avatar, "Toggle control");
            control.AddComponent<ModularAvatarMenuItem>().Control = new VRCExpressionsMenu.Control
            {
                name = "Toggle", type = VRCExpressionsMenu.Control.ControlType.Toggle, value = 1,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = "Toggle" }
            };
            var reference = new AvatarObjectReference(); reference.Set(target);
            control.AddComponent<ModularAvatarObjectToggle>().Objects.Add(new ToggledObject { Object = reference, Active = !initiallyActive });
            Build(avatar);
            Assert.That(target != null, Is.True, "Both dynamic output branches must remain available.");
            return (avatar, target);
        }

        private static bool[] ToggleSequence(GameObject avatar, GameObject target)
        {
            var fx = (AnimatorController)avatar.GetComponent<VRCAvatarDescriptor>().baseAnimationLayers
                .Single(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController;
            var graph = PlayableGraph.Create("MA reactive output equivalence");
            try
            {
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var playable = AnimatorControllerPlayable.Create(graph, fx);
                AnimationPlayableOutput.Create(graph, "FX", avatar.GetComponent<Animator>()).SetSourcePlayable(playable);
                if (fx.parameters.Any(p => p.name == "IsLocal")) playable.SetBool("IsLocal", true);
                graph.Play();
                var result = new List<bool>();
                foreach (var input in new[] { false, true, false, true })
                {
                    var parameter = fx.parameters.Single(p => p.name == "Toggle");
                    if (parameter.type == AnimatorControllerParameterType.Float) playable.SetFloat("Toggle", input ? 1 : 0);
                    else playable.SetBool("Toggle", input);
                    for (int frame = 0; frame < 12; frame++) { graph.Evaluate(1f / 60); result.Add(target.activeSelf); }
                }
                return result.ToArray();
            }
            finally { graph.Destroy(); }
        }
    }
}
#endif
