using System;
using System.Collections.Generic;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.UIElements;
using LanSettings = FamilyRacing.Core.Networking.NetworkSettings;

namespace FamilyRacing
{
    // LAN screens for the shell (Task 9): the family-facing surface over
    // the LanSessionService commands — the browser (host/discover/manual
    // join/cancel), the lobby (course/class/vehicle, ready, start), the
    // race HUD (shared countdown/clock, standings, held controls, shared
    // pause, leave), the results screen (standings, delivery states,
    // rematch, save retry) and the recovery screen (reconnect/leave with
    // readable error). Every control carries a stable lan.* semantic name
    // and routes through the same lan.* commands agents drive; disabled
    // controls carry their honest reason on the tooltip.
    public sealed partial class FoundationShell
    {
        // In-place HUD labels on the lan-race screen so a rebuild-per-tick
        // never steals pointer capture from held controls.
        private Label lanTimeLabel;
        private Label lanStandingsLabel;
        private Label lanPhaseLabel;
        private Label lanActionLabel;
        private Label lanPauseLabel;

        // Manual-join feedback (PCQA-007): shell-side validation state.
        // The error line and the offending-field highlight update in
        // place so the typed address/port never resets to a rebuild, and
        // network.lastError stays reserved for real service verdicts.
        private TextField lanAddressField;
        private TextField lanPortField;
        private Label lanJoinErrorLabel;
        private string lanJoinErrorKey = "";
        private string lanJoinErrorCode = "";
        private string lanJoinErrorField = "";

        // Rebuild signature for LAN screens: the rendered roster,
        // configuration, discovery and delivery rows are part of the key
        // so a peer joining, readying, disconnecting or a result delivery
        // changing state rebuilds the tree exactly once.
        private static string LanRebuildSignature(FoundationState state)
        {
            var net = state.network;
            var key = (net.phase ?? "") + "|" + net.paused + "|"
                + (net.lastError ?? "") + "|" + net.pauseRequests.Count
                + "|" + net.pendingOperations.Count;
            if (net.descriptor != null)
                key += "|" + (net.descriptor.sessionId ?? "")
                    + (net.descriptor.eventId ?? "")
                    + (net.descriptor.classId ?? "")
                    + net.descriptor.joinable;
            foreach (var p in net.participants)
                key += "|" + p.slot + (p.participantId ?? "") + p.occupied
                    + p.connected + p.ready + (p.controlSource ?? "")
                    + (p.vehicleId ?? "") + (p.avatar ?? "")
                    + p.ownershipEpoch;
            foreach (var h in net.discovered)
                key += "|" + (h.address ?? "") + h.port;
            foreach (var r in net.results)
                key += "|" + (r.resultId ?? "") + (r.participantId ?? "")
                    + (r.state ?? "");
            return key;
        }

        private static bool IsLanScreen(string screen)
        {
            return FoundationService.IsLanScreen(screen);
        }

        // Readable bilingual text for a session error or disabled reason;
        // the raw code is the honest fallback so an unlisted code still
        // surfaces instead of vanishing.
        private string ReasonText(string code, FoundationState state)
        {
            if (string.IsNullOrEmpty(code)) return "";
            string text;
            try { text = T("lan.reason." + code, state); }
            catch (KeyNotFoundException) { return code; }
            catch (ArgumentException) { return code; }
            return text;
        }

        // The shared footer: phase/status line plus the safe last error.
        private void LanStatus(FoundationState state, string phaseText)
        {
            var label = AddLabel(root, phaseText, 20, Deep);
            label.name = "lan.status";
            var error = state.network != null ? state.network.lastError : null;
            if (!string.IsNullOrEmpty(error))
            {
                var err = AddLabel(root,
                    T("lan.err.title", state) + ": " + ReasonText(error, state),
                    18, Coral);
                err.name = "lan.error";
            }
            warningLabel = AddLabel(root, "", 18, Coral);
            warningLabel.name = "status.fontWarning";
            statusLabel = AddLabel(root, "", 18, Deep);
            statusLabel.name = "status.persistence";
        }

