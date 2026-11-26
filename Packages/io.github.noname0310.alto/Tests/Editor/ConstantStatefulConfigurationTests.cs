using System.Collections.Generic;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase("m_Volume")] [TestCase("m_Pitch")] [TestCase("Pan2D")]
        public void ConstantStatefulConfiguration_AudioDefaultsMatchNativeAnimator(string property)
        {
            Mesh("Shape"); var settings = ObjectSettings(); var audio = PolicyChild("Audio").AddComponent<AudioSource>();
            float initial = property == "Pan2D" ? 0 : 1;
            var source = Controller(StaticCurve(EditorCurveBinding.FloatCurve(audio.name, typeof(AudioSource), property), initial));
            float Read() => property == "m_Volume" ? audio.volume : property == "m_Pitch" ? audio.pitch : audio.panStereo;
            var expected = SampleFrozen(source, (p, i) => { }, 8, Read);
            var index = ConstantOutputIndex(source, out var controller);
            Assert.That(ConstantComponentProperties.Apply(index, settings, _ => false, new List<string>()), Is.EqualTo(1));
            Assert.That(SampleFrozen(CommitAnimator(controller, root), (p, i) => { }, 8, Read), Is.EqualTo(expected));
        }

        [TestCase("m_Volume", false)] [TestCase("m_Volume", true)]
        [TestCase("m_Pitch", false)] [TestCase("m_Pitch", true)]
        [TestCase("Loop", false)] [TestCase("Loop", true)]
        public void ConstantStatefulConfiguration_PlayAudioNativeWritersRemain(string property, bool writes)
        {
            var settings = ObjectSettings(); var audio = PolicyChild("Audio").AddComponent<AudioSource>();
            var source = Controller(StaticCurve(EditorCurveBinding.FloatCurve(audio.name, typeof(AudioSource), property), property == "Loop" ? 0 : 1));
            var behaviour = Track(source.layers[0].stateMachine.defaultState.AddStateMachineBehaviour<VRCAnimatorPlayAudio>());
            behaviour.SourcePath = audio.name;
            var mode = writes ? VRC_AnimatorPlayAudio.ApplySettings.AlwaysApply : VRC_AnimatorPlayAudio.ApplySettings.NeverApply;
            behaviour.VolumeApplySettings = behaviour.PitchApplySettings = behaviour.LoopApplySettings = mode;
            var index = ConstantOutputIndex(source, out _);
            Assert.That(ConstantComponentProperties.Apply(index, settings, _ => true, new List<string>()), Is.EqualTo(writes ? 0 : 1));
        }

        [TestCase(false)] [TestCase(true)]
        public void ConstantStatefulConfiguration_ChangedAudioAndParticleInitializationRemain(bool particles)
        {
            var settings = ObjectSettings(); Component target = particles ?
                PolicyChild("Output").AddComponent<ParticleSystem>() : PolicyChild("Output").AddComponent<AudioSource>();
            string property = particles ? "InitialModule.startLifetime.scalar" : "m_Volume";
            var index = ConstantOutputIndex(Controller(StaticCurve(EditorCurveBinding.FloatCurve(target.name, target.GetType(), property), .5f)), out _);
            var messages = new List<string>();
            Assert.That(ConstantComponentProperties.Apply(index, settings, _ => true, messages), Is.Zero);
            Assert.That(messages, Has.Some.Contains("lifecycle proof"));
        }

        [Test]
        public void ConstantStatefulConfiguration_InvariantParticleSpeedKeepsNativeSimulation()
        {
            Mesh("Shape"); var settings = ObjectSettings(); var particles = PolicyChild("Particles").AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particles.main; main.playOnAwake = false; main.startSpeed = 2; main.startLifetime = 3;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            var emission = particles.emission; emission.rateOverTime = 60;
            var shape = particles.shape; shape.enabled = false;
            particles.useAutoRandomSeed = false; particles.randomSeed = 1234;
            var source = Controller(StaticCurve(EditorCurveBinding.FloatCurve(particles.name, typeof(ParticleSystem), "InitialModule.startSpeed.scalar"), 2));
            void Simulate() { particles.Simulate(1f / 60, false, false, false); }
            particles.Play(); SampleFrozen(source, (p, i) => { }, 12, () => { Simulate(); return 0; });
            var expected = new ParticleSystem.Particle[particles.particleCount]; particles.GetParticles(expected);
            Assert.That(expected.Length, Is.GreaterThan(0), "The fixture must exercise real emitted particles");
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); particles.randomSeed = 1234;
            var index = ConstantOutputIndex(source, out var controller);
            Assert.That(ConstantComponentProperties.Apply(index, settings, _ => false, new List<string>()), Is.EqualTo(1));
            particles.Play(); SampleFrozen(CommitAnimator(controller, root), (p, i) => { }, 12, () => { Simulate(); return 0; });
            var actual = new ParticleSystem.Particle[particles.particleCount]; particles.GetParticles(actual);
            Assert.That(actual.Length, Is.EqualTo(expected.Length));
            for (int i = 0; i < actual.Length; i++)
            {
                Assert.That(actual[i].randomSeed, Is.EqualTo(expected[i].randomSeed));
                Assert.That(actual[i].position, Is.EqualTo(expected[i].position));
                Assert.That(actual[i].velocity, Is.EqualTo(expected[i].velocity));
                Assert.That(actual[i].remainingLifetime, Is.EqualTo(expected[i].remainingLifetime));
            }
        }
    }
}
