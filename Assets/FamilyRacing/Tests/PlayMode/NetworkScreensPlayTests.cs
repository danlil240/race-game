using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FamilyRacing.Tests
{
    // Task 9 LAN widget-callback parity: every control on the five LAN
    // screens routes through the same lan.* commands the agent surface
    // exposes — synthetic UI Toolkit pointer events drive real service
    // state, and disabled controls carry the honest availability reason.
    // Transport is faked through the Core ILanSessionLink/
    // ILanDiscoveryProbe seams so host, client and recovery surfaces are
    // deterministic; wire-level realism lives in the lifecycle suite.
    // Editor-only fixture: the generated PanelSettings loads via
    // AssetDatabase, so standalone player/device runs must exclude it.
    [UnityPlatform(RuntimePlatform.WindowsEditor, RuntimePlatform.OSXEditor,
        RuntimePlatform.LinuxEditor)]
    public sealed class NetworkScreensPlayTests
    {
        private const string PanelSettingsPath =
            "Assets/FamilyRacing/Generated/FoundationPanelSettings.asset";

        // --- LAN fakes (mirrors of the EditMode fixture seams) -----------

        private sealed class FakeLanLink : ILanSessionLink
        {
            public int StartHostCalls;
            public int StartClientCalls;
            public int StopCalls;
            public string LastClientAddress;
            public int LastClientPort;
            public readonly Queue<string> Sent = new Queue<string>();

            public bool StartHost(ushort port, out ushort boundPort, out string error)
            {
                StartHostCalls++;
                boundPort = port == 0 ? (ushort)47777 : port;
                error = "";
                return true;
            }

            public bool StartClient(string address, ushort port, out string error)
            {
                StartClientCalls++;
                LastClientAddress = address;
                LastClientPort = port;
                error = "";
                return true;
            }

            public void Stop()
            {
                StopCalls++;
            }

            public bool Send(ulong connectionId, string payload)
            {
                Sent.Enqueue(payload);
                return true;
            }
        }

        private sealed class FakeLanProbe : ILanDiscoveryProbe
        {
            public int SearchStarts;
            public int SearchStops;
            public readonly List<LanDiscoveredHost> Hosts =
                new List<LanDiscoveredHost>();

            public bool StartSearch(out string error)
            {
                SearchStarts++;
                error = "";
                return true;
            }

            public void StopSearch()
            {
                SearchStops++;
            }

            public bool StartResponder(ushort gamePort, string hostName,
                string compat, out string error)
            {
                error = "";
                return true;
            }

            public void StopResponder() { }

            public IReadOnlyList<LanDiscoveredHost> Entries
            {
                get { return Hosts; }
            }
        }

        private string profilesDirectory;
        private FoundationService service;
        private FakeLanLink link;
        private FakeLanProbe probe;
        private LanSessionService lan;
        private GameObject hostGo;
        private FoundationApp app;
        private GameObject inputGo;
        private FoundationInput driverInput;
        private GameObject shellGo;
        private UIDocument document;
        private PanelSettings panelSettings;
        private FoundationShell shell;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            profilesDirectory = Path.Combine(Path.GetTempPath(),
                "fr-lan-ui-" + Guid.NewGuid().ToString("N"));
            var runId = "run-" + Guid.NewGuid().ToString("N");
            service = new FoundationService(
                new ProfileStore(profilesDirectory, new UnityProfileSerializer()),
                "en", runId);
            service.SetDrivingContent(Catalog());
            link = new FakeLanLink();
            probe = new FakeLanProbe();
            lan = new LanSessionService(link, probe, "gameplay-test");
            service.SetLanSession(lan);

            hostGo = new GameObject("lan-screens-host");
            hostGo.SetActive(false);
            app = hostGo.AddComponent<FoundationApp>();
            Inject(app, "service", service);
            Inject(app, "runId", runId);

            inputGo = new GameObject("lan-input-adapter");
            driverInput = inputGo.AddComponent<FoundationInput>();
            driverInput.Bind(app);
            Inject(app, "driverInput", driverInput);

#if UNITY_EDITOR
            panelSettings = UnityEngine.Object.Instantiate(
                UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath));
#endif
            Assert.That(panelSettings, Is.Not.Null,
                "The generated PanelSettings asset must load; the clone is destroyed in teardown.");

            shellGo = new GameObject("lan-screens-shell");
            shellGo.SetActive(false);
            document = shellGo.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            shell = shellGo.AddComponent<FoundationShell>();
            shellGo.SetActive(true);
            shell.Bind(app);
            yield return WaitFor(() => document.rootVisualElement != null
                && document.rootVisualElement.panel != null, "panel attach");
        }

        [TearDown]
        public void TearDown()
        {
            if (shellGo != null) UnityEngine.Object.Destroy(shellGo);
            if (inputGo != null) UnityEngine.Object.Destroy(inputGo);
            if (hostGo != null) UnityEngine.Object.Destroy(hostGo);
            if (panelSettings != null) UnityEngine.Object.DestroyImmediate(panelSettings);
            try
            {
                if (Directory.Exists(profilesDirectory))
                    Directory.Delete(profilesDirectory, true);
            }
            catch (Exception) { }
        }

        // --- helpers --------------------------------------------------------

        private static void Inject(FoundationApp target, string field, object value)
        {
            typeof(FoundationApp).GetField(field,
                BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
        }

        private FoundationState Snapshot()
        {
            return app.Service.Snapshot();
        }

        private void Refresh()
        {
            shell.Refresh(Snapshot());
        }

        private T Widget<T>(string name) where T : VisualElement
        {
            var root = document.rootVisualElement;
            return root == null ? null : root.Q<T>(name);
        }

        private IEnumerator WaitFor(Func<bool> done, string what, float timeout = 5f)
        {
            var until = Time.unscaledTime + timeout;
            while (Time.unscaledTime < until && !done())
                yield return null;
            Assert.That(done(), Is.True, "Timed out waiting for " + what + ".");
        }

        private IEnumerator WaitForWidget<T>(string name) where T : VisualElement
        {
            yield return WaitFor(() =>
            {
                var widget = Widget<T>(name);
                return widget != null && widget.worldBound.width > 0f
                    && widget.worldBound.height > 0f;
            }, "widget " + name);
        }

        private static Dictionary<string, string> Params(
            params (string name, string value)[] entries)
        {
            var map = new Dictionary<string, string>();
            foreach (var (name, value) in entries) map[name] = value;
            return map;
        }

        private static void Press(Button button)
        {
            var center = button.worldBound.center;
            var touch = new Touch
            {
                fingerId = 0,
                position = center,
                phase = TouchPhase.Began
            };
            using (var down = PointerDownEvent.GetPooled(touch, EventModifiers.None))
            {
                down.target = button;
                button.SendEvent(down);
            }
            touch.phase = TouchPhase.Ended;
            using (var up = PointerUpEvent.GetPooled(touch, EventModifiers.None))
            {
                up.target = button;
                button.SendEvent(up);
            }
        }

        private static Touch TouchAt(VisualElement element, TouchPhase phase)
        {
            return new Touch
            {
                fingerId = 0,
                position = element.worldBound.center,
                phase = phase
            };
        }

        private static void PointerDownOn(VisualElement element)
        {
            using (var down = PointerDownEvent.GetPooled(
                TouchAt(element, TouchPhase.Began), EventModifiers.None))
            {
                down.target = element;
                element.SendEvent(down);
            }
        }

        private static void PointerUpOn(VisualElement element)
        {
            using (var up = PointerUpEvent.GetPooled(
                TouchAt(element, TouchPhase.Ended), EventModifiers.None))
            {
                up.target = element;
                element.SendEvent(up);
            }
        }

        private static DrivingContentCatalog Catalog()
        {
            var course = new CourseDefinition
            {
                id = "course-lan",
                checkpoints = new List<CheckpointGate>
                {
                    new CheckpointGate
                    {
                        id = "start", x = 0f, y = 0f, z = 0f,
                        halfWidth = 6f, dirX = 0f, dirZ = 1f,
                        recoverX = 0f, recoverZ = -3f
                    },
                    new CheckpointGate
                    {
                        id = "gate-1", x = 0f, y = 0f, z = 2000f,
                        halfWidth = 6f, dirX = 0f, dirZ = 1f,
                        recoverX = 0f, recoverZ = 1997f
                    }
                },
                spawnSlots = new List<MotorPose>
                {
                    new MotorPose { x = -6f, z = 0f },
                    new MotorPose { x = -2f, z = 0f },
                    new MotorPose { x = 2f, z = 0f },
                    new MotorPose { x = 6f, z = 0f }
                }
            };
            var mixed = new EventDefinition
            {
                id = "lan-mixed",
                courseId = "course-lan",
                laps = 1,
                countdownTicks = 8,
                vehicleIds = new List<string> { "car-01", "motorcycle-01" }
            };
            var extra = new EventDefinition
            {
                id = "lan-cars",
                courseId = "course-lan",
                laps = 2,
                countdownTicks = 8,
                vehicleIds = new List<string> { "car-01" }
            };
            // A solo-only event: race.start needs real solo content so the
            // raceActive LAN-entry gate is exercised by a live race, not
            // by a start that could never apply.
            var solo = new EventDefinition
            {
                id = "intro-01",
                courseId = "course-lan",
                laps = 1,
                countdownTicks = 8,
                vehicleIds = new List<string> { "car-01" }
            };
            return DrivingContentCatalog.Create("lan-content-1",
                new[] { VehicleTuning.Car01(), VehicleTuning.Motorcycle01() },
                new[] { course }, new[] { mixed, extra, solo });
        }

        // Async join/reconnect completions land on the next boundary the
        // way FoundationApp.DrainNetworkBoundary applies them.
        private void DrainOutcomes()
        {
            foreach (var outcome in service.DrainNetworkOutcomes())
                service.CompleteNetworkCommand(outcome);
        }

        private void CreateProfileAndNavigate(string screen)
        {
            var created = app.Dispatch("profile.create",
                Params(("slot", "0"), ("language", "en")));
            Assert.That(created.status, Is.EqualTo("applied"));
            var nav = app.Dispatch("ui.navigate", Params(("screen", screen)));
            Assert.That(nav.status, Is.EqualTo("applied"));
        }

        private static string SessionStateMessage(string sessionId,
            string phase, string localId, string eventId = "lan-mixed",
            string classId = "car")
        {
            return NetworkProtocol.Encode(NetworkProtocol.MsgSessionState,
                "sid", sessionId, "ph", phase, "ev", eventId, "cl", classId,
                "pd", "0", "pr", "", "occ", "1", "jn", "0",
                "p0", string.Join(",", new[]
                {
                    localId, "0", "fox", "0", "car-01", "1", "0",
                    "remote", "1"
                }),
                "p1", "", "p2", "", "p3", "");
        }

        private IEnumerator JoinAsClient()
        {
            CreateProfileAndNavigate("lan-browser");
            Refresh();
            yield return WaitForWidget<Button>("lan.join");
            var address = Widget<TextField>("lan.join.address");
            var port = Widget<TextField>("lan.join.port");
            Assert.That(address, Is.Not.Null);
            Assert.That(port, Is.Not.Null);
            address.value = "10.9.8.7";
            port.value = "48888";
            Press(Widget<Button>("lan.join"));
            Assert.That(Snapshot().network.phase, Is.EqualTo("connecting"));
            Assert.That(link.LastClientAddress, Is.EqualTo("10.9.8.7"));
            Assert.That(link.LastClientPort, Is.EqualTo(48888));

            lan.OnLinkConnected(3);
            link.Sent.Clear();
            var joinOp = Snapshot().network.pendingOperations;
            Assert.That(joinOp.Count, Is.GreaterThan(0));
            lan.OnLinkMessage(3, NetworkProtocol.Encode(
                NetworkProtocol.MsgJoinAccept,
                "sid", "s-test", "ph", "lobby", "pid",
                Snapshot().savedProfile.id,
                "slot", "1", "ev", "lan-mixed", "cl", "car",
                "ep", "1", "cred", "secret-test",
                "cid", joinOp[joinOp.Count - 1].commandId));
            DrainOutcomes();
            Refresh();
            yield return WaitForWidget<Button>("lan.ready");
        }

        // --- browser tests -------------------------------------------------

        [UnityTest]
        public IEnumerator BrowserHostButtonHostsAndLandsOnLobby()
        {
            CreateProfileAndNavigate("lan-browser");
            Refresh();
            yield return WaitForWidget<Button>("lan.host");
            Assert.That(Widget<Button>("lan.discover"), Is.Not.Null);
            Assert.That(Widget<Button>("lan.discovery.stop"), Is.Not.Null);
            Assert.That(Widget<TextField>("lan.join.address"), Is.Not.Null);
            // Without a session, leave is disabled with the honest reason.
            var leave = Widget<Button>("lan.leave");
            Assert.That(leave.enabledSelf, Is.False);
            Assert.That(leave.tooltip, Is.EqualTo("noSession"));

            Press(Widget<Button>("lan.host"));
            Assert.That(link.StartHostCalls, Is.EqualTo(1));
            var state = Snapshot();
            Assert.That(state.network.role, Is.EqualTo("host"));
            Assert.That(state.network.phase, Is.EqualTo("lobby"));
            Assert.That(state.screen, Is.EqualTo("lan-lobby"),
                "lan.host must land on the phase-derived lobby screen");
            Refresh();
            yield return WaitForWidget<Button>("lan.ready");
            Assert.That(Widget<Button>("lan.start"), Is.Not.Null);
            Assert.That(Widget<VisualElement>("lan.slot.0"), Is.Not.Null,
                "The local participant's roster card must render");
            var name = Widget<Label>("lan.slot.0.name");
            Assert.That(name, Is.Not.Null);
            Assert.That(name.text, Does.Contain("You"),
                "The local participant's card is marked as local");
        }

        [UnityTest]
        public IEnumerator BrowserDiscoveryControlsToggleAndJoinCards()
        {
            CreateProfileAndNavigate("lan-browser");
            Refresh();
            yield return WaitForWidget<Button>("lan.discover");

            Press(Widget<Button>("lan.discover"));
            Assert.That(probe.SearchStarts, Is.EqualTo(1));
            Assert.That(Snapshot().network.phase, Is.EqualTo("discovering"));

            // A discovered host renders a card that joins its address —
            // and only a same-compat advertisement may join at all.
            var advertised = Catalog();
            probe.Hosts.Add(new LanDiscoveredHost
            {
                address = "192.168.1.50", port = 47777, hostName = "peer",
                compat = "gameplay-test|" + advertised.CompatibilityVersion
                    + "|" + advertised.CompatibilityFingerprint
            });
            Refresh();
            yield return WaitForWidget<Button>("lan.discovered.0.join");
            var card = Widget<VisualElement>("lan.discovered.0");
            Assert.That(card, Is.Not.Null);
            Assert.That(Widget<Label>("lan.discovered.0.address").text,
                Does.Contain("192.168.1.50"));

            Press(Widget<Button>("lan.discovered.0.join"));
            Assert.That(link.LastClientAddress, Is.EqualTo("192.168.1.50"));
            Assert.That(link.LastClientPort, Is.EqualTo(47777));
            Assert.That(Snapshot().network.phase, Is.EqualTo("connecting"));

            // Cancelling mid-connect leaves honestly back to the browser.
            // The rebuild re-enables lan.leave, so refresh before pressing.
            Refresh();
            yield return WaitForWidget<Button>("lan.leave");
            Press(Widget<Button>("lan.leave"));
            var state = Snapshot();
            Assert.That(state.network.role, Is.EqualTo("none"));
            Assert.That(state.screen, Is.EqualTo("lan-browser"));

            // Back on the idle browser, discovery toggles again — each
            // phase change needs a refresh so the pressed widget is live.
            Refresh();
            yield return WaitForWidget<Button>("lan.discover");
            Press(Widget<Button>("lan.discover"));
            Refresh();
            yield return WaitForWidget<Button>("lan.discovery.stop");
            Press(Widget<Button>("lan.discovery.stop"));
            Assert.That(probe.SearchStops, Is.EqualTo(2));
            Assert.That(Snapshot().network.phase, Is.EqualTo("idle"));
        }

        // --- lobby tests ----------------------------------------------------

        private IEnumerator HostLobby()
        {
            CreateProfileAndNavigate("lan-browser");
            Refresh();
            yield return WaitForWidget<Button>("lan.host");
            Press(Widget<Button>("lan.host"));
            Refresh();
            yield return WaitForWidget<Button>("lan.ready");
        }

        [UnityTest]
        public IEnumerator LobbyConfigurationVehicleReadyStartFlow()
        {
            yield return HostLobby();
            var state = Snapshot();
            Assert.That(state.screen, Is.EqualTo("lan-lobby"));

            // The course/class pickers dispatch lan.configure with the
            // tapped identity; the roster reflects what the service kept.
            var course = Widget<Button>("lan.course.lan-mixed");
            Assert.That(course, Is.Not.Null, "lan-* events must be offered");
            Press(course);
            state = Snapshot();
            Assert.That(state.network.descriptor.eventId, Is.EqualTo("lan-mixed"));
            Assert.That(state.network.descriptor.classId, Is.EqualTo("car"));
            Refresh();
            yield return WaitForWidget<Button>("lan.vehicle.car-01");
            Press(Widget<Button>("lan.vehicle.car-01"));
            Assert.That(Snapshot().network.participants[0].vehicleId,
                Is.EqualTo("car-01"));

            Press(Widget<Button>("lan.ready"));
            Assert.That(Snapshot().network.participants[0].ready, Is.True);
            Refresh();

            // Start: all occupied seats ready -> countdown begins and the
            // screen follows the phase onto the race HUD. The rebuild must
            // lay out before the start button can take a pointer.
            yield return WaitForWidget<Button>("lan.start");
            Press(Widget<Button>("lan.start"));
            state = Snapshot();
            Assert.That(state.network.phase, Is.EqualTo("countdown"));
            Assert.That(state.screen, Is.EqualTo("lan-race"));
        }

        [UnityTest]
        public IEnumerator LobbyLeaveReturnsToBrowser()
        {
            yield return HostLobby();
            Press(Widget<Button>("lan.leave"));
            var state = Snapshot();
            Assert.That(state.network.role, Is.EqualTo("none"));
            Assert.That(state.network.phase, Is.EqualTo("idle"));
            Assert.That(state.screen, Is.EqualTo("lan-browser"));
        }

        // --- race HUD tests --------------------------------------------------

        private IEnumerator HostRacing()
        {
            yield return HostLobby();
            Press(Widget<Button>("lan.course.lan-cars"));
            Press(Widget<Button>("lan.vehicle.car-01"));
            Press(Widget<Button>("lan.ready"));
            Press(Widget<Button>("lan.start"));
            Refresh();
            yield return WaitForWidget<Button>("lan.race.steerLeft");
        }

        [UnityTest]
        public IEnumerator LanRaceHudPresentsSharedStateAndControls()
        {
            yield return HostRacing();
            var state = Snapshot();
            Assert.That(state.screen, Is.EqualTo("lan-race"));
            Assert.That(Widget<Label>("lan.hud.time"), Is.Not.Null);
            Assert.That(Widget<Label>("lan.hud.standings"), Is.Not.Null);
            Assert.That(Widget<Label>("lan.hud.phase"), Is.Not.Null);
            Assert.That(Widget<Label>("lan.hud.paused"), Is.Not.Null);
            Assert.That(Widget<Label>("lan.hud.action"), Is.Not.Null);
            // The controls row stays LTR so arrow glyphs never mirror.
            var controls = Widget<VisualElement>("lan.controls");
            Assert.That(controls.languageDirection,
                Is.EqualTo(LanguageDirection.LTR));
            Assert.That(Widget<Button>("lan.race.brake"), Is.Not.Null);
            Assert.That(Widget<Button>("lan.race.drift"), Is.Not.Null);
            Assert.That(Widget<Button>("lan.race.boost"), Is.Not.Null);
            Assert.That(Widget<Button>("lan.race.recover"), Is.Not.Null);
            Assert.That(Widget<Button>("lan.race.pause"), Is.Not.Null);
            Assert.That(Widget<Button>("lan.race.leave"), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator LanRaceHoldButtonsDriveBoundedInput()
        {
            yield return HostRacing();
            var left = Widget<Button>("lan.race.steerLeft");
            var right = Widget<Button>("lan.race.steerRight");

            PointerDownOn(left);
            driverInput.Pump(false, "lan-race");
            Assert.That(Snapshot().input.steering, Is.EqualTo(-1f));
            PointerUpOn(left);
            PointerDownOn(right);
            driverInput.Pump(false, "lan-race");
            Assert.That(Snapshot().input.steering, Is.EqualTo(1f));
            PointerUpOn(right);
            driverInput.Pump(false, "lan-race");
            Assert.That(Snapshot().input.steering, Is.EqualTo(0f));

            // The brake is a held demand like steering: pointer-down
            // holds it, pointer-up releases it, and release dispatches
            // through the same input.set the steering row uses.
            var brake = Widget<Button>("lan.race.brake");
            PointerDownOn(brake);
            driverInput.Pump(false, "lan-race");
            Assert.That(Snapshot().input.brake, Is.EqualTo(1f));
            PointerUpOn(brake);
            driverInput.Pump(false, "lan-race");
            Assert.That(Snapshot().input.brake, Is.EqualTo(0f));
        }

        [UnityTest]
        public IEnumerator LanRaceRecoverPressesStayBounded()
        {
            yield return HostRacing();
            // Recovery is a one-shot action press, not a held control.
            Press(Widget<Button>("lan.race.recover"));
            driverInput.Pump(false, "lan-race");
            var state = Snapshot();
            // The input.set carrying the press applied; the recover flag
            // is consumed by the action path rather than latched in the
            // observable held-input slice.
            Assert.That(state.input.recover, Is.False);
            // M5 Task 5: the LAN HUD renders a live gadget button — the
            // press rides the next input frame to the host as a pending
            // request; only the host grants/spends items.
            var gadgetButton = Widget<Button>("lan.race.gadget");
            Assert.That(gadgetButton, Is.Not.Null);
            Assert.That(gadgetButton.enabledSelf, Is.True);
            Press(gadgetButton);
            driverInput.Pump(false, "lan-race");
            Assert.That(Snapshot().input.gadget, Is.True,
                "the LAN gadget press emits the same input.set bit");
        }

        // --- client / recovery tests ----------------------------------------

        [UnityTest]
        public IEnumerator ClientJoinLandsInLobbyWithHostOnlyControlsDisabled()
        {
            yield return JoinAsClient();
            var state = Snapshot();
            Assert.That(state.screen, Is.EqualTo("lan-lobby"));
            Assert.That(state.network.role, Is.EqualTo("client"));
            Assert.That(state.network.localParticipantId,
                Is.EqualTo(state.savedProfile.id));
            // Host-only controls are present but disabled with reasons —
            // a client sees the affordance and why it is unavailable.
            var start = Widget<Button>("lan.start");
            Assert.That(start.enabledSelf, Is.False);
            Assert.That(start.tooltip, Is.EqualTo("hostOnly"));
            var configure = Widget<Button>("lan.course.lan-mixed");
            Assert.That(configure, Is.Not.Null);
            Assert.That(configure.enabledSelf, Is.False);
            // Ready remains a real client control.
            var ready = Widget<Button>("lan.ready");
            Assert.That(ready.enabledSelf, Is.True);
            Press(ready);
            // The ready request is async: it sits in pendingOperations
            // until the host's op.result lands.
            Assert.That(Snapshot().network.pendingOperations.Count,
                Is.GreaterThan(0));
        }

        [UnityTest]
        public IEnumerator HostLossLandsOnRecoveryScreen()
        {
            yield return JoinAsClient();
            lan.OnLinkDisconnected(3, "");
            DrainOutcomes();
            var state = Snapshot();
            Assert.That(state.network.phase, Is.EqualTo("recovery"));
            Assert.That(state.screen, Is.EqualTo("lan-recovery"));
            Refresh();
            yield return WaitForWidget<Button>("lan.reconnect");
            var status = Widget<Label>("lan.recovery.status");
            Assert.That(status.text, Is.Not.Empty,
                "Recovery must surface a readable reason");
            Assert.That(state.network.lastError, Is.EqualTo("hostLost"));

            // Reconnect drives lan.reconnect — an async join-kind op.
            Press(Widget<Button>("lan.reconnect"));
            Assert.That(link.StartClientCalls, Is.EqualTo(2),
                "Reconnect reuses the stored endpoint");
            Assert.That(link.LastClientAddress, Is.EqualTo("10.9.8.7"));
            Assert.That(Snapshot().network.phase, Is.EqualTo("reconnecting"));

            // Leaving from recovery clears the session and returns to
            // the browser.
            Press(Widget<Button>("lan.leave"));
            Assert.That(Snapshot().network.role, Is.EqualTo("none"));
            Assert.That(Snapshot().screen, Is.EqualTo("lan-browser"));
        }

        // --- results tests ---------------------------------------------------

        [UnityTest]
        public IEnumerator ResultsPhaseRendersStandingsAndHonestDelivery()
        {
            yield return JoinAsClient();
            // A host session.state carrying phase=results moves the client
            // to the results surface on the next boundary.
            lan.OnLinkMessage(3, SessionStateMessage("s-test", "results",
                Snapshot().savedProfile.id));
            DrainOutcomes();
            var state = Snapshot();
            Assert.That(state.screen, Is.EqualTo("lan-results"));
            Refresh();
            yield return WaitForWidget<Button>("lan.results.retrySave");
            Assert.That(Widget<Button>("lan.results.request"), Is.Not.Null);
            // Rematch is host-only: present but honestly disabled.
            var rematch = Widget<Button>("lan.rematch");
            Assert.That(rematch.enabledSelf, Is.False);
            Assert.That(rematch.tooltip, Is.EqualTo("hostOnly"));
            Press(Widget<Button>("lan.leave"));
            Assert.That(Snapshot().screen, Is.EqualTo("lan-browser"));
        }

        private int CountSent(string type)
        {
            var count = 0;
            foreach (var payload in link.Sent)
                if (payload.Contains("|" + type + "|")
                    || payload.EndsWith("|" + type, StringComparison.Ordinal))
                    count++;
            return count;
        }

        [UnityTest]
        public IEnumerator LanRacePauseAndCameraButtonsDriveCommands()
        {
            yield return HostRacing();
            // Host pause: pause.toggle is the shared toggle — it pauses
            // everyone and resumes everyone.
            Press(Widget<Button>("lan.race.pause"));
            Assert.That(Snapshot().network.paused, Is.True,
                "lan.race.pause must dispatch pause.toggle");
            Press(Widget<Button>("lan.race.pause"));
            Assert.That(Snapshot().network.paused, Is.False);

            // Camera: the draft preference toggles so the LAN camera
            // applies the new choice on the next frame.
            var camera = Widget<Button>("lan.race.camera");
            Assert.That(Snapshot().draftProfile.camera, Is.EqualTo("chase"));
            Press(camera);
            Assert.That(Snapshot().draftProfile.camera,
                Is.EqualTo("overhead"), "lan.race.camera must dispatch camera.toggle");
            Press(Widget<Button>("lan.race.camera"));
            Assert.That(Snapshot().draftProfile.camera, Is.EqualTo("chase"));

            // Race leave tears the session down from the HUD too.
            Press(Widget<Button>("lan.race.leave"));
            Assert.That(Snapshot().network.role, Is.EqualTo("none"));
            Assert.That(Snapshot().screen, Is.EqualTo("lan-browser"));
        }

        [UnityTest]
        public IEnumerator ClientRacePauseSendsAHostRequest()
        {
            yield return JoinAsClient();
            // Move the fake session into racing so pause requests are legal.
            lan.OnLinkMessage(3, SessionStateMessage("s-test", "racing",
                Snapshot().savedProfile.id));
            DrainOutcomes();
            Assert.That(Snapshot().screen, Is.EqualTo("lan-race"));
            Refresh();
            yield return WaitForWidget<Button>("lan.race.pause");
            Press(Widget<Button>("lan.race.pause"));
            var state = Snapshot();
            Assert.That(state.network.pendingOperations.Count,
                Is.GreaterThan(0),
                "client pause is an async pause.request to the host");
            Assert.That(CountSent(NetworkProtocol.MsgPauseRequest),
                Is.EqualTo(1));
        }

        private RaceResult MakeResult(string resultId, string raceId,
            string participantId, string outcome, int coins)
        {
            return new RaceResult
            {
                resultId = resultId,
                sessionId = "s-test",
                raceId = raceId,
                eventId = "lan-cars",
                contentVersion = "lan-content-1",
                finalized = true,
                finalizedHostTick = 100,
                finalizedElapsedTicks = 900,
                completionCoins = 20,
                racers = new List<RaceResultEntry>
                {
                    new RaceResultEntry
                    {
                        position = 1, slot = 0,
                        participantId = participantId,
                        vehicleId = "car-01", attribution = "human",
                        outcome = outcome, finishTicks = outcome == "dnf" ? 0 : 900,
                        lapsCompleted = outcome == "dnf" ? 0 : 1,
                        coinsAwarded = coins
                    }
                }
            };
        }

        [UnityTest]
        public IEnumerator ResultsButtonsDriveRequestAndRematch()
        {
            yield return HostRacing();
            // Drive the coordinator seam the bridge uses: countdown ->
            // racing -> results, then publish the immutable result.
            Assert.That(lan.BeginRace(), Is.True);
            Assert.That(lan.FinishRace(), Is.True);
            var hostId = Snapshot().savedProfile.id;
            Assert.That(lan.PublishResult(
                MakeResult("res-1", "race-1", hostId, "finished", 20)),
                Is.True);
            DrainOutcomes();
            var state = Snapshot();
            Assert.That(state.network.phase, Is.EqualTo("results"));
            Assert.That(state.screen, Is.EqualTo("lan-results"));
            Assert.That(state.network.results.Count, Is.GreaterThan(0));
            Refresh();
            yield return WaitForWidget<Button>("lan.results.request");

            // Result request on the host is the local retrieval path —
            // applied, and the ledger stays put.
            Press(Widget<Button>("lan.results.request"));
            Assert.That(Snapshot().network.results.Count,
                Is.EqualTo(state.network.results.Count));
            Assert.That(Snapshot().network.lastError, Is.EqualTo(""));

            // Host rematch returns everyone to the lobby surface.
            Press(Widget<Button>("lan.rematch"));
            var after = Snapshot();
            Assert.That(after.network.phase, Is.EqualTo("lobby"));
            Assert.That(after.screen, Is.EqualTo("lan-lobby"));
        }

        [UnityTest]
        public IEnumerator ClientResultsRequestAndRetrySaveDriveCommands()
        {
            yield return JoinAsClient();
            var clientId = Snapshot().savedProfile.id;
            lan.OnLinkMessage(3, SessionStateMessage("s-test", "results",
                clientId));
            DrainOutcomes();
            // A failed local delivery: the offer names the client as a
            // DNF so the reward applier answers ineligible — persisted
            // status fails honestly and the retry affordance enables.
            lan.OnLinkMessage(3, NetworkProtocol.EncodeResultOffer(
                MakeResult("res-1", "race-1", clientId, "dnf", 0)));
            DrainOutcomes();
            var state = Snapshot();
            Assert.That(state.screen, Is.EqualTo("lan-results"));
            Assert.That(state.network.results.Count, Is.EqualTo(1));
            Assert.That(state.network.results[0].state, Is.EqualTo("failed"));
            var acks = CountSent(NetworkProtocol.MsgResultAck);
            Assert.That(acks, Is.GreaterThan(0), "the offer must be acked");
            Refresh();
            yield return WaitForWidget<Button>("lan.results.retrySave");
            var retry = Widget<Button>("lan.results.retrySave");
            Assert.That(retry.enabledSelf, Is.True,
                "a failed delivery must offer retry");

            // Result request is an async client op: pending until the
            // host's answer; the wire carries the immutable raceId.
            Press(Widget<Button>("lan.results.request"));
            var pending = Snapshot().network.pendingOperations;
            Assert.That(pending.Count, Is.GreaterThan(0),
                "lan.results.request must be an accepted async op");
            Assert.That(CountSent(NetworkProtocol.MsgResultRequest),
                Is.EqualTo(1));
            string lastSent = null;
            foreach (var payload in link.Sent) lastSent = payload;
            Assert.That(lastSent, Does.Contain("race=race-1"),
                "the request must name the retained race identity");

            // Retry re-applies the same immutable facts and acks again —
            // the outcome stays a failed delivery, honestly surfaced.
            Press(Widget<Button>("lan.results.retrySave"));
            Assert.That(CountSent(NetworkProtocol.MsgResultAck),
                Is.EqualTo(acks + 1), "retry must re-ack the delivery");
            Assert.That(Snapshot().network.results[0].state,
                Is.EqualTo("failed"));
        }

        // --- bilingual/RTL ---------------------------------------------------

        [UnityTest]
        public IEnumerator LanScreensRenderHebrewRtl()
        {
            CreateProfileAndNavigate("lan-browser");
            var draft = Snapshot().draftProfile;
            var updated = app.Dispatch("settings.update", Params(
                ("language", "he"), ("camera", draft.camera),
                ("reducedCameraShake", draft.reducedCameraShake ? "true" : "false"),
                ("musicVolume", "0.8"), ("effectsVolume", "0.8"),
                ("voiceVolume", "0.8")));
            Assert.That(updated.status, Is.EqualTo("applied"));
            app.Dispatch("settings.save", Params());
            Refresh();
            yield return WaitForWidget<Button>("lan.host");
            Assert.That(document.rootVisualElement.languageDirection,
                Is.EqualTo(LanguageDirection.RTL));
            var host = Widget<Button>("lan.host");
            Assert.That(host.text, Is.EqualTo("אירוח מירוץ"));
            Press(host);
            Refresh();
            yield return WaitForWidget<Button>("lan.ready");
            Assert.That(Widget<Button>("lan.ready").text, Is.EqualTo("מוכנים!"));
            Assert.That(Widget<Button>("lan.leave").text, Is.EqualTo("עזיבה"));
            // Participant identity stays LTR inside the RTL roster.
            var name = Widget<Label>("lan.slot.0.name");
            Assert.That(name.languageDirection,
                Is.EqualTo(LanguageDirection.LTR));
        }

        // --- solo-flow preservation -----------------------------------------

        [UnityTest]
        public IEnumerator SoloRaceBlocksLanEntryAndScreensStay()
        {
            CreateProfileAndNavigate("practice");
            var started = app.Dispatch("race.start",
                Params(("vehicleId", "car-01"), ("eventId", "intro-01")));
            // The fixture now carries real solo content: the race must
            // actually start so the raceActive gate is proven live.
            Assert.That(started.status, Is.EqualTo("applied"),
                "race.start must succeed for the raceActive gate to be tested");
            Assert.That(Snapshot().driving, Is.Not.Null);

            var host = app.Dispatch("lan.host", Params(("port", "47777")));
            Assert.That(host.status, Is.EqualTo("rejected"));
            Assert.That(host.code, Is.EqualTo("raceActive"),
                "A live solo race must refuse LAN session entry");
            var join = app.Dispatch("lan.join",
                Params(("address", "10.0.0.1"), ("port", "47777")));
            Assert.That(join.status, Is.EqualTo("rejected"));
            Assert.That(join.code, Is.EqualTo("raceActive"));
            var reconnect = app.Dispatch("lan.reconnect", Params());
            Assert.That(reconnect.code, Is.EqualTo("raceActive"));
            // The session never formed: still a solo race, no network role.
            Assert.That(Snapshot().network.role, Is.EqualTo("none"));
            Assert.That(link.StartHostCalls, Is.EqualTo(0));

            // Leaving the solo race reopens LAN entry.
            var exited = app.Dispatch("race.exit", Params());
            Assert.That(exited.status, Is.EqualTo("applied"));
            var hosted = app.Dispatch("lan.host", Params(("port", "47777")));
            Assert.That(hosted.status, Is.EqualTo("applied"));

            // And inside the session, solo lifecycle refuses honestly.
            var raceStart = app.Dispatch("race.start",
                Params(("vehicleId", "car-01"), ("eventId", "intro-01")));
            Assert.That(raceStart.status, Is.EqualTo("rejected"));
            Assert.That(raceStart.code, Is.EqualTo("networkActive"));
            app.Dispatch("lan.leave", Params());
            yield break;
        }
    }
}
