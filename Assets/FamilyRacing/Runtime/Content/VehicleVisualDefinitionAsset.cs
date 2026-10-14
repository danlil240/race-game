using FamilyRacing.Core.Driving;
using UnityEngine;

namespace FamilyRacing
{
    // Authored vehicle visual (M4 Task 4): the recognizable rally car or
    // dirt motorcycle a VehiclePresentation binds instead of the primitive
    // silhouette. Pure presentation — the asset carries no gameplay data;
    // collision always stays with the tuning's swept box.
    //
    // Prefab conventions the RacerVisualRig relies on:
    //   * root sits on the ground plane, forward is +Z;
    //   * `seatMountName` marks the child a seated character parents to;
    //   * `gripLeftName`/`gripRightName` mark the steering contact points
    //     the rider's hands aim at (dash wheel for the car, bars for the
    //     motorcycle);
    //   * `wheelNames` lists the wheel pivot transforms in the same order
    //     as the physics tuning's wheel specs; each pivot owns a "Spin"
    //     child that carries the rolling-cylinder visual. The pivot gets
    //     the road-wheel steer + suspension travel; the Spin child gets
    //     the axle-laid rotation and roll.
    //   * renderers named "*Accent*" (plus any transform named "Accent")
    //     take the racer's profile colorIndex through a material
    //     property override — shared materials stay shared.
    //   * no colliders anywhere in the prefab.
    [CreateAssetMenu(menuName = "FamilyRacing/Vehicle Visual",
        fileName = "vehicle-visual")]
    public sealed class VehicleVisualDefinitionAsset : ScriptableObject
    {
        public string visualId = "rally-car-01";
        // Must match the binding VehicleDefinitionAsset.silhouette —
        // "car" | "motorcycle". A mismatch means the authored mounts
        // would not line up with that class's tuning, so presentation
        // refuses it and reports the primitive fallback instead.
        public string vehicleClass = "car";
        public GameObject prefab;

        [Header("Mounts (child names inside the prefab)")]
        public string seatMountName = "SeatMount";
        public string gripLeftName = "GripL";
        public string gripRightName = "GripR";
        public string dashWheelName = "DashWheel";
        // Wheel pivot names in the physics tuning's wheel order (car:
        // FL, FR, RL, RR; motorcycle: F, R). `wheelSteerable` marks which
        // pivots display the road-wheel steer.
        public string[] wheelNames = { "WheelFL", "WheelFR", "WheelRL", "WheelRR" };
        public bool[] wheelSteerable = { true, true, false, false };
        // Shared authored wheel radius; matches the tuning's per-wheel
        // radius so the visible tire meets the same contact patch.
        public float wheelRadius = 0.32f;
        // Cosmetic road-wheel steer display at full lock (degrees).
        public float steerDegrees = 25f;

        [Header("Physical anchoring")]
        // The physical backend renders the body at its rigid-body origin
        // (wheel mounts are body-space offsets below it) while the prefab
        // root is authored at ground level. The rig drops the instantiated
        // visual by this height so authored wheels sit exactly on the
        // tuning's contact patches.
        public float bodyOriginHeight = 0.77f;

        [Header("Rider")]
        // Extra rider-only roll at full steering for arcade rigs — the
        // physical backend owns chassis lean, so the rider never adds
        // roll there (the PhysicalLeanIsNotAppliedTwice contract).
        public float riderLeanDegrees = 8f;

        public bool Validate(out string error)
        {
            if (string.IsNullOrEmpty(visualId))
            {
                error = name + ": vehicle visual has no visualId";
                return false;
            }
            if (vehicleClass != "car" && vehicleClass != "motorcycle")
            {
                error = name + ": vehicleClass '" + vehicleClass
                    + "' must be 'car' or 'motorcycle'";
                return false;
            }
            if (prefab == null)
            {
                error = name + ": visual '" + visualId
                    + "' has no prefab";
                return false;
            }
            if (wheelNames == null || wheelNames.Length == 0)
            {
                error = name + ": visual '" + visualId
                    + "' declares no wheel mounts";
                return false;
            }
            if (wheelRadius <= 0f || wheelRadius > 1f)
            {
                error = name + ": visual '" + visualId
                    + "' wheelRadius out of range";
                return false;
            }
            error = null;
            return true;
        }
    }
}
