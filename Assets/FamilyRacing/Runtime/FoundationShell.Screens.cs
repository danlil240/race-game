using System;
using System.Collections.Generic;
using FamilyRacing.Core.Foundation;
using UnityEngine;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    // Screen builders for the shell: profiles, settings, sandbox. Split from
    // FoundationShell.cs to keep each file focused.
    public sealed partial class FoundationShell
    {
        // --- profiles screen ----------------------------------------------

        private void BuildProfiles(FoundationState state)
        {
            AddLabel(root, T("shell.title", state), 44, Deep);
            AddLabel(root, T("shell.disclaimer", state), 18, Deep);
            AddLabel(root, T("profile.select", state), 28, Deep);
            var cards = Row(root);
            for (var i = 0; i < state.slots.Count; i++)
            {
                var slot = i;
                var info = state.slots[slot];
                var card = new VisualElement();
                card.name = "profile." + slot;
                card.style.width = 200;
                card.style.marginLeft = card.style.marginRight = 10;
                card.style.backgroundColor = Cream;
                card.style.borderTopLeftRadius = card.style.borderTopRightRadius =
                    card.style.borderBottomLeftRadius = card.style.borderBottomRightRadius = 18;
                var band = new VisualElement();
                band.style.height = 120;
                band.style.backgroundColor = CardColors[slot % CardColors.Length];
                band.style.borderTopLeftRadius = band.style.borderTopRightRadius = 18;
                card.Add(band);
                AddLabel(card, T(AnimalKeys[slot % AnimalKeys.Length], state), 26, Deep);
                var pick = AddButton(card, info.status == "Missing" ? "+" : ">",
                    CardColors[slot % CardColors.Length], () => SelectCard(state, slot),
                    "profile." + slot + ".select");
                pick.style.minHeight = 56;
                ApplyAvailability(pick, info.status == "Missing" ? "profile.create" : "profile.select");
                cards.Add(card);
            }
            var langRow = Row(root);
            AddLabel(langRow, T("settings.language", state), 22, Deep);
            langEnButton = AddButton(langRow, "English", Ocean, () => Language("en"),
                "settings.language.en");
            langHeButton = AddButton(langRow, "עברית", Ocean, () => Language("he"),
                "settings.language.he");
            ApplyAvailability(langEnButton, "ui.language");
            ApplyAvailability(langHeButton, "ui.language");
            warningLabel = AddLabel(root, "", 18, Coral);
            warningLabel.name = "status.fontWarning";
            statusLabel = AddLabel(root, "", 20, Deep);
            statusLabel.name = "status.persistence";
        }

        private void SelectCard(FoundationState state, int slot)
        {
            var info = state.slots[slot];
            if (info.status == "Missing")
            {
                var create = new Dictionary<string, string>();
                create["slot"] = slot.ToString();
                create["language"] = state.language;
                var result = app.Dispatch("profile.create", create);
                if (result.status == "applied")
                    app.Dispatch("ui.navigate", Screen("settings"));
            }
            else
            {
                var select = new Dictionary<string, string>();
                select["slot"] = slot.ToString();
                var result = app.Dispatch("profile.select", select);
                if (result.status == "applied" && info.status == "Loaded")
                    app.Dispatch("ui.navigate", Screen("sandbox"));
            }
        }

        private void Language(string language)
        {
            var p = new Dictionary<string, string>();
            p["language"] = language;
            app.Dispatch("ui.language", p);
        }

        // --- settings screen ----------------------------------------------

        private void BuildSettings(FoundationState state)
        {
            AddLabel(root, T("shell.title", state), 40, Deep);
            var draft = state.draftProfile;
            var langRow = Row(root);
            AddLabel(langRow, T("settings.language", state), 22, Deep);
            langEnButton = AddButton(langRow, "English", Ocean,
                () => UpdateSettings(language: "en"), "settings.language.en");
            langHeButton = AddButton(langRow, "עברית", Ocean,
                () => UpdateSettings(language: "he"), "settings.language.he");
            var camRow = Row(root);
            AddLabel(camRow, T("settings.camera", state), 22, Deep);
            chaseButton = AddButton(camRow, T("settings.camera.chase", state), Ocean,
                () => UpdateSettings(camera: "chase"), "settings.camera.chase");
            overheadButton = AddButton(camRow, T("settings.camera.overhead", state), Ocean,
                () => UpdateSettings(camera: "overhead"), "settings.camera.overhead");
            var shakeRow = Row(root);
            shakeButton = AddButton(shakeRow, T("settings.reducedShake", state), Ocean,
                () => UpdateSettings(toggleShake: true), "settings.reducedShake");
            musicSlider = AddSlider(root, T("settings.volume.music", state),
                draft != null ? draft.musicVolume : 0.8f, v => UpdateSettings(music: v),
                "settings.volume.music");
            effectsSlider = AddSlider(root, T("settings.volume.effects", state),
                draft != null ? draft.effectsVolume : 0.8f, v => UpdateSettings(effects: v),
                "settings.volume.effects");
            voiceSlider = AddSlider(root, T("settings.volume.voice", state),
                draft != null ? draft.voiceVolume : 0.8f, v => UpdateSettings(voice: v),
                "settings.volume.voice");
            foreach (var control in new VisualElement[]
                { langEnButton, langHeButton, chaseButton, overheadButton,
                  shakeButton, musicSlider, effectsSlider, voiceSlider })
                ApplyAvailability(control, "settings.update");
            var actionRow = Row(root);
            saveButton = AddButton(actionRow, T("action.save", state), Coral,
                () => app.Dispatch("settings.save", emptyParams), "settings.save");
            ApplyAvailability(saveButton, "settings.save");
            var back = AddButton(actionRow, T("action.back", state), Deep,
                () => app.Dispatch("ui.navigate", Screen("profiles")), "action.back");
            ApplyAvailability(back, "ui.navigate");
            warningLabel = AddLabel(root, "", 18, Coral);
            warningLabel.name = "status.fontWarning";
            statusLabel = AddLabel(root, "", 20, Deep);
            statusLabel.name = "status.persistence";
        }

        private Slider AddSlider(VisualElement parent, string label, float value,
            Action<float> onChange, string name = null)
        {
            var row = Row(parent);
            var caption = AddLabel(row, label, 22, Deep);
            caption.style.minWidth = 220;
            var slider = new Slider(0f, 1f) { value = value };
            if (name != null) slider.name = name;
            slider.style.minWidth = 300;
            slider.style.minHeight = 48;
            slider.RegisterValueChangedCallback(evt =>
            {
                if (!suppressSliderEvents) onChange(evt.newValue);
            });
            row.Add(slider);
            return slider;
        }

        private void UpdateSettings(string language = null,
            string camera = null, bool toggleShake = false, float? music = null,
            float? effects = null, float? voice = null)
        {
            // The settings tree refreshes in place between edits, so callbacks
            // must merge against the live draft — the FoundationState captured
            // at build time would silently drop earlier pending values.
            var draft = app.Service.Snapshot().draftProfile;
            if (draft == null) return;
            settingsParams["language"] = language ?? draft.language;
            settingsParams["camera"] = camera ?? draft.camera;
            var shake = toggleShake ? !draft.reducedCameraShake : draft.reducedCameraShake;
            settingsParams["reducedCameraShake"] = shake ? "true" : "false";
            settingsParams["musicVolume"] = Format(music ?? draft.musicVolume);
            settingsParams["effectsVolume"] = Format(effects ?? draft.effectsVolume);
            settingsParams["voiceVolume"] = Format(voice ?? draft.voiceVolume);
            app.Dispatch("settings.update", settingsParams);
        }

        // --- sandbox screen ------------------------------------------------

        private void BuildSandbox(FoundationState state)
        {
            AddLabel(root, T("sandbox.title", state), 40, Deep);
            var indicators = Row(root);
            steeringLabel = AddLabel(indicators, T("input.steering", state) + ": 0", 22, Deep);
            steeringLabel.name = "indicator.steering";
            AddLed(indicators, "drift", T("input.drift", state));
            AddLed(indicators, "boost", T("input.boost", state));
            AddLed(indicators, "brake", T("input.brake", state));
            AddLed(indicators, "gadget", T("input.gadget", state));
            AddLed(indicators, "recover", T("input.recovery", state));
            var controls = Row(root);
            AddHoldButton(controls, "◀", Ocean, held => app.Input.TouchLeft(held), "input.left");
            AddHoldButton(controls, "▶", Ocean, held => app.Input.TouchRight(held), "input.right");
            AddHoldButton(controls, T("input.brake", state), Deep, held => app.Input.TouchBrake(held), "input.brake");
            AddHoldButton(controls, T("input.drift", state), Seafoam, held => app.Input.TouchDrift(held), "input.drift");
            AddHoldButton(controls, T("input.boost", state), Coral, held => app.Input.TouchBoost(held), "input.boost");
            foreach (var held in controls.Children()) ApplyAvailability(held, "input.set");
            var actions = Row(root);
            var gadget = AddButton(actions, T("input.gadget", state), Deep,
                () => app.Input.TouchGadget(), "input.gadget");
            var recover = AddButton(actions, T("input.recovery", state), Deep,
                () => app.Input.TouchRecover(), "input.recover");
            var camera = AddButton(actions, T("settings.camera", state), Ocean,
                () => app.Dispatch("camera.toggle", emptyParams), "action.camera");
            var pause = AddButton(actions, T("input.pause", state), Coral,
                () => app.Dispatch("pause.toggle", emptyParams), "action.pause");
            var practice = AddButton(actions, T("practice.open", state), Seafoam,
                () => app.Dispatch("ui.navigate", Screen("practice")), "action.practice");
            var lan = AddButton(actions, T("lan.title", state), Ocean,
                () => app.Dispatch("ui.navigate", Screen("lan-browser")), "action.lan");
            var back = AddButton(actions, T("action.back", state), Deep,
                () => app.Dispatch("ui.navigate", Screen("profiles")), "action.back");
            ApplyAvailability(gadget, "input.set");
            ApplyAvailability(recover, "input.set");
            ApplyAvailability(camera, "camera.toggle");
            ApplyAvailability(pause, "pause.toggle");
            ApplyAvailability(practice, "race.start");
            ApplyAvailability(lan, "ui.navigate");
            ApplyAvailability(back, "ui.navigate");
            // M5 Task 10: the gameplay-loop entries — gated *.open
            // commands, exactly like an agent dispatching them. A
            // disabled button carries the discovery reason as its tooltip.
            var loop = Row(root);
            loop.name = "loop.entries";
            var campaign = AddButton(loop, T("campaign.title", state), Coral,
                () => app.Dispatch("campaign.open", emptyParams), "loop.campaign");
            var quick = AddButton(loop, T("quick.title", state), Seafoam,
                () => app.Dispatch("quickRace.open", emptyParams), "loop.quickRace");
            var trial = AddButton(loop, T("trial.title", state), Ocean,
                () => app.Dispatch("timeTrial.open", emptyParams), "loop.timeTrial");
            var garage = AddButton(loop, T("garage.title", state), Deep,
                () => app.Dispatch("garage.open", emptyParams), "loop.garage");
            var bounties = AddButton(loop, T("bounties.title", state), Seafoam,
                () => app.Dispatch("bounties.open", emptyParams), "loop.bounties");
            ApplyAvailability(campaign, "campaign.open");
            ApplyAvailability(quick, "quickRace.open");
            ApplyAvailability(trial, "timeTrial.open");
            ApplyAvailability(garage, "garage.open");
            ApplyAvailability(bounties, "bounties.open");
            pauseOverlay = AddLabel(root, T("input.pause", state), 40, Coral);
            pauseOverlay.name = "overlay.pause";
            statusLabel = AddLabel(root, "", 18, Deep);
            statusLabel.name = "status.persistence";
        }

        private void AddLed(VisualElement parent, string key, string text)
        {
            var led = new VisualElement();
            led.name = "indicator." + key;
            led.style.width = 120;
            led.style.minHeight = 56;
            led.style.backgroundColor = Off;
            led.style.borderTopLeftRadius = led.style.borderTopRightRadius =
                led.style.borderBottomLeftRadius = led.style.borderBottomRightRadius = 12;
            led.style.marginLeft = led.style.marginRight = 6;
            var label = new Label(text);
            label.style.fontSize = 18;
            label.style.color = Color.white;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            led.Add(label);
            leds[key] = led;
            parent.Add(led);
        }
    }
}
