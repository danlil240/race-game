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

        private static void LaunchMissile(GadgetWorldState state, GadgetRacerInput owner,
            List<GadgetRacerInput> racers, GadgetDefinition def, List<DrivingEvent> events, ref int seq)
        {
            var fx = (float)Math.Sin(owner.heading);
            var fz = (float)Math.Cos(owner.heading);
            var best = def.targetingRangeMeters * def.targetingRangeMeters;
            var target = "";
            foreach (var rival in racers)
            {
                if (!Eligible(rival, owner.participantId) || Math.Abs(rival.y - owner.y) > PatchVerticalRangeMeters) continue;
                var dx = rival.x - owner.x; var dz = rival.z - owner.z;
                var d2 = dx * dx + dz * dz;
                if (d2 < 1e-6f || d2 > best || (target != "" && d2 == best)) continue;
                if (dx * fx + dz * fz < .5f * (float)Math.Sqrt(d2)) continue;
                best = d2; target = rival.participantId;
            }
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
                    def.slowMultiplier, def.slowDurationTicks, def.immunityTicks, events, ref seq);
            }
        }

        private static void ApplyWeaponHit(GadgetWorldState state, string victim, string effectId,
            string eventKind, float multiplier, int slowTicks, int immunityTicks,
            List<DrivingEvent> events, ref int seq)
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
            row.foamImmunityTicksRemaining = slowTicks + immunityTicks;
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
                if (target != null && Eligible(target, p.ownerParticipantId)
                    && Math.Abs(target.y - p.y) <= PatchVerticalRangeMeters)
                {
                    var desired = (float)Math.Atan2(target.x - p.x, target.z - p.z);
                    var delta = (float)Math.Atan2(Math.Sin(desired - p.heading), Math.Cos(desired - p.heading));
                    var turn = p.turnRate * dt;
                    p.heading += Math.Max(-turn, Math.Min(turn, delta));
                }
                else p.targetParticipantId = ""; // Never reacquire after losing a lock.
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
                    p.slowMultiplier, p.slowTicks, p.immunityTicks, events, ref seq);
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
                    mine.slowMultiplier, mine.slowTicks, mine.immunityTicks, events, ref seq);
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
