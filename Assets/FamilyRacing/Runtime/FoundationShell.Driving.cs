using System.Collections.Generic;
using FamilyRacing.Core.Foundation;
using UnityEngine;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    // Driving screens for the shell: the practice picker and the temporary
    // transparent driving overlay. Task 4 replaces the overlay with the
    // final HUD; the shape (readouts + held controls + action buttons) is
    // deliberate so real-agent input works end-to-end already.
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

        // --- driving overlay ---------------------------------------------
        // Transparent over the graybox course: a top strip of readouts, a
        // bottom row of held touch controls, and action buttons. The pause
        // overlay label gets an opaque backing so it stays readable over
        // the world.

        private void BuildDriving(FoundationState state)
        {
            var top = Row(root);
            elapsedLabel = AddLabel(top, "0:00.0", 30, Deep);
            elapsedLabel.name = "hud.elapsed";
            phaseLabel = AddLabel(top, "", 30, Coral);
            phaseLabel.name = "hud.phase";
            speedLabel = AddLabel(top, "", 30, Deep);
            speedLabel.name = "hud.speed";
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1f;
            root.Add(spacer);
            var controls = Row(root);
            AddHoldButton(controls, "◀", Ocean, held => app.Input.TouchLeft(held), "input.left");
            AddHoldButton(controls, "▶", Ocean, held => app.Input.TouchRight(held), "input.right");
            AddHoldButton(controls, T("input.drift", state), Seafoam,
                held => app.Input.TouchDrift(held), "input.drift");
            AddHoldButton(controls, T("input.boost", state), Coral,
                held => app.Input.TouchBoost(held), "input.boost");
            foreach (var held in controls.Children()) ApplyAvailability(held, "input.set");
            var actions = Row(root);
            var recover = AddButton(actions, T("input.recovery", state), Deep,
                () => app.Input.TouchRecover(), "input.recover");
            var camera = AddButton(actions, T("settings.camera", state), Ocean,
                () => app.Dispatch("camera.toggle", emptyParams), "action.camera");
            var pause = AddButton(actions, T("input.pause", state), Coral,
                () => app.Dispatch("pause.toggle", emptyParams), "action.pause");
            var exit = AddButton(actions, T("driving.exit", state), Deep,
                () => app.Dispatch("race.exit", emptyParams), "action.exit");
            ApplyAvailability(recover, "input.set");
            ApplyAvailability(camera, "camera.toggle");
            ApplyAvailability(pause, "pause.toggle");
            ApplyAvailability(exit, "race.exit");
            pauseOverlay = AddLabel(root, T("input.pause", state), 44, Coral);
            pauseOverlay.name = "overlay.pause";
            pauseOverlay.style.backgroundColor = new Color(0.99f, 0.97f, 0.92f, 0.85f);
            statusLabel = AddLabel(root, "", 18, Deep);
            statusLabel.name = "status.persistence";
        }

        private void UpdateDrivingIndicators(FoundationState state)
        {
            if (state.screen != "driving") return;
            var session = state.driving;
            if (session == null) return;
            if (elapsedLabel != null)
                elapsedLabel.text = T("hud.time", state) + " " +
                    FormatRaceTime(session.elapsedTicks);
            if (phaseLabel != null)
            {
                if (session.phase == "countdown" && session.countdownTicksRemaining > 0)
                    phaseLabel.text = ((session.countdownTicksRemaining + 29) / 30).ToString();
                else if (session.phase == "finished")
                    phaseLabel.text = T("hud.finished", state);
                else
                    phaseLabel.text = "";
            }
            if (speedLabel != null && session.motor != null)
                speedLabel.text = T("hud.speed", state) + " " +
                    ((int)(session.motor.speed * 3.6f)).ToString();
        }

        private static string FormatRaceTime(long ticks)
        {
            // Simulation ticks at 30 Hz; race time is displayed as m:ss.d
            // with invariant digits (numbers stay LTR in both locales).
            var deciseconds = ticks * 10 / 30;
            var minutes = deciseconds / 600;
            var seconds = deciseconds % 600 / 10;
            var tenth = deciseconds % 10;
            return minutes.ToString() + ":" +
                seconds.ToString("00") + "." + tenth.ToString();
        }
    }
}
