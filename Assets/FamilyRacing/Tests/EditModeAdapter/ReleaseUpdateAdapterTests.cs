using NUnit.Framework;

namespace FamilyRacing.Tests
{
    public sealed class ReleaseUpdateAdapterTests
    {
        [Test]
        public void GitHubJsonDecodesAndIgnoresUntrustedPageUrl()
        {
            var offer = ReleaseUpdateChecker.ParseOffer(
                "{\"tag_name\":\"v1.2.0\",\"html_url\":\"https://evil.example\",\"assets\":[{\"name\":\"FamilyRacing-1.2.0-Windows-x64.zip\",\"size\":100}]}",
                "1.1.0", "Windows");
            Assert.That(offer, Is.Not.Null);
            Assert.That(offer.PageUrl, Is.EqualTo("https://github.com/danlil240/race-game/releases/tag/v1.2.0"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("{")]
        [TestCase("null")]
        [TestCase("{}")]
        [TestCase("[]")]
        [TestCase("{\"message\":\"API rate limit exceeded\"}")]
        public void InvalidOrUnavailableResponseDoesNotInterruptStartup(string json)
        {
            Assert.That(ReleaseUpdateChecker.ParseOffer(json, "1.0.0", "Windows"), Is.Null);
        }
    }
}
