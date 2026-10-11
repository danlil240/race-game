using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Profiles
{
    [Serializable]
    public sealed class ProfileSave
    {
        public int schemaVersion = 1;
        public string id;
        public int slot;
        public string avatar;
        public int colorIndex;
        public string language;
        public string camera;
        public bool reducedCameraShake;
        public float musicVolume;
        public float effectsVolume;
        public float voiceVolume;
        public int coins;
        public List<string> unlockedVehicles;
    }
}
