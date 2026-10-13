using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FamilyRacing.Core.Driving;

namespace FamilyRacing.Core.Racing
{
    // Deterministic canonical fingerprint of a DrivingContentCatalog's
    // tuned data — the admission token a join.request carries alongside
    // the human-readable content version. Two catalogs fingerprint
    // identically iff every replay-relevant tuned value is bit-identical;
    // the authored version string, dictionary insertion order and
    // delivery/presentation metadata are deliberately excluded.
    //
    // Canonical form: a binary stream hashed with SHA-256. Strings are
    // UTF-8 with a length prefix, integers little-endian fixed-width,
    // booleans one byte, and floats their raw IEEE-754 bits (sub-ULP
    // deltas are detected and no locale or formatting rule can round a
    // tuned value). All NaN payloads canonicalize to one quiet NaN.
    public static class ContentFingerprint
    {
        public static string Compute(DrivingContentCatalog catalog)
        {
            if (catalog == null) return "";
            using (var stream = new MemoryStream(4096))
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(1); // canonical schema epoch
                    writer.Write((int)catalog.SimulationBackend);
                    WriteVehicles(writer, catalog.Vehicles);
                    WriteCourses(writer, catalog.Courses);
                    WriteEvents(writer, catalog.Events);
                    writer.Flush();
                    var hash = SHA256.Create()
                        .ComputeHash(stream.GetBuffer(), 0, (int)stream.Length);
                    var hex = new StringBuilder(hash.Length * 2);
                    foreach (var b in hash)
                        hex.Append(b.ToString("x2",
                            System.Globalization.CultureInfo.InvariantCulture));
                    return hex.ToString();
                }
            }
        }

        private static void WriteVehicles(BinaryWriter writer,
            IReadOnlyDictionary<string, VehicleTuning> vehicles)
        {
            var ids = SortedKeys(vehicles);
            writer.Write(ids.Count);
            foreach (var id in ids)
            {
                var tuning = vehicles[id];
                Str(writer, id);
                Str(writer, tuning.vehicleId);
                F(writer, tuning.maxSpeed);
                F(writer, tuning.acceleration);
                F(writer, tuning.steeringRate);
                F(writer, tuning.steeringReferenceSpeed);
                F(writer, tuning.grip);
                F(writer, tuning.driftMinSpeed);
                F(writer, tuning.driftSteeringFactor);
                F(writer, tuning.driftLateralAcceleration);
                F(writer, tuning.driftChargePerSecond);
                F(writer, tuning.boostMaxSpeed);
                F(writer, tuning.boostAcceleration);
                F(writer, tuning.boostDurationSeconds);
                F(writer, tuning.boostDrainPerSecond);
                F(writer, tuning.boostMinCharge);
                F(writer, tuning.boostChargeSeconds);
                F(writer, tuning.cleanLandingCredit);
                F(writer, tuning.landingMinAirSeconds);
                F(writer, tuning.landingMinDrop);
                F(writer, tuning.driftSteeringThreshold);
                F(writer, tuning.gravity);
                F(writer, tuning.terminalFallSpeed);
                F(writer, tuning.groundProbe);
                F(writer, tuning.rideHeight);
                F(writer, tuning.halfWidth);
                F(writer, tuning.halfLength);
                F(writer, tuning.height);
                F(writer, tuning.wallSpeedRetention);
                writer.Write((int)tuning.simulationBackend);
                WritePhysicsTuning(writer, tuning.physics);
            }
        }

        private static void WritePhysicsTuning(BinaryWriter writer,
            VehiclePhysicsTuning tuning)
        {
            writer.Write(tuning != null);
            if (tuning == null) return;
            F(writer, tuning.massKg);
            F(writer, tuning.comOffsetX);
            F(writer, tuning.comOffsetY);
            F(writer, tuning.comOffsetZ);
            F(writer, tuning.chassisLength);
            F(writer, tuning.chassisWidth);
            F(writer, tuning.chassisHeight);
            F(writer, tuning.inertiaMultiplierX);
            F(writer, tuning.inertiaMultiplierY);
            F(writer, tuning.inertiaMultiplierZ);
            var wheels = tuning.wheels;
            writer.Write(wheels != null ? wheels.Length : 0);
            if (wheels != null)
            {
                foreach (var wheel in wheels)
                {
                    var present = wheel != null;
                    writer.Write(present);
                    if (!present) continue;
                    F(writer, wheel.mountX);
                    F(writer, wheel.mountY);
                    F(writer, wheel.mountZ);
                    F(writer, wheel.radius);
                    writer.Write(wheel.steerable);
                    writer.Write(wheel.driven);
                }
            }
            F(writer, tuning.suspensionRestLength);
            F(writer, tuning.suspensionExtraDroop);
            F(writer, tuning.springRatePerWheel);
            F(writer, tuning.damperPerWheel);
            F(writer, tuning.maxNormalLoadPerWheel);
            F(writer, tuning.frictionCoefficient);
            F(writer, tuning.peakDriveForce);
            F(writer, tuning.peakBrakeForce);
            F(writer, tuning.cruiseSpeed);
            F(writer, tuning.boostSpeed);
            F(writer, tuning.dragCoefficient);
            F(writer, tuning.rollingResistance);
            F(writer, tuning.steerMaxRadians);
            F(writer, tuning.steerHighSpeedRadians);
            F(writer, tuning.steerReferenceSpeed);
            F(writer, tuning.steerRateRadiansPerSecond);
            F(writer, tuning.antiRollRate);
            F(writer, tuning.rollDamping);
            F(writer, tuning.assistMaxLeanRadians);
            F(writer, tuning.assistFallAngleRadians);
            F(writer, tuning.balanceGain);
            F(writer, tuning.balanceDamping);
        }

        private static void WriteCourses(BinaryWriter writer,
            IReadOnlyDictionary<string, CourseDefinition> courses)
        {
            var ids = SortedKeys(courses);
            writer.Write(ids.Count);
            foreach (var id in ids)
            {
                var course = courses[id];
                Str(writer, id);
                Str(writer, course.id);
                F(writer, course.spawnX);
                F(writer, course.spawnY);
                F(writer, course.spawnZ);
                F(writer, course.spawnHeading);
                var gates = course.checkpoints;
                writer.Write(gates != null ? gates.Count : 0);
                if (gates != null)
                {
                    foreach (var gate in gates)
                    {
                        var present = gate != null;
                        writer.Write(present);
                        if (!present) continue;
                        Str(writer, gate.id);
                        F(writer, gate.x);
                        F(writer, gate.y);
                        F(writer, gate.z);
                        F(writer, gate.halfWidth);
                        F(writer, gate.dirX);
                        F(writer, gate.dirZ);
                        F(writer, gate.recoverX);
                        F(writer, gate.recoverY);
                        F(writer, gate.recoverZ);
                        F(writer, gate.recoverHeading);
                    }
                }
                WritePoses(writer, course.spawnSlots);
                var route = course.route;
                writer.Write(route != null ? route.Count : 0);
                if (route != null)
                {
                    foreach (var waypoint in route)
                    {
                        var present = waypoint != null;
                        writer.Write(present);
                        if (!present) continue;
                        F(writer, waypoint.x);
                        F(writer, waypoint.y);
                        F(writer, waypoint.z);
                        F(writer, waypoint.radius);
                    }
                }
                var bounds = course.recoveryBounds;
                writer.Write(bounds != null ? bounds.Count : 0);
                if (bounds != null)
                {
                    foreach (var volume in bounds)
                    {
                        var present = volume != null;
                        writer.Write(present);
                        if (!present) continue;
                        F(writer, volume.minX);
                        F(writer, volume.minY);
                        F(writer, volume.minZ);
                        F(writer, volume.maxX);
                        F(writer, volume.maxY);
                        F(writer, volume.maxZ);
                    }
                }
                WritePoses(writer, course.safePoses);
            }
        }

        private static void WriteEvents(BinaryWriter writer,
            IReadOnlyDictionary<string, EventDefinition> events)
        {
            var ids = SortedKeys(events);
            writer.Write(ids.Count);
            foreach (var id in ids)
            {
                var evt = events[id];
                Str(writer, id);
                Str(writer, evt.id);
                Str(writer, evt.courseId);
                writer.Write(evt.laps);
                writer.Write(evt.countdownTicks);
                var vehicles = evt.vehicleIds;
                writer.Write(vehicles != null ? vehicles.Count : 0);
                if (vehicles != null)
                    foreach (var vehicleId in vehicles)
                        Str(writer, vehicleId);
                var recovery = evt.recovery;
                writer.Write(recovery != null);
                if (recovery != null)
                {
                    F(writer, recovery.offCourseSeconds);
                    F(writer, recovery.minCourseY);
                    F(writer, recovery.stuckSpeed);
                    F(writer, recovery.stuckSeconds);
                    F(writer, recovery.beachedSeconds);
                    F(writer, recovery.overturnedSeconds);
                    F(writer, recovery.outsideBoundsSeconds);
                }
                writer.Write(evt.completionCoins);
            }
        }

        private static void WritePoses(BinaryWriter writer,
            List<MotorPose> poses)
        {
            writer.Write(poses != null ? poses.Count : 0);
            if (poses == null) return;
            foreach (var pose in poses)
            {
                F(writer, pose.x);
                F(writer, pose.y);
                F(writer, pose.z);
                F(writer, pose.heading);
            }
        }

        private static List<string> SortedKeys<T>(
            IReadOnlyDictionary<string, T> map)
        {
            var keys = new List<string>();
            if (map != null) keys.AddRange(map.Keys);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private static void Str(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? "");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        // Raw IEEE-754 little-endian bits, canonicalizing every NaN
        // payload to one quiet NaN so a tuned value fingerprints on its
        // meaning, not its bit representation accident.
        private static void F(BinaryWriter writer, float value)
        {
            var bits = float.IsNaN(value)
                ? BitConverter.SingleToInt32Bits(float.NaN)
                : BitConverter.SingleToInt32Bits(value);
            writer.Write(bits);
        }
    }
}
