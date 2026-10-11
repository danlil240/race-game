using System;
using System.Collections.Generic;
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
        }

        public string courseId = "graybox-01";
        public Vector3 spawnPosition = new Vector3(0f, 0f, -60f);
        public float spawnHeadingDegrees;
        public Gate[] gates = new Gate[0];

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
                        dirZ = gate.direction.y
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
