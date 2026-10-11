using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    public sealed class ValidationProfileDirectoryTests
    {
        private static MethodInfo Selector()
        {
            var selector = typeof(FoundationApp).GetMethod("SelectProfilesDirectory",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(selector, Is.Not.Null,
                "Validation restart needs a separately owned synthetic profile store.");
            return selector;
        }

        [Test]
        public void SyntheticRestartStoreOverrideOnlyAppliesWithValidationMode()
        {
            var selector = Selector();
            var args = new[] { "player.exe", "--validation-profile-dir", "synthetic-store" };
            Assert.That(selector.Invoke(null, new object[] { "run", "normal", args }),
                Is.EqualTo(Path.GetFullPath("synthetic-store")));
            Assert.That(selector.Invoke(null, new object[] { null, "normal", args }),
                Is.EqualTo(Path.Combine("normal", "Profiles")));
            Assert.That(selector.Invoke(null, new object[] { "run", "normal", new string[0] }),
                Is.EqualTo(Path.Combine("run", "profiles")));
        }

        [Test]
        public void ValidationOverrideWithSpacesIsNormalizedToAnAbsolutePath()
        {
            var selector = Selector();
            var relative = Path.Combine("synthetic store", "shared");
            var args = new[] { "player.exe", "--validation-profile-dir", relative };
            var selected = (string)selector.Invoke(null, new object[] { "run", "normal", args });
            Assert.That(selected, Is.EqualTo(Path.GetFullPath(relative)));
            Assert.That(Path.IsPathRooted(selected), Is.True);
        }

        [Test]
        public void MalformedValidationOverrideIsRejectedWithoutCreatingDirectories()
        {
            var selector = Selector();
            var store = Path.Combine(Path.GetTempPath(),
                "select-profiles-" + Guid.NewGuid().ToString("N"));
            Assert.That(selector.Invoke(null, new object[] { "run", "normal",
                    new[] { "player.exe", "--validation-profile-dir", store } }),
                Is.EqualTo(store));
            var malformed = new[]
            {
                new[] { "player.exe", "--validation-profile-dir" },
                new[] { "player.exe", "--validation-profile-dir", "" },
                new[] { "player.exe", "--validation-profile-dir", "   " },
                new[] { "player.exe", "--validation-profile-dir", "--validation-dir" },
                new[] { "player.exe", "--validation-profile-dir", store,
                    "--validation-profile-dir", store },
            };
            foreach (var args in malformed)
            {
                var error = Assert.Throws<TargetInvocationException>(
                    () => selector.Invoke(null, new object[] { "run", "normal", args }));
                Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
            }
            Assert.That(Directory.Exists(store), Is.False,
                "Selecting a synthetic store must not precreate it.");
        }

        [Test]
        public void OrdinaryLaunchIgnoresEvenAMalformedOverride()
        {
            var selector = Selector();
            var malformed = new[] { "player.exe", "--validation-profile-dir", "--validation-dir" };
            Assert.That(selector.Invoke(null, new object[] { null, "normal", malformed }),
                Is.EqualTo(Path.Combine("normal", "Profiles")));
        }

        [Test]
        public void NormalProfilesFlagSelectsTheRealUserStoreInValidationMode()
        {
            var selector = Selector();
            var args = new[] { "player.exe", "--validation-dir", "run",
                "--validation-normal-profiles" };
            Assert.That(selector.Invoke(null, new object[] { "run", "normal", args }),
                Is.EqualTo(Path.Combine("normal", "Profiles")));
            // Without the flag the synthetic per-run store is still selected.
            Assert.That(selector.Invoke(null, new object[] { "run", "normal",
                    new[] { "player.exe", "--validation-dir", "run" } }),
                Is.EqualTo(Path.Combine("run", "profiles")));
        }

        [Test]
        public void NormalProfilesFlagIsMutuallyExclusiveWithProfileDirOverride()
        {
            var selector = Selector();
            var both = new[]
            {
                new[] { "player.exe", "--validation-normal-profiles",
                    "--validation-profile-dir", "synthetic-store" },
                new[] { "player.exe", "--validation-profile-dir", "synthetic-store",
                    "--validation-normal-profiles" },
            };
            foreach (var args in both)
            {
                var error = Assert.Throws<TargetInvocationException>(
                    () => selector.Invoke(null, new object[] { "run", "normal", args }));
                Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
            }
        }

        [Test]
        public void DuplicateNormalProfilesFlagIsRejected()
        {
            var selector = Selector();
            var args = new[] { "player.exe", "--validation-normal-profiles",
                "--validation-normal-profiles" };
            var error = Assert.Throws<TargetInvocationException>(
                () => selector.Invoke(null, new object[] { "run", "normal", args }));
            Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
        }

        [Test]
        public void OrdinaryLaunchIgnoresNormalProfilesFlagAndConflictingFlags()
        {
            var selector = Selector();
            Assert.That(selector.Invoke(null, new object[] { null, "normal",
                    new[] { "player.exe", "--validation-normal-profiles" } }),
                Is.EqualTo(Path.Combine("normal", "Profiles")));
            // Even a duplicate plus a conflicting override is ignored: without
            // --validation-dir the arguments are never inspected.
            var conflicting = new[] { "player.exe", "--validation-normal-profiles",
                "--validation-normal-profiles",
                "--validation-profile-dir", "synthetic-store" };
            Assert.That(selector.Invoke(null, new object[] { null, "normal", conflicting }),
                Is.EqualTo(Path.Combine("normal", "Profiles")));
        }
    }
}
