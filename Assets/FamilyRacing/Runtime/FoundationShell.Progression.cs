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
        private Button resultsRetryButton;
        private Button resultsContinueButton;
        // Availability lands at build time, but the driving screen never
        // rebuilds mid-session — without a re-apply on gate flips the
        // overlay buttons keep the enabled state they were created with
        // (all three disabled before a result exists). Re-applying when
        // the finished/unresolved key changes keeps the buttons live
        // exactly when discovery says their commands apply.
        private string resultsAvailabilityKey = "?";

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
                    + progression.rewardUnresolved
                    + progression.adventureComplete
                    + progression.campaignWorldId;
                // M6 Task 3: the world strip repaints on availability,
                // selection or completion flips — a card tap lands in
                // the stream and the next signature re-renders once.
                foreach (var world in progression.worlds)
                    if (world != null)
                        key += "|w:" + world.worldId + "="
                            + (world.available ? "A" : "L")
                            + (world.selected ? "S" : "-")
                            + (world.worldComplete ? "D" : "-")
                            + world.completedEvents + "/"
                            + world.stars;
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
            var campaign = gameplay != null
                ? gameplay.FindCampaign(progression != null
                    ? progression.campaignWorldId : null)
                : null;
            // The map titles itself after the SELECTED world — the same
            // world whose rows the stream published in campaignEvents.
            var worldName = campaign != null
                ? (state.language == "he" ? campaign.nameHe : campaign.nameEn)
                : "";
            var title = AddLabel(scroll,
                string.IsNullOrEmpty(worldName)
                    ? T("campaign.title", state) : worldName, 40, Deep);
            title.name = "campaign.title";
            // Whole-adventure presentation: the banner appears only when
            // every authored world reports complete.
            if (progression != null && progression.adventureComplete)
            {
                var banner = AddLabel(scroll,
                    T("campaign.adventureComplete", state), 30, Coral);
                banner.name = "campaign.adventureComplete";
            }
            BuildWorldStrip(scroll, state, progression);
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
            // The pick belongs to the selected world's event view — a
            // stale id from before a world switch (or a profile change)
            // must not light the setup strip for a foreign event.
            var streamed = false;
            if (progression != null && progression.campaignEvents != null)
                foreach (var row in progression.campaignEvents)
                    if (row != null && row.eventId == campaignEventId)
                        streamed = true;
            if (!streamed) campaignEventId = "";
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

        // --- world strip (M6 Task 3) -------------------------------------
        // One card per authored world inside a horizontal scroll strip.
        // The catalog carries no authored swatch/icon art yet, so the
        // card cycles the shell palette and a stable glyph set by world
        // order — a deterministic identity until the content ships real
        // art. Every card mirrors the streamed row: enabled exactly when
        // the world is open, with the shared worldLocked reason on the
        // disabled cards and the selected badge on the selected one.

        // Stable per-order identity glyphs — deterministic across locales
        // and rebuilds until authored world art lands.
        private static readonly string[] WorldIcons =
            { "☀", "🏜", "❄", "🌊", "🌋", "🌙" };

        private void BuildWorldStrip(VisualElement parent,
            FoundationState state, ProgressionState progression)
        {
            if (progression == null || progression.worlds == null
                || progression.worlds.Count == 0)
                return;
            var header = AddLabel(parent, T("campaign.worlds", state),
                24, Deep);
            header.name = "campaign.worlds.header";
            var strip = new ScrollView(ScrollViewMode.Horizontal);
            strip.name = "campaign.worlds";
            strip.style.minHeight = 190;
            strip.style.flexShrink = 0;
            parent.Add(strip);
            for (var i = 0; i < progression.worlds.Count; i++)
            {
                var world = progression.worlds[i];
                if (world == null) continue;
                var worldId = world.worldId;
                // A Button so the card is the tap target; the children
                // are labels with picking ignored so taps land on the
                // card itself. The click runs the REAL semantic command —
                // never a local field write — so the streamed worldId is
                // the only selection source of truth.
                var card = new Button(() =>
                {
                    var p = new Dictionary<string, string>();
                    p["worldId"] = worldId;
                    app.Dispatch("campaign.selectWorld", p);
                    // The event pick belonged to the previous world's
                    // list; clear it so the setup strip cannot carry a
                    // foreign event across the restack.
                    campaignEventId = "";
                    campaignVehicleId = "";
                }) { name = "campaign.world." + worldId };
                card.style.width = 300;
                card.style.maxWidth = 560;
                card.style.minHeight = 170;
                card.style.marginTop = card.style.marginBottom = 6;
                card.style.marginLeft = card.style.marginRight = 6;
                card.style.backgroundColor = world.available
                    ? (world.selected ? Coral : Ocean) : Off;
                card.style.borderTopLeftRadius =
                    card.style.borderTopRightRadius =
                    card.style.borderBottomLeftRadius =
                    card.style.borderBottomRightRadius = 14;
                strip.Add(card);
                var top = Row(card);
                top.pickingMode = PickingMode.Ignore;
                var swatch = new VisualElement();
                swatch.name = "campaign.world." + worldId + ".swatch";
                swatch.style.width = swatch.style.height = 26;
                swatch.style.backgroundColor =
                    CardColors[i % CardColors.Length];
                swatch.style.borderTopLeftRadius =
                    swatch.style.borderTopRightRadius =
                    swatch.style.borderBottomLeftRadius =
                    swatch.style.borderBottomRightRadius = 13;
                swatch.pickingMode = PickingMode.Ignore;
                top.Add(swatch);
                var icon = AddLabel(top,
                    WorldIcons[i % WorldIcons.Length], 26, Color.white);
                icon.name = "campaign.world." + worldId + ".icon";
                icon.pickingMode = PickingMode.Ignore;
                var name = AddLabel(card,
                    state.language == "he" ? world.nameHe : world.nameEn,
                    24, Color.white);
                name.name = "campaign.world." + worldId + ".name";
                // Long world names wrap inside the fixed card cell —
                // the card never grows past its slot.
                name.style.whiteSpace = WhiteSpace.Normal;
                name.pickingMode = PickingMode.Ignore;
                var progress = AddLabel(card,
                    T("campaign.events", state) + " "
                        + Ltr(world.completedEvents + "/"
                            + world.requiredEvents)
                        + " · " + T("campaign.stars", state) + " "
                        + Ltr(world.stars + "/" + world.requiredStars),
                    18, Color.white);
                progress.name = "campaign.world." + worldId + ".progress";
                progress.pickingMode = PickingMode.Ignore;
                if (world.selected)
                {
                    var badge = AddLabel(card,
                        T("campaign.selected", state), 18, Seafoam);
                    badge.name = "campaign.world." + worldId + ".selected";
                    badge.pickingMode = PickingMode.Ignore;
                }
                if (world.worldComplete)
                {
                    var done = AddLabel(card,
                        T("campaign.worldDone", state), 18, Sand);
                    done.name = "campaign.world." + worldId + ".done";
                    done.pickingMode = PickingMode.Ignore;
                }
                if (!world.available)
                {
                    var locked = AddLabel(card,
                        LoopReason(world.lockedReason, state), 18, Cream);
                    locked.name = "campaign.world." + worldId + ".lock";
                    locked.pickingMode = PickingMode.Ignore;
                }
                // Command availability first (a LAN session fences the
                // pick for every card); the streamed lock then overrides
                // so a closed world shows its own reason, not the
                // generic one.
                ApplyAvailability(card, "campaign.selectWorld");
                if (!world.available)
                {
                    card.SetEnabled(false);
                    card.tooltip =
                        LoopReason(world.lockedReason, state);
                }
            }
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
            // Courses are honest AND world-aware (M6 Task 3): only those
            // with an authored circuit, grouped under their owning world
            // in catalog order, and a course inside a locked world stays
            // visible but disabled with the same worldLocked verdict
            // quickRace.start would reject with — selection can never
            // bypass the owning world's gate.
            var courses = CircuitCourses(gameplay.Driving);
            var grouped = new HashSet<string>(System.StringComparer.Ordinal);
            if (gameplay.Campaigns != null)
                foreach (var world in gameplay.Campaigns)
                {
                    if (world == null || world.courseIds == null) continue;
                    var owned = new List<string>();
                    foreach (var courseId in world.courseIds)
                        if (courses.Contains(courseId)
                            && grouped.Add(courseId))
                            owned.Add(courseId);
                    if (owned.Count == 0) continue;
                    var header = AddLabel(scroll,
                        state.language == "he"
                            ? world.nameHe : world.nameEn,
                        24, Deep);
                    header.name = "quick.world." + world.worldId;
                    var courseRow = Row(scroll);
                    courseRow.name = "quick.courses." + world.worldId;
                    foreach (var courseId in owned)
                    {
                        var id = courseId;
                        var button = AddButton(courseRow, id,
                            id == quickCourseId ? Coral : Ocean,
                            () => { quickCourseId = id; quickClassId = "";
                                quickVehicleId = ""; },
                            "quick.course." + id);
                        if (!AdventureRules.CanAccessCourse(gameplay,
                                state.savedProfile, id))
                        {
                            button.SetEnabled(false);
                            button.tooltip =
                                LoopReason("worldLocked", state);
                        }
                    }
                }
            // A course authored outside every world still lists — the
            // catalog gate, not the grouping, decides its availability.
            var rest = new List<string>();
            foreach (var courseId in courses)
                if (!grouped.Contains(courseId)) rest.Add(courseId);
            if (rest.Count > 0)
            {
                var courseRow = Row(scroll);
                courseRow.name = "quick.courses.other";
                foreach (var courseId in rest)
                {
                    var id = courseId;
                    var button = AddButton(courseRow, id,
                        id == quickCourseId ? Coral : Ocean,
                        () => { quickCourseId = id; quickClassId = "";
                            quickVehicleId = ""; },
                        "quick.course." + id);
                    if (!AdventureRules.CanAccessCourse(gameplay,
                            state.savedProfile, id))
                    {
                        button.SetEnabled(false);
                        button.tooltip = LoopReason("worldLocked", state);
                    }
                }
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
            // Trials are the always-open lane: every authored trial
            // course stays enterable on a fresh profile regardless of
            // the world gate (timeTrial.start answers the same verdict —
            // EvaluateTrial never consults the campaign), but the
            // grouping still names the owning world honestly.
            var courses = TrialCourses(gameplay.Driving);
            var grouped = new HashSet<string>(System.StringComparer.Ordinal);
            if (gameplay.Campaigns != null)
                foreach (var world in gameplay.Campaigns)
                {
                    if (world == null || world.courseIds == null) continue;
                    var owned = new List<string>();
                    foreach (var courseId in world.courseIds)
                        if (courses.Contains(courseId)
                            && grouped.Add(courseId))
                            owned.Add(courseId);
                    if (owned.Count == 0) continue;
                    var header = AddLabel(scroll,
                        state.language == "he"
                            ? world.nameHe : world.nameEn,
                        24, Deep);
                    header.name = "trial.world." + world.worldId;
                    var courseRow = Row(scroll);
                    courseRow.name = "trial.courses." + world.worldId;
                    foreach (var courseId in owned)
                    {
                        var id = courseId;
                        AddButton(courseRow, id,
                            id == trialCourseId ? Coral : Ocean,
                            () => { trialCourseId = id; trialClassId = ""; },
                            "trial.course." + id);
                    }
                }
            var rest = new List<string>();
            foreach (var courseId in courses)
                if (!grouped.Contains(courseId)) rest.Add(courseId);
            if (rest.Count > 0)
            {
                var courseRow = Row(scroll);
                courseRow.name = "trial.courses.other";
                foreach (var courseId in rest)
                {
                    var id = courseId;
                    AddButton(courseRow, id,
                        id == trialCourseId ? Coral : Ocean,
                        () => { trialCourseId = id; trialClassId = ""; },
                        "trial.course." + id);
                }
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
            var actions = Row(resultsOverlay);
            resultsRetryButton = AddButton(actions,
                T("results.again", state), Seafoam,
                () => app.Dispatch("results.retry", emptyParams),
                "results.retry");
            resultsContinueButton = AddButton(actions,
                T("results.next", state), Ocean,
                () => app.Dispatch("results.continue", emptyParams),
                "results.continue");
            resultsAvailabilityKey = ResultsGateKey(state);
            ApplyAvailability(resultsRetrySaveButton, "results.retrySave");
            ApplyAvailability(resultsRetryButton, "results.retry");
            ApplyAvailability(resultsContinueButton, "results.continue");
        }

        // The availability inputs the three overlay buttons depend on:
        // whether a finished result is up and whether the unresolved-
        // reward gate holds. A change means the buttons' enabled state
        // is stale — UpdateResultsOverlay re-applies on the flip.
        private string ResultsGateKey(FoundationState state)
        {
            var solo = state != null ? state.soloRace : null;
            var done = solo != null && solo.result != null
                && solo.phase == "finished";
            return (done ? "d" : "-")
                + (state != null && state.progression != null
                    && state.progression.rewardUnresolved ? "u" : "-");
        }

        private void UpdateResultsOverlay(FoundationState state)
        {
            if (resultsOverlay == null) return;
            var solo = state.soloRace;
            var result = solo != null ? solo.result : null;
            var done = result != null && solo.phase == "finished";
            resultsOverlay.style.display = done
                ? DisplayStyle.Flex : DisplayStyle.None;
            var gate = ResultsGateKey(state);
            if (gate != resultsAvailabilityKey)
            {
                resultsAvailabilityKey = gate;
                ApplyAvailability(resultsRetrySaveButton,
                    "results.retrySave");
                ApplyAvailability(resultsRetryButton, "results.retry");
                ApplyAvailability(resultsContinueButton,
                    "results.continue");
                // The pause/finish restart buttons share SoloRestart's
                // gate — they must grey out on the same flip or an
                // enabled control would silently reject.
                ApplyAvailability(pauseRetryButton, "race.restart");
                ApplyAvailability(finishRetryButton, "race.restart");
            }
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