        private Dictionary<string, string> JoinParams(string address, int port)
        {
            var p = new Dictionary<string, string>();
            p["address"] = address ?? "";
            p["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return p;
        }

        // lan.results.request names the immutable race it asks about. The
        // live race slice is the primary source; the delivery ledger is
        // the fallback for a client whose race state already retired.
        private static Dictionary<string, string> ResultParams(
            FoundationState state)
        {
            var raceId = state.networkRace != null
                ? state.networkRace.raceId : null;
            if (string.IsNullOrEmpty(raceId) && state.network != null
                && state.network.results != null
                && state.network.results.Count > 0)
                raceId = state.network.results[0].raceId;
            var p = new Dictionary<string, string>();
            p["raceId"] = raceId ?? "";
            return p;
        }

        // --- lan-browser ---------------------------------------------------
        // Host, discover, discovered-host rows, manual address join, and
        // cancel during connecting — the only user-navigable LAN screen.

        private void BuildLanBrowser(FoundationState state)
        {
            AddLabel(root, T("lan.title", state), 40, Deep);
            AddLabel(root, T("lan.hint", state), 18, Deep);
            var net = state.network;
            var row = Row(root);
            var host = AddButton(row, T("lan.host", state), Coral,
                () => app.Dispatch("lan.host",
                    PortParam(LanSettings.DefaultGameplayPort)),
                "lan.host");
            ApplyAvailability(host, "lan.host");

            var discovering = net != null && net.phase == "discovering";
            var connecting = net != null && net.phase == "connecting";
            if (!connecting)
            {
                var discover = AddButton(row, T("lan.discover", state), Ocean,
                    () => app.Dispatch("lan.discover", emptyParams), "lan.discover");
                ApplyAvailability(discover, "lan.discover");
                var stop = AddButton(row, T("lan.stopSearch", state), Deep,
                    () => app.Dispatch("lan.discovery.stop", emptyParams),
                    "lan.discovery.stop");
                ApplyAvailability(stop, "lan.discovery.stop");
            }

            // Found hosts: address/port fields stay LTR inside the RTL
            // shell so a child never reads a mirrored dotted-quad.
            var foundRow = Row(root);
            AddLabel(foundRow, T("lan.found", state), 22, Deep);
            if (net != null && net.discovered != null && net.discovered.Count > 0)
            {
                for (var i = 0; i < net.discovered.Count; i++)
                {
                    var index = i;
                    var entry = net.discovered[index];
                    var card = new VisualElement();
                    card.name = "lan.discovered." + index;
                    card.style.backgroundColor = Cream;
                    card.style.marginLeft = card.style.marginRight = 6;
                    card.style.paddingTop = card.style.paddingBottom = 4;
                    var address = AddLabel(card,
                        "\u200E" + (entry.address ?? "") + ":" + entry.port,
                        18, Deep);
                    address.name = "lan.discovered." + index + ".address";
                    address.languageDirection = LanguageDirection.LTR;
                    var joinFound = AddButton(card, T("lan.join", state), Seafoam,
                        () => JoinDiscoveredHost(state, entry),
                        "lan.discovered." + index + ".join");
                    joinFound.style.minHeight = 48;
                    joinFound.style.fontSize = 20;
                    ApplyAvailability(joinFound, "lan.join");
                    foundRow.Add(card);
                }
            }
            else
            {
                var none = AddLabel(foundRow, T("lan.none", state), 18, Off);
                none.name = "lan.discovered.none";
            }

            // Manual join: explicit address + port fields for when
            // discovery cannot see the host (different subnet, filtered
            // broadcast); both fields are locked LTR.
            var manual = Row(root);
            manual.name = "lan.manual";
            AddLabel(manual, T("lan.address", state), 20, Deep);
            lanAddressField = new TextField { value = "" };
            lanAddressField.name = "lan.join.address";
            lanAddressField.languageDirection = LanguageDirection.LTR;
            lanAddressField.style.minWidth = 220;
            lanAddressField.style.minHeight = 44;
            // Editing either field clears the inline error in place.
            lanAddressField.RegisterValueChangedCallback(
                _ => ClearLanJoinFeedback(state));
            manual.Add(lanAddressField);
            AddLabel(manual, T("lan.port", state), 20, Deep);
            lanPortField = new TextField { value = LanSettings.DefaultGameplayPort
                .ToString(System.Globalization.CultureInfo.InvariantCulture) };
            lanPortField.name = "lan.join.port";
            lanPortField.languageDirection = LanguageDirection.LTR;
            lanPortField.style.minWidth = 110;
            lanPortField.style.minHeight = 44;
            lanPortField.RegisterValueChangedCallback(
                _ => ClearLanJoinFeedback(state));
            manual.Add(lanPortField);
            var join = AddButton(manual, T("lan.join", state), Seafoam,
                () => TryManualJoin(state), "lan.join");
            ApplyAvailability(join, "lan.join");
            // The inline error line: always present so a click can paint
            // it without a rebuild; build-time state keeps it honest
            // across unrelated signature rebuilds.
            lanJoinErrorLabel = AddLabel(root, "", 18, Coral);
            lanJoinErrorLabel.name = "lan.join.error";
            PaintLanJoinError(state);

            var actions = Row(root);
            var leave = AddButton(actions, T("lan.leave", state), Deep,
                () => app.Dispatch("lan.leave", emptyParams), "lan.leave");
            ApplyAvailability(leave, "lan.leave");
            var back = AddButton(actions, T("action.back", state), Deep,
                () => app.Dispatch("ui.navigate", Screen("sandbox")),
                "action.back");
            ApplyAvailability(back, "ui.navigate");

            LanStatus(state, connecting
                ? T("lan.connecting", state)
                : (discovering ? T("lan.discovering", state) : ""));
        }

        // Manual-join validation (PCQA-007): input problems are answered
        // by the shell itself — a readable line under the fields plus a
        // highlight on the offender — without ever dispatching; a
        // synchronous service rejection surfaces the same honest reason
        // code the agent channel got. Neither path fakes network.lastError.
        private void TryManualJoin(FoundationState state)
        {
            var address = lanAddressField != null && lanAddressField.value != null
                ? lanAddressField.value.Trim() : "";
            var portText = lanPortField != null && lanPortField.value != null
                ? lanPortField.value.Trim() : "";
            if (address.Length == 0)
            {
                FailManualJoin(state, "lan.join.error.address", "address");
                return;
            }
            // The check mirrors the transport's own gate exactly
            // (LanTransport.StartClient): an IPv4-literal endpoint —
            // hostnames and IPv6 can never connect on this transport,
            // so they are input errors here, not late connect failures.
            NetworkEndpoint endpoint;
            if (!NetworkEndpoint.TryParse(address,
                    LanSettings.DefaultGameplayPort, out endpoint)
                || endpoint.Family != NetworkFamily.Ipv4)
            {
                FailManualJoin(state, "lan.join.error.addressBad", "address");
                return;
            }
            int port;
            if (!int.TryParse(portText,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out port)
                || port < 0 || port > 65535)
            {
                FailManualJoin(state, "lan.join.error.port", "port");
                return;
            }
            var result = app.Dispatch("lan.join", JoinParams(address, port));
            if (result == null
                || result.status == CommandResult.Rejected
                || result.status == CommandResult.Failed)
            {
                ShowJoinOutcome(state, result);
                return;
            }
            ClearLanJoinFeedback(state);
        }

        // Discovered joins carry a valid address/port, so they always
        // dispatch — but a synchronous rejection (an incompatible
        // advertisement, a pending join, a full pending queue) still
        // paints the inline error instead of silently swallowing the
        // verdict. Clearing stale manual feedback happens up front, not
        // instead of surfacing.
        private void JoinDiscoveredHost(FoundationState state,
            LanDiscoveredHost entry)
        {
            ClearLanJoinFeedback(state);
            ShowJoinOutcome(state, app.Dispatch("lan.join",
                JoinParams(entry != null ? entry.address : null,
                    entry != null ? entry.port : 0)));
        }

        // Shared synchronous-outcome paint for both join paths:
        // applied/accepted is a no-op here (the success path owns its
        // feedback), rejected/failed surfaces the honest reason code
        // through ReasonText — the raw code survives when no key exists.
        // No field highlight: the service owns this verdict.
        private void ShowJoinOutcome(FoundationState state,
            CommandResult result)
        {
            if (result != null && result.status != CommandResult.Rejected
                && result.status != CommandResult.Failed) return;
            lanJoinErrorKey = "";
            lanJoinErrorCode = result != null
                && !string.IsNullOrEmpty(result.code)
                ? result.code : "rejected";
            lanJoinErrorField = "";
            PaintLanJoinError(state);
        }

        private void FailManualJoin(FoundationState state, string key,
            string field)
        {
            lanJoinErrorKey = key;
            lanJoinErrorCode = "";
            lanJoinErrorField = field;
            PaintLanJoinError(state);
        }

        private void ClearLanJoinFeedback(FoundationState state)
        {
            if (lanJoinErrorKey.Length == 0 && lanJoinErrorCode.Length == 0
                && lanJoinErrorField.Length == 0) return;
            lanJoinErrorKey = lanJoinErrorCode = lanJoinErrorField = "";
            PaintLanJoinError(state);
        }

        // In-place paint: the message and highlight land on the live
        // widgets, so typing never loses pointer capture and the fields
        // keep their contents. BuildLanBrowser calls the same paint at
        // build time so a rebuild for an unrelated signature change
        // re-renders the feedback honestly.
        private void PaintLanJoinError(FoundationState state)
        {
            if (lanJoinErrorLabel != null)
                lanJoinErrorLabel.text = lanJoinErrorKey.Length > 0
                    ? T(lanJoinErrorKey, state)
                    : ReasonText(lanJoinErrorCode, state);
            MarkJoinField(lanAddressField, lanJoinErrorField == "address");
            MarkJoinField(lanPortField, lanJoinErrorField == "port");
        }

        private static void MarkJoinField(TextField field, bool invalid)
        {
            if (field == null) return;
            field.EnableInClassList("lan-invalid", invalid);
            var border = invalid ? (StyleColor)Coral
                : new StyleColor(StyleKeyword.Null);
            field.style.borderTopColor = field.style.borderBottomColor =
                field.style.borderLeftColor = field.style.borderRightColor =
                    border;
            var width = invalid ? (StyleFloat)2f
                : new StyleFloat(StyleKeyword.Null);
            field.style.borderTopWidth = field.style.borderBottomWidth =
                field.style.borderLeftWidth = field.style.borderRightWidth =
                    width;
        }

        private static Dictionary<string, string> PortParam(int port)
        {
            var p = new Dictionary<string, string>();
            p["port"] = port.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            return p;
        }

        // --- lan-lobby -----------------------------------------------------
        // The roster is always the service's own view: ownership, connect
        // and control-source state are rendered readably per slot; the
        // host additionally gets course/class pickers, everyone gets a
        // vehicle picker and the shared ready/start/leave controls.

        private void BuildLanLobby(FoundationState state)
        {
            AddLabel(root, T("lan.lobby", state), 40, Deep);
            var net = state.network;
            var descriptor = net != null ? net.descriptor : null;
            var isHost = net != null && net.role == "host";

            // Course + class pickers: host-only through the availability
            // layer — a client sees the same buttons disabled with the
            // honest hostOnly reason instead of a hidden affordance.
            var eventIds = app != null && app.Service != null
                ? app.Service.LanEventIds()
                : (IReadOnlyList<string>)new List<string>();
            var eventId = descriptor != null && !string.IsNullOrEmpty(descriptor.eventId)
                ? descriptor.eventId
                : (eventIds.Count > 0 ? eventIds[0] : "");
            var classIds = eventId.Length > 0
                ? app.Service.LanClassIds(eventId)
                : (IReadOnlyList<string>)new List<string>();
            var classId = descriptor != null && !string.IsNullOrEmpty(descriptor.classId)
                ? descriptor.classId
                : (classIds.Count > 0 ? classIds[0] : "");

            var courseRow = Row(root);
            AddLabel(courseRow, T("lan.course", state), 22, Deep);
            foreach (var id in eventIds)
            {
                var chosen = id;
                var button = AddButton(courseRow, EventName(id, state),
                    chosen == eventId ? Coral : Ocean,
                    () => ConfigureLan(chosen,
                        classIdFor(chosen, classId)), "lan.course." + id);
                ApplyAvailability(button, "lan.configure");
            }
            var classRow = Row(root);
            AddLabel(classRow, T("lan.class", state), 22, Deep);
            foreach (var id in classIds)
            {
                var chosen = id;
                var button = AddButton(classRow, ClassName(id, state),
                    chosen == classId ? Coral : Ocean,
                    () => ConfigureLan(eventId, chosen), "lan.class." + id);
                ApplyAvailability(button, "lan.configure");
            }

            // Vehicle picker: every participant picks their own from the
            // configured class's legal set.
            var vehicleRow = Row(root);
            AddLabel(vehicleRow, T("lan.vehicle", state), 22, Deep);
            var vehicles = eventId.Length > 0 && classId.Length > 0
                ? app.Service.LanVehicleIds(eventId, classId)
                : (IReadOnlyList<string>)new List<string>();
            var localId = net != null ? net.localParticipantId : "";
            var myVehicle = "";
            if (net != null)
                foreach (var p in net.participants)
                    if (p.occupied && p.participantId == localId)
                        myVehicle = p.vehicleId;
            foreach (var id in vehicles)
            {
                var chosen = id;
                var p = new Dictionary<string, string>();
                p["vehicleId"] = chosen;
                var button = AddButton(vehicleRow, VehicleName(id, state),
                    chosen == myVehicle ? Coral : Ocean,
                    () => app.Dispatch("lan.vehicle", p), "lan.vehicle." + id);
                ApplyAvailability(button, "lan.vehicle");
            }

            // Roster: four slots, always rendered, with readable
            // ownership/connection/AI state per slot.
            var roster = Row(root);
            AddLabel(roster, T("lan.players", state), 22, Deep);
            if (net != null)
            {
                for (var i = 0; i < net.participants.Count; i++)
                {
                    var p = net.participants[i];
                    var card = new VisualElement();
                    card.name = "lan.slot." + p.slot;
                    card.style.backgroundColor = Cream;
                    card.style.minWidth = 170;
                    card.style.marginLeft = card.style.marginRight = 6;
                    card.style.paddingTop = card.style.paddingBottom = 4;
                    if (!p.occupied)
                    {
                        var empty = AddLabel(card, T("lan.empty", state), 20, Off);
                        empty.name = "lan.slot." + p.slot + ".name";
                        var none = AddLabel(card, "", 18, Off);
                        none.name = "lan.slot." + p.slot + ".status";
                    }
                    else
                    {
                        var tags = p.participantId == localId
                            ? " " + T("lan.you", state) : "";
                        if (p.slot == 0 && isHost)
                            tags += " · " + T("lan.hostTag", state);
                        // PCQA-008: the primary label is the participant's
                        // published identity, never the raw UUID — the id
                        // stays on the tooltip for diagnostics.
                        var name = AddLabel(card,
                            ParticipantName(p, state) + tags, 20, Deep);
                        name.name = "lan.slot." + p.slot + ".name";
                        name.tooltip = p.participantId ?? "";
                        var statusText = p.controlSource == "ai"
                            ? T("lan.ai", state)
                            : (!p.connected
                                ? T("lan.offline", state)
                                : (p.ready
                                    ? T("lan.ready", state)
                                    : T("lan.notReady", state)));
                        var status = AddLabel(card, statusText, 18,
                            p.connected ? Deep : Coral);
                        status.name = "lan.slot." + p.slot + ".status";
                        var vehicle = AddLabel(card,
                            VehicleName(p.vehicleId, state), 16, Off);
                        vehicle.name = "lan.slot." + p.slot + ".vehicle";
                    }
                    roster.Add(card);
                }
            }

            var actions = Row(root);
            var ready = AddButton(actions, T("lan.ready", state), Seafoam,
                () => app.Dispatch("lan.ready",
                    ReadyParam(net, localId)), "lan.ready");
            ApplyAvailability(ready, "lan.ready");
            var start = AddButton(actions, T("lan.start", state), Coral,
                () => app.Dispatch("lan.start", emptyParams), "lan.start");
            ApplyAvailability(start, "lan.start");
            var leave = AddButton(actions, T("lan.leave", state), Deep,
                () => app.Dispatch("lan.leave", emptyParams), "lan.leave");
            ApplyAvailability(leave, "lan.leave");

            LanStatus(state, isHost ? "" : T("lan.waitHost", state));
        }

        private string classIdFor(string eventId, string currentClassId)
        {
            // A course switch keeps the current class when the new event
            // still offers it, else the event's first class.
            var classes = app.Service.LanClassIds(eventId);
            foreach (var id in classes)
                if (id == currentClassId) return id;
            return classes.Count > 0 ? classes[0] : (currentClassId ?? "");
        }

        private void ConfigureLan(string eventId, string classId)
        {
            var p = new Dictionary<string, string>();
            p["eventId"] = eventId ?? "";
            p["classId"] = classId ?? "";
            app.Dispatch("lan.configure", p);
        }

        private static Dictionary<string, string> ReadyParam(
            NetworkSessionState net, string localId)
        {
            var ready = false;
            if (net != null)
                foreach (var p in net.participants)
                    if (p.occupied && p.participantId == localId)
                        ready = !p.ready;
            var result = new Dictionary<string, string>();
            result["ready"] = ready ? "true" : "false";
            return result;
        }

        // Readable participant identity (PCQA-008): the wire already
        // carries each participant's profile animal, so the roster names
        // people through the same profile.animal.* keys the profile
        // cards use. An empty or unknown avatar falls back to a
        // localized slot-based Player N instead of leaking the UUID.
        private string ParticipantName(ParticipantState p,
            FoundationState state)
        {
            if (p != null && !string.IsNullOrEmpty(p.avatar))
            {
                try { return T("profile.animal." + p.avatar, state); }
                catch (KeyNotFoundException) { }
                catch (ArgumentException) { }
            }
            var slot = p != null ? p.slot : -1;
            return T("lan.player", state) + " "
                + (slot + 1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
        }

        // Id -> name resolution for surfaces that only carry a
        // participantId (race standings, result rows, the delivery
        // ledger): the live roster owns the avatar, so names resolve
        // through it; a participant the roster no longer lists falls
        // back to the raw id honestly rather than inventing a name.
        private string ParticipantNameForId(NetworkSessionState net,
            string participantId, FoundationState state)
        {
            if (net != null && participantId != null)
                foreach (var p in net.participants)
                    if (p.occupied && p.participantId == participantId)
                        return ParticipantName(p, state);
            return participantId ?? "";
        }

        private string EventName(string eventId, FoundationState state)
        {
            string text;
            try { text = T("lan.event." + eventId, state); }
            catch (KeyNotFoundException) { return eventId ?? ""; }
            catch (ArgumentException) { return eventId ?? ""; }
            return text;
        }

        private string ClassName(string classId, FoundationState state)
        {
            string text;
            try { text = T("lan.class." + classId, state); }
            catch (KeyNotFoundException) { return classId ?? ""; }
            catch (ArgumentException) { return classId ?? ""; }
            return text;
        }

        // --- lan-race ------------------------------------------------------
        // Transparent HUD over the rendered course: shared clock, lap,
        // standings, countdown, shared-pause notice, one-shot action
        // acknowledgments and the same held controls as solo driving —
        // routed through input.set into the authoritative network race.

        private void BuildLanRace(FoundationState state)
        {
            var top = Row(root);
            // Same over-world exposure as the solo HUD (PCQA-011): the
            // shared wash keeps the readouts readable on dark geometry.
            top.name = "lan.hud.readouts";
            HudStrip(top);
            lanTimeLabel = AddLabel(top, "", 28, Deep);
            lanTimeLabel.name = "lan.hud.time";
            lanStandingsLabel = AddLabel(top, "", 22, Deep);
            lanStandingsLabel.name = "lan.hud.standings";
            lanPhaseLabel = AddLabel(top, "", 32, Coral);
            lanPhaseLabel.name = "lan.hud.phase";
            lanPauseLabel = AddLabel(top, "", 22, Coral);
            lanPauseLabel.name = "lan.hud.paused";
            // Held-gadget readout (M5 Task 5) — same shared field the
            // solo HUD uses; shows the host-published held item.
            gadgetLabel = AddLabel(top, "", 22, Coral);
            gadgetLabel.name = "lan.hud.gadget";

            lanActionLabel = AddLabel(root, "", 18, Seafoam);
            lanActionLabel.name = "lan.hud.action";

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1f;
            root.Add(spacer);

            // Physical steering arrows keep left/right meaning in RTL:
            // the row is locked LTR exactly like the solo HUD.
            var controls = Row(root);
            controls.name = "lan.controls";
            controls.languageDirection = LanguageDirection.LTR;
            AddHoldButton(controls, "◀", Ocean,
                held => app.Input.TouchLeft(held), "lan.race.steerLeft");
            AddHoldButton(controls, "▶", Ocean,
                held => app.Input.TouchRight(held), "lan.race.steerRight");
            AddHoldButton(controls, T("input.brake", state), Deep,
                held => app.Input.TouchBrake(held), "lan.race.brake");
            AddHoldButton(controls, T("input.drift", state), Seafoam,
                held => app.Input.TouchDrift(held), "lan.race.drift");
            AddHoldButton(controls, T("input.boost", state), Coral,
                held => app.Input.TouchBoost(held), "lan.race.boost");
            foreach (var held in controls.Children())
                ApplyAvailability(held, "input.set");

            var keys = AddLabel(root, T("hud.keys", state), 15, Deep);
            keys.name = "lan.hud.keys";
            keys.style.opacity = 0.75f;
            HudStrip(keys);

            var actions = Row(root);
            var recover = AddButton(actions, T("input.recovery", state), Deep,
                () => app.Input.TouchRecover(), "lan.race.recover");
            // The gadget bit rides the next input frame to the host —
            // the edge latch lives in the authoritative rules.
            var gadget = AddButton(actions, T("input.gadget", state), Seafoam,
                () => app.Input.TouchGadget(), "lan.race.gadget");
            var camera = AddButton(actions, T("settings.camera", state), Ocean,
                () => app.Dispatch("camera.toggle", emptyParams), "lan.race.camera");
            var pause = AddButton(actions, T("input.pause", state), Coral,
                () => app.Dispatch("pause.toggle", emptyParams), "lan.race.pause");
            var leave = AddButton(actions, T("lan.leave", state), Deep,
                () => app.Dispatch("lan.leave", emptyParams), "lan.race.leave");
            ApplyAvailability(recover, "input.set");
            ApplyAvailability(gadget, "input.set");
            ApplyAvailability(camera, "camera.toggle");
            ApplyAvailability(pause, "pause.toggle");
            ApplyAvailability(leave, "lan.leave");

            statusLabel = AddLabel(root, "", 18, Deep);
            statusLabel.name = "status.persistence";
        }

        // Per-refresh update that must never rebuild: standings, clock,
        // countdown and the action acknowledgment line change every tick.
        private void UpdateLanRaceIndicators(FoundationState state)
        {
            var race = state.networkRace;
            var net = state.network;
            if (race != null)
            {
                if (lanTimeLabel != null)
                    lanTimeLabel.text = T("hud.time", state) + " "
                        + FormatRaceTime(race.elapsedTicks);
                if (lanPhaseLabel != null)
                    lanPhaseLabel.text =
                        race.phase == "countdown" && race.countdownTicksRemaining > 0
                            ? ((race.countdownTicksRemaining
                                    + LanSettings.SimulationTicksPerSecond - 1)
                                / LanSettings.SimulationTicksPerSecond).ToString()
                            : (race.phase == "finished" ? T("hud.finished", state) : "");
                if (lanStandingsLabel != null && race.racers != null)
                {
                    var text = "";
                    var ids = "";
                    var ordered = race.standings != null && race.standings.Count > 0
                        ? race.standings : null;
                    var shown = 0;
                    if (ordered != null)
                        foreach (var slotIndex in ordered)
                        {
                            if (slotIndex < 0 || slotIndex >= race.racers.Count)
                                continue;
                            var racer = race.racers[slotIndex];
                            if (racer == null) continue;
                            if (shown > 0) text += "  ";
                            // Roster-resolved animal names, not UUIDs;
                            // the shown ids ride the tooltip for
                            // diagnostics.
                            text += ++shown + "."
                                + ParticipantNameForId(net,
                                    racer.participantId, state)
                                + (racer.dnf ? " DNF" : "");
                            if (ids.Length > 0) ids += ", ";
                            ids += racer.participantId ?? "";
                        }
                    lanStandingsLabel.text = text;
                    lanStandingsLabel.tooltip = ids;
                }
            }
            // Shared pause plus who asked for it — a client sees its own
            // request named instead of silently waiting.
            if (lanPauseLabel != null)
            {
                var paused = net != null && net.paused;
                var requests = net != null ? net.pauseRequests.Count : 0;
                var text = paused ? T("lan.sharedPaused", state) : "";
                if (requests > 0 && net != null)
                    text += (text.Length > 0 ? " · " : "")
                        + string.Join(", ", net.pauseRequests) + " "
                        + T("lan.pausedBy", state);
                lanPauseLabel.text = text;
            }
            // One-shot action acknowledgment: the newest action.* event
            // for the local participant proves the press reached the host
            // (queued), fired (consumed) or was honestly cancelled.
            if (gadgetLabel != null)
                gadgetLabel.text = GadgetSlotText(state);
            if (lanActionLabel != null)
            {
                var localId = net != null ? net.localParticipantId : "";
                string latest = null;
                if (state.drivingEvents != null)
                    foreach (var ev in state.drivingEvents)
                        if (ev != null && ev.participantId == localId
                            && ev.kind != null
                            && ev.kind.StartsWith("action.", StringComparison.Ordinal))
                            latest = ev.kind;
                lanActionLabel.text = latest == null
                    ? "" : "\u200E" + latest;
            }
        }

        // --- lan-results ---------------------------------------------------
        // Immutable standings plus the honest delivery ledger: pending /
        // failed / persisted are shown, never implied; the host can retry
        // delivery offers and a client can retry its own persistence.

        private void BuildLanResults(FoundationState state)
        {
            AddLabel(root, T("lan.results.title", state), 40, Deep);
            var race = state.networkRace;
            var net = state.network;
            var isHost = net != null && net.role == "host";
            if (race != null && race.racers != null)
            {
                var ordered = race.standings != null && race.standings.Count > 0
                    ? race.standings : null;
                var shown = 0;
                if (ordered != null)
                    foreach (var slotIndex in ordered)
                    {
                        if (slotIndex < 0 || slotIndex >= race.racers.Count)
                            continue;
                        var racer = race.racers[slotIndex];
                        if (racer == null) continue;
                        shown++;
                        var row = Row(root);
                        row.name = "lan.result." + (shown - 1);
                        var line = "\u200E" + shown + ". "
                            + ParticipantNameForId(net,
                                racer.participantId, state) + " — "
                            + VehicleName(racer.vehicleId, state);
                        line += racer.dnf ? " DNF"
                            : (racer.finished
                                ? " " + FormatRaceTime(racer.finishTicks) : "");
                        var label = AddLabel(row, line, 22, Deep);
                        label.languageDirection = LanguageDirection.LTR;
                        label.name = "lan.result." + (shown - 1) + ".line";
                        label.tooltip = racer.participantId ?? "";
                    }
            }

            // Result persistence: the host's retained ledger or the
            // client's own deliveries, rendered with honest state.
            if (net != null && net.results != null)
            {
                for (var i = 0; i < net.results.Count; i++)
                {
                    var delivery = net.results[i];
                    var row = Row(root);
                    row.name = "lan.delivery." + i;
                    var text = delivery.state == "persisted" ? T("lan.saved", state)
                        : (delivery.state == "failed" ? T("lan.saveFailed", state)
                        : T("lan.saving", state));
                    var label = AddLabel(row,
                        "\u200E" + ParticipantNameForId(net,
                            delivery.participantId, state) + ": " + text,
                        18, delivery.state == "failed" ? Coral : Deep);
                    label.languageDirection = LanguageDirection.LTR;
                    label.name = "lan.delivery." + i + ".line";
                    label.tooltip = delivery.participantId ?? "";
                }
            }

            var actions = Row(root);
            var retrySave = AddButton(actions, T("lan.retrySave", state), Coral,
                () => app.Dispatch("lan.results.retrySave", emptyParams),
                "lan.results.retrySave");
            ApplyAvailability(retrySave, "lan.results.retrySave");
            // lan.results.request requires the race identity: take it from
            // the live race slice first, then the delivery ledger so a
            // client that missed the race state can still ask the host.
            var ask = AddButton(actions, T("lan.results.title", state), Ocean,
                () => app.Dispatch("lan.results.request", ResultParams(state)),
                "lan.results.request");
            ApplyAvailability(ask, "lan.results.request");
            var rematch = AddButton(actions, T("lan.rematch", state), Seafoam,
                () => app.Dispatch("lan.rematch", emptyParams), "lan.rematch");
            ApplyAvailability(rematch, "lan.rematch");
            var leave = AddButton(actions, T("lan.leave", state), Deep,
                () => app.Dispatch("lan.leave", emptyParams), "lan.leave");
            ApplyAvailability(leave, "lan.leave");

            LanStatus(state, isHost ? "" : T("lan.waitHost", state));
        }

        // --- lan-recovery ---------------------------------------------------
        // The honest recovery surface: what happened (safe error code),
        // a reconnect attempt and a clean way out. Credentials stay
        // private to the session service — the screen never sees them.

        private void BuildLanRecovery(FoundationState state)
        {
            AddLabel(root, T("lan.recovery.title", state), 40, Coral);
            var net = state.network;
            var reconnecting = net != null && net.phase == "reconnecting";
            var status = AddLabel(root, reconnecting
                ? T("lan.reconnecting", state)
                : ReasonText(net != null ? net.lastError : "", state), 22, Deep);
            status.name = "lan.recovery.status";
            var actions = Row(root);
            var reconnect = AddButton(actions, T("lan.reconnect", state), Seafoam,
                () => app.Dispatch("lan.reconnect", emptyParams), "lan.reconnect");
            ApplyAvailability(reconnect, "lan.reconnect");
            var leave = AddButton(actions, T("lan.leave", state), Deep,
                () => app.Dispatch("lan.leave", emptyParams), "lan.leave");
            ApplyAvailability(leave, "lan.leave");
            LanStatus(state, "");
        }

        // Routes every lan-* screen to its builder; the phase-derived
        // screens are only reachable while a session is active.
        private void BuildLanScreen(FoundationState state)
        {
            switch (state.screen)
            {
                case "lan-lobby": BuildLanLobby(state); break;
                case "lan-race": BuildLanRace(state); break;
                case "lan-results": BuildLanResults(state); break;
                case "lan-recovery": BuildLanRecovery(state); break;
                default: BuildLanBrowser(state); break;
            }
        }
    }
}
