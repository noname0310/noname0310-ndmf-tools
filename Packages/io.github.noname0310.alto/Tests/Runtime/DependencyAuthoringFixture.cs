using nadena.dev.ndmf;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed class DependencyAuthoringFixture : MonoBehaviour, INDMFEditorOnly
    {
        public Material Material;
        public Component Target;
    }
}
