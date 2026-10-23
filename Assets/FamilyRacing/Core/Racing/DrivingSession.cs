using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;

namespace FamilyRacing.Core.Racing
{
    // Versioned registry of driving content: the vehicle tunings, course
    // and event definitions a run may start. Runtime content assets build
    // this once; the service refuses race.start without it so observation
    // never reports phantom content.
    public sealed class DrivingContentCatalog
    {
        public string Version { get; private set; }
        public IReadOnlyDictionary<string, VehicleTuning> Vehicles { get; private set; }
        public IReadOnlyDictionary<string, CourseDefinition> Courses { get; private set; }
        public IReadOnlyDictionary<string, EventDefinition> Events { get; private set; }
        // The race-level simulation backend the runtime instantiates once
        // per race. Arcade keeps every legacy behaviour; PhysicalAssisted
        // requires seated vehicles to carry a validated physics tuning.
        public VehicleSimulationBackend SimulationBackend { get; private set; }

        // The compatibility token the session layer compares on join:
        // identical authored version AND identical backend are required
        // for compatibility. Arcade catalogs keep the bare version string
        // so existing wire fixtures and records stay byte-identical; a
        // non-arcade backend folds into the token so an arcade build can
        // never silently join a physical race.
        public string CompatibilityVersion
        {
            get
            {
                return SimulationBackend == VehicleSimulationBackend.Arcade
                    ? Version
                    : Version + "|sim=" + (int)SimulationBackend;
            }
        }

        // Task 9: the deterministic canonical fingerprint of the catalog's
        // tuned data — every vehicle/course/event value that can change a
        // simulation outcome — hashed to a 64-hex token. Unlike
        // CompatibilityVersion (the human-readable identity a designer
        // bumps deliberately), the fingerprint detects ANY canonical
        // delta — a drifting tuning value fences two builds before a
        // slot is dealt. Computed on read so post-load tuning edits are
        // always bound; version strings and dictionary order are not.
        public string CompatibilityFingerprint
        {
            get { return ContentFingerprint.Compute(this); }
        }

