using System.Collections.Generic;
using FamilyRacing.Core.Foundation;
using UnityEngine;

namespace FamilyRacing
{
    // Offline presentation only. The observer consumes state transitions rather
    // than frame counts; each cue is queued once and recordings never overlap.
    public sealed class ProductionVoicePresenter : MonoBehaviour
    {
        public VoicePromptCatalogAsset catalog;
        private FoundationApp app;
        private AudioSource source;
        private ProductionVoiceTransitions transitions = new ProductionVoiceTransitions();
        private readonly Queue<string> pending = new Queue<string>();
        private readonly HashSet<string> missing = new HashSet<string>();
        private string language;
        private int slot = -1;
        private bool paused;

        public string PlayingCue { get; private set; }
        public string LastMissingCue { get; private set; }

        public void Bind(VoicePromptCatalogAsset asset)
        {
            catalog = asset;
            transitions = new ProductionVoiceTransitions();
            pending.Clear();
            if (source != null) source.Stop();
            PlayingCue = null;
        }

        private void LateUpdate()
        {
            if (app == null) app = GetComponent<FoundationApp>();
            if (app != null && app.Service != null) ApplyFrame(app.Service.Snapshot());
        }

        public void ApplyFrame(FoundationState state)
        {
            if (state == null) return;
            EnsureSource();
            if (language != state.language || slot != state.activeSlot)
            {
                source.Stop();
                pending.Clear();
                PlayingCue = null;
                language = state.language;
                slot = state.activeSlot;
                transitions = new ProductionVoiceTransitions();
            }
            foreach (var cue in transitions.Observe(state)) pending.Enqueue(cue);
            source.volume = state.draftProfile != null ? Mathf.Clamp01(state.draftProfile.voiceVolume) : 1f;
            if (state.paused != paused)
            {
                paused = state.paused;
                if (paused) source.Pause(); else source.UnPause();
            }
            if (paused || source.isPlaying) return;
            PlayingCue = null;
            while (pending.Count > 0)
            {
                var cue = pending.Dequeue();
                var clip = catalog != null ? catalog.Resolve(cue, language) : null;
                if (clip == null)
                {
                    LastMissingCue = cue + ":" + language;
                    if (missing.Add(LastMissingCue))
                        Debug.LogWarning("ProductionVoicePresenter: missing bundled recording " + LastMissingCue);
                    continue;
                }
                source.clip = clip;
                source.Play();
                PlayingCue = cue;
                break;
            }
        }

        private void EnsureSource()
        {
            if (source != null) return;
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
        }

        private void OnDisable()
        {
            if (source != null) source.Stop();
            pending.Clear();
            PlayingCue = null;
        }
    }

    // Pure transition policy, independently testable without an audio device.
    public sealed class ProductionVoiceTransitions
    {
        private readonly Dictionary<string, bool> worlds = new Dictionary<string, bool>();
        private int slot = -1;
        private bool baseline;
        private bool adventureComplete;
        private string world = "";
        private string race = "";

        public List<string> Observe(FoundationState state)
        {
            var cues = new List<string>();
            var progression = state != null ? state.progression : null;
            if (progression == null || !progression.available) return cues;
            if (slot != state.activeSlot)
            {
                worlds.Clear(); baseline = false; world = ""; race = "";
                slot = state.activeSlot;
            }
            var unlocked = false;
            foreach (var row in progression.worlds)
            {
                if (row == null) continue;
                bool previously;
                if (baseline && worlds.TryGetValue(row.worldId, out previously)
                    && !previously && row.available) unlocked = true;
                worlds[row.worldId] = row.available;
            }
            if (unlocked) cues.Add("world.unlocked");
            if (baseline && !adventureComplete && progression.adventureComplete)
                cues.Add("adventure.complete");
            adventureComplete = progression.adventureComplete;
            baseline = true;
            if (state.screen == "campaign" && world != progression.campaignWorldId
                && !string.IsNullOrEmpty(progression.campaignWorldId))
            {
                world = progression.campaignWorldId;
                cues.Add("world.intro." + world);
            }

            var solo = state.soloRace;
            if (solo == null) { race = ""; return cues; }
            if (solo.mode != "campaign" || solo.raceId == race
                || (solo.phase != "countdown" && solo.phase != "running")) return cues;
            race = solo.raceId;
            var eventId = "";
            if (solo.race != null && solo.race.racers != null)
                foreach (var racer in solo.race.racers)
                    if (racer != null && racer.session != null)
                    { eventId = racer.session.eventId; break; }
            var mask = 0;
            foreach (var row in progression.campaignEvents)
                if (row != null && row.eventId == eventId)
                {
                    mask = row.objectiveMask;
                    if (row.finale && !string.IsNullOrEmpty(row.rivalId))
                        cues.Add("rival.intro." + row.rivalId);
                    break;
                }
            cues.Add((mask & 1) == 0 ? "objective.completion"
                : (mask & 2) == 0 ? "objective.introductory" : "objective.mastery");
            return cues;
        }
    }
}
