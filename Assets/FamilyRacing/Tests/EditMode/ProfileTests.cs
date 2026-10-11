using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Profiles;
using NUnit.Framework;
#if UNITY_5_3_OR_NEWER
using UnityEngine;
#else
using System.Text.Json;
#endif

namespace FamilyRacing.Tests
{
    // The same tests exercise real files with either Unity JsonUtility or System.Text.Json.
    public sealed class ProfileTests
    {
        private string directory;
        private IProfileSerializer serializer;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "family-racing-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            serializer = new TestJsonSerializer();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        [TestCase(0, "fox")]
        [TestCase(1, "rabbit")]
        [TestCase(2, "panda")]
        [TestCase(3, "raccoon")]
        public void CreateAssignsEachSlotsDefaults(int slot, string avatar)
        {
            var profile = ProfileFactory.Create(slot, "he");
            Assert.That(profile.slot, Is.EqualTo(slot));
            Assert.That(profile.avatar, Is.EqualTo(avatar));
            Assert.That(profile.colorIndex, Is.EqualTo(slot));
            Assert.That(profile.schemaVersion, Is.EqualTo(1));
            Assert.That(Guid.TryParseExact(profile.id, "D", out _), Is.True);
            Assert.That(profile.language, Is.EqualTo("he"));
            Assert.That(profile.camera, Is.EqualTo("chase"));
            Assert.That(profile.reducedCameraShake, Is.False);
            Assert.That(profile.musicVolume, Is.EqualTo(0.65f));
            Assert.That(profile.effectsVolume, Is.EqualTo(0.85f));
            Assert.That(profile.voiceVolume, Is.EqualTo(1f));
            Assert.That(profile.coins, Is.Zero);
            Assert.That(profile.unlockedVehicles, Is.EquivalentTo(new[] { "car-01", "motorcycle-01" }));
        }

        [Test]
        public void CreateGeneratesDistinctProfileIdentities()
        {
            Assert.That(ProfileFactory.Create(0, "en").id, Is.Not.EqualTo(ProfileFactory.Create(0, "en").id));
        }

        [TestCase(-1)]
        [TestCase(4)]
        [TestCase(int.MinValue)]
        [TestCase(int.MaxValue)]
        public void CreateRejectsInvalidSlots(int slot)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ProfileFactory.Create(slot, "en"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("fr")]
        [TestCase("EN")]
        public void CreateRequiresExplicitSupportedLanguage(string language)
        {
            Assert.Throws<ArgumentException>(() => ProfileFactory.Create(0, language));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void SettingsAndIdentitySurviveAFreshStore(int slot)
        {
            var profile = ValidProfile(slot);
            profile.avatar = "raccoon";
            profile.colorIndex = 3;
            profile.language = "he";
            profile.camera = "overhead";
            profile.reducedCameraShake = true;
            profile.musicVolume = 0;
            profile.effectsVolume = 0.3f;
            profile.voiceVolume = 1;
            profile.coins = 123;
            profile.unlockedVehicles.Add("car-02");
            Store().Save(profile);
            var result = Store().Load(slot);
            Assert.That(result.Status, Is.EqualTo(ProfileLoadStatus.Loaded));
            Assert.That(result.Profile.id, Is.EqualTo(profile.id));
            Assert.That(result.Profile.slot, Is.EqualTo(slot));
            Assert.That(result.Profile.avatar, Is.EqualTo("raccoon"));
            Assert.That(result.Profile.colorIndex, Is.EqualTo(3));
            Assert.That(result.Profile.language, Is.EqualTo("he"));
            Assert.That(result.Profile.camera, Is.EqualTo("overhead"));
            Assert.That(result.Profile.reducedCameraShake, Is.True);
            Assert.That(result.Profile.musicVolume, Is.Zero);
            Assert.That(result.Profile.effectsVolume, Is.EqualTo(0.3f));
            Assert.That(result.Profile.voiceVolume, Is.EqualTo(1));
            Assert.That(result.Profile.coins, Is.EqualTo(123));
            Assert.That(result.Profile.unlockedVehicles, Is.EquivalentTo(new[] { "car-01", "motorcycle-01", "car-02" }));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
            Assert.That(File.Exists(Primary(slot)), Is.True);
        }

        [Test]
        public void SlotsSaveIndependently()
        {
            for (var slot = 0; slot < 4; slot++)
            {
                var profile = ValidProfile(slot);
                profile.coins = slot * 10;
                Store().Save(profile);
            }
            for (var slot = 0; slot < 4; slot++)
                Assert.That(Store().Load(slot).Profile.coins, Is.EqualTo(slot * 10));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(4));
        }

