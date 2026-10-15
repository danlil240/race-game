using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Racing;
using UnityEditor;
using UnityEngine;

namespace FamilyRacing.EditorTools
{
    // M6 Task 4: the full-content setup gate. Create() gathers the
    // authored inventory under Generated/FullContent, runs the
    // production contract (FullContentValidation) and fails the build
    // on any named reason — an incomplete catalog can never package.
    // CreatePreview() is the development-only stage used by the
    // *FullContentPreview flavors: it binds only authored content
    // through generic catalog validation and writes no candidate; with
    // nothing authored it fails honestly instead of producing an empty
    // "full-content" player.
    //
    // Neither method creates or edits assets — authoring belongs to the
    // world setup tasks (5–7) and voice production (Task 8); this gate
    // only judges what is authored. Re-running it preserves every
    // authored id, GUID and measurement by doing nothing but validate.
    public static class FullContentSetup
    {
        // All full-content authored assets live under this root; the
        // world-authoring setups drop their definitions here.
        public const string ContentDir =
            "Assets/FamilyRacing/Generated/FullContent";
        // The driving catalog identity for the full-content inventory.
        public const string DrivingVersion = "full-content-1";

        // Production gate: throws InvalidDataException listing every
        // named reason when the authored inventory is incomplete.
        public static void Create()
        {
            var reasons = ValidateProduction();
            if (reasons.Count > 0)
                throw new InvalidDataException(
                    "FullContentSetup: production validation failed:\n - "
                    + string.Join("\n - ", reasons));
        }

        // Development-only preview gate: gathers the authored inventory
        // and requires it to convert through generic catalog
        // validation. No production counts are enforced — a partial
        // world-in-progress still previews — but nothing authored means
        // nothing to preview, which fails honestly.
        public static void CreatePreview()
        {
            var reasons = new List<string>();
            var asset = GatherCatalogAsset(reasons);
            var driving = CollectDriving(reasons);
            if (reasons.Count > 0 || asset == null || driving == null)
                throw new InvalidDataException(
                    "FullContentSetup preview: no usable authored "
                    + "full-content inventory — the preview binds only "
                    + "authored content:\n - " + string.Join("\n - ",
                        reasons.Count > 0 ? reasons
                            : new List<string> { "no content found" }));
            var catalog = asset.ToCatalog(driving, out var error);
            if (catalog == null)
                throw new InvalidDataException(
                    "FullContentSetup preview: generic catalog "
                    + "validation failed: " + error);
            Debug.Log("FullContentSetup preview: " + catalog.Campaigns.Count
                + " world(s), " + driving.Courses.Count + " course(s), "
                + driving.Events.Count + " event(s) bind — "
                + "contentStage=partial, not a full-content candidate");
        }

        // The reasons Create() would throw with — also what the build
        // log reports when a full-content flavor refuses to package.
        public static IReadOnlyList<string> ValidateProduction()
        {
            var reasons = new List<string>();
            var asset = GatherCatalogAsset(reasons);
            var driving = CollectDriving(reasons);
            // The gatherers already named every missing piece.
            if (asset == null || driving == null) return reasons;
            foreach (var reason in FullContentValidation.Validate(
                asset, driving))
                reasons.Add(reason);
            return reasons;
        }

        // Exactly one GameplayCatalogAsset under ContentDir is the
        // production catalog; none or several is an authoring error.
        private static GameplayCatalogAsset GatherCatalogAsset(
            List<string> reasons)
        {
            var assets = Gather<GameplayCatalogAsset>();
            if (assets.Count == 0)
            {
                reasons.Add("catalog: no GameplayCatalogAsset authored "
                    + "under " + ContentDir);
                return null;
            }
            if (assets.Count > 1)
            {
                reasons.Add("catalog: expected exactly one "
                    + "GameplayCatalogAsset under " + ContentDir
                    + ", found " + assets.Count);
                return null;
            }
            return assets[0];
        }

