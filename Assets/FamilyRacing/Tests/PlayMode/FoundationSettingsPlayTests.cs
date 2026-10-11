using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FamilyRacing.Tests
{
    // Settings-screen UI/command parity against a real FoundationService and
    // ProfileStore in a synthetic directory: every rendered control callback
    // must merge the live service draft so sequential edits and command-driven
    // updates never drop each other's pending values. These are synthetic UI
    // Toolkit callbacks in the editor — not OS/physical touch and not a real
    // process restart.
    // Editor-only fixture: the generated PanelSettings loads via
    // AssetDatabase, so standalone player/device runs must exclude it.
    [UnityPlatform(RuntimePlatform.WindowsEditor, RuntimePlatform.OSXEditor,
        RuntimePlatform.LinuxEditor)]
    public sealed class FoundationSettingsPlayTests
    {
        private const string PanelSettingsPath =
            "Assets/FamilyRacing/Generated/FoundationPanelSettings.asset";

        private string profilesDirectory;
        private ProfileStore store;
        private FoundationService service;
        private GameObject hostGo;
        private FoundationApp app;
        private GameObject shellGo;
        private UIDocument document;
        private PanelSettings panelSettings;
        private FoundationShell shell;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            profilesDirectory = Path.Combine(Path.GetTempPath(),
                "fr-settings-ui-" + Guid.NewGuid().ToString("N"));
            store = new ProfileStore(profilesDirectory, new UnityProfileSerializer());
            var runId = "run-" + Guid.NewGuid().ToString("N");
            service = new FoundationService(store, "en", runId);

            // The host stays inactive so FoundationApp.Awake never runs and
            // never opens the real Application.persistentDataPath store;
            // Dispatch is a plain method over the injected composition root.
            hostGo = new GameObject("settings-parity-host");
            hostGo.SetActive(false);
            app = hostGo.AddComponent<FoundationApp>();
            Inject(app, "service", service);
            Inject(app, "runId", runId);

#if UNITY_EDITOR
            panelSettings = UnityEngine.Object.Instantiate(
                UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath));
#endif
            Assert.That(panelSettings, Is.Not.Null,
                "The generated PanelSettings asset must load; the clone is destroyed in teardown.");

            shellGo = new GameObject("settings-parity-shell");
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
            if (hostGo != null) UnityEngine.Object.Destroy(hostGo);
            if (panelSettings != null) UnityEngine.Object.DestroyImmediate(panelSettings);
            try
            {
                if (Directory.Exists(profilesDirectory))
                    Directory.Delete(profilesDirectory, true);
            }
            catch (Exception) { }
        }

        // --- fixture helpers ------------------------------------------------

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

        // The same pointer down/up pair a real touch produces — the path
        // Clickable.ProcessDown/UpEvent actually consumes.
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

        private string PrimaryPath()
        {
            return Path.Combine(profilesDirectory, "profile-0.json");
        }

        private IEnumerator CreateProfileAndOpenSettings()
        {
            var created = app.Dispatch("profile.create",
                Params(("slot", "0"), ("language", "en")));
            Assert.That(created.status, Is.EqualTo("applied"));
            Assert.That(Snapshot().screen, Is.EqualTo("settings"));
            Refresh();
            yield return WaitForWidget<Slider>("settings.volume.music");
        }

        // --- tests ------------------------------------------------------------

        [UnityTest]
        public IEnumerator SequentialVolumeControlsPreservePendingEdits()
        {
            yield return CreateProfileAndOpenSettings();
            var savedBytes = File.ReadAllBytes(PrimaryPath());

            var music = Widget<Slider>("settings.volume.music");
            music.value = 0.2f;
            Refresh();
            Assert.That(Widget<Slider>("settings.volume.music"), Is.SameAs(music),
                "A draft-only refresh must update sliders in place, not rebuild the tree.");
            Assert.That(Snapshot().draftProfile.musicVolume,
                Is.EqualTo(0.2f).Within(0.0005f));

            var effects = Widget<Slider>("settings.volume.effects");
            effects.value = 0.35f;
            Refresh();
            var voice = Widget<Slider>("settings.volume.voice");
            voice.value = 0.45f;
            Refresh();

            var state = Snapshot();
            Assert.That(state.draftProfile.musicVolume,
                Is.EqualTo(0.2f).Within(0.0005f),
                "Later volume edits must not reset earlier pending values.");
            Assert.That(state.draftProfile.effectsVolume,
                Is.EqualTo(0.35f).Within(0.0005f));
            Assert.That(state.draftProfile.voiceVolume,
                Is.EqualTo(0.45f).Within(0.0005f));
            Assert.That(state.savedProfile.musicVolume,
                Is.EqualTo(0.65f).Within(0.0005f),
                "Unsaved edits must not touch the saved profile.");
            Assert.That(state.pendingSettings, Is.True);
            Assert.That(File.ReadAllBytes(PrimaryPath()), Is.EqualTo(savedBytes),
                "Draft edits must not write the profile file.");
        }

        [UnityTest]
        public IEnumerator ShakeButtonTogglesBothDirections()
        {
            yield return CreateProfileAndOpenSettings();
            var savedBytes = File.ReadAllBytes(PrimaryPath());
            yield return WaitForWidget<Button>("settings.reducedShake");

            var shake = Widget<Button>("settings.reducedShake");
            Press(shake);
            Refresh();
            Assert.That(Snapshot().draftProfile.reducedCameraShake, Is.True,
                "First press must set reducedCameraShake.");

            var again = Widget<Button>("settings.reducedShake");
            Assert.That(again, Is.SameAs(shake),
                "A shake-only change must not rebuild the settings tree.");
            Press(again);
            Refresh();
            Assert.That(Snapshot().draftProfile.reducedCameraShake, Is.False,
                "Second press must toggle reducedCameraShake back off.");
            Assert.That(File.ReadAllBytes(PrimaryPath()), Is.EqualTo(savedBytes),
                "Draft edits must not write the profile file.");
        }

        [UnityTest]
        public IEnumerator CameraAndVolumeControlsMergeLatestCommandDraft()
        {
            yield return CreateProfileAndOpenSettings();

            // A controller-driven settings.update after the shell was built
            // must become the draft every later UI edit merges against.
            var update = app.Dispatch("settings.update", Params(
                ("language", "en"), ("camera", "overhead"),
                ("reducedCameraShake", "true"), ("musicVolume", "0.2"),
                ("effectsVolume", "0.35"), ("voiceVolume", "0.45")));
            Assert.That(update.status, Is.EqualTo("applied"));
            Refresh();

            var music = Widget<Slider>("settings.volume.music");
            Assert.That(music.value, Is.EqualTo(0.2f).Within(0.0005f),
                "In-place refresh must reflect the command-driven draft.");
            music.value = 0.6f;
            Refresh();

            var state = Snapshot();
            Assert.That(state.draftProfile.language, Is.EqualTo("en"));
            Assert.That(state.draftProfile.camera, Is.EqualTo("overhead"),
                "A volume edit must preserve the command-driven camera.");
            Assert.That(state.draftProfile.reducedCameraShake, Is.True);
            Assert.That(state.draftProfile.musicVolume,
                Is.EqualTo(0.6f).Within(0.0005f));
            Assert.That(state.draftProfile.effectsVolume,
                Is.EqualTo(0.35f).Within(0.0005f));
            Assert.That(state.draftProfile.voiceVolume,
                Is.EqualTo(0.45f).Within(0.0005f));

            yield return WaitForWidget<Button>("settings.camera.chase");
            Press(Widget<Button>("settings.camera.chase"));
            Refresh();
            state = Snapshot();
            Assert.That(state.draftProfile.camera, Is.EqualTo("chase"));
            Assert.That(state.draftProfile.reducedCameraShake, Is.True,
                "Camera clicks must preserve the pending shake flag.");
            Assert.That(state.draftProfile.musicVolume,
                Is.EqualTo(0.6f).Within(0.0005f));
            Assert.That(state.draftProfile.effectsVolume,
                Is.EqualTo(0.35f).Within(0.0005f));
            Assert.That(state.draftProfile.voiceVolume,
                Is.EqualTo(0.45f).Within(0.0005f));

            // The locale switch rebuilds the tree RTL; widgets re-query.
            Press(Widget<Button>("settings.language.he"));
            Refresh();
            Assert.That(Snapshot().language, Is.EqualTo("he"));
            Assert.That(document.rootVisualElement.languageDirection,
                Is.EqualTo(LanguageDirection.RTL));
            yield return WaitForWidget<Slider>("settings.volume.effects");

            Widget<Slider>("settings.volume.effects").value = 0.7f;
            Refresh();
            state = Snapshot();
            Assert.That(state.draftProfile.language, Is.EqualTo("he"));
            Assert.That(state.draftProfile.camera, Is.EqualTo("chase"));
            Assert.That(state.draftProfile.reducedCameraShake, Is.True);
            Assert.That(state.draftProfile.musicVolume,
                Is.EqualTo(0.6f).Within(0.0005f));
            Assert.That(state.draftProfile.effectsVolume,
                Is.EqualTo(0.7f).Within(0.0005f));
            Assert.That(state.draftProfile.voiceVolume,
                Is.EqualTo(0.45f).Within(0.0005f));
        }

        [UnityTest]
        public IEnumerator SavedUiEditsSurviveSyntheticStoreReload()
        {
            yield return CreateProfileAndOpenSettings();
            var id = Snapshot().savedProfile.id;

            Widget<Slider>("settings.volume.music").value = 0.2f;
            Refresh();
            Widget<Slider>("settings.volume.effects").value = 0.35f;
            Refresh();
            Widget<Slider>("settings.volume.voice").value = 0.45f;
            Refresh();
            yield return WaitForWidget<Button>("settings.camera.overhead");
            Press(Widget<Button>("settings.camera.overhead"));
            Refresh();
            Press(Widget<Button>("settings.reducedShake"));
            Refresh();
            Press(Widget<Button>("settings.language.he"));
            Refresh();

            // The Hebrew switch rebuilt the tree: re-query before saving.
            yield return WaitForWidget<Button>("settings.save");
            Press(Widget<Button>("settings.save"));
            Refresh();

            var state = Snapshot();
            Assert.That(state.pendingSettings, Is.False);
            Assert.That(state.persistenceStatus, Is.EqualTo("saved"));
            Assert.That(state.savedProfile.id, Is.EqualTo(id),
                "Save must preserve the profile identity.");
            Assert.That(state.savedProfile.language, Is.EqualTo("he"));
            Assert.That(state.savedProfile.camera, Is.EqualTo("overhead"));
            Assert.That(state.savedProfile.reducedCameraShake, Is.True);
            Assert.That(state.savedProfile.musicVolume,
                Is.EqualTo(0.2f).Within(0.0005f));
            Assert.That(state.savedProfile.effectsVolume,
                Is.EqualTo(0.35f).Within(0.0005f));
            Assert.That(state.savedProfile.voiceVolume,
                Is.EqualTo(0.45f).Within(0.0005f));

            // Synthetic store reload on the same directory — not a process
            // restart claim.
            var reloaded = new ProfileStore(profilesDirectory,
                new UnityProfileSerializer()).Load(0);
            Assert.That(reloaded.Status, Is.EqualTo(ProfileLoadStatus.Loaded));
            Assert.That(reloaded.Profile.id, Is.EqualTo(id));
            Assert.That(reloaded.Profile.language, Is.EqualTo("he"));
            Assert.That(reloaded.Profile.camera, Is.EqualTo("overhead"));
            Assert.That(reloaded.Profile.reducedCameraShake, Is.True);
            Assert.That(reloaded.Profile.musicVolume,
                Is.EqualTo(0.2f).Within(0.0005f));
            Assert.That(reloaded.Profile.effectsVolume,
                Is.EqualTo(0.35f).Within(0.0005f));
            Assert.That(reloaded.Profile.voiceVolume,
                Is.EqualTo(0.45f).Within(0.0005f));
        }

        [UnityTest]
        public IEnumerator FailedUiSaveRestoresSavedDraftAndPreservesFiles()
        {
            yield return CreateProfileAndOpenSettings();
            var primaryBefore = File.ReadAllBytes(PrimaryPath());
            var backupPath = PrimaryPath() + ".bak";

            Widget<Slider>("settings.volume.music").value = 0.2f;
            Refresh();
            Assert.That(Snapshot().pendingSettings, Is.True);

            // An unreadable backup refuses the save, leaving both files
            // untouched and restoring the saved draft.
            File.WriteAllText(backupPath, "{ not valid json");
            var badBackup = File.ReadAllBytes(backupPath);

            yield return WaitForWidget<Button>("settings.save");
            var save = Widget<Button>("settings.save");
            Assert.That(save.enabledInHierarchy, Is.True,
                "Pending edits must enable the save button.");
            Press(save);
            Refresh();

            var state = Snapshot();
            Assert.That(state.persistenceStatus, Is.EqualTo("saveFailed"));
            Assert.That(state.pendingSettings, Is.False);
            Assert.That(state.savedProfile.musicVolume,
                Is.EqualTo(0.65f).Within(0.0005f));
            Assert.That(state.draftProfile.musicVolume,
                Is.EqualTo(0.65f).Within(0.0005f),
                "A failed save must restore the previously saved draft.");
            Assert.That(Widget<Slider>("settings.volume.music").value,
                Is.EqualTo(0.65f).Within(0.0005f),
                "The rendered slider must reflect the restored draft.");
            Assert.That(File.ReadAllBytes(PrimaryPath()), Is.EqualTo(primaryBefore),
                "A refused save must leave the primary file byte-identical.");
            Assert.That(File.ReadAllBytes(backupPath), Is.EqualTo(badBackup),
                "A refused save must leave the invalid backup byte-identical.");
        }
    }
}
