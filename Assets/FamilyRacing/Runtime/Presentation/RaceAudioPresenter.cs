using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Racing;
using UnityEngine;

namespace FamilyRacing
{
    // Race audio presenter (M4 Task 5): consumes the published race state
    // and plays the catalog's bundled clips.
    //
    //   * One-shot cues fire off driving events, deduplicated by event
    //     identity — repeated snapshots, correction re-deliveries and
    //     reconnect republishes never replay a consumed cue. The first
    //     frame after a (re)bind is a cursor baseline: everything already
    //     in the stream is history for this observer and plays nothing.
    //   * Continuous loops ride live motor state: an engine loop per
    //     racer keyed to its vehicle class (pitch tracked to speed), a
    //     skid loop while drifting, a boost loop while boosting. Remote
    //     racers play at the catalog's remoteVoiceScale.
    //   * Music follows the phase group ("racing" while
    //     countdown/running, "results" once finished).
    //   * Every source's volume is exactly its own profile slider times
    //     the authored level — the three volume controls never cross.
    //   * Pause suspends every loop and the music; exit/teardown stops
    //     them outright. A catalog cue with no clip is reported on
    //     Facts.missingCues and logged once — never claimed as playing.
    public sealed class RaceAudioPresenter : MonoBehaviour
    {
        private const int MissingCapacity = 16;

        private sealed class Voice
        {
            internal string loopId;
            internal AudioSource source;
            internal float authoredVolume;
            internal string category = RacePresentationCatalogAsset.VolumeEffects;
            // Presenter-owned truth: true while the loop is meant to sound.
            // The AudioSource gets Play/UnPause while active, Pause while
            // the frame is paused, Stop + destroy when the voice retires.
            internal bool active;
            internal bool paused;
        }

        private RacePresentationCatalogAsset catalog;
        private readonly RaceEventCursor cursor = new RaceEventCursor();
        private bool contextActive;
        private bool framePaused;
        // Loop voices keyed "<loopId>@<racerKey>"; reused while the racer
        // exists so drift/boost toggling never churns components.
        private readonly Dictionary<string, Voice> voices =
            new Dictionary<string, Voice>(StringComparer.Ordinal);
        private readonly List<AudioSource> oneShotVoices =
            new List<AudioSource>();
        private AudioSource musicSource;
        private string musicCueId = "";
        private bool musicStarted;
        private bool musicPaused;
        private readonly List<string> missingOrder = new List<string>();
        private readonly HashSet<string> missingSet =
            new HashSet<string>(StringComparer.Ordinal);
        private int missingDropped;
        private readonly HashSet<string> missingLogged =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> lastCueAt =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private int oneShots;
        private int droppedOneShots;
        private readonly RaceAudioState snapshot = new RaceAudioState();

        public RaceAudioState Facts { get { return snapshot.Copy(); } }
        // A bound catalog is the honest precondition for publishing the
        // audio slice: unbound means nothing is sounding, so the schema-5
        // field stays null rather than reporting a phantom catalog.
        public bool Bound { get { return catalog != null; } }
        internal AudioSource MusicSource { get { return musicSource; } }
        internal string LastPlayedCue { get; private set; }
        internal int PlayedCueCount { get { return oneShots; } }

        public void Bind(RacePresentationCatalogAsset asset)
        {
            catalog = asset;
        }

        // (Re)bind to a race identity: the next ApplyFrame baselines the
        // published stream — attach/rejoin never replays history. Any
        // live loop/music from the previous binding stops here.
        public void Reset(string raceId)
        {
            StopAllVoices();
            StopMusic();
            cursor.Reset(raceId ?? "");
            contextActive = false;
            musicCueId = "";
            musicStarted = false;
            UpdateFacts();
        }

        public void ApplyFrame(FoundationState state, float deltaSeconds)
        {
            var raceId = RacePresentationUtil.RaceId(state);
            if (raceId == null)
            {
                // No race context: everything stops and the facts say so.
                if (contextActive || voices.Count > 0 || musicSource != null)
                {
                    StopAllVoices();
                    StopMusic();
                }
                contextActive = false;
                framePaused = false;
                cursor.Reset("");
                UpdateFacts();
                return;
            }
            if (!contextActive || raceId != cursor.RaceId)
            {
                StopAllVoices();
                StopMusic();
                cursor.Reset(raceId);
                musicCueId = "";
                musicStarted = false;
            }
            contextActive = true;
            framePaused = RacePresentationUtil.IsPaused(state);

            // Deduped one-shots: the priming frame marks the stream's
            // history seen without playing; later frames play exactly the
            // new identities — even while paused, since the sim freeze
            // means no new gameplay events arrive there anyway.
            var fresh = cursor.Consume(state.drivingEvents);
            foreach (var ev in fresh) PlayCueFor(ev, state);

            UpdateLoops(RacePresentationUtil.Racers(state), state);
            UpdateMusic(state);
            UpdateFacts();
        }

