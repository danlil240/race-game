namespace FamilyRacing.Core.Driving
{
    // Per-tick simulation modifiers the authoritative rule layer hands to
    // a backend beside the driver input: the gadget system's turbo and
    // foam effects. The seam is deliberately tiny — an acceleration
    // multiplier and a speed-cap multiplier — so every backend maps it
    // to its own force model (arcade moves the step's cap/acceleration;
    // the physical wheel solver scales its tapered cruise target and
    // peak drive force). Neutral values mean "no effect": an absent
    // modifier object is identical to multipliers of one.
    //
    // Rule-side only: nothing here is authored content — the authored
    // numbers live on GadgetDefinition and are folded into the racer's
    // live modifier row by GadgetRules before the batch runs.
    public sealed class MotorModifiers
    {
        public float accelerationMultiplier = 1f;
        public float speedCapMultiplier = 1f;

        public static MotorModifiers Neutral()
        {
            return new MotorModifiers();
        }

        public bool IsNeutral()
        {
            return accelerationMultiplier == 1f
                && speedCapMultiplier == 1f;
        }

        public MotorModifiers Copy()
        {
            return new MotorModifiers
            {
                accelerationMultiplier = accelerationMultiplier,
                speedCapMultiplier = speedCapMultiplier
            };
        }
    }
}
