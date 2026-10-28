using System;
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(Noname.AvatarTools.BlendShapeOptimization.Tests.BuildFailureFixturePlugin))]

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    internal sealed class BuildFailureFixturePlugin : Plugin<BuildFailureFixturePlugin>
    {
        public override string QualifiedName => "io.github.noname0310.alto.tests.build-failure";
        public override string DisplayName => "ALTO build failure fixture";
        protected override void Configure() => InPhase(BuildPhase.Generating).Run("Validate failure fixture", context =>
        {
            foreach (var fixture in context.AvatarRootObject.GetComponentsInChildren<BuildFailureFixture>(true)) ValidateFixture(fixture);
        });
        private static void ValidateFixture(BuildFailureFixture fixture) =>
            throw new InvalidOperationException("Deliberate build-only fixture failure: " + fixture.name);
    }
}
