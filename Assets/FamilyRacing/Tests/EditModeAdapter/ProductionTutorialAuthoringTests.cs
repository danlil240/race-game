using System;
using System.Linq;
using FamilyRacing.Core.Racing;
using FamilyRacing.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    public sealed class ProductionTutorialAuthoringTests
    {
        private const string Source = "Assets/FamilyRacing/Generated/Driving/";

        [OneTimeSetUp]
        public void Author() => ProductionTutorialAuthoring.Create();

        [Test]
        public void TutorialResolvesBothOriginalEventsForBothStarterVehicles()
        {
            var asset = AssetDatabase.LoadAssetAtPath<TutorialDefinitionAsset>(ProductionTutorialAuthoring.TutorialPath);
            var definition = asset.ToDefinition(out var error);
            Assert.That(definition, Is.Not.Null, error);
            var track = ProductionTutorialAuthoring.Track;
            var events = ProductionTutorialAuthoring.Events;
            var driving = DrivingContentCatalog.Create("tutorial-test",
                new[] { "car-01", "motorcycle-01" }.Select(id =>
                    AssetDatabase.LoadAssetAtPath<VehicleDefinitionAsset>(Source + id + ".asset").ToTuning(out _)),
                new[] { track.ToCourse(out _) }, events.Select(evt => evt.ToEvent(out _)));
            Assert.That(driving.Courses.Keys, Is.EquivalentTo(new[] { "graybox-01" }));
            Assert.That(driving.Events.Keys, Is.EquivalentTo(new[] { "tutorial-01", "intro-01" }));
            foreach (var id in new[] { definition.freeDriveEventId, definition.trialEventId })
            {
                Assert.That(driving.Events[id].courseId, Is.EqualTo("graybox-01"));
                Assert.That(driving.Events[id].AllowsVehicle("car-01"), Is.True);
                Assert.That(driving.Events[id].AllowsVehicle("motorcycle-01"), Is.True);
            }
            Assert.That(events.Single(evt => evt.eventId == definition.freeDriveEventId).laps, Is.EqualTo(99));
            Assert.That(events.Single(evt => evt.eventId == definition.trialEventId).laps, Is.EqualTo(1));
            var original = AssetDatabase.LoadAssetAtPath<TutorialDefinitionAsset>(Source + "tutorial-def-01.asset");
            // Preserve the proven rules and genuine clip references, including
            // null recordings. Replacing the jump lesson to hide bad geometry
            // would fail this comparison.
            Assert.That(JsonUtility.ToJson(asset), Is.EqualTo(JsonUtility.ToJson(original)));
            Assert.That(JsonUtility.ToJson(track), Is.EqualTo(JsonUtility.ToJson(
                AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(Source + "graybox-01.asset"))));
        }

        [Test]
        public void GeometryKeepsOriginalRampAndSwitchesWithTheTutorialCourse()
        {
            var parent = new GameObject("TutorialCourseTest");
            try
            {
                var selector = parent.AddComponent<LongRaceVisibility>();
                ProductionTutorialAuthoring.AddCourseGeometry(parent.transform);
                ProductionTutorialAuthoring.AddCourseGeometry(parent.transform);
                Assert.That(parent.transform.childCount, Is.EqualTo(1), "Reauthoring must not duplicate course geometry");
                var course = parent.transform.Find("graybox-01");
                Assert.That(course, Is.Not.Null);
                var original = AssetDatabase.LoadAssetAtPath<GameObject>(Source + "GrayboxCourse.prefab");
                var ramp = course.Find("Ramp");
                Assert.That(ramp, Is.Not.Null);
                Assert.That(ramp.GetComponent<Collider>(), Is.Not.Null);
                Assert.That(ramp.localPosition, Is.EqualTo(original.transform.Find("Ramp").localPosition));
                Assert.That(ramp.localRotation, Is.EqualTo(original.transform.Find("Ramp").localRotation));
                Assert.That(ramp.localScale, Is.EqualTo(original.transform.Find("Ramp").localScale));
                Assert.That(course.GetComponentsInChildren<Collider>(true).Length,
                    Is.EqualTo(original.GetComponentsInChildren<Collider>(true).Length));
                selector.SelectCourse("harbor-hop");
                Assert.That(ramp.gameObject.activeInHierarchy, Is.False);
                selector.SelectCourse("graybox-01");
                Assert.That(ramp.gameObject.activeInHierarchy, Is.True);
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void ReauthoringPreservesReferencesAndLeavesTheSourcesUntouched()
        {
            var tutorial = AssetDatabase.LoadAssetAtPath<TutorialDefinitionAsset>(ProductionTutorialAuthoring.TutorialPath);
            var track = ProductionTutorialAuthoring.Track;
            var events = ProductionTutorialAuthoring.Events;
            var assets = new Object[] { tutorial, track }.Concat(events).ToArray();
            var paths = assets.Select(AssetDatabase.GetAssetPath).ToArray();
            Assert.That(paths.All(path => !path.StartsWith(FullContentSetup.ContentDir + "/", StringComparison.Ordinal)), Is.True);
            var guids = paths.Select(AssetDatabase.AssetPathToGUID).ToArray();
            var sourcePaths = new[] { "tutorial-def-01", "graybox-01", "tutorial-01", "intro-01" }
                .Select(id => Source + id + ".asset").ToArray();
            var sourceJson = sourcePaths.Select(path => JsonUtility.ToJson(AssetDatabase.LoadMainAssetAtPath(path))).ToArray();
            ProductionTutorialAuthoring.Create();
            Assert.That(paths.Select(AssetDatabase.AssetPathToGUID), Is.EqualTo(guids));
            Assert.That(AssetDatabase.LoadAssetAtPath<TutorialDefinitionAsset>(ProductionTutorialAuthoring.TutorialPath), Is.SameAs(tutorial));
            Assert.That(ProductionTutorialAuthoring.Track, Is.SameAs(track));
            Assert.That(ProductionTutorialAuthoring.Events, Is.EqualTo(events));
            Assert.That(sourcePaths.Select(path => JsonUtility.ToJson(AssetDatabase.LoadMainAssetAtPath(path))), Is.EqualTo(sourceJson));
        }
    }
}
