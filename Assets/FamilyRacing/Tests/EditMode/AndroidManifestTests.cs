#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Xml;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    public sealed class AndroidManifestTests
    {
        [Test]
        public void GeneratedManifestKeepsUnityLauncherAndLanPermissions()
        {
            const string path = "Assets/Plugins/Android/AndroidManifest.xml";
            var original = File.ReadAllBytes(path);
            try
            {
                var setup = Type.GetType("FamilyRacing.EditorTools.NetworkPrototypeSetup, FamilyRacing.Editor", true);
                setup.GetMethod("EnsureAndroidManifest", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, null);
                var manifest = new XmlDocument();
                manifest.Load(path);
                var ns = new XmlNamespaceManager(manifest.NameTable);
                ns.AddNamespace("android", "http://schemas.android.com/apk/res/android");
                var launcher = manifest.SelectSingleNode("/manifest/application/activity[" +
                    "intent-filter/action[@android:name='android.intent.action.MAIN'] and " +
                    "intent-filter/category[@android:name='android.intent.category.LAUNCHER'] and " +
                    "meta-data[@android:name='android.app.lib_name' and @android:value='game']]", ns);
                Assert.That(launcher, Is.Not.Null, "The GameActivity build must be launchable from Android's home screen.");
                Assert.That(launcher.SelectSingleNode("meta-data[@android:name='unityplayer.UnityActivity' and @android:value='true']", ns), Is.Not.Null);
                foreach (var permission in new[] { "INTERNET", "ACCESS_NETWORK_STATE", "CHANGE_WIFI_MULTICAST_STATE" })
                    Assert.That(manifest.SelectSingleNode("/manifest/uses-permission[@android:name='android.permission." + permission + "']", ns), Is.Not.Null, permission);
            }
            finally { File.WriteAllBytes(path, original); }
        }
    }
}
#endif
