using System;
using System.Collections.Generic;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Racing;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    public sealed partial class FoundationShell
    {
        private bool IsPlayfulChaos => LoopContent() != null && LoopContent().QuickRaceOnly;
        private string homeDifficulty = "easy";
        private bool? lastTouchLayout;
        private bool UseTouchControls => Application.isMobilePlatform
            || (Touchscreen.current != null && Keyboard.current == null)
            || Array.IndexOf(Environment.GetCommandLineArgs(), "--touch-controls") >= 0;

        private void ApplyCompactRaceHud(FoundationState state, VisualElement top,
            VisualElement boostRow)
        {
            var controls = root.Q<VisualElement>("hud.controls");
            var keys = root.Q<Label>("hud.keys");
            var actions = root.Q<Button>("driving.gadget").parent;
            var pause = root.Q<Button>("driving.pause");
            var sharedDeck = controls.ClassListContains("race-deck");
            top.style.position = Position.Absolute;
            top.style.top = 12;
            top.style.left = 12;
            top.style.maxWidth = new Length(78, LengthUnit.Percent);
            top.style.flexWrap = Wrap.Wrap;
            elapsedLabel.style.fontSize = lapLabel.style.fontSize = 25;
            gateLabel.style.display = speedLabel.style.display = DisplayStyle.None;
            boostRow.style.position = Position.Absolute;
            boostRow.style.top = 78;
            boostRow.style.left = 12;
            boostRow.style.width = 310;
            boostRow.style.height = 36;
            var track = boostRow.Q<VisualElement>("hud.boostTrack");
            track.style.width = 200;
            track.style.height = 15;
            top.pickingMode = boostRow.pickingMode = PickingMode.Ignore;
            tutorialPanel.style.position = Position.Absolute;
            tutorialPanel.style.top = 128;
            tutorialPanel.style.left = tutorialPanel.style.right = 12;
            tutorialPanel.style.flexWrap = Wrap.Wrap;
            tutorialCueLabel.style.fontSize = 22;
            tutorialCueLabel.style.whiteSpace = WhiteSpace.Normal;
            tutorialCueLabel.style.flexShrink = 1;
            tutorialCueLabel.style.flexGrow = 1;
            tutorialIconLabel.style.fontSize = 32;
            foreach (var button in tutorialPanel.Query<Button>().ToList())
            {
                button.style.minHeight = 48;
                button.style.fontSize = 20;
            }

            var touch = UseTouchControls;
            controls.style.display = touch ? DisplayStyle.Flex : DisplayStyle.None;
            // Preserve the shared deck's nested clusters, safe areas and utility controls.
            if (!sharedDeck)
            {
            controls.style.position = Position.Absolute;
            controls.style.left = controls.style.right = 12;
            controls.style.bottom = 12;
            controls.style.height = 180;
            controls.pickingMode = PickingMode.Ignore;
            foreach (var element in controls.Children())
            {
                element.style.position = Position.Absolute;
                element.style.minWidth = element.style.width = 100;
                element.style.minHeight = element.style.height = 80;
                element.style.fontSize = 21;
                element.style.bottom = 0;
                if (element.name == "driving.steerLeft") element.style.left = 0;
                else if (element.name == "driving.steerRight") element.style.left = 110;
                else if (element.name == "driving.brake")
                { element.style.left = 0; element.style.bottom = 90; }
                else if (element.name == "driving.drift") element.style.right = 110;
                else element.style.right = 0;
            }
            }
            keys.text = T("fun.keys", state);
            keys.style.display = touch ? DisplayStyle.None : DisplayStyle.Flex;
            keys.style.position = Position.Absolute;
            keys.style.bottom = 8;
            keys.style.left = 12;
            keys.style.maxWidth = new Length(90, LengthUnit.Percent);
            keys.style.whiteSpace = WhiteSpace.Normal;
            keys.pickingMode = PickingMode.Ignore;

            if (!sharedDeck)
            {
            actions.style.position = Position.Absolute;
            actions.style.right = 12;
            actions.style.bottom = 104;
            actions.pickingMode = PickingMode.Ignore;
            foreach (var element in actions.Children())
            {
                element.style.minWidth = element.style.width = 100;
                element.style.minHeight = element.style.height = 80;
                element.style.fontSize = 19;
                element.style.whiteSpace = WhiteSpace.Normal;
                element.style.display = touch && (element.name == "driving.gadget"
                    || element.name == "driving.recover")
                    ? DisplayStyle.Flex : DisplayStyle.None;
                if (element.name == "driving.recover")
                    element.style.minWidth = element.style.width = 120;
            }
            }
            pause.RemoveFromHierarchy();
            root.Add(pause);
            pause.style.display = DisplayStyle.Flex;
            pause.style.position = Position.Absolute;
            pause.style.top = pause.style.right = 12;
            pause.style.height = pause.style.minHeight = 48;
            pause.style.width = pause.style.minWidth = 100;
            var menu = Row(pauseOverlay);
            AddButton(menu, T("settings.camera", state), Ocean,
                () => app.Dispatch("camera.toggle", emptyParams), "driving.menuCamera");
            AddButton(menu, T("driving.exit", state), Deep,
                () => app.Dispatch("race.exit", emptyParams), "driving.menuExit");
            statusLabel.style.display = DisplayStyle.None;
        }

        private void BuildPlayfulHome(FoundationState state)
        {
            var title = AddLabel(root, T("fun.title", state), 48, Deep);
            title.name = "home.title";
            AddLabel(root, T("fun.tagline", state), 24, Ocean);
            var hero = new VisualElement();
            hero.style.flexGrow = 1;
            hero.style.justifyContent = Justify.Center;
            hero.style.alignItems = Align.Center;
            root.Add(hero);
            var race = AddButton(hero, T("fun.raceNow", state), Coral,
                () => StartImmediateRace(), "home.raceNow");
            race.style.width = 360;
            race.style.minHeight = 100;
            race.style.fontSize = 36;
            ApplyAvailability(race, "quickRace.start");
            var difficulties = Row(hero);
            foreach (var id in new[] { "easy", "medium", "hard" })
            {
                var choice = id;
                var button = AddButton(difficulties, DifficultyName(choice, state),
                    choice == homeDifficulty ? Deep : Ocean,
                    () => { homeDifficulty = choice; rebuildKey = ""; },
                    "home.difficulty." + choice);
                button.style.minWidth = 120;
                button.style.fontSize = 22;
            }
            AddLabel(hero, T("fun.raceHint", state), 20, Deep);
            var actions = Row(root);
            AddButton(actions, T("lan.title", state), Ocean,
                () => app.Dispatch("ui.navigate", Screen("lan-browser")), "action.lan");
            AddButton(actions, T("practice.title", state), Seafoam,
                () => app.Dispatch("ui.navigate", Screen("practice")), "action.practice");
            AddButton(actions, T("action.settings", state), Ocean,
                () => app.Dispatch("ui.navigate", Screen("settings")), "action.settings");
            AddButton(actions, T("profile.select", state), Deep,
                () => app.Dispatch("ui.navigate", Screen("profiles")), "action.back");
        }

        private void StartImmediateRace()
        {
            var gameplay = LoopContent();
            if (gameplay == null || gameplay.Driving == null) return;
            var profile = app.Service.Snapshot().savedProfile;
            var candidates = new List<EventDefinition>(gameplay.Driving.Events.Values);
            candidates.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            foreach (var evt in candidates)
            {
                if (evt.eventType != GameplayEventType.Circuit || evt.isFinale) continue;
                foreach (var vehicleId in evt.vehicleIds)
                {
                    var entry = RaceModeRules.CanEnter(RaceMode.QuickRace, evt.courseId,
                        evt.id, vehicleId, profile, gameplay);
                    if (!entry.allowed) continue;
                    app.Dispatch("quickRace.start", new Dictionary<string, string>
                    {
                        { "courseId", evt.courseId }, { "classId", entry.classId },
                        { "vehicleId", vehicleId }, { "difficulty", homeDifficulty }
                    });
                    return;
                }
            }
        }
    }
}
