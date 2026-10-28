using System;
using UnityEditor;

namespace Noname.AvatarTools.Build
{
    [InitializeOnLoad]
    internal static class EditorBuildReadiness
    {
        internal static bool Initialized { get; private set; }

        static EditorBuildReadiness()
        {
            // Optional build backends can initialize in delayCall after assembly reload.
            // An update callback can run before them. Resume on the update after
            // the first delay boundary, when every initializer in that batch has
            // returned, without requiring a second Inspector repaint.
            EditorApplication.delayCall += () => EditorApplication.update += CompleteInitialization;
        }

        private static void CompleteInitialization()
        {
            EditorApplication.update -= CompleteInitialization;
            Initialized = true;
        }

        internal static bool Ready => Initialized && !EditorApplication.isCompiling && !EditorApplication.isUpdating;

        internal static void RequireReady()
        {
            if (!Ready) throw new InvalidOperationException("Editor initialization or asset import is still pending. " +
                "Wait for the Editor's delayed initialization callbacks before starting an avatar build or analysis.");
        }

        internal static void Schedule(Action action)
        {
            EditorApplication.CallbackFunction execute = null;
            execute = () =>
            {
                if (!Ready) return;
                EditorApplication.update -= execute;
                action();
            };
            EditorApplication.update += execute;
        }
    }
}
