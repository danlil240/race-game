namespace FamilyRacing.Core.Profiles
{
    public enum ProfileLoadStatus
    {
        Missing,
        Loaded,
        RecoveredFromBackup,
        Blocked
    }

    public sealed class ProfileLoadResult
    {
        public ProfileLoadStatus Status { get; }
        public ProfileSave Profile { get; }
        public string Diagnostic { get; }

        internal ProfileLoadResult(ProfileLoadStatus status, ProfileSave profile, string diagnostic)
        {
            Status = status;
            Profile = profile;
            Diagnostic = diagnostic;
        }
    }
}
