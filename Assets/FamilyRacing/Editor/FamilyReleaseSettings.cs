using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace FamilyRacing.EditorTools
{
    // M7 Task 1 — durable family release identity.
    //
    // Release/release.json is the single tracked source of truth for the
    // release version progression and the fixed application identity.
    // Apply() is invoked by every release entry point AFTER generic
    // FoundationProjectSetup, and by repeatable setup itself once the
    // config exists — so the release version can never be silently reset
    // to the development default. Validate() reports every invalid
    // identity/version field; Apply() validates completely before
    // touching PlayerSettings, so an invalid config mutates nothing.
    //
    // The keystore, passwords and key paths never appear in the tracked
    // config — only the public certificate SHA-256 fingerprint, which
    // Task 2's signing verification compares against the real signer.
    public static class FamilyReleaseSettings
    {
        // The private family release identity is fixed by the milestone
        // contract — updates must keep it so installed saves survive.
        public const string FixedApplicationId = "com.daniel.familyracing";
        public const string FixedCompanyName = "Family Racing";
        public const string FixedProductName = "Family Racing";
        public const int FixedMinSdkVersion = 25;

        // Optional override for tooling/tests; unset resolves to the
        // tracked Release/release.json at the project root.
        public const string ConfigEnvironmentVariable =
            "FAMILY_RACING_RELEASE_CONFIG";

        private static readonly Regex VersionNamePattern =
            new Regex("^[0-9]+\\.[0-9]+\\.[0-9]+$");

        private static readonly Regex Sha256Pattern =
            new Regex("^[0-9a-fA-F]{64}$");

        public static string DefaultConfigPath
        {
            get
            {
                return Path.GetFullPath(Path.Combine(
                    Application.dataPath, "..", "Release", "release.json"));
            }
        }

        public static string ResolveConfigPath()
        {
            var env = Environment.GetEnvironmentVariable(
                ConfigEnvironmentVariable);
            return string.IsNullOrEmpty(env)
                ? DefaultConfigPath
                : Path.GetFullPath(env);
        }

        // "Configured" means the RESOLVED config path — an explicit
        // FAMILY_RACING_RELEASE_CONFIG override is the config this run
        // uses, so checking only the tracked default would both stamp the
        // development version over an override-only setup and apply the
        // wrong config when both exist.
        public static bool IsConfigured()
        {
            return File.Exists(ResolveConfigPath());
        }

        // Repeatable-setup hook: applies the resolved release identity
        // only when the config exists. An invalid config surfaces as an
        // InvalidDataException — setup fails honestly rather than shipping
        // a silently wrong identity.
        public static void ApplyIfConfigured()
        {
            var path = ResolveConfigPath();
            if (File.Exists(path))
                Apply(path);
        }

        // Every invalid identity/version field, as named reasons. An
        // unreadable file reports only its load failure — field checks
        // cannot run reliably without parsed values.
        public static IReadOnlyList<string> Validate(string configPath)
        {
            var reasons = new List<string>();
            var config = Load(configPath, reasons);
            if (config == null) return reasons;
            Check(config, reasons);
            return reasons;
        }

        // Validate-then-apply: throws InvalidDataException listing every
        // invalid field before a single PlayerSettings write happens.
        public static void Apply(string configPath)
        {
            var reasons = new List<string>();
            var config = Load(configPath, reasons);
            if (config != null) Check(config, reasons);
            if (reasons.Count > 0)
                throw new InvalidDataException(
                    "FamilyReleaseSettings: invalid release config "
                    + configPath + ":\n - " + string.Join("\n - ", reasons));
            ApplyValid(config);
            Debug.Log("FamilyReleaseSettings: applied version "
                + config.versionName + " (Android code "
                + config.androidVersionCode + ", id "
                + config.applicationId + ", targetSdk "
                + config.androidTargetSdkVersion + ")");
        }

        private sealed class Config
        {
            public string versionName;
            public int? androidVersionCode;
            public int? previousAndroidVersionCode;
            public string applicationId;
            public string companyName;
            public string productName;
            public int? androidMinSdkVersion;
            public int? androidTargetSdkVersion;
            public string signingKeyAlias;
            public string expectedCertificateSha256;
        }

        private static Config Load(string configPath, List<string> reasons)
        {
            if (string.IsNullOrEmpty(configPath))
            {
                reasons.Add("configPath: empty");
                return null;
            }
            if (!File.Exists(configPath))
            {
                reasons.Add("config: file missing: " + configPath);
                return null;
            }
            JObject json;
            try
            {
                json = JObject.Parse(File.ReadAllText(configPath));
            }
            catch (Exception ex)
            {
                reasons.Add("config: not valid JSON: " + ex.Message);
                return null;
            }
            var schema = ReadInt(json, "schemaVersion", reasons);
            if (schema.HasValue && schema.Value != 1)
                reasons.Add("schemaVersion: unsupported schema "
                    + schema.Value + " (expected 1)");
            var config = new Config
            {
                versionName = ReadString(json, "versionName", reasons),
                androidVersionCode =
                    ReadInt(json, "androidVersionCode", reasons),
                previousAndroidVersionCode =
                    ReadInt(json, "previousAndroidVersionCode", reasons),
                applicationId = ReadString(json, "applicationId", reasons),
                companyName = ReadString(json, "companyName", reasons),
                productName = ReadString(json, "productName", reasons),
                androidMinSdkVersion =
                    ReadInt(json, "androidMinSdkVersion", reasons),
                androidTargetSdkVersion =
                    ReadInt(json, "androidTargetSdkVersion", reasons),
                signingKeyAlias =
                    ReadOptionalString(json, "signingKeyAlias", reasons),
                expectedCertificateSha256 =
                    ReadString(json, "expectedCertificateSha256", reasons)
            };
            return config;
        }

        private static void Check(Config config, List<string> reasons)
        {
            if (config.applicationId != null
                && config.applicationId != FixedApplicationId)
                reasons.Add("applicationId: \"" + config.applicationId
                    + "\" must equal the fixed family identity \""
                    + FixedApplicationId + "\"");
            if (config.companyName != null
                && config.companyName != FixedCompanyName)
                reasons.Add("companyName: \"" + config.companyName
                    + "\" must equal the fixed family identity \""
                    + FixedCompanyName + "\"");
            if (config.productName != null
                && config.productName != FixedProductName)
                reasons.Add("productName: \"" + config.productName
                    + "\" must equal the fixed family identity \""
                    + FixedProductName + "\"");
            if (config.versionName != null
                && !VersionNamePattern.IsMatch(config.versionName))
                reasons.Add("versionName: \"" + config.versionName
                    + "\" must be major.minor.patch digits");
            if (config.androidVersionCode.HasValue
                && config.androidVersionCode.Value <= 0)
                reasons.Add("androidVersionCode: "
                    + config.androidVersionCode.Value
                    + " must be positive");
            if (config.previousAndroidVersionCode.HasValue
                && config.previousAndroidVersionCode.Value < 0)
                reasons.Add("previousAndroidVersionCode: "
                    + config.previousAndroidVersionCode.Value
                    + " must be nonnegative");
            // Android orders installs by versionCode: a new release must
            // be strictly above every installed predecessor of the same
            // identity, or devices refuse the in-place update.
            if (config.androidVersionCode.HasValue
                && config.previousAndroidVersionCode.HasValue
                && config.androidVersionCode.Value
                    <= config.previousAndroidVersionCode.Value)
                reasons.Add("androidVersionCode: "
                    + config.androidVersionCode.Value
                    + " must be strictly greater than "
                    + "previousAndroidVersionCode "
                    + config.previousAndroidVersionCode.Value);
            if (config.androidMinSdkVersion.HasValue
                && config.androidMinSdkVersion.Value != FixedMinSdkVersion)
                reasons.Add("androidMinSdkVersion: "
                    + config.androidMinSdkVersion.Value
                    + " must remain API " + FixedMinSdkVersion
                    + " per the release contract");
            if (config.androidTargetSdkVersion.HasValue
                && config.androidTargetSdkVersion.Value <= 0)
                reasons.Add("androidTargetSdkVersion: must be a resolved "
                    + "positive API level (0 = auto is not allowed)");
            if (config.androidMinSdkVersion.HasValue
                && config.androidTargetSdkVersion.HasValue
                && config.androidTargetSdkVersion.Value
                    < config.androidMinSdkVersion.Value)
                reasons.Add("androidTargetSdkVersion: "
                    + config.androidTargetSdkVersion.Value
                    + " must be at or above androidMinSdkVersion "
                    + config.androidMinSdkVersion.Value);
            if (config.expectedCertificateSha256 != null
                && !Sha256Pattern.IsMatch(config.expectedCertificateSha256))
                reasons.Add("expectedCertificateSha256: must be 64 "
                    + "hex characters of the signing certificate "
                    + "SHA-256 fingerprint");
        }

        private static void ApplyValid(Config config)
        {
            PlayerSettings.companyName = config.companyName;
            PlayerSettings.productName = config.productName;
            PlayerSettings.bundleVersion = config.versionName;
            var android = UnityEditor.Build.NamedBuildTarget.Android;
            PlayerSettings.SetApplicationIdentifier(
                android, config.applicationId);
            PlayerSettings.Android.bundleVersionCode =
                config.androidVersionCode.Value;
            PlayerSettings.Android.minSdkVersion =
                (AndroidSdkVersions)config.androidMinSdkVersion.Value;
            PlayerSettings.Android.targetSdkVersion =
                (AndroidSdkVersions)config.androidTargetSdkVersion.Value;
        }

        private static string ReadString(JObject json, string field,
            List<string> reasons)
        {
            var token = json[field];
            if (token == null || token.Type != JTokenType.String
                || string.IsNullOrWhiteSpace((string)token))
            {
                reasons.Add(field + ": missing or not a non-empty string");
                return null;
            }
            return (string)token;
        }

        private static string ReadOptionalString(JObject json, string field,
            List<string> reasons)
        {
            var token = json[field];
            if (token == null) return null;
            if (token.Type != JTokenType.String
                || string.IsNullOrWhiteSpace((string)token))
            {
                reasons.Add(field + ": present but not a non-empty string");
                return null;
            }
            return (string)token;
        }

        private static int? ReadInt(JObject json, string field,
            List<string> reasons)
        {
            var token = json[field];
            if (token == null || token.Type != JTokenType.Integer)
            {
                reasons.Add(field + ": missing or not an integer");
                return null;
            }
            return (int)token;
        }
    }
}
