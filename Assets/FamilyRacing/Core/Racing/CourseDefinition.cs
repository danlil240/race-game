using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;

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

    // Unity-free course data: the shared graybox definition both vehicles
    // drive. Geometry lives in the generated prefab; this record carries
    // the sim-facing facts (spawn, ordered checkpoints).
    // `spawnSlots` optionally authors a multiplayer starting grid — the
    // network race coordinator seats slot N at spawnSlots[N]; solo play
    // keeps using spawnX/Y/Z/Heading and never reads them. When present
    // every slot must be finite and pairwise non-overlapping on the
    // ground plane.
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
            return copy;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
