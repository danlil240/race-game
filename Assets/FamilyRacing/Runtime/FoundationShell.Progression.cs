using System.Collections.Generic;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using UnityEngine;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    // M5 Task 10 gameplay-loop screens: the campaign map, quick-race and
    // time-trial pickers, the standalone garage and the bounty board, plus
    // the local results overlay on the driving screen. Every actionable
    // widget routes through the same FoundationService.Execute path the
    // agent channel uses — buttons dispatch the semantic *.open / *.start /
    // results.* / upgrade.* commands, and pickers consult the shared
    // CanEnter verdict so a disabled button carries the same reason the
    // command would reject with.
    public sealed partial class FoundationShell
    {
        // Picker selections are shell-side (never persisted): they re-enter
        // the rebuild key so a tap repaints the screen exactly once and
        // held widgets elsewhere keep pointer capture.
        private string campaignEventId = "";
        private string campaignVehicleId = "";
        private string campaignDifficulty = "";
        private string quickCourseId = "";
        private string quickClassId = "";
        private string quickVehicleId = "";
        private string quickDifficulty = "";
        private string trialCourseId = "";
        private string trialClassId = "";
        private VisualElement resultsOverlay;
        private Label resultsPlaceLabel;
        private Label resultsTimeLabel;
        private Label resultsCoinsLabel;
        private Label resultsBreakdownLabel;
        private Label resultsRecordLabel;
        private Button resultsRetrySaveButton;

        private static bool IsLoopScreen(string screen)
        {
            return screen == FoundationService.ScreenCampaign
                || screen == FoundationService.ScreenQuickRace
                || screen == FoundationService.ScreenTimeTrial
                || screen == FoundationService.ScreenGarage
                || screen == FoundationService.ScreenBounties;
        }

        // Every fact a loop screen renders that Refresh's base key does not
        // already cover: picker selections, the resolved campaign rows
        // (lock/star state changes inside a profile that stays Loaded),
        // bounty progress, the unresolved-reward gate and the command
        // availabilities the start buttons reflect. An identical signature
        // provably renders an identical tree, so widgets stay put between
        // frames — the same promise the LAN and garage signatures make.
        private string LoopSignature(FoundationState state)
        {
            var key = campaignEventId + "|" + campaignVehicleId + "|"
                + campaignDifficulty + "|" + quickCourseId + "|"
                + quickClassId + "|" + quickVehicleId + "|"
                + quickDifficulty + "|" + trialCourseId + "|" + trialClassId;
            var progression = state != null ? state.progression : null;
            if (progression != null)
            {
                key += "|p:" + progression.available
                    + progression.campaignStars
                    + progression.campaignCompletedEvents
                    + progression.rewardUnresolved;
                foreach (var row in progression.campaignEvents)
                    if (row != null)
                        key += "|e:" + row.eventId + "=" + row.objectiveMask
                            + (row.locked ? "L" : "O");
                foreach (var bounty in progression.bounties)
                    if (bounty != null)
                        key += "|b:" + bounty.assignmentId + "="
                            + bounty.progress + "/" + bounty.threshold;
            }
            var profile = state != null ? state.savedProfile : null;
            if (profile != null)
            {
                key += "|c:" + profile.coins;
                if (profile.unlockedVehicles != null)
                    foreach (var id in profile.unlockedVehicles)
                        key += "|u:" + id;
                if (profile.timeTrialRecords != null)
                    foreach (var record in profile.timeTrialRecords)
                        if (record != null)
                            key += "|r:" + record.courseId + "/"
                                + record.classId + "=" + record.bestTicks;
            }
            return key + "|a:" + ActionAvailability("campaign.start")
                + "/" + ActionAvailability("quickRace.start")
                + "/" + ActionAvailability("timeTrial.start");
        }

        private GameplayCatalog LoopContent()
        {
            return app != null && app.Service != null
                ? app.Service.GameplayContent : null;
        }

        // Left-to-right isolate for numbers/time inside RTL layouts —
        // the same convention FormatRaceTime follows.
        private static string Ltr(string value)
        {
            return "‎" + (value ?? "");
        }

        private string DifficultyName(string difficultyId, FoundationState state)
        {
            try { return T("difficulty." + (difficultyId ?? ""), state); }
            catch (KeyNotFoundException) { return difficultyId ?? ""; }
        }

        private string EventKindName(string eventType, FoundationState state)
        {
            if (string.IsNullOrEmpty(eventType)) return "";
            try { return T("event.type." + eventType, state); }
            catch (KeyNotFoundException) { return eventType; }
        }

        private string BountyFamilyName(string family, FoundationState state)
        {
            if (string.IsNullOrEmpty(family)) return "";
            try { return T("bounty.family." + family, state); }
            catch (KeyNotFoundException) { return family; }
        }

        private string LoopReason(string code, FoundationState state)
        {
            if (string.IsNullOrEmpty(code)) return "";
            try { return T("loop.reason." + code, state); }
            catch (KeyNotFoundException) { return code; }
        }

        private Button LoopBackButton(VisualElement parent, FoundationState state)
        {
            var back = AddButton(parent, T("action.back", state), Deep,
                () => app.Dispatch("ui.navigate", Screen("sandbox")),
                "loop.back");
            ApplyAvailability(back, "ui.navigate");
            return back;
        }

        // --- campaign map -------------------------------------------------

        private void BuildCampaign(FoundationState state)
        {
            var scroll = new ScrollView();
            scroll.name = "campaign.scroll";
            scroll.style.flexGrow = 1;
            root.Add(scroll);
            var progression = state.progression;
            var gameplay = LoopContent();
            var campaign = gameplay != null ? gameplay.Campaign : null;
            var worldName = campaign != null
                ? (state.language == "he" ? campaign.nameHe : campaign.nameEn)
                : "";
            var title = AddLabel(scroll,
                string.IsNullOrEmpty(worldName)
                    ? T("campaign.title", state) : worldName, 40, Deep);
            title.name = "campaign.title";
            if (progression != null && progression.available)
            {
                var stars = AddLabel(scroll,
                    T("campaign.stars", state) + " "
                        + Ltr(progression.campaignStars + "/"
                            + progression.campaignRequiredStars)
                        + " · " + T("campaign.events", state) + " "
                        + Ltr(progression.campaignCompletedEvents + "/"
                            + progression.campaignRequiredEvents),
                    22, Deep);
                stars.name = "campaign.stars";
                if (progression.campaignWorldComplete)
                {
                    var done = AddLabel(scroll,
                        T("campaign.worldDone", state), 26, Coral);
                    done.name = "campaign.worldDone";
                }
            }
            var profile = state.savedProfile;
            // Every authored event in authored order: locked rows render
            // disabled with the lock reason — never silently hidden.
            if (progression != null)
                foreach (var row in progression.campaignEvents)
                {
                    if (row == null) continue;
                    var eventId = row.eventId;
                    var rowEl = Row(scroll);
                    rowEl.name = "campaign.row." + eventId;
                    var label = (row.finale ? "★ " : "🏁 ")
                        + EventKindName(row.eventType, state)
                        + " " + row.courseId;
                    var pick = AddButton(rowEl, label,
                        row.finale ? Coral : Ocean,
                        () => { campaignEventId = eventId; },
                        "campaign.event." + eventId);
                    if (row.locked)
                    {
                        pick.SetEnabled(false);
                        var lockLabel = AddLabel(rowEl,
                            T("event.locked", state), 18, Coral);
                        lockLabel.name = "campaign.event." + eventId
                            + ".lock";
                    }
                    var starLabel = AddLabel(rowEl,
                        Ltr(row.stars + "/3") + " ★", 20, Seafoam);
                    starLabel.name = "campaign.event." + eventId + ".stars";
                }
            // Selection-dependent setup strip: vehicle + difficulty +
            // the one big start button dispatching campaign.start.
            var setup = new VisualElement();
            setup.name = "campaign.setup";
            scroll.Add(setup);
            if (!string.IsNullOrEmpty(campaignEventId)
                && gameplay != null && gameplay.Driving != null)
            {
                EventDefinition evt;
                if (gameplay.Driving.Events.TryGetValue(campaignEventId,
                        out evt) && evt != null)
                {
                    if (evt.isFinale
                        && !string.IsNullOrEmpty(evt.id))
                    {
                        var finale = FindFinale(campaign, evt.id);
                        if (finale != null
                            && !string.IsNullOrEmpty(
                                finale.rewardVehicleId))
                        {
                            var reward = AddLabel(setup,
                                T("campaign.reward", state) + ": "
                                    + VehicleName(finale.rewardVehicleId,
                                        state),
                                20, Coral);
                            reward.name = "campaign.reward";
                        }
                    }
                    var vehiclesRow = Row(setup);
                    vehiclesRow.name = "campaign.vehicles";
                    foreach (var vehicleId in evt.vehicleIds)
                    {
                        var id = vehicleId;
                        var owned = profile != null
                            && profile.unlockedVehicles != null
                            && profile.unlockedVehicles.Contains(id);
                        var button = AddButton(vehiclesRow,
                            VehicleName(id, state),
                            id == campaignVehicleId ? Coral
                                : owned ? Seafoam : Off,
                            () => { campaignVehicleId = id; },
                            "campaign.vehicle." + id);
                        var verdict = app.Service.CanEnter(
                            RaceMode.Campaign, "", campaignEventId, id);
                        if (!verdict.allowed)
                        {
                            button.SetEnabled(false);
                            button.tooltip = LoopReason(verdict.code,
                                state);
                        }
                    }
                    var difficultyRow = Row(setup);
                    difficultyRow.name = "campaign.difficulties";
                    AddLabel(difficultyRow, T("difficulty.title", state),
                        22, Deep);
                    foreach (var difficulty in LoopDifficulties(gameplay))
                    {
                        var id = difficulty;
                        AddButton(difficultyRow, DifficultyName(id, state),
                            id == campaignDifficulty ? Coral : Ocean,
                            () => { campaignDifficulty = id; },
                            "campaign.difficulty." + id);
                    }
                    var start = AddButton(setup, T("loop.race", state),
                        Coral, () => StartCampaign(state),
                        "campaign.start");
                    ApplyAvailability(start, "campaign.start");
                    if (string.IsNullOrEmpty(campaignVehicleId))
                        start.SetEnabled(false);
                }
            }
            var actions = Row(scroll);
            LoopBackButton(actions, state);
            warningLabel = AddLabel(root, "", 18, Coral);
            warningLabel.name = "status.fontWarning";
            statusLabel = AddLabel(root, "", 20, Deep);
            statusLabel.name = "status.persistence";
        }

        private static FinaleDefinition FindFinale(CampaignDefinition campaign,
            string eventId)
        {
            if (campaign == null || campaign.finales == null) return null;
            foreach (var finale in campaign.finales)
                if (finale != null && finale.eventId == eventId)
                    return finale;
            return null;
        }

        private static List<string> LoopDifficulties(GameplayCatalog gameplay)
        {
            var ids = new List<string>();
            if (gameplay != null && gameplay.Difficulties != null)
                foreach (var difficulty in gameplay.Difficulties)
                    if (difficulty != null
                        && !string.IsNullOrEmpty(difficulty.id))
                        ids.Add(difficulty.id);
            if (ids.Count == 0) ids.Add(RaceModeRules.DefaultDifficultyId);
            return ids;
        }

        private void StartCampaign(FoundationState state)
        {
            if (string.IsNullOrEmpty(campaignEventId)
                || string.IsNullOrEmpty(campaignVehicleId)) return;
            var p = new Dictionary<string, string>();
            p["eventId"] = campaignEventId;
            p["vehicleId"] = campaignVehicleId;
            p["difficulty"] = string.IsNullOrEmpty(campaignDifficulty)
                ? RaceModeRules.DefaultDifficultyId : campaignDifficulty;
            app.Dispatch("campaign.start", p);
        }

        // --- quick race ---------------------------------------------------

        private void BuildQuickRace(FoundationState state)
        {
            var scroll = new ScrollView();
            scroll.name = "quick.scroll";
            scroll.style.flexGrow = 1;
            root.Add(scroll);
            var title = AddLabel(scroll, T("quick.title", state), 40, Deep);
            title.name = "quick.title";
            AddLabel(scroll, T("quick.hint", state), 20, Deep);
            var gameplay = LoopContent();
            if (gameplay == null || gameplay.Driving == null) return;
            // Courses are honest: only those with an authored circuit.
            var courses = CircuitCourses(gameplay.Driving);
            var courseRow = Row(scroll);
            courseRow.name = "quick.courses";
            foreach (var courseId in courses)
            {
                var id = courseId;
                AddButton(courseRow, id,
                    id == quickCourseId ? Coral : Ocean,
                    () => { quickCourseId = id; quickClassId = "";
                        quickVehicleId = ""; },
                    "quick.course." + id);
            }
            if (string.IsNullOrEmpty(quickCourseId)) goto Actions;
            var classRow = Row(scroll);
            classRow.name = "quick.classes";
            foreach (var classId in CircuitClasses(gameplay.Driving,
                quickCourseId))
            {
                var id = classId;
                AddButton(classRow, ClassName(id, state),
                    id == quickClassId ? Coral : Ocean,
                    () => { quickClassId = id; quickVehicleId = ""; },
                    "quick.class." + id);
            }
            if (string.IsNullOrEmpty(quickClassId)) goto Actions;
            var evt = ResolveCircuit(gameplay.Driving, quickCourseId,
                quickClassId);
            if (evt != null)
            {
                var vehicleRow = Row(scroll);
                vehicleRow.name = "quick.vehicles";
                var profile = state.savedProfile;
                foreach (var vehicleId in evt.vehicleIds)
                {
                    var id = vehicleId;
                    var owned = profile != null
                        && profile.unlockedVehicles != null
                        && profile.unlockedVehicles.Contains(id);
                    var button = AddButton(vehicleRow, VehicleName(id, state),
                        id == quickVehicleId ? Coral
                            : owned ? Seafoam : Off,
                        () => { quickVehicleId = id; },
                        "quick.vehicle." + id);
                    var verdict = app.Service.CanEnter(RaceMode.QuickRace,
                        quickCourseId, "", id);
                    if (!verdict.allowed)
                    {
                        button.SetEnabled(false);
                        button.tooltip = LoopReason(verdict.code, state);
                    }
                }
                var difficultyRow = Row(scroll);
                difficultyRow.name = "quick.difficulties";
                AddLabel(difficultyRow, T("difficulty.title", state),
                    22, Deep);
                foreach (var difficulty in LoopDifficulties(gameplay))
                {
                    var id = difficulty;
                    AddButton(difficultyRow, DifficultyName(id, state),
                        id == quickDifficulty ? Coral : Ocean,
                        () => { quickDifficulty = id; },
                        "quick.difficulty." + id);
                }
                var start = AddButton(scroll, T("loop.race", state), Coral,
                    () => StartQuickRace(), "quick.start");
                ApplyAvailability(start, "quickRace.start");
                if (string.IsNullOrEmpty(quickVehicleId))
                    start.SetEnabled(false);
            }
        Actions:
            var actions = Row(scroll);
            LoopBackButton(actions, state);
            warningLabel = AddLabel(root, "", 18, Coral);
            warningLabel.name = "status.fontWarning";
            statusLabel = AddLabel(root, "", 20, Deep);
            statusLabel.name = "status.persistence";
        }

        private static List<string> CircuitCourses(
            DrivingContentCatalog driving)
        {
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            var courses = new List<string>();
            if (driving == null || driving.Events == null) return courses;
            foreach (var evt in driving.Events.Values)
                if (evt != null
                    && evt.eventType == GameplayEventType.Circuit
                    && !string.IsNullOrEmpty(evt.courseId)
                    && seen.Add(evt.courseId))
                    courses.Add(evt.courseId);
            courses.Sort(System.StringComparer.Ordinal);
            return courses;
        }

        private static List<string> CircuitClasses(
            DrivingContentCatalog driving, string courseId)
        {
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            var classes = new List<string>();
            if (driving == null || driving.Events == null) return classes;
            foreach (var evt in driving.Events.Values)
                if (evt != null
                    && evt.eventType == GameplayEventType.Circuit
                    && evt.courseId == courseId
                    && !string.IsNullOrEmpty(evt.vehicleClass)
                    && seen.Add(evt.vehicleClass))
                    classes.Add(evt.vehicleClass);
            classes.Sort(System.StringComparer.Ordinal);
            return classes;
        }

        // Mirrors RaceModeRules.ResolveCircuitEvent: the deterministic
        // lowest-id authored circuit for the course+class pick — the UI
        // renders the same event the command resolves.
        private static EventDefinition ResolveCircuit(
            DrivingContentCatalog driving, string courseId, string classId)
        {
            if (driving == null || driving.Events == null) return null;
            EventDefinition best = null;
            foreach (var evt in driving.Events.Values)
                if (evt != null
                    && evt.eventType == GameplayEventType.Circuit
                    && evt.courseId == courseId
                    && evt.vehicleClass == classId
                    && (best == null
                        || string.CompareOrdinal(evt.id, best.id) < 0))
                    best = evt;
            return best;
        }

        // ClassName lives in FoundationShell.Network.cs (the LAN class
        // label convention is shared verbatim).

        private void StartQuickRace()
        {
            if (string.IsNullOrEmpty(quickCourseId)
                || string.IsNullOrEmpty(quickClassId)
                || string.IsNullOrEmpty(quickVehicleId)) return;
            var p = new Dictionary<string, string>();
            p["courseId"] = quickCourseId;
            p["classId"] = quickClassId;
            p["vehicleId"] = quickVehicleId;
            p["difficulty"] = string.IsNullOrEmpty(quickDifficulty)
                ? RaceModeRules.DefaultDifficultyId : quickDifficulty;
            app.Dispatch("quickRace.start", p);
        }

        // --- time trial ---------------------------------------------------

        private void BuildTimeTrial(FoundationState state)
        {
            var scroll = new ScrollView();
            scroll.name = "trial.scroll";
            scroll.style.flexGrow = 1;
            root.Add(scroll);
            var title = AddLabel(scroll, T("trial.title", state), 40, Deep);
            title.name = "trial.title";
            AddLabel(scroll, T("trial.hint", state), 20, Deep);
            var gameplay = LoopContent();
            if (gameplay == null || gameplay.Driving == null) return;
            var courseRow = Row(scroll);
            courseRow.name = "trial.courses";
            foreach (var courseId in TrialCourses(gameplay.Driving))
            {
                var id = courseId;
                AddButton(courseRow, id,
                    id == trialCourseId ? Coral : Ocean,
                    () => { trialCourseId = id; trialClassId = ""; },
                    "trial.course." + id);
            }
            if (string.IsNullOrEmpty(trialCourseId)) goto Actions;
            var classRow = Row(scroll);
            classRow.name = "trial.classes";
            foreach (var classId in TrialClasses(gameplay.Driving,
                trialCourseId))
            {
                var id = classId;
                AddButton(classRow, ClassName(id, state),
                    id == trialClassId ? Coral : Ocean,
                    () => { trialClassId = id; },
                    "trial.class." + id);
            }
            if (string.IsNullOrEmpty(trialClassId)) goto Actions;
            var evt = ResolveTrial(gameplay.Driving, trialCourseId,
                trialClassId);
            if (evt != null)
            {
                // The normalized starter is what the session seats — the
                // shared verdict resolves it so the label can never
                // diverge from what timeTrial.start binds.
                var verdict = app.Service.CanEnter(RaceMode.TimeTrial,
                    trialCourseId, "", trialClassId, "");
                var starter = AddLabel(scroll,
                    T("trial.starter", state) + " "
                        + VehicleName(verdict.vehicleId, state), 22, Deep);
                starter.name = "trial.starter";
                if (evt.parTicks > 0)
                {
                    var par = AddLabel(scroll,
                        T("trial.par", state) + " "
                            + FormatRaceTime(evt.parTicks), 20, Ocean);
                    par.name = "trial.par";
                }
                var record = RecordFor(state.savedProfile,
                    trialCourseId, trialClassId);
                var recordLabel = AddLabel(scroll,
                    T("trial.record", state) + " "
                        + (record != null
                            ? FormatRaceTime(record.bestTicks)
                            : T("trial.noRecord", state)),
                    20, Seafoam);
                recordLabel.name = "trial.record";
                var start = AddButton(scroll, T("loop.race", state), Coral,
                    () => StartTimeTrial(), "trial.start");
                ApplyAvailability(start, "timeTrial.start");
                if (!verdict.allowed)
                {
                    start.SetEnabled(false);
                    start.tooltip = LoopReason(verdict.code, state);
                }
            }
        Actions:
            var actions = Row(scroll);
            LoopBackButton(actions, state);
            warningLabel = AddLabel(root, "", 18, Coral);
            warningLabel.name = "status.fontWarning";
            statusLabel = AddLabel(root, "", 20, Deep);
            statusLabel.name = "status.persistence";
        }

        private static List<string> TrialCourses(
            DrivingContentCatalog driving)
        {
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            var courses = new List<string>();
            if (driving == null || driving.Events == null) return courses;
            foreach (var evt in driving.Events.Values)
                if (evt != null && evt.eventType == GameplayEventType.Time
                    && !string.IsNullOrEmpty(evt.courseId)
                    && seen.Add(evt.courseId))
                    courses.Add(evt.courseId);
            courses.Sort(System.StringComparer.Ordinal);
            return courses;
        }

        private static List<string> TrialClasses(
            DrivingContentCatalog driving, string courseId)
        {
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            var classes = new List<string>();
            if (driving == null || driving.Events == null) return classes;
            foreach (var evt in driving.Events.Values)
                if (evt != null && evt.eventType == GameplayEventType.Time
                    && evt.courseId == courseId
                    && !string.IsNullOrEmpty(evt.vehicleClass)
                    && seen.Add(evt.vehicleClass))
                    classes.Add(evt.vehicleClass);
            classes.Sort(System.StringComparer.Ordinal);
            return classes;
        }

        private static EventDefinition ResolveTrial(
            DrivingContentCatalog driving, string courseId, string classId)
        {
            if (driving == null || driving.Events == null) return null;
            EventDefinition best = null;
            foreach (var evt in driving.Events.Values)
                if (evt != null && evt.eventType == GameplayEventType.Time
                    && evt.courseId == courseId
                    && evt.vehicleClass == classId
                    && (best == null
                        || string.CompareOrdinal(evt.id, best.id) < 0))
                    best = evt;
            return best;
        }

        private static FamilyRacing.Core.Profiles.TimeTrialRecord
            RecordFor(FamilyRacing.Core.Profiles.ProfileSave profile,
                string courseId, string classId)
        {
            if (profile == null || profile.timeTrialRecords == null)
                return null;
            foreach (var record in profile.timeTrialRecords)
                if (record != null && record.courseId == courseId
                    && record.classId == classId)
                    return record;
            return null;
        }

        private void StartTimeTrial()
        {
            if (string.IsNullOrEmpty(trialCourseId)
                || string.IsNullOrEmpty(trialClassId)) return;
            var p = new Dictionary<string, string>();
            p["courseId"] = trialCourseId;
            p["classId"] = trialClassId;
            app.Dispatch("timeTrial.start", p);
        }

        // --- garage (standalone screen) -----------------------------------

        private void BuildGarageScreen(FoundationState state)
        {
            var scroll = new ScrollView();
            scroll.name = "garage.scroll";
            scroll.style.flexGrow = 1;
            root.Add(scroll);
            garagePanel = new VisualElement();
            garagePanel.name = "garage.panel";
            garagePanel.style.marginTop = 8;
            scroll.Add(garagePanel);
            var actions = Row(scroll);
            LoopBackButton(actions, state);
            warningLabel = AddLabel(root, "", 18, Coral);
            warningLabel.name = "status.fontWarning";
            statusLabel = AddLabel(root, "", 20, Deep);
            statusLabel.name = "status.persistence";
        }

        // --- bounty board -------------------------------------------------

        private void BuildBounties(FoundationState state)
        {
            var title = AddLabel(root, T("bounties.title", state), 40, Deep);
            title.name = "bounties.title";
            AddLabel(root, T("bounties.hint", state), 20, Deep);
            var profile = state.savedProfile;
            var coins = AddLabel(root,
                T("garage.coins", state) + " "
                    + Ltr(profile != null
                        ? profile.coins.ToString() : "0"),
                24, Coral);
            coins.name = "bounties.coins";
            var progression = state.progression;
            if (progression != null)
                foreach (var row in progression.bounties)
                {
                    if (row == null) continue;
                    var rowEl = Row(root);
                    rowEl.name = "bounty." + row.assignmentId;
                    var name = AddLabel(rowEl,
                        BountyFamilyName(row.family, state), 22, Deep);
                    name.name = "bounty." + row.assignmentId + ".name";
                    var progress = AddLabel(rowEl,
                        T("bounties.progress", state) + " "
                            + Ltr(row.progress + "/" + row.threshold),
                        20, Deep);
                    progress.name = "bounty." + row.assignmentId
                        + ".progress";
                    var reward = AddLabel(rowEl,
                        T("bounties.reward", state) + " "
                            + Ltr(row.rewardCoins + " "
                                + T("garage.coins", state)),
                        20, Seafoam);
                    reward.name = "bounty." + row.assignmentId + ".reward";
                    var done = AddLabel(rowEl,
                        row.completed ? T("bounties.done", state) : "",
                        22, Coral);
                    done.name = "bounty." + row.assignmentId + ".done";
                }
            var actions = Row(root);
            LoopBackButton(actions, state);
            warningLabel = AddLabel(root, "", 18, Coral);
            warningLabel.name = "status.fontWarning";
            statusLabel = AddLabel(root, "", 20, Deep);
            statusLabel.name = "status.persistence";
        }

        // --- solo results overlay (driving screen) ------------------------
        // One panel shown once the local session finalized a result: the
        // placement, finish time, the honest coin breakdown from the
        // durable receipt, the retry-save affordance while a commit is
        // unresolved, and the one-action replay/continue pair — all
        // through the same results.* commands the agent channel drives.

        private void BuildResultsOverlay(FoundationState state)
        {
            resultsOverlay = Overlay("overlay.results");
            var title = AddLabel(resultsOverlay, T("results.title", state),
                44, Deep);
            title.name = "results.title";
            resultsPlaceLabel = AddLabel(resultsOverlay, "", 34, Coral);
            resultsPlaceLabel.name = "results.place";
            resultsTimeLabel = AddLabel(resultsOverlay, "", 26, Deep);
            resultsTimeLabel.name = "results.time";
            resultsCoinsLabel = AddLabel(resultsOverlay, "", 26, Seafoam);
            resultsCoinsLabel.name = "results.coins";
            resultsBreakdownLabel = AddLabel(resultsOverlay, "", 20, Deep);
            resultsBreakdownLabel.name = "results.breakdown";
            resultsRecordLabel = AddLabel(resultsOverlay, "", 26, Coral);
            resultsRecordLabel.name = "results.record";
            var saveRow = Row(resultsOverlay);
            resultsRetrySaveButton = AddButton(saveRow,
                T("results.retrySave", state), Coral,
                () => app.Dispatch("results.retrySave", emptyParams),
                "results.retrySave");
            ApplyAvailability(resultsRetrySaveButton, "results.retrySave");
            var actions = Row(resultsOverlay);
            var retry = AddButton(actions, T("results.again", state),
                Seafoam,
                () => app.Dispatch("results.retry", emptyParams),
                "results.retry");
            var next = AddButton(actions, T("results.next", state), Ocean,
                () => app.Dispatch("results.continue", emptyParams),
                "results.continue");
            ApplyAvailability(retry, "results.retry");
            ApplyAvailability(next, "results.continue");
        }

        private void UpdateResultsOverlay(FoundationState state)
        {
            if (resultsOverlay == null) return;
            var solo = state.soloRace;
            var result = solo != null ? solo.result : null;
            var done = result != null && solo.phase == "finished";
            resultsOverlay.style.display = done
                ? DisplayStyle.Flex : DisplayStyle.None;
            if (!done) return;
            FamilyRacing.Core.Racing.RaceResultEntry local = null;
            if (result.racers != null)
                foreach (var entry in result.racers)
                    if (entry != null
                        && entry.participantId == solo.participantId)
                    { local = entry; break; }
            if (resultsPlaceLabel != null && local != null)
                resultsPlaceLabel.text = T("results.place", state) + " "
                    + Ltr(local.position + "/"
                        + (result.racers != null
                            ? result.racers.Count : 1));
            if (resultsTimeLabel != null && local != null
                && local.finishTicks > 0)
                resultsTimeLabel.text = T("hud.time", state) + " "
                    + FormatRaceTime(local.finishTicks);
            // The durable receipt carries the honest breakdown — a
            // pending save shows the entry's award, a committed one
            // shows exactly what was written.
            var receipt = ReceiptFor(state.savedProfile, result.resultId);
            var coins = receipt != null ? receipt.awardedCoins
                : local != null ? local.coinsAwarded : 0;
            if (resultsCoinsLabel != null)
                resultsCoinsLabel.text = T("results.coins", state) + " "
                    + Ltr(coins.ToString());
            if (resultsBreakdownLabel != null)
                resultsBreakdownLabel.text = receipt != null
                    ? CoinBreakdown(receipt, state) : "";
            if (resultsRecordLabel != null)
                resultsRecordLabel.text = receipt != null
                    && receipt.recordImproved
                        ? T("results.newRecord", state) + " "
                            + FormatRaceTime(receipt.recordBestTicks)
                        : "";
            if (resultsRetrySaveButton != null)
                resultsRetrySaveButton.style.display =
                    state.progression != null
                        && state.progression.rewardUnresolved
                        ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private string CoinBreakdown(
            FamilyRacing.Core.Profiles.AppliedResultReceipt receipt,
            FoundationState state)
        {
            var parts = new List<string>();
            if (receipt.completionCoins > 0)
                parts.Add(T("results.coins.finish", state) + " "
                    + Ltr(receipt.completionCoins.ToString()));
            if (receipt.placementCoins > 0)
                parts.Add(T("results.coins.place", state) + " "
                    + Ltr(receipt.placementCoins.ToString()));
            if (receipt.challengeCoins > 0)
                parts.Add(T("results.coins.star", state) + " "
                    + Ltr(receipt.challengeCoins.ToString()));
            if (receipt.bountyCoins > 0)
                parts.Add(T("results.coins.bounty", state) + " "
                    + Ltr(receipt.bountyCoins.ToString()));
            if (receipt.rivalCoins > 0)
                parts.Add(T("results.coins.rival", state) + " "
                    + Ltr(receipt.rivalCoins.ToString()));
            return string.Join(" · ", parts);
        }

        private static FamilyRacing.Core.Profiles.AppliedResultReceipt
            ReceiptFor(FamilyRacing.Core.Profiles.ProfileSave profile,
                string resultId)
        {
            if (profile == null || profile.appliedResults == null
                || string.IsNullOrEmpty(resultId)) return null;
            foreach (var receipt in profile.appliedResults)
                if (receipt != null && receipt.resultId == resultId)
                    return receipt;
            return null;
        }
    }
}
