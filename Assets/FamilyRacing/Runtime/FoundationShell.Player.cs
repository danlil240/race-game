using System.Collections.Generic;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using UnityEngine;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    // Opt-in player presentation layered over FoundationShell when AssetTrialHud
    // is attached. Preserves semantic element names and command routing while
    // replacing the diagnostic sandbox with a player home menu, racer cards,
    // visual setup cards, bounded pause/results overlays, and a clean race HUD.
    public sealed partial class FoundationShell
    {
        private static readonly Color PlayerInk = new Color(0.07f, 0.12f, 0.18f, 1f);
        private static readonly Color PlayerNavy = new Color(0.11f, 0.19f, 0.27f, 0.96f);
        private static readonly Color PlayerSlate = new Color(0.17f, 0.28f, 0.38f, 0.96f);
        private static readonly Color PlayerCream = new Color(0.98f, 0.96f, 0.90f, 1f);
        private static readonly Color PlayerMuted = new Color(0.74f, 0.82f, 0.87f, 1f);
        private static readonly Color PlayerOrange = new Color(0.95f, 0.48f, 0.22f, 1f);
        private static readonly Color PlayerMint = new Color(0.35f, 0.84f, 0.72f, 1f);
        private static readonly Color PlayerScrim = new Color(0.03f, 0.06f, 0.10f, 0.78f);

        private void EnsurePlayerSetupDefaults(FoundationState state)
        {
            var gameplay = LoopContent();
            if (gameplay == null || gameplay.Driving == null) return;
            if (state.screen == FoundationService.ScreenQuickRace)
            {
                if (string.IsNullOrEmpty(quickCourseId))
                {
                    foreach (var courseId in CircuitCourses(gameplay.Driving))
                        if (AdventureRules.CanAccessCourse(gameplay, state.savedProfile, courseId))
                        { quickCourseId = courseId; break; }
                }
                if (!string.IsNullOrEmpty(quickCourseId) && string.IsNullOrEmpty(quickClassId))
                {
                    var classes = CircuitClasses(gameplay.Driving, quickCourseId);
                    if (classes.Count > 0) quickClassId = classes[0];
                }
                if (!string.IsNullOrEmpty(quickCourseId) && !string.IsNullOrEmpty(quickClassId)
                    && string.IsNullOrEmpty(quickVehicleId))
                {
                    var evt = ResolveCircuit(gameplay.Driving, quickCourseId, quickClassId);
                    if (evt != null && evt.vehicleIds != null)
                        foreach (var vehicleId in evt.vehicleIds)
                            if (app.Service.CanEnter(RaceMode.QuickRace, quickCourseId, "", vehicleId).allowed)
                            { quickVehicleId = vehicleId; break; }
                }
                if (string.IsNullOrEmpty(quickDifficulty))
                    quickDifficulty = LoopDifficulties(gameplay)[0];
            }
            else if (state.screen == FoundationService.ScreenTimeTrial)
            {
                if (string.IsNullOrEmpty(trialCourseId))
                {
                    var courses = TrialCourses(gameplay.Driving);
                    if (courses.Count > 0) trialCourseId = courses[0];
                }
                if (!string.IsNullOrEmpty(trialCourseId) && string.IsNullOrEmpty(trialClassId))
                {
                    var classes = TrialClasses(gameplay.Driving, trialCourseId);
                    if (classes.Count > 0) trialClassId = classes[0];
                }
            }
            else if (state.screen == FoundationService.ScreenCampaign)
            {
                var prog = state.progression;
                if (prog != null && prog.campaignEvents != null)
                {
                    var valid = false;
                    foreach (var row in prog.campaignEvents)
                        if (row != null && row.eventId == campaignEventId && !row.locked)
                            valid = true;
                    if (!valid)
                        foreach (var row in prog.campaignEvents)
                            if (row != null && !row.locked)
                            { campaignEventId = row.eventId; campaignVehicleId = ""; break; }
                }
                if (!string.IsNullOrEmpty(campaignEventId) && string.IsNullOrEmpty(campaignVehicleId)
                    && gameplay.Driving.Events.TryGetValue(campaignEventId, out var evt)
                    && evt != null && evt.vehicleIds != null)
                {
                    foreach (var vehicleId in evt.vehicleIds)
                        if (app.Service.CanEnter(RaceMode.Campaign, "", campaignEventId, vehicleId).allowed)
                        { campaignVehicleId = vehicleId; break; }
                }
                if (string.IsNullOrEmpty(campaignDifficulty))
                    campaignDifficulty = LoopDifficulties(gameplay)[0];
            }
        }

        private void BuildPlayerProfiles(FoundationState state)
        {
            var title = AddLabel(root, T("home.title", state), 40, PlayerCream);
            title.name = "player.title";
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            var subtitle = AddLabel(root, T("profile.select", state), 22, PlayerMint);
            subtitle.name = "profile.select";
            var disclaimer = AddLabel(root, T(LoopContentAvailable()
                ? "shell.disclaimer" : "shell.disclaimer.unavailable",
                state), 15, PlayerMuted);
            disclaimer.name = "shell.disclaimer";
            disclaimer.style.whiteSpace = WhiteSpace.Normal;
            disclaimer.style.maxWidth = 980;
            disclaimer.style.alignSelf = Align.Center;
            disclaimer.style.marginBottom = 8;

            var cards = Row(root);
            cards.name = "profile.cards";
            cards.style.marginTop = 4;
            cards.style.marginBottom = 8;
            for (var i = 0; i < state.slots.Count; i++)
            {
                var slot = i;
                var info = state.slots[slot];
                var selected = state.activeSlot == slot;
                var accent = CardColors[slot % CardColors.Length];
                var card = new VisualElement { name = "profile." + slot };
                card.style.width = 236;
                card.style.marginLeft = card.style.marginRight = 8;
                card.style.marginTop = card.style.marginBottom = 6;
                card.style.paddingTop = card.style.paddingBottom = 12;
                card.style.paddingLeft = card.style.paddingRight = 12;
                card.style.backgroundColor = PlayerNavy;
                Round(card, 18);
                Border(card, selected ? PlayerMint : accent, selected ? 3f : 2f);

                var portraitFrame = new VisualElement { name = "profile." + slot + ".portrait" };
                portraitFrame.style.height = 132;
                portraitFrame.style.marginBottom = 8;
                portraitFrame.style.backgroundColor = Color.Lerp(PlayerInk, accent, 0.35f);
                Round(portraitFrame, 12);
                var portrait = playerHud != null ? playerHud.Portrait(slot) : null;
                if (portrait != null)
                {
                    portraitFrame.style.backgroundImage = new StyleBackground(portrait);
                    portraitFrame.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                    portraitFrame.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                    portraitFrame.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                    portraitFrame.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                }
                card.Add(portraitFrame);

                var animal = AddLabel(card, T(AnimalKeys[slot % AnimalKeys.Length], state), 24, PlayerCream);
                animal.style.unityFontStyleAndWeight = FontStyle.Bold;
                animal.style.whiteSpace = WhiteSpace.Normal;

                var statusText = info.status == "Missing"
                    ? T("player.profile.new", state)
                    : info.status == "Blocked"
                        ? T("status.blocked", state)
                        : info.status == "RecoveredFromBackup"
                            ? T("status.recovered", state)
                            : info.profile != null
                                ? T("garage.coins", state) + " " + Ltr(info.profile.coins.ToString())
                                : T("player.profile.ready", state);
                var statusColor = info.status == "Blocked" ? Coral
                    : info.status == "RecoveredFromBackup" ? PlayerOrange
                    : info.status == "Loaded" ? PlayerMint : PlayerMuted;
                var slotStatus = AddLabel(card, statusText, 14, statusColor);
                slotStatus.name = "profile." + slot + ".status";
                slotStatus.style.whiteSpace = WhiteSpace.Normal;
                slotStatus.style.minHeight = 36;

                var actionLabel = info.status == "Missing"
                    ? T("player.profile.start", state)
                    : T("player.profile.play", state);
                var pick = AddButton(card, actionLabel,
                    info.status == "Missing" ? PlayerOrange : PlayerSlate,
                    () => SelectPlayerCard(state, slot),
                    "profile." + slot + ".select");
                pick.style.minHeight = 50;
                pick.style.fontSize = 20;
                ApplyAvailability(pick, info.status == "Missing" ? "profile.create" : "profile.select");
                if (info.status == "Blocked") pick.SetEnabled(false);
                cards.Add(card);
            }

            var langRow = Row(root);
            langRow.style.alignItems = Align.Center;
            langRow.style.marginTop = 4;
            var langCaption = AddLabel(langRow, T("settings.language", state), 18, PlayerMuted);
            langCaption.style.marginLeft = langCaption.style.marginRight = 8;
            langEnButton = AddButton(langRow, "English",
                state.language == "en" ? PlayerOrange : PlayerSlate,
                () => Language("en"), "settings.language.en");
            langHeButton = AddButton(langRow, "עברית",
                state.language == "he" ? PlayerOrange : PlayerSlate,
                () => Language("he"), "settings.language.he");
            langEnButton.style.minHeight = langHeButton.style.minHeight = 46;
            langEnButton.style.fontSize = langHeButton.style.fontSize = 18;
            HighlightPlayerToggle(langEnButton, state.language == "en");
            HighlightPlayerToggle(langHeButton, state.language == "he");
            ApplyAvailability(langEnButton, "ui.language");
            ApplyAvailability(langHeButton, "ui.language");

            warningLabel = AddLabel(root, "", 16, Coral);
            warningLabel.name = "status.fontWarning";
            warningLabel.style.whiteSpace = WhiteSpace.Normal;
            statusLabel = AddLabel(root, "", 16, PlayerCream);
            statusLabel.name = "status.persistence";
            statusLabel.style.whiteSpace = WhiteSpace.Normal;
        }

        private void SelectPlayerCard(FoundationState state, int slot)
        {
            var info = state.slots[slot];
            if (info.status == "Missing")
            {
                var create = new Dictionary<string, string>
                {
                    ["slot"] = slot.ToString(),
                    ["language"] = state.language
                };
                var result = app.Dispatch("profile.create", create);
                if (result.status == "applied")
                    app.Dispatch("ui.navigate", Screen("sandbox"));
            }
            else
            {
                var select = new Dictionary<string, string>
                {
                    ["slot"] = slot.ToString()
                };
                var result = app.Dispatch("profile.select", select);
                if (result.status == "applied"
                    && (info.status == "Loaded" || info.status == "RecoveredFromBackup"))
                    app.Dispatch("ui.navigate", Screen("sandbox"));
            }
        }

        private void BuildPlayerHome(FoundationState state)
        {
            var scroll = new ScrollView { name = "home.scroll" };
            scroll.style.flexGrow = 1;
            root.Add(scroll);

            var header = Row(scroll);
            header.name = "home.header";
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 10;
            header.style.paddingLeft = header.style.paddingRight = 14;
            header.style.paddingTop = header.style.paddingBottom = 10;
            header.style.backgroundColor = PlayerNavy;
            Round(header, 16);
            Border(header, PlayerSlate, 1.5f);

            var titleBlock = new VisualElement { name = "home.titleBlock" };
            titleBlock.style.alignItems = Align.FlexStart;
            var title = AddLabel(titleBlock, T("home.title", state), 36, PlayerCream);
            title.name = "home.title";
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            var subtitle = AddLabel(titleBlock, T("home.subtitle", state), 17, PlayerMuted);
            subtitle.name = "home.subtitle";
            subtitle.style.whiteSpace = WhiteSpace.Normal;
            header.Add(titleBlock);

            var activeSlot = state.activeSlot >= 0 ? state.activeSlot : 0;
            var driverCard = Row(header);
            driverCard.name = "home.driver";
            driverCard.style.alignItems = Align.Center;
            driverCard.style.backgroundColor = PlayerSlate;
            driverCard.style.paddingLeft = driverCard.style.paddingRight = 12;
            driverCard.style.paddingTop = driverCard.style.paddingBottom = 6;
            Round(driverCard, 14);
            Border(driverCard, PlayerMint, 1.5f);
            var avatar = new VisualElement { name = "home.driver.portrait" };
            avatar.style.width = avatar.style.height = 52;
            avatar.style.marginLeft = avatar.style.marginRight = 6;
            avatar.style.backgroundColor = CardColors[activeSlot % CardColors.Length];
            Round(avatar, 10);
            var portrait = playerHud != null ? playerHud.Portrait(activeSlot) : null;
            if (portrait != null)
            {
                avatar.style.backgroundImage = new StyleBackground(portrait);
                avatar.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            }
            driverCard.Add(avatar);
            var driverInfo = new VisualElement();
            var driverName = AddLabel(driverInfo,
                T(AnimalKeys[activeSlot % AnimalKeys.Length], state), 20, PlayerCream);
            driverName.name = "home.driver.name";
            driverName.style.unityFontStyleAndWeight = FontStyle.Bold;
            var coins = state.savedProfile != null ? state.savedProfile.coins : 0;
            var coinsLabel = AddLabel(driverInfo,
                T("garage.coins", state) + ": " + Ltr(coins.ToString()), 16, PlayerMint);
            coinsLabel.name = "home.coins";
            driverCard.Add(driverInfo);

            // Hero row: Quick Race as the prominent primary action + Campaign alongside it.
            var heroRow = Row(scroll);
            heroRow.name = "home.hero";
            heroRow.style.marginBottom = 8;

            var quickCard = new VisualElement { name = "home.quickCard" };
            quickCard.style.width = 520;
            quickCard.style.marginLeft = quickCard.style.marginRight = 8;
            quickCard.style.marginTop = quickCard.style.marginBottom = 6;
            quickCard.style.paddingTop = quickCard.style.paddingBottom = 12;
            quickCard.style.paddingLeft = quickCard.style.paddingRight = 14;
            quickCard.style.backgroundColor = PlayerNavy;
            Round(quickCard, 18);
            Border(quickCard, PlayerOrange, 2.5f);
            AddCoursePreviewBanner(quickCard, "coastal-village", 112);
            var quickHint = AddLabel(quickCard, T("home.primaryHint", state), 16, PlayerMuted);
            quickHint.style.whiteSpace = WhiteSpace.Normal;
            var quick = AddButton(quickCard, T("quick.title", state), PlayerOrange,
                () => app.Dispatch("quickRace.open", emptyParams), "loop.quickRace");
            quick.style.minHeight = 58;
            quick.style.fontSize = 24;
            ApplyAvailability(quick, "quickRace.open");
            heroRow.Add(quickCard);

            var campaignCard = new VisualElement { name = "home.campaignCard" };
            campaignCard.style.width = 520;
            campaignCard.style.marginLeft = campaignCard.style.marginRight = 8;
            campaignCard.style.marginTop = campaignCard.style.marginBottom = 6;
            campaignCard.style.paddingTop = campaignCard.style.paddingBottom = 12;
            campaignCard.style.paddingLeft = campaignCard.style.paddingRight = 14;
            campaignCard.style.backgroundColor = PlayerNavy;
            Round(campaignCard, 18);
            Border(campaignCard, PlayerMint, 2f);
            AddCoursePreviewBanner(campaignCard, "pine-forest", 112);
            var prog = state.progression;
            var campaignSummary = prog != null && prog.available
                ? T("campaign.stars", state) + " "
                    + Ltr(prog.campaignStars + "/" + prog.campaignRequiredStars)
                    + " · " + T("campaign.events", state) + " "
                    + Ltr(prog.campaignCompletedEvents + "/" + prog.campaignRequiredEvents)
                : T("campaign.worlds", state);
            var campaignHint = AddLabel(campaignCard, campaignSummary, 16, PlayerMint);
            campaignHint.style.whiteSpace = WhiteSpace.Normal;
            var campaign = AddButton(campaignCard, T("campaign.title", state), PlayerSlate,
                () => app.Dispatch("campaign.open", emptyParams), "loop.campaign");
            campaign.style.minHeight = 58;
            campaign.style.fontSize = 24;
            ApplyAvailability(campaign, "campaign.open");
            heroRow.Add(campaignCard);

            // Secondary modes row: Time Trial, Garage, Challenges, Practice, Local Multiplayer.
            var loop = Row(scroll);
            loop.name = "loop.entries";
            loop.style.marginBottom = 6;
            var trial = AddButton(loop, T("trial.title", state), PlayerSlate,
                () => app.Dispatch("timeTrial.open", emptyParams), "loop.timeTrial");
            var garage = AddButton(loop, T("garage.title", state), PlayerSlate,
                () => app.Dispatch("garage.open", emptyParams), "loop.garage");
            var bounties = AddButton(loop, T("bounties.title", state), PlayerSlate,
                () => app.Dispatch("bounties.open", emptyParams), "loop.bounties");
            var practice = AddButton(loop, T("practice.open", state), PlayerSlate,
                () => app.Dispatch("ui.navigate", Screen("practice")), "action.practice");
            var lan = AddButton(loop, T("lan.title", state), PlayerSlate,
                () => app.Dispatch("ui.navigate", Screen("lan-browser")), "action.lan");
            foreach (var btn in new[] { trial, garage, bounties, practice, lan })
            {
                btn.style.minWidth = 196;
                btn.style.minHeight = 54;
                btn.style.fontSize = 20;
            }
            ApplyAvailability(trial, "timeTrial.open");
            ApplyAvailability(garage, "garage.open");
            ApplyAvailability(bounties, "bounties.open");
            ApplyAvailability(practice, "race.start");
            ApplyAvailability(lan, "ui.navigate");

            // Footer utility row: Settings + Switch Driver.
            var utility = Row(scroll);
            utility.name = "home.utility";
            var settings = AddButton(utility, T("action.settings", state), PlayerNavy,
                () => app.Dispatch("ui.navigate", Screen("settings")), "action.settings");
            var back = AddButton(utility, T("player.switchDriver", state), PlayerNavy,
                () => app.Dispatch("ui.navigate", Screen("profiles")), "action.back");
            foreach (var btn in new[] { settings, back })
            {
                btn.style.minWidth = 180;
                btn.style.minHeight = 46;
                btn.style.fontSize = 18;
            }
            ApplyAvailability(settings, "ui.navigate");
            ApplyAvailability(back, "ui.navigate");

            warningLabel = AddLabel(root, "", 16, Coral);
            warningLabel.name = "status.fontWarning";
            warningLabel.style.whiteSpace = WhiteSpace.Normal;
            statusLabel = AddLabel(root, "", 16, PlayerCream);
            statusLabel.name = "status.persistence";
            statusLabel.style.whiteSpace = WhiteSpace.Normal;
        }

        private VisualElement AddCoursePreviewBanner(VisualElement parent, string courseId, int height, bool prepend = false)
        {
            var banner = new VisualElement { name = "preview." + courseId };
            banner.pickingMode = PickingMode.Ignore;
            banner.style.height = height;
            banner.style.width = new Length(100f, LengthUnit.Percent);
            banner.style.marginBottom = 8;
            banner.style.backgroundColor = PlayerInk;
            Round(banner, 10);
            var preview = playerHud != null ? playerHud.CoursePreview(courseId) : null;
            if (preview != null)
            {
                banner.style.backgroundImage = new StyleBackground(preview);
                banner.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                banner.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                banner.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                banner.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            }
            if (prepend) parent.Insert(0, banner);
            else parent.Add(banner);
            return banner;
        }

        private void StylePlayerButton(Button button, Color requestedBg)
        {
            button.focusable = true;
            var primary = ColorDistance(requestedBg, Coral) < 0.15f
                || ColorDistance(requestedBg, PlayerOrange) < 0.15f;
            var accent = ColorDistance(requestedBg, Seafoam) < 0.15f
                || ColorDistance(requestedBg, PlayerMint) < 0.15f;
            var dark = ColorDistance(requestedBg, Deep) < 0.15f
                || ColorDistance(requestedBg, PlayerNavy) < 0.15f;
            var fill = primary ? PlayerOrange : accent ? PlayerSlate : dark ? PlayerNavy : PlayerSlate;
            var border = primary ? PlayerCream : accent ? PlayerMint : new Color(0.32f, 0.48f, 0.60f, 0.85f);
            button.style.backgroundColor = fill;
            button.style.color = PlayerCream;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.paddingLeft = button.style.paddingRight = 16;
            button.style.paddingTop = button.style.paddingBottom = 8;
            button.style.whiteSpace = WhiteSpace.Normal;
            Round(button, 12);
            Border(button, border, primary ? 2f : 1.5f);
            button.RegisterCallback<FocusInEvent>(_ => Border(button, PlayerMint, 3f));
            button.RegisterCallback<FocusOutEvent>(_ => Border(button, border, primary ? 2f : 1.5f));
        }

        private void HighlightPlayerToggle(Button button, bool active)
        {
            if (button == null) return;
            button.style.backgroundColor = active ? PlayerOrange : PlayerSlate;
            Border(button, active ? PlayerMint : new Color(0.32f, 0.48f, 0.60f, 0.85f), active ? 2.5f : 1.5f);
        }

        private void StylePlayerScreen(FoundationState state)
        {
            var raceScreen = state.screen == "driving" || state.screen == "lan-race";
            root.style.backgroundColor = raceScreen ? Color.clear : PlayerInk;
            if (state.screen == "driving")
                StylePlayerDriving(state);
            else if (state.screen != "lan-race")
                StylePlayerSetup(state);
            foreach (var button in root.Query<Button>().ToList())
                button.style.opacity = button.enabledSelf ? 1f : 0.45f;
        }

        private void StylePlayerSetup(FoundationState state)
        {
            foreach (var label in root.Query<Label>().ToList())
            {
                label.style.whiteSpace = WhiteSpace.Normal;
                if (ColorDistance(label.style.color.value, Deep) < 0.12f)
                    label.style.color = PlayerCream;
                else if (ColorDistance(label.style.color.value, Ocean) < 0.12f)
                    label.style.color = PlayerMint;
            }
            if (state.screen == FoundationService.ScreenQuickRace
                || state.screen == FoundationService.ScreenTimeTrial)
            {
                var prefix = state.screen == FoundationService.ScreenQuickRace ? "quick" : "trial";
                var selectedCourse = state.screen == FoundationService.ScreenQuickRace ? quickCourseId : trialCourseId;
                var selectedClass = state.screen == FoundationService.ScreenQuickRace ? quickClassId : trialClassId;
                foreach (var button in root.Query<Button>().ToList())
                {
                    if (button.name != null && button.name.StartsWith(prefix + ".course."))
                    {
                        var courseId = button.name.Substring((prefix + ".course.").Length);
                        button.style.width = 272;
                        button.style.minHeight = 148;
                        button.style.paddingTop = 100;
                        button.style.paddingBottom = 10;
                        button.style.paddingLeft = button.style.paddingRight = 10;
                        button.style.fontSize = 20;
                        button.style.unityTextAlign = TextAnchor.MiddleCenter;
                        var banner = AddCoursePreviewBanner(button, courseId, 84, prepend: true);
                        banner.style.position = Position.Absolute;
                        banner.style.left = 8;
                        banner.style.right = 8;
                        banner.style.top = 8;
                        banner.style.width = StyleKeyword.Auto;
                        banner.style.marginBottom = 0;
                        var content = LoopContent();
                        if (content != null && courseId.EndsWith("-v1") &&
                            content.Driving.Courses.TryGetValue(courseId, out var course))
                        {
                            float length = 0;
                            var route = course.route;
                            for (int i = 1; i < route.Count; i++)
                                length += Vector2.Distance(new Vector2(route[i-1].x,route[i-1].z),new Vector2(route[i].x,route[i].z));
                            if (!course.isOpenRoute && route.Count > 1)
                                length += Vector2.Distance(new Vector2(route[0].x,route[0].z),new Vector2(route[route.Count-1].x,route[route.Count-1].z));
                            if (!course.isOpenRoute) length *= 2;
                            var detail = AddLabel(button,
                                T(course.isOpenRoute ? "route.journey" : "route.circuit",state) + " · " +
                                Ltr((length/1000f).ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+" km") + " · " + T("route.duration",state),
                                13,PlayerMint);
                            detail.style.position = Position.Absolute;
                            detail.style.bottom = 8; detail.style.left = 3; detail.style.right = 3;
                            detail.style.unityTextAlign = TextAnchor.MiddleCenter;
                            button.style.minHeight = 175; button.style.paddingBottom = 36;
                        }
                        HighlightPlayerToggle(button, courseId == selectedCourse);
                    }
                    else if (button.name != null && button.name.StartsWith(prefix + ".class."))
                    {
                        var classId = button.name.Substring((prefix + ".class.").Length);
                        button.style.minWidth = 170;
                        button.style.minHeight = 52;
                        HighlightPlayerToggle(button, classId == selectedClass);
                    }
                    else if (button.name != null && button.name.StartsWith("quick.vehicle."))
                    {
                        var vehicleId = button.name.Substring("quick.vehicle.".Length);
                        button.style.minWidth = 190;
                        button.style.minHeight = 54;
                        HighlightPlayerToggle(button, vehicleId == quickVehicleId);
                    }
                    else if (button.name != null && button.name.StartsWith("quick.difficulty."))
                    {
                        var diffId = button.name.Substring("quick.difficulty.".Length);
                        button.style.minWidth = 140;
                        button.style.minHeight = 48;
                        HighlightPlayerToggle(button, diffId == quickDifficulty);
                    }
                }
                var start = root.Q<Button>(prefix + ".start");
                if (start != null)
                {
                    start.style.minWidth = 280;
                    start.style.minHeight = 62;
                    start.style.fontSize = 26;
                    start.style.alignSelf = Align.Center;
                }
            }
            else if (state.screen == FoundationService.ScreenCampaign)
            {
                var title = root.Q<Label>("campaign.title");
                if (title != null)
                {
                    title.style.fontSize = 32;
                    title.style.marginTop = title.style.marginBottom = 0;
                }
                var stars = root.Q<Label>("campaign.stars");
                if (stars != null)
                {
                    stars.style.fontSize = 17;
                    stars.style.marginTop = 0;
                    stars.style.marginBottom = 4;
                }
                var scroll = root.Q<ScrollView>("campaign.scroll");
                var worldsStrip = root.Q("campaign.worlds");
                if (worldsStrip != null && state.progression != null
                    && state.progression.worlds != null && state.progression.worlds.Count <= 1)
                {
                    worldsStrip.style.display = DisplayStyle.None;
                    var worldsHeader = root.Q("campaign.worlds.header");
                    if (worldsHeader != null) worldsHeader.style.display = DisplayStyle.None;
                }
                if (scroll != null)
                {
                    var grid = new VisualElement { name = "campaign.grid" };
                    grid.style.flexDirection = FlexDirection.Row;
                    grid.style.flexWrap = Wrap.Wrap;
                    grid.style.justifyContent = Justify.Center;
                    var eventRows = new List<VisualElement>();
                    foreach (var child in scroll.contentContainer.Children())
                        if (child.name != null && child.name.StartsWith("campaign.row."))
                            eventRows.Add(child);
                    if (eventRows.Count > 0)
                    {
                        var insertAt = scroll.contentContainer.IndexOf(eventRows[0]);
                        scroll.contentContainer.Insert(insertAt, grid);
                        foreach (var row in eventRows)
                        {
                            row.style.width = 540;
                            row.style.justifyContent = Justify.SpaceBetween;
                            row.style.alignItems = Align.Center;
                            row.style.backgroundColor = PlayerNavy;
                            row.style.marginLeft = row.style.marginRight = 6;
                            row.style.marginTop = row.style.marginBottom = 2;
                            row.style.paddingLeft = row.style.paddingRight = 10;
                            row.style.paddingTop = row.style.paddingBottom = 2;
                            Round(row, 10);
                            grid.Add(row);
                        }
                    }
                }
                var setup = root.Q("campaign.setup");
                var back = root.Q<Button>("loop.back");
                if (setup != null)
                {
                    setup.style.flexDirection = FlexDirection.Row;
                    setup.style.flexWrap = Wrap.Wrap;
                    setup.style.justifyContent = Justify.Center;
                    setup.style.alignItems = Align.Center;
                    setup.style.backgroundColor = PlayerNavy;
                    setup.style.paddingTop = setup.style.paddingBottom = 6;
                    setup.style.paddingLeft = setup.style.paddingRight = 14;
                    setup.style.marginTop = 4;
                    setup.style.marginBottom = 0;
                    setup.style.maxWidth = 1092;
                    setup.style.alignSelf = Align.Center;
                    Round(setup, 14);
                    Border(setup, PlayerOrange, 2f);
                    if (back != null)
                    {
                        var actionsRow = back.parent;
                        setup.Add(back);
                        if (actionsRow != null && actionsRow.childCount == 0)
                            actionsRow.style.display = DisplayStyle.None;
                    }
                }
                foreach (var button in root.Query<Button>().ToList())
                {
                    if (button.name != null && button.name.StartsWith("campaign.event."))
                    {
                        var eventId = button.name.Substring("campaign.event.".Length);
                        button.style.minWidth = 380;
                        button.style.minHeight = 34;
                        button.style.fontSize = 15;
                        button.style.marginTop = button.style.marginBottom = 1;
                        HighlightPlayerToggle(button, eventId == campaignEventId);
                    }
                    else if (button.name != null && button.name.StartsWith("campaign.vehicle."))
                    {
                        var vehicleId = button.name.Substring("campaign.vehicle.".Length);
                        button.style.minHeight = 40;
                        button.style.fontSize = 16;
                        HighlightPlayerToggle(button, vehicleId == campaignVehicleId);
                    }
                    else if (button.name != null && button.name.StartsWith("campaign.difficulty."))
                    {
                        var diffId = button.name.Substring("campaign.difficulty.".Length);
                        button.style.minHeight = 40;
                        button.style.fontSize = 16;
                        HighlightPlayerToggle(button, diffId == campaignDifficulty);
                    }
                }
                var start = root.Q<Button>("campaign.start");
                if (start != null)
                {
                    start.style.minWidth = 170;
                    start.style.minHeight = 42;
                    start.style.fontSize = 19;
                    start.style.alignSelf = Align.Center;
                }
                if (back != null)
                {
                    back.style.minHeight = 42;
                    back.style.fontSize = 17;
                }
            }
        }

        private void StylePlayerGarage(FoundationState state)
        {
            if (garagePanel == null) return;
            foreach (var label in garagePanel.Query<Label>().ToList())
            {
                label.style.whiteSpace = WhiteSpace.Normal;
                if (ColorDistance(label.style.color.value, Deep) < 0.12f)
                    label.style.color = PlayerCream;
            }
            foreach (var button in garagePanel.Query<Button>().ToList())
                button.style.opacity = button.enabledSelf ? 1f : 0.45f;
        }

        private void StylePlayerDriving(FoundationState state)
        {
            var readouts = root.Q("hud.readouts");
            if (readouts != null)
            {
                HudCard(readouts);
                readouts.style.position = Position.Absolute;
                readouts.style.left = 16;
                readouts.style.top = 16;
                readouts.style.minHeight = 42;
                readouts.style.alignItems = Align.Center;
                readouts.style.justifyContent = Justify.FlexStart;
                foreach (var child in readouts.Children())
                {
                    child.style.fontSize = 17;
                    child.style.color = PlayerCream;
                    child.style.unityFontStyleAndWeight = FontStyle.Bold;
                    child.style.marginLeft = child.style.marginRight = 8;
                }
                if (positionLabel != null) positionLabel.style.color = PlayerMint;
                if (phaseLabel != null)
                {
                    phaseLabel.style.color = PlayerOrange;
                    phaseLabel.style.fontSize = 24;
                }
                if (driftLabel != null) driftLabel.style.color = PlayerMint;
                if (gateLabel != null) gateLabel.style.display = DisplayStyle.None;
            }

            var boost = root.Q("hud.boostRow");
            if (boost != null)
            {
                HudCard(boost);
                boost.style.position = Position.Absolute;
                boost.style.left = 16;
                boost.style.top = 64;
                boost.style.width = 236;
                boost.style.height = 32;
                boost.style.alignItems = Align.Center;
                boost.style.justifyContent = Justify.SpaceBetween;
                var caption = root.Q("hud.boostLabel");
                if (caption != null)
                {
                    caption.style.fontSize = 13;
                    caption.style.color = PlayerCream;
                    caption.style.unityFontStyleAndWeight = FontStyle.Bold;
                    caption.style.marginRight = 10;
                }
                var track = root.Q("hud.boostTrack");
                if (track != null)
                {
                    track.style.width = 140;
                    track.style.height = 8;
                    track.style.backgroundColor = PlayerInk;
                }
            }

            // Build the gadget card BEFORE pauseOverlay so pause and results
            // overlays always paint above every HUD widget.
            var gadgetCard = new VisualElement { name = "hud.gadget.card" };
            HudCard(gadgetCard);
            gadgetCard.style.position = Position.Absolute;
            gadgetCard.style.right = 16;
            gadgetCard.style.top = 60;
            gadgetCard.style.width = 232;
            gadgetCard.style.minHeight = 92;
            gadgetCard.style.paddingTop = gadgetCard.style.paddingBottom = 10;
            gadgetCard.style.paddingLeft = 56;
            gadgetCard.style.paddingRight = 10;
            if (gadgetLabel != null)
            {
                gadgetCard.Add(gadgetLabel);
                gadgetLabel.style.fontSize = 20;
                gadgetLabel.style.color = PlayerOrange;
                gadgetLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                gadgetLabel.style.marginLeft = gadgetLabel.style.marginRight = 0;
                gadgetLabel.style.whiteSpace = WhiteSpace.Normal;
            }
            var icon = new Label("?") { name = "hud.gadget.icon" };
            icon.style.position = Position.Absolute;
            icon.style.left = 14;
            icon.style.top = 18;
            icon.style.fontSize = 30;
            icon.style.unityFontStyleAndWeight = FontStyle.Bold;
            icon.style.color = PlayerMint;
            gadgetCard.Add(icon);
            var hint = new Label { name = "hud.gadget.hint" };
            hint.style.fontSize = 12;
            hint.style.color = PlayerMuted;
            hint.style.whiteSpace = WhiteSpace.Normal;
            gadgetCard.Add(hint);
            var pauseIdx = pauseOverlay != null ? root.IndexOf(pauseOverlay) : root.childCount;
            root.Insert(Mathf.Max(0, pauseIdx), gadgetCard);

            var pausePanel = WrapOverlayInBoundedPanel(pauseOverlay, "pause.panel", PlayerOrange);
            var finishPanel = WrapOverlayInBoundedPanel(finishOverlay, "finish.panel", PlayerMint);
            var resultsPanel = WrapOverlayInBoundedPanel(resultsOverlay, "results.panel", PlayerMint);
            var resumeBtn = root.Q<Button>("driving.resume");
            if (resumeBtn != null) StylePlayerButton(resumeBtn, PlayerOrange);
            if (pauseRetryButton != null) StylePlayerButton(pauseRetryButton, PlayerSlate);
            if (resultsContinueButton != null) StylePlayerButton(resultsContinueButton, PlayerOrange);
            if (resultsRetryButton != null) StylePlayerButton(resultsRetryButton, PlayerSlate);

            var keys = root.Q<Label>("hud.keys");
            var controls = root.Q("hud.controls");
            var pauseBtn = root.Q<Button>("driving.pause");
            var recoverBtn = root.Q<Button>("driving.recover");
            var cameraBtn = root.Q<Button>("driving.camera");
            var gadgetBtn = root.Q<Button>("driving.gadget");
            var exitBtn = root.Q<Button>("driving.exit");
            var actionsRow = recoverBtn != null ? recoverBtn.parent : null;

            if (!Application.isMobilePlatform)
            {
                if (controls != null) controls.style.display = DisplayStyle.None;
                if (pauseBtn != null)
                {
                    pauseBtn.text = "Esc · " + T("input.pause", state);
                    pauseBtn.style.position = Position.Absolute;
                    pauseBtn.style.right = 16;
                    pauseBtn.style.top = 16;
                    pauseBtn.style.minHeight = 36;
                    pauseBtn.style.height = 36;
                    pauseBtn.style.fontSize = 14;
                    pauseBtn.style.paddingLeft = pauseBtn.style.paddingRight = 12;
                    pauseBtn.style.marginTop = pauseBtn.style.marginBottom = 0;
                    root.Insert(Mathf.Max(0, root.IndexOf(pauseOverlay)), pauseBtn);
                }
                if (gadgetBtn != null)
                {
                    gadgetBtn.style.minHeight = 30;
                    gadgetBtn.style.height = 30;
                    gadgetBtn.style.fontSize = 13;
                    gadgetBtn.style.paddingLeft = gadgetBtn.style.paddingRight = 10;
                    gadgetBtn.style.marginTop = 6;
                    gadgetCard.Add(gadgetBtn);
                }
                if (pausePanel != null)
                {
                    var pauseActions = Row(pausePanel);
                    pauseActions.name = "pause.utility";
                    pauseActions.style.marginTop = 6;
                    foreach (var btn in new[] { cameraBtn, recoverBtn, exitBtn })
                    {
                        if (btn == null) continue;
                        btn.style.minHeight = 46;
                        btn.style.fontSize = 18;
                        pauseActions.Add(btn);
                    }
                    if (keys != null)
                    {
                        keys.style.backgroundColor = Color.clear;
                        keys.style.color = PlayerMuted;
                        keys.style.fontSize = 14;
                        keys.style.whiteSpace = WhiteSpace.Normal;
                        keys.style.maxWidth = 520;
                        keys.style.marginTop = 10;
                        pausePanel.Add(keys);
                    }
                }
                if (actionsRow != null) actionsRow.style.display = DisplayStyle.None;
            }

            foreach (var panel in new[] { pausePanel, finishPanel, resultsPanel })
            {
                if (panel == null) continue;
                foreach (var label in panel.Query<Label>().ToList())
                {
                    label.style.whiteSpace = WhiteSpace.Normal;
                    if (ColorDistance(label.style.color.value, Deep) < 0.12f)
                        label.style.color = PlayerCream;
                }
            }

            if (statusLabel != null)
            {
                HudCard(statusLabel);
                statusLabel.style.color = PlayerCream;
                statusLabel.style.fontSize = 13;
                statusLabel.style.position = Position.Absolute;
                statusLabel.style.bottom = 16;
                statusLabel.style.right = 16;
                statusLabel.style.maxWidth = 420;
                statusLabel.style.whiteSpace = WhiteSpace.Normal;
                statusLabel.style.display = DisplayStyle.None;
            }
            if (tutorialPanel != null) tutorialPanel.style.marginTop = 104;
        }

        private VisualElement WrapOverlayInBoundedPanel(VisualElement overlay, string panelName, Color accent)
        {
            if (overlay == null) return null;
            overlay.style.backgroundColor = PlayerScrim;
            var children = new List<VisualElement>();
            foreach (var child in overlay.Children()) children.Add(child);
            overlay.Clear();
            var panel = new VisualElement { name = panelName };
            panel.style.width = 580;
            panel.style.maxWidth = 640;
            panel.style.paddingTop = panel.style.paddingBottom = 22;
            panel.style.paddingLeft = panel.style.paddingRight = 26;
            panel.style.backgroundColor = PlayerNavy;
            panel.style.alignItems = Align.Center;
            Round(panel, 20);
            Border(panel, accent, 2.5f);
            foreach (var child in children) panel.Add(child);
            overlay.Add(panel);
            return panel;
        }

        private static void HudCard(VisualElement element)
        {
            element.style.backgroundColor = PlayerNavy;
            Round(element, 10);
            Border(element, PlayerSlate, 1.5f);
            element.style.paddingLeft = element.style.paddingRight = 10;
            element.style.paddingTop = element.style.paddingBottom = 4;
        }

        private static float ColorDistance(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);
        }

        private static void Round(VisualElement element, float radius)
        {
            element.style.borderTopLeftRadius = element.style.borderTopRightRadius =
                element.style.borderBottomLeftRadius = element.style.borderBottomRightRadius = radius;
        }

        private static void Border(VisualElement element, Color color, float width)
        {
            element.style.borderTopColor = element.style.borderBottomColor =
                element.style.borderLeftColor = element.style.borderRightColor = color;
            element.style.borderTopWidth = element.style.borderBottomWidth =
                element.style.borderLeftWidth = element.style.borderRightWidth = width;
        }
    }
}