        // Convert every authored driving asset under ContentDir into a
        // validated DrivingContentCatalog; conversion errors are named
        // reasons, never silent skips.
        private static DrivingContentCatalog CollectDriving(
            List<string> reasons)
        {
            var vehicles = new List<FamilyRacing.Core.Driving.VehicleTuning>();
            foreach (var asset in Gather<VehicleDefinitionAsset>())
            {
                var tuning = asset.ToTuning(out var error);
                if (tuning == null)
                    reasons.Add("vehicle " + asset.name + ": " + error);
                else vehicles.Add(tuning);
            }
            var courses = new List<CourseDefinition>();
            foreach (var asset in Gather<TrackDefinitionAsset>())
            {
                var course = asset.ToCourse(out var error);
                if (course == null)
                    reasons.Add("track " + asset.name + ": " + error);
                else courses.Add(course);
            }
            var events = new List<EventDefinition>();
            foreach (var asset in Gather<EventDefinitionAsset>())
            {
                var evt = asset.ToEvent(out var error);
                if (evt == null)
                    reasons.Add("event " + asset.name + ": " + error);
                else events.Add(evt);
            }
            if (vehicles.Count == 0)
                reasons.Add("driving: no vehicle definitions under "
                    + ContentDir);
            if (courses.Count == 0)
                reasons.Add("driving: no track definitions under "
                    + ContentDir);
            if (events.Count == 0)
                reasons.Add("driving: no event definitions under "
                    + ContentDir);
            if (vehicles.Count == 0 || courses.Count == 0
                || events.Count == 0)
                return null;
            try
            {
                return DrivingContentCatalog.Create(DrivingVersion,
                    vehicles, courses, events);
            }
            catch (ArgumentException ex)
            {
                reasons.Add("driving: " + ex.Message);
                return null;
            }
        }

        // Sorted by asset path so repeated runs enumerate content in a
        // stable order.
        private static List<T> Gather<T>() where T : ScriptableObject
        {
            var assets = new List<T>();
            if (!AssetDatabase.IsValidFolder(ContentDir)) return assets;
            var guids = AssetDatabase.FindAssets("t:" + typeof(T).Name,
                new[] { ContentDir });
            var paths = new List<string>();
            foreach (var guid in guids)
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            paths.Sort(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset != null) assets.Add(asset);
            }
            return assets;
        }

        // The packaging manifest every full-content flavor writes beside
        // its output. `contentStage` is the honest inventory claim:
        // "complete" only comes out of the four final flavors after the
        // production gate passed; preview flavors always write
        // "partial" and candidate=false — a preview can never produce
        // an ordinary or accepted full-content candidate.
        public static void WriteManifest(string buildRoot,
            string configuration, string contentStage)
        {
            if (contentStage != "complete" && contentStage != "partial")
                throw new ArgumentException(
                    "Unknown content stage: " + contentStage,
                    "contentStage");
            var root = Path.IsPathRooted(buildRoot)
                ? buildRoot
                : Path.GetFullPath(Path.Combine(Application.dataPath,
                    "..", buildRoot));
            Directory.CreateDirectory(root);
            var json = "{\n"
                + "  \"schemaVersion\": 1,\n"
                + "  \"presentation\": \"FullContent\",\n"
                + "  \"configuration\": \"" + configuration + "\",\n"
                + "  \"contentStage\": \"" + contentStage + "\",\n"
                + "  \"contentVersion\": \""
                    + FamilyRacing.Core.Progression.GameplayCatalog
                        .ContentVersion + "\",\n"
                + "  \"drivingVersion\": \"" + DrivingVersion + "\",\n"
                + "  \"candidate\": "
                    + (contentStage == "complete" ? "true" : "false")
                    + ",\n"
                + "  \"writtenUtc\": \""
                    + DateTime.UtcNow.ToString("o",
                        System.Globalization.CultureInfo.InvariantCulture)
                    + "\"\n"
                + "}\n";
            File.WriteAllText(Path.Combine(root, "manifest.json"), json);
        }
    }
}
