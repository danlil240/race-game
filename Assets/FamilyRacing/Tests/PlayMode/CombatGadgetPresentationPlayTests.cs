using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;

namespace FamilyRacing.Tests
{
    public sealed class CombatGadgetPresentationPlayTests
    {
        [TestCase("turbo")]
        [TestCase("bubble-shield")]
        [TestCase("foam-trap")]
        [TestCase("homing-missile")]
        [TestCase("mine")]
        [TestCase("emp-pulse")]
        public void EveryGadgetHasAReadableRuntimeIcon(string kind)
        {
            var texture = Resources.Load<Texture2D>("GadgetIcons/" + kind);
            Assert.IsNotNull(texture, kind);
            Assert.GreaterOrEqual(texture.width, 128);
            Assert.GreaterOrEqual(texture.height, 128);
        }

        [Test]
        public void WeaponsMirrorStateAndRetireOnExitAndRematch()
        {
            var host = new GameObject("combat-presenter-test");
            try
            {
                var presenter = host.AddComponent<GadgetPresenter>();
                var world = new GadgetWorldState();
                world.projectiles.Add(new GadgetProjectileState { projectileId = "m1", x = 5, z = 7, ticksRemaining = 60 });
                world.patches.Add(new GadgetPatchState { patchId = "mine1", kind = "mine", radiusMeters = 2.5f, ticksRemaining = 90, armingTicksRemaining = 5 });
                world.pulses.Add(new GadgetPulseState { pulseId = "e1", radiusMeters = 12, ticksRemaining = 10, totalTicks = 15 });
                var state = new FoundationState { driving = new DrivingSessionState { raceId = "r1", gadgets = world } };
                presenter.ApplyFrame(state, .03f);
                Assert.IsTrue(host.transform.Find("GadgetWorld/gadget-missile-m1").gameObject.activeSelf);
                Assert.IsTrue(host.transform.Find("GadgetWorld/gadget-mine-mine1").gameObject.activeSelf);
                Assert.IsTrue(host.transform.Find("GadgetWorld/gadget-emp-e1").gameObject.activeSelf);
                foreach (var collider in host.GetComponentsInChildren<Collider>())
                    Assert.IsFalse(collider.enabled, "weapon visuals never own gameplay collision");
                state.driving.raceId = "r2";
                state.driving.gadgets = new GadgetWorldState();
                presenter.ApplyFrame(state, .03f);
                Assert.AreEqual(0, ActiveChildren(host.transform.Find("GadgetWorld")));
                state.driving.gadgets = world;
                presenter.ApplyFrame(state, .03f);
                presenter.ApplyFrame(new FoundationState(), .03f);
                Assert.AreEqual(0, ActiveChildren(host.transform.Find("GadgetWorld")));
            }
            finally { Object.DestroyImmediate(host); }
        }

        private static int ActiveChildren(Transform root)
        {
            var count = 0;
            foreach (Transform child in root) if (child.gameObject.activeSelf) count++;
            return count;
        }
    }
}
