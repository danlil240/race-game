using FamilyRacing.Core.Profiles;
using Newtonsoft.Json;
using UnityEngine;

namespace FamilyRacing
{
    // Unity-side profile persistence adapter: JsonUtility produces the
    // on-disk file shape; Newtonsoft.Json performs strict deserialization
    // because JsonUtility cannot distinguish a stored null collection from
    // an empty one, and Core validation requires the null to be blocked.
    public sealed class UnityProfileSerializer : IProfileSerializer
    {
        public string Serialize(ProfileSave profile)
        {
            return JsonUtility.ToJson(profile);
        }

        public ProfileSave Deserialize(string json)
        {
            return JsonConvert.DeserializeObject<ProfileSave>(json);
        }
    }
}
