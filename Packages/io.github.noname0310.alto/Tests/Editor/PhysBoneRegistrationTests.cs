using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using nadena.dev.ndmf;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.ObjectOptimization;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        [TestCase(false, false)] [TestCase(true, false)] [TestCase(false, true)]
        public void PhysBoneRegistrationDistinguishesHoistingFromDuplicateRuntimeRegistration(bool hoist, bool duplicateRegistration)
        {
            Assert.That(PhysBoneManager.Inst == null, Is.True, "Use an isolated Edit Mode scene without a running dynamics manager.");
            var first = PolicyChild("First collider").AddComponent<VRCPhysBoneCollider>();
            var second = PolicyChild("Second collider").AddComponent<VRCPhysBoneCollider>();
            var bone = PolicyChild("Simulation").AddComponent<VRCPhysBone>();
            // Duplicate references in a simulation are different from registering
            // the collider component twice in the SDK execution queue.
            bone.colliders.Add(first); bone.colliders.Add(first); bone.colliders.Add(second);
            if (hoist)
            {
                LogAssert.Expect(LogType.Log, "Starting processing for avatar: " + root.name);
                var context = new BuildContext(root, null);
                var settings = context.GetState<ObjectOptimizationState>();
                settings.Scope = ObjectSettings().Scope;
                settings.HoistComponents = true; settings.ComponentHoisting = new ComponentHoistingOptions { PhysBones = false };
                var index = FrameIndex();
                Assert.That(ComponentHoisting.Apply(context, index, AvatarDependencyGraph.Create(root, index, settings, false),
                    new StaticTransformProof(root, index, settings), t => UnityEditor.AnimationUtility.CalculateTransformPath(t, root.transform)), Is.EqualTo(2));
                Assert.That(root.GetComponents<VRCPhysBoneCollider>().Length, Is.EqualTo(2));
                Assert.That(bone.colliders[0], Is.SameAs(bone.colliders[1]));
            }
            var colliders = root.GetComponentsInChildren<VRCPhysBoneCollider>(true);
            var owner = Track(new GameObject("Isolated dynamics registration"));
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(owner, scene);
            var manager = owner.AddComponent<PhysBoneManager>();
            var add = typeof(PhysBoneManager).GetMethod("AddColliders", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(add, Is.Not.Null);
            try
            {
                PhysBoneManager.Inst = manager; manager.Init();
                foreach (var collider in colliders) manager.AddCollider(collider);
                if (duplicateRegistration)
                {
                    manager.AddCollider(colliders[0]);
                    LogAssert.Expect(LogType.Error, new Regex("Shape with id [0-9]+ is already in the execution group at index [0-9]+\\. It will not be added again\\."));
                }
                add.Invoke(manager, null);
                Assert.That(colliders.All(c => c.shape != null && c.shape.id != CollisionScene.Shape.NullId), Is.True);
                Assert.That(colliders.Select(c => c.shape.id).Distinct().Count(), Is.EqualTo(colliders.Length));
                LogAssert.NoUnexpectedReceived();
            }
            finally { manager.Dispose(); PhysBoneManager.Inst = null; }
        }
    }
}
