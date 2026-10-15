using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Progression;

namespace FamilyRacing.Core.Racing
{
    // One verdict for "may this profile enter this mode/event/vehicle
    // combination" (M5 Task 8). `allowed` plus the stable machine `code`
    // every consumer surfaces — garage pickers, command dispatch and the
    // LAN lobby all read the same answer; `vehicleId`/`eventId`/`classId`
    // carry the RESOLVED selection (the time trial's normalized starter
    // and implicit event differ from what was asked).
    public sealed class EntryDecision
    {
        public bool allowed;
        public string code = "";
        public string vehicleId = "";
        public string eventId = "";
        public string classId = "";
    }

    // The race-mode vocabulary shared by local modes, reward contexts
    // and results. `Practice` is the legacy free-drive lane; `Campaign`
    // and `QuickRace` seat one human against three same-class AI through
    // the shared four-racer coordinator; `TimeTrial` is a lone
    // normalized-starter run against the clock; `Lan` is the networked
    // session mode (never creatable through SoloRaceSession).
    public enum RaceMode
    {
        Practice = 0,
        Campaign = 1,
        QuickRace = 2,
        TimeTrial = 3,
        Lan = 4
    }

    // String identity + progression policy for the mode vocabulary.
    // Progression modes bind a durable RaceRewardContext at race start;
    // practice and time-trial never earn rewards and bind nothing.
    public static class RaceModeRules
    {
        public const string DefaultDifficultyId = "easy";

        public static string ModeId(RaceMode mode)
        {
            switch (mode)
            {
                case RaceMode.Practice: return "practice";
                case RaceMode.Campaign: return "campaign";
                case RaceMode.QuickRace: return "quickRace";
                case RaceMode.TimeTrial: return "timeTrial";
                case RaceMode.Lan: return "lan";
                default: return "";
            }
        }

        public static bool TryParse(string value, out RaceMode mode)
        {
            switch (value)
            {
                case "practice": mode = RaceMode.Practice; return true;
                case "campaign": mode = RaceMode.Campaign; return true;
                case "quickRace": mode = RaceMode.QuickRace; return true;
                case "timeTrial": mode = RaceMode.TimeTrial; return true;
                case "lan": mode = RaceMode.Lan; return true;
                default: mode = RaceMode.Practice; return false;
            }
        }

        // Modes whose finished human results may award progression:
        // coins, challenge-bounty progress and reward receipts.
        public static bool IsProgression(RaceMode mode)
        {
            return mode == RaceMode.Campaign || mode == RaceMode.QuickRace
                || mode == RaceMode.Lan;
        }

        // --- shared entry eligibility (M5 Task 8) ----------------------
        // The ONE verdict solo race creation, LAN lobby vehicle picks,
        // the garage/discovery surface and raw start commands all
        // consult. Progression modes (Campaign, Quick Race) require a
        // profile AND ownership of the asked vehicle; Campaign also
        // enforces the campaign progression gate (a finale stays locked
        // until CampaignRules opens it). LAN exposes every implemented
        // vehicle in the configured event — normalized racing never
        // consults unlocks — and Time Trial resolves its authored
        // normalized starter honestly. Practice is not an entry mode:
        // it keeps its legacy race.start path and answers invalidMode.

        public static EntryDecision CanEnter(RaceMode mode,
            string courseId, string eventId, string vehicleId,
            ProfileSave profile, GameplayCatalog catalog)
        {
            if (catalog == null || catalog.IsLegacy
                || catalog.Driving == null)
                return Deny("contentUnavailable");
            return Evaluate(mode, courseId, eventId, "", vehicleId,
                profile, catalog.Driving, catalog);
        }

        // The driving-catalog-only overload: LAN lobby checks and other
        // consumers that hold no gameplay catalog. Progression-mode
        // checks that need the campaign/economy answer report
        // contentUnavailable rather than guessing.
        public static EntryDecision CanEnter(RaceMode mode,
            string courseId, string eventId, string vehicleId,
            ProfileSave profile, DrivingContentCatalog content)
        {
            if (content == null) return Deny("contentUnavailable");
            return Evaluate(mode, courseId, eventId, "", vehicleId,
                profile, content, null);
        }

