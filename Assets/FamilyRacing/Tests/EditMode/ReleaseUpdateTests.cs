using FamilyRacing.Core.Foundation;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    public class ReleaseUpdateTests
    {
        private static PublishedRelease Release(string tag = "v1.10.0") => new PublishedRelease
        {
            tag_name = tag,
            assets = new[] {
                new ReleasePackage { name = "FamilyRacing-" + tag.TrimStart('v') + "-Windows-x64.zip", size = 100 },
                new ReleasePackage { name = "FamilyRacing-" + tag.TrimStart('v') + "-Android.apk", size = 100 }
            }
        };

        [Test]
        public void NumericNewerReleaseOffersTrustedPageForBothPlatforms()
        {
            foreach (var platform in new[] { "Windows", "Android" })
            {
                var offer = ReleaseUpdatePolicy.FindUpdate("1.9.0", Release(), platform);
                Assert.That(offer, Is.Not.Null);
                Assert.That(offer.Version, Is.EqualTo("1.10.0"));
                Assert.That(offer.PageUrl, Is.EqualTo("https://github.com/danlil240/race-game/releases/tag/v1.10.0"));
            }
        }

        [TestCase("1.10.0")]
        [TestCase("2.0.0")]
        [TestCase("invalid")]
        [TestCase(null)]
        public void EqualOlderOrInvalidInstalledVersionDoesNotOffer(string current)
        {
            Assert.That(ReleaseUpdatePolicy.FindUpdate(current, Release(), "Windows"), Is.Null);
        }

        [TestCase("v1.2.0-beta.1")]
        [TestCase("1.2")]
        [TestCase("1.2.3.4")]
        [TestCase("v01.2.3")]
        [TestCase("v1.-2.3")]
        [TestCase("v2147483648.0.0")]
        [TestCase("v1.2.3/../../evil")]
        [TestCase(null)]
        public void MalformedReleaseVersionDoesNotOffer(string tag)
        {
            var release = Release("v1.2.3");
            release.tag_name = tag;
            Assert.That(ReleaseUpdatePolicy.FindUpdate("0.0.0", release, "Windows"), Is.Null);
        }

        [Test]
        public void DraftAndPrereleaseNeverOffer()
        {
            var release = Release();
            release.draft = true;
            Assert.That(ReleaseUpdatePolicy.FindUpdate("1.0.0", release, "Windows"), Is.Null);
            release.draft = false;
            release.prerelease = true;
            Assert.That(ReleaseUpdatePolicy.FindUpdate("1.0.0", release, "Windows"), Is.Null);
        }

        [Test]
        public void MissingEmptyOrWrongPlatformPackagesNeverOffer()
        {
            var release = Release();
            Assert.That(ReleaseUpdatePolicy.FindUpdate("1.0.0", release, "Linux"), Is.Null);
            release.assets = new[] { new ReleasePackage { name = "FamilyRacing-1.10.0-Android.apk", size = 100 } };
            Assert.That(ReleaseUpdatePolicy.FindUpdate("1.0.0", release, "Windows"), Is.Null);
            release.assets[0].size = 0;
            Assert.That(ReleaseUpdatePolicy.FindUpdate("1.0.0", release, "Android"), Is.Null);
            release.assets = null;
            Assert.That(ReleaseUpdatePolicy.FindUpdate("1.0.0", release, "Android"), Is.Null);
            Assert.That(ReleaseUpdatePolicy.FindUpdate("1.0.0", null, "Windows"), Is.Null);
        }
    }
}
