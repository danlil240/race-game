using System;
using System.Collections.Generic;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Foundation
{
    // Serializable full-state DTO. Every Snapshot() call returns an
    // independent deep copy; callers can freely mutate what they receive.
    // stateSchemaVersion 3 adds the network session slice; consumers must
    // negotiate the schema, never silently reinterpret it.
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

        public PresentationState Copy()
        {
            var copy = (PresentationState)MemberwiseClone();
            copy.camera = camera != null ? camera.Copy() : null;
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
