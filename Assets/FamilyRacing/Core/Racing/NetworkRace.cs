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
            internal VehicleTuning tuning;   // effective seated tuning
            internal string attribution;   // "human" | "ai"
            internal DrivingSession session;
            internal AiDriver ai;          // non-null while attribution is "ai"
            internal bool recoverHeld;     // one-shot recover edge latch
            internal bool finished;
            internal bool dnf;
            internal long finishTicks;
        }

        // Local-mode context carried only by TryCreateLocal races: the
        // mode/rules identity the format-2 result binds, the visible AI
        // difficulty preset every AI seat (created at construction OR by
        // a control-source takeover) runs under, and the authored rival
        // seat for the rivalDefeated fact. LAN races keep this null and
        // produce legacy format-1 results.
        private sealed class LocalContext
        {
            internal string mode;
            internal string rulesFingerprint;
            internal AiDifficultyDefinition difficulty;
            internal string rivalParticipantId;
            // M5 Task 7: the lobby-validated class the race ran under —
            // stamped on the format-2 result as a record-grouping fact.
            internal string classId;
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
        // Authoritative per-racer metric counters fed at every completed
        // boundary; always on (cheap), bound onto format-2 results only.
        private readonly RaceMetricsAccumulator metrics =
            new RaceMetricsAccumulator();
        private readonly LocalContext local;
        // M5 Task 5: the shared authoritative gadget world — authored
        // pickup sites on the course plus the seeded draw bag and every
        // racer's gadget row. Null when the catalog supplies no gadget
        // definitions or the course authors no sites; every consumer
        // treats null as "gadgets inert".
        private readonly GadgetDefinitionCatalog gadgets;
        private GadgetWorldState gadgetWorld;

        private NetworkRace(string sessionId, string raceId,
            DrivingContentCatalog content, EventDefinition evt,
            CourseDefinition course, NetworkRacerSpec[] specs,
            LocalContext local, VehicleTuning[] tunings,
            GadgetDefinitionCatalog gadgets)
        {
            this.sessionId = sessionId;
            this.raceId = raceId;
            this.content = content;
            this.evt = evt;
            this.course = course;
            this.local = local;
            this.gadgets = gadgets;
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
                var attribution = AttributionFor(spec.controlSource);
                var tuning = tunings != null && tunings[slot] != null
                    ? tunings[slot]
                    : content.Vehicles[spec.vehicleId];
                // Each per-racer session gets its own id namespace
                // (raceId-r<slot>) so emitted event identities never
                // collide; the shared race identity stays on the
                // coordinator state.
                var session = new DrivingSession(
                    raceId + "-r" + slot.ToString(CultureInfo.InvariantCulture),
                    tuning, course, evt,
                    content.Version, "", spawn, spec.participantId,
                    fingerprint, backend);
                racers[slot] = new Racer
                {
                    participantId = spec.participantId,
                    slot = slot,
                    vehicleId = spec.vehicleId,
                    tuning = tuning,
                    attribution = attribution,
                    ai = attribution == "ai"
                        ? new AiDriver(LaneFor(slot), DifficultyFor())
                        : null,
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

            // The shared authoritative gadget world (M5 Task 5): one row
            // per seated racer, pickups on the course's authored sites,
            // and a draw bag seeded by the race identity — the same deal
            // on every device and every replay, independent of
            // standings. Absent catalog content or a site-free course
            // leaves the world null so snapshots carry no dead payload.
            if (gadgets != null && gadgets.gadgets != null
                && gadgets.gadgets.Count > 0
                && course.pickupSites != null
                && course.pickupSites.Count > 0)
            {
                var roster = new List<string>(specs.Length);
                foreach (var spec in specs)
                    roster.Add(spec.participantId);
                gadgetWorld = GadgetRules.Create(gadgets,
                    course.pickupSites, GadgetRules.StableSeed(raceId),
                    roster, raceId);
            }
        }

        // Validates the whole roster before constructing anything: a
        // rejected race leaves no partially built state. "human" control
        // (local | remote participant vocabulary) and "ai" are admitted;
        // vehicle ids must exist, belong to the event, and match the
        // class the lobby selected.
        public static bool TryCreate(string sessionId, string raceId,
            DrivingContentCatalog content, string eventId, string classId,
            IReadOnlyList<NetworkRacerSpec> racerSpecs,
            out NetworkRace race, out string error,
            GadgetDefinitionCatalog gadgets = null)
        {
            // The LAN factory always runs the normalized catalog
            // baseline (requestedTuning ignored) and produces legacy
            // format-1 results — the local seam owns both differences.
            return ValidateAndCreate(sessionId, raceId, content, eventId,
                classId, racerSpecs, null, gadgets, out race, out error);
        }

        // The local creation seam (M5 Task 4): same validation and roster
        // contract as TryCreate, plus the local-mode context the
        // format-2 result binds and an optional validated per-racer tune.
        // Differences from the LAN factory, all deliberate:
        //  - `spec.requestedTuning` is honored when present and valid for
        //    the seat (vehicle id match, full Validate(), same class,
        //    backend-compatible) — the local seam exists so a profile-
        //    sourced tune can seat; LAN still ignores it and always runs
        //    the normalized catalog baseline.
        //  - every AI seat — constructed now or created later by a
        //    control-source takeover — runs under `difficulty`.
        //  - the finalized result is bound format-2 with mode, rules
        //    fingerprint, per-seat difficulty and accumulated metrics.
        public static bool TryCreateLocal(string sessionId, string raceId,
            DrivingContentCatalog content, string eventId, string classId,
            IReadOnlyList<NetworkRacerSpec> racerSpecs, string mode,
            string rulesFingerprint, AiDifficultyDefinition difficulty,
            string rivalParticipantId,
            out NetworkRace race, out string error,
            GadgetDefinitionCatalog gadgets = null)
        {
            race = null;
            error = null;
            var local = new LocalContext
            {
                mode = mode ?? "",
                rulesFingerprint = rulesFingerprint ?? "",
                difficulty = difficulty,
                rivalParticipantId = rivalParticipantId,
                classId = classId ?? ""
            };
            if (!ValidateAndCreate(sessionId, raceId, content, eventId,
                    classId, racerSpecs, local, gadgets, out race,
                    out error))
                return false;
            return true;
        }

        // Shared validation+construction for both factories — the local
        // context is null for LAN and drives the tuning-honor and
        // format-2 result differences only.
        private static bool ValidateAndCreate(string sessionId, string raceId,
            DrivingContentCatalog content, string eventId, string classId,
            IReadOnlyList<NetworkRacerSpec> racerSpecs, LocalContext local,
            GadgetDefinitionCatalog gadgets,
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
            var tunings = new VehicleTuning[NetworkSettings.ParticipantCapacity];
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
                var tuning = content.Vehicles[spec.vehicleId];
                if (local != null && spec.requestedTuning != null)
                {
                    // The local seam honors a validated per-racer tune:
                    // same vehicle id, full tuning validation, same
                    // class identity, and the race-backend contract —
                    // anything else falls back to refusal rather than
                    // silently racing an unvalidated tune.
                    var requested = spec.requestedTuning;
                    string tuningError;
                    if (requested.vehicleId != spec.vehicleId
                        || !requested.Validate(out tuningError)
                        || VehicleTuning.ClassIdOf(requested.vehicleId)
                            != classId)
                    { error = "invalidVehicle"; return false; }
                    tuning = requested;
                }
                // The backend is a race-level contract selected once at
                // creation: a physically simulated race refuses to seat
                // a vehicle that cannot satisfy the backend instead of
                // failing inside the first batch.
                if (content.SimulationBackend
                        == VehicleSimulationBackend.PhysicalAssisted
                    && (tuning.simulationBackend
                            != VehicleSimulationBackend.PhysicalAssisted
                        || tuning.physics == null))
                { error = "invalidVehicle"; return false; }
                tunings[spec.slot] = tuning;
                specs[spec.slot] = spec;
            }
            foreach (var spec in specs)
                if (spec == null) { error = "invalidRoster"; return false; }

            race = new NetworkRace(sessionId, raceId, content, evt, course,
                specs, local, tunings, gadgets);
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
                if (racer == null || racer.tuning == null) continue;
                spawns.Add(new VehicleSpawn
                {
                    participantId = racer.participantId,
                    tuning = racer.tuning,
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
                        ? racer.ai.Sample(preStep[slot], course,
                            GadgetContextFor(racer, preStep))
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
                // M5 Task 5: the shared world's per-racer modifier rides
                // the prepared request into the simulation batch —
                // host-authoritative effects reach both backends through
                // exactly one seam.
                racer.session.PendingModifiers =
                    GadgetRules.ModifierFor(gadgetWorld,
                        racer.participantId);
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
            var completed = new DrivingStepResult[prepared.Count];
            for (var i = 0; i < prepared.Count; i++)
            {
                var entry = prepared[i];
                var racer = entry.racer;
                VehicleTickResult result;
                if (!byParticipant.TryGetValue(racer.participantId,
                        out result))
                    result = null;
                completed[i] = racer.session.CompleteStep(result);
                Stamp(racer, completed[i].events, emitted);
            }

            // --- 4b) ONE authoritative gadget boundary ------------------
            // After every session committed (poses/paths are final), the
            // shared world advances exactly once: edge-triggered uses,
            // swept-segment collections and patch hits all resolve in
            // stable slot order off the committed boundary facts. Pause
            // never reaches here — the early return above freezes the
            // world whole. A frozen or recovery boundary participates
            // with `recovering` so clocks tick but nothing collects.
            Dictionary<string, List<DrivingEvent>> gadgetEvents = null;
            if (gadgetWorld != null)
            {
                var gadgetInput = new GadgetStepInput { tick = hostTick };
                for (var i = 0; i < prepared.Count; i++)
                {
                    var entry = prepared[i];
                    var request = entry.request;
                    var done = completed[i];
                    var racerInput = new GadgetRacerInput
                    {
                        participantId = entry.racer.participantId,
                        slot = entry.racer.slot,
                        gadgetHeld = request.input.Gadget,
                        recovering = !request.simulateMotion
                            || request.reset != null
                            || (done != null && done.reset != null),
                        finished = done != null && done.state != null
                            && done.state.finished
                    };
                    var committed = done != null ? done.state.motor : null;
                    if (committed != null)
                    {
                        racerInput.x = committed.x;
                        racerInput.y = committed.y;
                        racerInput.z = committed.z;
                    }
                    // The swept segments are the boundary's real
                    // substep path — a respawn teleport can never
                    // manufacture a collection because teleports do
                    // not appear in the traveled path.
                    var path = done != null && done.state != null
                        ? done.state.substepPath : null;
                    if (path != null)
                        for (var s = 1; s < path.Count; s++)
                        {
                            var a = path[s - 1].pose;
                            var b = path[s].pose;
                            racerInput.segments.Add(new GadgetSegment
                            {
                                ax = a.x, ay = a.y, az = a.z,
                                bx = b.x, by = b.y, bz = b.z
                            });
                        }
                    gadgetInput.racers.Add(racerInput);
                }
                var gadgetStep = GadgetRules.Step(gadgetInput,
                    gadgetWorld, gadgets);
                if (gadgetStep.state != null)
                    gadgetWorld = gadgetStep.state;
                foreach (var ev in gadgetStep.events)
                {
                    // Attribution rides at emission time — whoever owned
                    // the slot this boundary, never re-derived later.
                    var owner = FindRacer(ev.participantId);
                    if (owner != null) ev.attribution = owner.attribution;
                    emitted.Add(ev);
                    if (!string.IsNullOrEmpty(ev.participantId))
                    {
                        if (gadgetEvents == null)
                            gadgetEvents = new Dictionary<string,
                                List<DrivingEvent>>(StringComparer.Ordinal);
                        List<DrivingEvent> list;
                        if (!gadgetEvents.TryGetValue(ev.participantId,
                                out list))
                            gadgetEvents[ev.participantId] = list =
                                new List<DrivingEvent>();
                        list.Add(ev);
                    }
                }
            }

            // --- 5) metrics, recovery resets, finish tracking -----------
            for (var i = 0; i < prepared.Count; i++)
            {
                var entry = prepared[i];
                var racer = entry.racer;
                var done = completed[i];
                // Metric facts count at the completed simulated boundary
                // — attribution stamped at emission time, so facts a
                // human earned stay human facts even if a takeover
                // happens before the next boundary (Task 4). Gadget
                // events merge into the same boundary view.
                List<DrivingEvent> gadgetList;
                var boundaryEvents = gadgetEvents != null
                    && gadgetEvents.TryGetValue(racer.participantId,
                        out gadgetList)
                    ? Merged(done.events, gadgetList)
                    : done.events;
                metrics.RecordBoundary(hostTick, racer.participantId,
                    racer.attribution, phase == PhaseRunning,
                    done.state, boundaryEvents);
                // A rule-requested recovery reset lands on the body
                // before this boundary publishes — the committed state
                // already shows the post-reset pose.
                if (done.reset != null)
                    simulation.Restore(done.reset.participantId,
                        done.reset.state);
                if (!racer.finished && done.state.finished)
                {
                    racer.finished = true;
                    racer.finishTicks = done.state.finishTicks;
                    if (firstFinishElapsedTicks == 0)
                    {
                        firstFinishElapsedTicks = racer.finishTicks;
                        finishDeadlineElapsedTicks = racer.finishTicks
                            + (long)evt.finishGraceSeconds * NetworkSettings.SimulationTicksPerSecond;
                    }
                }
            }
            TryFinalize(emitted);
            return BuildState(emitted);
        }

        // Ordered concatenation for the metrics view: the session's own
        // events first, then this boundary's gadget events.
        private static List<DrivingEvent> Merged(List<DrivingEvent> events,
            List<DrivingEvent> extra)
        {
            var merged = new List<DrivingEvent>(
                (events != null ? events.Count : 0)
                + (extra != null ? extra.Count : 0));
            if (events != null) merged.AddRange(events);
            if (extra != null) merged.AddRange(extra);
            return merged;
        }

        // The gadget facts one AI sample needs (M5 Task 5): the seat's
        // held item plus host-computed spatial facts — a rival near
        // behind arms the trap, a live rival patch ahead arms the
        // shield. A null/empty row yields null so Sample stays fast.
        private AiGadgetContext GadgetContextFor(Racer racer,
            DrivingSessionState[] preStep)
        {
            if (gadgetWorld == null) return null;
            var row = gadgetWorld.Racer(racer.participantId);
            if (row == null || string.IsNullOrEmpty(row.heldItemId))
                return null;
            var context = new AiGadgetContext
            {
                heldItemId = row.heldItemId,
                heldGeneration = row.heldGeneration,
                heldTicks = row.heldTicks,
                // The rules' own press latch rides the context so a
                // takeover landing on a held button emits a release
                // before its press — the rules swallow any edge while
                // this stays set, and a swallowed pulse must not mark
                // the generation spent.
                pressLatched = row.gadgetHeld
            };
            var def = gadgets != null ? gadgets.Gadget(row.heldItemId) : null;
            context.kind = def != null ? def.kind : "";
            var motor = preStep != null && racer.slot < preStep.Length
                && preStep[racer.slot] != null
                ? preStep[racer.slot].motor : null;
            if (motor == null) return context;
            var fx = (float)Math.Sin(motor.heading);
            var fz = (float)Math.Cos(motor.heading);
            var behind = NetworkSettings.AiGadgetRivalBehindMeters;
            for (var i = 0; i < preStep.Length; i++)
            {
                if (i == racer.slot || preStep[i] == null) continue;
                var peer = preStep[i].motor;
                if (peer == null) continue;
                var dx = peer.x - motor.x;
                var dz = peer.z - motor.z;
                if (dx * dx + dz * dz <= behind * behind
                    && dx * fx + dz * fz < 0f)
                    context.rivalNearBehind = true;
            }
            var ahead = NetworkSettings.AiGadgetPatchAheadMeters;
            if (gadgetWorld.patches != null)
                foreach (var patch in gadgetWorld.patches)
                {
                    if (patch == null
                        || patch.ownerParticipantId == racer.participantId)
                        continue;
                    var dx = patch.x - motor.x;
                    var dz = patch.z - motor.z;
                    var forward = dx * fx + dz * fz;
                    if (forward > 0f && forward <= ahead
                        && Math.Abs(patch.y - motor.y)
                            <= CourseDefinition.MinSpawnSeparationMeters)
                        context.patchAhead = true;
                }
            return context;
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
            // handing the slot back drops the driver entirely. Local
            // races seat the selected difficulty on every takeover too.
            racer.ai = attribution == "ai"
                ? new AiDriver(LaneFor(racer.slot), DifficultyFor()) : null;
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

        // Lifecycle cancellation of one racer's latched one-shots
        // (local-mode input clear): emits ordered cancelled events the
        // next boundary publishes, same as the session-level policy.
        public void CancelPending(string participantId, string reason)
        {
            var racer = FindRacer(participantId);
            if (racer == null) return;
            racer.session.CancelPending(reason);
            Stamp(racer, racer.session.DrainEvents(), staged);
        }

        // Independent authoritative metric rows for the whole roster,
        // in roster order — observers mutate freely.
        public List<RaceMetrics> Metrics()
        {
            return metrics.Snapshot();
        }

        // The AI difficulty every local seat runs under — null on LAN,
        // where the follower keeps its established constants.
        private AiDifficultyDefinition DifficultyFor()
        {
            return local != null ? local.difficulty : null;
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
            // Local races bind the format-2 result identity BEFORE first
            // publication: mode, rules fingerprint, per-seat difficulty,
            // accumulated metrics and the rivalDefeated fact — so the
            // fingerprint a consumer computes already covers them.
            if (local != null)
            {
                RaceResultFactory.Bind(final, local.mode,
                    local.rulesFingerprint, metrics.Snapshot(),
                    local.difficulty != null ? local.difficulty.id : "",
                    local.rivalParticipantId);
                // Task 7 record-grouping facts ride the same format-2
                // binding — stamped before first publication so the
                // fingerprint covers them.
                final.courseId = evt.courseId ?? "";
                final.classId = local.classId ?? "";
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
                events = emitted ?? new List<DrivingEvent>(),
                // The published world is a deep copy — observation and
                // wire paths can never mutate the host's live state.
                gadgets = gadgetWorld != null ? gadgetWorld.Copy() : null
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
                // Authoritative placement/clock facts refresh every
                // boundary — counters never come from this call.
                metrics.UpdateStandards(racer.participantId,
                    positionOf[i],
                    racer.finished ? "finished" : racer.dnf ? "dnf" : "",
                    racer.finished ? racer.finishTicks : 0,
                    elapsedTicks);
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
