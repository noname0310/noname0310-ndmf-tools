using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using Noname.AvatarTools.Build;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [Test]
        public void AutomaticAdditionalPassRulesKeepPrimaryGeometryAndPermitAnimatedSwaps()
        {
            MaterialFixture(2, 4);
            var filter = Filter(); filter.RemoveAdditionalMaterialPasses = true;
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            var rules = AutomaticRenderingRules.MaterialRules(context.GetState<BuildFilterState>(), new[] { renderer });
            var plan = MaterialSlotPlan.Create(renderer, rules);
            var controller = VirtualSlotController(SlotClip(3, renderer.sharedMaterials[0]));
            Assert.That(RewriteSlots(controller, plan, rules.ToArray()), Is.EqualTo(1));
            var source = renderer.sharedMesh; plan.Apply(context);
            Assert.That(renderer.sharedMesh, Is.SameAs(source));
            Assert.That(renderer.sharedMaterials.Length, Is.EqualTo(2));
            Assert.That(source.subMeshCount, Is.EqualTo(2));
        }

        [Test]
        public void AutomaticPassPreservationDoesNotOverrideAnExplicitExclusion()
        {
            MaterialFixture(1, 3);
            var filter = Filter(); filter.RemoveAdditionalMaterialPasses = true;
            filter.PreserveAutomaticRenderers.Add(renderer);
            var explicitRule = new MaterialExclusion { Renderer = renderer, Slot = 2 };
            filter.MaterialExclusions.Add(explicitRule);
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            var rules = AutomaticRenderingRules.MaterialRules(context.GetState<BuildFilterState>(), new[] { renderer });
            Assert.That(rules, Is.EqualTo(new[] { explicitRule }));
            Assert.That(MaterialSlotPlan.Create(renderer, rules).SlotMap, Is.EqualTo(new[] { 0, 1, -1 }));
        }

        [Test]
        public void AutomaticRendererRulesFindGeneratedInactiveRenderersInsideScope()
        {
            var filter = Filter(); var scope = PolicyChild("Scope"); filter.ScopeRoot = scope.transform;
            filter.ExcludedRendererTypes = RendererFilterTypes.ParticleSystems | RendererFilterTypes.Lines | RendererFilterTypes.Trails;
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            var obj = PolicyChild("Generated", scope.transform); obj.SetActive(false);
            var particles = obj.AddComponent<ParticleSystem>(); var particleRenderer = obj.GetComponent<ParticleSystemRenderer>();
            var line = PolicyChild("Line", scope.transform).AddComponent<LineRenderer>();
            var trail = PolicyChild("Trail", scope.transform).AddComponent<TrailRenderer>();
            var outside = PolicyChild("Outside").AddComponent<LineRenderer>();
            AutomaticRenderingRules.RemoveRenderers(root, context.GetState<BuildFilterState>(), Array.Empty<VirtualAnimatorController>(), new List<string>());
            Assert.That(particles == null && particleRenderer == null && line == null && trail == null, Is.True);
            Assert.That(obj != null && outside != null && renderer != null, Is.True);
        }

        [Test]
        public void AutomaticParticleRuleRetainsSimulationAndRendererReferencedByAnotherSystem()
        {
            var filter = Filter(); filter.ExcludedRendererTypes = RendererFilterTypes.ParticleSystems;
            var parent = PolicyChild("Preserved particles").AddComponent<ParticleSystem>();
            var child = PolicyChild("Referenced particles").AddComponent<ParticleSystem>();
            var subemitters = parent.subEmitters; subemitters.enabled = true;
            subemitters.AddSubEmitter(child, ParticleSystemSubEmitterType.Birth, ParticleSystemSubEmitterProperties.InheritNothing);
            filter.PreserveAutomaticRenderers.Add(parent.GetComponent<ParticleSystemRenderer>());
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            AutomaticRenderingRules.RemoveRenderers(root, context.GetState<BuildFilterState>(), Array.Empty<VirtualAnimatorController>(), new List<string>());
            Assert.That(parent != null && child != null && child.GetComponent<ParticleSystemRenderer>() != null, Is.True);
        }

        [Test]
        public void AutomaticRendererRuleRespectsAnimatedObjectReferences()
        {
            var filter = Filter(); filter.ExcludedRendererTypes = RendererFilterTypes.Lines;
            var line = PolicyChild("Referenced line").AddComponent<LineRenderer>();
            var clip = Clip();
            AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("Consumer", typeof(Animator), "m_Reference"),
                new[] { new ObjectReferenceKeyframe { value = line } });
            var controller = VirtualSlotController(clip);
            var context = new BuildContext(root, null); BuildFilterPass.RemoveEarly(context);
            AutomaticRenderingRules.RemoveRenderers(root, context.GetState<BuildFilterState>(), new[] { controller }, new List<string>());
            Assert.That(line != null, Is.True);
        }

        [Test]
        public void AutomaticRenderingRulesDefaultToNoLoss()
        {
            var filter = Filter();
            Assert.That(filter.RemoveAdditionalMaterialPasses, Is.False);
            Assert.That(filter.ExcludedRendererTypes, Is.EqualTo(RendererFilterTypes.None));
        }
    }
}
