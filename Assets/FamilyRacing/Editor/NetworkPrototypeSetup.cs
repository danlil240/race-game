using System.IO;
using FamilyRacing.Networking;
using UnityEditor;
using UnityEngine;

namespace FamilyRacing.EditorTools
{
    // Repeatable, non-destructive setup for the LAN network slice: authors
    // the Android manifest additions required for offline LAN sockets and
    // applies the networking player settings. Runs inside
    // FoundationProjectSetup.Create so every build and setup path picks it
    // up. No Relay, Unity Services authentication, or cloud lobby
    // dependencies are introduced; all gameplay stays offline.
    public static class NetworkPrototypeSetup
    {
        public const ushort GameplayPort = LanTransport.DefaultGameplayPort;   // UDP 47777
        public const ushort DiscoveryPort = LanDiscovery.DefaultDiscoveryPort; // UDP 47778
        private const string AndroidPluginsDir = "Assets/Plugins/Android";
        private const string AndroidManifestPath = AndroidPluginsDir + "/AndroidManifest.xml";

        // unityLibrary manifest additions. All three are Android "normal"
        // permissions granted at install time — no runtime dialog. INTERNET
        // covers unicast + broadcast UDP; ACCESS_NETWORK_STATE lets join
        // flows distinguish "no network" from "denied";
        // CHANGE_WIFI_MULTICAST_STATE is required to hold the MulticastLock
        // that keeps broadcast discovery replies flowing on Android 9+.
        private const string AndroidManifest =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">\n" +
            "    <uses-permission android:name=\"android.permission.INTERNET\" />\n" +
            "    <uses-permission android:name=\"android.permission.ACCESS_NETWORK_STATE\" />\n" +
            "    <uses-permission android:name=\"android.permission.CHANGE_WIFI_MULTICAST_STATE\" />\n" +
            "</manifest>\n";

        public static void Create()
        {
            EnsureAndroidManifest();
            ApplyPlayerSettings();
        }

        private static void EnsureAndroidManifest()
        {
            if (!Directory.Exists(AndroidPluginsDir))
                Directory.CreateDirectory(AndroidPluginsDir);
            if (File.Exists(AndroidManifestPath)
                && File.ReadAllText(AndroidManifestPath) == AndroidManifest)
                return;
            File.WriteAllText(AndroidManifestPath, AndroidManifest);
            AssetDatabase.ImportAsset(AndroidManifestPath);
        }

        private static void ApplyPlayerSettings()
        {
            // Belt and suspenders alongside the manifest declaration: this
            // flag keeps INTERNET in the merged manifest even if a future
            // manifest edit drops the explicit entry.
            PlayerSettings.Android.forceInternetPermission = true;
        }

        // Called by FoundationBuild right before each platform build so the
        // produced player cannot silently ship without LAN capability. Logs
        // the effective LAN configuration for the build log; throws when a
        // required artifact is missing.
        public static void VerifyBuildConfiguration(BuildTarget target)
        {
            if (target == BuildTarget.Android
                && !File.Exists(AndroidManifestPath))
                throw new InvalidDataException(
                    "Android LAN manifest missing: " + AndroidManifestPath);
            Debug.Log("NetworkPrototypeSetup: LAN gameplay UDP " + GameplayPort +
                ", discovery UDP " + DiscoveryPort +
                "; validation endpoints remain loopback-only; " +
                "no Relay/UGS/cloud lobby dependencies.");
        }
    }
}
