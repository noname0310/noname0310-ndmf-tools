using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    [ExecuteAlways]
    public sealed class ParameterPolicySourceFixture : MonoBehaviour
    {
        public RuntimeAnimatorController Controller;
        public string From, To;
        public string Provided;
        public bool Prefix;
        public Mesh PreviewMesh;
        public bool ModifyPreviewOnEnable;
        public UnityEngine.Object ReferencedObject;

        private void OnEnable()
        {
            if (!ModifyPreviewOnEnable || PreviewMesh == null) return;
            var vertices = PreviewMesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] += Vector3.one;
            PreviewMesh.vertices = vertices;
        }
    }

}
