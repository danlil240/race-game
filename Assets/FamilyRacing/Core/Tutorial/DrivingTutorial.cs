using System;
using System.Collections.Generic;
using FamilyRacing.Core.Localization;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Tutorial
{
    // One localized lesson cue: the ShellText key for the spoken/shown
    // prompt, a presentation glyph and the bundled English/Hebrew clip
    // identities the presenter resolves. Clip names may be empty while
    // licensed recordings are pending; the cue identity still travels so
    // captures can see what a child was told.
    public sealed class TutorialCue
    {
        public string textKey = "";
        public string icon = "";
        public string audioEn = "";
        public string audioHe = "";

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(textKey)) { error = "textKey"; return false; }
            var known = false;
            foreach (var key in ShellText.Keys)
                if (key == textKey) { known = true; break; }
            if (!known) { error = "textKey:" + textKey; return false; }
            return true;
        }

        public TutorialCue Copy()
        {
            return new TutorialCue
            {
                textKey = textKey,
                icon = icon,
                audioEn = audioEn,
                audioHe = audioHe
            };
        }
    }

    // One ordered lesson. Completion is data-driven and always derives
    // from real simulation output — never from commands:
    //   "event"      — count driving events of `eventKind` while running;
    //   "steerTicks" — accumulate simulation boundaries that hold real
    //                  steering input at or above `minSteering`;
    //   "brakeTicks" — accumulate simulation boundaries that hold real
    //                  brake demand at or above `minBrake`.
    public sealed class TutorialLesson
    {
        public const string RuleEvent = "event";
        public const string RuleSteerTicks = "steerTicks";
        public const string RuleBrakeTicks = "brakeTicks";

        public string lessonId = "";
        public string rule = RuleEvent;
        public string eventKind = "";
        public int requiredCount = 1;
        public int requiredSteerTicks = 30;
        public float minSteering = 0.25f;
        public int requiredBrakeTicks = 20;
        public float minBrake = 0.5f;
        public TutorialCue cue = new TutorialCue();

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(lessonId)) { error = "lessonId"; return false; }
            if (rule != RuleEvent && rule != RuleSteerTicks
                && rule != RuleBrakeTicks) { error = "rule"; return false; }
            if (rule == RuleEvent)
            {
                if (string.IsNullOrEmpty(eventKind)) { error = "eventKind"; return false; }
                if (requiredCount < 1) { error = "requiredCount"; return false; }
            }
            else if (rule == RuleSteerTicks)
            {
                if (requiredSteerTicks < 1) { error = "requiredSteerTicks"; return false; }
                if (float.IsNaN(minSteering) || float.IsInfinity(minSteering)
                    || minSteering <= 0f || minSteering > 1f)
                { error = "minSteering"; return false; }
            }
            else
            {
                if (requiredBrakeTicks < 1) { error = "requiredBrakeTicks"; return false; }
                if (float.IsNaN(minBrake) || float.IsInfinity(minBrake)
                    || minBrake <= 0f || minBrake > 1f)
                { error = "minBrake"; return false; }
            }
            if (cue == null || !cue.Validate(out error)) { error = "cue." + error; return false; }
            return true;
        }

        public TutorialLesson Copy()
        {
            return new TutorialLesson
            {
                lessonId = lessonId,
                rule = rule,
                eventKind = eventKind,
                requiredCount = requiredCount,
                requiredSteerTicks = requiredSteerTicks,
                minSteering = minSteering,
                requiredBrakeTicks = requiredBrakeTicks,
                minBrake = minBrake,
                cue = cue == null ? null : cue.Copy()
            };
        }
    }

    // The authored tutorial: the free-drive event the lessons run inside,
    // the one-lap event the lessons unlock as the graduation trial, the
    // ordered lesson list and the trial/finish cues. Produced by
    // TutorialDefinitionAsset; validated before the service accepts it.
    public sealed class TutorialDefinition
    {
        public string tutorialId = "";
        public string freeDriveEventId = "";
        public string trialEventId = "";
        public List<TutorialLesson> lessons = new List<TutorialLesson>();
        public TutorialCue trialCue = new TutorialCue();
        public TutorialCue finishedCue = new TutorialCue();

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(tutorialId)) { error = "tutorialId"; return false; }
            if (string.IsNullOrEmpty(freeDriveEventId)) { error = "freeDriveEventId"; return false; }
            if (string.IsNullOrEmpty(trialEventId)) { error = "trialEventId"; return false; }
            if (lessons == null || lessons.Count == 0) { error = "lessons"; return false; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lesson in lessons)
            {
                if (lesson == null) { error = "lesson:null"; return false; }
                if (!lesson.Validate(out error)) { error = "lesson." + lesson.lessonId + "." + error; return false; }
                if (!seen.Add(lesson.lessonId)) { error = "lesson.duplicate:" + lesson.lessonId; return false; }
            }
            if (trialCue == null || !trialCue.Validate(out error)) { error = "trialCue." + error; return false; }
            if (finishedCue == null || !finishedCue.Validate(out error)) { error = "finishedCue." + error; return false; }
            return true;
        }

        public TutorialDefinition Copy()
        {
            var copy = new TutorialDefinition
            {
                tutorialId = tutorialId,
                freeDriveEventId = freeDriveEventId,
                trialEventId = trialEventId,
                lessons = new List<TutorialLesson>(),
                trialCue = trialCue == null ? null : trialCue.Copy(),
                finishedCue = finishedCue == null ? null : finishedCue.Copy()
            };
            if (lessons != null)
                foreach (var lesson in lessons)
                    copy.lessons.Add(lesson == null ? null : lesson.Copy());
            return copy;
        }
    }

    // Serializable per-boundary tutorial observation: phase, the current
    // lesson and its objective, completed lessons, and the cue identity +
    // sequence the presenter watches to (re)announce prompts.
    [Serializable]
    public sealed class TutorialState
    {
        public string tutorialId;
        public string phase;              // "lessons" | "trial" | "graduated"
        public string lessonId;           // current lesson during "lessons", else ""
        public int lessonIndex;
        public int lessonTotal;
        public List<string> completedLessons = new List<string>();
        public string objective;          // ShellText key of the current cue
        public string icon;
        public string audioEn;
        public string audioHe;
        public string cueId;              // lessonId | "trial" | "finished"
        public int cueSequence;           // +1 on every cue issue incl. replay
        public string trialEventId;
        public bool graduated;

        public TutorialState Copy()
        {
            return new TutorialState
            {
                tutorialId = tutorialId,
                phase = phase,
                lessonId = lessonId,
                lessonIndex = lessonIndex,
                lessonTotal = lessonTotal,
                completedLessons = new List<string>(completedLessons),
                objective = objective,
                icon = icon,
                audioEn = audioEn,
                audioHe = audioHe,
                cueId = cueId,
                cueSequence = cueSequence,
                trialEventId = trialEventId,
                graduated = graduated
            };
        }
    }

    // What one Advance call changed. The owning service turns these into
    // correlated tutorial.* driving events on the session's own stream.
    public sealed class TutorialAdvance
    {
        public string completedLessonId;  // lesson finished this boundary, else null
        public string newPhase;           // phase entered this boundary, else null
    }

    // Session-local ordered lesson tracker. It consumes only the real
    // DrivingSession state/events each simulated boundary: lessons are
    // strictly ordered (an event never banks for a later lesson), only
    // the running phase counts, `replay` re-issues the current cue
    // without any progression, and the one-lap trial graduates solely on
    // the session's own race.finished event. Nothing here persists —
    // progress lives and dies with the tutorial session.
    public sealed class DrivingTutorial
    {
        public const string PhaseLessons = "lessons";
        public const string PhaseTrial = "trial";
        public const string PhaseGraduated = "graduated";
        private const string TrialCueId = "trial";
        private const string FinishedCueId = "finished";

        private readonly TutorialDefinition definition;
        private string phase = PhaseLessons;
        private int lessonIndex;
        private int progress;
        private int cueSequence = 1;
        private long lastSimulationTick = -1;
        private readonly List<string> completed = new List<string>();

        public DrivingTutorial(TutorialDefinition definition)
        {
            if (definition == null)
                throw new ArgumentException("Invalid tutorial definition: null");
            if (!definition.Validate(out var error))
                throw new ArgumentException("Invalid tutorial definition: " + error);
            this.definition = definition.Copy();
        }

        public string Phase { get { return phase; } }
        public string FreeDriveEventId { get { return definition.freeDriveEventId; } }
        public string TrialEventId { get { return definition.trialEventId; } }
        public string CurrentLessonId
        {
            get
            {
                return phase == PhaseLessons ? definition.lessons[lessonIndex].lessonId : "";
            }
        }

        public TutorialAdvance Advance(DrivingSessionState state,
            IReadOnlyList<DrivingEvent> events)
        {
            var result = new TutorialAdvance();
            if (phase == PhaseGraduated) return result;

            if (phase == PhaseTrial)
            {
                // Graduation is the session's own finish: ordered gate
                // crossings produced it, never a command or teleport.
                if (events != null)
                    foreach (var ev in events)
                        if (ev != null && ev.kind == "race.finished")
                        {
                            phase = PhaseGraduated;
                            result.newPhase = phase;
                            IssueCue();
                            break;
                        }
                return result;
            }

            // Lessons count only real running-phase driving. Countdown,
            // finished and any other boundary cannot manufacture progress.
            var tickAdvanced = TickAdvanced(state);
            if (state == null || state.phase != "running") return result;

            var lesson = definition.lessons[lessonIndex];
            if (lesson.rule == TutorialLesson.RuleSteerTicks)
            {
                if (tickAdvanced && state.input != null
                    && Math.Abs(state.input.steering) >= lesson.minSteering)
                    progress++;
            }
            else if (lesson.rule == TutorialLesson.RuleBrakeTicks)
            {
                if (tickAdvanced && state.input != null
                    && state.input.brake >= lesson.minBrake)
                    progress++;
            }
            else if (events != null)
            {
                foreach (var ev in events)
                    if (ev != null && ev.kind == lesson.eventKind)
                        progress++;
            }

            var required = lesson.rule == TutorialLesson.RuleSteerTicks
                ? lesson.requiredSteerTicks
                : lesson.rule == TutorialLesson.RuleBrakeTicks
                    ? lesson.requiredBrakeTicks
                    : lesson.requiredCount;
            if (progress >= required)
            {
                result.completedLessonId = lesson.lessonId;
                completed.Add(lesson.lessonId);
                lessonIndex++;
                progress = 0;
                if (lessonIndex >= definition.lessons.Count)
                {
                    phase = PhaseTrial;
                    result.newPhase = phase;
                }
                IssueCue();
            }
            return result;
        }

        // Repeat the current cue for the child: same objective, same cue
        // identity, just a fresh announcement. Never advances a lesson.
        public void Replay()
        {
            cueSequence++;
        }

        // race.restart inside a tutorial keeps completed lessons and
        // re-issues the current cue on the fresh session; after
        // graduation a retry means running the trial again.
        public void Restart()
        {
            progress = 0;
            lastSimulationTick = -1;
            if (phase == PhaseGraduated) phase = PhaseTrial;
            cueSequence++;
        }

        public TutorialState Snapshot()
        {
            var cue = CurrentCue();
            var state = new TutorialState
            {
                tutorialId = definition.tutorialId,
                phase = phase,
                lessonIndex = phase == PhaseLessons ? lessonIndex : definition.lessons.Count,
                lessonTotal = definition.lessons.Count,
                cueSequence = cueSequence,
                trialEventId = definition.trialEventId,
                graduated = phase == PhaseGraduated
            };
            foreach (var id in completed) state.completedLessons.Add(id);
            if (cue != null)
            {
                state.objective = cue.textKey;
                state.icon = cue.icon;
                state.audioEn = cue.audioEn;
                state.audioHe = cue.audioHe;
            }
            if (phase == PhaseLessons)
            {
                state.lessonId = definition.lessons[lessonIndex].lessonId;
                state.cueId = state.lessonId;
            }
            else
            {
                state.lessonId = "";
                state.cueId = phase == PhaseTrial ? TrialCueId : FinishedCueId;
            }
            return state;
        }

        private TutorialCue CurrentCue()
        {
            if (phase == PhaseTrial) return definition.trialCue;
            if (phase == PhaseGraduated) return definition.finishedCue;
            return definition.lessons[lessonIndex].cue;
        }

        private void IssueCue()
        {
            cueSequence++;
        }

        // Steering progress is measured in simulated boundaries: count a
        // boundary at most once even if Advance is re-called on the same
        // tick snapshot.
        private bool TickAdvanced(DrivingSessionState state)
        {
            if (state == null) return false;
            var advanced = state.simulationTick != lastSimulationTick;
            lastSimulationTick = state.simulationTick;
            return advanced;
        }
    }
}
