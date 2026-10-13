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
            internal AiDriver ai;          // non-null while attribution is "ai"
            internal bool recoverHeld;     // one-shot recover edge latch
            internal bool finished;
            internal bool dnf;
            internal long finishTicks;
        }

        // A candidate respawn pose closer than this to a committed peer
        // body (ground-plane distance, and vertically — a mid-jump car
        // overhead does not block a ground spawn) is occupied. Matches
        // the authored minimum spawn separation so a respawn can never
        // interpenetrate a chassis.
        private const float RecoveryOccupancyMeters
            = CourseDefinition.MinSpawnSeparationMeters;

        private readonly string sessionId;
        private readonly string raceId;
        private readonly DrivingContentCatalog content;
        private readonly EventDefinition evt;
        private readonly CourseDefinition course;
        private readonly Racer[] racers =
            new Racer[NetworkSettings.ParticipantCapacity];
        private readonly List<DrivingEvent> staged = new List<DrivingEvent>();
        private long eventSequence;
        // Legacy IMotorWorld adapter: one lazily-built arcade simulation
        // per world so the old call shape stays alive while callers
        // migrate to the injected-seam Step.
        private IMotorWorld legacyWorld;
        private ArcadeVehicleSimulation legacySimulation;

        private string phase;
        private bool paused;
        private long hostTick;
        private int countdownRemaining;
        private long elapsedTicks;
        private long firstFinishElapsedTicks;
        private long finishDeadlineElapsedTicks;
        private RaceResult result;
        // The world the most recent Step ran inside — retained so the
        // published state can attach its live cost/shape diagnostics on
        // every boundary, including paused/finalized boundaries where
        // the sim is not stepped again (Task 11).
        private IVehicleSimulation lastSimulation;

        private NetworkRace(string sessionId, string raceId,
            DrivingContentCatalog content, EventDefinition evt,
            CourseDefinition course, NetworkRacerSpec[] specs)
        {
            this.sessionId = sessionId;
            this.raceId = raceId;
            this.content = content;
            this.evt = evt;
            this.course = course;
            countdownRemaining = evt.countdownTicks;
            phase = countdownRemaining > 0 ? PhaseCountdown : PhaseRunning;
            // One canonical content identity + backend selection for the
            // whole race — computed once so every seated session binds
            // the same tuning fingerprint (Task 11 observability).
            var fingerprint = content.CompatibilityFingerprint;
            var backend = content.SimulationBackend;
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
                    content.Version, "", spawn, spec.participantId,
                    fingerprint, backend);
                var attribution = AttributionFor(spec.controlSource);
                racers[slot] = new Racer
                {
                    participantId = spec.participantId,
                    slot = slot,
                    vehicleId = spec.vehicleId,
                    attribution = attribution,
                    ai = attribution == "ai" ? new AiDriver(LaneFor(slot)) : null,
                    session = session
                };
                // Recovery placement must never overlap a peer body:
                // the session asks this probe before choosing between
                // the primary safe pose and the authored candidates.
                // Committed poses only — deterministic in slot order.
                var occupiedSlot = slot;
                session.PoseOccupied = pose =>
                {
                    for (var other = 0; other < racers.Length; other++)
                    {
                        var peer = racers[other];
                        if (peer == null || other == occupiedSlot
                            || peer.session == null) continue;
                        var motor = peer.session.Snapshot().motor;
                        if (motor == null) continue;
                        var dx = motor.x - pose.x;
                        var dz = motor.z - pose.z;
                        if (dx * dx + dz * dz
                                < RecoveryOccupancyMeters
                                    * RecoveryOccupancyMeters
                            && Math.Abs(motor.y - pose.y)
                                < RecoveryOccupancyMeters)
                            return true;
                    }
                    return false;
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
                // The backend is a race-level contract selected once at
                // creation: a physically simulated race refuses to seat
                // a vehicle that cannot satisfy the backend instead of
                // failing inside the first batch.
                if (content.SimulationBackend
                        == VehicleSimulationBackend.PhysicalAssisted)
                {
                    var vehicle = content.Vehicles[spec.vehicleId];
                    if (vehicle.simulationBackend
                            != VehicleSimulationBackend.PhysicalAssisted
                        || vehicle.physics == null)
                    { error = "invalidVehicle"; return false; }
                }
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

        // The roster the owning simulation must be initialized with
        // before the first Step: one VehicleSpawn per seated racer,
        // carrying the catalog tuning for its vehicle and the session's
        // current motor (the authored grid pose at race start).
        public List<VehicleSpawn> VehicleSpawns()
        {
            var spawns = new List<VehicleSpawn>(racers.Length);
            foreach (var racer in racers)
            {
                if (racer == null) continue;
                VehicleTuning tuning;
                if (!content.Vehicles.TryGetValue(racer.vehicleId, out tuning)
                    || tuning == null)
                    continue;
                spawns.Add(new VehicleSpawn
                {
                    participantId = racer.participantId,
                    tuning = tuning,
                    initialState = racer.session.Snapshot().motor
                });
            }
            return spawns;
        }

        // Advances every unfinalized racer exactly one 30 Hz boundary under
        // the shared clock through ONE injected simulation batch. The
        // per-tick order is the binding seam every backend honours:
        //   validate/sample inputs -> prepare every request (+ pending
        //   resets) -> apply resets -> forces + the shared batch -> complete
        //   every rule from the returned paths -> apply rule-requested
        //   recovery resets -> publish ONE committed boundary.
        // Inputs are keyed by participantId; absent racers take the zero
        // input (auto-acceleration still drives). Racers attributed "ai"
        // are driven by the waypoint follower on the course's authored
        // route — submitted inputs for an ai-owned slot never steer it.
        // Every AI sample reads the same pre-step snapshot, and every
        // contact body the backend consults is the batch's own pre-step
        // view, so slot iteration order cannot change outcomes. Returns
        // the state plus the events produced by this step; while paused,
        // or once the race is finished/aborted, the simulation is never
        // called and the state is republished unchanged.
        public NetworkRaceState Step(
            IReadOnlyDictionary<string, DriverInput> inputs,
            IVehicleSimulation simulation)
        {
            var emitted = DrainStaged();
            if (simulation != null) lastSimulation = simulation;
            if (paused || simulation == null
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

            // The single pre-step view every AI sample reads this tick.
            var preStep = SnapshotAll();

            // --- 1) prepare every request (+ pending resets) ------------
            var prepared = new List<Prepared>(racers.Length);
            var requests = new List<VehicleTickRequest>(racers.Length);
            for (var slot = 0; slot < racers.Length; slot++)
            {
                var racer = racers[slot];
                if (racer.finished || racer.dnf) continue;
                DriverInput input;
                if (racer.attribution == "ai")
                {
                    input = racer.ai != null
                        ? racer.ai.Sample(preStep[slot], course)
                        : default(DriverInput);
                }
                else if (inputs == null
                    || !inputs.TryGetValue(racer.participantId, out input))
                {
                    input = default;
                }
                // The recover one-shot latches on the press edge exactly
                // like the solo command path: holding it never stacks
                // recoveries; the queued action is consumed on a later
                // simulated boundary.
                if (input.Recover && !racer.recoverHeld)
                {
                    // The wire-held recover carries a synthetic per-tick
                    // id, not a client-submitted one-shot — there is no
                    // acknowledgment contract to satisfy, so a declined
                    // queue is an intentional silent drop.
                    racer.session.QueuePending("recover",
                        raceId + "-r" + slot + "-rc"
                            + hostTick.ToString(CultureInfo.InvariantCulture),
                        out _);
                }
                racer.recoverHeld = input.Recover;
                var request = racer.session.PrepareStep(input);
                if (request == null) continue; // session finalized
                request.participantId = racer.participantId;
                prepared.Add(new Prepared { racer = racer, request = request });
                requests.Add(request);
            }

            // --- 2) pending/manual resets land on the bodies first ------
            for (var i = 0; i < prepared.Count; i++)
            {
                var reset = prepared[i].request.reset;
                if (reset != null)
                    simulation.Restore(reset.participantId, reset.state);
            }

            // --- 3) exactly one shared batch for the whole roster -------
            var results = simulation.Step(requests);
            var byParticipant = new Dictionary<string, VehicleTickResult>(
                StringComparer.Ordinal);
            if (results != null)
                foreach (var result in results)
                    if (result != null && result.participantId != null
                        && !byParticipant.ContainsKey(result.participantId))
                        byParticipant[result.participantId] = result;

            // --- 4) complete every rule in slot order -------------------
            for (var i = 0; i < prepared.Count; i++)
            {
                var entry = prepared[i];
                var racer = entry.racer;
                VehicleTickResult result;
                if (!byParticipant.TryGetValue(racer.participantId,
                        out result))
                    result = null;
                var completed = racer.session.CompleteStep(result);
                Stamp(racer, completed.events, emitted);
                // 5) a rule-requested recovery reset lands on the body
                // before this boundary publishes — the committed state
                // already shows the post-reset pose.
                if (completed.reset != null)
                    simulation.Restore(completed.reset.participantId,
                        completed.reset.state);
                if (!racer.finished && completed.state.finished)
                {
                    racer.finished = true;
                    racer.finishTicks = completed.state.finishTicks;
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

        private sealed class Prepared
        {
            internal Racer racer;
            internal VehicleTickRequest request;
        }

        // Legacy call shape retained while callers migrate: wraps the
        // supplied motor world in a persistent ArcadeVehicleSimulation so
        // this overload produces bit-for-bit identical outcomes through
        // the same prepare/batch/complete seam. Removed once every caller
        // injects a simulation.
        public NetworkRaceState Step(
            IReadOnlyDictionary<string, DriverInput> inputs, IMotorWorld world)
        {
            if (world == null)
                return Step(inputs, (IVehicleSimulation)null);
            if (legacySimulation == null || legacyWorld != world)
            {
                legacyWorld = world;
                legacySimulation = new ArcadeVehicleSimulation(world);
                legacySimulation.Initialize(VehicleSpawns());
            }
            return Step(inputs, legacySimulation);
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
            // Terminal races have no live clock to freeze and must never
            // mint lifecycle events — a teardown pause/resume flip is a
            // presentation no-op.
            if (phase == PhaseFinished || phase == PhaseAborted) return;
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

        // Ownership transfer used by the lifecycle policy: a confirmed
        // disconnect seats the Task-4 AI on the reserved slot, and an
        // authenticated rejoin hands it back to a human. The change
        // applies from this host tick on — events already emitted keep
        // the control source recorded at their tick, so absence driving
        // never earns human credit retroactively.
        public bool SetControlSource(string participantId, string controlSource)
        {
            var racer = FindRacer(participantId);
            var attribution = AttributionFor(controlSource);
            if (racer == null || attribution == null) return false;
            if (attribution == racer.attribution) return true;
            racer.attribution = attribution;
            // A fresh driver on every human->ai takeover re-anchors the
            // follower onto the nearest route point at the slot's lane;
            // handing the slot back drops the driver entirely.
            racer.ai = attribution == "ai"
                ? new AiDriver(LaneFor(racer.slot)) : null;
            staged.Add(CoordinatorEvent("race.control",
                (racer.participantId ?? "") + "=" + attribution));
            return true;
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

        // The slot's lateral lane for AI driving: symmetric spread around
        // the route line (-3, -1, +1, +3 m for the four slots).
        private static float LaneFor(int slot)
        {
            return (slot - (NetworkSettings.ParticipantCapacity - 1) * 0.5f)
                * NetworkSettings.AiLaneSpacingMeters;
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
            // One shared world serves the whole roster, so the same
            // diagnostics snapshot describes every racer's simulation;
            // it rides the state stream and is stripped from the
            // compact LAN wire by its non-serializable DTO type.
            var diagnosticsSource = lastSimulation
                as IVehicleSimulationDiagnostics;
            var diagnostics = diagnosticsSource != null
                ? diagnosticsSource.Diagnostics : null;
            for (var i = 0; i < racers.Length; i++)
            {
                var racer = racers[i];
                snaps[i].simulation = diagnostics;
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
