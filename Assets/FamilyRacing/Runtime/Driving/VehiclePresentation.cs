using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using UnityEngine;

namespace FamilyRacing
{
    // Graybox vehicle visual: a root parked at the motor's ground pose,
    // a lean pivot that carries the cosmetic body roll, and child
    // primitives assembled into a per-class silhouette. The lean pivot
    // only rotates the visual — collision is always the motor's swept
    // box in Core, so the motorcycle can lean hard while its stable
    // collision body stays upright. No colliders: the world never sees
    // these meshes.
    public sealed class VehiclePresentation : MonoBehaviour
    {
        private const float LeanSmoothing = 10f;

        private Transform leanPivot;
        private readonly List<GameObject> parts = new List<GameObject>();
        private Material bodyMaterial;
        private Material darkMaterial;
        private float leanMaxDegrees;
        private float leanRoll;
        private string shownKey = "";

        // (Re)builds the silhouette for the given definition. Safe to
        // call every frame — it only rebuilds when the key changes.
        public void SetVehicle(VehicleDefinitionAsset asset)
        {
            if (asset == null) return;
            EnsureBuilt();
            var key = asset.silhouette + "|" + asset.vehicleId;
            if (bodyMaterial != null) bodyMaterial.color = asset.bodyColor;
            leanMaxDegrees = asset.leanDegrees;
            if (key == shownKey) return;
            shownKey = key;
            Rebuild(asset);
        }

        // Committed motor pose → root transform; steering hold → cosmetic
        // roll on the lean pivot. The root itself only yaws: roll can
        // never leak into the collision body's orientation.
        public void ApplyPose(MotorState motor, float steering)
        {
            if (leanPivot == null || motor == null) return;
            transform.SetPositionAndRotation(
                new Vector3(motor.x, motor.y, motor.z),
                Quaternion.Euler(0f, motor.heading * Mathf.Rad2Deg, 0f));
            var target = -Mathf.Clamp(steering, -1f, 1f) * leanMaxDegrees;
            leanRoll = Mathf.Lerp(leanRoll, target,
                1f - Mathf.Exp(-LeanSmoothing * Time.deltaTime));
            leanPivot.localRotation = Quaternion.Euler(0f, 0f, leanRoll);
        }

        public float LeanRoll { get { return leanRoll; } }
        public string ShownKey { get { return shownKey; } }

        private void EnsureBuilt()
        {
            if (leanPivot != null) return;
            var pivot = new GameObject("LeanPivot");
            pivot.transform.SetParent(transform, false);
            leanPivot = pivot.transform;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            bodyMaterial = new Material(shader);
            darkMaterial = new Material(shader) { color = new Color(0.16f, 0.17f, 0.19f) };
        }

        private void Rebuild(VehicleDefinitionAsset asset)
        {
            foreach (var part in parts)
                if (part != null) Destroy(part);
            parts.Clear();
            if (asset.silhouette == "motorcycle")
                BuildMotorcycle(asset);
            else if (asset.silhouette == "car")
                BuildCar(asset);
            else
            {
                // Unknown silhouette falls back to a plain scaled body so
                // new classes can never render as an invisible vehicle.
                Part(PrimitiveType.Cube, "Body", bodyMaterial,
                    new Vector3(0f, asset.bodyScale.y * 0.5f, 0f),
                    asset.bodyScale);
            }
        }

        // Car: a wide low body, a cabin set toward the rear, and four
        // dark wheels — reads as a boxy rally car at a glance.
        private void BuildCar(VehicleDefinitionAsset asset)
        {
            var s = asset.bodyScale;
            Part(PrimitiveType.Cube, "Body", bodyMaterial,
                new Vector3(0f, 0.18f + s.y * 0.5f, 0f), s);
            Part(PrimitiveType.Cube, "Cabin", bodyMaterial,
                new Vector3(0f, 0.18f + s.y + 0.24f, -s.z * 0.1f),
                new Vector3(s.x * 0.72f, 0.48f, s.z * 0.42f));
            var wheel = new Vector3(0.62f, 0.14f, 0.62f);
            var wx = s.x * 0.5f - 0.1f;
            var wz = s.z * 0.5f - 0.55f;
            Part(PrimitiveType.Cylinder, "WheelFL", darkMaterial,
                new Vector3(-wx, 0.31f, wz), wheel, Quaternion.Euler(0f, 0f, 90f));
            Part(PrimitiveType.Cylinder, "WheelFR", darkMaterial,
                new Vector3(wx, 0.31f, wz), wheel, Quaternion.Euler(0f, 0f, 90f));
            Part(PrimitiveType.Cylinder, "WheelRL", darkMaterial,
                new Vector3(-wx, 0.31f, -wz), wheel, Quaternion.Euler(0f, 0f, 90f));
            Part(PrimitiveType.Cylinder, "WheelRR", darkMaterial,
                new Vector3(wx, 0.31f, -wz), wheel, Quaternion.Euler(0f, 0f, 90f));
        }

        // Motorcycle: two tall wheels, a narrow frame, handlebars and a
        // rider — a visibly different silhouette from the car even in
        // flat graybox shading.
        private void BuildMotorcycle(VehicleDefinitionAsset asset)
        {
            var s = asset.bodyScale;
            var wheel = new Vector3(0.78f, 0.08f, 0.78f);
            Part(PrimitiveType.Cylinder, "WheelF", darkMaterial,
                new Vector3(0f, 0.39f, s.z * 0.38f), wheel, Quaternion.Euler(0f, 0f, 90f));
            Part(PrimitiveType.Cylinder, "WheelR", darkMaterial,
                new Vector3(0f, 0.39f, -s.z * 0.36f), wheel, Quaternion.Euler(0f, 0f, 90f));
            Part(PrimitiveType.Cube, "Frame", bodyMaterial,
                new Vector3(0f, 0.78f, 0f), new Vector3(0.3f, 0.45f, s.z * 0.66f));
            Part(PrimitiveType.Cube, "Handlebar", darkMaterial,
                new Vector3(0f, 1.28f, s.z * 0.33f), new Vector3(0.72f, 0.07f, 0.12f));
            Part(PrimitiveType.Cube, "Rider", bodyMaterial,
                new Vector3(0f, 1.32f, -s.z * 0.08f), new Vector3(0.4f, 0.62f, 0.36f));
            Part(PrimitiveType.Sphere, "Head", darkMaterial,
                new Vector3(0f, 1.82f, -s.z * 0.04f), new Vector3(0.3f, 0.3f, 0.3f));
        }

        private GameObject Part(PrimitiveType primitive, string name,
            Material material, Vector3 localPosition, Vector3 localScale)
        {
            return Part(primitive, name, material, localPosition, localScale,
                Quaternion.identity);
        }

        private GameObject Part(PrimitiveType primitive, string name,
            Material material, Vector3 localPosition, Vector3 localScale,
            Quaternion localRotation)
        {
            var part = GameObject.CreatePrimitive(primitive);
            part.name = name;
            part.transform.SetParent(leanPivot, false);
            part.transform.localPosition = localPosition;
            part.transform.localRotation = localRotation;
            part.transform.localScale = localScale;
            // Visual only: collision lives entirely in the motor's swept
            // box, so no part may carry a collider.
            var collider = part.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            part.GetComponent<Renderer>().sharedMaterial = material;
            parts.Add(part);
            return part;
        }

        private void OnDestroy()
        {
            if (bodyMaterial != null) Destroy(bodyMaterial);
            if (darkMaterial != null) Destroy(darkMaterial);
        }
    }
}
