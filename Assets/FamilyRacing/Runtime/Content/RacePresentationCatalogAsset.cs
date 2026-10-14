using System;
using UnityEngine;

namespace FamilyRacing
{
    // Race presentation catalog (M4 Task 5): the authored mapping from
    // stable cue/effect ids to bundled licensed/original assets, their
    // pooling/voice bounds and their volume category. Presenters resolve
    // everything through this catalog — an empty slot is an honest
    // missing-asset state that is reported, never a silent substitute.
    //
    // Volume categories map onto the profile's three sliders:
    //   "effects" -> draftProfile.effectsVolume
    //   "music"   -> draftProfile.musicVolume
    //   "voice"   -> draftProfile.voiceVolume
    // One-shots are triggered by driving event kinds; loops are bound to
    // live motor state (engine per vehicle class, skid while drifting,
    // boost while boosting); music cues key on the race phase group.
    [CreateAssetMenu(menuName = "FamilyRacing/Race Presentation Catalog",
        fileName = "race-presentation")]
    public sealed class RacePresentationCatalogAsset : ScriptableObject
    {
        public const string VolumeEffects = "effects";
        public const string VolumeMusic = "music";
        public const string VolumeVoice = "voice";

        // One-shot audio cue: fired by one driving event kind, played on
        // a pooled 2D/positional voice. `clip == null` is the honest
        // missing-recording state.
        [Serializable]
        public sealed class Cue
        {
            public string cueId = "";
            [Tooltip("Driving event kind that fires this cue, e.g. " +
                "motor.landed / checkpoint.crossed / race.finished.")]
            public string eventKind = "";
            public AudioClip clip;
            public string volumeCategory = VolumeEffects;
            [Range(0f, 1f)] public float volume = 1f;
            [Tooltip("Minimum seconds between plays of this cue.")]
            public float minIntervalSeconds = 0.1f;
        }

        // Continuous loop bound to live racer/race state. Engine loops
        // are keyed "engine.<vehicleClass>" (engine.car, engine.motorcycle);
        // "skid" and "boost" ride the motor's drifting/boostActive flags.
        [Serializable]
        public sealed class Loop
        {
            public string loopId = "";
            public AudioClip clip;
            public string volumeCategory = VolumeEffects;
            [Range(0f, 1f)] public float volume = 1f;
            [Tooltip("Max simultaneous sources for this loop id.")]
            public int maxVoices = 4;
        }

        // Race music cue bound to a phase group: "racing" covers
        // countdown + running, "results" covers finished/results.
        [Serializable]
        public sealed class MusicCue
        {
            public string cueId = "";
            public string phase = "racing";
            public AudioClip clip;
            [Range(0f, 1f)] public float volume = 1f;
        }

        // Pooled visual effect: `poolSize` instances of `prefab` are
        // pre-instantiated and reused; exhaustion is observable on the
        // published pool facts and reduces decoration only.
        [Serializable]
        public sealed class Effect
        {
            public string effectId = "";
            public GameObject prefab;
            public int poolSize = 8;
            public float lifetimeSeconds = 0.8f;
            [Tooltip("Face the render camera instead of lying flat.")]
            public bool faceCamera;
            [Tooltip("Scale multiplier reached at end of life.")]
            public float growTo = 1.6f;
        }

        // Event kind -> pooled effect burst.
        [Serializable]
        public sealed class EffectTrigger
        {
            public string eventKind = "";
            public string effectId = "";
            public int count = 1;
        }

        public string catalogId = "race-presentation-01";
        public Cue[] cues = new Cue[0];
        public Loop[] loops = new Loop[0];
        public MusicCue[] music = new MusicCue[0];
        public Effect[] effects = new Effect[0];
        public EffectTrigger[] effectTriggers = new EffectTrigger[0];

        // Continuous emission bindings (state-driven, not event-driven).
        public string driftEffectId = "drift-dust";
        public string boostEffectId = "boost-flare";
        public float driftEmitSeconds = 0.14f;
        public float boostEmitSeconds = 0.08f;

        // Continuous audio bindings.
        public string engineLoopPrefix = "engine.";
        public string skidLoopId = "skid";
        public string boostLoopId = "boost";
        [Tooltip("Simultaneous one-shot cue voices.")]
        public int maxOneShotVoices = 8;
        [Tooltip("Volume scale applied to non-local racers.")]
        [Range(0f, 1f)] public float remoteVoiceScale = 0.45f;
        public float enginePitchMin = 0.85f;
        public float enginePitchMax = 1.7f;
        [Tooltip("Speed (m/s) that maps to enginePitchMax.")]
        public float engineReferenceSpeed = 24f;

        public Cue CueForEvent(string eventKind)
        {
            if (cues == null || eventKind == null) return null;
            foreach (var cue in cues)
                if (cue != null && cue.eventKind == eventKind) return cue;
            return null;
        }

        public Loop LoopFor(string loopId)
        {
            if (loops == null || loopId == null) return null;
            foreach (var loop in loops)
                if (loop != null && loop.loopId == loopId) return loop;
            return null;
        }

        public MusicCue MusicForPhase(string phaseGroup)
        {
            if (music == null || phaseGroup == null) return null;
            foreach (var cue in music)
                if (cue != null && cue.phase == phaseGroup) return cue;
            return null;
        }

        public Effect EffectFor(string effectId)
        {
            if (effects == null || effectId == null) return null;
            foreach (var effect in effects)
                if (effect != null && effect.effectId == effectId)
                    return effect;
            return null;
        }

        public System.Collections.Generic.List<EffectTrigger> TriggersFor(
            string eventKind)
        {
            var matches = new System.Collections.Generic.List<EffectTrigger>();
            if (effectTriggers == null || eventKind == null) return matches;
            foreach (var trigger in effectTriggers)
                if (trigger != null && trigger.eventKind == eventKind)
                    matches.Add(trigger);
            return matches;
        }

        public bool Validate(out string error)
        {
            if (string.IsNullOrEmpty(catalogId))
            {
                error = name + ": catalogId is empty";
                return false;
            }
            if (!ValidateIds(cues, c => c.cueId, "cue", out error)
                || !ValidateIds(loops, l => l.loopId, "loop", out error)
                || !ValidateIds(effects, e => e.effectId, "effect",
                    out error))
                return false;
            if (effectTriggers != null)
                foreach (var trigger in effectTriggers)
                {
                    if (trigger == null || string.IsNullOrEmpty(trigger.eventKind)
                        || string.IsNullOrEmpty(trigger.effectId))
                    {
                        error = name + ": effect trigger missing kind/effectId";
                        return false;
                    }
                    if (EffectFor(trigger.effectId) == null)
                    {
                        error = name + ": effect trigger '" + trigger.eventKind
                            + "' references unknown effectId '" + trigger.effectId + "'";
                        return false;
                    }
                }
            error = null;
            return true;
        }

        private bool ValidateIds<T>(T[] entries, Func<T, string> id,
            string label, out string error)
        {
            var seen = new System.Collections.Generic.HashSet<string>(
                StringComparer.Ordinal);
            if (entries != null)
                foreach (var entry in entries)
                {
                    if (entry == null || string.IsNullOrEmpty(id(entry)))
                    {
                        error = name + ": " + label + " missing id";
                        return false;
                    }
                    if (!seen.Add(id(entry)))
                    {
                        error = name + ": duplicate " + label + " id '" + id(entry) + "'";
                        return false;
                    }
                }
            error = null;
            return true;
        }
    }
}
