using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Profiles
{
    public static class ProfileFactory
    {
        public static ProfileSave Create(int slot, string language)
        {
            ProfileValidation.RequireValidSlot(slot);
            if (!ProfileValidation.IsSupportedLanguage(language))
                throw new ArgumentException("Language must be \"en\" or \"he\".", "language");
            return new ProfileSave
            {
                schemaVersion = ProfileValidation.SchemaVersion,
                id = Guid.NewGuid().ToString("D"),
                slot = slot,
                avatar = ProfileValidation.Avatars[slot],
                colorIndex = slot,
                language = language,
                camera = "chase",
                reducedCameraShake = false,
                musicVolume = 0.65f,
                effectsVolume = 0.85f,
                voiceVolume = 1f,
                coins = 0,
                unlockedVehicles = new List<string> { "car-01", "motorcycle-01" }
            };
        }
    }
}
