using System;
using System.Collections.Generic;
using System.Globalization;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Progression;

namespace FamilyRacing.Core.Racing
{
    // The local four-racer/single-racer race session adapter (M5 Task 4):
    // Campaign and Quick Race seat ONE human against three same-class AI
    // inside the shared NetworkRace coordinator — the same prepare/
    // shared-batch/complete seam, countdown, elapsed clock, pause,
    // finish/DNF policy, standings and single immutable result LAN uses,
    // with no transport anywhere. Time Trial reuses the lone
    // DrivingSession against the authored `time` event.
    //
    // Practice is deliberately NOT here: it keeps its legacy
    // `FoundationState.driving` path. LAN races never come through here
    // either — the session owns local clocks only.
    public sealed class SoloRaceSession
    {
        private sealed class TrialWorld
        {
            internal DrivingSession session;
            internal EventDefinition evt;
            // The resolved record-grouping class (event's authored class
            // or the requested one) — stamped onto the trial result.
            internal string classId;
        }

        private readonly string mode;          // campaign | quickRace | timeTrial
        private readonly string sessionId;
        private readonly string raceId;
        private readonly string participantId;
        private readonly string difficultyId;
        private readonly string rulesFingerprint;
        private readonly SoloRaceRequest request;
        private readonly DrivingContentCatalog content;

        private readonly NetworkRace race;     // campaign | quickRace
        private readonly TrialWorld trial;     // timeTrial
        private readonly RaceMetricsAccumulator trialMetrics;

        private bool paused;
        private long hostTick;
        private RaceResult result;
        private string abortReason = "";

        private SoloRaceSession(SoloRaceRequest request, string mode,
            string sessionId, string raceId, string participantId,
            string difficultyId, string rulesFingerprint,
            DrivingContentCatalog content, NetworkRace race,
            TrialWorld trial, RaceMetricsAccumulator trialMetrics)
        {
            this.request = request;
            this.mode = mode;
            this.sessionId = sessionId;
            this.raceId = raceId;
            this.participantId = participantId;
            this.difficultyId = difficultyId;
            this.rulesFingerprint = rulesFingerprint;
            this.content = content;
            this.race = race;
            this.trial = trial;
            this.trialMetrics = trialMetrics;
        }

        public string Mode { get { return mode; } }
        public string SessionId { get { return sessionId; } }
        public string RaceId { get { return raceId; } }
        public string ParticipantId { get { return participantId; } }
        public string DifficultyId { get { return difficultyId; } }
        public bool Paused { get { return paused; } }
        public RaceResult Result { get { return result; } }
        public SoloRaceRequest Request { get { return request != null ? request.Copy() : null; } }

        // Validates the whole selection before constructing anything — a
        // rejected start leaves no partially built state, exactly like
        // NetworkRace.TryCreate. `profile` supplies the local human's
        // participant identity and is required for progression modes
        // (the reward context binds its id); a time trial tolerates a
        // null profile by falling back to request.participantId.
        public static bool TryCreate(SoloRaceRequest request,
            ProfileSave profile, GameplayCatalog catalog,
            out SoloRaceSession session, out string error)
        {
            session = null;
            error = null;
            if (request == null) { error = "invalidRequest"; return false; }
            RaceMode mode;
            if (!RaceModeRules.TryParse(request.mode, out mode)
                || mode == RaceMode.Lan || mode == RaceMode.Practice)
            { error = "invalidMode"; return false; }
            if (catalog == null || catalog.IsLegacy || catalog.Driving == null)
            { error = "contentUnavailable"; return false; }
            var content = catalog.Driving;
            var participantId = !string.IsNullOrEmpty(request.participantId)
                ? request.participantId
                : profile != null ? profile.id : "";
            if (RaceModeRules.IsProgression(mode)
                && string.IsNullOrEmpty(participantId))
            { error = "requiresSelectedProfile"; return false; }
            if (string.IsNullOrEmpty(participantId))
                participantId = "local";

            // The visible AI preset resolves up front: an unknown id is
            // refused at the creation boundary, never mid-race.
            var difficultyId = string.IsNullOrEmpty(request.difficultyId)
                ? RaceModeRules.DefaultDifficultyId : request.difficultyId;
            AiDifficultyDefinition difficulty = null;
            if (catalog.Difficulties != null)
                foreach (var candidate in catalog.Difficulties)
                    if (candidate != null && candidate.id == difficultyId)
                        difficulty = candidate;
            if (difficulty == null)
            { error = "unknownDifficulty"; return false; }

            var raceId = !string.IsNullOrEmpty(request.raceId)
                ? request.raceId
                : "solo-" + Guid.NewGuid().ToString("N")
                    .Substring(0, 12);
            var sessionId = "solo:" + raceId;
            var rulesFingerprint = catalog.CompatibilityFingerprint;
            var requestCopy = request.Copy();
            requestCopy.difficultyId = difficultyId;
            requestCopy.raceId = raceId;
            requestCopy.participantId = participantId;

            if (mode == RaceMode.TimeTrial)
                return CreateTrial(requestCopy, catalog, content, sessionId,
                    raceId, participantId, difficultyId, rulesFingerprint,
                    catalog.Gadgets, out session, out error);
            return CreateRace(requestCopy, mode, catalog, content, sessionId,
                raceId, participantId, difficulty, rulesFingerprint,
                profile, out session, out error);
        }

