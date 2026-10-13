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
        // The session vocabulary for DrivingSessionState.simulationBackend
        // ("arcade" | "physicalAssisted"); the physical path needs the
        // pursuit-feasibility brake below because its turn radius is real.
        private const string PhysicalBackendId = "physicalAssisted";

        // Lateral lane offset applied perpendicular to the local route
        // direction at the current target. The coordinator gives each
        // slot a different lane so four followers on the same route fan
        // out instead of tailgating through every contact — the offset
        // moves the aim point only; physics and rules are unchanged.
        private readonly float laneOffset;
        private bool initialized;
        private int routeIndex;
        private int stuckTicks;
        private int progressTicks;
        private float progressAnchorX;
        private float progressAnchorZ;
        // Escalating aim skip: repeated displacement wedges mean the
        // fixed lane aims into an obstruction; each streak moves the
        // pursuit one waypoint further ahead. Gate progress clears it.
        private int wedgeSkips;
        private int lastNextCheckpoint = -1;
        private bool wasRecovering;

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

            // Gate progress clears the wedge escalation — a fresh
            // section starts clean instead of carrying a stale skip.
            if (racer.nextCheckpoint != lastNextCheckpoint)
            {
                lastNextCheckpoint = racer.nextCheckpoint;
                wedgeSkips = 0;
            }
            var route = course.route;
            float tx, tz;
            if (route != null && route.Count > 0)
            {
                // First contact (or a respawn boundary) re-anchors onto
                // the nearest route point — a disconnect/rejoin takeover
                // or a recovery starts following from where the body
                // actually is instead of rewinding to index zero. The
                // wedge escalation survives the respawn boundary on
                // purpose: respawning erases position but not the fact
                // that the direct line trapped the body — the re-anchor
                // aims the accrued skip ahead of the nearest point, and
                // each respawn's edge accrues one skip itself so the
                // rules' quicker recoveries (stuck/beached/off-course
                // fire inside the watchdog window) still escalate the
                // line instead of repeating it.
                if (!initialized || motor.recovering)
                {
                    if (motor.recovering && !wasRecovering)
                        wedgeSkips = (wedgeSkips + 1) % route.Count;
                    routeIndex = (NearestIndex(motor, route) + wedgeSkips)
                        % route.Count;
                    initialized = true;
                }
                wasRecovering = motor.recovering;
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
            // Task 7: brake before tight turns — the strictest corner
            // inside the speed-scaled lookahead sets the target speed,
            // and the held brake ramps with the overshoot. This is the
            // ordinary brake control a human presses, not a privileged
            // slowdown: the backend's brake force decides what it does.
            var brake = CornerBrake(racer, course);
            // Physical pursuit feasibility: a physical racer's reachable
            // turn radius is bounded by real wheel-angle authority AND
            // the friction ellipse — it never shrinks to the arcade
            // kinematic's constant-rate bound. When the pursuit target
            // sits inside the reachable disc, steering alone can only
            // orbit it forever (the waypoint stays beyond both the reach
            // and passed-forgiveness windows); the human answer is
            // braking until the reachable radius closes on the target.
            var pursuitBrake = racer.simulationBackend == PhysicalBackendId
                ? PursuitBrake(racer, tx, tz)
                : 0f;
            if (pursuitBrake > brake) brake = pursuitBrake;
            // Drift is a normal input: real steering demand at speed, so
            // DriftEligible's ordinary gates (grounded, min speed,
            // threshold) decide whether it actually engages. While the
            // pursuit brake is active a drift would surrender exactly the
            // rear bite the shrinking radius needs, so it stays off.
            var drift = motor.grounded
                && pursuitBrake <= 0f
                && Math.Abs(steer) >= NetworkSettings.AiDriftSteerThreshold
                && motor.speed >= NetworkSettings.AiDriftMinSpeed;
            // Boost is held on straights with a charged meter — the same
            // hold-to-fire semantics a human uses; the motor's charge and
            // duration rules bound it. Boosting while braking or leaning
            // hard is suppressed the way a human would feather off.
            var boost = brake <= 0f
                && Math.Abs(steer) <= NetworkSettings.AiBoostMaxSteer
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
            // Displacement watchdog, windowed: a speedometer cannot see
            // a wall-ramming or ramp-stall loop — the motor reads real
            // speed into the obstruction while the net position never
            // moves, so the velocity streak above stays blind. Once per
            // streak window the check asks whether the grounded position
            // covered less than the stuck span since the last window —
            // a bouncing wedge cannot dodge it by escaping a fixed disc
            // because the anchor re-seats every window and net
            // displacement stays small either way. A legitimately moving
            // car always covers the span. A respawn boundary resets the
            // window instead of counting the teleport.
            if (motor.grounded && !motor.recovering)
            {
                if (progressTicks == 0)
                {
                    progressAnchorX = motor.x;
                    progressAnchorZ = motor.z;
                }
                progressTicks++;
                if (progressTicks >= NetworkSettings.AiProgressStuckTicks)
                {
                    progressTicks = 0;
                    if (Distance(motor.x - progressAnchorX,
                            motor.z - progressAnchorZ)
                            <= NetworkSettings.AiProgressStuckMeters)
                    {
                        recover = true;
                        // The firing window also escalates the pursuit
                        // further ahead: re-driving the exact aim that
                        // wedged it would re-wedge deterministically
                        // (lane offset never changes), while the
                        // checkpoint rules — not the route — decide what
                        // counts, so aiming past the obstruction stays
                        // honest. The skip persists across the recovery
                        // respawn through wedgeSkips, so each cycle aims
                        // measurably further along the loop instead of
                        // repeating the same line.
                        if (route != null && route.Count > 0)
                        {
                            wedgeSkips = (wedgeSkips + 1) % route.Count;
                            routeIndex = (routeIndex + 1 + wedgeSkips)
                                % route.Count;
                        }
                    }
                    progressAnchorX = motor.x;
                    progressAnchorZ = motor.z;
                }
            }
            else
            {
                progressTicks = 0;
                progressAnchorX = motor.x;
                progressAnchorZ = motor.z;
            }
            return new DriverInput(steer, drift, boost, false, recover,
                false, false, null, brake);
        }

        // The full-authority yaw rate of the racer's class (rad/s), from
        // the same normalized presets the race loads — the AI never reads
        // claimed upgrades. Unknown classes use the slowest known rate so
        // braking errs early rather than late.
        private static float ClassYawRate(string vehicleId)
        {
            var car = VehicleTuning.Car01().steeringRate;
            var moto = VehicleTuning.Motorcycle01().steeringRate;
            switch (VehicleTuning.ClassIdOf(vehicleId))
            {
                case "car": return car;
                case "motorcycle": return moto;
                default: return Math.Min(car, moto);
            }
        }

        // Scan the route from the current follow index: every vertex's
        // turn angle over its arc length bounds the followable speed at
        // steeringRate·margin·arc/θ, and the strictest corner inside the
        // lookahead window sets the target. Brake demand is the overshoot
        // ratio clamped to [0,1] — zero when the upcoming route is
        // followable at the current speed.
        private float CornerBrake(DrivingSessionState racer,
            CourseDefinition course)
        {
            var route = course.route;
            if (route == null || route.Count < 3) return 0f;
            var motor = racer.motor;
            var speed = motor.speed;
            if (!Finite(speed) || speed <= 0.1f) return 0f;
            var yawRate = ClassYawRate(racer.vehicleId);
            if (yawRate <= 0f) return 0f;
            var lookahead = speed * NetworkSettings.AiBrakeLookaheadSeconds
                + NetworkSettings.AiBrakeMinLookaheadMeters;

            var current = route[routeIndex];
            if (current == null) return 0f;
            var travelled = Distance(motor.x - current.x, motor.z - current.z);
            var target = float.MaxValue;
            var i = routeIndex;
            var visited = 0;
            while (travelled <= lookahead && visited < route.Count)
            {
                var prev = route[(i - 1 + route.Count) % route.Count];
                var cur = route[i];
                var next = route[(i + 1) % route.Count];
                visited++;
                if (cur != null && prev != null && next != null)
                {
                    var inX = cur.x - prev.x;
                    var inZ = cur.z - prev.z;
                    var outX = next.x - cur.x;
                    var outZ = next.z - cur.z;
                    var inLen = Distance(inX, inZ);
                    var outLen = Distance(outX, outZ);
                    if (inLen > 0.001f && outLen > 0.001f)
                    {
                        var turn = Math.Abs(WrapAngle(
                            (float)Math.Atan2(outX, outZ)
                            - (float)Math.Atan2(inX, inZ)));
                        if (turn > 0.01f)
                        {
                            var arc = (inLen + outLen) * 0.5f;
                            var cornerSpeed = yawRate
                                * NetworkSettings.AiCorneringMargin
                                * arc / turn;
                            if (cornerSpeed < target) target = cornerSpeed;
                        }
                    }
                    travelled += outLen;
                }
                i = (i + 1) % route.Count;
            }
            if (target == float.MaxValue || speed <= target) return 0f;
            return Clamp((speed - target) / NetworkSettings.AiBrakeOverspeedSpan,
                0f, 1f);
        }

        // Brake demand while the pursuit target sits inside the turn
        // radius reachable at the current speed — the physical backend's
        // orbit breaker. The demand is the size of the unreachable disc
        // beyond the target's distance, ramped over the same span
        // CornerBrake uses for overshoot: barely inside yields a touch,
        // deep inside yields full braking. Ordinary DriverInput.Brake —
        // the backend's brake force decides the deceleration.
        private float PursuitBrake(DrivingSessionState racer,
            float tx, float tz)
        {
            var phys = ClassPhysicsTuning(racer.vehicleId);
            if (phys == null) return 0f;
            var motor = racer.motor;
            var speed = motor.speed;
            if (!Finite(speed) || speed <= 0.1f) return 0f;
            var radius = PhysicalTurnRadius(phys, speed);
            if (float.IsInfinity(radius) || radius <= 0.001f) return 0f;
            // Exact Dubins unreachability: the target must sit inside
            // one of the two minimum-radius tangent circles — the discs
            // a constant-radius arc from this heading can never enter —
            // not merely within `radius` of the car. A target dead ahead
            // inside that disc IS reachable by driving straight, so the
            // old distance-only test braked on approach lines that were
            // never orbiting (observed live: the authored-ramp approach
            // slowed to ~8 m/s, the shortened jump dropped off the ramp
            // edge, and the same overturn cycle repeated).
            var fx = (float)Math.Sin(motor.heading);
            var fz = (float)Math.Cos(motor.heading);
            var lx = tx - (motor.x - fz * radius);
            var lz = tz - (motor.z + fx * radius);
            var rx = tx - (motor.x + fz * radius);
            var rz = tz - (motor.z - fx * radius);
            var insideSq = Math.Min(lx * lx + lz * lz,
                                    rx * rx + rz * rz);
            if (insideSq >= radius * radius) return 0f;
            var margin = radius - (float)Math.Sqrt(insideSq);
            return Clamp(margin
                / NetworkSettings.AiBrakeOverspeedSpan, 0f, 1f);
        }

        // Steady-state turn radius a physical vehicle can actually hold
        // at this speed: the larger of the wheel-angle (Ackermann) bound
        // and the friction-ellipse bound. The grip margin keeps the
        // ellipse budget honest — drive force and surface noise consume
        // part of mu*N, so demanding the full coefficient would promise
        // a radius the tires cannot deliver.
        private static float PhysicalTurnRadius(VehiclePhysicsTuning phys,
            float speed)
        {
            var authority = VehicleAssistRules.SteeringTarget(
                1f, speed, phys);
            var geometric = authority > 0.0001f
                ? Wheelbase(phys) / (float)Math.Tan(authority)
                : float.MaxValue;
            var friction = speed > 0.001f
                ? speed * speed / (phys.frictionCoefficient
                    * VehiclePhysicsTuning.StandardGravity
                    * NetworkSettings.AiCorneringMargin)
                : 0f;
            return Math.Max(geometric, friction);
        }

        // The physics preset the class convention resolves to — the same
        // authored presets the physical vehicles are generated from.
        // Unknown classes yield no pursuit model rather than a guessed
        // one.
        private static VehiclePhysicsTuning ClassPhysicsTuning(
            string vehicleId)
        {
            switch (VehicleTuning.ClassIdOf(vehicleId))
            {
                case "car": return VehiclePhysicsTuning.Car01();
                case "motorcycle": return VehiclePhysicsTuning.Motorcycle01();
                default: return null;
            }
        }

        // Axle span from the authored wheel mounts — the Ackermann
        // wheelbase the steering geometry turns about.
        private static float Wheelbase(VehiclePhysicsTuning phys)
        {
            var wheels = phys.wheels;
            if (wheels == null || wheels.Length == 0) return 1f;
            var min = float.MaxValue;
            var max = float.MinValue;
            foreach (var w in wheels)
            {
                if (w == null) continue;
                if (w.mountZ < min) min = w.mountZ;
                if (w.mountZ > max) max = w.mountZ;
            }
            var span = max - min;
            return span > 0.01f ? span : 1f;
        }

        private static float Distance(float x, float z)
        {
            return (float)Math.Sqrt((double)x * x + (double)z * z);
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
