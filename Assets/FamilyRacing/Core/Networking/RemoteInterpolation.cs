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

        // Bounded velocity projection: forward + lateral + vertical
        // displacement over `seconds`, heading and flags held from the
        // source pose. Honest motion only — no collisions, no events.
        private static MotorState Extrapolate(MotorState motor, double seconds)
        {
            var dt = (float)seconds;
            var copy = motor.Copy();
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
            return copy;
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
