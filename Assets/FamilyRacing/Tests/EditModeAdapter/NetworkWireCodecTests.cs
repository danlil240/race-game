using System.Collections.Generic;
using System.Text;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using FamilyRacing.Networking;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Task-5 wire byte ceilings: oversized gameplay packets fail
    // explicitly — decode rejects before a single field is parsed and
    // encode refuses rather than emitting a truncated racer state.
    // Editor-side tests because the codec lives in the Runtime assembly
    // (JsonUtility); the PlayMode suite covers the same bounds on live
    // traffic.
    public sealed class NetworkWireCodecTests
    {
        private static byte[] Tagged(string tag, int bodyBytes)
        {
            var packet = new byte[NetworkWireCodec.HeaderBytes + bodyBytes];
            var tagBytes = Encoding.ASCII.GetBytes(tag);
            for (var i = 0; i < tagBytes.Length; i++) packet[i] = tagBytes[i];
            return packet;
        }

        // A valid frame batch cannot reach the 4 KiB bound (4 samples x
        // 8 ids x 64 chars encodes well under it), so the input ceiling
        // exists to gate hostile or buggy peer traffic — decode-side.
        [Test]
        public void OversizedInputPacketRejectedBeforeParse()
        {
            var packet = Tagged(NetworkWireCodec.TagInputBatch,
                NetworkSettings.MaxInputPacketBytes);
            Assert.That(packet.Length,
                Is.GreaterThan(NetworkSettings.MaxInputPacketBytes));
            List<NetworkInputFrame> frames;
            string error;
            Assert.That(NetworkWireCodec.TryDecodeInputBatch(packet,
                out frames, out error), Is.False);
            Assert.That(error, Is.EqualTo("oversizedPacket"));
            Assert.That(frames, Is.Null);
        }

        [Test]
        public void OversizedSnapshotPacketRejectedBeforeParse()
        {
            var packet = Tagged(NetworkWireCodec.TagSnapshot,
                NetworkSettings.SnapshotCeilingBytes);
            Assert.That(packet.Length,
                Is.GreaterThan(NetworkSettings.SnapshotCeilingBytes));
            NetworkSnapshot snapshot;
            string error;
            Assert.That(NetworkWireCodec.TryDecodeSnapshot(packet,
                out snapshot, out error), Is.False);
            Assert.That(error, Is.EqualTo("oversizedPacket"));
            Assert.That(snapshot, Is.Null);
        }

        [Test]
        public void OversizedEventBatchPacketRejectedBeforeParse()
        {
            var packet = Tagged(NetworkWireCodec.TagEventBatch,
                NetworkSettings.MaxEventBatchBytes);
            Assert.That(packet.Length,
                Is.GreaterThan(NetworkSettings.MaxEventBatchBytes));
            NetworkEventBatch batch;
            string error;
            Assert.That(NetworkWireCodec.TryDecodeEventBatch(packet,
                out batch, out error), Is.False);
            Assert.That(error, Is.EqualTo("oversizedPacket"));
            Assert.That(batch, Is.Null);
        }

        // Encode-side contract: a snapshot whose body would exceed the
        // 16 KiB ceiling fails explicitly — packet stays null, nothing
        // is truncated or partially emitted.
        [Test]
        public void OversizedSnapshotEncodeFailsExplicitlyNeverTruncates()
        {
            var snapshot = new NetworkSnapshot
            {
                sessionId = "s",
                raceId = "r",
                raceOrdinal = 1,
                snapshotSequence = 1,
                state = new NetworkRaceState
                {
                    sessionId = "s",
                    raceId = "r",
                    racers =
                    {
                        new NetworkRacerState
                        {
                            participantId = new string('x',
                                NetworkSettings.SnapshotCeilingBytes)
                        }
                    }
                }
            };
            byte[] packet;
            string error;
            Assert.That(NetworkWireCodec.TryEncodeSnapshot(snapshot,
                out packet, out error), Is.False);
            Assert.That(error, Is.EqualTo("snapshotOverflow"));
            Assert.That(packet, Is.Null,
                "overflow must never emit a truncated snapshot");
        }

        [Test]
        public void OversizedEventBatchEncodeFailsExplicitly()
        {
            var batch = new NetworkEventBatch
            {
                sessionId = "s",
                raceId = "r",
                raceOrdinal = 1,
                batchSequence = 1
            };
            batch.events.Add(new DrivingEvent
            {
                id = "e1",
                detail = new string('x',
                    NetworkSettings.MaxEventBatchBytes)
            });
            byte[] packet;
            string error;
            Assert.That(NetworkWireCodec.TryEncodeEventBatch(batch,
                out packet, out error), Is.False);
            Assert.That(error, Is.EqualTo("eventBatchOverflow"));
            Assert.That(packet, Is.Null);
        }

        // The publish path inherits the codec ceiling: Publish refuses
        // an unencodable snapshot, records the failure, and does not
        // retain it as the latest published state.
        [Test]
        public void PublishDeclinesOversizedSnapshot()
        {
            var transport = new LanTransport(new StubAccess());
            var discovery = new LanDiscovery(new StubAccess());
            var link = new LanSessionLink(transport, discovery);
            var session = new LanSessionService(link, link, "codec-test");
            link.Bind(session);
            var bridge = new NetworkRaceBridge(transport, session);
            try
            {
                var oversized = new NetworkSnapshot
                {
                    sessionId = "s",
                    raceId = "r",
                    raceOrdinal = 1,
                    snapshotSequence = 1,
                    state = new NetworkRaceState
                    {
                        racers =
                        {
                            new NetworkRacerState
                            {
                                participantId = new string('x',
                                    NetworkSettings.SnapshotCeilingBytes)
                            }
                        }
                    }
                };
                Assert.That(bridge.Publish(oversized), Is.False);
                Assert.That(bridge.LastError, Is.Not.Empty);
                Assert.That(bridge.CurrentSnapshot, Is.Null,
                    "a refused snapshot must not become 'latest'");
            }
            finally
            {
                bridge.Dispose();
                link.Dispose();
            }
        }

        // Task 7: brake and throttle are semantic wire values — the
        // round trip must preserve a partial brake demand and the
        // absent-vs-explicit-zero throttle distinction, because the host
        // reconstructs DriverInput from the decoded frame fields.
        [Test]
        public void InputBatchRoundTripPreservesBrakeAndThrottle()
        {
            var frames = new List<NetworkInputFrame>
            {
                new NetworkInputFrame
                {
                    sessionId = "s", raceId = "r", participantId = "p",
                    ownershipEpoch = 2, inputSequence = 41, clientTick = 40,
                    steering = -0.5f, drift = true, boost = false,
                    recover = false, changeCamera = false, pause = false,
                    brake = 0.6f,
                    hasThrottleOverride = true, throttle = 0f
                },
                new NetworkInputFrame
                {
                    sessionId = "s", raceId = "r", participantId = "p",
                    ownershipEpoch = 2, inputSequence = 42, clientTick = 41,
                    steering = 0f, brake = 0f,
                    hasThrottleOverride = false, throttle = 0f
                }
            };
            byte[] packet;
            string error;
            Assert.That(NetworkWireCodec.TryEncodeInputBatch(frames,
                out packet, out error), Is.True, error);
            List<NetworkInputFrame> decoded;
            Assert.That(NetworkWireCodec.TryDecodeInputBatch(packet,
                out decoded, out error), Is.True, error);
            Assert.That(decoded.Count, Is.EqualTo(2));

            var first = decoded[0];
            Assert.That(first.brake, Is.EqualTo(0.6f).Within(0.0001f));
            Assert.That(first.hasThrottleOverride, Is.True);
            Assert.That(first.throttle, Is.EqualTo(0f).Within(0.0001f));
            // Explicit zero decodes as a real coast — not automatic.
            Assert.That(first.input.HasThrottleOverride, Is.True);
            Assert.That(first.input.Throttle, Is.EqualTo(0f));
            Assert.That(first.input.Brake, Is.EqualTo(0.6f).Within(0.0001f));

            // A frame without an override restores automatic throttle.
            var second = decoded[1];
            Assert.That(second.hasThrottleOverride, Is.False);
            Assert.That(second.input.HasThrottleOverride, Is.False);
            Assert.That(second.input.Throttle, Is.EqualTo(1f));
            Assert.That(second.input.Brake, Is.EqualTo(0f));
        }

        // A peer running a Task-5 build emits frames without the new
        // fields — decode defaults keep automatic throttle and no brake
        // rather than manufacturing an ambiguous zero override.
        [Test]
        public void LegacyInputFrameDecodesWithAutomaticThrottle()
        {
            var json = "{\"frames\":[{\"sessionId\":\"s\",\"raceId\":\"r\"," +
                "\"participantId\":\"p\",\"ownershipEpoch\":1," +
                "\"inputSequence\":1,\"clientTick\":1,\"steering\":0.25," +
                "\"drift\":false,\"boost\":true,\"gadget\":false," +
                "\"recover\":false,\"changeCamera\":false,\"pause\":false," +
                "\"actionIds\":[]}]}";
            var body = Encoding.UTF8.GetBytes(json);
            var packet = new byte[NetworkWireCodec.HeaderBytes + body.Length];
            var tag = Encoding.ASCII.GetBytes(NetworkWireCodec.TagInputBatch);
            for (var i = 0; i < tag.Length; i++) packet[i] = tag[i];
            System.Buffer.BlockCopy(body, 0, packet,
                NetworkWireCodec.HeaderBytes, body.Length);
            List<NetworkInputFrame> decoded;
            string error;
            Assert.That(NetworkWireCodec.TryDecodeInputBatch(packet,
                out decoded, out error), Is.True, error);
            Assert.That(decoded.Count, Is.EqualTo(1));
            var frame = decoded[0];
            Assert.That(frame.hasThrottleOverride, Is.False);
            Assert.That(frame.brake, Is.EqualTo(0f));
            Assert.That(frame.input.HasThrottleOverride, Is.False);
            Assert.That(frame.input.Throttle, Is.EqualTo(1f));
            Assert.That(frame.input.Brake, Is.EqualTo(0f));
            Assert.That(frame.input.Steering, Is.EqualTo(0.25f).Within(0.001f));
        }

        // JsonUtility materializes a null object field as an empty
        // instance; decode normalizes a non-finalized result back to
        // null so clients can never observe a phantom outcome.
        [Test]
        public void NullResultDecodesAsNullNotEmptyInstance()
        {
            var snapshot = new NetworkSnapshot
            {
                sessionId = "s",
                raceId = "r",
                raceOrdinal = 1,
                snapshotSequence = 1,
                state = new NetworkRaceState { sessionId = "s", raceId = "r" }
            };
            byte[] packet;
            string error;
            Assert.That(NetworkWireCodec.TryEncodeSnapshot(snapshot,
                out packet, out error), Is.True, error);
            NetworkSnapshot decoded;
            Assert.That(NetworkWireCodec.TryDecodeSnapshot(packet,
                out decoded, out error), Is.True, error);
            Assert.That(decoded.state.result, Is.Null);
        }

        [Test]
        public void WrongTagShortAndMalformedPacketsRejectCleanly()
        {
            List<NetworkInputFrame> frames;
            string error;
            Assert.That(NetworkWireCodec.TryDecodeInputBatch(
                new byte[] { 0, 1 }, out frames, out error), Is.False);
            Assert.That(error, Is.EqualTo("badPacket"));
            var foreign = Tagged("FRN1", 8);
            Assert.That(NetworkWireCodec.TryDecodeInputBatch(foreign,
                out frames, out error), Is.False);
            Assert.That(error, Is.EqualTo("wrongType"));
            var garbage = Tagged(NetworkWireCodec.TagInputBatch, 16);
            Assert.That(NetworkWireCodec.TryDecodeInputBatch(garbage,
                out frames, out error), Is.False);
            Assert.That(error, Is.EqualTo("malformed"));
        }

        private sealed class StubAccess : ILanNetworkAccess
        {
            public LanNetworkAccessState State =>
                LanNetworkAccessState.Available;
            public string Detail => "stub";
            public void EnsureAccess() { }
        }
    }
}
