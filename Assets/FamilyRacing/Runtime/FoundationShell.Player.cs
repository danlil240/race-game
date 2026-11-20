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
            BuildKidProfiles(state);
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
            BuildKidHome(state);
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
            button.style.backgroundColor = primary ? KidOrange : KidSurface;
            button.style.color = KidText;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.paddingLeft = button.style.paddingRight = 20;
            button.style.paddingTop = button.style.paddingBottom = 8;
            button.style.whiteSpace = WhiteSpace.Normal;
            Round(button, 22);
            NoBorder(button);
            AddPressFeedback(button);
        }

        private void HighlightPlayerToggle(Button button, bool active)
        {
            if (button == null) return;
            button.style.backgroundColor = active ? KidOrange : KidSurface;
            NoBorder(button);
        }

        private void StylePlayerScreen(FoundationState state)
        {
            var raceScreen = state.screen == "driving" || state.screen == "lan-race";
            root.style.backgroundColor = raceScreen ? Color.clear : KidBg;
            if (state.screen == "driving")
            {
                StylePlayerDriving(state);
                StyleKidOverlayButtons(state);
            }
            else if (state.screen != "lan-race")
            {
                StylePlayerSetup(state);
                StyleKidScreen(state);
            }
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
            StyleKidGarage(state);
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
            var icon = root.Q<Label>("hud.gadget.icon") ?? new Label("?") { name = "hud.gadget.icon" };
            icon.style.position = Position.Absolute;
            icon.style.left = 14;
            icon.style.top = 18;
            icon.style.width = 40;
            icon.style.height = 40;
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

            if (!TouchRaceLayout)
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

            if (TouchRaceLayout)
            {
                // The deck owns the localized action buttons. Keep the
                // gadget readout below the compact top utility dock.
                gadgetCard.style.top = 104;
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
            panel.style.backgroundColor = KidBg;
            panel.style.alignItems = Align.Center;
            Round(panel, 34);
            NoBorder(panel);
            foreach (var child in children) panel.Add(child);
            overlay.Add(panel);
            return panel;
        }

        private static void HudCard(VisualElement element)
        {
            element.style.backgroundColor = PlayerNavy;
            Round(element, 14);
            NoBorder(element);
            element.style.paddingLeft = element.style.paddingRight = 12;
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
