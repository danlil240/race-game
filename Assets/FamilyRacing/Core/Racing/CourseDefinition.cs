using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;

namespace FamilyRacing.Core.Racing
{
    // One ordered course gate. Index 0 is the start/finish line. A gate
    // counts when the swept previous->current pose segment crosses its
    // plane forward (along dirX/dirZ) within halfWidth. The safe recovery
    // pose is where a respawn returns the vehicle after this gate is
    // accepted — authored just behind the plane along the travel
    // direction, facing the way the lap flows.
    [Serializable]
    public sealed class CheckpointGate
    {
        public string id;
        public float x;
        public float y;
        public float z;
        public float halfWidth;
        public float dirX;
        public float dirZ;
        public float recoverX;
        public float recoverY;
        public float recoverZ;
        public float recoverHeading;

        public CheckpointGate Copy()
        {
            return new CheckpointGate
            {
                id = id,
                x = x,
                y = y,
                z = z,
                halfWidth = halfWidth,
                dirX = dirX,
                dirZ = dirZ,
                recoverX = recoverX,
                recoverY = recoverY,
                recoverZ = recoverZ,
                recoverHeading = recoverHeading
            };
        }
    }

    // One authored AI racing-line point. The Task-4 waypoint follower
    // accepts the waypoint inside `radius` metres on the ground plane;
    // radius <= 0 falls back to NetworkSettings.AiWaypointReachMeters.
    // The route is an ordered loop: after the last point the follower
    // resumes at index 0, which must sit just past the start/finish line.
    [Serializable]
    public sealed class RouteWaypoint
    {
        public float x;
        public float y;
        public float z;
        public float radius;

        public RouteWaypoint Copy()
        {
            return new RouteWaypoint
            {
                x = x,
                y = y,
                z = z,
                radius = radius
            };
        }
    }

    // One authored optional shortcut lane (M6 Task 4): a directional
    // traversal detector that credits the "shortcut.accepted" metric by
    // id without being a mandatory progress gate. Legality is an
    // authoring contract enforced by FullContentValidation: the lane
    // rejoins the main route before the next required gate — and its
    // plane must never sit inside a mandatory gate's crossing region —
    // so crossing it can never skip a checkpoint.
    [Serializable]
    public sealed class ShortcutZone
    {
        public string id;
        public float x;
        public float y;
        public float z;
        public float halfWidth;
        public float dirX;
        public float dirZ;

        public ShortcutZone Copy()
        {
            return (ShortcutZone)MemberwiseClone();
        }
    }

    // One authored axis-aligned permitted volume for the physical
    // backend's outside-bounds recovery. Everything inside a volume is
    // legal space — ground, permitted jump airspace, the ramp — and any
    // pose outside EVERY volume accrues the authored outside-bounds
    // grace before the auto-recovery fires. The kill height
    // (recovery.minCourseY) still applies inside a volume.
    [Serializable]
    public sealed class RecoveryVolume
    {
        public float minX;
        public float minY;
        public float minZ;
        public float maxX;
        public float maxY;
        public float maxZ;

        public bool Contains(float x, float y, float z)
        {
            return x >= minX && x <= maxX
                && y >= minY && y <= maxY
                && z >= minZ && z <= maxZ;
        }

        public RecoveryVolume Copy()
        {
            return (RecoveryVolume)MemberwiseClone();
        }
    }

    // Unity-free course data: the shared graybox definition both vehicles
    // drive. Geometry lives in the generated prefab; this record carries
    // the sim-facing facts (spawn, ordered checkpoints).
    // `recoveryBounds` authors the permitted space the physical backend
    // judges outside-bounds recovery against — including the airspace a
    // designed jump traverses, so a real flight is never a failure.
    // `safePoses` authors the deterministic fallback respawn candidates
    // tried, in order, when the last accepted safe pose is occupied by
    // another racer; they are candidates only — the primary target stays
    // the checkpoint-derived safe pose.
    // `spawnSlots` optionally authors a multiplayer starting grid — the
    // network race coordinator seats slot N at spawnSlots[N]; solo play
    // keeps using spawnX/Y/Z/Heading and never reads them. When present
    // every slot must be finite and pairwise non-overlapping on the
    // ground plane.
    // `route` optionally authors the AI racing line: an ordered loop of
    // waypoints compatible with the gate order — each leg crosses its
    // mandatory gate plane inside the authored half-width, including the
    // ramp approach. It is data, not an advantage: the AI still drives
    // the same physics, checkpoint and recovery rules as humans.
    public sealed class CourseDefinition
    {
        public const float MinSpawnSeparationMeters = 3.5f;

