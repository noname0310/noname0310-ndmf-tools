using UnityEngine;
using UnityEngine.Animations;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    [RequireComponent(typeof(PositionConstraint))]
    public sealed class RequiredSdkAuthoringFixture : MonoBehaviour, IEditorOnly
    {
        public Component Target;
    }
}
