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
            AddLabel(root, T("practice.title", state), 40, Deep);
            AddLabel(root, T("practice.hint", state), 20, Deep);
            var picker = Row(root);
            var car = AddButton(picker, T("practice.car", state), Coral,
                () => StartRace("car-01"), "practice.car");
            var moto = AddButton(picker, T("practice.motorcycle", state), Seafoam,
                () => StartRace("motorcycle-01"), "practice.motorcycle");
            // Optional onboarding entry: the same tutorial.start command
            // an agent uses, one button per vehicle class.
            var tutorialRow = Row(root);
            AddLabel(tutorialRow, T("practice.tutorial", state), 22, Deep);
            var learnCar = AddButton(tutorialRow, T("practice.car", state), Ocean,
                () => StartTutorial("car-01"), "tutorial.car");
            var learnMoto = AddButton(tutorialRow, T("practice.motorcycle", state), Ocean,
                () => StartTutorial("motorcycle-01"), "tutorial.motorcycle");
            ApplyAvailability(learnCar, "tutorial.start");
            ApplyAvailability(learnMoto, "tutorial.start");
            var actions = Row(root);
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

            var boostRow = Row(root);
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
            tutorialPanel.style.backgroundColor = new Color(0.99f, 0.97f, 0.92f, 0.85f);
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
            AddHoldButton(controls, T("input.drift", state), Seafoam,
                held => app.Input.TouchDrift(held), "driving.drift");
            AddHoldButton(controls, T("input.boost", state), Coral,
                held => app.Input.TouchBoost(held), "driving.boost");
            foreach (var held in controls.Children()) ApplyAvailability(held, "input.set");

            var actions = Row(root);
            var recover = AddButton(actions, T("input.recovery", state), Deep,
                () => app.Input.TouchRecover(), "driving.recover");
            var camera = AddButton(actions, T("settings.camera", state), Ocean,
                () => app.Dispatch("camera.toggle", emptyParams), "driving.camera");
            // Gadgets are deferred content: the affordance exists but is
            // visibly disabled with an honest label/tooltip instead of
            // promising input the command path would reject.
            var gadget = AddButton(actions, T("hud.gadget.soon", state), Off,
                () => { }, "driving.gadget");
            gadget.SetEnabled(false);
            gadget.tooltip = T("hud.gadget.unavailable", state);
            var pause = AddButton(actions, T("input.pause", state), Coral,
                () => app.Dispatch("pause.toggle", emptyParams), "driving.pause");
            var exit = AddButton(actions, T("driving.exit", state), Deep,
                () => app.Dispatch("race.exit", emptyParams), "driving.exit");
            ApplyAvailability(recover, "input.set");
            ApplyAvailability(camera, "camera.toggle");
            ApplyAvailability(pause, "pause.toggle");
            ApplyAvailability(exit, "race.exit");

            // Pause and finish overlays cover the screen when shown; both
            // keep the world dimly visible and offer their own actions.
            pauseOverlay = Overlay("overlay.pause");
            AddLabel(pauseOverlay, T("hud.paused", state), 44, Deep);
            var pauseRow = Row(pauseOverlay);
            var resume = AddButton(pauseRow, T("hud.resume", state), Seafoam,
                () => app.Dispatch("pause.toggle", emptyParams), "driving.resume");
            var pauseRetry = AddButton(pauseRow, T("hud.retry", state), Coral,
                () => app.Dispatch("race.restart", emptyParams), "driving.retry");
            ApplyAvailability(resume, "pause.toggle");
            ApplyAvailability(pauseRetry, "race.restart");

            finishOverlay = Overlay("overlay.finish");
            var finished = AddLabel(finishOverlay, T("hud.finished", state), 44, Coral);
            finished.name = "hud.finishTitle";
            finishTimeLabel = AddLabel(finishOverlay, "", 30, Deep);
            finishTimeLabel.name = "hud.finishTime";
            var finishRow = Row(finishOverlay);
            var retry = AddButton(finishRow, T("hud.retry", state), Seafoam,
                () => app.Dispatch("race.restart", emptyParams), "driving.retry");
            var finishExit = AddButton(finishRow, T("driving.exit", state), Deep,
                () => app.Dispatch("race.exit", emptyParams), "driving.exit");
            ApplyAvailability(retry, "race.restart");
            ApplyAvailability(finishExit, "race.exit");

            statusLabel = AddLabel(root, "", 18, Deep);
            statusLabel.name = "status.persistence";
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
            overlay.style.backgroundColor = new Color(0.99f, 0.97f, 0.92f, 0.85f);
            overlay.style.alignItems = Align.Center;
            overlay.style.justifyContent = Justify.Center;
            overlay.style.display = DisplayStyle.None;
            root.Add(overlay);
            return overlay;
        }

        private void UpdateDrivingIndicators(FoundationState state)
        {
            if (state.screen != "driving") return;
            var session = state.driving;
            if (session == null) return;
            if (elapsedLabel != null)
                elapsedLabel.text = T("hud.time", state) + " " +
                    FormatRaceTime(session.elapsedTicks);
            if (lapLabel != null)
                lapLabel.text = T("hud.lap", state) + " " +
                    Mathf.Min(session.lap + 1, session.lapsRequired) + "/" +
                    session.lapsRequired;
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
                var done = session.phase == "finished";
                finishOverlay.style.display = done ? DisplayStyle.Flex : DisplayStyle.None;
                if (done && finishTimeLabel != null)
                    finishTimeLabel.text = T("hud.time", state) + " " +
                        FormatRaceTime(session.finishTicks > 0
                            ? session.finishTicks
                            : session.elapsedTicks);
            }
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