        // --- one-shot cues -------------------------------------------------

        private void PlayCueFor(DrivingEvent ev, FoundationState state)
        {
            if (catalog == null || ev == null) return;
            var cue = catalog.CueForEvent(ev.kind);
            if (cue == null) return;
            if (cue.clip == null)
            {
                RecordMissing(cue.cueId);
                return;
            }
            // Per-cue retrigger throttle bounds a burst of identical
            // events (e.g. multi-gate sweeps) without dropping identity.
            float lastAt;
            var now = Time.unscaledTime;
            if (cue.minIntervalSeconds > 0f
                && lastCueAt.TryGetValue(cue.cueId, out lastAt)
                && now - lastAt < cue.minIntervalSeconds)
                return;
            var local = RacePresentationUtil.IsLocalEvent(state, ev);
            var volume = BusVolume(cue.volumeCategory, state)
                * Mathf.Clamp01(cue.volume)
                * (local ? 1f : catalog.remoteVoiceScale);
            var source = FreeOneShotVoice();
            if (source == null)
            {
                droppedOneShots++;
                return;
            }
            lastCueAt[cue.cueId] = now;
            source.PlayOneShot(cue.clip, Mathf.Clamp01(volume));
            oneShots++;
            LastPlayedCue = cue.cueId;
        }

        private AudioSource FreeOneShotVoice()
        {
            foreach (var existing in oneShotVoices)
                if (existing != null && !existing.isPlaying) return existing;
            var bound = catalog != null ? catalog.maxOneShotVoices : 4;
            if (bound <= 0 || oneShotVoices.Count >= bound) return null;
            var holder = new GameObject(
                "OneShotVoice-" + oneShotVoices.Count);
            holder.transform.SetParent(transform, false);
            var source = holder.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            oneShotVoices.Add(source);
            return source;
        }

        // --- continuous loops -------------------------------------------------

        private void UpdateLoops(
            List<RacePresentationUtil.PresentedRacer> racers,
            FoundationState state)
        {
            var wanted = catalog == null || racers == null
                ? null
                : new HashSet<string>(StringComparer.Ordinal);
            if (wanted != null)
                foreach (var racer in racers)
                {
                    if (racer.motor == null) continue;
                    var engineLoop =
                        catalog.engineLoopPrefix
                        + VehicleTuning.ClassIdOf(racer.vehicleId);
                    if (catalog.LoopFor(engineLoop) != null)
                        wanted.Add(engineLoop + "@" + racer.key);
                    if (racer.motor.drifting && racer.motor.grounded
                        && catalog.LoopFor(catalog.skidLoopId) != null)
                        wanted.Add(catalog.skidLoopId + "@" + racer.key);
                    if (racer.motor.boostActive
                        && catalog.LoopFor(catalog.boostLoopId) != null)
                        wanted.Add(catalog.boostLoopId + "@" + racer.key);
                }

            // Retire voices for racers/conditions that ended.
            var stale = new List<string>();
            foreach (var pair in voices)
                if (wanted == null || !wanted.Contains(pair.Key))
                    stale.Add(pair.Key);
            foreach (var key in stale) RemoveVoice(key);
            if (wanted == null) return;

            foreach (var racer in racers)
            {
                if (racer.motor == null) continue;
                var engineLoop = catalog.engineLoopPrefix
                    + VehicleTuning.ClassIdOf(racer.vehicleId);
                if (wanted.Contains(engineLoop + "@" + racer.key))
                    ApplyLoop(engineLoop, racer, EnginePitch(racer.motor),
                        state);
                if (wanted.Contains(catalog.skidLoopId + "@" + racer.key))
                    ApplyLoop(catalog.skidLoopId, racer, 1f, state);
                if (wanted.Contains(catalog.boostLoopId + "@" + racer.key))
                    ApplyLoop(catalog.boostLoopId, racer, 1f, state);
            }
        }

