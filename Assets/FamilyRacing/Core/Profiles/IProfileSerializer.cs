namespace FamilyRacing.Core.Profiles
{
    public interface IProfileSerializer
    {
        string Serialize(ProfileSave profile);
        ProfileSave Deserialize(string json);
    }
}
