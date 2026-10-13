using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;

namespace FamilyRacing.Networking
{
    // The client-side prediction world for the physical backend
    // (Task 10): exactly one local rigidbody vehicle inside its own
    // collision-only PhysicsCourseWorld, stepped through the identical
    // force model and the same two-fixed-substep logical interval as
    // the authoritative host's PhysicsVehicleSimulation — this type
    // composes that implementation rather than duplicating it.
    //
    // The single-roster bound is structural: Initialize refuses more
    // than one body, so remote display vehicles can never exist here
    // as dynamic colliders. Racer-versus-racer contact prediction is
    // deliberately absent — remote contacts are authoritative host
    // facts that arrive as corrections after separation.
    public sealed class PhysicsPredictionSimulation : IVehicleSimulation
    {
        private readonly PhysicsVehicleSimulation inner;
        private bool disposed;

        public PhysicsPredictionSimulation(PhysicsCourseWorld courseWorld)
        {
            inner = new PhysicsVehicleSimulation(courseWorld);
        }

        // The collision-only world this prediction runs inside —
        // diagnostic seam only.
        public PhysicsCourseWorld World { get { return inner.World; } }

        // The single local body plus static course collision — never
        // a second authoritative roster. Re-initialization replaces the
        // roster atomically (race-adoption boundary); a roster of two
        // or more is a contract violation and throws before any body
        // exists, so remote vehicles cannot become dynamic colliders
        // by accident.
        public void Initialize(IReadOnlyList<VehicleSpawn> vehicles)
        {
            if (disposed) throw new ObjectDisposedException(
                nameof(PhysicsPredictionSimulation));
            if (vehicles != null && vehicles.Count > 1)
                throw new ArgumentException(
                    "PhysicsPredictionSimulation simulates exactly one "
                    + "local racer; remote racers are display-only and "
                    + "have no body in the prediction world (roster "
                    + "size " + vehicles.Count + ")");
            inner.Initialize(vehicles);
        }

        public IReadOnlyList<VehicleTickResult> Step(
            IReadOnlyList<VehicleTickRequest> requests)
        {
            return inner.Step(requests);
        }

        public void Restore(string participantId, MotorState state)
        {
            inner.Restore(participantId, state);
        }

        public MotorState Capture(string participantId)
        {
            return inner.Capture(participantId);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            inner.Dispose();
        }
    }
}