        [TestCase(-1)]
        [TestCase(4)]
        [TestCase(int.MinValue)]
        [TestCase(int.MaxValue)]
        public void InvalidSlotNeverConstructsOrWritesAPath(int slot)
        {
            var profile = ValidProfile(0);
            profile.slot = slot;
            Assert.Throws<ArgumentOutOfRangeException>(() => Store().Load(slot));
            Assert.Throws<ArgumentOutOfRangeException>(() => Store().Save(profile));
            Assert.That(Directory.GetFiles(directory), Is.Empty);
        }

        [Test]
        public void MissingDirectoryIsMissingWithoutCreatingIt()
        {
            Directory.Delete(directory);
            var result = Store().Load(0);
            Assert.That(result.Status, Is.EqualTo(ProfileLoadStatus.Missing));
            Assert.That(result.Profile, Is.Null);
            Assert.That(Directory.Exists(directory), Is.False);
        }

        [Test]
        public void FirstSaveCreatesTheSuppliedDirectory()
        {
            Directory.Delete(directory);
            Store().Save(ValidProfile(0));
            Assert.That(Store().Load(0).Status, Is.EqualTo(ProfileLoadStatus.Loaded));
        }

        [Test]
        public void SecondSaveKeepsLastKnownGoodBackup()
        {
            var profile = ValidProfile(0);
            Store().Save(profile);
            var original = File.ReadAllText(Primary(0));
            profile.coins = 50;
            Store().Save(profile);
            Assert.That(Store().Load(0).Profile.coins, Is.EqualTo(50));
            Assert.That(File.ReadAllText(Backup(0)), Is.EqualTo(original));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(2));
        }

