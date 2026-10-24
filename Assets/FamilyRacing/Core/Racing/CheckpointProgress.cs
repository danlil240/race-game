using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;

namespace FamilyRacing.Core.Racing
{
    // Ordered progress tracker owned by one DrivingSession. Index 0 of the
    // course gate list is the start/finish line: it counts as pre-crossed
    // at spawn, so every forward crossing of gate 0 while expected
    // completes a lap. `marks` mirrors the per-gate rows published in the
    // driving snapshot; the tracker itself is session-internal.
    public sealed class CheckpointState
    {
        public int expectedCheckpoint;
        public int lap;
        public int lapsRequired;
        public bool finished;
        public readonly List<CheckpointMark> marks = new List<CheckpointMark>();

        public CheckpointState Copy()
        {
            var copy = new CheckpointState
            {
                expectedCheckpoint = expectedCheckpoint,
                lap = lap,
                lapsRequired = lapsRequired,
                finished = finished
            };
            foreach (var mark in marks) copy.marks.Add(mark.Copy());
            return copy;
        }
    }

    // One ordered progress event inside a step. `kind` is "checkpoint"
    // (a gate accepted), "lap" (a gate cycle completed) or "finish"
    // (the required laps are done). `lap` carries the completed lap count
    // for "lap"/"finish" events; `gateId`/`checkpointIndex` identify the
    // accepted gate.
    public sealed class CheckpointEvent
    {
        public string kind;
        public int checkpointIndex;
        public string gateId;
        public int lap;
    }

    // Result of one Advance call. `state` is the same tracker instance the
    // caller passed (Advance mutates it in place); `events` are ordered in
    // travel order. `recoveryPose`/`recoveryPoseUpdated` report the newest
    // accepted safe pose so the session can track lastValidRecoveryPose.
    public sealed class CheckpointResult
    {
        public CheckpointState state;
        public readonly List<CheckpointEvent> events = new List<CheckpointEvent>();
        public bool recoveryPoseUpdated;
        public MotorPose recoveryPose;
    }

    // Ordered directional gate crossings. Only the swept previous->current
    // pose segment (projected on the course plane) is evaluated — never a
    // raw trigger overlap — and crossings are applied strictly in travel
    // order against the single expected gate. A crossing counts when the
    // segment moves from behind the gate plane to past it along the gate's
    // authored direction within its half-width. Reverse crossings, gate
    // re-entry, out-of-order planes and start-line crossings before a full
    // cycle can never manufacture progress; respawn teleports are never
    // submitted here because recovery replaces the pose without Advance.
    public static class CheckpointProgress
    {
        private const float MinDirectionLength = 0.001f;

        // Initial tracker: gate 0 (the start line) is pre-crossed by the
        // spawn, so expectedCheckpoint is 1 whenever a second gate exists.
        public static CheckpointState Create(CourseDefinition course, int lapsRequired)
        {
            var state = new CheckpointState { lapsRequired = lapsRequired };
            var gates = course != null ? course.checkpoints : null;
            var count = gates == null ? 0 : gates.Count;
            for (var i = 0; i < count; i++)
            {
                state.marks.Add(new CheckpointMark
                {
                    id = gates[i] != null ? gates[i].id : "",
                    index = i,
                    crossed = false
                });
            }
            state.expectedCheckpoint = count > 1 ? 1 : 0;
            if (count > 1) state.marks[0].crossed = true;
            return state;
        }

