using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Noname.AvatarTools
{
    [Serializable]
    public sealed class ExpressionControlPreservation
    {
        public VRCExpressionsMenu Menu;
        [HideInInspector] public bool Selected;
        [HideInInspector] public string ControlName;
        [HideInInspector] public VRCExpressionsMenu.Control.ControlType ControlType;
        public bool IncludeSubMenus = true;
    }

    [AddComponentMenu("Noname/ALTO Trace And Optimize Expressions")]
    [DisallowMultipleComponent]
    [UnityEngine.Scripting.APIUpdating.MovedFrom(true, "Noname.AvatarTools", "Noname.AdvancedAvatarOptimizer", "AdvancedTraceAndOptimizeExpressions")]
    public sealed class ALTOTraceAndOptimizeExpressions : MonoBehaviour, INDMFEditorOnly
    {
        [Tooltip("Remove controls whose parameters were disconnected by ALTO Animator Parameter Policy. Every channel must be unused.")]
        public bool RemoveFrozenControls = true;
        [Tooltip("Remove controls that cannot affect an observable avatar output. Parameter declarations alone are not consumers.")]
        public bool RemoveDeadControls = true;
        [Tooltip("Remove empty navigation, including cycles without usable contents. Submenus with a live activation parameter remain.")]
        public bool RemoveEmptySubMenus = true;
        [Tooltip("Remove unused, non-networked Expression Parameters. Synchronized parameter order, types and settings remain unchanged.")]
        public bool PruneUnusedLocalParameters = true;

        [Tooltip("Preserve all controls in these menu assets and their submenus. Matching display names and control types are retained after generators rename parameters.")]
        public List<VRCExpressionsMenu> PreserveMenus = new();
        public List<ExpressionControlPreservation> PreserveControls = new();

        public VRCAvatarDescriptor FindDescriptor()
        {
            for (var current = transform; current != null; current = current.parent)
            {
                var descriptor = current.GetComponent<VRCAvatarDescriptor>();
                if (descriptor != null) return descriptor;
            }
            return null;
        }
    }
}
