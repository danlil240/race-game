using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;

namespace FamilyRacing.Core.Networking
{
    // Host-side validated input path (Task 5). Every sample that may steer
    // an authoritative racer — including the host's own local racer —
    // passes through TryEnqueue with the participant resolved from the
    // authenticated connection, never trusted from the payload.
    //
    // Validation order is part of the contract (tests pin the codes):
    //   invalidFrame -> noRace -> staleSession -> staleRace ->
    //   unknownConnection -> foreignOwnership -> unknownParticipant ->
    //   staleEpoch -> nonFiniteInput -> tooManyActions -> badActionId ->
    //   duplicateSequence -> futureSequence -> bufferOverflow
    //
    // Consumption is bounded by policy: at most one new sample per racer
    // per host tick (bursts grant no extra steps), held input neutralizes
    // after InputStaleTicks silent ticks, and the queue hard-caps at
    // InputQueueCapacity — overflow explicitly resynchronizes by clearing
    // the backlog instead of replaying stale bursts.
    public sealed class HostInputBuffer
    {
        private sealed class Lane
        {
            internal long expectedEpoch;
            internal readonly Queue<NetworkInputFrame> queue =
                new Queue<NetworkInputFrame>();
            internal bool seeded;              // any sample ever accepted
            internal long lastEnqueued;        // highest sequence seen
            internal long lastConsumed;        // sequence last applied to a boundary
            internal long lastConsumedClientTick;
            internal long lastConsumedHostTick;
            internal DriverInput held;         // input held between samples
            internal int staleTicks;           // consecutive ticks without a usable sample
            // One-shot action dedup is independent of held input and is
            // deliberately retained across reseat/replay boundaries (a
            // bounded LRU) so a retried id can never fire twice.
            internal readonly HashSet<string> seenActions =
                new HashSet<string>(StringComparer.Ordinal);
            internal readonly Queue<string> seenActionOrder = new Queue<string>();
            internal readonly Queue<string> pendingActions = new Queue<string>();
        }

        private readonly Dictionary<string, Lane> lanes =
            new Dictionary<string, Lane>(StringComparer.Ordinal);
        private string sessionId = "";
        private string raceId = "";
        private bool active;
        private bool paused;
        private int overflowCount;

        // Number of times a lane overflowed the replay bound and resynced.
        public int OverflowCount { get { return overflowCount; } }
        public bool RaceActive { get { return active; } }
        public string SessionId { get { return sessionId; } }
        public string RaceId { get { return raceId; } }

        // Seated participant ids (lanes are created for humans only).
        public IEnumerable<string> Participants { get { return lanes.Keys; } }

        // Opens the validated path for one race. Queued samples and held
        // input from any previous race are discarded — but one-shot dedup
        // survives, so a retried id can never double-fire across a rematch
        // or reconnect boundary.
        public void BeginRace(string sessionId, string raceId)
        {
            this.sessionId = sessionId ?? "";
            this.raceId = raceId ?? "";
            active = true;
            foreach (var lane in lanes.Values) ResetLane(lane);
        }

        // Closes the validated path; subsequent frames fail with "noRace"
        // until the next BeginRace.
        public void EndRace()
        {
            active = false;
            sessionId = "";
            raceId = "";
        }

        // Seats (or reseats after rejoin) a human participant under an
        // ownership epoch. Reseating purges queued samples and held input
        // so old-epoch traffic can never drive a reclaimed racer; the
        // one-shot dedup set is retained.
        public void SeatParticipant(string participantId, long ownershipEpoch)
        {
            if (string.IsNullOrEmpty(participantId)) return;
            Lane lane;
            if (!lanes.TryGetValue(participantId, out lane))
            {
                lane = new Lane();
                lanes[participantId] = lane;
            }
            lane.expectedEpoch = ownershipEpoch;
            ResetLane(lane);
        }

