using System;
using System.Collections.Generic;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Foundation
{
    // Serializable full-state DTO. Every Snapshot() call returns an
    // independent deep copy; callers can freely mutate what they receive.
    // stateSchemaVersion 2 expands the record with the driving session
    // observation; consumers must negotiate the schema, never silently
    // reinterpret it.
    [Serializable]
    public sealed class FoundationState
    {
        public int stateSchemaVersion = 2;
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
    }

    // Runtime-visible presentation facts (geometry, input ownership, locale,
    // font qualification) so the stream can reconstruct UI decisions instead
    // of hiding them in opaque client state.
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
