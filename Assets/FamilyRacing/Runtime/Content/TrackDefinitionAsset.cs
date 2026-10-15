using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;
using UnityEngine;

namespace FamilyRacing
{
    // Authored course data converted into a validated Core
    // CourseDefinition. The graybox geometry lives in the generated prefab;
    // this asset carries spawn and the ordered checkpoint gates.
    [CreateAssetMenu(menuName = "FamilyRacing/Track Definition",
        fileName = "track-definition")]
    public sealed class TrackDefinitionAsset : ScriptableObject
    {
        [Serializable]
        public sealed class Gate
        {
            public string id = "gate-1";
            public Vector3 center;
            public float halfWidth = 6f;
            public Vector2 direction = new Vector2(0f, 1f);
            // Safe respawn after this gate is accepted: authored just
            // behind the plane along the travel direction.
            public Vector3 recoverPosition;
            public float recoverHeadingDegrees;
        }

        // One authored multiplayer grid slot: the network race coordinator
        // seats roster slot N at spawnSlots[N]. Solo play ignores this
        // array and uses spawnPosition/spawnHeadingDegrees.
        [Serializable]
        public sealed class SpawnSlot
        {
            public Vector3 position;
            public float headingDegrees;
        }

        // One authored AI racing-line point on the ground plane (x,z).
        // radius <= 0 falls back to the shared default reach. The route
        // is an ordered loop compatible with the gate order — the AI
        // still plays by the same physics/checkpoint/recovery rules.
        [Serializable]
        public sealed class RoutePoint
        {
            public float x;
            public float z;
            public float radius;
        }

        // One authored axis-aligned permitted volume for the physical
        // backend's outside-bounds recovery. Author ground, ramps and
        // permitted jump airspace inside a volume — airtime inside one
        // is never a failure.
        [Serializable]
        public sealed class BoundsVolume
        {
            public Vector3 min;
            public Vector3 max;
        }

        // One authored gadget pickup column (M5 Task 5): `center.y` is
        // the road height, so elevated routes and flyovers never touch
        // a ground-level site (DifferentHeightsNeverInteract). The host
        // rules sweep the racers' real traveled segments against it.
        [Serializable]
        public sealed class PickupSite
        {
            public string id = "";
            public Vector3 center;
            public float radiusMeters = 2f;
            public float verticalRangeMeters = 2.5f;
        }

        // One authored optional shortcut lane (M6 Task 4): a named
        // directional traversal region distinct from the mandatory
        // gates — the shortcut metrics credit its id when a racer
        // sweeps it forward. Legality (rejoin before the next required
        // gate, never overlapping a mandatory gate plane) is the
        // production validator's job; this asset carries the data.
        [Serializable]
        public sealed class ShortcutLane
        {
            public string id = "";
            public Vector3 center;
            public float halfWidth = 5f;
            public Vector2 direction = new Vector2(0f, 1f);
        }

        public string courseId = "graybox-01";
        public Vector3 spawnPosition = new Vector3(0f, 0f, -60f);
        public float spawnHeadingDegrees;
        public Gate[] gates = new Gate[0];
        public SpawnSlot[] spawnSlots = new SpawnSlot[0];
        public RoutePoint[] aiRoute = new RoutePoint[0];
        [Header("Physical recovery")]
        public BoundsVolume[] recoveryBounds = new BoundsVolume[0];
        // Deterministic fallback respawn candidates tried in order when
        // the checkpoint-derived safe pose is occupied.
        public SpawnSlot[] safePoses = new SpawnSlot[0];
        [Header("Gadgets")]
        // Authored pickup trigger columns; empty keeps gadgets inert on
        // this course (the world object is never fabricated).
        public PickupSite[] pickupSites = new PickupSite[0];
        [Header("Shortcuts")]
        // Authored optional shortcut lanes (M6 Task 4): traversal
        // detectors credited by the shortcut metrics, never part of the
        // mandatory gate order. Full-content courses author exactly the
        // two legal lanes the production contract requires.
        public ShortcutLane[] shortcutLanes = new ShortcutLane[0];

        public CourseDefinition ToCourse(out string error)
        {
            var course = new CourseDefinition
            {
                id = courseId,
                spawnX = spawnPosition.x,
                spawnY = spawnPosition.y,
                spawnZ = spawnPosition.z,
                spawnHeading = spawnHeadingDegrees * Mathf.Deg2Rad,
                checkpoints = new List<CheckpointGate>()
            };
            if (gates != null)
                foreach (var gate in gates)
                {
                    if (gate == null) continue;
                    course.checkpoints.Add(new CheckpointGate
                    {
                        id = gate.id,
                        x = gate.center.x,
                        y = gate.center.y,
                        z = gate.center.z,
                        halfWidth = gate.halfWidth,
                        dirX = gate.direction.x,
                        dirZ = gate.direction.y,
                        recoverX = gate.recoverPosition.x,
                        recoverY = gate.recoverPosition.y,
                        recoverZ = gate.recoverPosition.z,
                        recoverHeading = gate.recoverHeadingDegrees * Mathf.Deg2Rad
                    });
                }
            if (spawnSlots != null)
                foreach (var slot in spawnSlots)
                {
                    if (slot == null) continue;
                    course.spawnSlots.Add(new MotorPose
                    {
                        x = slot.position.x,
                        y = slot.position.y,
                        z = slot.position.z,
                        heading = slot.headingDegrees * Mathf.Deg2Rad
                    });
                }
            if (aiRoute != null)
                foreach (var point in aiRoute)
                {
                    if (point == null) continue;
                    course.route.Add(new RouteWaypoint
                    {
                        x = point.x,
                        z = point.z,
                        radius = point.radius
                    });
                }
            if (recoveryBounds != null)
                foreach (var volume in recoveryBounds)
                {
                    if (volume == null) continue;
                    course.recoveryBounds.Add(new RecoveryVolume
                    {
                        minX = volume.min.x,
                        minY = volume.min.y,
                        minZ = volume.min.z,
                        maxX = volume.max.x,
                        maxY = volume.max.y,
                        maxZ = volume.max.z
                    });
                }
            if (safePoses != null)
                foreach (var candidate in safePoses)
                {
                    if (candidate == null) continue;
                    course.safePoses.Add(new MotorPose
                    {
                        x = candidate.position.x,
                        y = candidate.position.y,
                        z = candidate.position.z,
                        heading = candidate.headingDegrees * Mathf.Deg2Rad
                    });
                }
            if (pickupSites != null)
                foreach (var site in pickupSites)
                {
                    if (site == null) continue;
                    course.pickupSites.Add(new GadgetPickupSite
                    {
                        id = site.id,
                        x = site.center.x,
                        y = site.center.y,
                        z = site.center.z,
                        radiusMeters = site.radiusMeters,
                        verticalRangeMeters = site.verticalRangeMeters
                    });
                }
            if (shortcutLanes != null)
                foreach (var lane in shortcutLanes)
                {
                    if (lane == null) continue;
                    course.shortcutZones.Add(new ShortcutZone
                    {
                        id = lane.id,
                        x = lane.center.x,
                        y = lane.center.y,
                        z = lane.center.z,
                        halfWidth = lane.halfWidth,
                        dirX = lane.direction.x,
                        dirZ = lane.direction.y
                    });
                }
            if (!course.Validate(out error))
            {
                error = name + ": invalid course field " + error;
                return null;
            }
            error = null;
            return course;
        }
    }
}
