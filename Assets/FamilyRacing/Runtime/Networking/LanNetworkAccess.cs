using System;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;
using UnityEngine.Android;
#endif

namespace FamilyRacing.Networking
{
    /// <summary>
    /// Gate consulted before any LAN socket is opened. States mirror the
    /// platform-neutral vocabulary used in diagnostics: available (sockets
    /// may open), requesting (a runtime request is in flight), denied (the
    /// user or system refused; manual retry is possible), unsupported
    /// (the platform can never grant this access).
    /// </summary>
    public enum LanNetworkAccessState
    {
        Available,
        Requesting,
        Denied,
        Unsupported
    }

    /// <summary>
    /// Minimal access surface so transport/discovery can be tested with
    /// stubs. <see cref="EnsureAccess"/> starts any platform request flow;
    /// <see cref="State"/> reports the last observed result. Implementations
    /// never block.
    /// </summary>
    public interface ILanNetworkAccess
    {
        LanNetworkAccessState State { get; }
        /// <summary>Human-readable reason behind the current state.</summary>
        string Detail { get; }
        /// <summary>
        /// Kick any pending platform request (e.g. acquiring a multicast
        /// lock). Idempotent; may be called again after a denial to retry.
        /// </summary>
        void EnsureAccess();
    }

    /// <summary>
    /// Factory plus state-name helpers for the LAN access gate.
    /// </summary>
    public static class LanNetworkAccess
    {
        /// <summary>The gate for the running platform.</summary>
        public static ILanNetworkAccess Create()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidLanNetworkAccess();
#else
            return new AlwaysAvailableLanNetworkAccess();
#endif
        }

        public static string StateName(LanNetworkAccessState state)
        {
            switch (state)
            {
                case LanNetworkAccessState.Available: return "available";
                case LanNetworkAccessState.Requesting: return "requesting";
                case LanNetworkAccessState.Denied: return "denied";
                case LanNetworkAccessState.Unsupported: return "unsupported";
                default: return "unsupported";
            }
        }
    }

    /// <summary>
    /// Editor and desktop builds need no runtime permission for LAN UDP;
    /// the gate is always satisfied.
    /// </summary>
    public sealed class AlwaysAvailableLanNetworkAccess : ILanNetworkAccess
    {
        public LanNetworkAccessState State => LanNetworkAccessState.Available;
        public string Detail => "no runtime permission required on this platform";
        public void EnsureAccess() { }
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    /// <summary>
    /// Android gate. INTERNET, ACCESS_NETWORK_STATE and
    /// CHANGE_WIFI_MULTICAST_STATE are normal (install-time) permissions
    /// declared in the unityLibrary manifest; there is no runtime dialog
    /// for them. If INTERNET is absent the OS would refuse every socket,
    /// so the gate reports denied with a manifest diagnostic. On top of
    /// the permission check a WifiManager MulticastLock is held so devices
    /// that filter broadcast/multicast responses still deliver discovery
    /// replies. Discovery denial never blocks unicast manual join — the
    /// gameplay socket only needs this gate to read available.
    /// </summary>
    public sealed class AndroidLanNetworkAccess : ILanNetworkAccess
    {
        private const string InternetPermission = "android.permission.INTERNET";

        private AndroidJavaObject multicastLock;

        public LanNetworkAccessState State =>
            Permission.HasUserAuthorizedPermission(InternetPermission)
                ? LanNetworkAccessState.Available
                : LanNetworkAccessState.Denied;

        public string Detail => State == LanNetworkAccessState.Available
            ? "INTERNET granted; multicast lock " + (multicastLock != null ? "held" : "pending")
            : "INTERNET permission missing from manifest or revoked";

        public void EnsureAccess()
        {
            if (State != LanNetworkAccessState.Available) return;
            if (multicastLock != null) return;
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var context = activity.Call<AndroidJavaObject>("getApplicationContext"))
                using (var wifi = context.Call<AndroidJavaObject>("getSystemService", "wifi"))
                {
                    multicastLock = wifi.Call<AndroidJavaObject>(
                        "createMulticastLock", "familyracing-lan-discovery");
                    multicastLock.Call("setReferenceCounted", true);
                    multicastLock.Call("acquire");
                }
            }
            catch (Exception e)
            {
                // A missing lock degrades discovery on some devices but
                // never justifies blocking unicast join — report and move on.
                Debug.LogWarning("LanNetworkAccess: multicast lock failed: " + e.Message);
                multicastLock = null;
            }
        }

        public void Release()
        {
            if (multicastLock == null) return;
            try { multicastLock.Call("release"); }
            catch (Exception) { }
            multicastLock.Dispose();
            multicastLock = null;
        }
    }
#endif
}
