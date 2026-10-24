using System;

namespace FamilyRacing.Core.Foundation
{
    [Serializable]
    public sealed class PublishedRelease
    {
        public string tag_name;
        public bool draft;
        public bool prerelease;
        public ReleasePackage[] assets;
    }

    [Serializable]
    public sealed class ReleasePackage
    {
        public string name;
        public long size;
    }

    public sealed class ReleaseUpdateOffer
    {
        public string Version { get; }
        public string PageUrl { get; }
        public ReleaseUpdateOffer(string version, string pageUrl)
        {
            Version = version;
            PageUrl = pageUrl;
        }
    }

    public static class ReleaseUpdatePolicy
    {
        public const string RepositoryUrl = "https://github.com/danlil240/race-game";
        public const string LatestReleaseApi = "https://api.github.com/repos/danlil240/race-game/releases/latest";

        public static ReleaseUpdateOffer FindUpdate(string installed, PublishedRelease release, string platform)
        {
            if (release == null || release.draft || release.prerelease || release.assets == null)
                return null;
            if (!TryVersion(installed, out var current) ||
                !TryVersion(release.tag_name, out var latest) || latest.CompareTo(current) <= 0)
                return null;
            var version = latest.ToString(3);
            string package;
            if (platform == "Windows") package = "FamilyRacing-" + version + "-Windows-x64.zip";
            else if (platform == "Android") package = "FamilyRacing-" + version + "-Android.apk";
            else return null;
            foreach (var asset in release.assets)
                if (asset != null && asset.size > 0 && asset.name == package)
                    // Construct the URL from the validated tag. Never follow a response-provided URL.
                    return new ReleaseUpdateOffer(version, RepositoryUrl + "/releases/tag/" + release.tag_name);
            return null;
        }

        private static bool TryVersion(string value, out Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(value)) return false;
            if (value[0] == 'v') value = value.Substring(1);
            var parts = value.Split('.');
            if (parts.Length != 3) return false;
            foreach (var part in parts)
            {
                if (part.Length == 0 || (part.Length > 1 && part[0] == '0')) return false;
                foreach (var character in part)
                    if (character < '0' || character > '9') return false;
                if (!int.TryParse(part, out _)) return false;
            }
            return Version.TryParse(value, out version);
        }
    }
}
