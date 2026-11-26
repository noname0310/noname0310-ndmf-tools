using Noname.AvatarTools.Analysis;
using NUnit.Framework;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed class ShaderEntryUseTests
    {
        [TestCase("float unused() { return _Gain; } float4 frag() { return 1; }", false)]
        [TestCase("float helper() { return _Gain; } float4 frag() { return helper(); }", true)]
        [TestCase("float helper(float x) { return x; } float helper(float2 x) { return _Gain; } float4 frag() { return helper(0); }", true)]
        [TestCase("static float value = _Gain; float4 frag() { return value; }", true)]
        [TestCase("static float values[2] = {0,_Gain}; float4 frag() { return values[0]; }", true)]
        [TestCase("static float a=0,b=_Gain; float4 frag() { return b; }", true)]
        [TestCase("float helper() { return _Gain; } static float value=helper(); float4 frag() { return value; }", true)]
        [TestCase("float _Gain; float4 frag() { return 1; }", false)]
        [TestCase("cbuffer Material { float _Gain; }; float4 frag() { return 1; }", false)]
        public void EntryUse_CoversCallsAndEveryGlobalInitializer(string source, bool expected)
        {
            var slice = ShaderEntryUse.Analyze(new ShaderInputProgram(source, "test.hlsl"), "frag",
                ShaderInputQuery.Identifier("_Gain"), out bool read);
            Assert.That(slice.Complete, Is.True, slice.Issue);
            Assert.That(read, Is.EqualTo(expected));
            Assert.That(slice.Names, Does.Contain("frag"));
        }

        [Test]
        public void EntryUse_UnprocessedBranchesCannotProveAbsence()
        {
            var slice = ShaderEntryUse.Analyze(new ShaderInputProgram("#if EXTERNAL\nfloat f(){return _Gain;}\n#endif\nfloat4 frag(){return 1;}", "test.hlsl"),
                "frag", ShaderInputQuery.Identifier("_Gain"), out bool read);
            Assert.That(slice.Complete, Is.False); Assert.That(read, Is.True);
        }

        [Test]
        public void EntryUse_RetainsDormantCallerDiscoveryWithoutRootingKeywordDeclarations()
        {
            var program = new ShaderInputProgram("static const int ENGINE_CHOICE=0; float _Gain; " +
                "float leaf(){return _Gain;} float middle(){return leaf();} float4 frag(){return 1;}", "test.hlsl");
            var slice = ShaderEntryUse.Analyze(program, "frag", ShaderInputQuery.Identifier("_Gain"), out bool read);
            Assert.That(slice.Complete, Is.True, slice.Issue); Assert.That(read, Is.False);
            Assert.That(slice.Names, Does.Contain("leaf")); Assert.That(slice.Names, Does.Contain("middle"));
            Assert.That(slice.Names, Does.Not.Contain("ENGINE_CHOICE"));
        }
    }
}