        // Campaign and Quick Race: one human + three same-class AI through
        // the shared four-racer coordinator. The roster is deterministic:
        // the human owns slot 0; AI seats 1..3 take remaining class-legal
        // vehicles in authored order (cycling when fewer than three other
        // legal vehicles exist). A finale's authored rival owns seat 1.
        // Entry eligibility is the shared RaceModeRules.CanEnter verdict —
        // the same answer the garage, discovery and raw commands consult —
        // and the human seat carries the profile-resolved purchased tune.
        private static bool CreateRace(SoloRaceRequest request, RaceMode mode,
            GameplayCatalog catalog, DrivingContentCatalog content,
            string sessionId, string raceId, string participantId,
            AiDifficultyDefinition difficulty, string rulesFingerprint,
            ProfileSave profile,
            out SoloRaceSession session, out string error)
        {
            session = null;
            error = null;
            // The classId-explicit seam: quickRace.start names course +
            // class and lets the rules resolve the authored circuit
            // event — the same verdict the UI pickers and raw
            // solo.start consult.
            var decision = RaceModeRules.Evaluate(mode, request.courseId,
                request.eventId, request.classId, request.vehicleId,
                profile, content, catalog);
            if (!decision.allowed)
            { error = decision.code; return false; }
            EventDefinition evt = content.Events[decision.eventId];
            var classId = decision.classId;
            // The profile's durable upgrades resolve onto the catalog
            // baseline for this seat — a null resolve is a content or
            // tuning failure the race must not paper over.
            var tuning = VehicleTuningResolver.Resolve(mode,
                request.vehicleId, profile, catalog);
            if (tuning == null)
            { error = "invalidVehicle"; return false; }

            // The authored rival of a class finale owns the first AI
            // seat under a stable, honest participant identity.
            string rivalId = null;
            if (catalog.Campaign != null && catalog.Campaign.finales != null)
                foreach (var finale in catalog.Campaign.finales)
                    if (finale != null && finale.eventId == evt.id)
                        rivalId = finale.rivalId;
            var rivalParticipantId = rivalId != null
                ? "rival-" + rivalId : null;

            // Class-legal vehicles other than the human's, in authored
            // order — deterministic AI seat assignment, cycling when a
            // class offers fewer alternates than seats.
            var alternates = new List<string>();
            if (evt.vehicleIds != null)
                foreach (var id in evt.vehicleIds)
                    if (id != request.vehicleId
                        && VehicleTuning.ClassIdOf(id) == classId)
                        alternates.Add(id);
            if (alternates.Count == 0)
                alternates.Add(request.vehicleId);

            var specs = new List<NetworkRacerSpec>(
                NetworkSettings.ParticipantCapacity);
            specs.Add(new NetworkRacerSpec
            {
                slot = 0,
                participantId = participantId,
                vehicleId = request.vehicleId,
                controlSource = "local",
                // Task 8: the profile-resolved purchased tune — the
                // local seam honors it after the same validation the
                // baseline passes (vehicle id, full Validate, class,
                // backend). AI seats keep the catalog baseline:
                // purchases are never the field's.
                requestedTuning = tuning
            });
            var alternate = 0;
            for (var slot = 1; slot < NetworkSettings.ParticipantCapacity; slot++)
            {
                var aiParticipantId = slot == 1 && rivalParticipantId != null
                    ? rivalParticipantId
                    : "ai-slot-" + slot.ToString(CultureInfo.InvariantCulture);
                specs.Add(new NetworkRacerSpec
                {
                    slot = slot,
                    participantId = aiParticipantId,
                    vehicleId = alternates[alternate++ % alternates.Count],
                    controlSource = "ai"
                });
            }

            NetworkRace race;
            if (!NetworkRace.TryCreateLocal(sessionId, raceId, content,
                    evt.id, classId, specs, RaceModeRules.ModeId(mode),
                    rulesFingerprint, difficulty, rivalParticipantId,
                    out race, out error, catalog.Gadgets))
                return false;

            session = new SoloRaceSession(request,
                RaceModeRules.ModeId(mode), sessionId, raceId, participantId,
                difficulty.id, rulesFingerprint, content, race, null, null);
            return true;
        }

