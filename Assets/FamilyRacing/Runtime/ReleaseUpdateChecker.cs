using System;
using System.Collections;
using FamilyRacing.Core.Foundation;
using UnityEngine;
using UnityEngine.Networking;

namespace FamilyRacing
{
    // One asynchronous startup request. A release service outage must never block the game.
    [DisallowMultipleComponent]
    public sealed class ReleaseUpdateChecker : MonoBehaviour
    {
        public ReleaseUpdateOffer Offer { get; private set; }
        private UnityWebRequest request;

        private IEnumerator Start()
        {
            // Editor tests/authoring and batch validation must not contact the release service.
            if (Application.isEditor || Application.isBatchMode) yield break;
            var platform = Application.platform == RuntimePlatform.WindowsPlayer ? "Windows"
                : Application.platform == RuntimePlatform.Android ? "Android" : null;
            if (platform == null) yield break;
            using (var pending = UnityWebRequest.Get(ReleaseUpdatePolicy.LatestReleaseApi))
            {
                request = pending;
                pending.timeout = 6;
                pending.redirectLimit = 0;
                pending.SetRequestHeader("Accept", "application/vnd.github+json");
                pending.SetRequestHeader("User-Agent", "FamilyRacing/" + Application.version);
                yield return pending.SendWebRequest();
                if (pending.result == UnityWebRequest.Result.Success && pending.responseCode == 200)
                    Offer = ParseOffer(pending.downloadHandler.text, Application.version, platform);
                request = null;
            }
        }

        public static ReleaseUpdateOffer ParseOffer(string json, string installed, string platform)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 262144) return null;
            try
            {
                return ReleaseUpdatePolicy.FindUpdate(installed,
                    JsonUtility.FromJson<PublishedRelease>(json), platform);
            }
            catch (ArgumentException) { return null; }
        }

        private void OnDestroy()
        {
            if (request == null) return;
            request.Abort();
            request.Dispose();
            request = null;
        }
    }
}
