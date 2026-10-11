using System;
using System.Collections.Generic;
using FamilyRacing.Core.Profiles;

namespace FamilyRacing.Core.Foundation
{
    // Serializable full-state DTO. Every Snapshot() call returns an
    // independent deep copy; callers can freely mutate what they receive.
    [Serializable]
    public sealed class FoundationState
    {
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
    [Serializable]
    public sealed class FeatureCoverage
    {
        public string tutorial = "notImplemented";
        public string drivingSimulation = "notImplemented";
        public string campaign = "notImplemented";
        public string rewardsUpgradesCosmetics = "notImplemented";
        public string lan = "notImplemented";
        public string content = "notImplemented";
    }
}
