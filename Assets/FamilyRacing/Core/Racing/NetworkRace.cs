using System;
using System.Collections.Generic;
using System.Globalization;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;

namespace FamilyRacing.Core.Racing
{
    // The authoritative four-racer race coordinator: one host clock at the
    // shared 30 Hz boundary, one shared countdown, one shared elapsed race
    // clock, one shared pause, per-racer DrivingSession progress and finish
    // facts, standings, and a single finalized RaceResult implementing the
    // shared finish policy — all four finish, or 60 unpaused race seconds
    // after the first finisher with the remainder becoming DNF.
    //
    // Determinism contract (Task 6 replays prediction inputs through this
    // path): racers step in slot order on one shared tick, identical finish
    // ticks break ties by stable slot index, and the result object is
    // created once and never rebuilt or reordered.
    public sealed class NetworkRace
    {
        public const string PhaseCountdown = "countdown";
        public const string PhaseRunning = "running";
        public const string PhaseFinished = "finished";
        public const string PhaseAborted = "aborted";

        private sealed class Racer
        {
            internal string participantId;
            internal int slot;
            internal string vehicleId;
            internal string attribution;   // "human" | "ai"
            internal DrivingSession session;
            internal bool finished;
            internal bool dnf;
            internal long finishTicks;
        }

        private readonly string sessionId;
        private readonly string raceId;
        private readonly DrivingContentCatalog content;
        private readonly EventDefinition evt;
        private readonly Racer[] racers =
            new Racer[NetworkSettings.ParticipantCapacity];
        private readonly List<DrivingEvent> staged = new List<DrivingEvent>();
        private long eventSequence;

        private string phase;
        private bool paused;
        private long hostTick;
        private int countdownRemaining;
        private long elapsedTicks;
        private long firstFinishElapsedTicks;
        private long finishDeadlineElapsedTicks;
        private RaceResult result;

        private NetworkRace(string sessionId, string raceId,
            DrivingContentCatalog content, EventDefinition evt,
            CourseDefinition course, NetworkRacerSpec[] specs)
        {
            this.sessionId = sessionId;
            this.raceId = raceId;
            this.content = content;
            this.evt = evt;
            countdownRemaining = evt.countdownTicks;
            phase = countdownRemaining > 0 ? PhaseCountdown : PhaseRunning;
            for (var slot = 0; slot < specs.Length; slot++)
            {
                var spec = specs[slot];
                var spawn = course.spawnSlots[slot];
                // Each per-racer session gets its own id namespace
                // (raceId-r<slot>) so emitted event identities never
                // collide; the shared race identity stays on the
                // coordinator state.
                var session = new DrivingSession(
                    raceId + "-r" + slot.ToString(CultureInfo.InvariantCulture),
                    content.Vehicles[spec.vehicleId], course, evt,
                    content.Version, "", spawn);
                racers[slot] = new Racer
                {
                    participantId = spec.participantId,
                    slot = slot,
                    vehicleId = spec.vehicleId,
                    attribution = AttributionFor(spec.controlSource),
                    session = session
                };
            }
            // Per-racer "race.started" events are already staged by the
            // sessions; no extra coordinator-level marker is emitted so
            // consumers can rely on one race.started per seated racer.
        }

        // Validates the whole roster before constructing anything: a
        // rejected race leaves no partially built state. "human" control
        // (local | remote participant vocabulary) and "ai" are admitted;
        // vehicle ids must exist, belong to the event, and match the
        // class the lobby selected.
        public static bool TryCreate(string sessionId, string raceId,
            DrivingContentCatalog content, string eventId, string classId,
            IReadOnlyList<NetworkRacerSpec> racerSpecs,
            out NetworkRace race, out string error)
        {
            race = null;
            error = null;
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(raceId))
            { error = "invalidIdentity"; return false; }
            if (content == null)
            { error = "contentUnavailable"; return false; }
            EventDefinition evt;
            if (string.IsNullOrEmpty(eventId)
                || !content.Events.TryGetValue(eventId, out evt) || evt == null)
            { error = "unknownEvent"; return false; }
            CourseDefinition course;
            if (!content.Courses.TryGetValue(evt.courseId, out course)
                || course == null)
            { error = "unknownCourse"; return false; }
            if (string.IsNullOrEmpty(classId) || !EventHasClass(evt, classId))
            { error = "invalidClass"; return false; }
            if (racerSpecs == null
                || racerSpecs.Count != NetworkSettings.ParticipantCapacity)
            { error = "invalidRoster"; return false; }
            if (course.spawnSlots == null
                || course.spawnSlots.Count < NetworkSettings.ParticipantCapacity)
            { error = "insufficientSpawns"; return false; }

