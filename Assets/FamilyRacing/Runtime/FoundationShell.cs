using System;
using System.Collections.Generic;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    // Bilingual UI Toolkit shell: profile cards, settings, and the input
    // sandbox. The full visual tree rebuilds only when the screen, locale,
    // active selection or slot status changes; everything else (input
    // indicators, statuses, highlights) updates in place so held touch
    // buttons keep pointer capture.
    public sealed partial class FoundationShell : MonoBehaviour
    {
        private static readonly Color Sand = new Color(0.96f, 0.90f, 0.78f);
        private static readonly Color Coral = new Color(0.91f, 0.51f, 0.42f);
        private static readonly Color Seafoam = new Color(0.50f, 0.78f, 0.70f);
        private static readonly Color Ocean = new Color(0.24f, 0.49f, 0.63f);
        private static readonly Color Deep = new Color(0.16f, 0.24f, 0.30f);
        private static readonly Color Cream = new Color(0.99f, 0.97f, 0.92f);
        // Semi-opaque cream wash: the one backdrop every floating panel or
        // HUD strip over the live course shares (tutorial cue panel,
        // pause/finish overlays, the HUD readout strips — PCQA-011).
        private static readonly Color PanelWash = new Color(0.99f, 0.97f, 0.92f, 0.85f);
        private static readonly Color Off = new Color(0.75f, 0.72f, 0.66f);
        private static readonly Color[] CardColors = { Coral, Seafoam, Ocean, new Color(0.80f, 0.66f, 0.45f) };
        private static readonly string[] AnimalKeys =
            { "profile.animal.fox", "profile.animal.rabbit", "profile.animal.panda", "profile.animal.raccoon" };

        private FoundationApp app;
        private UIDocument document;
        private VisualElement root;
        private AssetTrialHud playerHud;
        private bool playerPresentation;
        private string rebuildKey = "";
        private bool suppressSliderEvents;
        private Label statusLabel;
        private Label warningLabel;
        private Label steeringLabel;
        private readonly Dictionary<string, VisualElement> leds =
            new Dictionary<string, VisualElement>();
        private VisualElement pauseOverlay;
        private VisualElement finishOverlay;
        // The driving-screen restart affordances re-apply availability
        // when the unresolved-reward gate flips — race.restart joins the
        // results.* buttons in UpdateResultsOverlay's gate-key refresh.
        private Button pauseRetryButton;
        private Button finishRetryButton;
        private Label elapsedLabel;
        private Label positionLabel;
        private Label phaseLabel;
        private Label speedLabel;
        private Label driftLabel;
        private Label gadgetLabel;
        private Label lapLabel;
        private Label gateLabel;
        private Label finishTimeLabel;
        private Label finishVehicleLabel;
        private VisualElement boostFill;
        private VisualElement tutorialPanel;
        private VisualElement garagePanel;
        private Label tutorialIconLabel;
        private Label tutorialCueLabel;
        private Label tutorialProgressLabel;
        private Button saveButton;
        private readonly Dictionary<string, string> emptyParams = new Dictionary<string, string>(0);
        private readonly Dictionary<string, string> settingsParams = new Dictionary<string, string>();
        private Slider musicSlider, effectsSlider, voiceSlider;
        private Button chaseButton, overheadButton, shakeButton, langEnButton, langHeButton;

        public void Bind(FoundationApp owner)
        {
            app = owner;
            document = GetComponent<UIDocument>();
            root = document.rootVisualElement;
            playerHud = GetComponent<AssetTrialHud>();
            playerPresentation = playerHud != null;
            ApplyTextConfiguration(root);
            rebuildKey = "";
        }

        // The tutorial cue icon the presenter animates; null off the
        // driving screen or before the first rebuild.
        public VisualElement TutorialCueIcon
        {
            get { return tutorialIconLabel; }
        }

        // Applied only on bind/rebind so a deliberate mutation of the root's
        // text generator or font is surfaced by readiness, not corrected
        // silently on the next refresh.
        private void ApplyTextConfiguration(VisualElement element)
        {
            element.style.flexGrow = 1f;
            var settings = document != null && document.panelSettings != null
                ? document.panelSettings.textSettings : null;
            if (settings == null || settings.defaultFontAsset == null) return;
            FoundationFonts.Warmup(document.panelSettings);
            element.style.unityTextGenerator = TextGeneratorType.Advanced;
            element.style.unityFontDefinition =
                FontDefinition.FromSDFFont(settings.defaultFontAsset);
        }

        public void Refresh(FoundationState state)
        {
            // The document recreates its root when re-enabled or when its
            // panel detaches; a cached root would paint into nowhere.
            if (root == null || root.panel == null)
            {
                var live = document != null ? document.rootVisualElement : null;
                if (live == null) return;
                root = live;
                ApplyTextConfiguration(root);
                rebuildKey = "";
            }
            if (state == null) return;
            if (playerPresentation) EnsurePlayerSetupDefaults(state);
            root.languageDirection = state.language == "he"
                ? LanguageDirection.RTL : LanguageDirection.LTR;
            var key = state.screen + "|" + state.language + "|" + state.activeSlot;
            foreach (var slot in state.slots) key += "|" + slot.status;
            // LAN screens rebuild on roster/discovery/config/delivery
            // changes as well — the signature is cheap and covers only the
            // structure, never per-tick race readouts.
            if (IsLanScreen(state.screen) && state.network != null)
                key += "|" + LanRebuildSignature(state);
            // Loop screens repaint on picker/progression changes too —
            // identical signatures keep live widgets (and their pointer
            // captures) exactly like the LAN signature does.
            if (IsLoopScreen(state.screen))
                key += "|" + LoopSignature(state);
            // The profiles disclaimer flips on the published catalog
            // verdict, which the base key does not cover — the same
            // cheap-signature rule the LAN screens follow.
            if (state.screen == "profiles")
                key += "|" + (LoopContentAvailable()
                    ? "loop:yes" : "loop:no");
            if (key != rebuildKey)
            {
                rebuildKey = key;
                RebuildTree(state);
            }
            UpdateIndicators(state);
        }

        private string T(string key, FoundationState state) => ShellText.Get(key, state.language);

        private void RebuildTree(FoundationState state)
        {
            root.Clear();
            leds.Clear();
            statusLabel = warningLabel = steeringLabel = null;
            pauseOverlay = finishOverlay = boostFill = null;
            pauseRetryButton = finishRetryButton = null;
            elapsedLabel = positionLabel = phaseLabel = speedLabel = null;
            driftLabel = null;
            gadgetLabel = null;
            lapLabel = gateLabel = finishTimeLabel = null;
            finishVehicleLabel = null;
            tutorialPanel = null;
            garagePanel = null;
            garageSignature = "";
            resultsOverlay = null;
            resultsPlaceLabel = resultsTimeLabel = resultsCoinsLabel = null;
            resultsBreakdownLabel = resultsRecordLabel = null;
            resultsRetrySaveButton = null;
            resultsRetryButton = resultsContinueButton = null;
            resultsAvailabilityKey = "?";
            tutorialIconLabel = tutorialCueLabel = tutorialProgressLabel = null;
            lanTimeLabel = lanStandingsLabel = lanPhaseLabel = null;
            lanActionLabel = lanPauseLabel = null;
            lanAddressField = lanPortField = null;
            lanJoinErrorLabel = null;
            // Manual-join feedback belongs to the browser: leaving the
            // screen retires it so a later visit never resurrects a
            // stale error line.
            if (state.screen != "lan-browser")
                lanJoinErrorKey = lanJoinErrorCode = lanJoinErrorField = "";
            saveButton = null;
            musicSlider = effectsSlider = voiceSlider = null;
            chaseButton = overheadButton = shakeButton = langEnButton = langHeButton = null;
            // Driving and the LAN race need a transparent overlay: the
            // graybox world renders through and only the controls sit on
            // top.
            root.style.backgroundColor =
                state.screen == "driving" || state.screen == "lan-race"
                    ? new Color(0, 0, 0, 0) : Sand;
            root.style.paddingTop = root.style.paddingBottom = 16;
            root.style.paddingLeft = root.style.paddingRight = 16;
            // The screen the tree last rendered owns Settings' return
            // (PCQA-004): profile creation builds settings over
            // profiles; the sandbox action builds it over the sandbox.
            if (IsSettingsOriginScreen(state.screen))
                settingsReturnScreen = state.screen;
            switch (state.screen)
            {
                case "settings": BuildSettings(state); break;
                case "sandbox":
                    if (playerPresentation) BuildPlayerHome(state);
                    else BuildSandbox(state);
                    break;
                case "practice": BuildPractice(state); break;
                case "driving": BuildDriving(state); break;
                case "lan-browser":
                case "lan-lobby":
                case "lan-race":
                case "lan-results":
                case "lan-recovery": BuildLanScreen(state); break;
                // M5 Task 10 loop screens — reachable only through the
                // gated *.open commands, rendered from the published
                // progression slice.
                case FoundationService.ScreenCampaign: BuildCampaign(state); break;
                case FoundationService.ScreenQuickRace: BuildQuickRace(state); break;
                case FoundationService.ScreenTimeTrial: BuildTimeTrial(state); break;
                case FoundationService.ScreenGarage: BuildGarageScreen(state); break;
                case FoundationService.ScreenBounties: BuildBounties(state); break;
                default:
                    if (playerPresentation) BuildPlayerProfiles(state);
                    else BuildProfiles(state);
                    break;
            }
            if (playerPresentation) StylePlayerScreen(state);
        }

        private Label AddLabel(VisualElement parent, string text, int size, Color color)
        {
            var label = new Label(text);
            label.style.fontSize = size;
            label.style.color = color;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            parent.Add(label);
            return label;
        }

        private Button AddButton(VisualElement parent, string text, Color bg, Action onClick,
            string name = null)
        {
            var button = new Button(onClick) { text = text };
            if (name != null) button.name = name;
            button.style.minHeight = 72;
            button.style.fontSize = 26;
            button.style.color = Color.white;
            button.style.backgroundColor = bg;
            button.style.borderTopLeftRadius = button.style.borderTopRightRadius =
                button.style.borderBottomLeftRadius = button.style.borderBottomRightRadius = 14;
            button.style.marginTop = button.style.marginBottom = 6;
            button.style.marginLeft = button.style.marginRight = 6;
            parent.Add(button);
            if (playerPresentation) StylePlayerButton(button, bg);
            return button;
        }

        // Held touch controls capture the pressing pointer explicitly and
        // track pointer IDs: a hold ends on pointer up, cancel, capture loss
        // or element detach (e.g. a locale rebuild), never only on up.
        // The pointer callbacks register in the TrickleDown phase: Button's
        // built-in Clickable consumes PointerDown/Up/Cancel in the bubble
        // phase (StopImmediatePropagation on down), and trickle-down
        // handlers run before bubble handlers even on the same element —
        // without it a primary/touch down never reaches the hold logic.
        private void AddHoldButton(VisualElement parent, string text, Color bg,
            Action<bool> onHold, string name = null)
        {
            var button = new Button { text = text };
            if (name != null) button.name = name;
            button.style.minHeight = 96;
            button.style.minWidth = 140;
            button.style.fontSize = 30;
            button.style.color = Color.white;
            button.style.backgroundColor = bg;
            button.style.borderTopLeftRadius = button.style.borderTopRightRadius =
                button.style.borderBottomLeftRadius = button.style.borderBottomRightRadius = 16;
            button.style.marginTop = button.style.marginBottom = 6;
            button.style.marginLeft = button.style.marginRight = 6;
            var heldPointers = new HashSet<int>();
            Action<int> release = pointerId =>
            {
                if (!heldPointers.Remove(pointerId)) return;
                if (heldPointers.Count == 0) onHold(false);
            };
            button.RegisterCallback<PointerDownEvent>(evt =>
            {
                heldPointers.Add(evt.pointerId);
                button.CapturePointer(evt.pointerId);
                onHold(true);
            }, TrickleDown.TrickleDown);
            button.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (heldPointers.Contains(evt.pointerId))
                    button.ReleasePointer(evt.pointerId);
                release(evt.pointerId);
            }, TrickleDown.TrickleDown);
            button.RegisterCallback<PointerCancelEvent>(evt => release(evt.pointerId),
                TrickleDown.TrickleDown);
            button.RegisterCallback<PointerCaptureOutEvent>(evt => release(evt.pointerId),
                TrickleDown.TrickleDown);
            // DetachFromPanelEvent has no trickle-down phase; the default
            // (bubble) registration still fires at the target.
            button.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                if (heldPointers.Count > 0)
                {
                    heldPointers.Clear();
                    onHold(false);
                }
            });
            parent.Add(button);
        }

        // Command-driven controls reflect discovery availability: an action
        // the service reports unavailable shows disabled with its reason.
        private void ApplyAvailability(VisualElement element, string action)
        {
            if (element == null || app == null || app.Service == null) return;
            foreach (var descriptor in app.Service.Discover())
            {
                if (descriptor.action != action) continue;
                element.SetEnabled(descriptor.available);
                if (playerPresentation)
                    element.style.opacity = descriptor.available ? 1f : 0.45f;
                if (!string.IsNullOrEmpty(descriptor.reason))
                    element.tooltip = descriptor.reason;
                return;
            }
        }

        private VisualElement Row(VisualElement parent)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.Center;
            row.style.flexWrap = Wrap.Wrap;
            parent.Add(row);
            return row;
        }

        private Dictionary<string, string> Screen(string target)
        {
            var p = new Dictionary<string, string>();
            p["screen"] = target;
            return p;
        }

        private static string Format(float value)
        {
            return value.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        // --- in-place indicator updates -------------------------------------

        private void UpdateIndicators(FoundationState state)
        {
            if (warningLabel != null && state.presentation != null)
            {
                warningLabel.text = state.presentation.fontWarning;
                if (playerPresentation)
                    warningLabel.style.display = !string.IsNullOrEmpty(warningLabel.text)
                        ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (statusLabel != null)
            {
                var slot = state.activeSlot >= 0 && state.activeSlot < state.slots.Count
                    ? state.slots[state.activeSlot] : null;
                if (slot != null && slot.status == "Blocked") statusLabel.text = T("status.blocked", state);
                else if (slot != null && slot.status == "RecoveredFromBackup")
                    statusLabel.text = T("status.recovered", state);
                else if (state.persistenceStatus == "saveFailed")
                    statusLabel.text = T("status.saveFailed", state);
                else if (state.persistenceStatus == "saved"
                    && (!playerPresentation || state.screen == "settings"))
                    statusLabel.text = T("status.saved", state);
                else if (statusLabel.text.Length > 0) statusLabel.text = "";
                if (playerPresentation)
                    statusLabel.style.display = statusLabel.text.Length > 0
                        ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (state.screen == "sandbox" && state.input != null)
            {
                if (steeringLabel != null)
                    steeringLabel.text = T("input.steering", state) + ": " +
                        state.input.steering.ToString("0.00");
                SetLed("drift", state.input.drift);
                SetLed("boost", state.input.boost);
                SetLed("brake", state.input.brake > 0f);
                SetLed("gadget", state.input.gadget);
                SetLed("recover", state.input.recover);
            }
            UpdateDrivingIndicators(state);
            if (garagePanel != null && (state.screen == "practice"
                || state.screen == FoundationService.ScreenGarage))
                RefreshGarage(state);
            UpdateResultsOverlay(state);
            if (state.screen == "lan-race")
                UpdateLanRaceIndicators(state);
            if (pauseOverlay != null)
                pauseOverlay.style.display = state.paused ? DisplayStyle.Flex : DisplayStyle.None;
            if (state.screen == "settings" && state.draftProfile != null)
            {
                Highlight(langEnButton, state.draftProfile.language == "en");
                Highlight(langHeButton, state.draftProfile.language == "he");
                Highlight(chaseButton, state.draftProfile.camera == "chase");
                Highlight(overheadButton, state.draftProfile.camera == "overhead");
                Highlight(shakeButton, state.draftProfile.reducedCameraShake);
                suppressSliderEvents = true;
                if (musicSlider != null) musicSlider.value = state.draftProfile.musicVolume;
                if (effectsSlider != null) effectsSlider.value = state.draftProfile.effectsVolume;
                if (voiceSlider != null) voiceSlider.value = state.draftProfile.voiceVolume;
                suppressSliderEvents = false;
                if (saveButton != null)
                {
                    saveButton.SetEnabled(state.pendingSettings);
                    if (playerPresentation)
                        saveButton.style.opacity = state.pendingSettings ? 1f : 0.45f;
                }
            }
        }

        private void SetLed(string key, bool on)
        {
            VisualElement led;
            if (leds.TryGetValue(key, out led))
                led.style.backgroundColor = on ? Coral : Off;
        }

        private void Highlight(Button button, bool active)
        {
            if (button == null) return;
            if (playerPresentation)
            {
                HighlightPlayerToggle(button, active);
                return;
            }
            button.style.backgroundColor = active ? Coral : Ocean;
        }
    }
}
