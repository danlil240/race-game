using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FamilyRacing.EditorTools
{
    // Authoring is explicit. Shipping entry points validate the saved scene;
    // they never substitute Foundation.unity after validating another catalog.
    public static class ProductionSceneAuthoring
    {
        public const string ScenePath = FullContentSetup.ContentDir + "/FamilyRacing.unity";
        public const string RoutesPath = FullContentSetup.ContentDir + "/courses.json";
        public const string WorldPrefabPath = FullContentSetup.ContentDir + "/ProductionWithTutorial.prefab";

        [Serializable] private sealed class RouteManifest
        {
            public int schemaVersion = 1;
            public string contentIdentity = FullContentSetup.DrivingVersion;
            public RouteRow[] courses;
        }
        [Serializable] private sealed class RouteRow
        {
            public string courseId;
            public TrackDefinitionAsset.RoutePoint[] aiRoute;
            public TrackDefinitionAsset.Gate[] gates;
        }

        [MenuItem("Family Racing/Production/Author Complete Campaign")]
        public static void Create()
        {
            FoundationProjectSetup.Create();
            ProductionCourseAuthoring.Create();
            ProductionCampaignAuthoring.Create();
            ProductionVoiceAuthoring.Create();
            ProductionTutorialAuthoring.Create();
            var geometry = UnityEngine.Object.Instantiate(Required<GameObject>(ProductionCourseAuthoring.PrefabPath));
            try
            {
                geometry.name = "ProductionWorld";
                ProductionTutorialAuthoring.AddCourseGeometry(geometry.transform);
                PrefabUtility.SaveAsPrefabAsset(geometry, WorldPrefabPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(geometry); }
            var source = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null
                ? ScenePath : VisualBenchmarkSetup.ScenePath;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(source) == null)
                throw new InvalidOperationException("Create VisualBenchmark before production scene authoring.");
            var scene = EditorSceneManager.OpenScene(source);
            if (source != ScenePath && !EditorSceneManager.SaveScene(scene, ScenePath, false))
                throw new IOException("Cannot create production scene");
            var world = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<DrivingWorld>(true)).Single();
            var serialized = new SerializedObject(world);
            var tracks = Enumerable.Range(0, 6)
                .Select(index => Required<TrackDefinitionAsset>(ProductionCourseAuthoring.TrackPath(index)))
                .ToArray();
            File.WriteAllText(RoutesPath, JsonUtility.ToJson(new RouteManifest
            {
                courses = tracks.Select(track => new RouteRow
                { courseId = track.courseId, aiRoute = track.aiRoute, gates = track.gates }).ToArray()
            }, true));
            AssignArray(serialized, "tracks", tracks.Concat(new[] { ProductionTutorialAuthoring.Track }).ToArray());
            serialized.FindProperty("track").objectReferenceValue = tracks[0];
            AssignArray(serialized, "vehicles", Find<VehicleDefinitionAsset>());
            AssignArray(serialized, "events", Find<EventDefinitionAsset>().Concat(ProductionTutorialAuthoring.Events).ToArray());
            serialized.FindProperty("coursePrefab").objectReferenceValue =
                Required<GameObject>(WorldPrefabPath);
            var catalog = Required<GameplayCatalogAsset>(ProductionCampaignAuthoring.CatalogPath);
            var voice = Required<VoicePromptCatalogAsset>(ProductionVoiceAuthoring.CatalogPath);
            catalog.voicePrompts = voice;
            EditorUtility.SetDirty(catalog);
            serialized.FindProperty("gameplayCatalog").objectReferenceValue = catalog;
            serialized.FindProperty("gadgetCatalog").objectReferenceValue = catalog.gadgets;
            serialized.FindProperty("contentVersion").stringValue = FullContentSetup.DrivingVersion;
            serialized.FindProperty("tutorial").objectReferenceValue =
                Required<TutorialDefinitionAsset>(ProductionTutorialAuthoring.TutorialPath);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            var presenter = world.GetComponent<ProductionVoicePresenter>()
                ?? world.gameObject.AddComponent<ProductionVoicePresenter>();
            presenter.Bind(voice);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            ValidateScene();
            var gaps = FullContentSetup.ValidateProduction();
            Debug.Log("PRODUCTION_SCENE_AUTHORED " + ScenePath + " contentBlockers=" + gaps.Count);
            foreach (var gap in gaps) Debug.Log("PRODUCTION_BLOCKER " + gap);
        }

        public static string[] PrepareBuild()
        {
            FullContentSetup.Create();
            ValidateScene();
            return new[] { ScenePath };
        }

        // A development-only player permits real campaign driving tests while
        // spoken assets remain unfinished. It never passes the shipping gate.
        public static void BuildValidationWindows()
        {
            Create();
            FullContentSetup.CreatePreview();
            var path = "Builds/Windows-Campaign-Validation/FamilyRacing.exe";
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath }, locationPathName = path,
                target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Campaign validation player failed: " + report.summary.result);
            FullContentSetup.WriteManifest("Builds/Windows-Campaign-Validation", "Development", "partial");
        }

        public static void ValidateScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
                throw new InvalidOperationException("Production scene missing; run ProductionSceneAuthoring.Create first.");
            var scene = EditorSceneManager.OpenScene(ScenePath);
            var worlds = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<DrivingWorld>(true)).ToArray();
            if (worlds.Length != 1) throw new InvalidOperationException("Production scene must contain one DrivingWorld");
            var serialized = new SerializedObject(worlds[0]);
            var expectedTracks = Enumerable.Range(0, 6)
                .Select(index => (UnityEngine.Object)Required<TrackDefinitionAsset>(ProductionCourseAuthoring.TrackPath(index)))
                .Concat(new UnityEngine.Object[] { ProductionTutorialAuthoring.Track }).ToArray();
            RequireArray(serialized, "tracks", expectedTracks);
            RequireArray(serialized, "vehicles", Find<VehicleDefinitionAsset>());
            RequireArray(serialized, "events", Find<EventDefinitionAsset>().Concat(ProductionTutorialAuthoring.Events).ToArray());
            if (serialized.FindProperty("contentVersion").stringValue != FullContentSetup.DrivingVersion
                || serialized.FindProperty("gameplayCatalog").objectReferenceValue != Required<GameplayCatalogAsset>(ProductionCampaignAuthoring.CatalogPath)
                || serialized.FindProperty("coursePrefab").objectReferenceValue != Required<GameObject>(WorldPrefabPath)
                || serialized.FindProperty("tutorial").objectReferenceValue != Required<TutorialDefinitionAsset>(ProductionTutorialAuthoring.TutorialPath)
                || serialized.FindProperty("track").objectReferenceValue != expectedTracks[0])
                throw new InvalidOperationException("Production scene references stale content");
            if (worlds[0].GetComponent<ProductionVoicePresenter>() == null)
                throw new InvalidOperationException("Production scene is missing its voice presenter");
        }

        private static T Required<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null) throw new InvalidOperationException("Missing production asset: " + path);
            return asset;
        }

        private static T[] Find<T>() where T : UnityEngine.Object
            => AssetDatabase.FindAssets("t:" + typeof(T).Name, new[] { FullContentSetup.ContentDir })
                .Select(AssetDatabase.GUIDToAssetPath).OrderBy(path => path, StringComparer.Ordinal)
                .Select(AssetDatabase.LoadAssetAtPath<T>).ToArray();

        private static void AssignArray(SerializedObject serialized, string name, UnityEngine.Object[] values)
        {
            var property = serialized.FindProperty(name);
            property.arraySize = values.Length;
            for (var index = 0; index < values.Length; index++)
                property.GetArrayElementAtIndex(index).objectReferenceValue = values[index];
        }

        private static void RequireArray(SerializedObject serialized, string name, UnityEngine.Object[] expected)
        {
            var property = serialized.FindProperty(name);
            if (property.arraySize != expected.Length)
                throw new InvalidOperationException("Production scene has stale " + name);
            for (var index = 0; index < expected.Length; index++)
                if (property.GetArrayElementAtIndex(index).objectReferenceValue != expected[index])
                    throw new InvalidOperationException("Production scene has stale " + name + " entry " + index);
        }
    }
}
