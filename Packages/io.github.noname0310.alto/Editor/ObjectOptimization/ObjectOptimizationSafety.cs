using System.Linq;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEditor.Compilation;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.ObjectOptimization
{
    internal static class ObjectOptimizationSafety
    {
        private static readonly HashSet<string> EditorAssemblies = new(CompilationPipeline.GetAssemblies(AssembliesType.Editor)
            .Where(a => (a.flags & AssemblyFlags.EditorAssembly) != 0).Select(a => a.name));

        internal static string UnsupportedRuntime(GameObject avatar)
        {
            foreach (var component in avatar.GetComponentsInChildren<Component>(true))
            {
                if (component == null) return "a missing component may have runtime side effects";
                if (EditorAssemblies.Contains(component.GetType().Assembly.GetName().Name)) continue;
                if (!(component is MonoBehaviour) || component is INDMFEditorOnly || component is IEditorOnly ||
                    component is VRCAvatarDescriptor || component is VRCContactReceiver || component is VRCContactSender ||
                    component is VRCPhysBone || component is VRCPhysBoneCollider || component is VRCRaycast ||
                    component is VRCHeadChop || component is VRCSpatialAudioSource || component is VRC.Core.PipelineManager) continue;
                if (component.GetType().FullName == "ONSPAudioSource" && component.GetType().Assembly.GetName().Name == "VRC.SDKBase") continue;
                // SDK constraints only mutate their configured transforms; those references are
                // protected separately. Custom subclasses are not treated as SDK implementations.
                if (component.GetType().Assembly.GetName().Name == "VRC.SDK3.Dynamics.Constraint") continue;
                return "unsupported runtime component " + component.GetType().FullName;
            }
            if (avatar.GetComponentsInChildren<Animation>(true).Any()) return "a legacy Animation component can play unindexed clips";
            if (avatar.GetComponentsInChildren<Component>(true).Any(c => c != null && c.GetType().FullName == "UnityEngine.Playables.PlayableDirector"))
                return "a Playable Director can play unindexed clips";
            return null;
        }
    }
}
