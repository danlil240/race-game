using System.Collections.Generic;
using System.Text;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using FamilyRacing.Networking;
using NUnit.Framework;
using UnityEngine;

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
        // snapshot ceiling fails explicitly — packet stays null, nothing
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
            var foreign = Tagged("FRN2", 8);
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

        // --- Task 9: versioned physical snapshots ----------------------------

        // An airborne, upside-down-leaning physical state with live
        // wheel/controller histories — the worst shape the compact wire
        // DTO must round-trip.
        private static MotorState PhysicalMotor()
        {
            var physics = new VehiclePhysicsState
            {
                velocityX = 0.5f, velocityY = 1.25f, velocityZ = 9.5f,
                angularVelocityX = 0.4f, angularVelocityY = -0.2f,
                angularVelocityZ = 0.1f,
                filteredSteering = -0.12f, filteredRollRate = 0.03f,
                balancePrevError = 0.01f, filteredLeanTarget = -0.05f,
                driftAmount = 0.4f,
                grounded = false, airSeconds = 0.4f, airPeakY = 3.4f,
                airWallContact = true,
                overturnedSeconds = 0.1f, contactLossSeconds = 0.05f,
                lastContact = "vehicle", recoveryEpoch = 4,
                wheels = new WheelPhysicsState[4]
            };
            physics.TrySetAttitude(0.5f, 0.2f, -0.3f, 0.8f);
            for (var i = 0; i < physics.wheels.Length; i++)
                physics.wheels[i] = new WheelPhysicsState
                {
                    index = i,
                    grounded = i < 2,
                    compression = 0.05f + i * 0.01f,
                    previousCompression = 0.04f + i * 0.01f,
                    contactValid = true,
                    steering = i < 2 ? 0.2f : 0f,
                    spin = 3f + i,
                    // Diagnostic-only fields: populated to prove they
                    // never reach the wire.
                    normalLoad = 4000f + i,
                    slipLongitudinal = 0.7f,
                    slipLateral = -0.3f
                };
            return new MotorState
            {
                x = 1.5f, y = 2.8f, z = -3.25f, heading = 0.6f,
                speed = 9.4f, lateralSpeed = -1.1f, verticalSpeed = 1.25f,
                grounded = false, airSeconds = 0.4f,
                drifting = true, driftSign = -1,
                boostCharge = 0.6f, boostActive = true,
                boostSecondsRemaining = 1.2f, boostLatched = true,
                airWallContact = true, airPeakY = 3.4f,
                recovering = false, lastContact = "vehicle", tick = 77,
                physics = physics
            };
        }

        private static NetworkRacerState PhysicalRacer(int slot,
            int idChars)
        {
            // A live mid-race session shape, not a fresh spawn: gate
            // marks, held input, camera/tutorial/controls facts, the
            // 64-char content fingerprint and the recovery bookkeeping a
            // real boundary accumulates — the skeletal fixture let the
            // snapshot "stay well under" a ceiling the real race crossed
            // (run-physics-t11-s: snapshotOverflow after 45 publishes).
            var session = new DrivingSessionState
            {
                raceId = "race-net" + new string('r', idChars),
                vehicleId = "car-01" + new string('v', idChars),
                eventId = "evt-net" + new string('e', idChars),
                courseId = "course-net" + new string('c', idChars),
                contentVersion = "physics-1|sim=1",
                contentFingerprint = new string('f', 64),
                phase = "running",
                nextCheckpoint = 3,
                lap = 2,
                motor = PhysicalMotor(),
                input = new InputState
                {
                    steering = -0.31f,
                    drift = true,
                    boost = false,
                    throttle = 1f,
                    hasThrottleOverride = true
                },
                lastRecoveryReason = "route-trap#2"
            };
            for (var gate = 0; gate < 8; gate++)
                session.checkpoints.Add(new CheckpointMark
                {
                    id = "gate-" + gate,
                    index = gate,
                    crossed = true
                });
            return new NetworkRacerState
            {
                participantId = "p-" + slot
                    + new string('q', idChars),
                slot = slot,
                vehicleId = "car-01" + new string('v', idChars),
                attribution = "human",
                position = slot + 1,
                session = session
            };
        }

        private static NetworkSnapshot PhysicalSnapshot(int racerCount,
            int idChars = 8)
        {
            var snapshot = new NetworkSnapshot
            {
                sessionId = "lan-" + new string('s', idChars),
                raceId = "race-" + new string('r', idChars),
                raceOrdinal = 1,
                snapshotSequence = 12,
                state = new NetworkRaceState
                {
                    sessionId = "lan-" + new string('s', idChars),
                    raceId = "race-" + new string('r', idChars),
                    phase = "running",
                    hostTick = 24,
                    elapsedTicks = 22
                }
            };
            for (var i = 0; i < racerCount; i++)
                snapshot.state.racers.Add(PhysicalRacer(i, idChars));
            return snapshot;
        }

        private static byte[] Wrap(string tag, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var packet = new byte[NetworkWireCodec.HeaderBytes + body.Length];
            var tagBytes = Encoding.ASCII.GetBytes(tag);
            System.Buffer.BlockCopy(tagBytes, 0, packet, 0, tagBytes.Length);
            System.Buffer.BlockCopy(body, 0, packet,
                NetworkWireCodec.HeaderBytes, body.Length);
            return packet;
        }

        [Test]
        public void PhysicalSnapshotRoundTripsReplayState()
        {
            var snapshot = PhysicalSnapshot(2);
            var source = snapshot.state.racers[1].session.motor;
            byte[] packet;
            string error;
            Assert.That(NetworkWireCodec.TryEncodeSnapshot(snapshot,
                out packet, out error), Is.True, error);
            NetworkSnapshot decoded;
            Assert.That(NetworkWireCodec.TryDecodeSnapshot(packet,
                out decoded, out error), Is.True, error);

            var restored = decoded.state.racers[1].session.motor;
            Assert.That(restored, Is.Not.Null);
            Assert.That(restored.physics, Is.Not.Null);
            var expected = source.physics;
            var actual = restored.physics;
            Assert.That(actual.attitudeX,
                Is.EqualTo(expected.attitudeX).Within(1e-4f));
            Assert.That(actual.attitudeY,
                Is.EqualTo(expected.attitudeY).Within(1e-4f));
            Assert.That(actual.attitudeZ,
                Is.EqualTo(expected.attitudeZ).Within(1e-4f));
            Assert.That(actual.attitudeW,
                Is.EqualTo(expected.attitudeW).Within(1e-4f));
            Assert.That(actual.velocityZ,
                Is.EqualTo(expected.velocityZ).Within(1e-4f));
            Assert.That(actual.angularVelocityY,
                Is.EqualTo(expected.angularVelocityY).Within(1e-4f));
            Assert.That(actual.recoveryEpoch,
                Is.EqualTo(expected.recoveryEpoch));
            Assert.That(actual.filteredSteering,
                Is.EqualTo(expected.filteredSteering).Within(1e-4f));
            Assert.That(actual.driftAmount,
                Is.EqualTo(expected.driftAmount).Within(1e-4f));
            Assert.That(actual.airSeconds,
                Is.EqualTo(expected.airSeconds).Within(1e-4f));
            Assert.That(actual.lastContact,
                Is.EqualTo(expected.lastContact));
            Assert.That(actual.wheels.Length,
                Is.EqualTo(expected.wheels.Length));
            for (var i = 0; i < actual.wheels.Length; i++)
            {
                Assert.That(actual.wheels[i].index, Is.EqualTo(i));
                Assert.That(actual.wheels[i].grounded,
                    Is.EqualTo(expected.wheels[i].grounded));
                Assert.That(actual.wheels[i].compression,
                    Is.EqualTo(expected.wheels[i].compression)
                        .Within(1e-4f));
                Assert.That(actual.wheels[i].previousCompression,
                    Is.EqualTo(expected.wheels[i].previousCompression)
                        .Within(1e-4f));
                Assert.That(actual.wheels[i].contactValid,
                    Is.EqualTo(expected.wheels[i].contactValid));
                Assert.That(actual.wheels[i].steering,
                    Is.EqualTo(expected.wheels[i].steering).Within(1e-4f));
                Assert.That(actual.wheels[i].spin,
                    Is.EqualTo(expected.wheels[i].spin).Within(1e-4f));
            }
            // The decoded motor is valid input for the authoritative
            // restore path.
            string stateError;
            Assert.That(actual.Validate(out stateError), Is.True,
                stateError);
        }

        [Test]
        public void PhysicalSnapshotWireCarriesCompactDtoOnly()
        {
            var snapshot = PhysicalSnapshot(2);
            byte[] packet;
            string error;
            Assert.That(NetworkWireCodec.TryEncodeSnapshot(snapshot,
                out packet, out error), Is.True, error);
            var body = Encoding.UTF8.GetString(packet,
                NetworkWireCodec.HeaderBytes,
                packet.Length - NetworkWireCodec.HeaderBytes);
            // The compact DTO rides `racer.physics`; diagnostic-only
            // contact data must never reach the wire.
            Assert.That(body, Does.Contain("\"physics\""));
            Assert.That(body, Does.Not.Contain("normalLoad"));
            Assert.That(body, Does.Not.Contain("slipLongitudinal"));
            Assert.That(body, Does.Not.Contain("slipLateral"));
            Assert.That(body, Does.Not.Contain("contactX"));
            var wire = JsonUtility.FromJson<NetworkSnapshot>(body);
            Assert.That(wire.state.racers[0].physics, Is.Not.Null);
            Assert.That(wire.state.racers[0].physics.present, Is.True);
            // The envelope stays on session.motor; its inline physics
            // member is stripped for the wire.
            Assert.That(wire.state.racers[0].session.motor, Is.Not.Null);
            Assert.That(wire.state.racers[0].session.motor.boostCharge,
                Is.EqualTo(0.6f).Within(1e-4f));
            Assert.That(
                wire.state.racers[0].session.motor.physics == null
                    || wire.state.racers[0].session.motor.physics
                        .wheels.Length == 0,
                Is.True, "inline diagnostic physics stays off the wire");
        }

        [Test]
        public void PhysicalSnapshotEncodeLeavesSourceUntouched()
        {
            var snapshot = PhysicalSnapshot(1);
            byte[] packet;
            string error;
            Assert.That(NetworkWireCodec.TryEncodeSnapshot(snapshot,
                out packet, out error), Is.True, error);
            Assert.That(snapshot.state.racers[0].session.motor.physics,
                Is.Not.Null,
                "encoding must never strip the host's live physics state");
            Assert.That(snapshot.state.racers[0].physics == null
                || !snapshot.state.racers[0].physics.present,
                Is.True);
        }

        [Test]
        public void PhysicalSnapshotRejectsMalformedWireStateAtomically()
        {
            // One malformed racer DTO kills the whole packet — partial
            // state must never be applied.
            var wire = new NetworkSnapshot
            {
                sessionId = "s", raceId = "r", raceOrdinal = 1,
                snapshotSequence = 1,
                state = new NetworkRaceState
                {
                    sessionId = "s", raceId = "r", phase = "running",
                    racers =
                    {
                        PhysicalRacer(0, 4),
                        new NetworkRacerState
                        {
                            participantId = "bad-1",
                            slot = 1,
                            session = new DrivingSessionState
                            {
                                motor = new MotorState()
                            },
                            physics = new NetworkPhysicsState
                            {
                                present = true,
                                attitudeW = 3f // non-unit quaternion
                            }
                        }
                    }
                }
            };
            // Strip the valid racer's inline physics so only the DTO
            // distinguishes the two paths.
            wire.state.racers[0].session.motor.physics = null;
            var packet = Wrap(NetworkWireCodec.TagSnapshot,
                JsonUtility.ToJson(wire));
            NetworkSnapshot decoded;
            string error;
            Assert.That(NetworkWireCodec.TryDecodeSnapshot(packet,
                out decoded, out error), Is.False);
            Assert.That(decoded, Is.Null,
                "a malformed physical DTO rejects the whole snapshot");
        }

        [Test]
        public void PhysicalSnapshotRejectsMalformedInlineMotor()
        {
            // A snapshot still carrying an inline (non-DTO) physics
            // state that fails validation is refused the same way.
            var wire = PhysicalSnapshot(1);
            wire.state.racers[0].session.motor.physics.driftAmount = 2f;
            var packet = Wrap(NetworkWireCodec.TagSnapshot,
                JsonUtility.ToJson(wire));
            NetworkSnapshot decoded;
            string error;
            Assert.That(NetworkWireCodec.TryDecodeSnapshot(packet,
                out decoded, out error), Is.False);
            Assert.That(decoded, Is.Null);
        }

        [Test]
        public void EmptyPhysicsDtoDecodesAsAbsent()
        {
            // JsonUtility materializes a missing/absent DTO as an empty
            // instance; `present` is the explicit wire marker — an empty
            // DTO must not fabricate a motor.
            var wire = new NetworkSnapshot
            {
                sessionId = "s", raceId = "r", raceOrdinal = 1,
                snapshotSequence = 1,
                state = new NetworkRaceState
                {
                    sessionId = "s", raceId = "r", phase = "running",
                    racers =
                    {
                        new NetworkRacerState
                        {
                            participantId = "arcade-1",
                            slot = 0,
                            session = new DrivingSessionState
                            {
                                motor = new MotorState { speed = 5f }
                            }
                            // physics absent -> {} -> present=false
                        }
                    }
                }
            };
            var packet = Wrap(NetworkWireCodec.TagSnapshot,
                JsonUtility.ToJson(wire));
            NetworkSnapshot decoded;
            string error;
            Assert.That(NetworkWireCodec.TryDecodeSnapshot(packet,
                out decoded, out error), Is.True, error);
            var motor = decoded.state.racers[0].session.motor;
            Assert.That(motor, Is.Not.Null);
            Assert.That(motor.physics, Is.Null,
                "an absent physical DTO must not mint physical state — "
                + "the materialized default instance is normalized away");
        }

        [Test]
        public void FourRacerMaxIdentitySnapshotStaysUnderCeiling()
        {
            // Worst case: four physical racers at the 64-char identity
            // bound with 60-char session/race ids in live mid-race
            // session shape — the compact DTO keeps the legal maximum
            // inside the snapshot ceiling.
            var snapshot = PhysicalSnapshot(4, 56);
            byte[] packet;
            string error;
            Assert.That(NetworkWireCodec.TryEncodeSnapshot(snapshot,
                out packet, out error), Is.True, error);
            Assert.That(packet.Length,
                Is.LessThanOrEqualTo(NetworkSettings.SnapshotCeilingBytes));
            NetworkSnapshot decoded;
            Assert.That(NetworkWireCodec.TryDecodeSnapshot(packet,
                out decoded, out error), Is.True, error);
            Assert.That(decoded.state.racers.Count, Is.EqualTo(4));
        }

        [Test]
        public void SnapshotWithNullRacerListRejectsCleanly()
        {
            // A crafted "racers":null body parses to a null list — the
            // codec must refuse it as malformed instead of dereferencing
            // it inside the physical fold.
            var nullRacers = Wrap(NetworkWireCodec.TagSnapshot,
                "{\"sessionId\":\"s\",\"raceId\":\"r\",\"raceOrdinal\":1,"
                + "\"snapshotSequence\":1,\"state\":{\"sessionId\":\"s\","
                + "\"raceId\":\"r\",\"phase\":\"running\",\"racers\":null},"
                + "\"acks\":[]}");
            NetworkSnapshot decoded;
            string error;
            Assert.That(NetworkWireCodec.TryDecodeSnapshot(nullRacers,
                out decoded, out error), Is.False);
            Assert.That(decoded, Is.Null);
            Assert.That(error, Is.EqualTo("malformed"));
        }

        [Test]
        public void SnapshotWithNullStateRejectsCleanly()
        {
            var nullState = Wrap(NetworkWireCodec.TagSnapshot,
                "{\"sessionId\":\"s\",\"raceId\":\"r\",\"raceOrdinal\":1,"
                + "\"snapshotSequence\":1,\"state\":null,\"acks\":[]}");
            NetworkSnapshot decoded;
            string error;
            Assert.That(NetworkWireCodec.TryDecodeSnapshot(nullState,
                out decoded, out error), Is.False);
            Assert.That(decoded, Is.Null);
            Assert.That(error, Is.EqualTo("malformed"));
        }

        [Test]
        public void SnapshotWithNonObjectStateRejectsCleanly()
        {
            // A crafted "state" carrying a scalar or array must never
            // throw through the decoder — the wrong JSON kind is a clean
            // malformed rejection, not an escaped exception.
            foreach (var stateJson in new[] { "\"x\"", "5", "true", "[]" })
            {
                var packet = Wrap(NetworkWireCodec.TagSnapshot,
                    "{\"sessionId\":\"s\",\"raceId\":\"r\",\"raceOrdinal\":1,"
                    + "\"snapshotSequence\":1,\"state\":" + stateJson
                    + ",\"acks\":[]}");
                NetworkSnapshot decoded;
                string error;
                Assert.That(NetworkWireCodec.TryDecodeSnapshot(packet,
                    out decoded, out error), Is.False, stateJson);
                Assert.That(decoded, Is.Null);
                Assert.That(error, Is.EqualTo("malformed"), stateJson);
            }
        }

        [Test]
        public void SnapshotWithNonArrayRacersRejectsCleanly()
        {
            foreach (var racersJson in new[] { "\"x\"", "5", "{}" })
            {
                var packet = Wrap(NetworkWireCodec.TagSnapshot,
                    "{\"sessionId\":\"s\",\"raceId\":\"r\",\"raceOrdinal\":1,"
                    + "\"snapshotSequence\":1,\"state\":{\"sessionId\":\"s\","
                    + "\"raceId\":\"r\",\"phase\":\"running\",\"racers\":"
                    + racersJson + "},\"acks\":[]}");
                NetworkSnapshot decoded;
                string error;
                Assert.That(NetworkWireCodec.TryDecodeSnapshot(packet,
                    out decoded, out error), Is.False, racersJson);
                Assert.That(decoded, Is.Null);
                Assert.That(error, Is.EqualTo("malformed"), racersJson);
            }
        }

        [Test]
        public void SnapshotEncodeRejectsNullRacerList()
        {
            var snapshot = new NetworkSnapshot
            {
                sessionId = "s", raceId = "r", raceOrdinal = 1,
                snapshotSequence = 1,
                state = new NetworkRaceState
                {
                    sessionId = "s", raceId = "r", phase = "running",
                    racers = null
                }
            };
            byte[] packet;
            string error;
            Assert.That(NetworkWireCodec.TryEncodeSnapshot(snapshot,
                out packet, out error), Is.False);
            Assert.That(packet, Is.Null);
            Assert.That(error, Is.EqualTo("malformed"));
        }

        [Test]
        public void LegacyN5TagsAreNotGameplayPackets()
        {
            // Protocol 2 gameplay uses P1** tags; legacy N5** traffic is
            // fenced at the envelope before any body is read.
            Assert.That(NetworkWireCodec.IsGameplayPacket(
                Tagged(NetworkWireCodec.TagSnapshot, 4)), Is.True);
            Assert.That(NetworkWireCodec.TagSnapshot, Is.EqualTo("P1SS"));
            Assert.That(NetworkWireCodec.TagInputBatch, Is.EqualTo("P1IB"));
            Assert.That(NetworkWireCodec.TagEventBatch, Is.EqualTo("P1EB"));
            Assert.That(NetworkWireCodec.TagResyncRequest,
                Is.EqualTo("P1RS"));
            var legacy = Tagged("N5SS", 8);
            Assert.That(NetworkWireCodec.IsGameplayPacket(legacy),
                Is.False);
            NetworkSnapshot snapshot;
            string error;
            Assert.That(NetworkWireCodec.TryDecodeSnapshot(legacy,
                out snapshot, out error), Is.False);
            Assert.That(error, Is.EqualTo("wrongType"));
            List<NetworkInputFrame> frames;
            Assert.That(NetworkWireCodec.TryDecodeInputBatch(
                Tagged("N5IB", 8), out frames, out error), Is.False);
            Assert.That(error, Is.EqualTo("wrongType"));
        }
    }
}