        public static DrivingContentCatalog Create(string version,
            IEnumerable<VehicleTuning> vehicles,
            IEnumerable<CourseDefinition> courses,
            IEnumerable<EventDefinition> events,
            VehicleSimulationBackend backend = VehicleSimulationBackend.Arcade)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("Content version must not be empty.", "version");
            if (!Enum.IsDefined(typeof(VehicleSimulationBackend), backend))
                throw new ArgumentException("Invalid simulation backend: " + backend);
            var vehicleMap = new Dictionary<string, VehicleTuning>(StringComparer.Ordinal);
            if (vehicles != null)
                foreach (var tuning in vehicles)
                {
                    if (tuning == null)
                        throw new ArgumentException("Invalid vehicle tuning: null");
                    if (!tuning.Validate(out var tuningError))
                        throw new ArgumentException("Invalid vehicle tuning: " + tuningError);
                    if (vehicleMap.ContainsKey(tuning.vehicleId))
                        throw new ArgumentException("Duplicate vehicle id: " + tuning.vehicleId);
                    vehicleMap[tuning.vehicleId] = tuning;
                }
            var courseMap = new Dictionary<string, CourseDefinition>(StringComparer.Ordinal);
            if (courses != null)
                foreach (var course in courses)
                {
                    if (course == null)
                        throw new ArgumentException("Invalid course definition: null");
                    if (!course.Validate(out var courseError))
                        throw new ArgumentException("Invalid course definition: " + courseError);
                    if (courseMap.ContainsKey(course.id))
                        throw new ArgumentException("Duplicate course id: " + course.id);
                    courseMap[course.id] = course;
                }
            var eventMap = new Dictionary<string, EventDefinition>(StringComparer.Ordinal);
            if (events != null)
                foreach (var evt in events)
                {
                    if (evt == null)
                        throw new ArgumentException("Invalid event definition: null");
                    if (!evt.Validate(out var eventError))
                        throw new ArgumentException("Invalid event definition: " + eventError);
                    if (eventMap.ContainsKey(evt.id))
                        throw new ArgumentException("Duplicate event id: " + evt.id);
                    if (!courseMap.ContainsKey(evt.courseId))
                        throw new ArgumentException("Event " + evt.id + " references unknown course " + evt.courseId);
                    if (courseMap[evt.courseId].isOpenRoute && evt.laps != 1)
                        throw new ArgumentException("Open route events require one traversal: " + evt.id);
                    foreach (var vehicleId in evt.vehicleIds)
                        if (!vehicleMap.ContainsKey(vehicleId))
                            throw new ArgumentException(
                                "Event " + evt.id + " references unknown vehicle " + vehicleId);
                    eventMap[evt.id] = evt;
                }
            if (vehicleMap.Count == 0 || courseMap.Count == 0 || eventMap.Count == 0)
                throw new ArgumentException("Driving content requires vehicles, a course and events.");
            return new DrivingContentCatalog
            {
                Version = version,
                Vehicles = vehicleMap,
                Courses = courseMap,
                Events = eventMap,
                SimulationBackend = backend
            };
        }
    }

    // One fixed-step driving session: owns the selected vehicle/event, the
    // phase machine, the simulation tick (which freezes under pause because
    // the service simply stops calling Step), the elapsed race clock (which
    // only runs in the running phase) and the bounded pending-action queue.
    // Step() advances exactly one 1/30-second boundary; Snapshot() returns
    // an independent deep copy; every emitted event is drained exactly once.
    public sealed class DrivingSession
    {
        public const int PendingCapacity = 4;
        public const string PhaseCountdown = "countdown";
        public const string PhaseRunning = "running";
        public const string PhaseFinished = "finished";
        public const float StepSeconds = 1f / 30f;

        private sealed class PendingAction
        {
            internal string kind;
            internal string commandId;
            internal long enqueuedTick;
        }

        private readonly string raceId;
        private readonly string vehicleId;
        private readonly VehicleTuning tuning;
        private readonly CourseDefinition course;
        private readonly EventDefinition evt;
        private readonly string contentVersion;
        // Task 11 observability: the canonical tuned-content fingerprint
        // this session was created under plus the race-level backend the
        // coordinator selected — both published verbatim on the session
        // state so a stream consumer binds physics evidence to a tuning
        // identity without resolving the catalog itself.
        private readonly string contentFingerprint;
        private readonly VehicleSimulationBackend raceBackend;
        private readonly List<PendingAction> pending = new List<PendingAction>();
        private readonly List<DrivingEvent> staged = new List<DrivingEvent>();

        private MotorState motor;
        private DriverInput lastInput;
        private string phase = PhaseCountdown;
        private int countdownRemaining;
        private long simulationTick;
        private long elapsedTicks;
        private long eventSequence;
        private CheckpointState progress;
        private MotorPose lastSafePose;
        private readonly RecoveryRules recovery = new RecoveryRules();
        private long finishTicks;
        private readonly string participantId;
        // The request this session prepared for the boundary currently in
        // flight — null between boundaries. CompleteStep consumes it.
        private VehicleTickRequest inFlightRequest;
        // Task 11: the three-sample path of the most recent completed
        // interval (published verbatim on the snapshot) and the reason
        // of the most recent applied recovery.
        private VehiclePathSample[] lastPath;
        private string lastRecoveryReason = "";
        // M5 Task 5: the session-owned authoritative gadget world for
        // solo paths (practice/tutorial free-drive and time trial). A
        // coordinator-owned session (NetworkRace) passes a null catalog
        // — the shared world lives on the coordinator so it can never
        // diverge across the roster.
        private readonly GadgetDefinitionCatalog gadgetCatalog;
        private GadgetWorldState gadgetWorld;

        // `startPose` lets an authoritative coordinator seat this racer at
        // an authored grid slot instead of the course's solo spawn; solo
        // callers omit it and get the course spawn unchanged. `participantId`
        // is the simulation roster identity the coordinator binds requests
        // and resets to; solo callers omit it and the session answers to its
        // own raceId.
        // `contentFingerprint` and `raceBackend` are pure observability:
        // they pin the session's published state to the catalog identity
        // and race-level backend selection without changing behaviour.
        // Callers that omit them publish "" and the tuning's declared
        // backend — the same effective backend every existing fixture
        // already selected.
        // `gadgets` seats the session-owned gadget world when the course
        // authors pickup sites — coordinator-owned sessions pass null and
        // consume the shared world through PendingModifiers instead.
        public DrivingSession(string raceId, VehicleTuning tuning, CourseDefinition course,
            EventDefinition evt, string contentVersion, string startCommandId,
            MotorPose? startPose = null, string participantId = null,
            string contentFingerprint = null,
            VehicleSimulationBackend? raceBackend = null,
            GadgetDefinitionCatalog gadgets = null)
        {
            if (string.IsNullOrEmpty(raceId)) throw new ArgumentException("raceId", "raceId");
            if (tuning == null || course == null || evt == null)
                throw new ArgumentNullException("session definitions");
            if (!tuning.Validate(out var tuningError))
                throw new ArgumentException("Invalid tuning: " + tuningError);
            if (!course.Validate(out var courseError))
                throw new ArgumentException("Invalid course: " + courseError);
            if (!evt.Validate(out var eventError))
                throw new ArgumentException("Invalid event: " + eventError);
            if (course.isOpenRoute && evt.laps != 1)
                throw new ArgumentException("Open route events require one traversal.");
            var spawn = startPose ?? new MotorPose
            {
                x = course.spawnX,
                y = course.spawnY,
                z = course.spawnZ,
                heading = course.spawnHeading
            };
            if (!Finite(spawn.x) || !Finite(spawn.y) || !Finite(spawn.z)
                || !Finite(spawn.heading))
                throw new ArgumentException("Invalid start pose.");
            this.raceId = raceId;
            this.participantId = string.IsNullOrEmpty(participantId)
                ? raceId : participantId;
            vehicleId = tuning.vehicleId;
            this.tuning = tuning;
            this.course = course;
            this.evt = evt;
            this.contentVersion = contentVersion ?? "";
            this.contentFingerprint = contentFingerprint ?? "";
            this.raceBackend = raceBackend ?? tuning.simulationBackend;
            countdownRemaining = evt.countdownTicks;
            if (countdownRemaining <= 0) phase = PhaseRunning;
            // Authored poses carry surface height; the physical backend
            // spawns the body its own settled lift above the surface so
            // one authored pose serves every vehicle class. The legacy
            // envelope's y is the body centre for this backend.
            var physical = tuning.simulationBackend
                == VehicleSimulationBackend.PhysicalAssisted
                && tuning.physics != null;
            motor = new MotorState
            {
                x = spawn.x,
                y = spawn.y + (physical ? tuning.physics.SettledLift() : 0f),
                z = spawn.z,
                heading = spawn.heading,
                grounded = true
            };
            progress = CheckpointProgress.Create(course, evt.laps);
            // With no accepted gate the safe respawn is the spawn itself.
            lastSafePose = spawn;
            // A session-owned gadget world exists only when the catalog
            // supplies definitions AND the course authors pickup sites —
            // otherwise the state stays absent (never an empty shell that
            // would masquerade as an enabled-but-dead feature).
            gadgetCatalog = gadgets;
            if (gadgets != null && gadgets.gadgets != null
                && gadgets.gadgets.Count > 0
                && course.pickupSites != null
                && course.pickupSites.Count > 0)
            {
                gadgetWorld = GadgetRules.Create(gadgets,
                    course.pickupSites,
                    GadgetRules.StableSeed(raceId),
                    new[] { this.participantId }, raceId);
            }
            Emit("race.started", startCommandId ?? "", evt.id);
            // A countdown-free event goes straight to running: emit the
            // transition here or consumers waiting on race.running hang.
            if (countdownRemaining <= 0) Emit("race.running", "", "");
        }

        public string RaceId { get { return raceId; } }
        public string VehicleId { get { return vehicleId; } }
        public string EventId { get { return evt.id; } }
        public string Phase { get { return phase; } }
        public long SimulationTick { get { return simulationTick; } }
        public string ParticipantId { get { return participantId; } }

        // The coordinator's per-tick modifier for the next prepared
        // request (M5 Task 5): the shared gadget world's row for this
        // racer, set before PrepareStep. A session-owned world falls back
        // to its own row; null means neutral either way.
        public MotorModifiers PendingModifiers;

        // The session-owned gadget world for observers (practice,
        // tutorial, time trial). Null when the coordinator owns the
        // shared world — racers read their row off NetworkRaceState
        // instead. The returned object is the live state: callers treat
        // it read-only.
        public GadgetWorldState GadgetWorld { get { return gadgetWorld; } }

        // Occupancy probe the coordinator installs: answers true when
        // another racer's committed body blocks a candidate respawn
        // pose. Null means "no peers" (solo/lab) — every candidate
        // reads clear.
        public Func<MotorPose, bool> PoseOccupied;

        // Latch a one-shot action until a simulated boundary consumes it.
        // Rejecting returns an error code and changes nothing — callers rely
        // on that to refuse before mutating held input.
        public bool QueuePending(string kind, string commandId, out string error)
        {
            error = null;
            if (kind != "recover")
            {
                error = "unavailable";
                return false;
            }
            foreach (var entry in pending)
                if (entry.commandId == commandId)
                    return true; // identical delivery already latched
            if (pending.Count >= PendingCapacity)
            {
                error = "pendingQueueFull";
                return false;
            }
            pending.Add(new PendingAction
            {
                kind = kind,
                commandId = commandId,
                enqueuedTick = simulationTick
            });
            Emit("action.queued", commandId ?? "", kind);
            return true;
        }

        // Lifecycle cancellation (pause, exit, profile switch, input clear):
        // every latched action produces an ordered cancelled event.
        public int CancelPending(string reason)
        {
            var cancelled = pending.Count;
            foreach (var entry in pending)
                Emit("action.cancelled", entry.commandId, reason ?? "");
            pending.Clear();
            return cancelled;
        }

        // Command-time lifecycle events (race ended/exited) with command
        // correlation; staged so the owning service can publish them on the
        // same command boundary.
        public void EmitLifecycle(string kind, string commandId, string detail)
        {
            Emit(kind, commandId ?? "", detail ?? "");
        }

        // Events are produced once and consumed once: the service drains
        // after every Step and after command-time emission.
        public List<DrivingEvent> DrainEvents()
        {
            var drained = new List<DrivingEvent>(staged);
            staged.Clear();
            return drained;
        }

        // --- Task 4 simulation/rule boundary --------------------------------
        // One boundary is now two explicit phases so a coordinator can
        // prepare EVERY racer, run ONE shared simulation batch, then
        // complete every rule in slot order:
        //   PrepareStep  — rule bookkeeping only: held input, pending
        //     action consumption, countdown/elapsed clocks, manual
        //     recovery. Produces the batch request; a manual reset rides
        //     out on request.reset for the coordinator to apply to the
        //     body BEFORE the batch runs.
        //   CompleteStep — rule completion from the returned result:
        //     motor commit, motor/progress/finish events, per-segment
        //     gate evaluation over the chronological substep path,
        //     automatic recovery. A rule-requested recovery rides out on
        //     result.reset for the coordinator to apply BEFORE the state
        //     is published.
        // A finished session prepares nothing (null) — the coordinator
        // simply does not include it in the batch.

        public VehicleTickRequest PrepareStep(DriverInput input)
        {
            lastInput = input;
            inFlightRequest = null;
            if (phase == PhaseFinished) return null;

            simulationTick++;
            var consumed = ConsumePendingOnce();
            var request = new VehicleTickRequest
            {
                participantId = participantId,
                input = input,
                simulateMotion = false
            };

            if (phase == PhaseCountdown)
            {
                if (consumed != null && consumed.kind == "recover")
                    ApplyRecovery("manual", consumed.commandId, request);
                countdownRemaining--;
                if (countdownRemaining <= 0)
                {
                    phase = PhaseRunning;
                    Emit("race.running", "", "");
                }
            }
            else if (phase == PhaseRunning)
            {
                elapsedTicks++;
                if (consumed != null && consumed.kind == "recover")
                {
                    // Manual recovery replaces the pose on this boundary;
                    // the body stays frozen and no progress evaluation
                    // runs, so the teleport can never manufacture a
                    // crossing. The reset travels on the request so the
                    // coordinator lands it on the body before physics.
                    ApplyRecovery("manual", consumed.commandId, request);
                }
                else
                {
                    request.simulateMotion = true;
                }
            }

            // The rule facts the simulation merges from — exactly the
            // pre-step motor state the old inline step consumed.
            request.rulesBefore = motor.Copy();
            // M5 Task 5: per-tick gadget modifiers ride the batch seam —
            // the coordinator's shared world when present, else the
            // session-owned world's row. Neutral when absent.
            request.modifiers = PendingModifiers != null
                ? PendingModifiers
                : GadgetRules.ModifierFor(gadgetWorld, participantId);
            inFlightRequest = request;
            return request;
        }

        public DrivingStepResult CompleteStep(VehicleTickResult result)
        {
            var output = new DrivingStepResult();
            var request = inFlightRequest;
            inFlightRequest = null;
            var step = result != null ? result.step : null;
            // The last completed interval's ordered substep path — the
            // within-tick motion evidence the snapshot publishes
            // verbatim (null until the first stepped boundary).
            if (result != null) lastPath = result.path;

            if (phase == PhaseFinished && request == null)
            {
                // Nothing was prepared for a finished session — the
                // coordinator calls CompleteStep(null) so staged events
                // still drain on the boundary.
            }
            else if (request == null)
            {
                Emit("motor.error", "", "unpreparedComplete");
            }
            else if (step == null || step.state == null)
            {
                Emit("motor.error", "", "missingResult");
            }
            else if (step.rejected)
            {
                // A rejected result leaves the motor state untouched.
                Emit("motor.error", "", step.error ?? "rejected");
            }
            else if (!request.simulateMotion)
            {
                // Frozen boundary (countdown or a pending-reset tick):
                // the rule side already committed everything it owns in
                // PrepareStep; the body's pose was restored by the
                // coordinator and its reported state is not a movement.
            }
            else
            {
                // The respawn boundary is flagged: its movement is
                // excluded from progress and landing evaluation, and the
                // flag clears when this step commits.
                var recoveryFlagged = motor.recovering;
                motor = step.state;
                motor.tick = simulationTick;
                motor.recovering = false;
                var physical = tuning.simulationBackend
                    == VehicleSimulationBackend.PhysicalAssisted;
                // The physical backend owns motion; the rule layer owns
                // the drift/boost economy. A recovery-flagged boundary
                // grants nothing, exactly like the arcade flagged step.
                if (physical && !recoveryFlagged)
                    AdvancePhysicalEconomy(request.input, step);
                foreach (var motorEvent in step.events)
                {
                    var detail = motorEvent.value != 0f
                        ? motorEvent.value.ToString("0.###",
                            System.Globalization.CultureInfo.InvariantCulture)
                        : "";
                    Emit("motor." + motorEvent.kind, "", detail);
                }
                if (!recoveryFlagged)
                {
                    // Gate crossings evaluate on the two chronological
                    // substep segments — never the end-to-end chord, so
                    // a reset boundary (absent here by construction) or a
                    // mid-interval excursion can never be flattened away
                    // or manufacture a crossing.
                    var poses = SegmentPoses(request, result);
                    for (var i = 0; i + 1 < poses.Count && !progress.finished; i++)
                    {
                        var advance = CheckpointProgress.Advance(
                            course, progress, poses[i], poses[i + 1]);
                        foreach (var progressEvent in advance.events)
                        {
                            if (progressEvent.kind == "checkpoint")
                                Emit("checkpoint.crossed", "", progressEvent.gateId ?? "");
                            else if (progressEvent.kind == "lap")
                                Emit("race.lap", "", progressEvent.lap.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture));
                        }
                        if (advance.recoveryPoseUpdated)
                            lastSafePose = advance.recoveryPose;
                    }
                }
                if (progress.finished)
                {
                    phase = PhaseFinished;
                    finishTicks = elapsedTicks;
                    Emit("race.finished", "", elapsedTicks.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    // The physical evaluator reads real contact episodes,
                    // authored bounds and driver demand; the arcade
                    // overload is preserved for the legacy backend.
                    var reason = physical
                        ? recovery.Evaluate(motor, request.input, course,
                            evt.recovery, StepSeconds)
                        : recovery.Evaluate(motor, evt.recovery,
                            StepSeconds);
                    if (reason != null)
                    {
                        // Rule-requested recovery: the session commits the
                        // reset pose now and asks the coordinator to land
                        // the identical state on the body before the
                        // boundary publishes — snapshots always show the
                        // post-reset pose, never the fallen one.
                        ApplyRecovery(reason, "", null);
                        output.reset = new VehicleReset
                        {
                            participantId = participantId,
                            state = motor.Copy()
                        };
                    }
                }
            }

            // M5 Task 5 session-owned gadget step: a solo session runs the
            // authoritative rules itself (a coordinator-owned session
            // leaves this world null — the shared world steps once for the
            // whole roster). The boundary's swept segments, edge latch,
            // recovery flag and committed pose feed exactly one
            // deterministic step; pause freezes it because no boundary
            // reaches CompleteStep at all.
            if (gadgetWorld != null && request != null)
            {
                var input = new GadgetStepInput { tick = simulationTick };
                var racer = new GadgetRacerInput
                {
                    participantId = participantId,
                    slot = 0,
                    gadgetHeld = request.input.Gadget,
                    // A frozen boundary (countdown or a pending reset)
                    // still ticks clocks but can never collect or hit —
                    // same rule the coordinator applies to a respawn.
                    recovering = !request.simulateMotion
                        || request.reset != null || output.reset != null,
                    finished = phase == PhaseFinished,
                    x = motor.x,
                    y = motor.y,
                    z = motor.z
                };
                var poses = SegmentPoses(request, result);
                for (var i = 0; i + 1 < poses.Count; i++)
                {
                    var a = poses[i];
                    var b = poses[i + 1];
                    racer.segments.Add(new GadgetSegment
                    {
                        ax = a.x, ay = a.y, az = a.z,
                        bx = b.x, by = b.y, bz = b.z
                    });
                }
                input.racers.Add(racer);
                var gadgetStep = GadgetRules.Step(input, gadgetWorld,
                    gadgetCatalog);
                if (gadgetStep.state != null)
                    gadgetWorld = gadgetStep.state;
                foreach (var gadgetEvent in gadgetStep.events)
                    EmitGadget(gadgetEvent);
            }

            output.state = Snapshot();
            output.events = DrainEvents();
            return output;
        }

        // The chronological pose list for crossing evaluation: the
        // simulation's three substep-boundary samples when supplied, or
        // the single pre-step -> committed segment otherwise.
        private List<MotorPose> SegmentPoses(VehicleTickRequest request,
            VehicleTickResult result)
        {
            var poses = new List<MotorPose>(4);
            var path = result != null ? result.path : null;
            if (path != null && path.Length >= 2)
            {
                for (var i = 0; i < path.Length; i++)
                    poses.Add(path[i].pose);
                return poses;
            }
            if (request != null && request.rulesBefore != null)
                poses.Add(request.rulesBefore.Pose);
            poses.Add(motor.Pose);
            return poses;
        }

        // Legacy inline-step adapter retained while callers migrate to the
        // simulation seam: runs the identical rule work through
        // PrepareStep/CompleteStep with the arcade motor stepped inline,
        // so observable behaviour (events, ticks, progress, recovery) is
        // unchanged. `racers` is the shared racer-contact seam: solo
        // callers omit it and ArcadeMotor steps with racers:null exactly
        // as before.
        public DrivingStepResult Step(DriverInput input, IMotorWorld world,
            IRacerContactSource racers = null)
        {
            lastInput = input;
            if (phase == PhaseFinished || world == null)
            {
                var idle = new DrivingStepResult();
                idle.state = Snapshot();
                idle.events = DrainEvents();
                return idle;
            }
            var request = PrepareStep(input);
            VehicleTickResult result;
            if (request.simulateMotion)
            {
                var step = ArcadeMotor.Step(request.rulesBefore, input,
                    tuning, world, StepSeconds, racers,
                    request.modifiers);
                var before = request.rulesBefore.Pose;
                result = new VehicleTickResult
                {
                    participantId = participantId,
                    step = step,
                    path = step != null && step.rejected
                        ? ArcadeVehicleSimulation.FixedPath(before)
                        : ArcadeVehicleSimulation.MovementPath(before,
                            step != null && step.state != null
                                ? step.state.Pose : before)
                };
            }
            else
            {
                result = new VehicleTickResult
                {
                    participantId = participantId,
                    step = new MotorStepResult { state = motor.Copy() },
                    path = ArcadeVehicleSimulation.FixedPath(motor.Pose)
                };
            }
            return CompleteStep(result);
        }

        // Drift/boost economy for the physical backend, mirroring
        // ArcadeMotor's committed-step semantics: the shared
        // DriftBoostRules decide flag engagement and charge flow; the
        // forces those flags request are demands the wheel solver bounds
        // next boundary. Physical-only differences: boost activation is
        // refused while braking or airborne (a demand that cannot
        // deliver must not consume the latch), and the clean-landing
        // credit arrives as a sim-emitted event the charge applies here.
        private void AdvancePhysicalEconomy(DriverInput input,
            MotorStepResult step)
        {
            var wantDrift = DriftBoostRules.DriftEligible(input, motor,
                tuning);
            if (wantDrift && !motor.drifting)
            {
                motor.drifting = true;
                motor.driftSign = input.Steering >= 0f ? 1 : -1;
                Emit("motor.driftStart", "", "");
            }
            else if (!wantDrift && motor.drifting)
            {
                motor.drifting = false;
                motor.driftSign = 0;
                Emit("motor.driftEnd", "", "");
            }
            if (!input.Boost) motor.boostLatched = false;
            if (DriftBoostRules.CanStartBoostPhysical(motor, input, tuning))
            {
                motor.boostActive = true;
                motor.boostLatched = true;
                motor.boostSecondsRemaining = tuning.boostDurationSeconds;
                Emit("motor.boostStart", "", "");
            }
            if (motor.boostActive)
            {
                motor.boostSecondsRemaining -= StepSeconds;
                motor.boostCharge = Clamp01(motor.boostCharge
                    - tuning.boostDrainPerSecond * StepSeconds);
                if (DriftBoostRules.MustEndBoost(motor, input))
                {
                    motor.boostActive = false;
                    motor.boostLatched = false;
                    motor.boostSecondsRemaining = 0f;
                    Emit("motor.boostEnd", "", "");
                }
            }
            else if (motor.boostCharge < 1f)
            {
                motor.boostCharge = Clamp01(motor.boostCharge
                    + DriftBoostRules.ChargeGained(motor, tuning,
                        StepSeconds));
            }
            // The physical sim emits cleanLanding with the tuning credit
            // as its value; recovery-flagged movement never reaches this
            // path, so a respawn can never mint a landing credit.
            if (step != null && step.events != null)
                foreach (var ev in step.events)
                    if (ev.kind == "cleanLanding" && ev.value > 0f)
                        motor.boostCharge = Clamp01(
                            motor.boostCharge + ev.value);
        }

        private static float Clamp01(float value)
        {
            return value < 0f ? 0f : value > 1f ? 1f : value;
        }

        // The respawn target: the last accepted safe pose unless another
        // racer's committed body occupies it, in which case the authored
        // course.safePoses candidates are tried in authored order and
        // the first clear one wins. Every-candidate-blocked falls back
        // to the primary pose — a respawn never strands the racer
        // mid-course.
        private MotorPose SelectRecoveryPose()
        {
            var probe = PoseOccupied;
            if (probe == null || !probe(lastSafePose)) return lastSafePose;
            if (course.safePoses != null)
                foreach (var candidate in course.safePoses)
                {
                    if (!Finite(candidate.x) || !Finite(candidate.y)
                        || !Finite(candidate.z) || !Finite(candidate.heading))
                        continue;
                    if (!probe(candidate)) return candidate;
                }
            return lastSafePose;
        }

        // The full controller reset a physical recovery carries: zero
        // linear/angular velocity, invalidated wheel contact/damper
        // history, cleared steering/roll/balance/drift histories and
        // episode clocks, upright yaw attitude at the safe heading, and
        // the bumped recovery epoch a stale replica can never undo.
        private MotorState BuildPhysicalResetState(MotorPose pose,
            float charge, long epoch)
        {
            var physics = new VehiclePhysicsState
            {
                wheels = new WheelPhysicsState[tuning.physics.wheels.Length],
                grounded = true,
                airPeakY = pose.y,
                lastContact = "ground",
                recoveryEpoch = epoch
            };
            for (var i = 0; i < physics.wheels.Length; i++)
                physics.wheels[i] = new WheelPhysicsState { index = i };
            var half = pose.heading * 0.5f;
            physics.TrySetAttitude(0f, (float)Math.Sin(half), 0f,
                (float)Math.Cos(half));
            return new MotorState
            {
                x = pose.x,
                // Authored respawn poses are surface height; restore the
                // body its settled lift above them (per-vehicle).
                y = pose.y + tuning.physics.SettledLift(),
                z = pose.z,
                heading = pose.heading,
                grounded = true,
                recovering = true,
                lastContact = "ground",
                boostCharge = charge,
                tick = simulationTick,
                physics = physics
            };
        }

        // Respawn at the last accepted safe pose (or the first clear
        // authored candidate when it is occupied): velocity and held
        // motion (drift, active boost) reset, the existing boost charge
        // is preserved (recovery never creates charge), the boundary is
        // flagged `recovering` so progress/landing evaluation skips it,
        // and the race clock keeps running via the elapsedTicks increment
        // the caller already applied. The physical backend additionally
        // resets every controller history and bumps the recovery epoch;
        // its event detail carries the epoch after the reason.
        private void ApplyRecovery(string reason, string commandId,
            VehicleTickRequest request)
        {
            lastRecoveryReason = reason ?? "";
            var charge = motor.boostCharge;
            var pose = SelectRecoveryPose();
            var detail = reason ?? "";
            if (tuning.simulationBackend
                    == VehicleSimulationBackend.PhysicalAssisted
                && tuning.physics != null && tuning.physics.wheels != null)
            {
                var epoch = (motor.physics != null
                    ? motor.physics.recoveryEpoch : 0L) + 1L;
                motor = BuildPhysicalResetState(pose, charge, epoch);
                detail = detail + "#" + epoch.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                motor = new MotorState
                {
                    x = pose.x,
                    y = pose.y,
                    z = pose.z,
                    heading = pose.heading,
                    grounded = true,
                    recovering = true,
                    lastContact = "ground",
                    boostCharge = charge,
                    tick = simulationTick
                };
            }
            recovery.Reset();
            if (request != null)
            {
                // The reset rides the prepared request: the coordinator
                // lands it on the body before the batch runs, so the
                // simulation never sees the pre-reset pose this boundary.
                request.reset = new VehicleReset
                {
                    participantId = participantId,
                    state = motor.Copy()
                };
            }
            Emit("race.recovered", commandId ?? "", detail);
        }

        // At most one pending action is consumed per simulated boundary.
        private PendingAction ConsumePendingOnce()
        {
            if (pending.Count == 0) return null;
            var entry = pending[0];
            pending.RemoveAt(0);
            Emit("action.consumed", entry.commandId, entry.kind);
            return entry;
        }

        public DrivingSessionState Snapshot()
        {
            var state = new DrivingSessionState
            {
                raceId = raceId,
                vehicleId = vehicleId,
                eventId = evt.id,
                courseId = course.id,
                contentVersion = contentVersion,
                phase = phase,
                countdownTicksRemaining = countdownRemaining,
                simulationTick = simulationTick,
                elapsedTicks = elapsedTicks,
                lap = progress.lap,
                lapsRequired = evt.laps,
                isOpenRoute = course.isOpenRoute,
                nextCheckpoint = progress.expectedCheckpoint,
                finished = phase == PhaseFinished,
                finishTicks = finishTicks,
                motor = motor.Copy(),
                input = new InputState
                {
                    steering = lastInput.Steering,
                    drift = lastInput.Drift,
                    boost = lastInput.Boost,
                    gadget = lastInput.Gadget,
                    recover = lastInput.Recover,
                    changeCamera = lastInput.ChangeCamera,
                    pause = lastInput.Pause,
                    brake = lastInput.Brake,
                    hasThrottleOverride = lastInput.HasThrottleOverride,
                    throttle = lastInput.Throttle
                },
                pendingActions = new List<PendingActionState>(),
                checkpoints = new List<CheckpointMark>(),
                camera = null,
                controls = new List<string>(),
                simulationBackend = raceBackend
                    == VehicleSimulationBackend.PhysicalAssisted
                    ? "physicalAssisted" : "arcade",
                contentFingerprint = contentFingerprint,
                lastRecoveryReason = lastRecoveryReason,
                gadgets = gadgetWorld != null ? gadgetWorld.Copy() : null
            };
            if (lastPath != null)
                state.substepPath.AddRange(lastPath);
            // Clamped progress toward the next expected gate on the current
            // legal segment — enough for an authoritative standings sort
            // without reproducing checkpoint sweeps.
            var gateCount = course.checkpoints != null ? course.checkpoints.Count : 0;
            if (gateCount > 0)
            {
                var prevIndex = (progress.expectedCheckpoint - 1 + gateCount) % gateCount;
                var prev = course.checkpoints[prevIndex];
                var next = course.checkpoints[progress.expectedCheckpoint];
                var segX = next.x - prev.x;
                var segZ = next.z - prev.z;
                var segLenSquared = segX * segX + segZ * segZ;
                if (segLenSquared > 0.0001f)
                {
                    var along = ((motor.x - prev.x) * segX
                        + (motor.z - prev.z) * segZ) / segLenSquared;
                    var clamped = along < 0f ? 0f : along > 1f ? 1f : along;
                    state.segmentProgressMeters = clamped
                        * (float)System.Math.Sqrt(segLenSquared);
                }
                state.acceptedGatesThisLap =
                    progress.expectedCheckpoint == 0
                        ? gateCount - 1
                        : progress.expectedCheckpoint - 1;
                if (course.isOpenRoute)
                {
                    float total = 0f, completed = 0f;
                    for (int i = 1; i < gateCount; i++)
                    {
                        var a = course.checkpoints[i - 1]; var b = course.checkpoints[i];
                        float dx = b.x - a.x, dz = b.z - a.z;
                        float length = (float)Math.Sqrt(dx * dx + dz * dz);
                        total += length;
                        if (i < progress.expectedCheckpoint) completed += length;
                    }
                    state.routeProgress = progress.finished ? 1f : total > 0f
                        ? Math.Clamp((completed + state.segmentProgressMeters) / total, 0f, 1f) : 0f;
                }
            }
            foreach (var entry in pending)
            {
                state.pendingActions.Add(new PendingActionState
                {
                    kind = entry.kind,
                    commandId = entry.commandId,
                    enqueuedTick = entry.enqueuedTick
                });
            }
            foreach (var mark in progress.marks)
                state.checkpoints.Add(mark.Copy());
            return state;
        }

        private static bool Finite(float v)
        {
            return !(float.IsNaN(v) || float.IsInfinity(v));
        }

        private void Emit(string kind, string commandId, string detail)
        {
            staged.Add(new DrivingEvent
            {
                id = raceId + "-e" + (++eventSequence),
                tick = simulationTick,
                kind = kind,
                commandId = commandId,
                detail = detail
            });
        }

        // A gadget event the session-owned rules produced rides the same
        // bounded staged stream with the session's participant identity
        // stamped — solo consumers see no difference from a coordinator
        // event, and the deterministic rules id survives for dedupe.
        private void EmitGadget(DrivingEvent ev)
        {
            if (ev == null) return;
            if (string.IsNullOrEmpty(ev.participantId))
                ev.participantId = ev.kind == GadgetRules.EventRespawn
                    ? "" : participantId;
            staged.Add(ev);
        }
    }
}
