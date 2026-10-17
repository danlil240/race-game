using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.EditorTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FamilyRacing.Tests
{
    // M7 Task 1: the family release identity must be repeatable and the
    // version progression monotonic. These tests pin the contract that
    // generic setup can never reset an applied release version, that a
    // changed application identity or a non-increasing Android version
    // code is rejected without any partial settings mutation, and that a
    // failed release build leaves temporary editor flags restored.
    //
    // PlayerSettings/EditorUserBuildSettings are global editor state:
    // every test restores what it touches so ProjectSettings.asset and
    // editor preferences are never left dirty by a run.
    public sealed class FamilyReleaseSettingsTests
    {
        private string priorBundleVersion;
        private int priorVersionCode;
        private string priorApplicationId;
        private string priorCompany;
        private string priorProduct;
        private AndroidSdkVersions priorMinSdk;
        private AndroidSdkVersions priorTargetSdk;
        private bool priorExportAsProject;
        private bool priorBuildAppBundle;
        private readonly List<string> tempFiles = new List<string>();

        private static UnityEditor.Build.NamedBuildTarget Android
        {
            get { return UnityEditor.Build.NamedBuildTarget.Android; }
        }

        [SetUp]
        public void SetUp()
        {
            priorBundleVersion = PlayerSettings.bundleVersion;
            priorVersionCode = PlayerSettings.Android.bundleVersionCode;
            priorApplicationId = PlayerSettings.GetApplicationIdentifier(Android);
            priorCompany = PlayerSettings.companyName;
            priorProduct = PlayerSettings.productName;
            priorMinSdk = PlayerSettings.Android.minSdkVersion;
            priorTargetSdk = PlayerSettings.Android.targetSdkVersion;
            priorExportAsProject = EditorUserBuildSettings.exportAsGoogleAndroidProject;
            priorBuildAppBundle = EditorUserBuildSettings.buildAppBundle;
        }

        [TearDown]
        public void TearDown()
        {
            PlayerSettings.bundleVersion = priorBundleVersion;
            PlayerSettings.Android.bundleVersionCode = priorVersionCode;
            PlayerSettings.SetApplicationIdentifier(Android, priorApplicationId);
            PlayerSettings.companyName = priorCompany;
            PlayerSettings.productName = priorProduct;
            PlayerSettings.Android.minSdkVersion = priorMinSdk;
            PlayerSettings.Android.targetSdkVersion = priorTargetSdk;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = priorExportAsProject;
            EditorUserBuildSettings.buildAppBundle = priorBuildAppBundle;
            foreach (var path in tempFiles)
                if (File.Exists(path)) File.Delete(path);
            tempFiles.Clear();
        }

        private static string ConfigPath
        {
            get { return FamilyReleaseSettings.DefaultConfigPath; }
        }

        private static JObject LoadConfig()
        {
            Assert.That(File.Exists(ConfigPath), Is.True,
                "Release/release.json must be tracked in the repository.");
            return JObject.Parse(File.ReadAllText(ConfigPath));
        }

        private string WriteTempConfig(JObject config)
        {
            var path = Path.Combine(Path.GetTempPath(),
                "family-release-test-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, config.ToString());
            tempFiles.Add(path);
            return path;
        }

        private static string Joined(IReadOnlyList<string> reasons)
        {
            return reasons == null ? "" : string.Join("\n", reasons);
        }

        // The tracked config must satisfy its own contract — the release
        // identity source can never drift invalid.
        [Test]
        public void TrackedReleaseConfigIsValid()
        {
            var reasons = FamilyReleaseSettings.Validate(ConfigPath);
            Assert.That(Joined(reasons), Is.EqualTo(""),
                "The tracked Release/release.json must pass validation.");
        }

        // The defect this task fixes: FoundationProjectSetup used to reset
        // bundleVersion to 0.1.0 on every pass. With a release config the
        // declared version must survive repeated generic setup.
        [Test]
        public void RepeatedSetupPreservesReleaseVersion()
        {
            var config = LoadConfig();
            var expectedName = (string)config["versionName"];
            var expectedCode = (int)config["androidVersionCode"];
            var expectedMinSdk = (int)config["androidMinSdkVersion"];
            var expectedTargetSdk = (int)config["androidTargetSdkVersion"];

            // Dirty the version fields: only a release-aware setup may
            // restore them, and it must restore the declared values.
            PlayerSettings.bundleVersion = "0.0.1-dirtied";
            PlayerSettings.Android.bundleVersionCode = 77;

            FamilyReleaseSettings.Apply(ConfigPath);
            FoundationProjectSetup.Create();
            FoundationProjectSetup.Create();

            Assert.That(PlayerSettings.bundleVersion, Is.EqualTo(expectedName),
                "Repeated setup must not reset the release bundle version.");
            Assert.That(PlayerSettings.Android.bundleVersionCode,
                Is.EqualTo(expectedCode),
                "Repeated setup must not reset the release version code.");
            Assert.That(PlayerSettings.GetApplicationIdentifier(Android),
                Is.EqualTo((string)config["applicationId"]),
                "Application identity must stay fixed.");
            Assert.That(PlayerSettings.companyName,
                Is.EqualTo((string)config["companyName"]),
                "Company identity must stay fixed.");
            Assert.That(PlayerSettings.productName,
                Is.EqualTo((string)config["productName"]),
                "Product identity must stay fixed.");
            Assert.That((int)PlayerSettings.Android.minSdkVersion,
                Is.EqualTo(expectedMinSdk));
            Assert.That((int)PlayerSettings.Android.targetSdkVersion,
                Is.EqualTo(expectedTargetSdk));
        }

        // The family identity is fixed so updates preserve saves: a config
        // that changes it must be rejected wholesale — no field may be
        // partially applied first.
        [Test]
        public void ReleaseRejectsChangedApplicationIdentity()
        {
            var config = LoadConfig();
            config["applicationId"] = "com.daniel.other-identity";
            var path = WriteTempConfig(config);

            var reasons = FamilyReleaseSettings.Validate(path);
            Assert.That(reasons, Is.Not.Null.And.Not.Empty,
                "A changed application identity must be reported invalid.");
            Assert.That(Joined(reasons), Does.Contain("applicationId"),
                "The rejection must name the applicationId field; got:\n"
                + Joined(reasons));

            // Sentinel values make any partial mutation observable.
            PlayerSettings.bundleVersion = "0.0.0-sentinel";
            PlayerSettings.Android.bundleVersionCode = 99;
            PlayerSettings.SetApplicationIdentifier(Android, "com.test.sentinel");
            PlayerSettings.companyName = "Sentinel Co";
            PlayerSettings.productName = "Sentinel Product";

            Assert.Throws<InvalidDataException>(
                () => FamilyReleaseSettings.Apply(path));

            Assert.That(PlayerSettings.bundleVersion,
                Is.EqualTo("0.0.0-sentinel"),
                "An invalid config must not mutate bundleVersion.");
            Assert.That(PlayerSettings.Android.bundleVersionCode,
                Is.EqualTo(99),
                "An invalid config must not mutate the version code.");
            Assert.That(PlayerSettings.GetApplicationIdentifier(Android),
                Is.EqualTo("com.test.sentinel"),
                "An invalid config must not mutate the application id.");
            Assert.That(PlayerSettings.companyName,
                Is.EqualTo("Sentinel Co"));
            Assert.That(PlayerSettings.productName,
                Is.EqualTo("Sentinel Product"));
        }

        // Android orders installs by versionCode: an attempted code equal
        // to or below the declared predecessor must be rejected so an
        // update can never ship a non-increasing code.
        [Test]
        public void ReleaseRejectsNonIncreasingVersionCode()
        {
            var config = LoadConfig();
            var code = (int)config["androidVersionCode"];

            config["previousAndroidVersionCode"] = code;
            var equalPath = WriteTempConfig(config);
            var reasons = FamilyReleaseSettings.Validate(equalPath);
            Assert.That(Joined(reasons), Does.Contain("androidVersionCode"),
                "A version code equal to the predecessor must be rejected; got:\n"
                + Joined(reasons));
            Assert.Throws<InvalidDataException>(
                () => FamilyReleaseSettings.Apply(equalPath));

            config["previousAndroidVersionCode"] = code + 5;
            var belowPath = WriteTempConfig(config);
            reasons = FamilyReleaseSettings.Validate(belowPath);
            Assert.That(Joined(reasons), Does.Contain("androidVersionCode"),
                "A version code below the predecessor must be rejected; got:\n"
                + Joined(reasons));
            Assert.Throws<InvalidDataException>(
                () => FamilyReleaseSettings.Apply(belowPath));
        }

        // Release build methods flip temporary editor build flags inside a
        // try and restore them in finally: a failed release build can never
        // leave the editor pointed at a different export mode. Both flags
        // are seeded to the opposite of what the export sets, so a missing
        // restore is observable.
        [Test]
        public void BuildRestoresSettingsAfterFailure()
        {
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
            EditorUserBuildSettings.buildAppBundle = true;
            try
            {
                // The export must fail honestly here: the production
                // full-content gate has no authored inventory to pass, and
                // no --android-export-dir argument exists in a test run.
                // The failure is expected; only the state afterwards is.
                Assert.That(() => FoundationBuild.ExportAndroidFamilyRelease(),
                    Throws.Exception);
            }
            finally
            {
                Assert.That(
                    EditorUserBuildSettings.exportAsGoogleAndroidProject,
                    Is.False,
                    "A failed release build must restore the export flag.");
                Assert.That(EditorUserBuildSettings.buildAppBundle, Is.True,
                    "A failed release build must restore the app-bundle flag.");
            }
        }
    }
}