        // Evaluate the swept pose segment against every gate, collect the
        // forward crossings in travel order, then accept only the gate the
        // tracker is currently waiting for at each moment. Mutates `state`
        // in place; the result carries the ordered events and the latest
        // accepted safe recovery pose.
        public static CheckpointResult Advance(CourseDefinition course, CheckpointState state,
            MotorPose previous, MotorPose current)
        {
            var result = new CheckpointResult { state = state };
            if (course == null || state == null || state.finished)
                return result;
            var gates = course.checkpoints;
            var count = gates == null ? 0 : gates.Count;
            if (count == 0) return result;
            if (!Finite(previous.x) || !Finite(previous.y) || !Finite(previous.z)
                || !Finite(current.x) || !Finite(current.y) || !Finite(current.z))
                return result;

            // Collect every directional plane crossing this step makes,
            // tagged with its fraction along the travel segment.
            List<Crossing> crossings = null;
            for (var i = 0; i < count; i++)
            {
                float fraction;
                if (TryCross(gates[i], previous, current, out fraction))
                {
                    if (crossings == null) crossings = new List<Crossing>();
                    crossings.Add(new Crossing { index = i, fraction = fraction });
                }
            }
            if (crossings == null) return result;

            // Travel order: earliest plane contact first; index breaks ties
            // deterministically for degenerate co-planar gates.
            crossings.Sort((a, b) =>
                a.fraction != b.fraction
                    ? a.fraction.CompareTo(b.fraction)
                    : a.index.CompareTo(b.index));
            for (var i = 0; i < crossings.Count; i++)
            {
                var index = crossings[i].index;
                if (index != state.expectedCheckpoint) continue;
                Accept(course, state, index, result);
                if (state.finished) break;
            }
            return result;
        }

        private static void Accept(CourseDefinition course, CheckpointState state,
            int index, CheckpointResult result)
        {
            var gate = course.checkpoints[index];
            if (index < state.marks.Count) state.marks[index].crossed = true;
            result.events.Add(new CheckpointEvent
            {
                kind = "checkpoint",
                checkpointIndex = index,
                gateId = gate.id,
                lap = state.lap
            });
            state.expectedCheckpoint = course.isOpenRoute
                ? Math.Min(index + 1, course.checkpoints.Count - 1)
                : (index + 1) % course.checkpoints.Count;
            if (course.isOpenRoute && index == course.checkpoints.Count - 1)
            {
                state.lap = 1;
                state.finished = true;
                result.events.Add(new CheckpointEvent { kind = "finish",
                    checkpointIndex = index, gateId = gate.id, lap = 1 });
            }
            else if (!course.isOpenRoute && index == 0)
            {
                // Crossing the line while expected completes the cycle and
                // starts the next lap on the line.
                state.lap++;
                result.events.Add(new CheckpointEvent
                {
                    kind = "lap",
                    checkpointIndex = index,
                    gateId = gate.id,
                    lap = state.lap
                });
                for (var i = 0; i < state.marks.Count; i++)
                    state.marks[i].crossed = i == 0;
                if (state.lap >= state.lapsRequired)
                {
                    state.finished = true;
                    result.events.Add(new CheckpointEvent
                    {
                        kind = "finish",
                        checkpointIndex = index,
                        gateId = gate.id,
                        lap = state.lap
                    });
                }
            }
            result.recoveryPose = new MotorPose
            {
                x = gate.recoverX,
                y = gate.recoverY,
                z = gate.recoverZ,
                heading = gate.recoverHeading
            };
            result.recoveryPoseUpdated = true;
        }

        // Directional swept crossing of one gate plane. The plane passes
        // through the gate centre with the authored direction as normal;
        // the gate's lateral bound is |offset along tangent| <= halfWidth.
        // A crossing requires signed distance <= 0 before and > 0 after.
        private static bool TryCross(CheckpointGate gate, MotorPose previous,
            MotorPose current, out float fraction)
        {
            fraction = 0f;
            if (gate == null) return false;
            var length = (float)Math.Sqrt(gate.dirX * gate.dirX + gate.dirZ * gate.dirZ);
            if (!Finite(length) || length < MinDirectionLength) return false;
            var nx = gate.dirX / length;
            var nz = gate.dirZ / length;
            var s0 = (previous.x - gate.x) * nx + (previous.z - gate.z) * nz;
            var s1 = (current.x - gate.x) * nx + (current.z - gate.z) * nz;
            if (!(s0 <= 0f && s1 > 0f)) return false;
            fraction = s0 / (s0 - s1);
            var ix = previous.x + (current.x - previous.x) * fraction;
            var iz = previous.z + (current.z - previous.z) * fraction;
            // Lateral offset along the gate tangent (-nz, nx).
            var lateral = (ix - gate.x) * -nz + (iz - gate.z) * nx;
            if (Math.Abs(lateral) > gate.halfWidth) return false;
            return true;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private struct Crossing
        {
            internal int index;
            internal float fraction;
        }
    }
}