        private void ApplyLoop(string loopId,
            RacePresentationUtil.PresentedRacer racer, float pitch,
            FoundationState state)
        {
            var entry = catalog.LoopFor(loopId);
            if (entry == null) return;
            var key = loopId + "@" + racer.key;
            Voice voice;
            if (!voices.TryGetValue(key, out voice))
            {
                // Per-loop voice bound: refuse extras honestly (no silent
                // over-subscription); the cap lives in authored data.
                var inUse = 0;
                foreach (var other in voices.Values)
                    if (other.loopId == loopId) inUse++;
                if (inUse >= entry.maxVoices) return;
                if (entry.clip == null)
                {
                    RecordMissing(loopId);
                    return;
                }
                var go = new GameObject("Loop-" + key);
                go.transform.SetParent(transform, false);
                var source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = true;
                source.spatialBlend = 0f;
                source.clip = entry.clip;
                voice = new Voice
                {
                    loopId = loopId,
                    source = source,
                    authoredVolume = entry.volume,
                    category = entry.volumeCategory
                };
                voices[key] = voice;
            }
            voice.source.pitch = pitch;
            voice.source.volume = BusVolume(voice.category, state)
                * Mathf.Clamp01(voice.authoredVolume)
                * (racer.local ? 1f : catalog.remoteVoiceScale);
            if (framePaused)
            {
                if (voice.source.isPlaying) voice.source.Pause();
                voice.paused = true;
                voice.active = false;
            }
            else
            {
                if (voice.paused)
                {
                    voice.source.UnPause();
                    voice.paused = false;
                }
                else if (!voice.source.isPlaying)
                {
                    voice.source.Play();
                }
                voice.active = true;
            }
        }

        private float EnginePitch(MotorState motor)
        {
            var reference = catalog != null && catalog.engineReferenceSpeed > 0f
                ? catalog.engineReferenceSpeed : 24f;
            var t = Mathf.Clamp01(Mathf.Abs(motor.speed) / reference);
            return Mathf.Lerp(
                catalog != null ? catalog.enginePitchMin : 0.85f,
                catalog != null ? catalog.enginePitchMax : 1.7f, t);
        }

        private void RemoveVoice(string key)
        {
            Voice voice;
            if (!voices.TryGetValue(key, out voice)) return;
            voices.Remove(key);
            if (voice != null && voice.source != null)
            {
                voice.source.Stop();
                Destroy(voice.source.gameObject);
            }
        }

        private void StopAllVoices()
        {
            var keys = new List<string>(voices.Keys);
            foreach (var key in keys) RemoveVoice(key);
            foreach (var source in oneShotVoices)
                if (source != null && source.isPlaying) source.Stop();
        }

        // --- music --------------------------------------------------------

        private void UpdateMusic(FoundationState state)
        {
            var group = RacePresentationUtil.MusicGroup(state);
            var cue = catalog != null && group != ""
                ? catalog.MusicForPhase(group) : null;
            var wantId = cue != null ? cue.cueId : "";
            if (wantId != musicCueId)
            {
                StopMusic();
                musicCueId = wantId;
            }
            if (cue == null)
            {
                musicCueId = "";
                return;
            }
            if (cue.clip == null)
            {
                RecordMissing(cue.cueId);
                musicCueId = "";
                return;
            }
            EnsureMusicSource();
            if (musicSource.clip != cue.clip)
            {
                musicSource.clip = cue.clip;
                musicStarted = false;
            }
            musicSource.volume =
                BusVolume(RacePresentationCatalogAsset.VolumeMusic, state)
                * Mathf.Clamp01(cue.volume);
            if (framePaused)
            {
                if (musicSource.isPlaying) musicSource.Pause();
                musicPaused = true;
            }
            else if (musicPaused)
            {
                musicSource.UnPause();
                musicPaused = false;
                musicStarted = true;
            }
            else if (!musicSource.isPlaying)
            {
                musicSource.Play();
                musicStarted = true;
            }
        }

        private void EnsureMusicSource()
        {
            if (musicSource != null) return;
            var go = new GameObject("MusicVoice");
            go.transform.SetParent(transform, false);
            musicSource = go.AddComponent<AudioSource>();
            musicSource.playOnAwake = false;
            musicSource.loop = true;
            musicSource.spatialBlend = 0f;
        }

        private void StopMusic()
        {
            if (musicSource != null && musicSource.isPlaying)
                musicSource.Stop();
            musicStarted = false;
            musicPaused = false;
        }

