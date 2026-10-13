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

        // `startPose` lets an authoritative coordinator seat this racer at
        // an authored grid slot instead of the course's solo spawn; solo
        // callers omit it and get the course spawn unchanged. `participantId`
        // is the simulation roster identity the coordinator binds requests
        // and resets to; solo callers omit it and the session answers to its
        // own raceId.
        public DrivingSession(string raceId, VehicleTuning tuning, CourseDefinition course,
            EventDefinition evt, string contentVersion, string startCommandId,
            MotorPose? startPose = null, string participantId = null)
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
            countdownRemaining = evt.countdownTicks;
            if (countdownRemaining <= 0) phase = PhaseRunning;
            motor = new MotorState
            {
                x = spawn.x,
                y = spawn.y,
                z = spawn.z,
                heading = spawn.heading,
                grounded = true
            };
            progress = CheckpointProgress.Create(course, evt.laps);
            // With no accepted gate the safe respawn is the spawn itself.
            lastSafePose = spawn;
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
            inFlightRequest = request;
            return request;
        }

        public DrivingStepResult CompleteStep(VehicleTickResult result)
        {
            var output = new DrivingStepResult();
            var request = inFlightRequest;
            inFlightRequest = null;
            var step = result != null ? result.step : null;

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
                    var reason = recovery.Evaluate(motor, evt.recovery, StepSeconds);
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
                    tuning, world, StepSeconds, racers);
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

        // Respawn at the last accepted safe pose: velocity and held motion
        // (drift, active boost) reset, the existing boost charge is
        // preserved (recovery never creates charge), the boundary is
        // flagged `recovering` so progress/landing evaluation skips it,
        // and the race clock keeps running via the elapsedTicks increment
        // the caller already applied.
        private void ApplyRecovery(string reason, string commandId,
            VehicleTickRequest request)
        {
            var charge = motor.boostCharge;
            motor = new MotorState
            {
                x = lastSafePose.x,
                y = lastSafePose.y,
                z = lastSafePose.z,
                heading = lastSafePose.heading,
                grounded = true,
                recovering = true,
                lastContact = "ground",
                boostCharge = charge,
                tick = simulationTick
            };
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
            Emit("race.recovered", commandId ?? "", reason ?? "");
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
                    pause = lastInput.Pause
                },
                pendingActions = new List<PendingActionState>(),
                checkpoints = new List<CheckpointMark>(),
                camera = null,
                controls = new List<string>()
            };
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
    }
}