            var specs = new NetworkRacerSpec[NetworkSettings.ParticipantCapacity];
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var spec in racerSpecs)
            {
                if (spec == null || spec.slot < 0
                    || spec.slot >= NetworkSettings.ParticipantCapacity
                    || specs[spec.slot] != null
                    || string.IsNullOrEmpty(spec.participantId)
                    || !seenIds.Add(spec.participantId)
                    || AttributionFor(spec.controlSource) == null)
                { error = "invalidRoster"; return false; }
                if (!content.Vehicles.ContainsKey(spec.vehicleId))
                { error = "unknownVehicle"; return false; }
                if (!evt.AllowsVehicle(spec.vehicleId)
                    || VehicleTuning.ClassIdOf(spec.vehicleId) != classId)
                { error = "invalidVehicle"; return false; }
                specs[spec.slot] = spec;
            }
            foreach (var spec in specs)
                if (spec == null) { error = "invalidRoster"; return false; }

            race = new NetworkRace(sessionId, raceId, content, evt, course, specs);
            return true;
        }

        public string SessionId { get { return sessionId; } }
        public string RaceId { get { return raceId; } }
        public string Phase { get { return phase; } }
        public bool Paused { get { return paused; } }
        public long HostTick { get { return hostTick; } }
        public RaceResult Result { get { return result; } }

        // Task 4 supplies per-participant racer-contact sources here;
        // with no provider every session steps with racers:null exactly
        // like solo play.
        public Func<string, IRacerContactSource> ContactSourceProvider { get; set; }

        // Advances every unfinalized racer exactly one 30 Hz boundary under
        // the shared clock. Inputs are keyed by participantId; absent racers
        // take the zero input (auto-acceleration still drives). Returns the
        // state plus the events produced by this step; while paused, or once
        // the race is finished/aborted, the state is republished unchanged.
        public NetworkRaceState Step(
            IReadOnlyDictionary<string, DriverInput> inputs, IMotorWorld world)
        {
            var emitted = DrainStaged();
            if (paused || world == null
                || phase == PhaseFinished || phase == PhaseAborted)
                return BuildState(emitted);

            hostTick++;
            if (countdownRemaining > 0)
            {
                countdownRemaining--;
                if (countdownRemaining == 0)
                {
                    phase = PhaseRunning;
                    emitted.Add(CoordinatorEvent("race.running", ""));
                }
            }
            else
            {
                elapsedTicks++;
            }

            for (var slot = 0; slot < racers.Length; slot++)
            {
                var racer = racers[slot];
                if (racer.finished || racer.dnf) continue;
                DriverInput input;
                if (inputs == null
                    || !inputs.TryGetValue(racer.participantId, out input))
                    input = default;
                var contact = ContactSourceProvider == null
                    ? null
                    : ContactSourceProvider(racer.participantId);
                var step = racer.session.Step(input, world, contact);
                Stamp(racer, step.events, emitted);
                if (!racer.finished && step.state.finished)
                {
                    racer.finished = true;
                    racer.finishTicks = step.state.finishTicks;
                    if (firstFinishElapsedTicks == 0)
                    {
                        firstFinishElapsedTicks = racer.finishTicks;
                        finishDeadlineElapsedTicks = racer.finishTicks
                            + NetworkSettings.FinishGraceTicks;
                    }
                }
            }
            TryFinalize(emitted);
            return BuildState(emitted);
        }

        public NetworkRaceState Snapshot()
        {
            return BuildState(new List<DrivingEvent>());
        }

        // Shared pause: while paused the race stops simulating entirely —
        // countdown, race clock, motor progression, progress, and finish
        // deadlines all hold — and resuming continues from the frozen
        // facts. Entering pause cancels latched one-shots per racer, the
        // same policy solo pause applies.
        public void SetPaused(bool value)
        {
            if (value == paused) return;
            paused = value;
            if (paused)
                foreach (var racer in racers)
                {
                    racer.session.CancelPending("paused");
                    Stamp(racer, racer.session.DrainEvents(), staged);
                }
            staged.Add(CoordinatorEvent(paused ? "race.paused" : "race.resumed", ""));
        }

        // Ends the race without a result: aborted races never produce a
        // reward-bearing RaceResult. Idempotent.
        public void Abort(string reason)
        {
            if (phase == PhaseFinished || phase == PhaseAborted) return;
            phase = PhaseAborted;
            foreach (var racer in racers)
            {
                racer.session.CancelPending(reason ?? "aborted");
                Stamp(racer, racer.session.DrainEvents(), staged);
            }
            staged.Add(CoordinatorEvent("race.aborted", reason ?? ""));
        }

        // Routes a one-shot action (e.g. "recover") to a racer's own
        // pending queue — the same latching semantics solo callers get.
        public bool QueuePending(string participantId, string kind,
            string commandId, out string error)
        {
            var racer = FindRacer(participantId);
            if (racer == null) { error = "unknownParticipant"; return false; }
            if (phase == PhaseFinished || phase == PhaseAborted
                || racer.finished || racer.dnf)
            { error = "raceOver"; return false; }
            return racer.session.QueuePending(kind, commandId, out error);
        }

        // --- internals -------------------------------------------------------

        private Racer FindRacer(string participantId)
        {
            foreach (var racer in racers)
                if (racer != null && racer.participantId == participantId)
                    return racer;
            return null;
        }

        private static string AttributionFor(string controlSource)
        {
            // LanSessionService control-source vocabulary: "local" and
            // "remote" are human-driven; "ai" (and "none") are not.
            switch (controlSource)
            {
                case "local":
                case "remote":
                    return "human";
                case "ai":
                case "none":
                    return "ai";
                default:
                    return null;
            }
        }

        private static bool EventHasClass(EventDefinition evt, string classId)
        {
            if (evt.vehicleIds == null) return false;
            foreach (var id in evt.vehicleIds)
                if (VehicleTuning.ClassIdOf(id) == classId) return true;
            return false;
        }

        private void TryFinalize(List<DrivingEvent> emitted)
        {
            if (result != null || phase != PhaseRunning) return;
            var done = true;
            foreach (var racer in racers)
                if (!racer.finished && !racer.dnf) { done = false; break; }
            if (!done && finishDeadlineElapsedTicks > 0
                && elapsedTicks >= finishDeadlineElapsedTicks)
            {
                foreach (var racer in racers)
                    if (!racer.finished) racer.dnf = true;
                done = true;
            }
            if (!done) return;

            phase = PhaseFinished;
            var snaps = SnapshotAll();
            var order = StandingOrder(snaps);
            var final = new RaceResult
            {
                resultId = raceId + "/result",
                sessionId = sessionId,
                raceId = raceId,
                eventId = evt.id,
                contentVersion = content.Version,
                finalized = true,
                finalizedHostTick = hostTick,
                finalizedElapsedTicks = elapsedTicks,
                completionCoins = evt.completionCoins
            };
            for (var i = 0; i < order.Count; i++)
            {
                var slot = order[i];
                var racer = racers[slot];
                final.racers.Add(new RaceResultEntry
                {
                    position = i + 1,
                    slot = slot,
                    participantId = racer.participantId,
                    vehicleId = racer.vehicleId,
                    attribution = racer.attribution,
                    outcome = racer.finished ? "finished" : "dnf",
                    finishTicks = racer.finished ? racer.finishTicks : 0,
                    lapsCompleted = snaps[slot].lap,
                    coinsAwarded = racer.finished ? evt.completionCoins : 0
                });
            }
            result = final;
            emitted.Add(CoordinatorEvent("race.finalized", result.resultId));
        }

        private DrivingSessionState[] SnapshotAll()
        {
            var snaps = new DrivingSessionState[racers.Length];
            for (var i = 0; i < racers.Length; i++)
                snaps[i] = racers[i].session.Snapshot();
            return snaps;
        }

        // Standings order: finishers rank ahead of non-finishers and sort
        // by finish tick then stable slot; non-finishers sort by accepted
        // lap, accepted gates on the current lap, clamped metres on the
        // current legal segment, then stable slot.
        private List<int> StandingOrder(DrivingSessionState[] snaps)
        {
            var order = new List<int>(racers.Length);
            for (var i = 0; i < racers.Length; i++) order.Add(i);
            order.Sort((a, b) =>
                CompareRacers(racers[a], snaps[a], racers[b], snaps[b]));
            return order;
        }

        private static int CompareRacers(Racer a, DrivingSessionState sa,
            Racer b, DrivingSessionState sb)
        {
            if (a.finished != b.finished) return a.finished ? -1 : 1;
            if (a.finished)
            {
                if (a.finishTicks != b.finishTicks)
                    return a.finishTicks.CompareTo(b.finishTicks);
                return a.slot.CompareTo(b.slot);
            }
            if (sa.lap != sb.lap) return sb.lap.CompareTo(sa.lap);
            if (sa.acceptedGatesThisLap != sb.acceptedGatesThisLap)
                return sb.acceptedGatesThisLap.CompareTo(sa.acceptedGatesThisLap);
            if (sa.segmentProgressMeters != sb.segmentProgressMeters)
                return sb.segmentProgressMeters.CompareTo(sa.segmentProgressMeters);
            return a.slot.CompareTo(b.slot);
        }

        private NetworkRaceState BuildState(List<DrivingEvent> emitted)
        {
            var snaps = SnapshotAll();
            var order = StandingOrder(snaps);
            var positionOf = new int[racers.Length];
            for (var i = 0; i < order.Count; i++) positionOf[order[i]] = i + 1;

            var state = new NetworkRaceState
            {
                sessionId = sessionId,
                raceId = raceId,
                phase = phase,
                hostTick = hostTick,
                countdownTicksRemaining = countdownRemaining,
                elapsedTicks = elapsedTicks,
                paused = paused,
                firstFinishElapsedTicks = firstFinishElapsedTicks,
                finishDeadlineElapsedTicks = finishDeadlineElapsedTicks,
                result = result,
                events = emitted ?? new List<DrivingEvent>()
            };
            state.standings.AddRange(order);
            for (var i = 0; i < racers.Length; i++)
            {
                var racer = racers[i];
                state.racers.Add(new NetworkRacerState
                {
                    participantId = racer.participantId,
                    slot = racer.slot,
                    vehicleId = racer.vehicleId,
                    attribution = racer.attribution,
                    position = positionOf[i],
                    finished = racer.finished,
                    dnf = racer.dnf,
                    finishTicks = racer.finishTicks,
                    session = snaps[i]
                });
            }
            return state;
        }

        private List<DrivingEvent> DrainStaged()
        {
            var drained = new List<DrivingEvent>(staged);
            staged.Clear();
            return drained;
        }

        private static void Stamp(Racer racer, List<DrivingEvent> events,
            List<DrivingEvent> into)
        {
            // Per-racer events are attributed at emission time — whoever
            // owned the input then — never re-derived from later
            // connection state.
            foreach (var ev in events)
            {
                ev.participantId = racer.participantId;
                ev.attribution = racer.attribution;
                into.Add(ev);
            }
        }

        private DrivingEvent CoordinatorEvent(string kind, string detail)
        {
            var id = raceId + "-e" + (++eventSequence)
                .ToString(CultureInfo.InvariantCulture);
            return new DrivingEvent
            {
                id = id,
                tick = hostTick,
                kind = kind,
                commandId = "",
                detail = detail ?? "",
                participantId = "",
                attribution = ""
            };
        }
    }
}