        // --- shared bits ---------------------------------------------------

        private float BusVolume(string category, FoundationState state)
        {
            var profile = state != null ? state.draftProfile : null;
            if (profile == null) return 1f;
            switch (category)
            {
                case RacePresentationCatalogAsset.VolumeMusic:
                    return Mathf.Clamp01(profile.musicVolume);
                case RacePresentationCatalogAsset.VolumeVoice:
                    return Mathf.Clamp01(profile.voiceVolume);
                default:
                    return Mathf.Clamp01(profile.effectsVolume);
            }
        }

        private void RecordMissing(string cueId)
        {
            if (string.IsNullOrEmpty(cueId)) return;
            if (missingSet.Add(cueId))
            {
                missingOrder.Add(cueId);
                while (missingOrder.Count > MissingCapacity)
                {
                    missingSet.Remove(missingOrder[0]);
                    missingOrder.RemoveAt(0);
                    missingDropped++;
                }
            }
            if (missingLogged.Add(cueId))
                Debug.LogWarning("RaceAudioPresenter: no clip bundled for cue '"
                    + cueId + "' — the catalog slot is empty; reporting "
                    + "missing instead of playing.");
        }

        private void UpdateFacts()
        {
            snapshot.catalogId = catalog != null ? catalog.catalogId : "none";
            snapshot.paused = framePaused && contextActive;
            // The cue bound to the live phase group, or "none". Pause
            // keeps the bound id — suspended is still the actual cue.
            snapshot.musicCue = contextActive && musicCueId != ""
                ? musicCueId : "none";
            snapshot.loops.Clear();
            var liveVoices = 0;
            foreach (var pair in voices)
            {
                if (!pair.Value.active) continue;
                snapshot.loops.Add(pair.Key);
                liveVoices++;
            }
            snapshot.loops.Sort(StringComparer.Ordinal);
            var playingOneShots = 0;
            foreach (var source in oneShotVoices)
                if (source != null && source.isPlaying) playingOneShots++;
            snapshot.voices = liveVoices + playingOneShots
                + (musicStarted && musicSource != null && musicSource.isPlaying
                    ? 1 : 0);
            snapshot.oneShots = oneShots;
            snapshot.droppedOneShots = droppedOneShots;
            snapshot.missingCues.Clear();
            snapshot.missingCues.AddRange(missingOrder);
            snapshot.missingDropped = missingDropped;
        }

        private void OnDisable()
        {
            StopAllVoices();
            StopMusic();
            contextActive = false;
            framePaused = false;
            cursor.Reset("");
            UpdateFacts();
        }

        internal bool LoopActive(string loopId, string racerKey)
        {
            Voice voice;
            return loopId != null && racerKey != null
                && voices.TryGetValue(loopId + "@" + racerKey, out voice)
                && voice.active;
        }

        internal AudioSource LoopSource(string loopId, string racerKey)
        {
            Voice voice;
            return loopId != null && racerKey != null
                && voices.TryGetValue(loopId + "@" + racerKey, out voice)
                ? voice.source : null;
        }
    }

    // --- shared presentation plumbing ---------------------------------------
    // Both race presenters consume the same published state: this helper
    // resolves the observed race context (solo session or host-confirmed
    // LAN race), the presented racers with their committed motor state,
    // and the shared pause flag.

    internal static class RacePresentationUtil
    {
        internal struct PresentedRacer
        {
            public string key;          // participantId, or the solo raceId
            public string vehicleId;
            public MotorState motor;    // committed pose/state
            public bool local;
        }

        // The race identity the presentation is bound to: the solo
        // session's raceId, else the host-confirmed LAN race id. Null
        // when no race is presented.
        public static string RaceId(FoundationState state)
        {
            if (state == null) return null;
            if (state.driving != null
                && !string.IsNullOrEmpty(state.driving.raceId))
                return state.driving.raceId;
            var race = state.networkRace;
            if (race != null && !string.IsNullOrEmpty(race.raceId))
                return race.raceId;
            return null;
        }

        public static bool IsPaused(FoundationState state)
        {
            if (state == null) return false;
            if (state.paused) return true;
            if (state.network != null && state.network.paused) return true;
            if (state.networkRace != null && state.networkRace.paused)
                return true;
            return false;
        }

