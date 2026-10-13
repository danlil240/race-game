using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Networking
{
    // Remote racer presentation (Task 6). Each delivered snapshot pushes
    // every racer's authoritative motor pose into a bounded per-
    // participant buffer keyed by host tick. Sample() renders a
    // participant at a host-time coordinate the caller supplies —
    // InterpolationDelaySeconds behind the newest tick — interpolating
    // between the two bracketing poses, extrapolating by velocity at
    // most ExtrapolationLimitSeconds past the newest entry, then holding
    // the last pose and reporting it stale. Reordered or duplicated
    // pushes never rewind the buffer, and racers absent from a snapshot
    // lose their lane deterministically.
    //
    // The render time is in host seconds (hostTick /
    // SimulationTicksPerSecond); the caller computes the delayed render
    // coordinate from the newest buffered tick plus local elapsed time.
    public sealed class RemoteInterpolation
    {
        private struct Entry
        {
            public long tick;
            public MotorState motor;
        }

        private sealed class Lane
        {
            public readonly List<Entry> entries = new List<Entry>();
            public bool stale;
            public double extrapolationSeconds;
            public long sourceTick = -1;
        }

        private readonly Dictionary<string, Lane> lanes =
            new Dictionary<string, Lane>(StringComparer.Ordinal);

        public void Push(NetworkSnapshot snapshot)
        {
            if (snapshot == null || snapshot.state == null
                || snapshot.state.racers == null) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var tick = snapshot.state.hostTick;
            foreach (var racer in snapshot.state.racers)
            {
                if (racer == null || racer.session == null
                    || racer.session.motor == null
                    || string.IsNullOrEmpty(racer.participantId))
                    continue;
                seen.Add(racer.participantId);
                Lane lane;
                if (!lanes.TryGetValue(racer.participantId, out lane))
                {
                    lane = new Lane();
                    lanes[racer.participantId] = lane;
                }
                var last = lane.entries.Count - 1;
                // Reordered/duplicated snapshots never rewind a lane.
                if (last >= 0 && tick <= lane.entries[last].tick) continue;
                lane.entries.Add(new Entry
                {
                    tick = tick,
                    motor = racer.session.motor.Copy()
                });
                while (lane.entries.Count
                        > NetworkSettings.InterpolationBufferCapacity)
                    lane.entries.RemoveAt(0);
            }
            // A racer absent from the roster loses its lane — no phantom
            // poses outlive the snapshot they came from.
            var dead = new List<string>();
            foreach (var key in lanes.Keys)
                if (!seen.Contains(key)) dead.Add(key);
            foreach (var key in dead) lanes.Remove(key);
        }

        // The presentation pose at a host-time coordinate (seconds of
        // hostTick/30). Interpolates inside the buffer, extrapolates at
        // most ExtrapolationLimitSeconds past the newest entry, then
        // holds the last pose marked stale. Returns null before the
        // participant's first buffered pose.
        public MotorState Sample(string participantId, double renderHostTime)
        {
            Lane lane;
            if (participantId == null
                || !lanes.TryGetValue(participantId, out lane)
                || lane.entries.Count == 0)
                return null;
            var entries = lane.entries;
            var target = renderHostTime
                * NetworkSettings.SimulationTicksPerSecond;
            var newest = entries[entries.Count - 1];
            if (target >= newest.tick)
            {
                var over = (target - newest.tick)
                    / NetworkSettings.SimulationTicksPerSecond;
                lane.sourceTick = newest.tick;
                lane.extrapolationSeconds = over;
                if (over <= NetworkSettings.ExtrapolationLimitSeconds)
                {
                    lane.stale = false;
                    return Extrapolate(newest.motor, over);
                }
                lane.stale = true;
                return newest.motor.Copy();
            }
            var oldest = entries[0];
            if (target <= oldest.tick)
            {
                lane.stale = false;
                lane.extrapolationSeconds = 0;
                lane.sourceTick = oldest.tick;
                return oldest.motor.Copy();
            }
            for (var i = entries.Count - 2; i >= 0; i--)
            {
                if (entries[i].tick > target) continue;
                var a = entries[i];
                var b = entries[i + 1];
                var span = b.tick - a.tick;
                var alpha = span > 0
                    ? (float)((target - a.tick) / span) : 1f;
                lane.stale = false;
                lane.extrapolationSeconds = 0;
                lane.sourceTick = b.tick;
                return Lerp(a.motor, b.motor, alpha);
            }
            return oldest.motor.Copy();
        }

        public bool IsStale(string participantId)
        {
            Lane lane;
            return participantId != null
                && lanes.TryGetValue(participantId, out lane) && lane.stale;
        }

        public double ExtrapolationSeconds(string participantId)
        {
            Lane lane;
            return participantId != null
                && lanes.TryGetValue(participantId, out lane)
                ? lane.extrapolationSeconds : 0;
        }

        // The host tick the last sample anchored to — the upper bracket
        // while interpolating, the newest entry while extrapolating or
        // held. -1 before the first sample.
        public long SourceTick(string participantId)
        {
            Lane lane;
            return participantId != null
                && lanes.TryGetValue(participantId, out lane)
                ? lane.sourceTick : -1;
        }

        public int BufferDepth(string participantId)
        {
            Lane lane;
            return participantId != null
                && lanes.TryGetValue(participantId, out lane)
                ? lane.entries.Count : 0;
        }

        public long NewestTick(string participantId)
        {
            Lane lane;
            return participantId != null
                && lanes.TryGetValue(participantId, out lane)
                && lane.entries.Count > 0
                ? lane.entries[lane.entries.Count - 1].tick : -1;
        }

        public void Reset()
        {
            lanes.Clear();
        }

        // Observable interpolation facts for the validation stream under
        // `remotePresentation` — one entry per remote participant lane.
        public RemoteInterpolationState State()
        {
            var state = new RemoteInterpolationState();
            var ids = new List<string>(lanes.Keys);
            ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var lane = lanes[id];
                state.racers.Add(new RemoteInterpolationEntry
                {
                    participantId = id,
                    sourceTick = lane.sourceTick,
                    newestTick = lane.entries.Count > 0
                        ? lane.entries[lane.entries.Count - 1].tick : -1,
                    bufferCount = lane.entries.Count,
                    stale = lane.stale,
                    extrapolationSeconds = lane.extrapolationSeconds
                });
            }
            return state;
        }

        // Bounded velocity projection over `seconds` — at most the 100 ms
        // extrapolation cap — with flags held from the source pose.
        // Physical samples project the body through its SI linear
        // velocity and integrate the attitude by the SI angular velocity
        // (first-order quaternion step, normalized); legacy samples
        // project through the heading-decomposed speed fields. Honest
        // motion only — no collisions, no events.
        private static MotorState Extrapolate(MotorState motor, double seconds)
        {
            var dt = (float)seconds;
            var copy = motor.Copy();
            var physics = motor.physics;
            if (physics != null && copy.physics != null)
            {
                copy.x += physics.velocityX * dt;
                copy.y += physics.velocityY * dt;
                copy.z += physics.velocityZ * dt;
                // q' = q + dt * (1/2 * ω̃ ⊗ q), then renormalize through
                // TrySetAttitude. ω̃ = (ωx, ωy, ωz, 0) in world frame.
                var hx = physics.angularVelocityX * 0.5f * dt;
                var hy = physics.angularVelocityY * 0.5f * dt;
                var hz = physics.angularVelocityZ * 0.5f * dt;
                var qx = physics.attitudeX;
                var qy = physics.attitudeY;
                var qz = physics.attitudeZ;
                var qw = physics.attitudeW;
                copy.physics.TrySetAttitude(
                    qx + (hx * qw + hy * qz - hz * qy),
                    qy + (-hx * qz + hy * qw + hz * qx),
                    qz + (hx * qy - hy * qx + hz * qw),
                    qw + (-hx * qx - hy * qy - hz * qz));
                return copy;
            }
            var fx = (float)Math.Sin(motor.heading);
            var fz = (float)Math.Cos(motor.heading);
            var rx = fz;
            var rz = -fx;
            copy.x += (fx * motor.speed + rx * motor.lateralSpeed) * dt;
            copy.z += (fz * motor.speed + rz * motor.lateralSpeed) * dt;
            copy.y += motor.verticalSpeed * dt;
            return copy;
        }

        // Pose interpolation: continuous kinematic fields lerp (heading
        // along the shortest arc); discrete flags follow the newer entry.
        // Physical samples additionally slerp the full attitude along the
        // shortest arc and lerp the SI linear/angular velocities — roll
        // and pitch ride the buffer, not just yaw.
        private static MotorState Lerp(MotorState a, MotorState b, float alpha)
        {
            var copy = b.Copy();
            copy.x = a.x + (b.x - a.x) * alpha;
            copy.y = a.y + (b.y - a.y) * alpha;
            copy.z = a.z + (b.z - a.z) * alpha;
            var delta = b.heading - a.heading;
            while (delta > Math.PI) delta -= (float)(Math.PI * 2.0);
            while (delta <= -Math.PI) delta += (float)(Math.PI * 2.0);
            copy.heading = a.heading + delta * alpha;
            copy.speed = a.speed + (b.speed - a.speed) * alpha;
            copy.lateralSpeed = a.lateralSpeed
                + (b.lateralSpeed - a.lateralSpeed) * alpha;
            copy.verticalSpeed = a.verticalSpeed
                + (b.verticalSpeed - a.verticalSpeed) * alpha;
            var pa = a.physics;
            var pb = b.physics;
            if (pa != null && pb != null && copy.physics != null)
            {
                SlerpAttitude(pa, pb, alpha, copy.physics);
                var pc = copy.physics;
                pc.velocityX = pa.velocityX
                    + (pb.velocityX - pa.velocityX) * alpha;
                pc.velocityY = pa.velocityY
                    + (pb.velocityY - pa.velocityY) * alpha;
                pc.velocityZ = pa.velocityZ
                    + (pb.velocityZ - pa.velocityZ) * alpha;
                pc.angularVelocityX = pa.angularVelocityX
                    + (pb.angularVelocityX - pa.angularVelocityX) * alpha;
                pc.angularVelocityY = pa.angularVelocityY
                    + (pb.angularVelocityY - pa.angularVelocityY) * alpha;
                pc.angularVelocityZ = pa.angularVelocityZ
                    + (pb.angularVelocityZ - pa.angularVelocityZ) * alpha;
            }
            return copy;
        }

        // Shortest-arc quaternion slerp between two attitudes, written
        // into `dst` through the normalizing setter. A negative dot
        // flips one endpoint so the blend never takes the long way —
        // required for full-attitude interpolation past 180 degrees.
        private static void SlerpAttitude(VehiclePhysicsState a,
            VehiclePhysicsState b, float alpha, VehiclePhysicsState dst)
        {
            var ax = a.attitudeX;
            var ay = a.attitudeY;
            var az = a.attitudeZ;
            var aw = a.attitudeW;
            var bx = b.attitudeX;
            var by = b.attitudeY;
            var bz = b.attitudeZ;
            var bw = b.attitudeW;
            var dot = ax * bx + ay * by + az * bz + aw * bw;
            if (dot < 0f)
            {
                dot = -dot;
                bx = -bx; by = -by; bz = -bz; bw = -bw;
            }
            float wa, wb;
            if (dot > 0.9995f)
            {
                // Nearly parallel: nlerp is exact enough and avoids the
                // acos/sin singularity.
                wa = 1f - alpha;
                wb = alpha;
            }
            else
            {
                var theta = Math.Acos(dot);
                var sinTheta = (float)Math.Sin(theta);
                wa = (float)Math.Sin((1f - alpha) * theta) / sinTheta;
                wb = (float)Math.Sin(alpha * theta) / sinTheta;
            }
            dst.TrySetAttitude(
                wa * ax + wb * bx,
                wa * ay + wb * by,
                wa * az + wb * bz,
                wa * aw + wb * bw);
        }
    }

    // Per-participant interpolation facts inside `remotePresentation`.
    [Serializable]
    public sealed class RemoteInterpolationEntry
    {
        public string participantId = "";
        public long sourceTick = -1;    // tick the last sample anchored to
        public long newestTick = -1;    // newest buffered authoritative tick
        public int bufferCount;
        public bool stale;              // held past the extrapolation bound
        public double extrapolationSeconds;

        public RemoteInterpolationEntry Copy()
        {
            return (RemoteInterpolationEntry)MemberwiseClone();
        }
    }

    // Observable remote-presentation facts for the validation stream.
    [Serializable]
    public sealed class RemoteInterpolationState
    {
        public List<RemoteInterpolationEntry> racers =
            new List<RemoteInterpolationEntry>();

        public RemoteInterpolationState Copy()
        {
            var copy = new RemoteInterpolationState();
            foreach (var entry in racers)
                copy.racers.Add(entry != null ? entry.Copy() : null);
            return copy;
        }
    }
}
