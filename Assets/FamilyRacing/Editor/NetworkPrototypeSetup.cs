using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Networking;
using FamilyRacing.Networking;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

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
            EnsureLanRaceContent();
        }

        // --- LAN race content ---------------------------------------------
        // Four non-overlapping grid slots on the shared graybox course:
        // two staggered rows of two, >=5 m apart, inside the start gate
        // band. The network race coordinator seats roster slot N at
        // spawnSlots[N]; solo play ignores them entirely.
        private const string DrivingContentDir = "Assets/FamilyRacing/Generated/Driving";
        private const string LanTrackPath = DrivingContentDir + "/graybox-01.asset";
        private const int LanEventLaps = 3;
        private const int LanCountdownTicks = 90;
        private static readonly Vector3[] LanSpawnPositions =
        {
            new Vector3(-5f, 0f, -57f),
            new Vector3(5f, 0f, -57f),
            new Vector3(-5f, 0f, -62f),
            new Vector3(5f, 0f, -62f)
        };

        private static void EnsureLanRaceContent()
        {
            var track = AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(LanTrackPath);
            if (track == null)
            {
                // FoundationProjectSetup runs DrivingPrototypeSetup first;
                // a standalone invocation reports the miss honestly
                // instead of fabricating a partial track.
                Debug.LogWarning("NetworkPrototypeSetup: " + LanTrackPath
                    + " missing — LAN race content skipped");
                return;
            }
            EnsureSpawnGrid(track);
            EnsureRoute(track);
            // Separate class-pure event configurations per vehicle class;
            // the authored coin fact rides on the event so a finalized
            // race can award finishers (Task 8 applies it).
            EnsureLanEvent("lan-car-01", track, "car-01");
            EnsureLanEvent("lan-motorcycle-01", track, "motorcycle-01");
            // Authoring the assets is not enough: the runtime catalog is
            // built from the bootstrap scene's DrivingWorld.events list,
            // which DrivingPrototypeSetup fills with solo events only.
            // An unwired LAN event makes lan.configure fail with
            // unknownEvent on every built player.
            WireLanEventsIntoScene();
        }

        // Append the LAN event assets to the bootstrap scene's
        // DrivingWorld.events list — same inspect-not-trust pattern as
        // DrivingPrototypeSetup.WireScene: an existing entry is left
        // alone, a missing one is added in place, and scenes we opened
        // are closed afterwards. Never reorders or removes solo entries.
        private static void WireLanEventsIntoScene()
        {
            const string scenePath =
                "Assets/FamilyRacing/Generated/Foundation.unity";
            var lanEvents = new List<Object>();
            foreach (var eventId in new[] { "lan-car-01", "lan-motorcycle-01" })
            {
                var asset = AssetDatabase.LoadAssetAtPath<EventDefinitionAsset>(
                    DrivingContentDir + "/" + eventId + ".asset");
                if (asset != null) lanEvents.Add(asset);
            }
            if (lanEvents.Count == 0) return;
            var alreadyOpen = false;
            var scene = new Scene();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var open = SceneManager.GetSceneAt(i);
                if (open.path == scenePath)
                { scene = open; alreadyOpen = true; break; }
            }
            if (!alreadyOpen)
                scene = EditorSceneManager.OpenScene(
                    scenePath, OpenSceneMode.Additive);
            var dirty = false;
            foreach (var rootObj in scene.GetRootGameObjects())
            foreach (var app in rootObj
                         .GetComponentsInChildren<FoundationApp>(true))
            {
                var world = app.GetComponent<DrivingWorld>();
                if (world == null) continue;
                var serialized = new SerializedObject(world);
                var events = serialized.FindProperty("events");
                if (events == null || !events.isArray) continue;
                var present = new List<Object>();
                for (var i = 0; i < events.arraySize; i++)
                    present.Add(events.GetArrayElementAtIndex(i)
                        .objectReferenceValue);
                var merged = new List<Object>(present);
                foreach (var lanEvent in lanEvents)
                    if (!present.Contains(lanEvent)) merged.Add(lanEvent);
                if (merged.Count == present.Count) continue;
                events.arraySize = merged.Count;
                for (var i = 0; i < merged.Count; i++)
                    events.GetArrayElementAtIndex(i)
                        .objectReferenceValue = merged[i];
                serialized.ApplyModifiedPropertiesWithoutUndo();
                dirty = true;
            }
            if (dirty)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            if (!alreadyOpen) EditorSceneManager.CloseScene(scene, true);
        }

        // The AI racing line for the graybox course, compatible with the
        // gate order: north to gate-1, west onto the landmark ramp (the
        // AI climbs and jumps it under the normal airborne/landing rules),
        // north around IslandNorthEast to gate-2, down the east edge to
        // gate-3, under IslandSouthEast to gate-4, then a swing south of
        // the line and back across start/finish. Every leg crosses its
        // mandatory gate plane inside the authored half-width; the route
        // is a loop, so the index after the last point resumes at 0 just
        // past the line.
        private static readonly Vector2[] LanRoutePoints =
        {
            new Vector2(0f, -30f),
            new Vector2(0f, 10f),
            new Vector2(0f, 30f),
            new Vector2(-30f, 24f),
            new Vector2(-54f, 14f),
            new Vector2(-60f, 30f),
            new Vector2(-60f, 46f),
            new Vector2(-40f, 58f),
            new Vector2(-10f, 62f),
            new Vector2(20f, 62f),
            new Vector2(48f, 60f),
            new Vector2(62f, 55f),
            new Vector2(82f, 42f),
            new Vector2(95f, 18f),
            new Vector2(95f, -14f),
            new Vector2(99f, -34f),
            new Vector2(92f, -48f),
            new Vector2(68f, -60f),
            new Vector2(38f, -56f),
            new Vector2(10f, -62f),
            new Vector2(0f, -66f),
            new Vector2(0f, -48f)
        };

        private static void EnsureRoute(TrackDefinitionAsset track)
        {
            var dirty = track.aiRoute == null
                || track.aiRoute.Length != LanRoutePoints.Length;
            if (!dirty)
            {
                for (var i = 0; i < LanRoutePoints.Length; i++)
                {
                    var point = track.aiRoute[i];
                    if (point == null || point.x != LanRoutePoints[i].x
                        || point.z != LanRoutePoints[i].y
                        || point.radius != 0f)
                    {
                        dirty = true;
                        break;
                    }
                }
            }
            if (!dirty) return;
            track.aiRoute =
                new TrackDefinitionAsset.RoutePoint[LanRoutePoints.Length];
            for (var i = 0; i < LanRoutePoints.Length; i++)
                track.aiRoute[i] = new TrackDefinitionAsset.RoutePoint
                {
                    x = LanRoutePoints[i].x,
                    z = LanRoutePoints[i].y,
                    radius = 0f
                };
            EditorUtility.SetDirty(track);
        }

        private static void EnsureSpawnGrid(TrackDefinitionAsset track)
        {
            var dirty = false;
            if (track.spawnSlots == null
                || track.spawnSlots.Length != LanSpawnPositions.Length)
            {
                track.spawnSlots =
                    new TrackDefinitionAsset.SpawnSlot[LanSpawnPositions.Length];
                dirty = true;
            }
            for (var i = 0; i < LanSpawnPositions.Length; i++)
            {
                if (track.spawnSlots[i] == null)
                {
                    track.spawnSlots[i] = new TrackDefinitionAsset.SpawnSlot();
                    dirty = true;
                }
                if (track.spawnSlots[i].position != LanSpawnPositions[i]
                    || track.spawnSlots[i].headingDegrees != 0f)
                {
                    track.spawnSlots[i].position = LanSpawnPositions[i];
                    track.spawnSlots[i].headingDegrees = 0f;
                    dirty = true;
                }
            }
            if (dirty) EditorUtility.SetDirty(track);
        }

        private static void EnsureLanEvent(string eventId,
            TrackDefinitionAsset track, string vehicleId)
        {
            var path = DrivingContentDir + "/" + eventId + ".asset";
            var asset = AssetDatabase.LoadAssetAtPath<EventDefinitionAsset>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<EventDefinitionAsset>();
                AssetDatabase.CreateAsset(asset, path);
            }
            var dirty = asset.eventId != eventId
                || asset.track != track
                || asset.laps != LanEventLaps
                || asset.countdownTicks != LanCountdownTicks
                || asset.completionCoins != NetworkSettings.LanCompletionCoins
                || asset.vehicleIds == null
                || asset.vehicleIds.Length != 1
                || asset.vehicleIds[0] != vehicleId;
            if (!dirty) return;
            asset.eventId = eventId;
            asset.track = track;
            asset.laps = LanEventLaps;
            asset.countdownTicks = LanCountdownTicks;
            asset.vehicleIds = new[] { vehicleId };
            asset.completionCoins = NetworkSettings.LanCompletionCoins;
            EditorUtility.SetDirty(asset);
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
