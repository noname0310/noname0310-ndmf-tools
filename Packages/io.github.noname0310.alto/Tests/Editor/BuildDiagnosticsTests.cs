using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using nadena.dev.ndmf;
using nadena.dev.ndmf.ui;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed class BuildDiagnosticsTests
    {
        private GameObject root;
        private bool disableWindow;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("ALTO diagnostic test");
            root.AddComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            disableWindow = ErrorReportWindow.DISABLE_WINDOW;
            ErrorReportWindow.DISABLE_WINDOW = true;
        }

        [TearDown]
        public void TearDown()
        {
            BuildDiagnostics.End(root);
            Object.DestroyImmediate(root);
            ErrorReportWindow.DISABLE_WINDOW = disableWindow;
        }

        [Test]
        public void ReferencedExclusionShowsCauseAndClickableObjectsWithoutOpeningStackTrace()
        {
            var target = new GameObject("Excluded target"); target.transform.SetParent(root.transform);
            var owner = new GameObject("Reference owner"); owner.transform.SetParent(root.transform);
            var constraint = owner.AddComponent<ParentConstraint>();
            constraint.AddSource(new ConstraintSource { sourceTransform = target.transform, weight = 1 });
            var filter = root.AddComponent<ALTOBuildExclusions>();
            filter.ApplyOnPlatforms = OptimizationPlatforms.All;
            filter.ExcludedObjects.Add(target);
            var context = new BuildContext(root, null, isClone: false);
            using (new ObjectRegistryScope(context.ObjectRegistry))
            {
                LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: Cannot exclude"));
                BuildDiagnostics.Run(context, "Apply explicit build exclusions", () => BuildFilterPass.ApplyEarly(context, filter));
            }
            Assert.That(context.Successful, Is.False);
            Assert.That(context.ErrorReport.Errors, Has.Count.EqualTo(1));
            var diagnostic = (BuildDiagnostic)context.ErrorReport.Errors.Single().TheError;
            Assert.That(diagnostic.Severity, Is.EqualTo(ErrorSeverity.Error));
            Assert.That(diagnostic.FormatDetails(), Does.Contain("Reference owner").And.Contain("Excluded target").And.Contain("Remove or update"));
            CollectionAssert.IsSubsetOf(new Object[] { constraint, target.transform, filter }, diagnostic.References.Select(r => r.Object).ToArray());
            var ui = diagnostic.CreateVisualElement(context.ErrorReport);
            Assert.That(ui.Q<Label>("description").text, Does.Contain("Excluded target"));
            Assert.That(target != null, Is.True, "Validation must still finish before deletion.");
        }

        [Test]
        public void FailedPassStopsLaterAltoPassesAndSdkCallbacks()
        {
            var context = new BuildContext(root, null);
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: Conflicting settings"));
            BuildDiagnostics.Run(context, "Validate", () => throw BuildDiagnostics.Validation("Conflicting settings"));
            bool called = false;
            BuildDiagnostics.Run(context, "Later pass", () => called = true);
            Assert.That(BuildDiagnostics.RunSdk(root, "Later SDK callback", () => called = true), Is.False);
            Assert.That(called, Is.False);
            Assert.That(context.ErrorReport.Errors, Has.Count.EqualTo(1));
        }

        [Test]
        public void LateSdkFailureIsAttachedToTheSameNdmfAvatarReport()
        {
            var context = new BuildContext(root, null);
            BuildDiagnostics.Run(context, "Capture", () => { });
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: Late settings conflict"));
            bool result = BuildDiagnostics.RunSdk(root, "Final SDK cleanup", () => throw BuildDiagnostics.Validation("Late settings conflict"));
            Assert.That(result, Is.False);
            Assert.That(context.Successful, Is.False);
            var entry = context.ErrorReport.Errors.Single();
            Assert.That(entry.Plugin.QualifiedName, Is.EqualTo("noname.alto"));
            Assert.That(entry.PassName, Is.EqualTo("Final SDK cleanup"));
            Assert.That(entry.TheError.ToMessage(), Does.Contain("Late settings conflict"));
        }

        [Test]
        public void UnexpectedExceptionsRemainInternalErrorsButShowTheirMessage()
        {
            var context = new BuildContext(root, null);
            LogAssert.Expect(LogType.Exception, new Regex("NullReferenceException: Missing output map"));
            BuildDiagnostics.Run(context, "Finalize", () => throw new NullReferenceException("Missing output map"));
            var diagnostic = (BuildDiagnostic)context.ErrorReport.Errors.Single().TheError;
            Assert.That(diagnostic.Severity, Is.EqualTo(ErrorSeverity.InternalError));
            Assert.That(diagnostic.FormatDetails(), Does.Contain("Missing output map"));
            Assert.That(diagnostic.StackTrace, Does.Contain("NullReferenceException"));
        }

        [Test]
        public void WarningsRemainNonfatalAndASecondBuildDoesNotInheritFailure()
        {
            var first = new BuildContext(root, null);
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: First build"));
            BuildDiagnostics.Run(first, "Validate", () => throw BuildDiagnostics.Validation("First build"));
            var second = new BuildContext(root, null);
            bool ran = false;
            BuildDiagnostics.Run(second, "Capture", () => ran = true);
            Assert.That(ran, Is.True);
            BuildDiagnostics.Warning(second, "Triangle budget could not be reached", "70002 / 70000 triangles");
            Assert.That(second.Successful, Is.True);
            Assert.That(second.ErrorReport.Errors.Single().TheError.Severity, Is.EqualTo(ErrorSeverity.NonFatal));
        }

        [Test]
        public void ReflectionWrappersRetainTheUnderlyingValidationDiagnostic()
        {
            var error = new TargetInvocationException(BuildDiagnostics.Validation("Invalid scope"));
            var diagnostic = BuildDiagnostics.FromException(error, "Capture scope");
            Assert.That(diagnostic.Severity, Is.EqualTo(ErrorSeverity.Error));
            Assert.That(diagnostic.FormatDetails(), Does.Contain("Invalid scope"));
            Assert.That(diagnostic.StackTrace, Does.Contain("TargetInvocationException"));
        }
    }
}