        // Time Trial: the authored `time` event for the course+class (or
        // an explicitly named event), the normalized starter vehicle and
        // one DrivingSession — no AI fill, no items, no rewards. A lone
        // session needs no spawn slots. Event/starter resolution is the
        // shared RaceModeRules verdict so the garage's trial answer is
        // the same one the session seats.
        private static bool CreateTrial(SoloRaceRequest request,
            GameplayCatalog gameplay, DrivingContentCatalog content,
            string sessionId, string raceId,
            string participantId, string difficultyId,
            string rulesFingerprint, GadgetDefinitionCatalog gadgets,
            out SoloRaceSession session, out string error)
        {
            session = null;
            error = null;
            var decision = RaceModeRules.Evaluate(RaceMode.TimeTrial,
                request.courseId, request.eventId, request.classId,
                request.vehicleId, null, content, gameplay);
            if (!decision.allowed)
            { error = decision.code; return false; }
            var evt = content.Events[decision.eventId];
            var classId = decision.classId;
            CourseDefinition course = content.Courses[evt.courseId];
            var starterId = decision.vehicleId;
            var tuning = content.Vehicles[starterId];

            // The lone trial session owns its gadget world when the
            // course authors pickup sites — same authoritative rules,
            // roster of one.
            var trialSession = new DrivingSession(raceId, tuning, course,
                evt, content.Version, request.commandId, null,
                participantId, content.CompatibilityFingerprint,
                content.SimulationBackend, gadgets);
            var world = new TrialWorld
            {
                session = trialSession, evt = evt, classId = classId ?? ""
            };
            session = new SoloRaceSession(request, "timeTrial", sessionId,
                raceId, participantId, difficultyId, rulesFingerprint,
                content, null, world, new RaceMetricsAccumulator());
            return true;
        }

        // The roster the owning simulation initializes with: the shared
        // coordinator's four spawns, or the trial's single spawn.
        public List<VehicleSpawn> VehicleSpawns()
        {
            if (race != null) return race.VehicleSpawns();
            var spawns = new List<VehicleSpawn>(1);
            VehicleTuning tuning;
            if (trial != null
                && content.Vehicles.TryGetValue(trial.session.VehicleId,
                    out tuning))
            {
                spawns.Add(new VehicleSpawn
                {
                    participantId = trial.session.ParticipantId,
                    tuning = tuning,
                    initialState = trial.session.Snapshot().motor
                });
            }
            return spawns;
        }

        // One 30 Hz boundary under the session's own clock. Four-racer
        // modes forward the local input into the shared coordinator
        // (AI seats sample the same pre-step snapshot as LAN); the
        // trial runs the same prepare/batch/complete seam with a batch
        // of one. Paused or finalized sessions republish unchanged and
        // never touch the simulation or the metric counters.
        public SoloRaceState Step(DriverInput input,
            IVehicleSimulation simulation)
        {
            var emitted = new List<DrivingEvent>();
            if (paused || abortReason.Length > 0)
                return Build(emitted);

            if (race != null)
            {
                var inputs = new Dictionary<string, DriverInput>(
                    StringComparer.Ordinal)
                {
                    { participantId, input }
                };
                var state = race.Step(inputs, simulation);
                emitted.AddRange(state.events);
                if (race.Result != null && result == null)
                    result = race.Result;
            }
            else
            {
                // No simulation provider: republish unchanged rather
                // than completing against a missing result — the
                // session is observable but cannot advance, never a
                // fabricated error per tick. Staged events still drain.
                if (simulation == null)
                {
                    emitted.AddRange(trial.session.DrainEvents());
                    return Build(emitted);
                }
                var request = trial.session.PrepareStep(input);
                DrivingStepResult completed;
                if (request == null)
                {
                    // Finalized: no motion, but the boundary still drains
                    // any staged events (same contract as the service's
                    // practice path).
                    completed = trial.session.CompleteStep(null);
                }
                else
                {
                    hostTick++;
                    if (request.reset != null)
                        simulation.Restore(request.reset.participantId,
                            request.reset.state);
                    VehicleTickResult tickResult = null;
                    var results = simulation.Step(new[] { request });
                    if (results != null)
                        foreach (var candidate in results)
                            if (candidate != null
                                && candidate.participantId
                                    == trial.session.ParticipantId)
                                tickResult = candidate;
                    completed = trial.session.CompleteStep(tickResult);
                    if (completed.reset != null)
                        simulation.Restore(completed.reset.participantId,
                            completed.reset.state);
                }
                emitted.AddRange(completed.events);
                var snap = completed.state;
                var finished = snap != null && snap.finished;
                // Metric facts count at completed simulated boundaries
                // only — a finished session's drain boundary can never
                // move the counters.
                if (request != null)
                {
                    trialMetrics.RecordBoundary(hostTick, participantId,
                        "human",
                        trial.session.Phase == DrivingSession.PhaseRunning,
                        snap, completed.events);
                    trialMetrics.UpdateStandards(participantId, 1,
                        finished ? "finished" : "",
                        finished ? snap.finishTicks : 0,
                        snap != null ? snap.elapsedTicks : 0);
                }
                if (finished && result == null)
                {
                    var rows = trialMetrics.Snapshot();
                    result = RaceResultFactory.FromTrial(sessionId, raceId,
                        trial.evt.id, content.Version, mode,
                        rulesFingerprint, participantId,
                        trial.session.VehicleId, hostTick,
                        snap.elapsedTicks, true, snap.finishTicks,
                        snap.lap, rows.Count > 0 ? rows[0] : null,
                        trial.evt.courseId, trial.classId);
                }
            }
            return Build(emitted);
        }

