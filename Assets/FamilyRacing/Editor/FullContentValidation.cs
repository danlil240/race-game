using System;
using System.Collections.Generic;
using System.Text;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.EditorTools
{
    // M6 Task 4: the production-content gate. Validate() inspects the
    // authored GameplayCatalogAsset plus the converted driving catalog
    // against the fixed full-content contract — exactly the three
    // worlds, six courses, eight vehicle models, 72 regular events plus
    // six rival finales, six unique rivals, bilingual display names,
    // per-course spawn/recovery/pickup/shortcut inventory and the final
    // bilingual voice cue set. It returns a list of named reasons;
    // an empty list means structurally valid, never "playtested".
    //
    // This is deliberately a different level than the generic catalog
    // conversion (GameplayCatalogAsset.ToCatalog / GameplayCatalog
    // .TryCreate): a tiny fixture can be a perfectly valid generic
    // catalog and still fail here, because it is not the shipping
    // inventory. The production contract lives only in this class.
    public static class FullContentValidation
    {
        // The fixed production inventory (plan identity table): world,
        // course and class ids are authored constants, not discovered.
        public static readonly string[] WorldIds =
            { "sunny-island", "redrock-canyon", "snowy-peaks" };
        public static readonly string[][] WorldCourses =
        {
            new[] { "harbor-hop", "lighthouse-loop" },
            new[] { "canyon-dash", "mesa-skyway" },
            new[] { "snowpine-circuit", "aurora-pass" }
        };
        public static readonly string[] ClassIds = { "car", "motorcycle" };
        // Regular event ids end in these suffixes; "clean" maps onto the
        // runtime type GameplayEventType.CleanDriving ("clean-driving").
        public static readonly string[] TypeSuffixes =
            { "circuit", "boost", "stunt", "time", "gadget", "clean" };
        // Production roster: four rally cars and four motorcycles.
        public static readonly string[] VehicleIds =
        {
            "car-01", "car-02", "car-03", "car-04",
            "motorcycle-01", "motorcycle-02", "motorcycle-03",
            "motorcycle-04"
        };

        public const int SpawnSlotCount = 4;
        public const int MinShortcutLanes = 2;
        public const int MinPickupSites = 1;
        public const int MinSafePoses = 1;
        public const int RegularEventCount = 72;
        public const int FinaleCount = 6;

        public static string EventTypeOfSuffix(string suffix)
        {
            return suffix == "clean" ? GameplayEventType.CleanDriving
                : suffix;
        }

        public static string RegularEventId(string courseId,
            string classId, string suffix)
        {
            return courseId + "-" + classId + "-" + suffix;
        }

        public static string FinaleId(string worldId, string classId)
        {
            return worldId + "-" + classId + "-finale";
        }

        public static string RivalId(string worldId, string classId)
        {
            return worldId + "-" + classId + "-rival";
        }

        // The cosmetic a first rival victory grants (plan: livery-<rivalId>).
        public static string LiveryId(string worldId, string classId)
        {
            return "livery-" + RivalId(worldId, classId);
        }

        // World-completion reward vehicles (plan Tasks 5–7):
        // sunny-island -> *-02, redrock-canyon -> *-03, snowy-peaks -> *-04.
        public static string RewardVehicleId(int worldIndex, string classId)
        {
            return classId + "-0" + (worldIndex + 2);
        }

        // The cues a production catalog must ship in both locales:
        // three world introductions, six rival introductions, the three
        // objective explanations, world unlock and adventure completion.
        public static string[] RequiredCueIds()
        {
            var cues = new List<string>();
            foreach (var worldId in WorldIds)
                cues.Add("world.intro." + worldId);
            foreach (var worldId in WorldIds)
                foreach (var classId in ClassIds)
                    cues.Add("rival.intro." + RivalId(worldId, classId));
            cues.Add("objective.completion");
            cues.Add("objective.introductory");
            cues.Add("objective.mastery");
            cues.Add("world.unlocked");
            cues.Add("adventure.complete");
            return cues.ToArray();
        }

        public static IReadOnlyList<string> Validate(
            GameplayCatalogAsset asset, DrivingContentCatalog driving)
        {
            var reasons = new List<string>();
            if (asset == null)
                reasons.Add("catalog asset: missing GameplayCatalogAsset");
            if (driving == null)
                reasons.Add("driving catalog: missing");
            if (asset == null || driving == null)
                return reasons;

            // Generic conversion stays the floor: a catalog that cannot
            // convert can never be production content. Its error is a
            // named reason; the driving-level inventory checks below
            // still run so a single failure reports the full damage.
            var catalog = asset.ToCatalog(driving, out var error);
            if (catalog == null)
                reasons.Add("catalog: " + error);

            CheckVehicles(driving, reasons);
            CheckCourses(driving, reasons);
            CheckEvents(driving, reasons);
            if (catalog != null)
            {
                CheckWorlds(catalog, reasons);
                CheckFinales(catalog, driving, reasons);
                CheckRivals(catalog, reasons);
            }
            CheckVoicePrompts(asset, reasons);
            return reasons;
        }

        private static void CheckVehicles(DrivingContentCatalog driving,
            List<string> reasons)
        {
            foreach (var id in VehicleIds)
                if (!driving.Vehicles.ContainsKey(id))
                    reasons.Add("vehicles: missing '" + id + "'");
            foreach (var id in driving.Vehicles.Keys)
                if (Array.IndexOf(VehicleIds, id) < 0)
                    reasons.Add("vehicles: unexpected '" + id
                        + "' — the production roster is exactly "
                        + "car-01..04 and motorcycle-01..04");
        }

        private static void CheckCourses(DrivingContentCatalog driving,
            List<string> reasons)
        {
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var courses in WorldCourses)
                foreach (var courseId in courses)
                    expected.Add(courseId);
            foreach (var id in expected)
                if (!driving.Courses.ContainsKey(id))
                    reasons.Add("courses: missing '" + id + "'");
            foreach (var id in driving.Courses.Keys)
                if (!expected.Contains(id))
                    reasons.Add("courses: unexpected '" + id + "'");

            foreach (var courseId in expected)
            {
                CourseDefinition course;
                if (!driving.Courses.TryGetValue(courseId, out course)
                    || course == null)
                    continue;
                var slots = course.spawnSlots;
                var slotCount = slots != null ? slots.Count : 0;
                if (slotCount != SpawnSlotCount)
                    reasons.Add("course '" + courseId + "': expected "
                        + SpawnSlotCount + " spawnSlots, found "
                        + slotCount);
                if (course.safePoses == null || course.safePoses.Count
                        < MinSafePoses)
                    reasons.Add("course '" + courseId
                        + "': safePoses empty — authored recovery "
                        + "fallback poses are required");
                if (course.pickupSites == null || course.pickupSites.Count
                        < MinPickupSites)
                    reasons.Add("course '" + courseId
                        + "': pickupSites empty — every world hosts "
                        + "gadget events and needs authored pickup "
                        + "columns");
                CheckShortcuts(course, reasons);
            }
        }

        // Two distinct legal optional shortcuts per course, checked
        // against the same directed-segment model the runtime sweep
        // uses (CheckpointProgress.TryCross): each detector is the
        // segment centre +/- tangent * halfWidth on the ground plane,
        // the tangent being the authored direction rotated 90 degrees.
        // A lane whose segment meets a mandatory gate's segment shares
        // the gate's crossing region — crossing it IS the main route —
        // and two lanes sharing a segment at the same height are one
        // shortcut authored twice.
        //
        // Domains are chosen to match runtime semantics: the gate sweep
        // ignores y (TryCross projects onto the course plane), so a lane
        // threaded under an elevated gate still counts as overlapping —
        // height cannot clear a zone/gate overlap. The lane-vs-lane
        // dedup DOES use height, because stacked lanes on elevated
        // routes (Mesa Skyway/Aurora Pass) are legitimately distinct
        // shortcuts sharing ground-plane extent.
        //
        // Not enforceable here: "rejoins before the next required gate"
        // — ShortcutZone carries no ordering or rejoin geometry (no
        // gate indices, no entry/exit points), so that legality half
        // stays an authoring contract proven by traversal evidence,
        // never by this validator.
        private const float GateOverlapMeters = 0.05f;
        private const float SamePlaneDistanceMeters = 1f;
        private const float SamePlaneHeightMeters = 1f;

        private static void CheckShortcuts(CourseDefinition course,
            List<string> reasons)
        {
            var zones = course.shortcutZones;
            var count = zones != null ? zones.Count : 0;
            if (count < MinShortcutLanes)
                reasons.Add("course '" + course.id + "': expected at least "
                    + MinShortcutLanes + " shortcutZones, found " + count);
            if (zones == null) return;
            var gates = course.checkpoints != null
                ? course.checkpoints : new List<CheckpointGate>();
            for (var i = 0; i < zones.Count; i++)
            {
                var zone = zones[i];
                if (zone == null) continue;
                float ztx, ztz;
                // A zero direction bounds no segment — Validate() names
                // that defect as "shortcut.direction" itself.
                if (!DetectorTangent(zone.dirX, zone.dirZ,
                        out ztx, out ztz))
                    continue;
                foreach (var gate in gates)
                {
                    if (gate == null) continue;
                    float gtx, gtz;
                    if (!DetectorTangent(gate.dirX, gate.dirZ,
                            out gtx, out gtz))
                        continue;
                    if (SegmentDistanceXz(zone.x, zone.z, ztx, ztz,
                            zone.halfWidth, gate.x, gate.z, gtx, gtz,
                            gate.halfWidth) < GateOverlapMeters)
                    {
                        reasons.Add("course '" + course.id + "': shortcut '"
                            + zone.id + "' overlaps mandatory gate '"
                            + gate.id + "'");
                        break;
                    }
                }
                for (var j = i + 1; j < zones.Count; j++)
                {
                    var other = zones[j];
                    if (other == null) continue;
                    if (Math.Abs(zone.y - other.y) >= SamePlaneHeightMeters)
                        continue; // different elevation: distinct lanes
                    float otx, otz;
                    if (!DetectorTangent(other.dirX, other.dirZ,
                            out otx, out otz))
                        continue;
                    if (SegmentDistanceXz(zone.x, zone.z, ztx, ztz,
                            zone.halfWidth, other.x, other.z, otx, otz,
                            other.halfWidth) < SamePlaneDistanceMeters)
                        reasons.Add("course '" + course.id + "': shortcut '"
                            + zone.id + "' occupies the same plane at "
                            + "the same height as '" + other.id + "'");
                }
            }
        }

        // Unit tangent of a detector's lateral extent: a crossing plane
        // runs through the centre perpendicular to the authored
        // direction, so the segment's half-extents lie along (-nz, nx).
        private static bool DetectorTangent(float dirX, float dirZ,
            out float tx, out float tz)
        {
            tx = 0f; tz = 0f;
            var length = Math.Sqrt(dirX * dirX + dirZ * dirZ);
            // NaN fails the comparison too, so this covers zero/NaN.
            if (!(length >= 0.001)) return false;
            tx = (float)(-dirZ / length);
            tz = (float)(dirX / length);
            return true;
        }

        // Shortest distance between two detector segments on the ground
        // plane — centre +/- tangent * halfWidth, the full crossing
        // region the sweep credits, never just the centre point.
        private static float SegmentDistanceXz(
            float ax, float az, float atx, float atz, float aHalf,
            float bx, float bz, float btx, float btz, float bHalf)
        {
            var a1x = ax - atx * aHalf; var a1z = az - atz * aHalf;
            var a2x = ax + atx * aHalf; var a2z = az + atz * aHalf;
            var b1x = bx - btx * bHalf; var b1z = bz - btz * bHalf;
            var b2x = bx + btx * bHalf; var b2z = bz + btz * bHalf;
            if (ProperlyCrossXz(a1x, a1z, a2x, a2z,
                    b1x, b1z, b2x, b2z))
                return 0f;
            var distance = PointSegmentDistanceXz(a1x, a1z,
                b1x, b1z, b2x, b2z);
            distance = Math.Min(distance, PointSegmentDistanceXz(
                a2x, a2z, b1x, b1z, b2x, b2z));
            distance = Math.Min(distance, PointSegmentDistanceXz(
                b1x, b1z, a1x, a1z, a2x, a2z));
            distance = Math.Min(distance, PointSegmentDistanceXz(
                b2x, b2z, a1x, a1z, a2x, a2z));
            return (float)distance;
        }

        // Strict interior crossing of two ground-plane segments.
        // Endpoint distances alone miss an X where no endpoint sits
        // near the other segment; collinear overlaps and endpoint
        // touches are caught by the point distances instead.
        private static bool ProperlyCrossXz(
            float a1x, float a1z, float a2x, float a2z,
            float b1x, float b1z, float b2x, float b2z)
        {
            var d1 = CrossXz(b1x, b1z, b2x, b2z, a1x, a1z);
            var d2 = CrossXz(b1x, b1z, b2x, b2z, a2x, a2z);
            var d3 = CrossXz(a1x, a1z, a2x, a2z, b1x, b1z);
            var d4 = CrossXz(a1x, a1z, a2x, a2z, b2x, b2z);
            return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f))
                && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
        }

        // Signed cross of (p-o) x (q-o) projected on the ground plane.
        private static float CrossXz(float ox, float oz,
            float px, float pz, float qx, float qz)
        {
            return (px - ox) * (qz - oz) - (pz - oz) * (qx - ox);
        }

        private static float PointSegmentDistanceXz(float px, float pz,
            float ax, float az, float bx, float bz)
        {
            var abx = bx - ax; var abz = bz - az;
            var lengthSq = abx * abx + abz * abz;
            var t = lengthSq < 1e-9f ? 0f
                : ((px - ax) * abx + (pz - az) * abz) / lengthSq;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            var dx = px - (ax + abx * t);
            var dz = pz - (az + abz * t);
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        private static void CheckEvents(DrivingContentCatalog driving,
            List<string> reasons)
        {
            var expectedCourses = new HashSet<string>(
                StringComparer.Ordinal);
            foreach (var courses in WorldCourses)
                foreach (var courseId in courses)
                    expectedCourses.Add(courseId);
            var expectedClasses = new HashSet<string>(
                StringComparer.Ordinal);
            foreach (var classId in ClassIds) expectedClasses.Add(classId);
            var expectedTypes = new HashSet<string>(
                StringComparer.Ordinal);
            foreach (var suffix in TypeSuffixes)
                expectedTypes.Add(EventTypeOfSuffix(suffix));

            // The authored inventory is exactly 72 regular + 6 finale
            // events — a stray row no campaign claims is drift the
            // gate must name, never silently ignore.
            if (driving.Events.Count != RegularEventCount + FinaleCount)
                reasons.Add("events: expected exactly "
                    + (RegularEventCount + FinaleCount)
                    + " authored event definitions (" + RegularEventCount
                    + " regular + " + FinaleCount + " finales), found "
                    + driving.Events.Count);

            // Bucket every typed non-finale event by its
            // (course, class, type) cell.
            var cells = new Dictionary<string, List<EventDefinition>>(
                StringComparer.Ordinal);
            var finaleCount = 0;
            foreach (var evt in driving.Events.Values)
            {
                if (evt == null) continue;
                var isFinale = evt.isFinale
                    || evt.eventType == GameplayEventType.Finale;
                if (isFinale) { finaleCount++; continue; }
                // Generic conversion only validates campaign-claimed
                // events, so an untyped stray would slip the matrix
                // check below — name it instead of skipping it.
                if (string.IsNullOrEmpty(evt.eventType))
                {
                    reasons.Add("event '" + evt.id + "': untyped events "
                        + "are not production content");
                    continue;
                }
                if (!expectedCourses.Contains(evt.courseId)
                    || !expectedClasses.Contains(evt.vehicleClass)
                    || !expectedTypes.Contains(evt.eventType))
                {
                    reasons.Add("event '" + evt.id + "': typed event "
                        + "outside the production class x course x type "
                        + "matrix");
                    continue;
                }
                var key = evt.courseId + "/" + evt.vehicleClass + "/"
                    + evt.eventType;
                List<EventDefinition> bucket;
                if (!cells.TryGetValue(key, out bucket))
                {
                    bucket = new List<EventDefinition>();
                    cells[key] = bucket;
                }
                bucket.Add(evt);
            }

            foreach (var courses in WorldCourses)
                foreach (var courseId in courses)
                    foreach (var classId in ClassIds)
                        foreach (var suffix in TypeSuffixes)
                        {
                            var key = courseId + "/" + classId + "/"
                                + EventTypeOfSuffix(suffix);
                            var expectedId = RegularEventId(courseId,
                                classId, suffix);
                            List<EventDefinition> bucket;
                            if (!cells.TryGetValue(key, out bucket)
                                || bucket.Count == 0)
                            {
                                reasons.Add("event cell " + key
                                    + ": missing '" + expectedId + "'");
                                continue;
                            }
                            if (bucket.Count > 1)
                            {
                                var names = new StringBuilder();
                                foreach (var evt in bucket)
                                    names.Append("'").Append(evt.id)
                                        .Append("' ");
                                reasons.Add("event cell " + key
                                    + ": duplicate — " + names);
                                continue;
                            }
                            if (bucket[0].id != expectedId)
                                reasons.Add("event '" + bucket[0].id
                                    + "': id must be '" + expectedId + "'");
                        }

            if (finaleCount != FinaleCount)
                reasons.Add("finales: expected " + FinaleCount
                    + ", found " + finaleCount);
            var expectedFinales = new HashSet<string>(
                StringComparer.Ordinal);
            foreach (var worldId in WorldIds)
                foreach (var classId in ClassIds)
                    expectedFinales.Add(FinaleId(worldId, classId));
            // Driving-level existence is checked here (not only through
            // the converted catalog) so a missing finale reports its id
            // even when generic conversion fails first.
            foreach (var finaleId in expectedFinales)
                if (!driving.Events.ContainsKey(finaleId))
                    reasons.Add("finales: missing '" + finaleId + "'");
            foreach (var evt in driving.Events.Values)
            {
                if (evt == null) continue;
                if (!evt.isFinale
                    && evt.eventType != GameplayEventType.Finale)
                    continue;
                if (!expectedFinales.Contains(evt.id))
                    reasons.Add("finales: unexpected '" + evt.id + "'");
            }
        }

        private static void CheckWorlds(GameplayCatalog catalog,
            List<string> reasons)
        {
            var campaigns = catalog.Campaigns;
            var count = campaigns != null ? campaigns.Count : 0;
            if (count != WorldIds.Length)
                reasons.Add("worlds: expected " + WorldIds.Length
                    + " ordered ["
                    + string.Join(", ", WorldIds) + "], found " + count);
            if (campaigns == null) return;
            for (var i = 0; i < campaigns.Count; i++)
            {
                var campaign = campaigns[i];
                if (campaign == null) continue;
                if (i < WorldIds.Length && campaign.worldId != WorldIds[i])
                    reasons.Add("world[" + i + "]: expected '" + WorldIds[i]
                        + "', found '" + campaign.worldId + "'");
                var index = Array.IndexOf(WorldIds, campaign.worldId);
                if (index < 0) continue;
                // Bilingual display names: English must carry Latin
                // letters, Hebrew must carry U+0590..U+05FF characters —
                // a pasted English copy is not a translation.
                if (!HasLatin(campaign.nameEn))
                    reasons.Add("world '" + campaign.worldId
                        + "': nameEn must contain English text");
                if (!HasHebrew(campaign.nameHe))
                    reasons.Add("world '" + campaign.worldId
                        + "': nameHe must contain Hebrew text");
                var expectedCourses = WorldCourses[index];
                if (campaign.courseIds == null
                    || campaign.courseIds.Count != expectedCourses.Length
                    || !ContainsAll(campaign.courseIds, expectedCourses))
                    reasons.Add("world '" + campaign.worldId
                        + "': courseIds must be exactly ["
                        + string.Join(", ", expectedCourses) + "]");
            }
        }

        private static void CheckFinales(GameplayCatalog catalog,
            DrivingContentCatalog driving, List<string> reasons)
        {
            for (var w = 0; w < WorldIds.Length; w++)
            {
                var world = catalog.FindCampaign(WorldIds[w]);
                foreach (var classId in ClassIds)
                {
                    var finaleId = FinaleId(WorldIds[w], classId);
                    EventDefinition evt;
                    // Missing events were already named by the
                    // driving-level check; only the claim/type/reward
                    // details need the converted catalog.
                    if (!driving.Events.TryGetValue(finaleId, out evt)
                        || evt == null)
                        continue;
                    if (!evt.isFinale
                        || evt.eventType != GameplayEventType.Finale)
                        reasons.Add("finale '" + finaleId
                            + "': eventType must be 'finale' with "
                            + "isFinale");
                    if (evt.vehicleClass != classId)
                        reasons.Add("finale '" + finaleId
                            + "': vehicleClass must be '" + classId + "'");
                    FinaleDefinition row = null;
                    if (world == null)
                    {
                        reasons.Add("finale '" + finaleId
                            + "': world '" + WorldIds[w] + "' missing — "
                            + "no campaign can claim it");
                    }
                    else if (world.finales != null)
                    {
                        foreach (var candidate in world.finales)
                            if (candidate != null
                                && candidate.eventId == finaleId)
                                row = candidate;
                    }
                    if (world != null && row == null)
                    {
                        reasons.Add("finale '" + finaleId + "': world '"
                            + WorldIds[w] + "' does not claim it");
                        continue;
                    }
                    if (row == null) continue;
                    if (row.classId != classId)
                        reasons.Add("finale '" + finaleId
                            + "': classId must be '" + classId + "'");
                    var rivalId = RivalId(WorldIds[w], classId);
                    if (row.rivalId != rivalId)
                        reasons.Add("finale '" + finaleId
                            + "': rivalId must be '" + rivalId + "'");
                    var reward = RewardVehicleId(w, classId);
                    if (row.rewardVehicleId != reward)
                        reasons.Add("finale '" + finaleId
                            + "': rewardVehicleId must be '" + reward + "'");
                }
            }
        }

        // Six unique rivals, one per world x class finale; each carries
        // a one-time victory bounty (the reward path that grants its
        // livery-<rivalId> cosmetic). Unique rival ids make the six
        // livery ids distinct by construction — a cosmetic catalog that
        // binds the liveries themselves arrives with Tasks 5–8.
        private static void CheckRivals(GameplayCatalog catalog,
            List<string> reasons)
        {
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            if (catalog.Campaigns != null)
                foreach (var campaign in catalog.Campaigns)
                {
                    if (campaign == null || campaign.finales == null)
                        continue;
                    foreach (var finale in campaign.finales)
                        if (finale != null
                            && !string.IsNullOrEmpty(finale.rivalId))
                            claimed.Add(finale.rivalId);
                }
            var liveries = new HashSet<string>(StringComparer.Ordinal);
            foreach (var worldId in WorldIds)
                foreach (var classId in ClassIds)
                {
                    var rivalId = RivalId(worldId, classId);
                    if (!claimed.Contains(rivalId))
                        reasons.Add("rival '" + rivalId
                            + "': no finale claims it");
                    var found = false;
                    if (catalog.Bounties != null)
                        foreach (var bounty in catalog.Bounties)
                            if (bounty != null && !bounty.repeatable
                                && bounty.rivalId == rivalId)
                                found = true;
                    if (!found)
                        reasons.Add("rival '" + rivalId
                            + "': missing one-time victory bounty for '"
                            + LiveryId(worldId, classId) + "'");
                    liveries.Add(LiveryId(worldId, classId));
                }
            if (liveries.Count != FinaleCount)
                reasons.Add("cosmetics: expected " + FinaleCount
                    + " unique rival liveries, found " + liveries.Count);
        }

        // Final voice clips are production content: every required cue
        // must resolve a bound clip in both locales. Missing final clips
        // fail final-content validation — never a silent fallback.
        private static void CheckVoicePrompts(GameplayCatalogAsset asset,
            List<string> reasons)
        {
            if (asset.voicePrompts == null)
            {
                reasons.Add("voice prompts: missing "
                    + "VoicePromptCatalogAsset — final bilingual clips "
                    + "are required");
                return;
            }
            string voiceError;
            if (!asset.voicePrompts.Validate(out voiceError))
                reasons.Add("voice prompts: " + voiceError);
            foreach (var cueId in RequiredCueIds())
            {
                if (asset.voicePrompts.Resolve(cueId, "en") == null)
                    reasons.Add("voice cue '" + cueId
                        + "': missing english clip");
                if (asset.voicePrompts.Resolve(cueId, "he") == null)
                    reasons.Add("voice cue '" + cueId
                        + "': missing hebrew clip");
            }
        }

        private static bool ContainsAll(List<string> list,
            string[] expected)
        {
            foreach (var id in expected)
                if (!list.Contains(id)) return false;
            return true;
        }

        private static bool HasLatin(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var c in text)
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                    return true;
            return false;
        }

        private static bool HasHebrew(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var c in text)
                if (c >= '֐' && c <= '׿')
                    return true;
            return false;
        }
    }
}