        // Fences the lane to a new ownership epoch: all in-flight samples
        // belong to the old epoch and are purged so stale input can never
        // steer a reclaimed racer. The sequence baseline also resets — a
        // rejoining client (possibly a restarted process) begins a fresh
        // stream, and the epoch gate still rejects any old-epoch packet.
        public void SetOwnershipEpoch(string participantId, long ownershipEpoch)
        {
            Lane lane;
            if (!lanes.TryGetValue(participantId, out lane)) return;
            if (lane.expectedEpoch == ownershipEpoch) return;
            lane.expectedEpoch = ownershipEpoch;
            lane.queue.Clear();
            lane.held = default(DriverInput);
            lane.staleTicks = 0;
            lane.seeded = false;
            lane.lastEnqueued = 0;
            // The consumed baseline resets with the fence: a rejoined
            // (possibly restarted) process restarts its stream at
            // sequence 1 and must not be eaten by the defensive
            // consumed-sequence dequeue.
            lane.lastConsumed = 0;
            lane.lastConsumedClientTick = 0;
            lane.lastConsumedHostTick = 0;
        }

        public void UnseatParticipant(string participantId)
        {
            if (participantId != null) lanes.Remove(participantId);
        }

        // Shared pause: the host freezes the race clock, so consumption
        // stops producing usable samples entirely. Held input neutralizes
        // at pause entry; frames arriving during the pause still advance
        // their stream acknowledgement but can never become held input.
        // Resume therefore requires a genuinely fresh sample — nothing
        // buffered before or during the pause survives into it.
        public void SetPaused(bool value)
        {
            if (paused == value) return;
            paused = value;
            if (value)
                foreach (var lane in lanes.Values)
                {
                    lane.held = default(DriverInput);
                    lane.staleTicks = 0;
                }
        }

        // Control loss (disconnect, host loss): clears everything queued
        // or held for the participant and returns the pending one-shot
        // ids so the caller can emit explicit cancellations — a queued
        // action must never fire for a racer whose controls were just
        // lost. The sequence baseline and the action dedup set survive:
        // a late-arriving old-epoch packet is still fenced by the epoch
        // gate, and a retried action id still deduplicates.
        public List<string> ReleaseLane(string participantId)
        {
            var released = new List<string>();
            Lane lane;
            if (participantId == null || !lanes.TryGetValue(participantId, out lane))
                return released;
            lane.queue.Clear();
            lane.held = default(DriverInput);
            lane.staleTicks = 0;
            while (lane.pendingActions.Count > 0)
                released.Add(lane.pendingActions.Dequeue());
            return released;
        }

        public bool TryEnqueue(NetworkInputFrame frame, string connectionParticipantId,
            out string error)
        {
            error = null;
            if (frame == null) { error = "invalidFrame"; return false; }
            if (!active) { error = "noRace"; return false; }
            if (!string.Equals(frame.sessionId, sessionId, StringComparison.Ordinal))
            { error = "staleSession"; return false; }
            if (!string.Equals(frame.raceId, raceId, StringComparison.Ordinal))
            { error = "staleRace"; return false; }
            // Ownership comes from the authenticated connection, never
            // from the payload's claimed participantId.
            if (string.IsNullOrEmpty(connectionParticipantId))
            { error = "unknownConnection"; return false; }
            if (!string.Equals(frame.participantId, connectionParticipantId,
                    StringComparison.Ordinal))
            { error = "foreignOwnership"; return false; }
            Lane lane;
            if (!lanes.TryGetValue(connectionParticipantId, out lane))
            { error = "unknownParticipant"; return false; }
            if (frame.ownershipEpoch != lane.expectedEpoch)
            { error = "staleEpoch"; return false; }
            if (float.IsNaN(frame.steering) || float.IsInfinity(frame.steering))
            { error = "nonFiniteInput"; return false; }
            var actions = frame.actionIds;
            if (actions != null)
            {
                if (actions.Count > NetworkSettings.MaxFrameActionIds)
                { error = "tooManyActions"; return false; }
                foreach (var id in actions)
                    if (string.IsNullOrEmpty(id)
                        || id.Length > NetworkSettings.ActionIdLengthMax)
                    { error = "badActionId"; return false; }
            }
            if (lane.seeded && frame.inputSequence <= lane.lastEnqueued)
            { error = "duplicateSequence"; return false; }
            if (lane.seeded && frame.inputSequence
                    > lane.lastEnqueued + NetworkSettings.InputSequenceWindow)
            { error = "futureSequence"; return false; }
            if (lane.queue.Count >= NetworkSettings.InputQueueCapacity)
            {
                // Explicit resynchronization: drop the backlog rather than
                // apply a stale burst; the next snapshot's ack carries the
                // true last-consumed sequence for the client's reset.
                lane.queue.Clear();
                lane.held = default(DriverInput);
                lane.staleTicks = 0;
                overflowCount++;
                error = "bufferOverflow";
                return false;
            }
            lane.seeded = true;
            lane.lastEnqueued = frame.inputSequence;
            lane.queue.Enqueue(frame);
            if (actions != null)
                foreach (var id in actions) AcceptAction(lane, id);
            return true;
        }