        // The solo pause contract: entering pause holds the session's own
        // clock — the coordinator's shared pause for four-racer modes,
        // a step gate for the trial. Elapsed and objective ticks cannot
        // advance while paused because no boundary ever reaches the
        // simulation or the accumulator.
        public void SetPaused(bool value)
        {
            if (value == paused) return;
            paused = value;
            if (race != null)
            {
                race.SetPaused(value);
            }
            else if (value && trial != null)
            {
                // Same lifecycle policy the coordinator applies: queued
                // one-shots cancel on the pause boundary.
                trial.session.CancelPending("paused");
            }
        }

        // Ends the session without a rewardable result — idempotent and
        // mirroring the coordinator's abort contract: latched actions
        // cancel with the reason and one race.aborted event emits.
        public void Abort(string reason)
        {
            if (abortReason.Length > 0 || result != null) return;
            abortReason = reason ?? "";
            if (race != null)
            {
                race.Abort(reason);
            }
            else if (trial != null)
            {
                trial.session.CancelPending(reason);
                trial.session.EmitLifecycle("race.aborted", "", reason ?? "");
            }
        }

        // Independent snapshot for state-stream consumers: the current
        // state plus NO new events (events only publish through Step or
        // command drains, like the coordinator's own contract).
        public SoloRaceState Snapshot()
        {
            return Build(new List<DrivingEvent>());
        }

        // Drains events a command-time mutation staged (pause, abort) so
        // the owner publishes them on the same boundary. The
        // coordinator's staged queue drains through one non-advancing
        // boundary — a Step with no simulation returns immediately after
        // draining — while the trial drains its session directly.
        public List<DrivingEvent> DrainStagedEvents()
        {
            var drained = new List<DrivingEvent>();
            if (race != null)
            {
                var state = race.Step(null, (IVehicleSimulation)null);
                if (state.events != null) drained.AddRange(state.events);
            }
            else if (trial != null)
            {
                drained.AddRange(trial.session.DrainEvents());
            }
            return drained;
        }

        // Routes a one-shot action (e.g. "recover") to the local human's
        // own pending queue — the same latching semantics the practice
        // command path gets.
        public bool QueuePending(string kind, string commandId, out string error)
        {
            if (race != null)
                return race.QueuePending(participantId, kind, commandId,
                    out error);
            if (trial != null)
                return trial.session.QueuePending(kind, commandId, out error);
            error = "unavailable";
            return false;
        }

        // Lifecycle cancellation for the local human's latched one-shots
        // (input.clear, focus loss): produces ordered cancelled events,
        // same as the solo session policy.
        public void CancelPending(string reason)
        {
            if (race != null)
                race.CancelPending(participantId, reason);
            else if (trial != null)
                trial.session.CancelPending(reason);
        }

        private SoloRaceState Build(List<DrivingEvent> emitted)
        {
            var state = new SoloRaceState
            {
                mode = mode,
                sessionId = sessionId,
                raceId = raceId,
                participantId = participantId,
                paused = paused,
                difficultyId = difficultyId,
                // The human seat's tuning provenance: progression modes
                // run the profile-resolved purchased tune; the trial
                // runs the authored normalized starter.
                tuningSource = mode == "timeTrial"
                    ? "normalized"
                    : mode == "campaign" || mode == "quickRace"
                        ? "purchased" : "",
                result = result != null ? result.Copy() : null,
                abortReason = abortReason
            };
            if (race != null)
            {
                var raceState = race.Snapshot();
                state.race = raceState;
                state.phase = raceState.phase;
                state.paused = state.paused || raceState.paused;
                foreach (var metric in race.Metrics())
                    state.metrics.Add(metric);
            }
            else if (trial != null)
            {
                var snap = trial.session.Snapshot();
                state.trial = snap;
                state.phase = snap != null ? snap.phase : "";
                foreach (var metric in trialMetrics.Snapshot())
                    state.metrics.Add(metric);
            }
            if (emitted != null)
                foreach (var ev in emitted)
                    state.events.Add(ev != null ? ev.Copy() : null);
            return state;
        }
    }
}
