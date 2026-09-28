using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Noname.AvatarTools.Editor
{
    internal static class PoseOverridePresets
    {
        // Exact MMD names. Width variants are intentional and must match the source mesh.
        internal static readonly string[] MmdEyeShapeNames =
        {
            "\u307e\u3070\u305f\u304d",             // Blink
            "\u7b11\u3044",                         // Smile with closed eyes
            "\u30a6\u30a3\u30f3\u30af",             // Wink
            "\u30a6\u30a3\u30f3\u30af\u53f3",       // Right wink
            "\u30a6\u30a3\u30f3\u30af\uff12",       // Wink 2
            "\uff73\uff68\uff9d\uff78\uff12\u53f3", // Right wink 2
            "\u306a\u3054\u307f"                    // Relaxed closed eyes
        };

        private static readonly EyePose[] MmdEyePoses =
        {
            EyePose.Closed, EyePose.Joyful, EyePose.JoyfulLeft, EyePose.JoyfulRight,
            EyePose.ClosedLeft, EyePose.ClosedRight, EyePose.Closed
        };

        internal static Dictionary<string, EyePose> GetMmdRequests(Mesh mesh)
            => Enumerable.Range(0, MmdEyeShapeNames.Length)
                .Where(i => mesh != null && mesh.GetBlendShapeIndex(MmdEyeShapeNames[i]) >= 0)
                .ToDictionary(i => MmdEyeShapeNames[i], i => MmdEyePoses[i]);

        internal static bool TryGetVrcBlink(BlendShapePoseOverride component, out string name, out string error)
        {
            name = null;
            error = null;
            var descriptor = component.FindDescriptor();
            if (descriptor == null)
            {
                error = "No VRC Avatar Descriptor was found above this component.";
                return false;
            }
            var settings = descriptor.customEyeLookSettings;
            if (settings.eyelidType != VRCAvatarDescriptor.EyelidType.Blendshapes)
            {
                error = "Set the descriptor's Eyelid Type to Blendshapes to use this preset.";
                return false;
            }
            var renderer = settings.eyelidsSkinnedMesh;
            if (renderer == null || renderer.sharedMesh == null)
            {
                error = "Assign the descriptor's Eyelids Mesh to use this preset.";
                return false;
            }
            if (component.TargetMesh != renderer)
            {
                error = "Set Target Mesh to the descriptor's Eyelids Mesh to add its Blink shape.";
                return false;
            }
            int index = settings.eyelidsBlendshapes != null && settings.eyelidsBlendshapes.Length > 0
                ? settings.eyelidsBlendshapes[0] : -1;
            if (index < 0 || index >= renderer.sharedMesh.blendShapeCount)
            {
                error = "Assign a valid Blink slot in the descriptor to use this preset.";
                return false;
            }
            name = renderer.sharedMesh.GetBlendShapeName(index);
            return true;
        }

        internal static string[] GetAvailableMmdShapes(Mesh mesh)
            => mesh == null ? Array.Empty<string>() : MmdEyeShapeNames.Where(name => mesh.GetBlendShapeIndex(name) >= 0).ToArray();

        internal static int AddMissingEntries(BlendShapePoseOverride component, IEnumerable<string> names)
        {
            if (component.Overrides == null) component.Overrides = new List<BlendShapePoseEntry>();
            var existing = new HashSet<string>(component.Overrides.Where(e => e != null).Select(e => e.BlendShapeName), StringComparer.Ordinal);
            int added = 0;
            foreach (string name in names)
            {
                if (string.IsNullOrEmpty(name) || !existing.Add(name)) continue;
                // Discovery assigns each expression independently after the row has been added.
                component.Overrides.Add(new BlendShapePoseEntry { BlendShapeName = name });
                added++;
            }
            return added;
        }
    }
}
