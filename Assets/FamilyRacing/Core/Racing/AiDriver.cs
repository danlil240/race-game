using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;

namespace FamilyRacing.Core.Racing
{
    // Honest fill-in driver for vacant or disconnected race slots: a
    // waypoint follower over the course's authored `route` loop. It emits
    // only the ordinary bounded DriverInput a human produces — steering
    // toward the next waypoint, drift through real steering demand,
    // boost on straights with a charged meter, and the same recover
    // one-shot humans press when stalled. There is no catch-up
    // multiplier, no teleport, and no privileged state: the sample reads
    // exactly the DrivingSessionState snapshot every participant sees,
    // and the session's motor, checkpoint and recovery rules apply
    // unchanged.
    //
    // One instance serves one racer; the coordinator owns them. All
    // decisions derive from the presented state plus deterministic
    // bookkeeping (current route index, stalled streak), so identical
    // input histories always produce identical samples.
    public sealed class AiDriver
    {
        private const float TwoPi = (float)(Math.PI * 2.0);

        // Lateral lane offset applied perpendicular to the local route
        // direction at the current target. The coordinator gives each
        // slot a different lane so four followers on the same route fan
        // out instead of tailgating through every contact — the offset
        // moves the aim point only; physics and rules are unchanged.
        private readonly float laneOffset;
        private bool initialized;
        private int routeIndex;
        private int stuckTicks;

        public AiDriver()
            : this(0f)
        {
        }

        public AiDriver(float lateralOffsetMeters)
        {
            laneOffset = float.IsNaN(lateralOffsetMeters)
                || float.IsInfinity(lateralOffsetMeters)
                ? 0f
                : lateralOffsetMeters;
        }

        public DriverInput Sample(DrivingSessionState racer, CourseDefinition course)
        {
            if (racer == null || course == null || racer.motor == null)
                return default;
            if (racer.phase == DrivingSession.PhaseFinished)
                return default;
            var motor = racer.motor;
            if (!Finite(motor.x) || !Finite(motor.y) || !Finite(motor.z)
                || !Finite(motor.heading))
                return default;

            var route = course.route;
            float tx, tz;
            if (route != null && route.Count > 0)
            {
                // First contact (or a respawn boundary) re-anchors onto
                // the nearest route point — a disconnect/rejoin takeover
                // or a recovery starts following from where the body
                // actually is instead of rewinding to index zero.
                if (!initialized || motor.recovering)
                {
                    routeIndex = NearestIndex(motor, route);
                    initialized = true;
                }
                AdvanceIndex(motor, route);
                var wp = route[routeIndex];
                var prev = route[(routeIndex - 1 + route.Count) % route.Count];
                if (wp == null) return default;
                var sx = prev != null ? wp.x - prev.x : 0f;
                var sz = prev != null ? wp.z - prev.z : 0f;
                var sl = (float)Math.Sqrt(sx * sx + sz * sz);
                if (sl > 0.001f && laneOffset != 0f)
                {
                    // Perpendicular lane aim: spread followers across the
                    // line the way humans pick lanes.
                    tx = wp.x - sz / sl * laneOffset;
                    tz = wp.z + sx / sl * laneOffset;
                }
                else
                {
                    tx = wp.x;
                    tz = wp.z;
                }
            }
            else
            {
                // Routes are authored content; without one the honest
                // fallback is the next expected gate's centre. The
                // follower cannot skip a gate by aiming anywhere else —
                // the ordered checkpoint rules decide what counts.
                var gates = course.checkpoints;
                var index = racer.nextCheckpoint;
                if (gates == null || index < 0 || index >= gates.Count
                    || gates[index] == null)
                    return default;
                tx = gates[index].x;
                tz = gates[index].z;
            }

            var dx = tx - motor.x;
            var dz = tz - motor.z;
            var desired = (float)Math.Atan2(dx, dz);
            var steer = Clamp(WrapAngle(desired - motor.heading)
                * NetworkSettings.AiSteeringGain, -1f, 1f);
            // Drift is a normal input: real steering demand at speed, so
            // DriftEligible's ordinary gates (grounded, min speed,
            // threshold) decide whether it actually engages.
            var drift = motor.grounded
                && Math.Abs(steer) >= NetworkSettings.AiDriftSteerThreshold
                && motor.speed >= NetworkSettings.AiDriftMinSpeed;
            // Boost is held on straights with a charged meter — the same
            // hold-to-fire semantics a human uses; the motor's charge and
            // duration rules bound it.
            var boost = Math.Abs(steer) <= NetworkSettings.AiBoostMaxSteer
                && motor.boostCharge >= NetworkSettings.AiBoostMinCharge;

            // A grounded near-stopped streak requests the ordinary
            // recover one-shot (queued and consumed like any command).
            // The respawn boundary itself — flagged recovering — never
            // counts toward the streak.
            var recover = false;
            if (motor.grounded && !motor.recovering
                && Math.Abs(motor.speed) < NetworkSettings.AiRecoverStuckSpeed)
            {
                stuckTicks++;
                recover = stuckTicks >= NetworkSettings.AiRecoverStuckTicks;
            }
            else
            {
                stuckTicks = 0;
            }
            return new DriverInput(steer, drift, boost, false, recover, false, false);
        }

        // Nearest route point by ground-plane distance. Deterministic:
        // first strict minimum wins, so equal distances resolve by index.
        private static int NearestIndex(MotorState motor, List<RouteWaypoint> route)
        {
            var best = 0;
            var bestD2 = float.MaxValue;
            for (var i = 0; i < route.Count; i++)
            {
                var wp = route[i];
                if (wp == null) continue;
                var dx = wp.x - motor.x;
                var dz = wp.z - motor.z;
                var d2 = dx * dx + dz * dz;
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    best = i;
                }
            }
            return best;
        }

        // Move the follow index forward past reached waypoints and past
        // waypoints already behind the travel direction (bounded by the
        // passed factor so a far-away overshoot still turns back to it).
        private void AdvanceIndex(MotorState motor, List<RouteWaypoint> route)
        {
            var fx = (float)Math.Sin(motor.heading);
            var fz = (float)Math.Cos(motor.heading);
            for (var guard = 0; guard < route.Count; guard++)
            {
                var wp = route[routeIndex];
                if (wp == null) break;
                var dx = wp.x - motor.x;
                var dz = wp.z - motor.z;
                var d2 = dx * dx + dz * dz;
                var reach = wp.radius > 0f
                    ? wp.radius
                    : NetworkSettings.AiWaypointReachMeters;
                if (d2 < reach * reach)
                {
                    routeIndex = (routeIndex + 1) % route.Count;
                    continue;
                }
                // Overshoot forgiveness: the waypoint sits behind the
                // nose within a modest window — orbiting it would loop
                // the car, so take the next one instead. A far overshoot
                // still turns around honestly.
                var passed = reach * NetworkSettings.AiWaypointPassedFactor;
                if (dx * fx + dz * fz < 0f && d2 < passed * passed)
                {
                    routeIndex = (routeIndex + 1) % route.Count;
                    continue;
                }
                break;
            }
        }

        private static float WrapAngle(float radians)
        {
            if (float.IsNaN(radians) || float.IsInfinity(radians)) return 0f;
            while (radians > Math.PI) radians -= TwoPi;
            while (radians <= -Math.PI) radians += TwoPi;
            return radians;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return value < min ? min : (value > max ? max : value);
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
