using System;
using System.Collections.Generic;
using FamilyRacing.Core.Localization;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    public sealed class LocalizationTests
    {
        // The shell strings the plan requires the bilingual catalog to cover.
        private static readonly string[] RequiredKeys =
        {
            "shell.title",
            "shell.disclaimer",
            "profile.select",
            "profile.animal.fox",
            "profile.animal.rabbit",
            "profile.animal.panda",
            "profile.animal.raccoon",
            "settings.language",
            "settings.camera",
            "settings.camera.chase",
            "settings.camera.overhead",
            "settings.reducedShake",
            "settings.volume.music",
            "settings.volume.effects",
            "settings.volume.voice",
            "sandbox.title",
            "input.steering",
            "input.drift",
            "input.boost",
            "input.brake",
            "input.gadget",
            "input.recovery",
            "input.pause",
            "action.back",
            "action.save",
            "status.saved",
            "status.blocked",
            "status.recovered",
            "status.saveFailed",
            "warning.missingFont"
        };

        [Test]
        public void RequiredKeysArePresent()
        {
            foreach (var key in RequiredKeys)
                Assert.That(ShellText.Keys, Does.Contain(key), "Missing catalog key: " + key);
        }

        [Test]
        public void KeysContainNoDuplicates()
        {
            var seen = new HashSet<string>();
            foreach (var key in ShellText.Keys)
                Assert.That(seen.Add(key), Is.True, "Duplicate catalog key: " + key);
        }

        [Test]
        public void EveryKeyHasNonEmptyTextInBothLanguages()
        {
            Assert.That(ShellText.Keys, Is.Not.Empty);
            foreach (var key in ShellText.Keys)
            {
                Assert.That(ShellText.Get(key, "en"), Is.Not.Null.And.Not.Empty, key);
                Assert.That(ShellText.Get(key, "he"), Is.Not.Null.And.Not.Empty, key);
            }
        }

        [Test]
        public void EveryKeyIsTranslated()
        {
            foreach (var key in ShellText.Keys)
                Assert.That(ShellText.Get(key, "he"), Is.Not.EqualTo(ShellText.Get(key, "en")), key);
        }

        [TestCase("en", false)]
        [TestCase("he", true)]
        public void RightToLeftOnlyForHebrew(string language, bool expected)
        {
            Assert.That(ShellText.IsRightToLeft(language), Is.EqualTo(expected));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("fr")]
        [TestCase("EN")]
        public void UnsupportedLanguagesAreRejected(string language)
        {
            Assert.Throws<ArgumentException>(() => ShellText.IsRightToLeft(language));
            Assert.Throws<ArgumentException>(() => ShellText.Get("shell.title", language));
        }

        [TestCase("unknown.key")]
        [TestCase("")]
        [TestCase("SHELL.TITLE")]
        public void UnknownKeysAreRejected(string key)
        {
            Assert.Throws<KeyNotFoundException>(() => ShellText.Get(key, "en"));
        }

        [Test]
        public void NullKeyIsRejected()
        {
            Assert.Throws<ArgumentNullException>(() => ShellText.Get(null, "en"));
        }

        [Test]
        public void EveryGameplayEventTypeHasAnEventKindLabel()
        {
            // The closed GameplayEventType set drives the shell's
            // "event.type.<type>" kind labels — a new event type added
            // without its bilingual pair must fail here.
            var fields = typeof(FamilyRacing.Core.Racing.GameplayEventType)
                .GetFields(System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Static);
            Assert.That(fields, Is.Not.Empty);
            foreach (var field in fields)
            {
                var eventType = (string)field.GetValue(null);
                var key = "event.type." + eventType;
                Assert.That(ShellText.Keys, Does.Contain(key),
                    "missing localization for event type " + eventType);
                Assert.That(ShellText.Get(key, "en"), Is.Not.Empty, key);
                Assert.That(ShellText.Get(key, "he"), Is.Not.Empty, key);
            }
        }
    }
}
