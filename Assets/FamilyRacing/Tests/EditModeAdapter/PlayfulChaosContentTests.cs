using FamilyRacing.Core.Progression;
using FamilyRacing.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FamilyRacing.Tests
{
    public sealed class PlayfulChaosContentTests
    {
        [Test]
        public void PackagedFontsCoverNewUiText()
        {
            var panel = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(
                "Assets/FamilyRacing/Generated/FoundationPanelSettings.asset");
            foreach (var key in FamilyRacing.Core.Localization.ShellText.Keys)
                foreach (var locale in new[] { "en", "he" })
                {
                    var text = FamilyRacing.Core.Localization.ShellText.Get(key, locale);
                    Assert.That(panel.textSettings.defaultFontAsset.HasCharacters(text,
                        out var missing, true, true), Is.True,
                        key + ": missing character codes " + (missing == null ? "" : string.Join(",", missing)));
                }
        }

        [Test]
        public void PlayableSceneHasOneAudioListener()
        {
            EditorSceneManager.OpenScene(PlayfulChaosSetup.ScenePath);
            Assert.That(Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None).Length,
                Is.EqualTo(1), "engine, boost and landing cues need an active listener");
        }

        [Test]
        public void PlayableSceneLoadsQuickRaceAndPreservesAuthoredTuneOnSetup()
        {
            PlayfulChaosSetup.Create();
            var car = AssetDatabase.LoadAssetAtPath<VehicleDefinitionAsset>(
                PlayfulChaosSetup.ContentDir + "/car-responsive.asset");
            var serialized = new SerializedObject(car);
            var acceleration = serialized.FindProperty("acceleration");
            var original = acceleration.floatValue;
            try
            {
                acceleration.floatValue = 8.125f;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PlayfulChaosSetup.Create();
                Assert.That(car.acceleration, Is.EqualTo(8.125f));
                var scene = EditorSceneManager.OpenScene(PlayfulChaosSetup.ScenePath);
                DrivingWorld world = null;
                foreach (var root in scene.GetRootGameObjects())
                    if (root.GetComponentInChildren<DrivingWorld>(true) != null)
                        world = root.GetComponentInChildren<DrivingWorld>(true);
                Assert.That(world, Is.Not.Null);
                Assert.That(world.Load(), Is.Not.Null);
                Assert.That(world.Gameplay.QuickRaceOnly, Is.True);
                Assert.That(world.Gameplay.Economy, Is.Null);
                Assert.That(world.Gameplay.Driving.Events.ContainsKey("fun-graybox-car-01"), Is.True);
                Assert.That(world.CourseVisualId, Is.EqualTo("sunny-island-benchmark-01"));
            }
            finally
            {
                serialized.Update();
                acceleration.floatValue = original;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
            }
        }
    }
}