        [Test]
        public void ThirdSaveRotatesBackupToPreviousValidPrimary()
        {
            var profile = ValidProfile(0);
            Store().Save(profile);
            profile.coins = 50;
            Store().Save(profile);
            var second = File.ReadAllText(Primary(0));
            profile.coins = 100;
            Store().Save(profile);
            Assert.That(File.ReadAllText(Backup(0)), Is.EqualTo(second));
            Assert.That(Store().Load(0).Profile.coins, Is.EqualTo(100));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ValidBackupRecoversMissingOrCorruptPrimaryWithoutAlteringFiles(bool missing)
        {
            File.WriteAllText(Backup(0), serializer.Serialize(ValidProfile(0)));
            if (!missing) File.WriteAllText(Primary(0), "broken private data");
            var result = Store().Load(0);
            Assert.That(result.Status, Is.EqualTo(ProfileLoadStatus.RecoveredFromBackup));
            Assert.That(result.Diagnostic, Does.Contain("backup").And.Not.Contain("restored"));
            Assert.That(result.Profile.id, Is.EqualTo("11111111-1111-4111-8111-111111111111"));
            Assert.That(File.Exists(Primary(0)), Is.EqualTo(!missing));
            if (!missing) Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo("broken private data"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnreadableDataBlocksWithoutResetting(bool hasBackup)
        {
            File.WriteAllText(Primary(0), "broken private data");
            if (hasBackup) File.WriteAllText(Backup(0), "also broken private data");
            var result = Store().Load(0);
            Assert.That(result.Status, Is.EqualTo(ProfileLoadStatus.Blocked));
            Assert.That(result.Profile, Is.Null);
            Assert.That(result.Diagnostic, Is.Not.Empty);
            Assert.That(result.Diagnostic, Does.Not.Contain("private data").And.Not.Contain(directory));
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo("broken private data"));
        }

        [Test]
        public void FuturePrimaryBlocksEvenWithValidOlderBackup()
        {
            var future = ValidProfile(0);
            future.schemaVersion = 2;
            File.WriteAllText(Primary(0), serializer.Serialize(future));
            File.WriteAllText(Backup(0), serializer.Serialize(ValidProfile(0)));
            var before = File.ReadAllText(Primary(0));
            var result = Store().Load(0);
            Assert.That(result.Status, Is.EqualTo(ProfileLoadStatus.Blocked));
            Assert.That(result.Profile, Is.Null);
            Assert.That(result.Diagnostic, Does.Contain("newer"));
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(before));
        }

        [Test]
        public void FutureBackupDoesNotPreventReadingValidPrimary()
        {
            File.WriteAllText(Primary(0), serializer.Serialize(ValidProfile(0)));
            var future = ValidProfile(0);
            future.schemaVersion = 2;
            File.WriteAllText(Backup(0), serializer.Serialize(future));
            Assert.That(Store().Load(0).Status, Is.EqualTo(ProfileLoadStatus.Loaded));
        }

        [TestCase("corrupt")]
        [TestCase("future")]
        [TestCase("invalid")]
        public void SaveRefusesDestructiveOverwriteOfBlockedPrimary(string kind)
        {
            var blocked = ValidProfile(0);
            if (kind == "future") blocked.schemaVersion = 2;
            if (kind == "invalid") blocked.coins = -1;
            var original = kind == "corrupt" ? "private corrupt content" : serializer.Serialize(blocked);
            File.WriteAllText(Primary(0), original);
            var backup = serializer.Serialize(ValidProfile(0));
            File.WriteAllText(Backup(0), backup);
            var exception = Assert.Throws<InvalidOperationException>(() => Store().Save(ValidProfile(0)));
            Assert.That(exception.Message, Does.Not.Contain(directory).And.Not.Contain("private corrupt content"));
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(original));
            Assert.That(File.ReadAllText(Backup(0)), Is.EqualTo(backup));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SavePreservesInvalidOrFutureBackup(bool future)
        {
            File.WriteAllText(Primary(0), serializer.Serialize(ValidProfile(0)));
            var profile = ValidProfile(0);
            profile.schemaVersion = 2;
            var backup = future ? serializer.Serialize(profile) : "corrupt backup";
            File.WriteAllText(Backup(0), backup);
            var original = File.ReadAllText(Primary(0));
            Assert.Throws<InvalidOperationException>(() => Store().Save(ValidProfile(0)));
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(original));
            Assert.That(File.ReadAllText(Backup(0)), Is.EqualTo(backup));
        }

        [Test]
        public void SaveRefusesReplacingAnExistingProfileIdentity()
        {
            Store().Save(ValidProfile(0));
            var replacement = ValidProfile(0);
            replacement.id = "22222222-2222-4222-8222-222222222222";
            Assert.Throws<InvalidOperationException>(() => Store().Save(replacement));
            Assert.That(Store().Load(0).Profile.id, Is.EqualTo("11111111-1111-4111-8111-111111111111"));
        }

        [Test]
        public void SaveRefusesCreatingPrimaryOverAnOrphanedBackup()
        {
            var backup = serializer.Serialize(ValidProfile(0));
            File.WriteAllText(Backup(0), backup);
            Assert.Throws<InvalidOperationException>(() => Store().Save(ValidProfile(0)));
            Assert.That(File.Exists(Primary(0)), Is.False);
            Assert.That(File.ReadAllText(Backup(0)), Is.EqualTo(backup));
        }

