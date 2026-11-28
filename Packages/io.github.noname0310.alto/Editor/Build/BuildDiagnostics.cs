using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using nadena.dev.ndmf;
using nadena.dev.ndmf.fluent;
using nadena.dev.ndmf.localization;
using nadena.dev.ndmf.ui;
using Noname.AvatarTools.BlendShapeOptimization;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Build
{
    // Diagnostics belong to the build, including SDK callbacks after NDMF has finished.
    // Weak keys prevent a cancelled build from retaining its generated avatar/assets.
    internal static class BuildDiagnostics
    {
        private const string DiagnosticKey = "ALTO.BuildDiagnostic";
        private sealed class Session
        {
            internal BuildContext Context;
            internal bool Failed;
        }
        private static readonly ConditionalWeakTable<GameObject, Session> Sessions = new();

        internal static DeclaringPass RunReported(this Sequence sequence, string name, InlinePass action,
            [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
            sequence.Run(name, context => Run(context, name, () => action(context)), file, line);

        internal static void Run(BuildContext context, string stage, Action action)
        {
            var session = Sessions.GetValue(context.AvatarRootObject, _ => new Session());
            if (session.Context != context) { session.Context = context; session.Failed = false; }
            if (session.Failed) return;
            try { action(); }
            catch (Exception exception)
            {
                session.Failed = true;
                if (!Report(context, FromException(exception, stage))) throw;
                Debug.LogException(exception, context.AvatarRootObject);
            }
        }

        internal static bool RunSdk(GameObject avatar, string stage, Action action)
        {
            Sessions.TryGetValue(avatar, out var session);
            if (session?.Failed == true) return false;
            try
            {
                int before = session?.Context.ErrorReport.Errors.Count ?? 0;
                action();
                if (session != null && session.Context.ErrorReport.Errors.Count > before)
                    ShowUpdatedReport(session.Context.ErrorReport);
                return true;
            }
            catch (Exception exception)
            {
                if (session != null)
                {
                    session.Failed = true;
                    if (Report(session.Context, FromException(exception, stage)))
                        ShowUpdatedReport(session.Context.ErrorReport);
                }
                Debug.LogException(exception, avatar);
                return false;
            }
        }

        internal static void End(GameObject avatar) => Sessions.Remove(avatar);

        private static void ShowUpdatedReport(ErrorReport report)
        {
            if (Application.isBatchMode || ErrorReportWindow.DISABLE_WINDOW) return;
            // NDMF's public setter avoids refreshing when handed the same report instance.
            foreach (var window in Resources.FindObjectsOfTypeAll<ErrorReportWindow>())
                if (window.CurrentReport == report) window.CurrentReport = null;
            ErrorReportWindow.ShowReport(report);
        }

        // Keep the original exception type for callers which already handle validation failures.
        internal static InvalidOperationException Validation(string message) =>
            Validation("ALTO settings could not be applied", message, null);

        internal static InvalidOperationException Validation(string title, string details, string hint,
            params Object[] references)
        {
            var exception = new InvalidOperationException(details);
            var diagnostic = new BuildDiagnostic(ErrorSeverity.Error, title, details, hint);
            foreach (var reference in references)
                if (reference != null) diagnostic.AddReference(ObjectRegistry.GetReference(reference));
            exception.Data[DiagnosticKey] = diagnostic;
            return exception;
        }

        internal static BuildDiagnostic FromException(Exception exception, string stage)
        {
            var current = exception;
            while ((current is TargetInvocationException || current is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                   && current.InnerException != null) current = current.InnerException;
            var diagnostic = current.Data[DiagnosticKey] as BuildDiagnostic ?? new BuildDiagnostic(
                ErrorSeverity.InternalError, "ALTO could not complete this build step",
                current.GetType().Name + ": " + current.Message,
                "Include this message and the stack trace when reporting the problem.");
            diagnostic.Stage = stage;
            diagnostic.StackTrace = exception.ToString();
            return diagnostic;
        }

        internal static void Warning(BuildContext context, string title, string details, string hint = null,
            params Object[] references)
        {
            var diagnostic = new BuildDiagnostic(ErrorSeverity.NonFatal, title, details, hint);
            using (new ObjectRegistryScope(context.ObjectRegistry))
                foreach (var reference in references)
                    if (reference != null) diagnostic.AddReference(ObjectRegistry.GetReference(reference));
            Report(context, diagnostic);
        }

        internal static bool Report(BuildContext context, BuildDiagnostic diagnostic)
        {
            // NDMF exposes reports publicly but reopening a report's execution scope is
            // internal in 1.x. Keep this compatibility bridge confined to diagnostics;
            // it never runs a build pass or replaces another plugin's error handling.
            try
            {
                using var reportScope = NdmfReportBridge.Open(context.ErrorReport);
                using var pluginScope = NdmfReportBridge.Plugin(context.ErrorReport);
                using var passScope = NdmfReportBridge.Pass(context.ErrorReport, diagnostic.Stage);
                using var registry = new ObjectRegistryScope(context.ObjectRegistry);
                ErrorReport.ReportError(diagnostic);
                return true;
            }
            catch (Exception bridgeError)
            {
                Debug.LogWarning("[ALTO] NDMF diagnostic integration is unavailable: " + bridgeError.Message);
                if (diagnostic.Severity >= ErrorSeverity.Error) Debug.LogError(diagnostic.ToMessage(), context.AvatarRootObject);
                else Debug.LogWarning(diagnostic.ToMessage(), context.AvatarRootObject);
                return false;
            }
        }

        private static class NdmfReportBridge
        {
            private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            private static readonly ConstructorInfo Scope = typeof(ErrorReport).Assembly.GetType("nadena.dev.ndmf.ErrorReportScope")
                ?.GetConstructor(Flags, null, new[] { typeof(ErrorReport) }, null);
            private static readonly MethodInfo WithPlugin = typeof(ErrorReport).GetMethod("WithContext", Flags);
            private static readonly MethodInfo WithPass = typeof(ErrorReport).GetMethod("WithContextPassName", Flags);
            internal static IDisposable Open(ErrorReport report) =>
                (IDisposable)(Scope ?? throw new MissingMethodException("NDMF ErrorReportScope")).Invoke(new object[] { report });
            internal static IDisposable Plugin(ErrorReport report) =>
                (IDisposable)(WithPlugin ?? throw new MissingMethodException("NDMF WithContext")).Invoke(report, new object[] { ALTOPlugin.Instance });
            internal static IDisposable Pass(ErrorReport report, string stage) => stage == null ? null :
                (IDisposable)(WithPass ?? throw new MissingMethodException("NDMF WithContextPassName")).Invoke(report, new object[] { stage });
        }
    }

    internal sealed class BuildDiagnostic : SimpleError
    {
        private readonly string title, details, hint;
        internal string Stage, StackTrace;
        public override ErrorSeverity Severity { get; }
        public override Localizer Localizer => null;
        public override string TitleKey => "ALTO.BuildDiagnostic";

        internal BuildDiagnostic(ErrorSeverity severity, string title, string details, string hint = null)
        { Severity = severity; this.title = title; this.details = details; this.hint = hint; }

        public override string FormatTitle() => title;
        public override string FormatDetails() =>
            (string.IsNullOrEmpty(Stage) ? "" : "Build step: " + Stage + "\n\n") + details +
            (string.IsNullOrEmpty(hint) ? "" : "\n\n" + hint);
        public override string FormatHint() => null;

        public override VisualElement CreateVisualElement(ErrorReport report)
        {
            var element = base.CreateVisualElement(report);
            if (!string.IsNullOrEmpty(StackTrace))
            {
                var foldout = new Foldout { text = "Stack trace", value = false };
                foldout.Add(new TextField { value = StackTrace, multiline = true, isReadOnly = true });
                element.Add(foldout);
            }
            return element;
        }
    }
}
