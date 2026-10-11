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
        private static readonly Color Off = new Color(0.75f, 0.72f, 0.66f);
        private static readonly Color[] CardColors = { Coral, Seafoam, Ocean, new Color(0.80f, 0.66f, 0.45f) };
        private static readonly string[] AnimalKeys =
            { "profile.animal.fox", "profile.animal.rabbit", "profile.animal.panda", "profile.animal.raccoon" };

        private FoundationApp app;
        private UIDocument document;
        private VisualElement root;
        private string rebuildKey = "";
        private bool suppressSliderEvents;
        private Label statusLabel;
        private Label warningLabel;
        private Label steeringLabel;
        private readonly Dictionary<string, VisualElement> leds =
            new Dictionary<string, VisualElement>();
        private Label pauseOverlay;
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
            ApplyTextConfiguration(root);
            rebuildKey = "";
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
            root.languageDirection = state.language == "he"
                ? LanguageDirection.RTL : LanguageDirection.LTR;
            var key = state.screen + "|" + state.language + "|" + state.activeSlot;
            foreach (var slot in state.slots) key += "|" + slot.status;
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
            statusLabel = warningLabel = steeringLabel = pauseOverlay = null;
            saveButton = null;
            musicSlider = effectsSlider = voiceSlider = null;
            chaseButton = overheadButton = shakeButton = langEnButton = langHeButton = null;
            root.style.backgroundColor = Sand;
            root.style.paddingTop = root.style.paddingBottom = 16;
            root.style.paddingLeft = root.style.paddingRight = 16;
            switch (state.screen)
            {
                case "settings": BuildSettings(state); break;
                case "sandbox": BuildSandbox(state); break;
                default: BuildProfiles(state); break;
            }
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
                warningLabel.text = state.presentation.fontWarning;
            if (statusLabel != null)
            {
                var slot = state.activeSlot >= 0 && state.activeSlot < state.slots.Count
                    ? state.slots[state.activeSlot] : null;
                if (slot != null && slot.status == "Blocked") statusLabel.text = T("status.blocked", state);
                else if (slot != null && slot.status == "RecoveredFromBackup")
                    statusLabel.text = T("status.recovered", state);
                else if (state.persistenceStatus == "saveFailed")
                    statusLabel.text = T("status.saveFailed", state);
                else if (state.persistenceStatus == "saved")
                    statusLabel.text = T("status.saved", state);
                else if (statusLabel.text.Length > 0) statusLabel.text = "";
            }
            if (state.screen == "sandbox" && state.input != null)
            {
                if (steeringLabel != null)
                    steeringLabel.text = T("input.steering", state) + ": " +
                        state.input.steering.ToString("0.00");
                SetLed("drift", state.input.drift);
                SetLed("boost", state.input.boost);
                SetLed("gadget", state.input.gadget);
                SetLed("recover", state.input.recover);
                if (pauseOverlay != null)
                    pauseOverlay.style.display = state.paused ? DisplayStyle.Flex : DisplayStyle.None;
            }
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
                if (saveButton != null) saveButton.SetEnabled(state.pendingSettings);
            }
        }

        private void SetLed(string key, bool on)
        {
            VisualElement led;
            if (leds.TryGetValue(key, out led))
                led.style.backgroundColor = on ? Coral : Off;
        }

        private static void Highlight(Button button, bool active)
        {
            if (button != null)
                button.style.backgroundColor = active ? Coral : Ocean;
        }
    }
}
