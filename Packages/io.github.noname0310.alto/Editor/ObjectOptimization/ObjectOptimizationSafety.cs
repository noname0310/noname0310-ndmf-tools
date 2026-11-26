using System.Linq;
using System.Collections.Generic;
using Noname.AvatarTools.Analysis;
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
        // The SDK attaches this hidden hierarchy listener to PhysBone targets in the Editor.
        // It only notifies the owning simulations; it is not an independent avatar output.
        // Resolve the exact SDK type so an unrelated script with the same name stays unsafe.
        private static readonly System.Type ParentChangeDetector =
            System.Type.GetType("VRC.Dynamics.ParentChangeDetector, VRC.Dynamics");

        internal static bool IsParentChangeDetector(Component component) =>
            component != null && component.GetType() == ParentChangeDetector;

        private static readonly HashSet<string> EditorAssemblies = new(CompilationPipeline.GetAssemblies(AssembliesType.Editor)
            .Where(a => (a.flags & AssemblyFlags.EditorAssembly) != 0).Select(a => a.name));
        private static readonly Dictionary<System.Type, bool> SupportedTypes = new();
        private static readonly Dictionary<System.Reflection.Assembly, string> AssemblyNames = new();

        internal static bool IsBuildOnly(Component component) => component is INDMFEditorOnly || component is IEditorOnly ||
            component != null && EditorAssemblies.Contains(AssemblyName(component.GetType()));

        // Type/assembly identity is immutable for the lifetime of this Editor
        // domain. Keep component state out of this metadata cache.
        internal static string AssemblyName(System.Type type)
        {
            var assembly = type.Assembly;
            if (!AssemblyNames.TryGetValue(assembly, out string name))
                AssemblyNames.Add(assembly, name = assembly.GetName().Name);
            return name;
        }

        private static bool Supports(Component component)
        {
            var type = component.GetType();
            if (SupportedTypes.TryGetValue(type, out bool supported)) return supported;
            supported = !(component is MonoBehaviour) || component is INDMFEditorOnly || component is IEditorOnly ||
                component is VRCAvatarDescriptor || component is VRCContactReceiver || component is VRCContactSender ||
                component is VRCPhysBone || component is VRCPhysBoneCollider || component is VRCRaycast ||
                component is VRCHeadChop || component is VRCSpatialAudioSource || component is VRC.Core.PipelineManager ||
                IsParentChangeDetector(component) || EditorAssemblies.Contains(AssemblyName(type)) ||
                type.FullName == "ONSPAudioSource" && AssemblyName(type) == "VRC.SDKBase" ||
                // Custom subclasses outside the SDK assembly remain unsafe.
                AssemblyName(type) == "VRC.SDK3.Dynamics.Constraint";
            SupportedTypes.Add(type, supported);
            return supported;
        }

        internal static string UnsupportedRuntime(GameObject avatar)
        {
            using var cost = AnalysisCost.Measure("Runtime component safety");
            bool legacyAnimation = false, playableDirector = false;
            foreach (var component in avatar.GetComponentsInChildren<Component>(true))
            {
                if (component == null) return "a missing component may have runtime side effects";
                if (!Supports(component)) return "unsupported runtime component " + component.GetType().FullName;
                legacyAnimation |= component is Animation;
                playableDirector |= component.GetType().FullName == "UnityEngine.Playables.PlayableDirector";
            }
            // Preserve diagnostic precedence even when a later component is unsafe.
            if (legacyAnimation) return "a legacy Animation component can play unindexed clips";
            if (playableDirector) return "a Playable Director can play unindexed clips";
            return null;
        }
    }
}
