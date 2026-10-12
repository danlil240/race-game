using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace FamilyRacing.Core.Networking
{
    // Private reconnect material carried only inside join.request /
    // join.accept and in the local credential store. It is deliberately a
    // plain serializable record so the runtime store can persist it
    // without ever exposing it through NetworkSessionState, snapshots,
    // event streams, or logs.
    [Serializable]
    public sealed class ReconnectMaterial
    {
        public string participantId = "";
        public string sessionId = "";
        public string hostAddress = "";
        public int hostPort;
        public string credential = "";
    }

    // Composition seam for the private, profile-scoped credential
    // persistence the runtime provides (a file under the profile-private
    // directory). Core only declares the contract so the service can
    // re-persist rotated credentials and delete them on explicit leave.
    // Implementations must never log or stream credential values.
    public interface IReconnectCredentialStore
    {
        void Save(ReconnectMaterial material);
        bool TryLoad(string participantId, out ReconnectMaterial material);
        void Clear();
    }

    // Host-side registry binding credentialed session ownership to
    // participant slots. It owns the credential lifecycle — issuance,
    // rotation on reclaim, revocation — so slot reservations can never be
    // stolen by display name or profile id alone. Registry contents are
    // transport-private: they never enter observation DTOs.
    //
    // Reclaim semantics: the presented credential must be the slot's
    // *current* credential for the *current* session. A successful
    // reclaim bumps the slot's ownership epoch, which the input buffer
    // and transport map use to fence the superseded connection. Every
    // issued credential stays in the authentication history so retired
    // credentials keep proving result-retrieval rights (Task 8) via
    // TryAuthenticate — which never reclaims and never bumps the epoch.
    //
    // Lifecycle: Release() drops the race-slot reservation at race end
    // while authentication history survives (rematch must not revoke
    // result-retrieval rights); Revoke() is explicit-leave severity and
    // destroys every credential the participant ever held.
    public sealed class ReconnectRegistry
    {
        private sealed class Record
        {
            public string participantId;
            public string credential;                       // current reclaim credential
            public readonly List<string> history =
                new List<string>();                          // every issued credential
            public long ownershipEpoch;                      // last epoch handed out
            public bool reserved;                            // slot survives while disconnected
        }

        private readonly RandomNumberGenerator rng =
            RandomNumberGenerator.Create();
        private readonly Dictionary<string, Record> byParticipant =
            new Dictionary<string, Record>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> byCredential =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private string sessionId = "";

        public string SessionId
        {
            get { return sessionId; }
        }

        // Opens a new session: every prior reservation, credential, and
        // epoch dies with the old session id.
        public void BeginSession(string id)
        {
            sessionId = id ?? "";
            byParticipant.Clear();
            byCredential.Clear();
        }

        // Issues the first credential for a freshly seated participant.
        // Re-issuing an occupied id rotates the credential and restarts
        // the epoch — a fresh join is a new ownership of the seat.
        public string Issue(string participantId)
        {
            Record record;
            if (!byParticipant.TryGetValue(participantId, out record))
            {
                record = new Record { participantId = participantId };
                byParticipant.Add(participantId, record);
            }
            record.reserved = false;
            record.ownershipEpoch = 1;
            record.credential = RegisterCredential(record);
            return record.credential;
        }

        // Rotates the reclaim credential — the host does this on every
        // successful reclaim so a presented credential can only ever
        // reclaim once. The retired credential keeps authenticating. The
        // ownership epoch is untouched (TryReclaim already bumped it).
        // Returns the new credential join.accept must carry back, or
        // null for an unknown participant.
        public string Rotate(string participantId)
        {
            Record record;
            if (!byParticipant.TryGetValue(participantId, out record))
                return null;
            record.credential = RegisterCredential(record);
            return record.credential;
        }

        // The binding reclaim contract from the task brief: a presented
        // credential that is session-scoped, still current, and backed by
        // a live reservation record identifies the participant and hands
        // out the next ownership epoch. Everything else is refused.
        public bool TryReclaim(string credential, string presentedSessionId,
            out string participantId, out long ownershipEpoch)
        {
            participantId = null;
            ownershipEpoch = 0;
            if (string.IsNullOrEmpty(credential)
                || string.IsNullOrEmpty(presentedSessionId)
                || presentedSessionId != sessionId)
                return false;
            string owner;
            Record record;
            if (!byCredential.TryGetValue(credential, out owner)
                || !byParticipant.TryGetValue(owner, out record)
                || record.credential != credential)
                return false;
            participantId = owner;
            ownershipEpoch = ++record.ownershipEpoch;
            record.reserved = false;
            return true;
        }

        // Weaker than TryReclaim: proves a credential was issued to a
        // participant in this session without reclaiming a slot or
        // bumping an epoch. Survives Release() so result retrieval keeps
        // working across rematches; dies with Revoke().
        public bool TryAuthenticate(string credential,
            string presentedSessionId, out string participantId)
        {
            participantId = null;
            if (string.IsNullOrEmpty(credential)
                || presentedSessionId != sessionId)
                return false;
            string owner;
            if (!byCredential.TryGetValue(credential, out owner))
                return false;
            participantId = owner;
            return true;
        }

        // Marks a disconnected participant's slot as reserved for the
        // remainder of the race; reclaim and release clear the flag.
        public void MarkReserved(string participantId, bool reserved)
        {
            Record record;
            if (byParticipant.TryGetValue(participantId, out record))
                record.reserved = reserved;
        }

        // Race end: the race-slot reservation is released — the record
        // and its epochs go away — while every issued credential stays in
        // the authentication history so result retrieval rights survive
        // rematches.
        public void Release(string participantId)
        {
            byParticipant.Remove(participantId);
        }

        // Explicit leave / lobby clear: the participant forfeits every
        // credential it ever held, including authentication-only rights.
        public void Revoke(string participantId)
        {
            Record record;
            if (!byParticipant.TryGetValue(participantId, out record))
                return;
            foreach (var credential in record.history)
                byCredential.Remove(credential);
            byParticipant.Remove(participantId);
        }

        // The epoch the registry last handed out for the participant —
        // zero when unknown. Read-only so callers can echo the
        // authoritative epoch into participant state.
        public long EpochFor(string participantId)
        {
            Record record;
            return byParticipant.TryGetValue(participantId, out record)
                ? record.ownershipEpoch : 0;
        }

        private string RegisterCredential(Record record)
        {
            // 256-bit random token, hex-encoded. Credentials are opaque to
            // everyone but the issuer and the owning process.
            var bytes = new byte[32];
            rng.GetBytes(bytes);
            var builder = new StringBuilder(64);
            for (var i = 0; i < bytes.Length; i++)
                builder.Append(bytes[i].ToString("x2"));
            var credential = builder.ToString();
            record.history.Add(credential);
            byCredential[credential] = record.participantId;
            return credential;
        }
    }
}