        // The classId-explicit internal seam: SoloRaceSession's trial
        // request and the LAN lobby's configured class both carry a
        // class that is NOT necessarily derivable from the vehicle id.
        internal static EntryDecision Evaluate(RaceMode mode,
            string courseId, string eventId, string classId,
            string vehicleId, ProfileSave profile,
            DrivingContentCatalog content, GameplayCatalog gameplay)
        {
            if (content == null) return Deny("contentUnavailable");
            switch (mode)
            {
                case RaceMode.Campaign:
                case RaceMode.QuickRace:
                    return EvaluateRace(mode, courseId, eventId, classId,
                        vehicleId, profile, content, gameplay);
                case RaceMode.TimeTrial:
                    return EvaluateTrial(courseId, eventId, classId,
                        vehicleId, content);
                case RaceMode.Lan:
                    return EvaluateLan(eventId, classId, vehicleId,
                        content);
                default:
                    return Deny("invalidMode");
            }
        }

        // Campaign/Quick Race: event membership, campaign progression,
        // ownership and class — the same precedence SoloRaceSession
        // enforced inline before this rule became the shared seam.
        // M5 Task 10: a quick race may name only course + class — the
        // authored circuit event for that pick resolves here so the
        // quickRace.start command, the UI picker and raw solo.start all
        // agree; a pick with no authored circuit reports unknownEvent.
        private static EntryDecision EvaluateRace(RaceMode mode,
            string courseId, string eventId, string classId,
            string vehicleId, ProfileSave profile,
            DrivingContentCatalog content, GameplayCatalog gameplay)
        {
            EventDefinition evt;
            if (string.IsNullOrEmpty(eventId))
            {
                if (mode != RaceMode.QuickRace)
                    return Deny("unknownEvent");
                evt = ResolveCircuitEvent(content, courseId, classId,
                    vehicleId);
                if (evt == null) return Deny("unknownEvent");
            }
            else if (!content.Events.TryGetValue(eventId, out evt)
                || evt == null)
                return Deny("unknownEvent");
            if (mode == RaceMode.Campaign)
            {
                if (gameplay == null || gameplay.Campaign == null)
                    return Deny("contentUnavailable");
                if (!IsCampaignEvent(gameplay, evt.id))
                    return Deny("eventNotCampaign");
                // The campaign progression gate: regular events are
                // always enterable; finales join the available set only
                // once the world's completion/star gate opens.
                var access = CampaignRules.Evaluate(
                    gameplay.Campaign, profile);
                if (!access.availableEventIds.Contains(evt.id))
                    return Deny("eventLocked");
            }
            if (string.IsNullOrEmpty(vehicleId)
                || !content.Vehicles.ContainsKey(vehicleId))
                return Deny("unknownVehicle");
            // A progression race seats the local human only in an
            // unlock the profile holds — a missing profile cannot prove
            // ownership.
            if (profile == null)
                return Deny("requiresSelectedProfile");
            if (profile.unlockedVehicles == null
                || !profile.unlockedVehicles.Contains(vehicleId))
                return Deny("vehicleLocked");
            if (!evt.AllowsVehicle(vehicleId))
                return Deny("vehicleNotForEvent");
            var resolvedClass = !string.IsNullOrEmpty(evt.vehicleClass)
                ? evt.vehicleClass
                : VehicleTuning.ClassIdOf(vehicleId);
            if (VehicleTuning.ClassIdOf(vehicleId) != resolvedClass)
                return Deny("invalidVehicle");
            if (!CourseExists(content, evt))
                return Deny("unknownCourse");
            return Allow(vehicleId, evt.id, resolvedClass);
        }

        // The authored circuit event for a course+class pick: the
        // explicit class wins, the asked vehicle's class derives it
        // otherwise. Deterministic across catalog edits — candidates
        // sort by id.
        private static EventDefinition ResolveCircuitEvent(
            DrivingContentCatalog content, string courseId,
            string classId, string vehicleId)
        {
            var derived = !string.IsNullOrEmpty(classId)
                ? classId : VehicleTuning.ClassIdOf(vehicleId);
            if (string.IsNullOrEmpty(derived)) return null;
            var matches = new List<EventDefinition>();
            if (content.Events != null)
                foreach (var candidate in content.Events.Values)
                    if (candidate != null
                        && candidate.eventType == GameplayEventType.Circuit
                        && candidate.courseId == courseId
                        && candidate.vehicleClass == derived)
                        matches.Add(candidate);
            matches.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            return matches.Count > 0 ? matches[0] : null;
        }

