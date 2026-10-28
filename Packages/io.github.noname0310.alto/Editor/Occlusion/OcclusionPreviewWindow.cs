using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Build;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Occlusion
{
    internal sealed class OcclusionPreviewWindow : EditorWindow
    {
        private ALTORemoveOccludedMesh settings;
        private PreviewRenderUtility preview;
        private readonly List<(Mesh Mesh, int Material)> meshes = new();
        private readonly List<Object> owned = new();
        private Material[] materials;
        private OcclusionEvaluation result;
        private string error;
        private Bounds bounds;
        private Vector2 orbit = new(160, 20), scroll;
        private float distance = 1;
        private bool showOccluders, showRemoved = true;

        internal static void Open(ALTORemoveOccludedMesh settings)
        {
            var window = GetWindow<OcclusionPreviewWindow>("Occlusion Preview");
            window.settings = settings; window.Show(); window.Analyze();
        }

        private void Analyze()
        {
            Release(); error = null; result = null;
            if (settings == null || settings.FindDescriptor() == null) return;
            try
            {
                var avatar = settings.FindDescriptor().gameObject;
                var scope = OptimizationBuildScope.Capture(settings, avatar);
                result = OcclusionEvaluation.Run(avatar, settings, scope);
                preview = new PreviewRenderUtility();
                var shader = AssetDatabase.LoadAssetAtPath<Shader>("Packages/io.github.noname0310.alto/Editor/Occlusion/OcclusionPreview.shader");
                if (shader == null || !shader.isSupported) throw new NotSupportedException("The occlusion preview shader is unavailable.");
                materials = new[] { new Color(0.35f, 0.7f, 1), new Color(1, 0.15f, 0.15f), new Color(0.5f, 0.5f, 0.5f) }
                    .Select(c => { var material = new Material(shader); material.SetColor("_Color", c); owned.Add(material); return material; }).ToArray();
                using var snapshot = new OcclusionPoseSnapshot(avatar, settings.ReferencePose);
                var candidateRenderers = result.Targets.Select(t => t.Renderer).ToHashSet();
                foreach (var target in result.Targets)
                {
                    var mesh = snapshot.WorldMesh(target.Renderer, target.Masks.Regions.Select(r => r.Name)); if (mesh == null) continue;
                    var vertices = mesh.vertices;
                    for (int removed = 0; removed < 2; removed++)
                    {
                        var indices = target.Masks.Indices.Where((_, i) => target.RemovedTriangles[i / 3] == (removed == 1)).ToArray();
                        AddMesh(vertices, indices, removed);
                    }
                }
                foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true).Where(r => scope.Contains(r.transform) &&
                    !candidateRenderers.Contains(r as SkinnedMeshRenderer) && snapshot.Active(r)))
                {
                    var mesh = snapshot.WorldMesh(renderer); if (mesh == null) continue;
                    var indices = Enumerable.Range(0, mesh.subMeshCount).Where(s => mesh.GetTopology(s) == MeshTopology.Triangles)
                        .SelectMany(s => mesh.GetIndices(s, applyBaseVertex: true)).ToArray();
                    AddMesh(mesh.vertices, indices, 2);
                }
                if (meshes.Count > 0)
                {
                    bounds = meshes[0].Mesh.bounds;
                    foreach (var item in meshes) bounds.Encapsulate(item.Mesh.bounds);
                }
                else bounds = new Bounds(Vector3.zero, Vector3.one);
                distance = 1;
            }
            catch (Exception ex) { error = ex.Message; Release(); }
            Repaint();
        }

        private void AddMesh(Vector3[] vertices, int[] indices, int material)
        {
            if (indices.Length == 0) return;
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            owned.Add(mesh); mesh.vertices = vertices; mesh.SetIndices(indices, MeshTopology.Triangles, 0);
            meshes.Add((mesh, material));
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Current setup only: generators and Animator state machines are not evaluated. " +
                "This view displays the reference pose. Red: removal candidate; blue: retained target geometry; gray: surrounding geometry.", MessageType.Info);
            using (new EditorGUI.DisabledScope(settings == null || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button("Recalculate Current Setup")) Analyze();
            showOccluders = EditorGUILayout.ToggleLeft("Show surrounding geometry (including non-occluders)", showOccluders);
            showRemoved = EditorGUILayout.ToggleLeft("Show removed regions", showRemoved);
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Error);
            var rect = GUILayoutUtility.GetRect(100, Mathf.Max(180, position.height * 0.5f), GUILayout.ExpandWidth(true));
            if (preview != null)
            {
                var ev = Event.current;
                if (rect.Contains(ev.mousePosition))
                {
                    if (ev.type == EventType.MouseDrag && ev.button == 0) { orbit += ev.delta; ev.Use(); Repaint(); }
                    if (ev.type == EventType.ScrollWheel) { distance = Mathf.Clamp(distance * Mathf.Exp(ev.delta.y * 0.05f), 0.15f, 10); ev.Use(); Repaint(); }
                }
                if (ev.type == EventType.Repaint) DrawPreview(rect);
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (string message in result?.Messages ?? new()) EditorGUILayout.LabelField(message, EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView();
        }

        private void DrawPreview(Rect rect)
        {
            preview.BeginPreview(rect, GUIStyle.none);
            var camera = preview.camera;
            float radius = Mathf.Max(bounds.extents.magnitude, 0.01f);
            var rotation = Quaternion.Euler(orbit.y, orbit.x, 0);
            camera.transform.SetPositionAndRotation(bounds.center - rotation * Vector3.forward * radius * 2.6f * distance, rotation);
            camera.nearClipPlane = radius * 0.001f; camera.farClipPlane = radius * (8 + 10 * distance);
            camera.fieldOfView = 35; camera.clearFlags = CameraClearFlags.Color; camera.backgroundColor = new Color(0.13f, 0.13f, 0.13f);
            foreach (var item in meshes)
                if ((item.Material != 2 || showOccluders) && (item.Material != 1 || showRemoved))
                    preview.DrawMesh(item.Mesh, Matrix4x4.identity, materials[item.Material], 0);
            camera.Render();
            GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.StretchToFill, false);
        }

        private void OnDisable() => Release();
        private void Release()
        {
            preview?.Cleanup(); preview = null;
            foreach (var item in owned) if (item != null) Object.DestroyImmediate(item);
            owned.Clear(); meshes.Clear(); materials = null;
        }
    }
}
