using System;
using System.Collections.Generic;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Foundation
{
    // Serializable full-state DTO. Every Snapshot() call returns an
    // independent deep copy; callers can freely mutate what they receive.
    // stateSchemaVersion 3 adds the network session slice, 4 the physical
    // surfaces, and 5 the render-profile/performance presentation slice;
    // consumers must negotiate the schema, never silently reinterpret it.
    [Serializable]
    public sealed class FoundationState
    {
        public int stateSchemaVersion = NetworkSettings.StateSchemaVersion;
        public string contentVersion = "";
        public string screen;
        public string language;
        public int activeSlot;
        public bool paused;
        public long tick;
        public InputState input;
        public List<SlotState> slots;
        public ProfileSave savedProfile;
        public ProfileSave draftProfile;
        public bool pendingSettings;
        public string persistenceStatus;
        public bool captureValid;
        public string captureError;
        public FeatureCoverage featureCoverage;
        public PresentationState presentation;
        public DrivingSessionState driving;
        public List<DrivingEvent> drivingEvents;
        // Durable marker for bounded-event pressure: the number of events
        // evicted from drivingEvents by the capacity bound. An observer
        // never sees events disappear without this counter moving.
        public int droppedDrivingEvents;
        // Schema-3 network slices. `network` is the lobby/session policy
        // view and is always populated (idle/none without a session);
        // networkRace (host-confirmed race state), prediction (local
        // predicted motor/ack/buffer/corrections) and remotePresentation
        // (interpolation facts) are delivered by Tasks 3/5/6 and stay null
        // until then — never silently aliased onto the solo `driving`
        // field.
        public NetworkSessionState network;
        // Host-confirmed shared race state (Task 3). Same emitted field
        // name as the object placeholder it replaces; null until a network
        // race exists.
        public NetworkRaceState networkRace;
        // Task 6 typed slices (same emitted field names as the object
        // placeholders they replace): the local predicted motor with
        // ack/buffer/correction facts, and per-remote-participant
        // interpolation facts. `prediction` is null on the host and
        // outside a LAN race — predicted data never appears authoritative
        // through `driving`. `remotePresentation` is a per-device
        // presentation diagnostic: once a replication bridge exists it is
        // always non-null; the host's entry list is simply empty because
        // the host renders remote racers straight from authoritative
        // state and runs no client-side interpolation.
        public NetworkPredictionState prediction;
        public RemoteInterpolationState remotePresentation;
        // Task 11 replication-link counters (same emitted field name):
        // the bridge's own packet/loss facts on THIS endpoint — null
        // until a replication bridge exists, never fabricated.
        public NetworkLinkState networkLink;
    }

    // Runtime-visible presentation facts (geometry, input ownership, locale,
    // font qualification, actual camera mode) so the stream can reconstruct
    // UI decisions instead of hiding them in opaque client state.
    [Serializable]
    public sealed class PresentationState
    {
        public int screenWidth;
        public int screenHeight;
        public string inputSource = "none";
        public string textDirection = "ltr";
        public bool hebrewFontAvailable;
        public string fontWarning = "";
        public string buildTarget = "";
        public bool validationActive;
        public int backlogSteps;
        public long stepOverloads;
        // The render camera actually applied this frame: mode "none" until
        // a race (solo or LAN) engages the presentation camera. This is
        // per-device presentation truth — solo also mirrors the same facts
        // under driving.camera; LAN racers publish them here because the
        // networkRace slice is host-confirmed shared state and a local
        // camera choice is not shared state.
        public DrivingCameraState camera;
        // --- Schema-5 additive slice (M4 Task 2) --------------------------
        // The render profile actually applied on this device — "tablet",
        // "pc", "none" before any Apply, or "missing" after a refused one.
        // Published with the applied render scale and frame-rate target so
        // an observer can never mistake the tablet profile for the PC one.
        public string renderProfileId = "none";
        public float renderScale;
        public int targetFrameRate;
        // The course-visual hierarchy actually loaded on this device —
        // "sunny-island-benchmark-01" on the dressed benchmark, "none"
        // before any course prefab is bound. Published so an observer can
        // verify the benchmark renders the authored environment rather
        // than the undressed graybox placeholder.
        public string courseVisualId = "none";
        // False on a headless run: frame intervals then describe unrendered
        // frames and can never pass as rendered-player evidence.
        public bool renderingAvailable = true;
        // Per-racer visual identity actually on screen (M4 Task 4): one
        // entry per presented vehicle — the solo racer or every LAN
        // racer view — carrying the resolved vehicle/character/color
        // identity (authored ids or honest "primitive-fallback"/"none"
        // tokens) and the pose source driving it. Bounded by the shared
        // four-racer capacity plus the solo slot.
        public List<RacerPresentationState> racers =
            new List<RacerPresentationState>();
        // Bounded render-performance summary (counts/percentiles/health —
        // never per-frame buffers). Null only when capture is disabled.
        public RenderPerformanceSummary performance;
        // --- Schema-5 additive slice (M4 Task 5) --------------------------
        // The actual race effects/audio presentation state on this
        // device: bound catalog identity, live pooled-effect counts and
        // pool-exhaustion counters, live loop/voice ids, the active music
        // cue, and the honest missing-asset lists. Null when no
        // presentation catalog is bound (a non-racing scene reports
        // nothing rather than fabricating a silent catalog).
        public RaceEffectsState effects;
        public RaceAudioState audio;

        public PresentationState Copy()
        {
            var copy = (PresentationState)MemberwiseClone();
            copy.camera = camera != null ? camera.Copy() : null;
            copy.performance = performance != null ? performance.Copy() : null;
            copy.effects = effects != null ? effects.Copy() : null;
            copy.audio = audio != null ? audio.Copy() : null;
            if (racers != null && racers.Count > 0)
            {
                copy.racers = new List<RacerPresentationState>(racers.Count);
                foreach (var racer in racers)
                    copy.racers.Add(racer != null ? racer.Copy() : null);
            }
            return copy;
        }
    }

    // One presented racer's visual identity (M4 Task 4). vehicleVisualId
    // is the authored VehicleVisualDefinitionAsset visualId actually
    // bound, or the fallback token when primitives render; characterId
    // is the resolved animal, the generic "fallback" rider, or "none"
    // when the primitive silhouette shows no animal. poseSource names
    // which state source drives the pose: "authoritative" (solo/host
    // commits), "predicted" (client-local prediction) or "interpolated"
    // (remote snapshot interpolation).
    [Serializable]
    public sealed class RacerPresentationState
    {
        public string participantId = "";
        public int slot;
        public string vehicleId = "";
        public string vehicleVisualId = "";
        public string characterId = "";
        public int colorIndex;
        public string poseSource = "";

        public RacerPresentationState Copy()
        {
            return (RacerPresentationState)MemberwiseClone();
        }
    }

    // One pooled effect channel's live facts (M4 Task 5). `exhausted`
    // counts refused activations this race — pool pressure is observable,
    // never silent.
    [Serializable]
    public sealed class EffectPoolFacts
    {
        public string effectId = "";
        public int capacity;
        public int active;
        public int exhausted;

        public EffectPoolFacts Copy()
        {
            return (EffectPoolFacts)MemberwiseClone();
        }
    }

    // The effects presenter's actual state (M4 Task 5): the bound catalog,
    // live pooled instances, per-pool capacity/pressure and the bounded
    // missing-prefab list. Per-race counters (each pool's `exhausted`,
    // `missingEffects`/`missingDropped`) reset on every re-baseline —
    // a new race identity, an explicit re-bind or teardown to no race —
    // so a rematch never reports the previous race's pressure; only
    // `catalogId` persists across races.
    [Serializable]
    public sealed class RaceEffectsState
    {
        public string catalogId = "none";
        public int activeEffects;
        public List<EffectPoolFacts> pools = new List<EffectPoolFacts>();
        // Effect ids whose catalog slot has no prefab — bounded (16) with
        // the overflow in `missingDropped` so the list stays truthful.
        public List<string> missingEffects = new List<string>();
        public int missingDropped;

        public RaceEffectsState Copy()
        {
            var copy = new RaceEffectsState
            {
                catalogId = catalogId,
                activeEffects = activeEffects,
                missingDropped = missingDropped
            };
            foreach (var pool in pools)
                copy.pools.Add(pool != null ? pool.Copy() : null);
            copy.missingEffects.AddRange(missingEffects);
            return copy;
        }
    }

    // The audio presenter's actual state (M4 Task 5): the bound catalog,
    // pause flag, active music cue id, live loop identities
    // ("<loopId>@<racerKey>"), live voice count, and the per-race
    // counters. `oneShots`, `droppedOneShots`, `refusedLoopVoices` and
    // `missingCues`/`missingDropped` describe the current race binding
    // only — every re-baseline (a new race identity, an explicit
    // re-bind or teardown to no race) zeroes them, so a rematch never
    // reports the previous race's totals; only `catalogId` persists
    // across races. Empty lists mean nothing is sounding — a missing
    // clip is a missing entry, never a claimed playback.
    [Serializable]
    public sealed class RaceAudioState
    {
        public string catalogId = "none";
        public bool paused;
        public string musicCue = "none";
        public List<string> loops = new List<string>();
        public int voices;
        public int oneShots;
        // One-shot activations refused because every authored voice was
        // busy — pressure is observable, never silent.
        public int droppedOneShots;
        // Loop activations refused because the loop's authored
        // maxVoices was reached — counted once per wanting episode
        // (loopId@racerKey while the condition holds), never per frame.
        public int refusedLoopVoices;
        public List<string> missingCues = new List<string>();
        public int missingDropped;

        public RaceAudioState Copy()
        {
            var copy = new RaceAudioState
            {
                catalogId = catalogId,
                paused = paused,
                musicCue = musicCue,
                voices = voices,
                oneShots = oneShots,
                droppedOneShots = droppedOneShots,
                refusedLoopVoices = refusedLoopVoices,
                missingDropped = missingDropped
            };
            copy.loops.AddRange(loops);
            copy.missingCues.AddRange(missingCues);
            return copy;
        }
    }

    [Serializable]
    public sealed class InputState
    {
        public float steering;
        public bool drift;
        public bool boost;
        public bool gadget;
        public bool recover;
        public bool changeCamera;
        public bool pause;
        // Held brake demand in [0,1] — 0 on every pre-brake build since
        // the field is additive and absent defaults to zero.
        public float brake;
        // True only when a control source explicitly requested a
        // throttle level; then `throttle` is the held demand. Absent
        // means automatic throttle — the distinction must stay
        // observable, not collapse into an ambiguous zero.
        public bool hasThrottleOverride;
        public float throttle = 1f;
    }

    [Serializable]
    public sealed class SlotState
    {
        public int slot;
        public string status;
        public ProfileSave profile;
    }

    // Later-gameplay features are named explicitly so observers can
    // distinguish implemented foundation coverage from remaining gaps.
    // `tutorial` tracks the full-release tutorial (incl. gadget
    // onboarding) and stays notImplemented; `drivingOnboarding` records
    // the M2 ordered-lesson intro onboarding only.
    [Serializable]
    public sealed class FeatureCoverage
    {
        public string tutorial = "notImplemented";
        public string drivingOnboarding = "implemented";
        public string drivingSimulation = "implemented";
        public string campaign = "notImplemented";
        public string rewardsUpgradesCosmetics = "notImplemented";
        public string lan = "notImplemented";
        public string content = "graybox";
    }
}
