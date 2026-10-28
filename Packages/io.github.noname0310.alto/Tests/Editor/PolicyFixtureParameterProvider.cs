using System.Collections.Generic;
using System.Collections.Immutable;
using nadena.dev.ndmf;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    [ParameterProviderFor(typeof(ParameterPolicySourceFixture))]
    internal sealed class PolicyFixtureParameterProvider : IParameterProvider
    {
        private readonly ParameterPolicySourceFixture source;
        public PolicyFixtureParameterProvider(ParameterPolicySourceFixture source) { this.source = source; }
        public IEnumerable<ProvidedParameter> GetSuppliedParameters(BuildContext context = null)
        {
            if (!string.IsNullOrEmpty(source.Provided))
                yield return new ProvidedParameter(source.Provided,
                    source.Prefix ? ParameterNamespace.PhysBonesPrefix : ParameterNamespace.Animator,
                    source, null, UnityEngine.AnimatorControllerParameterType.Float);
        }
        public void RemapParameters(ref ImmutableDictionary<(ParameterNamespace, string), ParameterMapping> nameMap,
            BuildContext context = null)
        {
            if (string.IsNullOrEmpty(source.From) || string.IsNullOrEmpty(source.To)) return;
            var ns = source.Prefix ? ParameterNamespace.PhysBonesPrefix : ParameterNamespace.Animator;
            var name = nameMap.TryGetValue((ns, source.To), out var parent) ? parent.ParameterName : source.To;
            nameMap = nameMap.SetItem((ns, source.From), new ParameterMapping(name));
        }
    }
}
