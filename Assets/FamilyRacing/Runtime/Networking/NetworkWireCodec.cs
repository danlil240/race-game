using System;
using System.Collections.Generic;
using System.Text;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FamilyRacing.Networking
{
    /// <summary>
    /// Bounded typed codec for the Task-9 gameplay wire (protocol 3 —
    /// Task 5 gadget state extends the snapshot body).
    /// Every packet is a 4-byte ASCII tag followed by a UTF-8 JSON body
    /// produced by JsonUtility against the serializable Core DTOs:
    ///
    ///   P1IB  client→host, sequenced-unreliable channel: a batch of up to
    ///         InputPacketSampleCapacity recent NetworkInputFrames.
    ///   P1SS  host→client, reliable-ordered channel: one full
    ///         NetworkSnapshot. Hard ceiling SnapshotCeilingBytes — encode
    ///         fails explicitly instead of truncating racer state. Each
    ///         racer's physical state travels as the compact versioned
    ///         NetworkPhysicsState DTO on racer.physics; the diagnostic
    ///         MotorState.physics view never crosses the wire.
    ///   P1EB  host→client, reliable-ordered channel: one
    ///         NetworkEventBatch of transient authoritative events with
    ///         stable event identities.
    ///   P1RS  client→host, sequenced-unreliable channel: an explicit
    ///         resend request — the client's prediction backlog
    ///         resynchronized and it asks the host to publish the next
    ///         authoritative snapshot immediately rather than waiting
    ///         for the every-other-tick cadence.
    ///
    /// Decode applies the same strict per-type limits before parsing, so
    /// an oversized packet is dropped before any field is read; semantic
    /// validation (ownership, race/epoch fencing, finiteness, sequencing)
    /// lives in HostInputBuffer and NetworkRaceBridge. Physical-state
    /// validation is atomic here: one malformed DTO or inline state
    /// kills the whole snapshot before anything is applied.
    /// </summary>
    public static class NetworkWireCodec
    {
        public const string TagInputBatch = "P1IB";
        public const string TagSnapshot = "P1SS";
        public const string TagEventBatch = "P1EB";
        public const string TagResyncRequest = "P1RS";

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

        /// <summary>True when the packet carries a protocol-2 gameplay
        /// tag — used to split shared-channel traffic without parsing.
        /// Legacy N5** envelopes are not gameplay packets: they belong
        /// to protocol 1 and are fenced here before any body is read.
        /// </summary>
        public static bool IsGameplayPacket(byte[] packet)
        {
            return packet != null && packet.Length >= HeaderBytes
                && packet[0] == (byte)'P' && packet[1] == (byte)'1';
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
            // A state without a racer list is malformed content — reject
            // before the deep copy can dereference it.
            if (snapshot.state != null && snapshot.state.racers == null)
            { error = "malformed"; return false; }
            // The wire projection is a deep copy: racer.physics carries
            // the compact DTO and the diagnostic MotorState.physics is
            // stripped — the host's live authoritative state is never
            // mutated by serialization.
            var wire = new NetworkSnapshot
            {
                sessionId = snapshot.sessionId,
                raceId = snapshot.raceId,
                raceOrdinal = snapshot.raceOrdinal,
                snapshotSequence = snapshot.snapshotSequence,
                state = snapshot.state != null ? snapshot.state.Copy() : null,
                acks = snapshot.acks
            };
            if (wire.state != null)
            {
                foreach (var racer in wire.state.racers)
                {
                    if (racer == null) continue;
                    var motor = racer.session != null
                        ? racer.session.motor : null;
                    racer.physics = NetworkPhysicsState.FromMotor(motor);
                    if (motor != null) motor.physics = null;
                }
            }
            var json = JsonUtility.ToJson(wire);
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
            // JsonUtility materializes an explicit JSON null on an object
            // or list field as a fresh default instance — "state":null
            // and "racers":null would decode as valid empty content
            // rather than the malformed wire data they are. Inspect the
            // payload itself before parsing: an absent field is caught
            // by the semantic checks below, but a required structure of
            // the wrong JSON kind (null, scalar, or mismatched container)
            // rejects here.
            if (HasMalformedStructure(json))
            { error = "malformed"; return false; }
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
            // Gadget world (protocol 3): a stripped or absent field
            // decodes as the empty instance — normalize back to null so
            // a gadget-free race never masquerades as gadget-enabled.
            // A present world validates atomically: one malformed or
            // unbounded payload kills the whole snapshot before any
            // client state can observe a partial gadget application.
            if (snapshot.state.gadgets != null)
            {
                if (IsEmptyGadgetWorld(snapshot.state.gadgets))
                {
                    snapshot.state.gadgets = null;
                }
                else if (!snapshot.state.gadgets.ValidateWire(out error))
                {
                    snapshot = null;
                    return false;
                }
            }
            // A snapshot without a racer list is malformed wire content,
            // not a clean parse — reject before the fold can dereference
            // it. (JsonUtility materializes "racers":null as a null
            // list, so the guard must live above the enumeration.)
            if (snapshot.state.racers == null)
            { snapshot = null; error = "malformed"; return false; }
            // Fold each racer's compact physical DTO back onto the motor
            // atomically: one malformed physical payload rejects the
            // whole snapshot before any client state can observe a
            // partial application.
            foreach (var racer in snapshot.state.racers)
            {
                if (racer == null) continue;
                if (!FoldPhysicalState(racer, out error))
                { snapshot = null; return false; }
            }
            return true;
        }

        // True when the snapshot body carries a required structural field
        // of the wrong JSON kind: `state` present but null/scalar/array
        // (only an object can hold a race state), or `state.racers`
        // present but null/scalar/object (only an array can hold the
        // racer list). Token-type checks never index a non-JObject —
        // crafted scalars/arrays fall through to clean rejection instead
        // of throwing through the transport callback. Absent fields and
        // unparseable bodies fall through — the semantic checks and
        // Parse<T> report them as malformed on their own.
        private static bool HasMalformedStructure(string json)
        {
            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception)
            {
                return false;
            }
            var state = root["state"];
            if (state != null && !(state is JObject)) return true;
            var racers = state is JObject stateObject
                ? stateObject["racers"] : null;
            if (racers != null && !(racers is JArray)) return true;
            var gadgets = state is JObject stateForGadgets
                ? stateForGadgets["gadgets"] : null;
            return gadgets != null && !(gadgets is JObject);
        }

        // True when a decoded GadgetWorldState is the absent-field
        // artifact rather than a sent payload: a live authoritative
        // world always carries its roster rows, so an empty racer list
        // on an otherwise default instance means the wire carried no
        // gadget content at all.
        private static bool IsEmptyGadgetWorld(GadgetWorldState world)
        {
            return world.tick == 0 && world.rngState == 0
                && world.bagCursor == 0 && world.nextPickupGeneration == 0
                && world.nextPatchSerial == 0
                && (world.itemBag == null || world.itemBag.Count == 0)
                && (world.pickups == null || world.pickups.Count == 0)
                && (world.patches == null || world.patches.Count == 0)
                && (world.racers == null || world.racers.Count == 0);
        }

        // The DTO is the wire contract: when present it validates as a
        // whole, then rebuilds the motor's physical state through the
        // same gate the authority uses. Without a DTO, any surviving
        // inline MotorState.physics is out-of-contract content — a
        // stripped artifact decodes as the empty instance and is
        // normalized away, while a real payload must pass the authority
        // validation or the packet is malformed.
        private static bool FoldPhysicalState(NetworkRacerState racer,
            out string error)
        {
            error = null;
            var motor = racer.session != null ? racer.session.motor : null;
            var dto = racer.physics;
            if (dto != null && dto.present)
            {
                if (!dto.Validate(out error))
                { error = "invalidPhysics"; return false; }
                if (motor != null) motor.physics = dto.ToMotor().physics;
                return true;
            }
            if (motor == null || motor.physics == null) return true;
            if (IsEmptyWirePhysics(motor.physics))
            { motor.physics = null; return true; }
            string inlineError;
            if (!motor.physics.Validate(out inlineError))
            { error = "invalidPhysics"; return false; }
            return true;
        }

        // True when a decoded MotorState.physics is the stripped-encode
        // artifact rather than a sent payload: JsonUtility materializes
        // the null member as a fresh VehiclePhysicsState — the type's own
        // default instance — identity attitude (field initializer gives
        // attitudeW=1, not 0), no wheels, no episode state, the default
        // contact label. A real payload is never that shape: an authored
        // body's wheel array always matches the tuning's wheel count.
        private static bool IsEmptyWirePhysics(VehiclePhysicsState physics)
        {
            if (physics.wheels != null && physics.wheels.Length > 0) return false;
            if (physics.attitudeX != 0f || physics.attitudeY != 0f
                || physics.attitudeZ != 0f || physics.attitudeW != 1f) return false;
            if (physics.velocityX != 0f || physics.velocityY != 0f
                || physics.velocityZ != 0f) return false;
            if (physics.angularVelocityX != 0f || physics.angularVelocityY != 0f
                || physics.angularVelocityZ != 0f) return false;
            if (physics.filteredSteering != 0f || physics.filteredRollRate != 0f
                || physics.balancePrevError != 0f
                || physics.filteredLeanTarget != 0f
                || physics.driftAmount != 0f) return false;
            if (physics.grounded || physics.airSeconds != 0f
                || physics.airPeakY != 0f || physics.airWallContact
                || physics.overturnedSeconds != 0f
                || physics.contactLossSeconds != 0f
                || physics.recoveryEpoch != 0) return false;
            return string.IsNullOrEmpty(physics.lastContact)
                || physics.lastContact == "none";
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