        // Consumes at most one new sample for the participant on this host
        // tick and returns the currently held input: the freshly consumed
        // sample, the previously held one inside the staleness bound, or
        // neutral once InputStaleTicks ticks pass without a usable sample.
        public DriverInput Consume(string participantId, long hostTick)
        {
            Lane lane;
            if (participantId == null
                || !lanes.TryGetValue(participantId, out lane))
                return default(DriverInput);
            while (lane.queue.Count > 0
                && lane.queue.Peek().inputSequence <= lane.lastConsumed)
                lane.queue.Dequeue(); // defensive: never replay a consumed sample
            if (paused)
            {
                // Frozen clock: samples still acknowledge their stream but
                // can never become held input while the race is paused.
                lane.held = default(DriverInput);
                if (lane.queue.Count > 0)
                {
                    var frame = lane.queue.Dequeue();
                    lane.lastConsumed = frame.inputSequence;
                    lane.lastConsumedClientTick = frame.clientTick;
                    lane.lastConsumedHostTick = hostTick;
                    lane.staleTicks = 0;
                }
                return lane.held;
            }
            if (lane.queue.Count > 0)
            {
                var frame = lane.queue.Dequeue();
                lane.lastConsumed = frame.inputSequence;
                lane.lastConsumedClientTick = frame.clientTick;
                lane.lastConsumedHostTick = hostTick;
                lane.held = frame.input;
                lane.staleTicks = 0;
            }
            else
            {
                lane.staleTicks++;
                if (lane.staleTicks >= NetworkSettings.InputStaleTicks)
                    lane.held = default(DriverInput);
            }
            return lane.held;
        }

        // One-shot action identities accepted since the last drain, in
        // arrival order; each is produced exactly once.
        public List<string> DrainActions(string participantId)
        {
            var drained = new List<string>();
            Lane lane;
            if (participantId != null && lanes.TryGetValue(participantId, out lane))
                while (lane.pendingActions.Count > 0)
                    drained.Add(lane.pendingActions.Dequeue());
            return drained;
        }

        // The last processed input for this participant: null while the
        // participant is not seated, zero-valued until the host has
        // actually consumed a sample on a boundary.
        public NetworkInputAck AckFor(string participantId)
        {
            Lane lane;
            if (participantId == null
                || !lanes.TryGetValue(participantId, out lane))
                return null;
            return new NetworkInputAck
            {
                participantId = participantId,
                inputSequence = lane.lastConsumed,
                clientTick = lane.lastConsumedClientTick,
                hostTick = lane.lastConsumedHostTick
            };
        }

        public int QueueDepth(string participantId)
        {
            Lane lane;
            return participantId != null && lanes.TryGetValue(participantId, out lane)
                ? lane.queue.Count : 0;
        }

        private static void ResetLane(Lane lane)
        {
            lane.queue.Clear();
            lane.held = default(DriverInput);
            lane.staleTicks = 0;
            lane.pendingActions.Clear();
            lane.lastConsumed = 0;
            lane.lastConsumedClientTick = 0;
            lane.lastConsumedHostTick = 0;
            lane.seeded = false;
            lane.lastEnqueued = 0;
            // The one-shot dedup set deliberately survives: a retried
            // action id must never fire twice across a replay, reconnect,
            // or rematch boundary.
        }

        private static void AcceptAction(Lane lane, string id)
        {
            if (!lane.seenActions.Add(id)) return; // dedup independent of held input
            lane.seenActionOrder.Enqueue(id);
            while (lane.seenActionOrder.Count > NetworkSettings.ActionIdDedupCapacity)
                lane.seenActions.Remove(lane.seenActionOrder.Dequeue());
            if (lane.pendingActions.Count < NetworkSettings.PendingInboundActions)
                lane.pendingActions.Enqueue(id);
            // Past the inbound bound the id stays recorded as seen — a
            // retry deduplicates the same way, so it is cancelled rather
            // than applied twice.
        }
    }
}
