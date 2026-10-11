using System;
using System.Collections.Generic;
using FamilyRacing.Core.Tutorial;
using UnityEngine;

namespace FamilyRacing
{
    // Authored onboarding content converted into a validated Core
    // TutorialDefinition: which event hosts the free-drive lessons, which
    // one-lap event is the graduation trial, the ordered lessons with
    // their completion rules, and the spoken-prompt clips for each cue.
    // The AudioClip slots take licensed local recordings only — no
    // runtime speech service; a null slot is an honest missing-audio
    // state the presenter reports, never a silent text-only substitute.
    [CreateAssetMenu(menuName = "FamilyRacing/Tutorial Definition",
        fileName = "tutorial-definition")]
    public sealed class TutorialDefinitionAsset : ScriptableObject
    {
        [Serializable]
        public sealed class Cue
        {
            [Tooltip("ShellText key for the shown prompt (EN/HE).")]
            public string textKey = "";
            [Tooltip("Short icon glyph shown beside the prompt.")]
            public string icon = "";
            [Tooltip("Bundled English recording; licensed local asset only.")]
            public AudioClip english;
            [Tooltip("Bundled Hebrew recording; licensed local asset only.")]
            public AudioClip hebrew;
        }

        [Serializable]
        public sealed class Lesson
        {
            public string lessonId = "";
            [Tooltip("event = count driving events; steerTicks = simulated " +
                "boundaries holding real steering input.")]
            public string rule = "event";
            public string eventKind = "";
            public int requiredCount = 1;
            public int requiredSteerTicks = 30;
            public float minSteering = 0.25f;
            public Cue cue = new Cue();
        }

        public string tutorialId = "tutorial-01";
        [Tooltip("Free-drive event the lessons run inside.")]
        public string freeDriveEventId = "tutorial-01";
        [Tooltip("One-lap event the completed lessons unlock as the trial.")]
        public string trialEventId = "intro-01";
        public Lesson[] lessons = new Lesson[0];
        public Cue trialCue = new Cue();
        public Cue finishedCue = new Cue();

        public TutorialDefinition ToDefinition(out string error)
        {
            var definition = new TutorialDefinition
            {
                tutorialId = tutorialId,
                freeDriveEventId = freeDriveEventId,
                trialEventId = trialEventId,
                lessons = new List<TutorialLesson>(),
                trialCue = ConvertCue(trialCue),
                finishedCue = ConvertCue(finishedCue)
            };
            if (lessons != null)
                foreach (var lesson in lessons)
                {
                    if (lesson == null) continue;
                    definition.lessons.Add(new TutorialLesson
                    {
                        lessonId = lesson.lessonId,
                        rule = lesson.rule,
                        eventKind = lesson.eventKind,
                        requiredCount = lesson.requiredCount,
                        requiredSteerTicks = lesson.requiredSteerTicks,
                        minSteering = lesson.minSteering,
                        cue = ConvertCue(lesson.cue)
                    });
                }
            if (!definition.Validate(out error))
            {
                error = name + ": invalid tutorial field " + error;
                return null;
            }
            error = null;
            return definition;
        }

        // The presenter resolves the actual bundled clip for a cue
        // identity (a lesson id, "trial" or "finished") and locale; null
        // is the honest missing-recording state.
        public AudioClip ClipFor(string cueId, string language)
        {
            var cue = FindCue(cueId);
            if (cue == null) return null;
            return language == "he" ? cue.hebrew : cue.english;
        }

        public Cue FindCue(string cueId)
        {
            if (cueId == "trial") return trialCue;
            if (cueId == "finished") return finishedCue;
            if (lessons != null)
                foreach (var lesson in lessons)
                    if (lesson != null && lesson.lessonId == cueId)
                        return lesson.cue;
            return null;
        }

        private static TutorialCue ConvertCue(Cue cue)
        {
            return new TutorialCue
            {
                textKey = cue != null ? cue.textKey : "",
                icon = cue != null ? cue.icon : "",
                audioEn = cue != null && cue.english != null ? cue.english.name : "",
                audioHe = cue != null && cue.hebrew != null ? cue.hebrew.name : ""
            };
        }
    }
}
