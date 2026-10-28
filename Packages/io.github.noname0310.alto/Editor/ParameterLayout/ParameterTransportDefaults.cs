using System.Linq;
using nadena.dev.ndmf.animator;

namespace Noname.AvatarTools.ParameterLayout
{
    internal static class ParameterTransportDefaults
    {
        internal static bool Read(VirtualAnimatorController controller)
        {
            // Even a parameter-only WD-Off layer changes Unity's handling of defaults
            // and Direct Blend Trees elsewhere in an otherwise all-WD-On controller.
            // Keep that mode intact. For WD-Off or mixed controllers, do not introduce
            // a new default writer; the transport has no pose bindings of its own.
            var states = controller.AllReachableNodes().OfType<VirtualState>().ToArray();
            return states.Length > 0 && states.All(state => state.WriteDefaultValues);
        }
    }
}
