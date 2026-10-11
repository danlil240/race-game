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
    }
}
