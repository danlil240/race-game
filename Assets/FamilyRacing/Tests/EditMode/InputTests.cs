using System;
using FamilyRacing.Core.Driving;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    public sealed class InputTests
    {
        private static DriverInput Input(float steering = 0f, bool drift = false, bool boost = false,
            bool gadget = false, bool recover = false, bool changeCamera = false, bool pause = false)
        {
            return new DriverInput(steering, drift, boost, gadget, recover, changeCamera, pause);
        }

        [TestCase(0f, 0f)]
        [TestCase(0.5f, 0.5f)]
        [TestCase(-0.5f, -0.5f)]
        [TestCase(1f, 1f)]
        [TestCase(-1f, -1f)]
        [TestCase(2f, 1f)]
        [TestCase(-2f, -1f)]
        [TestCase(1000f, 1f)]
        [TestCase(-1000f, -1f)]
        [TestCase(float.NaN, 0f)]
        [TestCase(float.PositiveInfinity, 0f)]
        [TestCase(float.NegativeInfinity, 0f)]
        public void ConstructorNormalizesSteering(float steering, float expected)
        {
            Assert.That(Input(steering).Steering, Is.EqualTo(expected));
        }

        [Test]
        public void ConstructorStoresCommandsInDeclaredOrder()
        {
            var input = new DriverInput(0.25f, true, false, true, false, true, false);
            Assert.That(input.Steering, Is.EqualTo(0.25f));
            Assert.That(input.Drift, Is.True);
            Assert.That(input.Boost, Is.False);
            Assert.That(input.Gadget, Is.True);
            Assert.That(input.Recover, Is.False);
            Assert.That(input.ChangeCamera, Is.True);
            Assert.That(input.Pause, Is.False);
        }

        [Test]
        public void DefaultInputIsNeutral()
        {
            var input = new DriverInput();
            Assert.That(input.Steering, Is.Zero);
            Assert.That(input.Drift, Is.False);
            Assert.That(input.Boost, Is.False);
            Assert.That(input.Gadget, Is.False);
            Assert.That(input.Recover, Is.False);
            Assert.That(input.ChangeCamera, Is.False);
            Assert.That(input.Pause, Is.False);
        }

        [TestCase(false, false, 0f)]
        [TestCase(true, false, -1f)]
        [TestCase(false, true, 1f)]
        [TestCase(true, true, 0f)]
        public void FromButtonsResolvesHeldSteering(bool left, bool right, float expected)
        {
            Assert.That(DriverInputComposer.FromButtons(left, right, false, false, false).Steering,
                Is.EqualTo(expected));
        }

        [Test]
        public void FromButtonsMapsCommandsAndLeavesTheRestNeutral()
        {
            var input = DriverInputComposer.FromButtons(false, false, true, true, true);
            Assert.That(input.Steering, Is.Zero);
            Assert.That(input.Drift, Is.True);
            Assert.That(input.Boost, Is.True);
            Assert.That(input.Gadget, Is.True);
            Assert.That(input.Recover, Is.False);
            Assert.That(input.ChangeCamera, Is.False);
            Assert.That(input.Pause, Is.False);
        }

        [TestCase(0.8f, 0.5f, 1f)]
        [TestCase(-0.9f, -0.5f, -1f)]
        [TestCase(0.4f, -0.4f, 0f)]
        [TestCase(-0.3f, 0.3f, 0f)]
        [TestCase(0f, 0f, 0f)]
        public void ComposeSumsSteeringWithinLimits(float keyboard, float touch, float expected)
        {
            var merged = DriverInputComposer.Compose(Input(keyboard), Input(touch));
            Assert.That(merged.Steering, Is.EqualTo(expected).Within(0.0001f));
        }

        [Test]
        public void ComposeCombinesEveryCommandWithOr()
        {
            var keyboard = Input(0f, drift: true, gadget: true, changeCamera: true);
            var touch = Input(0f, boost: true, recover: true, pause: true);
            var merged = DriverInputComposer.Compose(keyboard, touch);
            Assert.That(merged.Drift, Is.True);
            Assert.That(merged.Boost, Is.True);
            Assert.That(merged.Gadget, Is.True);
            Assert.That(merged.Recover, Is.True);
            Assert.That(merged.ChangeCamera, Is.True);
            Assert.That(merged.Pause, Is.True);
        }

        // --- throttle override and brake semantics -----------------------------

        [Test]
        public void DefaultInputUsesAutoThrottle_ExplicitZeroCoasts()
        {
            // Missing throttle means automatic throttle — a zero-initialized
            // struct included — while an explicit override of 0 is a coast.
            Assert.That(default(DriverInput).Throttle, Is.EqualTo(1f));
            Assert.That(default(DriverInput).HasThrottleOverride, Is.False);
            Assert.That(default(DriverInput).Brake, Is.EqualTo(0f));
            var explicitCoast = new DriverInput(0f, false, false, false,
                false, false, false, 0f, 0f);
            Assert.That(explicitCoast.Throttle, Is.EqualTo(0f));
            Assert.That(explicitCoast.HasThrottleOverride, Is.True);
            // The pre-brake constructor keeps automatic-throttle semantics.
            var legacy = Input();
            Assert.That(legacy.HasThrottleOverride, Is.False);
            Assert.That(legacy.Throttle, Is.EqualTo(1f));
            Assert.That(legacy.Brake, Is.EqualTo(0f));
        }

        [Test]
        public void InputCompositionKeepsBrakeAndOverride()
        {
            var keyboard = new DriverInput(0f, false, false, false,
                false, false, false, 0.5f, 0.8f);
            var touch = Input();
            var composed = DriverInputComposer.Compose(keyboard, touch);
            // Brake combines by max; an absent source never cancels an
            // explicit override.
            Assert.That(composed.Brake, Is.EqualTo(0.8f));
            Assert.That(composed.HasThrottleOverride, Is.True);
            Assert.That(composed.Throttle, Is.EqualTo(0.5f));
        }

        [Test]
        public void InputCompositionUsesLowerOverrideWhenBothSourcesOverride()
        {
            var keyboard = new DriverInput(0f, false, false, false,
                false, false, false, 0.3f, 0f);
            var touch = new DriverInput(0f, false, false, false,
                false, false, false, 0.7f, 0.4f);
            var composed = DriverInputComposer.Compose(keyboard, touch);
            Assert.That(composed.Throttle, Is.EqualTo(0.3f));
            Assert.That(composed.Brake, Is.EqualTo(0.4f));
        }

        [Test]
        public void ThrottleAndBrakeArgumentsAreBoundedDefensively()
        {
            var clamped = new DriverInput(0f, false, false, false,
                false, false, false, 4f, -2f);
            Assert.That(clamped.Throttle, Is.EqualTo(1f));
            Assert.That(clamped.HasThrottleOverride, Is.True);
            Assert.That(clamped.Brake, Is.EqualTo(0f));
            // A non-finite override or brake is defensive-neutralized:
            // automatic throttle and no brake.
            var nonFinite = new DriverInput(0f, false, false, false,
                false, false, false, float.NaN, float.PositiveInfinity);
            Assert.That(nonFinite.HasThrottleOverride, Is.False);
            Assert.That(nonFinite.Throttle, Is.EqualTo(1f));
            Assert.That(nonFinite.Brake, Is.EqualTo(0f));
        }

        [Test]
        public void ComposeOfNeutralInputsStaysNeutral()
        {
            var merged = DriverInputComposer.Compose(new DriverInput(), new DriverInput());
            Assert.That(merged.Steering, Is.Zero);
            Assert.That(merged.Drift, Is.False);
            Assert.That(merged.Boost, Is.False);
            Assert.That(merged.Gadget, Is.False);
            Assert.That(merged.Recover, Is.False);
            Assert.That(merged.ChangeCamera, Is.False);
            Assert.That(merged.Pause, Is.False);
        }
    }
}
