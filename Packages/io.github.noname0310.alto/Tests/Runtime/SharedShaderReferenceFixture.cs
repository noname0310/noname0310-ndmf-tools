using nadena.dev.ndmf;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed class SharedShaderReferenceFixture : MonoBehaviour, INDMFEditorOnly
    {
        public Material Material;
        public Shader Shader;
        public UnityEngine.Object Asset;
    }
}
