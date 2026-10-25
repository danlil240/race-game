using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FamilyRacing.EditorTools
{
    // Onboarding is independent of the fixed 6-course/78-event campaign
    // inventory. Preserve the already exercised graybox course, ramp, lesson
    // rules and trial instead of silently dropping lessons on another track.
    public static class ProductionTutorialAuthoring
    {
        public const string CourseId = "graybox-01";
        public const string TutorialPath = DirectoryPath + "/tutorial-def-01.asset";
        private const string DirectoryPath = "Assets/FamilyRacing/Generated/ProductionTutorial";
        private const string SourcePath = "Assets/FamilyRacing/Generated/Driving/";
        private const string TrackPath = DirectoryPath + "/graybox-01.asset";
        private static readonly string[] EventIds = { "tutorial-01", "intro-01" };

        public static TrackDefinitionAsset Track => Require<TrackDefinitionAsset>(TrackPath);
        public static EventDefinitionAsset[] Events => new[]
        {
            Require<EventDefinitionAsset>(DirectoryPath + "/tutorial-01.asset"),
            Require<EventDefinitionAsset>(DirectoryPath + "/intro-01.asset")
        };

        public static void Create()
        {
            // Check the original content before writing: these event identities
            // and their common track are what FoundationService resolves when
            // tutorial.start and the graduation transition run.
            var sourceTutorial = Require<TutorialDefinitionAsset>(SourcePath + "tutorial-def-01.asset");
            var sourceTrack = Require<TrackDefinitionAsset>(SourcePath + "graybox-01.asset");
            var sourceEvents = new[]
            {
                Require<EventDefinitionAsset>(SourcePath + "tutorial-01.asset"),
                Require<EventDefinitionAsset>(SourcePath + "intro-01.asset")
            };
            Require<GameObject>(SourcePath + "GrayboxCourse.prefab");
            if (sourceTutorial.freeDriveEventId != EventIds[0] || sourceTutorial.trialEventId != EventIds[1]
                || sourceTrack.courseId != CourseId)
                throw new InvalidDataException("Production tutorial source identities have changed.");
            for (int index = 0; index < sourceEvents.Length; index++)
                if (sourceEvents[index].eventId != EventIds[index] || sourceEvents[index].track != sourceTrack)
                    throw new InvalidDataException("Production tutorial events must share the original graybox track.");
            if (sourceTutorial.ToDefinition(out var error) == null || sourceTrack.ToCourse(out error) == null)
                throw new InvalidDataException("Production tutorial source is invalid: " + error);

            Directory.CreateDirectory(DirectoryPath);
            AssetDatabase.Refresh();
            var track = Save(Object.Instantiate(sourceTrack), TrackPath);
            for (int index = 0; index < sourceEvents.Length; index++)
            {
                var evt = Object.Instantiate(sourceEvents[index]);
                evt.track = track;
                Save(evt, DirectoryPath + "/" + EventIds[index] + ".asset");
            }
            // Copy real clip references exactly. Missing recordings remain
            // missing; this author never synthesizes or substitutes speech.
            Save(Object.Instantiate(sourceTutorial), TutorialPath);
            AssetDatabase.SaveAssets();
        }

        public static void AddCourseGeometry(Transform parent)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            var prefab = Require<GameObject>(SourcePath + "GrayboxCourse.prefab");
            var previous = parent.Find(CourseId);
            if (previous != null) Object.DestroyImmediate(previous.gameObject);
            var instance = Object.Instantiate(prefab, parent, false);
            // LongRaceVisibility switches the complete hierarchy by course id,
            // so the tutorial and campaign collision never coexist in a race.
            instance.name = CourseId;
        }

        private static T Require<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path)
            ?? throw new InvalidDataException("Production tutorial requires " + path);

        private static T Save<T>(T value, string path) where T : Object
        {
            value.name = Path.GetFileNameWithoutExtension(path);
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing == null) { AssetDatabase.CreateAsset(value, path); return value; }
            EditorUtility.CopySerialized(value, existing);
            Object.DestroyImmediate(value);
            EditorUtility.SetDirty(existing);
            return existing;
        }
    }
}
