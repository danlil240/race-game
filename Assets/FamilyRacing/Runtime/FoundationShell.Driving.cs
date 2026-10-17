using System.Collections.Generic;
using FamilyRacing.Core.Foundation;
using UnityEngine;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    // Driving screens for the shell: the practice picker and the real race
    // HUD. The HUD is transparent over the graybox course — a top strip of
    // readouts (time, lap, next gate, speed, countdown/finish), a boost
    // charge bar, large held touch controls, and pause/finish overlays.
    // Every control carries a stable semantic name (driving.*) and routes
    // through the same input.set / camera.toggle / pause.toggle / race.*
    // commands the keyboard and agents use.
    public sealed partial class FoundationShell
    {
        // --- practice picker ---------------------------------------------

        private void BuildPractice(FoundationState state)
        {
            // The garage rows make this screen taller than the panel;
            // without a scroller Yoga compresses rows under their
            // children and the next row picks over a button's lower
            // half — scroll keeps every row at full touch height.
            var scroll = new ScrollView();
            scroll.name = "practice.scroll";
            scroll.style.flexGrow = 1;
            root.Add(scroll);
            AddLabel(scroll, T("practice.title", state), 40, Deep);
            AddLabel(scroll, T("practice.hint", state), 20, Deep);
            var picker = Row(scroll);
            var car = AddButton(picker, T("practice.car", state), Coral,
                () => StartRace("car-01"), "practice.car");
            var moto = AddButton(picker, T("practice.motorcycle", state), Seafoam,
                () => StartRace("motorcycle-01"), "practice.motorcycle");
            // Optional onboarding entry: the same tutorial.start command
            // an agent uses, one button per vehicle class.
            var tutorialRow = Row(scroll);
            AddLabel(tutorialRow, T("practice.tutorial", state), 22, Deep);
            var learnCar = AddButton(tutorialRow, T("practice.car", state), Ocean,
                () => StartTutorial("car-01"), "tutorial.car");
            var learnMoto = AddButton(tutorialRow, T("practice.motorcycle", state), Ocean,
                () => StartTutorial("motorcycle-01"), "tutorial.motorcycle");
            ApplyAvailability(learnCar, "tutorial.start");
            ApplyAvailability(learnMoto, "tutorial.start");
            // The garage (M5 Task 8): per-vehicle upgrade rows with exact
            // authored level/cost/effect, locked-tier marks and the
            // equipped-cosmetic strip. Values refresh in place through
            // UpdateIndicators — a purchase or profile swap must never
            // need a full screen rebuild to be honest.
            garagePanel = new VisualElement();
            garagePanel.name = "garage.panel";
            garagePanel.style.marginTop = 8;
            scroll.Add(garagePanel);
            // Same keyboard legend the race HUD carries, shown before the
            // child picks a vehicle so the keys are never a surprise.
            var keys = AddLabel(scroll, T("hud.keys", state), 15, Deep);
            keys.name = "practice.keys";
            keys.style.opacity = 0.75f;
            var actions = Row(scroll);
            var back = AddButton(actions, T("action.back", state), Deep,
                () => app.Dispatch("ui.navigate", Screen("sandbox")), "action.back");
            ApplyAvailability(car, "race.start");
            ApplyAvailability(moto, "race.start");
            ApplyAvailability(back, "ui.navigate");
            warningLabel = AddLabel(root, "", 18, Coral);
            warningLabel.name = "status.fontWarning";
            statusLabel = AddLabel(root, "", 20, Deep);
            statusLabel.name = "status.persistence";
        }

        // Refills the garage rows ONLY when the facts they render change:
        // a real click spans frames (PointerDown on frame N, PointerUp on
        // frame N+k) and a rebuilt button loses pointer capture, so
        // rebuild-per-refresh would make buy/equip unclickable for real
        // users. The signature mirrors LanRebuildSignature — every fact
        // a row displays (coins, unlocks, levels, owned/equipped
        // cosmetics, command availability) is part of it, so an
        // unchanged signature provably renders an identical tree and the
        // live widgets stay put. A purchase, equip, profile swap or
        // availability flip rebuilds exactly once.
        private string garageSignature = "";

        private void RefreshGarage(FoundationState state)
        {
            if (garagePanel == null) return;
            var signature = GarageSignature(state);
            if (signature == garageSignature) return;
            garageSignature = signature;
            garagePanel.Clear();
            var gameplay = app != null && app.Service != null
                ? app.Service.GameplayContent : null;
            var profile = state != null ? state.savedProfile : null;
            var title = AddLabel(garagePanel, T("garage.title", state),
                30, Deep);
            title.name = "garage.title";
            var coins = AddLabel(garagePanel,
                T("garage.coins", state) + " "
                    + (profile != null ? profile.coins : 0), 24, Coral);
            coins.name = "garage.coins";
            if (gameplay == null || gameplay.Economy == null
                || gameplay.Driving == null || profile == null)
                return;

            var vehicleIds = new List<string>(gameplay.Driving.Vehicles.Keys);
            vehicleIds.Sort(System.StringComparer.Ordinal);
            var categories = gameplay.Economy.upgradeCategories
                ?? new List<FamilyRacing.Core.Progression
                    .UpgradeCategoryDefinition>();
            foreach (var vehicleId in vehicleIds)
            {
                var unlocked = profile.unlockedVehicles != null
                    && profile.unlockedVehicles.Contains(vehicleId);
                var row = UpgradeRowFor(profile, vehicleId);
                var header = Row(garagePanel);
                header.name = "garage." + vehicleId;
                var name = AddLabel(header, VehicleName(vehicleId, state),
                    22, Deep);
                name.name = "garage." + vehicleId + ".name";
                var locked = AddLabel(header, T("garage.locked", state),
                    20, Coral);
                locked.name = "garage." + vehicleId + ".locked";
                locked.style.display = unlocked
                    ? DisplayStyle.None : DisplayStyle.Flex;
                foreach (var category in categories)
                {
                    if (category == null || category.id == null) continue;
                    var level = row != null
                        ? System.Math.Max(0, row.LevelFor(category.id))
                        : 0;
                    var catRow = Row(garagePanel);
                    catRow.name = "garage." + vehicleId + "."
                        + category.id;
                    var levelLabel = AddLabel(catRow,
                        T("garage.level", state) + " " + level, 18, Deep);
                    levelLabel.name = "garage." + vehicleId + "."
                        + category.id + ".level";
                    var maxed = level >= category.maxLevel;
                    var costLabel = AddLabel(catRow, maxed
                            ? T("garage.max", state)
                            : T("garage.cost", state) + " "
                                + category.prices[level], 18, Deep);
                    costLabel.name = "garage." + vehicleId + "."
                        + category.id + ".cost";
                    var effectLabel = AddLabel(catRow, "+"
                        + ((int)System.Math.Round(
                            category.levelMultiplierStep * 100f))
                            .ToString() + "%", 18, Seafoam);
                    effectLabel.name = "garage." + vehicleId + "."
                        + category.id + ".effect";
                    var installed = new VisualElement();
                    installed.name = "garage." + vehicleId + "."
                        + category.id + ".installed";
                    installed.style.width = 10 + level * 14;
                    installed.style.height = 14;
                    installed.style.marginLeft = 8;
                    installed.style.backgroundColor =
                        level > 0 ? Seafoam : Off;
                    catRow.Add(installed);
                    var next = level + 1;
                    var buy = AddButton(catRow, T("garage.buy", state),
                        Coral,
                        () => PurchaseUpgrade(vehicleId, category.id, next),
                        "garage." + vehicleId + "."
                            + category.id + ".buy");
                    buy.SetEnabled(unlocked && !maxed);
                    ApplyAvailability(buy, "upgrade.purchase");
                    if (!unlocked || maxed) buy.SetEnabled(false);
                }
            }

            // Owned cosmetics strip: one row per owned id with its
            // equip/unequip button — the same cosmetic.equip command
            // agents drive.
            var owned = profile.ownedCosmetics;
            if (owned == null || owned.Count == 0) return;
            var cosmeticsTitle = AddLabel(garagePanel,
                T("garage.cosmetics", state), 22, Deep);
            cosmeticsTitle.name = "garage.cosmetics";
            foreach (var cosmeticId in owned)
            {
                if (string.IsNullOrEmpty(cosmeticId)) continue;
                var equipped = profile.equippedCosmetics != null
                    && profile.equippedCosmetics.Contains(cosmeticId);
                var row2 = Row(garagePanel);
                row2.name = "garage.cosmetics." + cosmeticId;
                var label = AddLabel(row2, cosmeticId, 18, Deep);
                label.name = "garage.cosmetics." + cosmeticId + ".label";
                var id = cosmeticId;
                var toggle = AddButton(row2,
                    T(equipped ? "garage.unequip" : "garage.equip",
                        state),
                    equipped ? Deep : Ocean,
                    () => EquipCosmetic(id, !equipped),
                    "garage.cosmetics." + cosmeticId + ".equip");
                ApplyAvailability(toggle, "cosmetic.equip");
            }
        }

        // Every fact RefreshGarage renders: profile presence and coins,
        // the unlocked/locked tier marks, the per-vehicle upgrade
        // counters (level, installed marker and next-cost derivation all
        // read these), the owned+equipped cosmetics the strip binds, and
        // the two command availabilities that disable the buttons.
        private string GarageSignature(FoundationState state)
        {
            var gameplay = app != null && app.Service != null
                ? app.Service.GameplayContent : null;
            var profile = state != null ? state.savedProfile : null;
            var key = (gameplay != null && gameplay.Economy != null
                && gameplay.Driving != null ? "c" : "-")
                + (profile != null ? "p" + profile.coins : "-");
            if (profile != null)
            {
                if (profile.unlockedVehicles != null)
                {
                    var unlocked = new List<string>(
                        profile.unlockedVehicles);
                    unlocked.Sort(System.StringComparer.Ordinal);
                    foreach (var id in unlocked)
                        key += "|u:" + id;
                }
                if (profile.vehicleUpgrades != null)
                    foreach (var row in profile.vehicleUpgrades)
                        if (row != null)
                            key += "|g:" + row.vehicleId + "="
                                + row.acceleration + ","
                                + row.handling + "," + row.boost;
                if (profile.ownedCosmetics != null)
                    foreach (var id in profile.ownedCosmetics)
                        key += "|o:" + id;
                if (profile.equippedCosmetics != null)
                    foreach (var id in profile.equippedCosmetics)
                        key += "|e:" + id;
            }
            return key + "|a:" + ActionAvailability("upgrade.purchase")
                + "/" + ActionAvailability("cosmetic.equip");
        }

        // The discovery descriptor's available+reason for one action —
        // part of the garage signature so a gate flip (rewardUnresolved,
        // raceActive) rebuilds disabled buttons honestly.
        private string ActionAvailability(string action)
        {
            if (app == null || app.Service == null) return "-";
            foreach (var descriptor in app.Service.Discover())
                if (descriptor.action == action)
                    return descriptor.available + ":"
                        + (descriptor.reason ?? "");
            return "-";
        }

        private void PurchaseUpgrade(string vehicleId, string category,
            int expectedLevel)
        {
            var p = new Dictionary<string, string>();
            // Deterministic purchase identity per (vehicle, category,
            // level): a repeated press for the same level replays the
            // stored receipt idempotently instead of double-charging.
            p["purchaseId"] = "garage-" + vehicleId + "-" + category
                + "-l" + expectedLevel;
            p["vehicleId"] = vehicleId;
            p["category"] = category;
            p["expectedLevel"] = expectedLevel.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            app.Dispatch("upgrade.purchase", p);
        }

        private void EquipCosmetic(string cosmeticId, bool equipped)
        {
            var p = new Dictionary<string, string>();
            // The required slot parameter names the cosmetic's authored
            // slot prefix — the same CosmeticSlot seam the handler
            // validates, so the button can never send a mismatched or
            // missing slot.
            p["slot"] = FamilyRacing.Core.Profiles.UpgradeService
                .CosmeticSlot(cosmeticId);
            p["cosmeticId"] = cosmeticId;
            p["equipped"] = equipped ? "true" : "false";
            app.Dispatch("cosmetic.equip", p);
        }

        private static FamilyRacing.Core.Profiles.VehicleUpgrade
            UpgradeRowFor(FamilyRacing.Core.Profiles.ProfileSave profile,
                string vehicleId)
        {
            if (profile.vehicleUpgrades != null)
                foreach (var row in profile.vehicleUpgrades)
                    if (row != null && row.vehicleId == vehicleId)
                        return row;
            return null;
        }

        private void StartRace(string vehicleId)
        {
            var p = new Dictionary<string, string>();
            p["vehicleId"] = vehicleId;
            p["eventId"] = "practice-02";
            app.Dispatch("race.start", p);
        }

        private void StartTutorial(string vehicleId)
        {
            var p = new Dictionary<string, string>();
            p["vehicleId"] = vehicleId;
            app.Dispatch("tutorial.start", p);
        }

        // --- race HUD ------------------------------------------------------
        // The root is transparent (see RebuildTree): the course renders
        // through and only the controls sit on top. Readouts update in place
        // so held controls keep pointer capture.

        private void BuildDriving(FoundationState state)
        {
            var top = Row(root);
            // PCQA-011: the readouts draw dark text straight over live
            // track geometry — the shared wash keeps them readable on
            // dark ground while the world stays visible around the strip.
            top.name = "hud.readouts";
            HudStrip(top);
            elapsedLabel = AddLabel(top, "", 28, Deep);
            elapsedLabel.name = "hud.elapsed";
            lapLabel = AddLabel(top, "", 28, Deep);
            lapLabel.name = "hud.lap";
            gateLabel = AddLabel(top, "", 28, Deep);
            gateLabel.name = "hud.gate";
            speedLabel = AddLabel(top, "", 28, Deep);
            speedLabel.name = "hud.speed";
            phaseLabel = AddLabel(top, "", 32, Coral);
            phaseLabel.name = "hud.phase";
            // Live drift feedback: invisible until the motor actually
            // engages a drift, so the Space/touch input gets a visible
            // answer instead of looking like it did nothing.
            driftLabel = AddLabel(top, "", 22, Seafoam);
            driftLabel.name = "hud.drift";
            // Held-gadget readout (M5 Task 5): names the item the gadget
            // button will spend, or the empty-slot marker when a gadget
            // world exists but the slot is free. Empty text when the
            // race authors no gadget content at all.
            gadgetLabel = AddLabel(top, "", 22, Coral);
            gadgetLabel.name = "hud.gadget";

            var boostRow = Row(root);
            // Same exposure as the readout strip: the caption is dark
            // text over the course, so the row gets the shared wash.
            boostRow.name = "hud.boostRow";
            HudStrip(boostRow);
            var boostCaption = AddLabel(boostRow, T("input.boost", state), 20, Deep);
            boostCaption.name = "hud.boostLabel";
            var boostTrack = new VisualElement();
            boostTrack.name = "hud.boostTrack";
            boostTrack.style.width = 260;
            boostTrack.style.height = 26;
            boostTrack.style.backgroundColor = Off;
            boostTrack.style.borderTopLeftRadius = boostTrack.style.borderTopRightRadius =
                boostTrack.style.borderBottomLeftRadius = boostTrack.style.borderBottomRightRadius = 12;
            boostFill = new VisualElement();
            boostFill.name = "hud.boostFill";
            boostFill.style.height = new Length(100f, LengthUnit.Percent);
            boostFill.style.width = new Length(100f, LengthUnit.Percent);
            boostFill.style.backgroundColor = Seafoam;
            boostFill.style.borderTopLeftRadius = boostFill.style.borderTopRightRadius =
                boostFill.style.borderBottomLeftRadius = boostFill.style.borderBottomRightRadius = 12;
            boostTrack.Add(boostFill);
            boostRow.Add(boostTrack);

            // Tutorial cue strip: icon (animated by TutorialPresenter),
            // the spoken-prompt text, lesson progress and replay/exit —
            // shown only while a tutorial session is active, routed
            // through the same tutorial.* commands agents use.
            tutorialPanel = new VisualElement();
            tutorialPanel.name = "tutorial.panel";
            tutorialPanel.style.display = DisplayStyle.None;
            tutorialPanel.style.flexDirection = FlexDirection.Row;
            tutorialPanel.style.justifyContent = Justify.Center;
            tutorialPanel.style.alignItems = Align.Center;
            tutorialPanel.style.backgroundColor = PanelWash;
            tutorialPanel.style.borderTopLeftRadius = tutorialPanel.style.borderTopRightRadius =
                tutorialPanel.style.borderBottomLeftRadius = tutorialPanel.style.borderBottomRightRadius = 14;
            tutorialPanel.style.marginTop = 6;
            tutorialPanel.style.paddingTop = tutorialPanel.style.paddingBottom = 4;
            tutorialIconLabel = AddLabel(tutorialPanel, "", 44, Coral);
            tutorialIconLabel.name = "tutorial.icon";
            tutorialCueLabel = AddLabel(tutorialPanel, "", 26, Deep);
            tutorialCueLabel.name = "tutorial.cue";
            tutorialProgressLabel = AddLabel(tutorialPanel, "", 20, Ocean);
            tutorialProgressLabel.name = "tutorial.progress";
            var replay = AddButton(tutorialPanel, T("tutorial.replay", state), Ocean,
                () => app.Dispatch("tutorial.replay", emptyParams), "tutorial.replay");
            var tutorialExit = AddButton(tutorialPanel, T("tutorial.exit", state), Deep,
                () => app.Dispatch("tutorial.exit", emptyParams), "tutorial.exit");
            ApplyAvailability(replay, "tutorial.replay");
            ApplyAvailability(tutorialExit, "tutorial.exit");
            root.Add(tutorialPanel);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1f;
            root.Add(spacer);

            // Steering keeps its physical meaning in both locales: the row
            // is locked LTR so the left steer button stays on the left of
            // the screen and the right one on the right under Hebrew RTL.
            var controls = Row(root);
            controls.name = "hud.controls";
            controls.languageDirection = LanguageDirection.LTR;
            AddHoldButton(controls, "◀", Ocean,
                held => app.Input.TouchLeft(held), "driving.steerLeft");
            AddHoldButton(controls, "▶", Ocean,
                held => app.Input.TouchRight(held), "driving.steerRight");
            AddHoldButton(controls, T("input.brake", state), Deep,
                held => app.Input.TouchBrake(held), "driving.brake");
            AddHoldButton(controls, T("input.drift", state), Seafoam,
                held => app.Input.TouchDrift(held), "driving.drift");
            AddHoldButton(controls, T("input.boost", state), Coral,
                held => app.Input.TouchBoost(held), "driving.boost");
            foreach (var held in controls.Children()) ApplyAvailability(held, "input.set");

            // Keyboard legend next to the touch controls — small, so it
            // helps without competing with the big child-facing buttons.
            var keys = AddLabel(root, T("hud.keys", state), 15, Deep);
            keys.name = "hud.keys";
            keys.style.opacity = 0.75f;
            HudStrip(keys);

            var actions = Row(root);
            var recover = AddButton(actions, T("input.recovery", state), Deep,
                () => app.Input.TouchRecover(), "driving.recover");
            var camera = AddButton(actions, T("settings.camera", state), Ocean,
                () => app.Dispatch("camera.toggle", emptyParams), "driving.camera");
            // Gadgets are live (M5 Task 5): the button fires the held
            // item through the same edge-latched input.set path the
            // keyboard E key uses — the slot label names it.
            var gadget = AddButton(actions, T("input.gadget", state), Seafoam,
                () => app.Input.TouchGadget(), "driving.gadget");
            var pause = AddButton(actions, T("input.pause", state), Coral,
                () => app.Dispatch("pause.toggle", emptyParams), "driving.pause");
            var exit = AddButton(actions, T("driving.exit", state), Deep,
                () => app.Dispatch("race.exit", emptyParams), "driving.exit");
            ApplyAvailability(recover, "input.set");
            ApplyAvailability(camera, "camera.toggle");
            ApplyAvailability(gadget, "input.set");
            ApplyAvailability(pause, "pause.toggle");
            ApplyAvailability(exit, "race.exit");

            // Pause and finish overlays cover the screen when shown; both
            // keep the world dimly visible and offer their own actions.
            pauseOverlay = Overlay("overlay.pause");
            AddLabel(pauseOverlay, T("hud.paused", state), 44, Deep);
            var pauseRow = Row(pauseOverlay);
            var resume = AddButton(pauseRow, T("hud.resume", state), Seafoam,
                () => app.Dispatch("pause.toggle", emptyParams), "driving.resume");
            pauseRetryButton = AddButton(pauseRow, T("hud.retry", state),
                Coral,
                () => app.Dispatch("race.restart", emptyParams),
                "driving.pauseRetry");
            ApplyAvailability(resume, "pause.toggle");
            ApplyAvailability(pauseRetryButton, "race.restart");

            finishOverlay = Overlay("overlay.finish");
            var finished = AddLabel(finishOverlay, T("hud.finished", state), 44, Coral);
            finished.name = "hud.finishTitle";
            finishTimeLabel = AddLabel(finishOverlay, "", 30, Deep);
            finishTimeLabel.name = "hud.finishTime";
            // The plan's finish scope: elapsed time AND the vehicle that ran,
            // named in the player's language.
            finishVehicleLabel = AddLabel(finishOverlay, "", 26, Deep);
            finishVehicleLabel.name = "hud.finishVehicle";
            var finishRow = Row(finishOverlay);
            finishRetryButton = AddButton(finishRow, T("hud.retry", state),
                Seafoam,
                () => app.Dispatch("race.restart", emptyParams),
                "driving.finishRetry");
            var finishExit = AddButton(finishRow, T("driving.exit", state), Deep,
                () => app.Dispatch("race.exit", emptyParams), "driving.finishExit");
            ApplyAvailability(finishRetryButton, "race.restart");
            ApplyAvailability(finishExit, "race.exit");

            // M5 Task 10: the local-mode results surface — placement,
            // honest coin breakdown, retry-save and one-action
            // replay/continue — replaces the practice finish overlay for
            // campaign/quickRace/timeTrial sessions.
            BuildResultsOverlay(state);

            statusLabel = AddLabel(root, "", 18, Deep);
            statusLabel.name = "status.persistence";
        }

        // One shared backdrop for HUD strips that float text over the live
        // course (PCQA-011): the same semi-opaque cream + 14-radius the
        // tutorial cue panel and the overlays already paint, so dark
        // readouts keep their contrast over dark track geometry.
        private void HudStrip(VisualElement element)
        {
            element.style.backgroundColor = PanelWash;
            element.style.borderTopLeftRadius = element.style.borderTopRightRadius =
                element.style.borderBottomLeftRadius = element.style.borderBottomRightRadius = 14;
            element.style.paddingTop = element.style.paddingBottom = 2;
            element.style.paddingLeft = element.style.paddingRight = 10;
        }

        // Full-screen semi-opaque overlay, hidden until shown by the
        // indicator update; children (labels/buttons) still receive input.
        private VisualElement Overlay(string name)
        {
            var overlay = new VisualElement();
            overlay.name = name;
            overlay.style.position = Position.Absolute;
            overlay.style.left = overlay.style.right = 0;
            overlay.style.top = overlay.style.bottom = 0;
            overlay.style.backgroundColor = PanelWash;
            overlay.style.alignItems = Align.Center;
            overlay.style.justifyContent = Justify.Center;
            overlay.style.display = DisplayStyle.None;
            root.Add(overlay);
            return overlay;
        }

        private void UpdateDrivingIndicators(FoundationState state)
        {
            if (state.screen != "driving") return;
            var session = PresentedSession(state);
            if (session == null) return;
            if (elapsedLabel != null)
                elapsedLabel.text = T("hud.time", state) + " " +
                    FormatRaceTime(session.elapsedTicks);
            if (lapLabel != null)
            {
                // PCQA-010: the tutorial free-drive event authors 99 laps
                // so lessons are never lap-limited — rendering "Lap 1/99"
                // reads to a child like a 99-lap requirement. Hide the
                // counter while lessons run (the tutorial strip already
                // shows "lesson x/7") and restore the real readout for
                // the trial phase and every non-tutorial race.
                var lessonPhase = session.tutorial != null
                    && session.tutorial.phase
                        == FamilyRacing.Core.Tutorial.DrivingTutorial.PhaseLessons;
                lapLabel.style.display = lessonPhase
                    ? DisplayStyle.None : DisplayStyle.Flex;
                if (!lessonPhase)
                    lapLabel.text = T("hud.lap", state) + " " +
                        Mathf.Min(session.lap + 1, session.lapsRequired) + "/" +
                        session.lapsRequired;
            }
            if (gateLabel != null)
            {
                // nextCheckpoint is the expected gate index; gate 0 is the
                // start/finish line, so the readable gate number for the
                // child counts only the non-start gates ahead.
                var total = session.checkpoints.Count;
                var nonStart = Mathf.Max(total - 1, 1);
                var next = session.nextCheckpoint == 0
                    ? nonStart
                    : Mathf.Min(session.nextCheckpoint, nonStart);
                gateLabel.text = session.finished || total == 0
                    ? ""
                    : T("hud.gate", state) + " " + next + "/" + nonStart;
            }
            if (boostFill != null && session.motor != null)
            {
                var charge = Mathf.Clamp01(session.motor.boostCharge);
                boostFill.style.width = new Length(charge * 100f, LengthUnit.Percent);
                boostFill.style.backgroundColor =
                    session.motor.boostActive ? Coral : Seafoam;
            }
            if (phaseLabel != null)
            {
                phaseLabel.text =
                    session.phase == "countdown" && session.countdownTicksRemaining > 0
                        ? ((session.countdownTicksRemaining + 29) / 30).ToString()
                        : "";
            }
            if (speedLabel != null && session.motor != null)
                speedLabel.text = T("hud.speed", state) + " " +
                    ((int)(session.motor.speed * 3.6f)).ToString();
            if (driftLabel != null)
                driftLabel.text = session.motor != null && session.motor.drifting
                    ? T("input.drift", state) + "!" : "";
            if (gadgetLabel != null)
                gadgetLabel.text = GadgetSlotText(state);
            if (tutorialPanel != null)
            {
                var tutorial = session.tutorial;
                tutorialPanel.style.display = tutorial != null
                    ? DisplayStyle.Flex : DisplayStyle.None;
                if (tutorial != null)
                {
                    if (tutorialIconLabel != null)
                        tutorialIconLabel.text = tutorial.icon;
                    if (tutorialCueLabel != null)
                        tutorialCueLabel.text = T(tutorial.objective, state);
                    if (tutorialProgressLabel != null)
                        tutorialProgressLabel.text = tutorial.phase == "lessons"
                            ? T("tutorial.lesson", state) + " " +
                                (tutorial.lessonIndex + 1) + "/" + tutorial.lessonTotal
                            : "";
                }
            }
            if (finishOverlay != null)
            {
                // The practice finish card belongs to the legacy driving
                // session only — a solo-mode finish shows the results
                // overlay instead, so the two never stack.
                var done = state.driving != null
                    && session.phase == "finished";
                finishOverlay.style.display = done ? DisplayStyle.Flex : DisplayStyle.None;
                if (done && finishTimeLabel != null)
                    finishTimeLabel.text = T("hud.time", state) + " " +
                        FormatRaceTime(session.finishTicks > 0
                            ? session.finishTicks
                            : session.elapsedTicks);
                if (done && finishVehicleLabel != null)
                    finishVehicleLabel.text = VehicleName(session.vehicleId, state);
            }
        }

        // The per-racer session the HUD reads: the legacy practice
        // `driving` slice, the time trial's lone session, or the local
        // seat's session inside the four-racer coordinator — whatever the
        // published state actually carries, never a synthesized one.
        private static FamilyRacing.Core.Racing.DrivingSessionState
            PresentedSession(FoundationState state)
        {
            if (state == null) return null;
            if (state.driving != null) return state.driving;
            var solo = state.soloRace;
            if (solo == null) return null;
            if (solo.trial != null) return solo.trial;
            if (solo.race != null && solo.race.racers != null)
                foreach (var racer in solo.race.racers)
                    if (racer != null
                        && racer.participantId == solo.participantId)
                        return racer.session;
            return null;
        }

        // The held-gadget readout: resolves the local racer's row in
        // whichever authoritative world this screen presents — the
        // session-owned solo world (practice/tutorial/time trial), the
        // local-mode coordinator's shared world (campaign/quickRace) or
        // the host-published LAN world. "" when no gadget world exists;
        // the localized empty marker when the slot is free.
        private string GadgetSlotText(FoundationState state)
        {
            var world = GadgetWorldFor(state);
            if (world == null) return "";
            var row = LocalGadgetRow(state, world);
            if (row == null) return T("hud.gadget.empty", state);
            if (string.IsNullOrEmpty(row.heldItemId))
                return T("hud.gadget.empty", state);
            var key = "gadget." + (row.heldKind ?? "");
            try { return T(key, state); }
            catch (KeyNotFoundException) { return row.heldItemId; }
        }

        // The live authoritative gadget world for the presented race —
        // exactly the slices the state publishes; never a synthesized one.
        private static FamilyRacing.Core.Racing.GadgetWorldState
            GadgetWorldFor(FoundationState state)
        {
            if (state == null) return null;
            if (state.driving != null && state.driving.gadgets != null)
                return state.driving.gadgets;
            var solo = state.soloRace;
            if (solo != null)
            {
                if (solo.race != null && solo.race.gadgets != null)
                    return solo.race.gadgets;
                if (solo.trial != null && solo.trial.gadgets != null)
                    return solo.trial.gadgets;
            }
            var race = state.networkRace;
            return race != null ? race.gadgets : null;
        }

        private static FamilyRacing.Core.Racing.GadgetRacerState
            LocalGadgetRow(FoundationState state,
                FamilyRacing.Core.Racing.GadgetWorldState world)
        {
            if (world == null || world.racers == null) return null;
            var localId = state != null && state.soloRace != null
                && !string.IsNullOrEmpty(state.soloRace.participantId)
                ? state.soloRace.participantId
                : state != null && state.network != null
                    ? state.network.localParticipantId : null;
            if (!string.IsNullOrEmpty(localId))
            {
                var found = world.Racer(localId);
                if (found != null) return found;
            }
            // Session-owned worlds carry exactly one row (solo seat);
            // fall back to it when no participant id resolves.
            return world.racers.Count == 1 ? world.racers[0] : null;
        }

        // Friendly vehicle name for the finish overlay: localized via
        // vehicle.* keys for catalog ids, falling back to the raw id so an
        // unknown vehicle still displays honestly (and never throws on an
        // unkeyed string).
        private string VehicleName(string vehicleId, FoundationState state)
        {
            return vehicleId == "car-01" || vehicleId == "motorcycle-01"
                ? T("vehicle." + vehicleId, state)
                : (vehicleId ?? "");
        }

        private static string FormatRaceTime(long ticks)
        {
            // Simulation ticks at 30 Hz; race time is displayed as m:ss.d
            // with invariant digits, prefixed by a left-to-right mark so the
            // timer stays LTR inside the Hebrew RTL shell.
            var deciseconds = ticks * 10 / 30;
            var minutes = deciseconds / 600;
            var seconds = deciseconds % 600 / 10;
            var tenth = deciseconds % 10;
            return "\u200E" + minutes.ToString() + ":"
                + seconds.ToString("00") + "." + tenth.ToString();
        }
    }
}