        [Test]
        public void SerializerFailureLeavesPreviousSaveAndBackupUntouched()
        {
            var profile = ValidProfile(0);
            Store().Save(profile);
            profile.coins = 5;
            Store().Save(profile);
            var original = File.ReadAllText(Primary(0));
            var backup = File.ReadAllText(Backup(0));
            var broken = new ProfileStore(directory, new FailingSerializer(serializer));
            var exception = Assert.Throws<InvalidOperationException>(() => broken.Save(profile));
            Assert.That(exception.Message, Does.Not.Contain("private serializer data").And.Not.Contain(directory));
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(original));
            Assert.That(File.ReadAllText(Backup(0)), Is.EqualTo(backup));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(2));
        }

        [Test]
        public void SerializerReturningInvalidSaveCannotWriteAFile()
        {
            var broken = new ProfileStore(directory, new InvalidOutputSerializer(serializer));
            Assert.Throws<InvalidOperationException>(() => broken.Save(ValidProfile(0)));
            Assert.That(Directory.GetFiles(directory), Is.Empty);
        }

        [Test]
        public void DiskWriteFailureLeavesPreviousSaveUntouched()
        {
            var profile = ValidProfile(0);
            Store().Save(profile);
            var original = File.ReadAllText(Primary(0));
            // Deny replacement using an actual file lock rather than mocking the filesystem.
            using (new FileStream(Primary(0), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                profile.coins = 100;
                Assert.Throws<InvalidOperationException>(() => Store().Save(profile));
            }
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(original));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        }

        [Test]
        public void StaleTemporaryFileIsNeverLoadedOrDeleted()
        {
            File.WriteAllText(Path.Combine(directory, "profile-0.json.abandoned.tmp"), "interrupted write");
            Assert.That(Store().Load(0).Status, Is.EqualTo(ProfileLoadStatus.Missing));
            Store().Save(ValidProfile(0));
            Assert.That(File.ReadAllText(Path.Combine(directory, "profile-0.json.abandoned.tmp")), Is.EqualTo("interrupted write"));
        }

        [Test]
        public void NullProfileCannotWriteAFile()
        {
            Assert.Throws<ArgumentNullException>(() => Store().Save(null));
            Assert.That(Directory.GetFiles(directory), Is.Empty);
        }

        // Removing any named invariant should let a broken profile load/save and fail these cases.
        [TestCase("schema")]
        [TestCase("futureSchema")]
        [TestCase("nullId")]
        [TestCase("emptyId")]
        [TestCase("malformedId")]
        [TestCase("emptyGuid")]
        [TestCase("avatar")]
        [TestCase("colorLow")]
        [TestCase("colorHigh")]
        [TestCase("language")]
        [TestCase("camera")]
        [TestCase("coins")]
        [TestCase("musicLow")]
        [TestCase("effectsHigh")]
        [TestCase("voiceNaN")]
        [TestCase("musicInfinity")]
        [TestCase("effectsNegativeInfinity")]
        [TestCase("nullVehicles")]
        [TestCase("nullVehicle")]
        [TestCase("emptyVehicle")]
        [TestCase("spaceVehicle")]
        [TestCase("whitespaceVehicle")]
        [TestCase("newlineVehicle")]
        [TestCase("pathVehicle")]
        public void InvalidProfileCannotOverwritePreviousGoodSave(string invariant)
        {
            Store().Save(ValidProfile(0));
            var before = File.ReadAllText(Primary(0));
            var invalid = InvalidProfile(invariant);
            Assert.Throws<ArgumentException>(() => Store().Save(invalid));
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(before));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        }

        [TestCase("schema")]
        [TestCase("nullId")]
        [TestCase("emptyId")]
        [TestCase("malformedId")]
        [TestCase("emptyGuid")]
        [TestCase("avatar")]
        [TestCase("colorLow")]
        [TestCase("colorHigh")]
        [TestCase("language")]
        [TestCase("camera")]
        [TestCase("coins")]
        [TestCase("musicLow")]
        [TestCase("effectsHigh")]
        [TestCase("nullVehicles")]
        [TestCase("nullVehicle")]
        [TestCase("emptyVehicle")]
        [TestCase("spaceVehicle")]
        [TestCase("whitespaceVehicle")]
        [TestCase("newlineVehicle")]
        [TestCase("pathVehicle")]
        public void InvalidSerializedProfileBlocksLoading(string invariant)
        {
            File.WriteAllText(Primary(0), InvalidPayload(invariant));
            Assert.That(Store().Load(0).Status, Is.EqualTo(ProfileLoadStatus.Blocked));
            Assert.That(Store().Load(0).Profile, Is.Null);
        }

        [Test]
        public void ProfileInWrongSlotBlocksLoading()
        {
            File.WriteAllText(Primary(0), serializer.Serialize(ValidProfile(1)));
            Assert.That(Store().Load(0).Status, Is.EqualTo(ProfileLoadStatus.Blocked));
        }

        [Test]
        public void InvalidBackupCannotRecoverCorruptPrimary()
        {
            File.WriteAllText(Primary(0), "corrupt");
            File.WriteAllText(Backup(0), serializer.Serialize(InvalidProfile("coins")));
            Assert.That(Store().Load(0).Status, Is.EqualTo(ProfileLoadStatus.Blocked));
        }

        [Test]
        public void SaveFailsSafelyWhenDirectoryPathIsAnExistingFile()
        {
            var filePath = Path.Combine(directory, "occupied");
            File.WriteAllText(filePath, "occupied path content");
            var store = new ProfileStore(filePath, serializer);
            var exception = Assert.Throws<InvalidOperationException>(() => store.Save(ValidProfile(0)));
            Assert.That(exception.Message, Does.Not.Contain(filePath).And.Not.Contain("occupied path content"));
            Assert.That(File.ReadAllText(filePath), Is.EqualTo("occupied path content"));
        }

        // Coverage additions: verified serialized output must retain the
        // submitted identity and slot before any disk write happens.
        [Test]
        public void SaveRefusesSerializerOutputWithChangedIdentity()
        {
            Store().Save(ValidProfile(0));
            var original = File.ReadAllText(Primary(0));
            var changed = new ProfileStore(directory, new MutatingOutputSerializer(serializer,
                p => { p.id = "22222222-2222-4222-8222-222222222222"; return p; }));
            Assert.Throws<InvalidOperationException>(() => changed.Save(ValidProfile(0)));
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(original));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        }

        [Test]
        public void SaveRefusesSerializerOutputWithChangedSlot()
        {
            Store().Save(ValidProfile(0));
            var original = File.ReadAllText(Primary(0));
            var changed = new ProfileStore(directory, new MutatingOutputSerializer(serializer,
                p => { p.slot = 1; return p; }));
            Assert.Throws<InvalidOperationException>(() => changed.Save(ValidProfile(0)));
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(original));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        }

        // JsonUtility cannot serialize a null List field (it emits []), so the
        // null-vehicles stored-file invariant writes its literal JSON payload.
        private string InvalidPayload(string invariant)
        {
            if (invariant == "nullVehicles")
                return "{\"schemaVersion\":1,\"id\":\"11111111-1111-4111-8111-111111111111\"," +
                    "\"slot\":0,\"avatar\":\"fox\",\"colorIndex\":0,\"language\":\"en\"," +
                    "\"camera\":\"chase\",\"reducedCameraShake\":false,\"musicVolume\":0.65," +
                    "\"effectsVolume\":0.85,\"voiceVolume\":1.0,\"coins\":0," +
                    "\"unlockedVehicles\":null}";
            return serializer.Serialize(InvalidProfile(invariant));
        }

        private ProfileStore Store() { return new ProfileStore(directory, serializer); }
        private string Primary(int slot) { return Path.Combine(directory, "profile-" + slot + ".json"); }
        private string Backup(int slot) { return Primary(slot) + ".bak"; }

        private static ProfileSave ValidProfile(int slot)
        {
            return new ProfileSave
            {
                schemaVersion = 1,
                id = "11111111-1111-4111-8111-111111111111",
                slot = slot,
                avatar = "fox",
                colorIndex = 0,
                language = "en",
                camera = "chase",
                musicVolume = 0.65f,
                effectsVolume = 0.85f,
                voiceVolume = 1f,
                coins = 0,
                unlockedVehicles = new List<string> { "car-01", "motorcycle-01" }
            };
        }

        private static ProfileSave InvalidProfile(string invariant)
        {
            var profile = ValidProfile(0);
            switch (invariant)
            {
                case "schema": profile.schemaVersion = 0; break;
                case "futureSchema": profile.schemaVersion = 2; break;
                case "nullId": profile.id = null; break;
                case "emptyId": profile.id = ""; break;
                case "malformedId": profile.id = "../escape"; break;
                case "emptyGuid": profile.id = Guid.Empty.ToString("D"); break;
                case "avatar": profile.avatar = "unknown"; break;
                case "colorLow": profile.colorIndex = -1; break;
                case "colorHigh": profile.colorIndex = 4; break;
                case "language": profile.language = "fr"; break;
                case "camera": profile.camera = "unknown"; break;
                case "coins": profile.coins = -1; break;
                case "musicLow": profile.musicVolume = -0.01f; break;
                case "effectsHigh": profile.effectsVolume = 1.01f; break;
                case "voiceNaN": profile.voiceVolume = float.NaN; break;
                case "musicInfinity": profile.musicVolume = float.PositiveInfinity; break;
                case "effectsNegativeInfinity": profile.effectsVolume = float.NegativeInfinity; break;
                case "nullVehicles": profile.unlockedVehicles = null; break;
                case "nullVehicle": profile.unlockedVehicles.Add(null); break;
                case "emptyVehicle": profile.unlockedVehicles.Add(""); break;
                case "spaceVehicle": profile.unlockedVehicles.Add("car 02"); break;
                case "whitespaceVehicle": profile.unlockedVehicles.Add("   "); break;
                case "newlineVehicle": profile.unlockedVehicles.Add("car-02\n"); break;
                case "pathVehicle": profile.unlockedVehicles.Add("../car-02"); break;
                default: throw new ArgumentException("Unknown test invariant.");
            }
            return profile;
        }

        private sealed class TestJsonSerializer : IProfileSerializer
        {
#if UNITY_5_3_OR_NEWER
            // Mirror UnityProfileSerializer: JsonUtility for the file shape,
            // strict Newtonsoft reads (JsonUtility cannot express a stored
            // null collection and silently loads it as an empty list).
            public string Serialize(ProfileSave profile) { return JsonUtility.ToJson(profile); }
            public ProfileSave Deserialize(string json)
            {
                return Newtonsoft.Json.JsonConvert.DeserializeObject<ProfileSave>(json);
            }
#else
            private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { IncludeFields = true };
            public string Serialize(ProfileSave profile) { return JsonSerializer.Serialize(profile, Options); }
            public ProfileSave Deserialize(string json) { return JsonSerializer.Deserialize<ProfileSave>(json, Options); }
#endif
        }

        // Only serialization is substituted; persistence and validation stay real.
        private sealed class FailingSerializer : IProfileSerializer
        {
            private readonly IProfileSerializer serializer;
            public FailingSerializer(IProfileSerializer serializer) { this.serializer = serializer; }
            public string Serialize(ProfileSave profile) { throw new InvalidOperationException("private serializer data"); }
            public ProfileSave Deserialize(string json) { return serializer.Deserialize(json); }
        }

        private sealed class InvalidOutputSerializer : IProfileSerializer
        {
            private readonly IProfileSerializer serializer;
            public InvalidOutputSerializer(IProfileSerializer serializer) { this.serializer = serializer; }
            public string Serialize(ProfileSave profile) { return "invalid serialized output"; }
            public ProfileSave Deserialize(string json) { return serializer.Deserialize(json); }
        }

        // Serializes a valid but altered profile, exercising output verification.
        private sealed class MutatingOutputSerializer : IProfileSerializer
        {
            private readonly IProfileSerializer serializer;
            private readonly Func<ProfileSave, ProfileSave> mutate;
            public MutatingOutputSerializer(IProfileSerializer serializer, Func<ProfileSave, ProfileSave> mutate)
            {
                this.serializer = serializer;
                this.mutate = mutate;
            }
            public string Serialize(ProfileSave profile)
            {
                var copy = serializer.Deserialize(serializer.Serialize(profile));
                return serializer.Serialize(mutate(copy));
            }
            public ProfileSave Deserialize(string json) { return serializer.Deserialize(json); }
        }
    }
}
