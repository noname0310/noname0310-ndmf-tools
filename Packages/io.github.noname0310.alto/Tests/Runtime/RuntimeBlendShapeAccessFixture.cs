using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    // No serialized renderer reference: runtime code can discover its own skin.
    public sealed class RuntimeBlendShapeAccessFixture : MonoBehaviour
    {
        public Object Asset;
        public int ShapeCount => GetComponent<SkinnedMeshRenderer>().sharedMesh.blendShapeCount;
        public string ShapeName(int index) => GetComponent<SkinnedMeshRenderer>().sharedMesh.GetBlendShapeName(index);
        public void SetWeight(int index, float value) => GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(index, value);
        public float GetWeight(int index) => GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(index);
    }
}
