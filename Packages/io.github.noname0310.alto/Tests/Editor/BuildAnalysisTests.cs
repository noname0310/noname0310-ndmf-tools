using System;
using System.IO;
using System.Text.RegularExpressions;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.Core;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void AnalysisIsolatesSharedTemporaryAssetsIncludingNestedReferences()
        {
            var mesh = Track(new Mesh { name = "Live preview", vertices = new[] { Vector3.zero } });
            var texture = Track(new Texture2D(1, 1));
            var material = Track(new Material(Shader.Find("Unlit/Texture")));
            material.mainTexture = texture;
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            renderer.sharedMesh = mesh;
            renderer.sharedMaterial = material;
            root.AddComponent<ParameterPolicySourceFixture>().PreviewMesh = mesh;
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject clone = null;
            try
            {
                clone = (GameObject)UnityEngine.Object.Instantiate(root, scene);
                using (AnalysisAssetIsolation.Capture(clone))
                {
                    var outputMesh = clone.GetComponent<MeshFilter>().sharedMesh;
                    var outputMaterial = clone.GetComponent<SkinnedMeshRenderer>().sharedMaterial;
                    Assert.That(outputMesh, Is.Not.SameAs(mesh));
                    Assert.That(clone.GetComponent<ParameterPolicySourceFixture>().PreviewMesh, Is.SameAs(outputMesh));
                    Assert.That(outputMaterial, Is.Not.SameAs(material));
                    Assert.That(outputMaterial.mainTexture, Is.Not.SameAs(texture));
                    Assert.That(outputMaterial.shader, Is.SameAs(material.shader));
                    outputMesh.vertices = new[] { Vector3.one };
                    Assert.That(mesh.vertices[0], Is.EqualTo(Vector3.zero));
                    UnityEngine.Object.DestroyImmediate(clone);
                }
                Assert.That(mesh != null && material != null && texture != null, Is.True);
                Assert.That(EditorUtility.IsPersistent(mesh), Is.False);
            }
            finally
            {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void AnalysisIsolatesUnsavedAnimatorGraphsWithoutNativeInstantiation()
        {
            var controller = TreeController(("Weight", Clip("a")));
            root.AddComponent<Animator>().runtimeAnimatorController = controller;
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject clone = null;
            try
            {
                clone = (GameObject)UnityEngine.Object.Instantiate(root, scene);
                using (AnalysisAssetIsolation.Capture(clone))
                {
                    var output = (UnityEditor.Animations.AnimatorController)clone.GetComponent<Animator>().runtimeAnimatorController;
                    Assert.That(output, Is.Not.SameAs(controller));
                    var originalState = controller.layers[0].stateMachine.defaultState;
                    var outputState = output.layers[0].stateMachine.defaultState;
                    Assert.That(output.layers[0].stateMachine, Is.Not.SameAs(controller.layers[0].stateMachine));
                    Assert.That(outputState, Is.Not.SameAs(originalState));
                    Assert.That(outputState.motion, Is.Not.SameAs(originalState.motion));
                    var tree = (UnityEditor.Animations.BlendTree)outputState.motion;
                    Assert.That(tree.children[0].motion, Is.Not.SameAs(((UnityEditor.Animations.BlendTree)originalState.motion).children[0].motion));
                    outputState.name = "Isolated state";
                    Assert.That(originalState.name, Is.Not.EqualTo(outputState.name));
                    UnityEngine.Object.DestroyImmediate(clone);
                }
                Assert.That(controller != null && controller.layers[0].stateMachine.defaultState != null, Is.True);
            }
            finally
            {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AnalysisDoesNotReplaceSuccessfulPlatformBaseline(bool existing)
        {
            string id = "avtr_" + Guid.NewGuid().ToString("D");
            root.AddComponent<PipelineManager>().blueprintId = id;
            string path = Path.Combine("Library", "nadena.dev.modular-avatar", "AvatarParameterInfo", id);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            try
            {
                if (existing) File.WriteAllText(path, "uploaded baseline");
                var timestamp = existing ? File.GetLastWriteTimeUtc(path) : default;
                Assert.Throws<InvalidOperationException>(() =>
                {
                    using (AnalysisParameterRecordScope.Capture(root))
                    {
                        if (existing) Assert.That(File.ReadAllText(path), Is.EqualTo("uploaded baseline"));
                        File.WriteAllText(path, "failed analysis result");
                        throw new InvalidOperationException("Build failed");
                    }
                });
                if (existing)
                {
                    Assert.That(File.ReadAllText(path), Is.EqualTo("uploaded baseline"));
                    Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(timestamp));
                }
                else Assert.That(File.Exists(path), Is.False);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void FailedAnalysisDiscardsCloneAndPreservesSourceScene()
        {
            root.AddComponent<VRCAvatarDescriptor>();
            var child = PolicyChild("Original child");
            var position = child.transform.localPosition;
            var mesh = Track(new Mesh { vertices = new[] { Vector3.zero } });
            var preview = child.AddComponent<ParameterPolicySourceFixture>();
            preview.PreviewMesh = mesh;
            preview.ModifyPreviewOnEnable = true;
            var scene = root.scene;
            int roots = scene.rootCount;
            bool dirty = scene.isDirty;
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: Simulated analysis failure"));
            var result = AvatarBuildAnalysis.Analyze(root, clone =>
            {
                Assert.That(clone.scene, Is.Not.EqualTo(scene));
                Assert.That(clone.transform.Find(child.name).GetComponent<ParameterPolicySourceFixture>().PreviewMesh.vertices[0],
                    Is.EqualTo(Vector3.one));
                clone.transform.Find(child.name).localPosition = Vector3.one;
                throw new InvalidOperationException("Simulated analysis failure");
            });
            try
            {
                Assert.That(result.Success, Is.False);
                Assert.That(result.Error, Does.Contain("Simulated analysis failure"));
                Assert.That(child.transform.localPosition, Is.EqualTo(position));
                Assert.That(mesh.vertices[0], Is.EqualTo(Vector3.zero));
                Assert.That(scene.rootCount, Is.EqualTo(roots));
                Assert.That(scene.isDirty, Is.EqualTo(dirty));
            }
            finally
            {
                if (File.Exists(result.ReportPath)) File.Delete(result.ReportPath);
            }
        }
    }
}
