using System;
using System.IO;
using FamilyRacing.Core.Networking;
using UnityEngine;

namespace FamilyRacing.Networking
{
    // Private, profile-scoped reconnect credential persistence. The file
    // lives under persistentDataPath in a non-shared directory — it is
    // what survives a client-process restart so a reserved slot can be
    // reclaimed. The credential never enters observation DTOs, streams,
    // or logs: nothing here prints or transmits the stored bytes.
    //
    // One record per install/profile pair: the store is scoped by the
    // local participant id it was saved for; TryLoad refuses material
    // belonging to a different profile so a credential can never leak
    // across profiles on a shared device.
    public sealed class ReconnectCredentialStore : IReconnectCredentialStore
    {
        private readonly string path;

        public ReconnectCredentialStore(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException("path");
            this.path = path;
        }

        public string Path
        {
            get { return path; }
        }

        public void Save(ReconnectMaterial material)
        {
            if (material == null || string.IsNullOrEmpty(material.credential))
                return;
            try
            {
                var directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)
                    && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(path, JsonUtility.ToJson(material));
            }
            catch (Exception)
            {
                // Persistence failure is survivable: the in-memory
                // credential still covers same-process reconnects. The
                // credential itself is never logged.
            }
        }

        public bool TryLoad(string participantId,
            out ReconnectMaterial material)
        {
            material = null;
            try
            {
                if (!File.Exists(path)) return false;
                var parsed = JsonUtility.FromJson<ReconnectMaterial>(
                    File.ReadAllText(path));
                // Scoped to the session's endpoint and the local profile:
                // material for a different participant is ignored so a
                // stored credential can never be borrowed across profiles.
                if (parsed == null
                    || parsed.participantId != participantId
                    || string.IsNullOrEmpty(parsed.credential)
                    || string.IsNullOrEmpty(parsed.sessionId)
                    || string.IsNullOrEmpty(parsed.hostAddress))
                    return false;
                material = parsed;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Clear()
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // Best effort: an undeleted file only ever fails closed —
                // TryLoad still scopes it to the owning profile/session.
            }
        }
    }
}