        // Time Trial: the authored `time` event for course+class (or an
        // explicitly named event), then the normalized starter — the
        // event's first class-legal vehicle. An explicitly different
        // requested vehicle rejects honestly rather than substituting.
        private static EntryDecision EvaluateTrial(string courseId,
            string eventId, string classId, string vehicleId,
            DrivingContentCatalog content)
        {
            var derived = !string.IsNullOrEmpty(classId)
                ? classId : VehicleTuning.ClassIdOf(vehicleId);
            EventDefinition evt = null;
            if (!string.IsNullOrEmpty(eventId))
            {
                if (!content.Events.TryGetValue(eventId, out evt)
                    || evt == null)
                    return Deny("unknownEvent");
            }
            else
            {
                var matches = new List<EventDefinition>();
                if (content.Events != null)
                    foreach (var candidate in content.Events.Values)
                        if (candidate != null
                            && candidate.eventType
                                == GameplayEventType.Time
                            && candidate.courseId == courseId
                            && candidate.vehicleClass == derived)
                            matches.Add(candidate);
                matches.Sort((a, b) =>
                    string.CompareOrdinal(a.id, b.id));
                if (matches.Count == 0) return Deny("unknownEvent");
                evt = matches[0];
            }
            var resolvedClass = !string.IsNullOrEmpty(evt.vehicleClass)
                ? evt.vehicleClass : derived;
            var starterId = "";
            if (evt.vehicleIds != null)
                foreach (var id in evt.vehicleIds)
                    if (string.IsNullOrEmpty(resolvedClass)
                        || VehicleTuning.ClassIdOf(id) == resolvedClass)
                    { starterId = id; break; }
            if (starterId == ""
                || !content.Vehicles.ContainsKey(starterId))
                return Deny("unknownVehicle");
            if (!string.IsNullOrEmpty(vehicleId))
            {
                if (!content.Vehicles.ContainsKey(vehicleId))
                    return Deny("unknownVehicle");
                if (vehicleId != starterId)
                    return Deny("invalidVehicle");
            }
            if (!CourseExists(content, evt))
                return Deny("unknownCourse");
            return Allow(starterId, evt.id, resolvedClass ?? "");
        }

        // LAN: every implemented vehicle in the configured event —
        // normalized racing never consults profile unlocks. The lobby's
        // configured class (or the event's authored class) binds the
        // seat exactly like NetworkRace's roster validation.
        private static EntryDecision EvaluateLan(string eventId,
            string classId, string vehicleId,
            DrivingContentCatalog content)
        {
            EventDefinition evt;
            if (string.IsNullOrEmpty(eventId)
                || !content.Events.TryGetValue(eventId, out evt)
                || evt == null)
                return Deny("unknownEvent");
            if (string.IsNullOrEmpty(vehicleId)
                || !content.Vehicles.ContainsKey(vehicleId))
                return Deny("unknownVehicle");
            if (!evt.AllowsVehicle(vehicleId))
                return Deny("vehicleNotForEvent");
            var resolvedClass = !string.IsNullOrEmpty(classId)
                ? classId
                : !string.IsNullOrEmpty(evt.vehicleClass)
                    ? evt.vehicleClass
                    : VehicleTuning.ClassIdOf(vehicleId);
            if (VehicleTuning.ClassIdOf(vehicleId) != resolvedClass)
                return Deny("invalidVehicle");
            if (!CourseExists(content, evt))
                return Deny("unknownCourse");
            return Allow(vehicleId, evt.id, resolvedClass);
        }

        private static bool IsCampaignEvent(GameplayCatalog catalog,
            string eventId)
        {
            var campaign = catalog != null ? catalog.Campaign : null;
            if (campaign == null || eventId == null) return false;
            if (campaign.regularEventIds != null
                && campaign.regularEventIds.Contains(eventId))
                return true;
            if (campaign.finales != null)
                foreach (var finale in campaign.finales)
                    if (finale != null && finale.eventId == eventId)
                        return true;
            return false;
        }

        private static bool CourseExists(
            DrivingContentCatalog content, EventDefinition evt)
        {
            CourseDefinition course;
            return content.Courses.TryGetValue(evt.courseId, out course)
                && course != null;
        }

        private static EntryDecision Deny(string code)
        {
            return new EntryDecision { allowed = false, code = code };
        }

        private static EntryDecision Allow(string vehicleId,
            string eventId, string classId)
        {
            return new EntryDecision
            {
                allowed = true, code = "",
                vehicleId = vehicleId ?? "",
                eventId = eventId ?? "",
                classId = classId ?? ""
            };
        }
    }
}