        // Music phase group: "racing" covers countdown through running
        // (solo "countdown"/"running", LAN "countdown"/"racing"),
        // "results" covers finished states, "" means no music context.
        public static string MusicGroup(FoundationState state)
        {
            if (state == null) return "";
            if (state.driving != null)
            {
                switch (state.driving.phase)
                {
                    case "countdown":
                    case "running": return "racing";
                    case "finished": return "results";
                    default: return "";
                }
            }
            var race = state.networkRace;
            if (race != null)
            {
                switch (race.phase)
                {
                    case "countdown":
                    case "running": return "racing";
                    case "finished": return "results";
                    default: return ""; // "aborted" and unknowns: silence
                }
            }
            return "";
        }

        // The racers visible to this observer with their committed motor
        // state: the solo session (keyed by raceId) or every LAN racer
        // with a live session slice (keyed by participantId).
        public static List<PresentedRacer> Racers(FoundationState state)
        {
            var list = new List<PresentedRacer>();
            if (state == null) return list;
            var solo = state.driving;
            if (solo != null && solo.motor != null)
            {
                list.Add(new PresentedRacer
                {
                    key = solo.raceId ?? "solo",
                    vehicleId = solo.vehicleId ?? "",
                    motor = solo.motor,
                    local = true
                });
                return list; // solo and LAN never share a screen
            }
            var race = state.networkRace;
            if (race != null && race.racers != null)
            {
                var localId = state.network != null
                    ? state.network.localParticipantId : null;
                foreach (var racer in race.racers)
                {
                    if (racer == null || racer.session == null
                        || racer.session.motor == null)
                        continue;
                    list.Add(new PresentedRacer
                    {
                        key = racer.participantId ?? "",
                        vehicleId = racer.vehicleId ?? "",
                        motor = racer.session.motor,
                        local = localId != null
                            && racer.participantId == localId
                    });
                }
            }
            return list;
        }

        // True when the event belongs to the locally presented racer —
        // solo events (empty participantId) or the LAN racer matching the
        // local participant.
        public static bool IsLocalEvent(FoundationState state,
            DrivingEvent ev)
        {
            var pid = ev != null ? ev.participantId : "";
            if (string.IsNullOrEmpty(pid)) return true;
            var localId = state != null && state.network != null
                ? state.network.localParticipantId : null;
            return localId == null || pid == localId;
        }

        // The committed pose an event's owner renders at: its attributed
        // participant's motor, else the local/solo racer's.
        public static Vector3 PositionFor(FoundationState state,
            DrivingEvent ev)
        {
            var racers = Racers(state);
            var pid = ev != null ? ev.participantId : "";
            foreach (var racer in racers)
                if (string.IsNullOrEmpty(pid) ? racer.local
                    : racer.key == pid)
                    return new Vector3(racer.motor.x, racer.motor.y,
                        racer.motor.z);
            if (racers.Count > 0)
            {
                var motor = racers[0].motor;
                return new Vector3(motor.x, motor.y, motor.z);
            }
            return Vector3.zero;
        }
    }

    // The per-race one-shot cursor both presenters share: event identity
    // dedupe plus the observer-attach baseline. The first Consume call
    // after Reset marks everything already published as seen and returns
    // nothing — rejoins and initial snapshots never replay history; every
    // later call returns exactly the new identities. The seen set is
    // bounded (512) with FIFO eviction matching the stream's own bound.
    internal sealed class RaceEventCursor
    {
        private const int SeenCapacity = 512;
        private readonly HashSet<string> seen =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> order = new Queue<string>();

        public string RaceId { get; private set; } = "";
        public bool Primed { get; private set; }

        public void Reset(string raceId)
        {
            RaceId = raceId ?? "";
            Primed = false;
            seen.Clear();
            order.Clear();
        }

        public List<DrivingEvent> Consume(List<DrivingEvent> published)
        {
            var fresh = new List<DrivingEvent>();
            var wasPrimed = Primed;
            if (published != null)
                foreach (var ev in published)
                {
                    if (ev == null || string.IsNullOrEmpty(ev.id)) continue;
                    if (!Mark(ev.id)) continue;
                    if (wasPrimed) fresh.Add(ev);
                }
            Primed = true;
            return fresh;
        }

        private bool Mark(string id)
        {
            if (!seen.Add(id)) return false;
            order.Enqueue(id);
            while (order.Count > SeenCapacity)
                seen.Remove(order.Dequeue());
            return true;
        }
    }
}
