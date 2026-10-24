using System;
using System.Collections;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Localization;
using FamilyRacing.Core.Profiles;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FamilyRacing.Tests
{
    public sealed class ReleaseUpdateNoticePlayTests
    {
        [UnityTest]
        public IEnumerator OfferPreservesRaceControlsLocalizesAndDismisses()
        {
            var host = new GameObject("update-test-app");
            host.SetActive(false);
            var app = host.AddComponent<FoundationApp>();
            var store = new ProfileStore(Path.Combine(Path.GetTempPath(), "fr-update-" + Guid.NewGuid().ToString("N")), new UnityProfileSerializer());
            var service = new FoundationService(store, "en", "update-test");
            typeof(FoundationApp).GetField("service", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(app, service);
            var go = new GameObject("update-test-shell");
            go.SetActive(false);
            PanelSettings panel = null;
            try
            {
#if UNITY_EDITOR
                panel = UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>(
                    "Assets/FamilyRacing/Generated/FoundationPanelSettings.asset"));
#endif
                Assert.That(panel, Is.Not.Null);
                var doc = go.AddComponent<UIDocument>();
                doc.panelSettings = panel;
                var shell = go.AddComponent<FoundationShell>();
                go.SetActive(true);
                shell.Bind(app);
                yield return null;
                shell.Refresh(service.Snapshot());
                Assert.That(doc.rootVisualElement.Q("update.notice"), Is.Null);
                var checker = go.GetComponent<ReleaseUpdateChecker>();
                Assert.That(checker.Offer, Is.Null, "Editor startup must not contact GitHub.");
                var racing = service.Snapshot();
                racing.screen = "driving";
                shell.Refresh(racing);
                var held = doc.rootVisualElement.Q<Button>("driving.steerLeft");
                Assert.That(held, Is.Not.Null);
                held.CapturePointer(7);
                typeof(ReleaseUpdateChecker).GetField("<Offer>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(checker, new ReleaseUpdateOffer("99.0.0", "https://github.com/danlil240/race-game/releases/tag/v99.0.0"));
                shell.Refresh(racing);
                Assert.That(doc.rootVisualElement.Q<Button>("driving.steerLeft"), Is.SameAs(held), "Async offer must not rebuild the racing tree.");
                Assert.That(held.HasPointerCapture(7), Is.True);
                Assert.That(doc.rootVisualElement.Q("update.notice"), Is.Null);
                held.ReleasePointer(7);
                var state = service.Snapshot();
                shell.Refresh(state);
                Assert.That(doc.rootVisualElement.Q<Label>("update.title").text, Does.Contain("99.0.0"));
                state.language = "he";
                shell.Refresh(state);
                Assert.That(doc.rootVisualElement.Q<Button>("update.open").text, Is.EqualTo(ShellText.Get("update.open", "he")));
                var button = doc.rootVisualElement.Q<Button>("update.later");
                using (var click = NavigationSubmitEvent.GetPooled())
                {
                    click.target = button;
                    button.SendEvent(click);
                }
                Assert.That(doc.rootVisualElement.Q("update.notice"), Is.Null);
                state.language = "en";
                shell.Refresh(state);
                Assert.That(doc.rootVisualElement.Q("update.notice"), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(host);
                if (panel != null) UnityEngine.Object.DestroyImmediate(panel);
            }
        }
    }
}
