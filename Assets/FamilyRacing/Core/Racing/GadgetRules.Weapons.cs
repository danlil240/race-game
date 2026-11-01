using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Racing
{
    public static partial class GadgetRules
    {
        public const string EventMissileLaunched = "gadget.missileLaunched";
        public const string EventMissileHit = "gadget.missileHit";
        public const string EventMineDropped = "gadget.mineDropped";
        public const string EventMineHit = "gadget.mineHit";
        public const string EventEmpPulse = "gadget.empPulse";
        public const string EventEmpHit = "gadget.empHit";

        private static bool Eligible(GadgetRacerInput racer, string owner)
        {
            return racer != null && racer.participantId != owner
                && !racer.finished && !racer.recovering;
        }

        // Missiles lock onto the nearest rival that is not behind the
        // shooter (leaders far ahead included, up to the authored range),
        // fly fast with lead pursuit, follow the road height and keep
        // hunting: a lost lock reacquires the nearest rival ahead of the
        // missile instead of flying off forever.
        private const float MissileVerticalLockMeters = 8f;
        private const float MissileClimbMetersPerSecond = 20f;
        private const float MissileTerminalMeters = 12f;

        private static string AcquireTarget(float x, float y, float z, float heading,
            float range, float minForwardDot, string owner, List<GadgetRacerInput> racers)
        {
            var fx = (float)Math.Sin(heading);
            var fz = (float)Math.Cos(heading);
            var best = range * range;
            var target = "";
            foreach (var rival in racers)
            {
                if (!Eligible(rival, owner) || Math.Abs(rival.y - y) > MissileVerticalLockMeters) continue;
                var dx = rival.x - x; var dz = rival.z - z;
                var d2 = dx * dx + dz * dz;
                if (d2 < 1e-6f || d2 >= best) continue;
                if (dx * fx + dz * fz < minForwardDot * (float)Math.Sqrt(d2)) continue;
                best = d2; target = rival.participantId;
            }
            return target;
        }

        private static void LaunchMissile(GadgetWorldState state, GadgetRacerInput owner,
            List<GadgetRacerInput> racers, GadgetDefinition def, List<DrivingEvent> events, ref int seq)
        {
            var fx = (float)Math.Sin(owner.heading);
            var fz = (float)Math.Cos(owner.heading);
            // Anything in the front half-plane, nearest first.
            var target = AcquireTarget(owner.x, owner.y, owner.z, owner.heading,
                def.targetingRangeMeters, -0.1f, owner.participantId, racers);
            while (state.projectiles.Count >= Networking.NetworkSettings.GadgetProjectileCapacity)
                state.projectiles.RemoveAt(0);
            var projectile = new GadgetProjectileState
            {
                projectileId = "missile-" + (++state.nextPatchSerial),
                ownerParticipantId = owner.participantId, targetParticipantId = target,
                x = owner.x + fx * 3f, y = owner.y, z = owner.z + fz * 3f,
                heading = owner.heading, speed = def.projectileSpeedMetersPerSecond,
                turnRate = def.turnRadiansPerSecond, radiusMeters = def.patchRadiusMeters,
                ticksRemaining = def.patchLifetimeTicks, slowMultiplier = def.slowMultiplier,
                slowTicks = def.slowDurationTicks, immunityTicks = def.immunityTicks
            };
            state.projectiles.Add(projectile);
            events.Add(Emit(state, EventMissileLaunched, owner.participantId, projectile.projectileId, ref seq));
        }

        private static void FireEmp(GadgetWorldState state, GadgetRacerInput owner,
            List<GadgetRacerInput> racers, GadgetDefinition def, List<DrivingEvent> events, ref int seq)
        {
            while (state.pulses.Count >= Networking.NetworkSettings.GadgetPulseCapacity) state.pulses.RemoveAt(0);
            var pulse = new GadgetPulseState
            {
                pulseId = "emp-" + (++state.nextPatchSerial), ownerParticipantId = owner.participantId,
                x = owner.x, y = owner.y, z = owner.z, radiusMeters = def.patchRadiusMeters,
                ticksRemaining = 15, totalTicks = 15
            };
            state.pulses.Add(pulse);
            events.Add(Emit(state, EventEmpPulse, owner.participantId, pulse.pulseId, ref seq));
            foreach (var rival in racers)
            {
                if (!Eligible(rival, owner.participantId) || Math.Abs(rival.y - owner.y) > PatchVerticalRangeMeters) continue;
                var dx = rival.x - owner.x; var dz = rival.z - owner.z;
                if (dx * dx + dz * dz > def.patchRadiusMeters * def.patchRadiusMeters) continue;
                ApplyWeaponHit(state, rival.participantId, pulse.pulseId, EventEmpHit,
                    def.slowMultiplier, def.slowDurationTicks, def.immunityTicks, events, ref seq,
                    0, 0f, 1f);
            }
        }

        private static void ApplyWeaponHit(GadgetWorldState state, string victim, string effectId,
            string eventKind, float multiplier, int slowTicks, int immunityTicks,
            List<DrivingEvent> events, ref int seq, int spinTicks, float spinTurns, float spinSign)
        {
            var row = state.Racer(victim);
            if (row == null || row.foamImmunityTicksRemaining > 0) return;
            if (row.shieldCharges > 0)
            {
                row.shieldCharges--;
                events.Add(Emit(state, EventShieldBlocked, victim, effectId, ref seq));
                return;
            }
            row.foamSlowTicksRemaining = slowTicks;
            row.foamSlowMultiplier = multiplier;
            row.foamImmunityTicksRemaining = Math.Max(slowTicks, spinTicks) + immunityTicks;
            if (spinTicks > 0)
            {
                StartSpin(row, spinTicks, spinTurns, spinSign);
                row.turboTicksRemaining = 0;
            }
            events.Add(Emit(state, eventKind, victim, effectId, ref seq));
        }

        private static void MoveProjectiles(GadgetWorldState state, List<GadgetRacerInput> racers,
            List<DrivingEvent> events, ref int seq)
        {
            const float dt = 1f / 30f;
            for (var i = state.projectiles.Count - 1; i >= 0; i--)
            {
                var p = state.projectiles[i];
                GadgetRacerInput target = null;
                foreach (var rival in racers)
                    if (rival != null && rival.participantId == p.targetParticipantId) { target = rival; break; }
                if (target == null || !Eligible(target, p.ownerParticipantId)
                    || Math.Abs(target.y - p.y) > MissileVerticalLockMeters)
                {
                    // Lost lock (or launched blind): hunt the nearest rival
                    // in front of the missile's nose.
                    p.targetParticipantId = AcquireTarget(p.x, p.y, p.z, p.heading,
                        p.speed * 2.5f, 0.2f, p.ownerParticipantId, racers);
                    target = null;
                    foreach (var rival in racers)
                        if (rival != null && rival.participantId == p.targetParticipantId) { target = rival; break; }
                }
                if (target != null)
                {
                    // Lead pursuit: aim where the rival will be when the
                    // missile arrives, using its own last-boundary motion.
                    float vx = 0f, vz = 0f;
                    var segs = target.segments;
                    if (segs != null && segs.Count > 0)
                    {
                        vx = (segs[segs.Count - 1].bx - segs[0].ax) / dt;
                        vz = (segs[segs.Count - 1].bz - segs[0].az) / dt;
                    }
                    var dx = target.x - p.x; var dz = target.z - p.z;
                    var dist = (float)Math.Sqrt(dx * dx + dz * dz);
                    var lead = Math.Min(0.8f, dist / Math.Max(1f, p.speed));
                    var aimX = target.x + vx * lead; var aimZ = target.z + vz * lead;
                    var desired = (float)Math.Atan2(aimX - p.x, aimZ - p.z);
                    var delta = (float)Math.Atan2(Math.Sin(desired - p.heading), Math.Cos(desired - p.heading));
                    // Terminal guidance: inside a dozen metres the missile
                    // snaps onto the intercept line.
                    var turn = dist < MissileTerminalMeters ? (float)Math.PI : p.turnRate * dt;
                    p.heading += Math.Max(-turn, Math.Min(turn, delta));
                    p.heading = (float)Math.Atan2(Math.Sin(p.heading), Math.Cos(p.heading));
                    var climb = MissileClimbMetersPerSecond * dt;
                    p.y += Math.Max(-climb, Math.Min(climb, target.y - p.y));
                }
                var ax = p.x; var az = p.z;
                p.x += (float)Math.Sin(p.heading) * p.speed * dt;
                p.z += (float)Math.Cos(p.heading) * p.speed * dt;
                GadgetRacerInput hit = null;
                var first = float.MaxValue;
                foreach (var rival in racers)
                {
                    if (!Eligible(rival, p.ownerParticipantId)) continue;
                    var time = SweptContact(ax, p.y, az, p.x, p.z, p.radiusMeters, rival);
                    if (time < first) { hit = rival; first = time; }
                }
                if (hit == null) continue;
                ApplyWeaponHit(state, hit.participantId, p.projectileId, EventMissileHit,
                    p.slowMultiplier, p.slowTicks, p.immunityTicks, events, ref seq,
                    MissileSpinTicks, MissileSpinTurns, SpinSign(hit, ax, az));
                state.projectiles.RemoveAt(i);
            }
        }

        private static void ResolveMines(GadgetWorldState state, List<GadgetRacerInput> racers,
            List<DrivingEvent> events, ref int seq)
        {
            for (var i = 0; i < state.patches.Count; i++)
            {
                var mine = state.patches[i];
                if (mine.kind != GadgetKind.Mine || mine.armingTicksRemaining > 0) continue;
                GadgetRacerInput hit = null;
                var first = float.MaxValue;
                foreach (var rival in racers)
                {
                    if (!Eligible(rival, mine.ownerParticipantId)) continue;
                    var time = SweptContact(mine.x, mine.y, mine.z, mine.x, mine.z, mine.radiusMeters, rival);
                    if (time < first) { hit = rival; first = time; }
                }
                if (hit == null) continue;
                ApplyWeaponHit(state, hit.participantId, mine.patchId, EventMineHit,
                    mine.slowMultiplier, mine.slowTicks, mine.immunityTicks, events, ref seq,
                    MineSpinTicks, MineSpinTurns, SpinSign(hit, mine.x, mine.z));
                state.patches.RemoveAt(i--);
            }
        }

        // Relative-motion quadratic per real racer substep. Intersections
        // must happen at the same time, rather than anywhere along two paths.
        private static float SweptContact(float ax, float y, float az, float bx, float bz,
            float radiusMeters, GadgetRacerInput rival)
        {
            var segments = rival.segments;
            var count = segments != null ? segments.Count : 0;
            if (count == 0) return float.MaxValue;
            var earliest = float.MaxValue;
            for (var j = 0; j < count; j++)
            {
                var s = segments[j];
                var t0 = (float)j / count; var span = 1f / count;
                var rx = ax + (bx - ax) * t0 - s.ax;
                var rz = az + (bz - az) * t0 - s.az;
                var vx = (bx - ax) * span - (s.bx - s.ax);
                var vz = (bz - az) * span - (s.bz - s.az);
                var a = vx * vx + vz * vz;
                var b = 2f * (rx * vx + rz * vz);
                var c = rx * rx + rz * rz - radiusMeters * radiusMeters;
                var discriminant = b * b - 4f * a * c;
                var enter = 0f; var exit = 1f;
                if (a <= 1e-8f)
                {
                    if (c > 0f) continue;
                }
                else
                {
                    if (discriminant < 0f) continue;
                    var root = (float)Math.Sqrt(discriminant);
                    enter = Math.Max(0f, (-b - root) / (2f * a));
                    exit = Math.Min(1f, (-b + root) / (2f * a));
                }
                var dy = s.by - s.ay;
                if (Math.Abs(dy) < 1e-8f)
                {
                    if (Math.Abs(s.ay - y) > PatchVerticalRangeMeters) continue;
                }
                else
                {
                    var low = (y - PatchVerticalRangeMeters - s.ay) / dy;
                    var high = (y + PatchVerticalRangeMeters - s.ay) / dy;
                    enter = Math.Max(enter, Math.Min(low, high));
                    exit = Math.Min(exit, Math.Max(low, high));
                }
                if (enter <= exit) earliest = Math.Min(earliest, t0 + span * enter);
            }
            return earliest;
        }
    }
}
