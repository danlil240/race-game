using System;
using System.IO;
using System.Text;

namespace FamilyRacing.Core.Profiles
{
    public sealed class ProfileStore
    {
        private readonly string directory;
        private readonly IProfileSerializer serializer;

        public ProfileStore(string directory, IProfileSerializer serializer)
        {
            if (directory == null) throw new ArgumentNullException("directory");
            if (serializer == null) throw new ArgumentNullException("serializer");
            this.directory = directory;
            this.serializer = serializer;
        }

        public ProfileLoadResult Load(int slot)
        {
            ProfileValidation.RequireValidSlot(slot);
            var primary = Read(PrimaryPath(slot), slot);
            if (primary.Check == ProfileCheck.Valid)
                return new ProfileLoadResult(ProfileLoadStatus.Loaded, primary.Profile, "");
            if (primary.Check == ProfileCheck.NewerSchema)
                return Blocked("The save was written by a newer version of the game and was left untouched.");
            var backup = Read(BackupPath(slot), slot);
            if (backup.Check == ProfileCheck.Valid)
                return new ProfileLoadResult(ProfileLoadStatus.RecoveredFromBackup, backup.Profile,
                    "The save was unreadable, so the profile was loaded from its backup; the original files were left unchanged.");
            if (primary.Check == ProfileCheck.Missing)
                return backup.Check == ProfileCheck.Missing
                    ? new ProfileLoadResult(ProfileLoadStatus.Missing, null, "")
                    : Blocked("The save is missing and its backup cannot be used.");
            return Blocked("The save could not be read and no usable backup exists.");
        }

        public void Save(ProfileSave profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            ProfileValidation.RequireValidSlot(profile.slot);
            ProfileValidation.RequireValidForSave(profile);

            var json = SerializeChecked(profile);
            EnsureWritableTargets(profile);

            var primary = PrimaryPath(profile.slot);
            var temp = primary + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                WriteTemp(temp, json);
                if (File.Exists(primary))
                    File.Replace(temp, primary, BackupPath(profile.slot));
                else
                    File.Move(temp, primary);
            }
            catch (InvalidOperationException)
            {
                TryDelete(temp);
                throw;
            }
            catch (Exception e)
            {
                TryDelete(temp);
                throw new InvalidOperationException(
                    "The save could not be completed. Existing data was left unchanged.", e);
            }
        }

        private static ProfileLoadResult Blocked(string diagnostic)
        {
            return new ProfileLoadResult(ProfileLoadStatus.Blocked, null, diagnostic);
        }

        private string PrimaryPath(int slot)
        {
            return Path.Combine(directory, "profile-" + slot + ".json");
        }

        private string BackupPath(int slot)
        {
            return PrimaryPath(slot) + ".bak";
        }

        private ProfileRead Read(string path, int slot)
        {
            if (!File.Exists(path)) return new ProfileRead(ProfileCheck.Missing, null);
            try
            {
                var profile = serializer.Deserialize(File.ReadAllText(path));
                var check = ProfileValidation.Check(profile, slot);
                // Schema upgrades happen in memory only: an older valid
                // file migrates forward on read and keeps its on-disk
                // bytes until an explicit successful save rewrites it.
                if (check == ProfileCheck.Valid && ProfileMigration.Migrate(profile))
                    check = ProfileValidation.Check(profile, slot);
                return new ProfileRead(check, check == ProfileCheck.Valid ? profile : null);
            }
            catch
            {
                return new ProfileRead(ProfileCheck.Invalid, null);
            }
        }

        private string SerializeChecked(ProfileSave profile)
        {
            string json;
            try
            {
                json = serializer.Serialize(profile);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("The profile could not be serialized.", e);
            }
            if (json == null)
                throw new InvalidOperationException("The serializer returned no data.");
            ProfileSave written;
            try
            {
                written = serializer.Deserialize(json);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("The serialized data could not be verified.", e);
            }
            if (ProfileValidation.Check(written, profile.slot) != ProfileCheck.Valid
                || written.id != profile.id
                || written.schemaVersion != profile.schemaVersion)
                throw new InvalidOperationException("The serialized data did not verify; nothing was written.");
            return json;
        }

        // Refuse to write whenever an existing primary or backup cannot be fully
        // trusted, so corrupt, newer, foreign, or orphaned files are preserved.
        private void EnsureWritableTargets(ProfileSave profile)
        {
            var primary = Read(PrimaryPath(profile.slot), profile.slot);
            var backup = Read(BackupPath(profile.slot), profile.slot);
            if (primary.Check == ProfileCheck.Missing)
            {
                if (backup.Check != ProfileCheck.Missing)
                    throw new InvalidOperationException(
                        "A backup exists without its save; it was left untouched instead of being overwritten.");
                return;
            }
            if (primary.Check != ProfileCheck.Valid || primary.Profile.id != profile.id)
                throw new InvalidOperationException(
                    "The existing save cannot be read or belongs to a different profile; it was left untouched.");
            if (backup.Check != ProfileCheck.Missing
                && (backup.Check != ProfileCheck.Valid || backup.Profile.id != profile.id))
                throw new InvalidOperationException(
                    "The existing backup cannot be read; it was left untouched.");
        }

        private static void WriteTemp(string path, string json)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Best effort cleanup of a temp file this save created.
            }
        }

        private struct ProfileRead
        {
            internal ProfileCheck Check;
            internal ProfileSave Profile;

            internal ProfileRead(ProfileCheck check, ProfileSave profile)
            {
                Check = check;
                Profile = profile;
            }
        }
    }
}