        public string id = "";
        public float spawnX;
        public float spawnY;
        public float spawnZ;
        public float spawnHeading;
        public List<CheckpointGate> checkpoints = new List<CheckpointGate>();
        public List<MotorPose> spawnSlots = new List<MotorPose>();
        public List<RouteWaypoint> route = new List<RouteWaypoint>();
        public List<RecoveryVolume> recoveryBounds = new List<RecoveryVolume>();
        public List<MotorPose> safePoses = new List<MotorPose>();
        // Authored gadget pickup sites (M5 Task 5): vertical trigger
        // columns the authoritative gadget rules sweep racer segments
        // against. `y` is road height — pickups on different-height
        // routes never interact. Empty means the course authors no
        // pickups; gadgets stay inert there.
        public List<GadgetPickupSite> pickupSites =
            new List<GadgetPickupSite>();
        // Authored optional shortcut lanes (M6 Task 4): traversal
        // detectors distinct from the mandatory checkpoint order — the
        // "distinctShortcuts"/"shortcut.accepted" metrics credit their
        // ids. Empty means the course authors no optional shortcut;
        // production validation requires two per full-content course.
        public List<ShortcutZone> shortcutZones =
            new List<ShortcutZone>();

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(id)) { error = "id"; return false; }
            if (!Finite(spawnX) || !Finite(spawnY) || !Finite(spawnZ) || !Finite(spawnHeading))
            { error = "spawn"; return false; }
            // Gate 0 is the start/finish line; a real course needs at
            // least one more mandatory gate — a single line would let
            // repeated crossings manufacture laps without any circuit.
            if (checkpoints == null || checkpoints.Count < 2)
            { error = "checkpoints"; return false; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var gate in checkpoints)
            {
                if (gate == null || string.IsNullOrEmpty(gate.id)) { error = "checkpoint.id"; return false; }
                if (!seen.Add(gate.id)) { error = "checkpoint.duplicate"; return false; }
                if (!Finite(gate.x) || !Finite(gate.y) || !Finite(gate.z)
                    || !Finite(gate.halfWidth) || gate.halfWidth <= 0f
                    || !Finite(gate.dirX) || !Finite(gate.dirZ))
                { error = "checkpoint." + gate.id; return false; }
                // A zero direction cannot define a directional crossing.
                if (gate.dirX * gate.dirX + gate.dirZ * gate.dirZ < 0.0001f)
                { error = "checkpoint.direction"; return false; }
                if (!Finite(gate.recoverX) || !Finite(gate.recoverY)
                    || !Finite(gate.recoverZ) || !Finite(gate.recoverHeading))
                { error = "checkpoint.recover"; return false; }
            }
            if (spawnSlots != null)
            {
                for (var i = 0; i < spawnSlots.Count; i++)
                {
                    var slot = spawnSlots[i];
                    if (!Finite(slot.x) || !Finite(slot.y) || !Finite(slot.z)
                        || !Finite(slot.heading))
                    { error = "spawnSlots"; return false; }
                    for (var j = i + 1; j < spawnSlots.Count; j++)
                    {
                        var dx = spawnSlots[i].x - spawnSlots[j].x;
                        var dz = spawnSlots[i].z - spawnSlots[j].z;
                        if (dx * dx + dz * dz
                            < MinSpawnSeparationMeters * MinSpawnSeparationMeters)
                        { error = "spawnSlots.overlap"; return false; }
                    }
                }
            }
            if (route != null)
                foreach (var waypoint in route)
                {
                    if (waypoint == null || !Finite(waypoint.x)
                        || !Finite(waypoint.y) || !Finite(waypoint.z))
                    { error = "route"; return false; }
                    if (!Finite(waypoint.radius) || waypoint.radius < 0f)
                    { error = "route.radius"; return false; }
                }
            if (recoveryBounds != null)
                foreach (var volume in recoveryBounds)
                {
                    if (volume == null
                        || !Finite(volume.minX) || !Finite(volume.maxX)
                        || !Finite(volume.minY) || !Finite(volume.maxY)
                        || !Finite(volume.minZ) || !Finite(volume.maxZ)
                        // An inverted axis can never contain a pose.
                        || volume.minX >= volume.maxX
                        || volume.minY >= volume.maxY
                        || volume.minZ >= volume.maxZ)
                    { error = "recoveryBounds"; return false; }
                }
            if (safePoses != null)
                foreach (var pose in safePoses)
                {
                    if (!Finite(pose.x) || !Finite(pose.y)
                        || !Finite(pose.z) || !Finite(pose.heading))
                    { error = "safePoses"; return false; }
                }
            if (pickupSites != null)
            {
                var seenSites = new HashSet<string>(StringComparer.Ordinal);
                foreach (var site in pickupSites)
                {
                    if (site == null || !site.Validate(out error))
                    { error = "pickupSites." + error; return false; }
                    if (!seenSites.Add(site.id))
                    { error = "pickupSites.duplicate"; return false; }
                }
                if (pickupSites.Count > NetworkSettings.GadgetPickupCapacity)
                { error = "pickupSites.capacity"; return false; }
            }
            if (shortcutZones != null)
            {
                var seenZones = new HashSet<string>(StringComparer.Ordinal);
                foreach (var zone in shortcutZones)
                {
                    if (zone == null || string.IsNullOrEmpty(zone.id))
                    { error = "shortcut.id"; return false; }
                    if (!seenZones.Add(zone.id))
                    { error = "shortcut.duplicate"; return false; }
                    if (!Finite(zone.x) || !Finite(zone.y) || !Finite(zone.z)
                        || !Finite(zone.halfWidth) || zone.halfWidth <= 0f
                        || !Finite(zone.dirX) || !Finite(zone.dirZ))
                    { error = "shortcut." + zone.id; return false; }
                    // A zero direction cannot define a directional crossing.
                    if (zone.dirX * zone.dirX + zone.dirZ * zone.dirZ
                        < 0.0001f)
                    { error = "shortcut.direction"; return false; }
                }
            }
            return true;
        }

        public CourseDefinition Copy()
        {
            var copy = new CourseDefinition
            {
                id = id,
                spawnX = spawnX,
                spawnY = spawnY,
                spawnZ = spawnZ,
                spawnHeading = spawnHeading,
                checkpoints = new List<CheckpointGate>()
            };
            if (checkpoints != null)
                foreach (var gate in checkpoints)
                    copy.checkpoints.Add(gate == null ? null : gate.Copy());
            if (spawnSlots != null)
            {
                copy.spawnSlots = new List<MotorPose>();
                foreach (var slot in spawnSlots)
                    copy.spawnSlots.Add(new MotorPose
                    {
                        x = slot.x,
                        y = slot.y,
                        z = slot.z,
                        heading = slot.heading
                    });
            }
            if (route != null)
            {
                copy.route = new List<RouteWaypoint>();
                foreach (var waypoint in route)
                    copy.route.Add(waypoint == null ? null : waypoint.Copy());
            }
            if (recoveryBounds != null)
            {
                copy.recoveryBounds = new List<RecoveryVolume>();
                foreach (var volume in recoveryBounds)
                    copy.recoveryBounds.Add(
                        volume == null ? null : volume.Copy());
            }
            if (safePoses != null)
            {
                copy.safePoses = new List<MotorPose>();
                foreach (var pose in safePoses)
                    copy.safePoses.Add(new MotorPose
                    {
                        x = pose.x,
                        y = pose.y,
                        z = pose.z,
                        heading = pose.heading
                    });
            }
            if (pickupSites != null)
            {
                copy.pickupSites = new List<GadgetPickupSite>();
                foreach (var site in pickupSites)
                    copy.pickupSites.Add(
                        site == null ? null : site.Copy());
            }
            if (shortcutZones != null)
            {
                copy.shortcutZones = new List<ShortcutZone>();
                foreach (var zone in shortcutZones)
                    copy.shortcutZones.Add(
                        zone == null ? null : zone.Copy());
            }
            return copy;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
