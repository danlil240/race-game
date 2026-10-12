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

        public string courseId = "graybox-01";
        public Vector3 spawnPosition = new Vector3(0f, 0f, -60f);
        public float spawnHeadingDegrees;
        public Gate[] gates = new Gate[0];
        public SpawnSlot[] spawnSlots = new SpawnSlot[0];
        public RoutePoint[] aiRoute = new RoutePoint[0];

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
