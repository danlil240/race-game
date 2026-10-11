using System.Collections.Generic;
using FamilyRacing;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace FamilyRacing.EditorTools
{
    // Repeatable, non-destructive project setup: creates only missing
    // generated assets (URP pipeline, panel settings, bootstrap scene,
    // .meta files) and applies the foundation player/graphics settings.
    // Safe to re-run; authored assets are never overwritten.
    public static class FoundationProjectSetup
    {
        private const string GeneratedDir = "Assets/FamilyRacing/Generated";
        private const string UrpAssetPath = GeneratedDir + "/FoundationURP.asset";
        private const string RendererPath = GeneratedDir + "/FoundationRenderer.asset";
        private const string PanelSettingsPath = GeneratedDir + "/FoundationPanelSettings.asset";
        private const string TextSettingsPath = GeneratedDir + "/FoundationTextSettings.asset";
        private const string ScenePath = GeneratedDir + "/Foundation.unity";

        public static void Create()
        {
            EnsureFolder("Assets/FamilyRacing", "Generated");
            var urp = EnsureUrp();
            GraphicsSettings.defaultRenderPipeline = urp;
            QualitySettings.renderPipeline = urp;
            var panel = EnsurePanelSettings();
            EnsureScene(panel);
            EnsureSceneInBuild();
            ApplyPlayerSettings();
            EnableInputSystem();
            AssetDatabase.SaveAssets();
        }

        private static void EnsureFolder(string parent, string name)
        {
            var path = parent + "/" + name;
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, name);
        }

        private static UniversalRenderPipelineAsset EnsureUrp()
        {
            var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            if (urp != null)
            {
                // Repeatable setup enforces the brief's Android render scale
                // on existing assets too.
                if (urp.renderScale != 0.75f)
                {
                    urp.renderScale = 0.75f;
                    EditorUtility.SetDirty(urp);
                }
                return urp;
            }
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, RendererPath);
            }
            urp = UniversalRenderPipelineAsset.Create(renderer);
            urp.renderScale = 0.75f;
            AssetDatabase.CreateAsset(urp, UrpAssetPath);
            return urp;
        }

        private static PanelSettings EnsurePanelSettings()
        {
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (panel != null) return panel;
            var text = AssetDatabase.LoadAssetAtPath<PanelTextSettings>(TextSettingsPath);
            if (text == null)
            {
                text = ScriptableObject.CreateInstance<PanelTextSettings>();
                AssetDatabase.CreateAsset(text, TextSettingsPath);
            }
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.textSettings = text;
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1280, 800);
            AssetDatabase.CreateAsset(panel, PanelSettingsPath);
            return panel;
        }

        private static void EnsureScene(PanelSettings panel)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null)
            {
                RepairScenePanel(panel);
                return;
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraObject = new GameObject("Main Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.96f, 0.90f, 0.78f);
            cameraObject.tag = "MainCamera";

            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.88f);
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var appObject = new GameObject("FoundationApp");
            appObject.AddComponent<FoundationApp>();
            appObject.AddComponent<FoundationInput>();
            var document = appObject.AddComponent<UIDocument>();
            AssignPanel(document, panel);
            appObject.AddComponent<FoundationShell>();

            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        // Serialized assignment: the property setter alone was observed to
        // leave m_PanelSettings at fileID 0 in the saved scene, which renders
        // as a blank background with no panel attached.
        private static void AssignPanel(UIDocument document, PanelSettings panel)
        {
            var serialized = new SerializedObject(document);
            var property = serialized.FindProperty("m_PanelSettings");
            if (property != null)
            {
                property.objectReferenceValue = panel;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                document.panelSettings = panel;
            }
        }

        // Existing scene assets are inspected, not trusted: a UIDocument
        // whose panelSettings is missing or points elsewhere is repaired in
        // place and the scene saved. Scenes already open (the user's working
        // context) are kept open; scenes we opened are closed afterwards.
        private static void RepairScenePanel(PanelSettings panel)
        {
            var alreadyOpen = false;
            var scene = new Scene();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var open = SceneManager.GetSceneAt(i);
                if (open.path == ScenePath) { scene = open; alreadyOpen = true; break; }
            }
            if (!alreadyOpen)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            var dirty = false;
            foreach (var root in scene.GetRootGameObjects())
            foreach (var document in root.GetComponentsInChildren<UIDocument>(true))
            {
                if (document.panelSettings == panel) continue;
                AssignPanel(document, panel);
                dirty = true;
            }
            if (dirty)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            if (!alreadyOpen)
                EditorSceneManager.CloseScene(scene, true);
        }

        private static void EnsureSceneInBuild()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            foreach (var scene in scenes)
                if (scene.path == ScenePath) return;
            scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        private static void ApplyPlayerSettings()
        {
            PlayerSettings.productName = "Family Racing";
            PlayerSettings.companyName = "Family Racing";
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.bundleVersion = "0.1.0";

            var standalone = UnityEditor.Build.NamedBuildTarget.Standalone;
            PlayerSettings.SetScriptingBackend(standalone, ScriptingImplementation.Mono2x);

            var android = UnityEditor.Build.NamedBuildTarget.Android;
            PlayerSettings.SetApplicationIdentifier(android, "com.daniel.familyracing");
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
                new[] { GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;
        }

        // Both input backends stay enabled so keyboard works even if the new
        // Input System events are not wired in a given build.
        private static void EnableInputSystem()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (assets == null || assets.Length == 0) return;
            var settings = new SerializedObject(assets[0]);
            var property = settings.FindProperty("activeInputHandler");
            if (property == null)
                property = settings.FindProperty("m_ActiveInputHandler");
            if (property == null) return;
            if (property.intValue != 2) // 2 = Both (old + new Input System)
            {
                property.intValue = 2;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
        }
    }
}
