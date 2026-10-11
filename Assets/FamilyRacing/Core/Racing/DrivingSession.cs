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

        public static DrivingContentCatalog Create(string version,
            IEnumerable<VehicleTuning> vehicles,
            IEnumerable<CourseDefinition> courses,
            IEnumerable<EventDefinition> events)
        {
            if (string.IsNullOrEmpty(version))
                throw new ArgumentException("Content version must not be empty.", "version");
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
                Events = eventMap
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
        private readonly List<CheckpointState> checkpoints = new List<CheckpointState>();
        private readonly List<DrivingEvent> staged = new List<DrivingEvent>();

        private MotorState motor;
        private DriverInput lastInput;
        private string phase = PhaseCountdown;
        private int countdownRemaining;
        private long simulationTick;
        private long elapsedTicks;
        private long eventSequence;

        public DrivingSession(string raceId, VehicleTuning tuning, CourseDefinition course,
            EventDefinition evt, string contentVersion, string startCommandId)
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
            this.raceId = raceId;
            vehicleId = tuning.vehicleId;
            this.tuning = tuning;
            this.course = course;
            this.evt = evt;
            this.contentVersion = contentVersion ?? "";
            countdownRemaining = evt.countdownTicks;
            if (countdownRemaining <= 0) phase = PhaseRunning;
            motor = new MotorState
            {
                x = course.spawnX,
                y = course.spawnY,
                z = course.spawnZ,
                heading = course.spawnHeading,
                grounded = true
            };
            for (var i = 0; i < course.checkpoints.Count; i++)
            {
                checkpoints.Add(new CheckpointState
                {
                    id = course.checkpoints[i].id,
                    index = i,
                    crossed = false
                });
            }
            Emit("race.started", startCommandId ?? "", evt.id);
        }

        public string RaceId { get { return raceId; } }
        public string VehicleId { get { return vehicleId; } }
        public string EventId { get { return evt.id; } }
        public string Phase { get { return phase; } }
        public long SimulationTick { get { return simulationTick; } }

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

        public DrivingStepResult Step(DriverInput input, IMotorWorld world)
        {
            var result = new DrivingStepResult();
            lastInput = input;
            if (phase == PhaseFinished || world == null)
            {
                result.state = Snapshot();
                result.events = DrainEvents();
                return result;
            }

            simulationTick++;
            ConsumePendingOnce();

            if (phase == PhaseCountdown)
            {
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
                var step = ArcadeMotor.Step(motor, input, tuning, world, StepSeconds);
                if (step.rejected)
                {
                    // A rejected step leaves the motor state untouched.
                    Emit("motor.error", "", step.error ?? "rejected");
                }
                else
                {
                    motor = step.state;
                    motor.tick = simulationTick;
                    foreach (var motorEvent in step.events)
                    {
                        var detail = motorEvent.value != 0f
                            ? motorEvent.value.ToString("0.###",
                                System.Globalization.CultureInfo.InvariantCulture)
                            : "";
                        Emit("motor." + motorEvent.kind, "", detail);
                    }
                }
            }

            result.state = Snapshot();
            result.events = DrainEvents();
            return result;
        }

        // At most one pending action is consumed per simulated boundary.
        private void ConsumePendingOnce()
        {
            if (pending.Count == 0) return;
            var entry = pending[0];
            pending.RemoveAt(0);
            Emit("action.consumed", entry.commandId, entry.kind);
            // Actual recovery rules arrive in Task 2; consumption is the
            // observable Task-1 contract.
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
                lap = 0,
                lapsRequired = evt.laps,
                nextCheckpoint = 0,
                finished = phase == PhaseFinished,
                finishTicks = 0,
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
                checkpoints = new List<CheckpointState>(),
                camera = null,
                controls = new List<string>()
            };
            foreach (var entry in pending)
            {
                state.pendingActions.Add(new PendingActionState
                {
                    kind = entry.kind,
                    commandId = entry.commandId,
                    enqueuedTick = entry.enqueuedTick
                });
            }
            foreach (var gate in checkpoints)
                state.checkpoints.Add(gate.Copy());
            return state;
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
