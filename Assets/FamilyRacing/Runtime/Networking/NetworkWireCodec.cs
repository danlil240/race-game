using System;
using System.Collections.Generic;
using System.Text;
using FamilyRacing.Core.Networking;
using UnityEngine;

namespace FamilyRacing.Networking
{
    /// <summary>
    /// Bounded typed codec for the Task-5 gameplay wire. Every packet is a
    /// 4-byte ASCII tag followed by a UTF-8 JSON body produced by
    /// JsonUtility against the serializable Core DTOs:
    ///
    ///   N5IB  client→host, sequenced-unreliable channel: a batch of up to
    ///         InputPacketSampleCapacity recent NetworkInputFrames.
    ///   N5SS  host→client, sequenced-unreliable channel: one full
    ///         NetworkSnapshot. Hard ceiling SnapshotCeilingBytes — encode
    ///         fails explicitly instead of truncating racer state.
    ///   N5EB  host→client, reliable-ordered channel: one
    ///         NetworkEventBatch of transient authoritative events with
    ///         stable event identities.
    ///   N5RS  client→host, sequenced-unreliable channel: an explicit
    ///         resend request — the client's prediction backlog
    ///         resynchronized and it asks the host to publish the next
    ///         authoritative snapshot immediately rather than waiting
    ///         for the every-other-tick cadence.
    ///
    /// Decode applies the same strict per-type limits before parsing, so
    /// an oversized packet is dropped before any field is read; semantic
    /// validation (ownership, race/epoch fencing, finiteness, sequencing)
    /// lives in HostInputBuffer and NetworkRaceBridge.
    /// </summary>
    public static class NetworkWireCodec
    {
        public const string TagInputBatch = "N5IB";
        public const string TagSnapshot = "N5SS";
        public const string TagEventBatch = "N5EB";
        public const string TagResyncRequest = "N5RS";

        public const int HeaderBytes = 4;

        [Serializable]
        private sealed class InputBatchBody
        {
            public List<NetworkInputFrame> frames = new List<NetworkInputFrame>();
        }

        // The resync request carries session/race identity so the host can
        // fence requests from stale races, plus the claimed epoch so a
        // superseded connection's request is ignored.
        [Serializable]
        public sealed class ResyncBody
        {
            public string sessionId = "";
            public string raceId = "";
            public string participantId = "";
            public long ownershipEpoch;
        }

        private static readonly byte[] InputTag = Encoding.ASCII.GetBytes(TagInputBatch);
        private static readonly byte[] SnapshotTag = Encoding.ASCII.GetBytes(TagSnapshot);
        private static readonly byte[] EventTag = Encoding.ASCII.GetBytes(TagEventBatch);
        private static readonly byte[] ResyncTag = Encoding.ASCII.GetBytes(TagResyncRequest);

        /// <summary>True when the packet carries a Task-5 gameplay tag —
        /// used to split shared-channel traffic without parsing.</summary>
        public static bool IsGameplayPacket(byte[] packet)
        {
            return packet != null && packet.Length >= HeaderBytes
                && packet[0] == (byte)'N' && packet[1] == (byte)'5';
        }

        /// <summary>The 4-byte tag of a gameplay packet as text, or ""
        /// when the packet is too short or not a gameplay envelope.</summary>
        public static string TagOf(byte[] packet)
        {
            if (!IsGameplayPacket(packet)) return "";
            return Encoding.ASCII.GetString(packet, 0, HeaderBytes);
        }

        // --- input batch (client -> host) ---------------------------------

        public static bool TryEncodeInputBatch(IReadOnlyList<NetworkInputFrame> frames,
            out byte[] packet, out string error)
        {
            packet = null;
            error = null;
            if (frames == null || frames.Count == 0)
            { error = "emptyBatch"; return false; }
            if (frames.Count > NetworkSettings.InputPacketSampleCapacity)
            { error = "tooManySamples"; return false; }
            var json = JsonUtility.ToJson(new InputBatchBody
            {
                frames = new List<NetworkInputFrame>(frames)
            });
            packet = Wrap(InputTag, json);
            if (packet.Length > NetworkSettings.MaxInputPacketBytes)
            { packet = null; error = "oversizedPacket"; return false; }
            return true;
        }

        public static bool TryDecodeInputBatch(byte[] packet,
            out List<NetworkInputFrame> frames, out string error)
        {
            frames = null;
            error = null;
            string json;
            if (!Unwrap(packet, InputTag, NetworkSettings.MaxInputPacketBytes,
                    out json, out error)) return false;
            var body = Parse<InputBatchBody>(json, out error);
            if (body == null) return false;
            if (body.frames == null || body.frames.Count == 0
                || body.frames.Count > NetworkSettings.InputPacketSampleCapacity)
            { error = "badBatch"; return false; }
            frames = body.frames;
            return true;
        }

        // --- resync request (client -> host) --------------------------------

        public static bool TryEncodeResyncRequest(string sessionId,
            string raceId, string participantId, long ownershipEpoch,
            out byte[] packet, out string error)
        {
            error = null;
            var json = JsonUtility.ToJson(new ResyncBody
            {
                sessionId = sessionId ?? "",
                raceId = raceId ?? "",
                participantId = participantId ?? "",
                ownershipEpoch = ownershipEpoch
            });
            packet = Wrap(ResyncTag, json);
            if (packet.Length > NetworkSettings.MaxInputPacketBytes)
            { packet = null; error = "oversizedPacket"; return false; }
            return true;
        }

        public static bool TryDecodeResyncRequest(byte[] packet,
            out ResyncBody body, out string error)
        {
            body = null;
            string json;
            if (!Unwrap(packet, ResyncTag, NetworkSettings.MaxInputPacketBytes,
                    out json, out error)) return false;
            body = Parse<ResyncBody>(json, out error);
            if (body == null) { if (error == null) error = "malformed"; return false; }
            return true;
        }

        // --- snapshot (host -> client) --------------------------------------

        public static bool TryEncodeSnapshot(NetworkSnapshot snapshot,
            out byte[] packet, out string error)
        {
            packet = null;
            error = null;
            if (snapshot == null) { error = "invalidSnapshot"; return false; }
            var json = JsonUtility.ToJson(snapshot);
            packet = Wrap(SnapshotTag, json);
            // Explicit failure on overflow — never truncate racer state.
            if (packet.Length > NetworkSettings.SnapshotCeilingBytes)
            { packet = null; error = "snapshotOverflow"; return false; }
            return true;
        }

        public static bool TryDecodeSnapshot(byte[] packet,
            out NetworkSnapshot snapshot, out string error)
        {
            snapshot = null;
            error = null;
            string json;
            if (!Unwrap(packet, SnapshotTag, NetworkSettings.SnapshotCeilingBytes,
                    out json, out error)) return false;
            snapshot = Parse<NetworkSnapshot>(json, out error);
            if (snapshot == null || snapshot.state == null)
            { snapshot = null; if (error == null) error = "malformed"; return false; }
            // JsonUtility materializes a null object field as an empty
            // instance ({} decodes to a non-null default RaceResult). The
            // authority contract is result!=null <=> finalized, and a real
            // result always carries finalized=true — normalize the empty
            // decode back to null so clients cannot observe a phantom
            // outcome.
            if (snapshot.state.result != null && !snapshot.state.result.finalized)
                snapshot.state.result = null;
            return true;
        }

        // --- event batch (host -> client, reliable ordered) ------------------

        public static bool TryEncodeEventBatch(NetworkEventBatch batch,
            out byte[] packet, out string error)
        {
            packet = null;
            error = null;
            if (batch == null) { error = "invalidBatch"; return false; }
            var json = JsonUtility.ToJson(batch);
            packet = Wrap(EventTag, json);
            if (packet.Length > NetworkSettings.MaxEventBatchBytes)
            { packet = null; error = "eventBatchOverflow"; return false; }
            return true;
        }

        public static bool TryDecodeEventBatch(byte[] packet,
            out NetworkEventBatch batch, out string error)
        {
            batch = null;
            error = null;
            string json;
            if (!Unwrap(packet, EventTag, NetworkSettings.MaxEventBatchBytes,
                    out json, out error)) return false;
            batch = Parse<NetworkEventBatch>(json, out error);
            if (batch == null || batch.events == null)
            { batch = null; if (error == null) error = "malformed"; return false; }
            return true;
        }

        // --- internals --------------------------------------------------------

        private static byte[] Wrap(byte[] tag, string json)
        {
            var body = Encoding.UTF8.GetBytes(json ?? "");
            var packet = new byte[HeaderBytes + body.Length];
            Buffer.BlockCopy(tag, 0, packet, 0, HeaderBytes);
            Buffer.BlockCopy(body, 0, packet, HeaderBytes, body.Length);
            return packet;
        }

        private static bool Unwrap(byte[] packet, byte[] tag, int maxBytes,
            out string json, out string error)
        {
            json = null;
            error = null;
            if (packet == null || packet.Length <= HeaderBytes)
            { error = "badPacket"; return false; }
            if (packet.Length > maxBytes)
            { error = "oversizedPacket"; return false; }
            for (var i = 0; i < HeaderBytes; i++)
                if (packet[i] != tag[i]) { error = "wrongType"; return false; }
            json = Encoding.UTF8.GetString(packet, HeaderBytes,
                packet.Length - HeaderBytes);
            return true;
        }

        private static T Parse<T>(string json, out string error) where T : class
        {
            error = null;
            if (string.IsNullOrEmpty(json)) { error = "malformed"; return null; }
            try
            {
                return JsonUtility.FromJson<T>(json);
            }
            catch (Exception)
            {
                error = "malformed";
                return null;
            }
        }
    }
}
