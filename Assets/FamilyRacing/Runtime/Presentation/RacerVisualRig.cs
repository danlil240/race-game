using FamilyRacing.Core.Driving;
using UnityEngine;

namespace FamilyRacing
{
    // The Task-4 visual implementation seam: one rig turns the committed
    // motor state into a recognizable vehicle + seated animal racer.
    // VehiclePresentation owns the root pose, the arcade lean pivot and
    // the physical wheel channel; this rig owns everything inside them:
    // prefab instantiation, mount resolution, wheel spin/steer for the
    // arcade path, rider seat/lean/aimed arms, landing squash and finish
    // celebration.
    //
    // Bounding contract: Bind is idempotent — re-binding the same
    // identity is a no-op and re-binding a different identity destroys
    // the previous visuals immediately, so repeated snapshots, rejoins
    // and rematches can never stack rigs. Rigs carry no colliders and
    // never write anything the simulation reads — wheel spin, rider lean
    // and squash are cosmetic decoration on top of the committed pose.
    public sealed class RacerVisualRig : MonoBehaviour
    {
        // Identity tokens published through PresentationState.racers so
        // an observer always sees the honest fallback, never a stale
        // authored id after the real visual went missing.
        public const string PrimitiveFallbackVisualId = "primitive-fallback";
        public const string FallbackCharacterId = "fallback";
        public const string NoCharacterId = "none";

        // Profile color slots (same palette the profile cards use):
        // coral, seafoam, ocean, sand. The index travels with the
        // participant/profile and tints the "Accent"-named parts.
        private static readonly Color[] RacerColors =
        {
            new Color(0.91f, 0.51f, 0.42f),
            new Color(0.50f, 0.78f, 0.70f),
            new Color(0.24f, 0.49f, 0.63f),
            new Color(0.80f, 0.66f, 0.45f)
        };

        // The palette lookup every accent override uses — public so
        // tests can assert the tint actually applied to the renderers.
        public static Color SlotColor(int colorIndex)
        {
            return RacerColors[
                ((colorIndex % RacerColors.Length) + RacerColors.Length)
                % RacerColors.Length];
        }

        private const float SquashRecovery = 2.6f;
        private const float CelebrationSeconds = 1.7f;
        private const float RiderLeanSmoothing = 9f;

        private static Material fallbackMaterial;

        private Transform contentRoot;
        private float bodyOriginHeight;

        private GameObject visualRoot;
        private Transform vehicleInstance;
        private Transform seatMount;
        private Transform gripL, gripR, dashWheel;
        private Transform[] wheelPivots = new Transform[0];
        private Transform[] wheelSpins = new Transform[0];
        private bool[] wheelSteerable = new bool[0];
        private Renderer[] accentRenderers = new Renderer[0];
        private readonly System.Collections.Generic.List<Renderer>
            accentScratch = new System.Collections.Generic.List<Renderer>();
        private MaterialPropertyBlock accentBlock;

        private Transform riderPivot;
        private Transform rider;
        private Transform armL, armR, head;
        private float riderLean;

        private VehicleVisualDefinitionAsset boundVehicle;
        private string boundKey = "";
        private float[] wheelSpinAngles = new float[0];
        private float dashSteer;

        private bool prevGrounded = true;
        private bool hasPrevGrounded;
        private float peakFallSpeed;
        private float squash;
        private float celebrateT;

        public string VehicleVisualId
        {
            get { return boundVehicle != null ? boundVehicle.visualId : ""; }
        }
        public string BoundCharacterId { get; private set; }
        public int BoundColorIndex { get; private set; }
        public bool HasVehicleVisual
        {
            get { return vehicleInstance != null; }
        }
        public Transform VehicleRoot { get { return vehicleInstance; } }
        public Transform RiderPivot { get { return riderPivot; } }
        public Transform Rider { get { return rider; } }
        public Transform[] WheelPivots { get { return wheelPivots; } }
        public Transform[] WheelSpins { get { return wheelSpins; } }

        // VehiclePresentation installs the transform the rig's visuals
        // parent to: the lean pivot, so arcade cosmetic roll carries the
        // authored visuals exactly like it carries the primitives.
        internal void SetContentRoot(Transform root)
        {
            contentRoot = root;
        }

        // Physical mode drops the ground-plane-authored vehicle by the
        // tuning's body-origin height so authored wheel mounts land on
        // the physics contact patches; arcade keeps zero.
        public void SetBodyOriginHeight(float height)
        {
            bodyOriginHeight = Mathf.Max(0f, height);
            ApplyVehicleAnchor();
        }

        private void ApplyVehicleAnchor()
        {
            if (vehicleInstance != null)
                vehicleInstance.localPosition =
                    new Vector3(0f, -bodyOriginHeight, 0f);
        }

        public void Bind(VehicleVisualDefinitionAsset vehicle,
            CharacterDefinitionAsset character, int colorIndex)
        {
            var key = (vehicle != null ? vehicle.visualId : "-")
                + "|" + (character != null ? character.characterId : "fallback")
                + "|" + colorIndex;
            if (key == boundKey && visualRoot != null) return;
            boundKey = key;
            Rebuild(vehicle, character, colorIndex);
        }

        // Detach every spawned visual; the next Bind rebuilds from
        // scratch. Called when the presentation falls back to primitives.
        internal void Clear()
        {
            boundKey = "";
            DestroyVisuals();
        }

        private void DestroyVisuals()
        {
            if (visualRoot != null)
            {
                // DestroyImmediate like the primitive path: a deferred
                // destroy leaves the stale visuals alive for a frame.
                DestroyImmediate(visualRoot);
                visualRoot = null;
            }
            vehicleInstance = null;
            seatMount = null; gripL = null; gripR = null; dashWheel = null;
            wheelPivots = new Transform[0];
            wheelSpins = new Transform[0];
            wheelSteerable = new bool[0];
            accentRenderers = new Renderer[0];
            riderPivot = null; rider = null;
            armL = null; armR = null; head = null;
            boundVehicle = null;
            BoundCharacterId = NoCharacterId;
            BoundColorIndex = -1;
            squash = 0f; celebrateT = 0f; riderLean = 0f;
            peakFallSpeed = 0f; hasPrevGrounded = false;
        }

        private void Rebuild(VehicleVisualDefinitionAsset vehicle,
            CharacterDefinitionAsset character, int colorIndex)
        {
            DestroyVisuals();
            boundVehicle = vehicle;
            BoundColorIndex = colorIndex;
            BoundCharacterId = NoCharacterId;
            if (vehicle == null || vehicle.prefab == null
                || contentRoot == null)
                return;

            visualRoot = new GameObject("VehicleVisual-" + vehicle.visualId);
            visualRoot.transform.SetParent(contentRoot, false);
            var instance = Instantiate(vehicle.prefab,
                visualRoot.transform);
            instance.name = vehicle.prefab.name;
            vehicleInstance = instance.transform;
            ApplyVehicleAnchor();

            seatMount = FindDeep(vehicleInstance, vehicle.seatMountName);
            gripL = FindDeep(vehicleInstance, vehicle.gripLeftName);
            gripR = FindDeep(vehicleInstance, vehicle.gripRightName);
            dashWheel = !string.IsNullOrEmpty(vehicle.dashWheelName)
                ? FindDeep(vehicleInstance, vehicle.dashWheelName)
                : null;
            var names = vehicle.wheelNames ?? new string[0];
            wheelPivots = new Transform[names.Length];
            wheelSpins = new Transform[names.Length];
            wheelSteerable = new bool[names.Length];
            wheelSpinAngles = new float[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                wheelPivots[i] = FindDeep(vehicleInstance, names[i]);
                if (wheelPivots[i] != null)
                    wheelSpins[i] = FindChild(wheelPivots[i], "Spin");
                wheelSteerable[i] = vehicle.wheelSteerable != null
                    && i < vehicle.wheelSteerable.Length
                    && vehicle.wheelSteerable[i];
                if (wheelPivots[i] == null)
                    Debug.LogWarning("RacerVisualRig: visual '"
                        + vehicle.visualId + "' is missing wheel mount '"
                        + names[i] + "' — that wheel stays unposed.");
            }

            CollectAccents(vehicleInstance);
            ApplyAccent(colorIndex);

            // Rider host on the seat mount; a missing mount keeps the
            // rider at the authored prefab's seat position instead of
            // silently dropping the animal.
            var riderHost = seatMount != null
                ? seatMount : vehicleInstance;
            if (seatMount == null)
                Debug.LogWarning("RacerVisualRig: visual '"
                    + vehicle.visualId + "' is missing seat mount '"
                    + vehicle.seatMountName + "' — rider parks at the "
                    + "prefab root.");
            var pivot = new GameObject("RiderPivot");
            pivot.transform.SetParent(riderHost, false);
            riderPivot = pivot.transform;

            if (character != null && character.prefab != null)
            {
                var charInstance = Instantiate(character.prefab,
                    riderPivot);
                charInstance.name = character.prefab.name;
                rider = charInstance.transform;
                BoundCharacterId = character.characterId;
            }
            else
            {
                // Honest placeholder: a generic seated figure under the
                // same rig-node names so aim/lean/celebration still work.
                rider = BuildFallbackRider(riderPivot);
                BoundCharacterId = FallbackCharacterId;
            }
            armL = FindDeep(rider, "ArmL");
            armR = FindDeep(rider, "ArmR");
            head = FindDeep(rider, "Head");
            CollectAccents(rider);
            ApplyAccent(colorIndex);
            PoseLegs(vehicle.vehicleClass);
            ResetVisuals();
        }

        // One shared pass over vehicle + rider renderers: every
        // "*Accent*" renderer takes the racer's profile color through a
        // property override — the authored materials stay shared.
        private void CollectAccents(Transform root)
        {
            if (root == null) return;
            foreach (var renderer in
                root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                if (renderer.name.IndexOf("Accent",
                    System.StringComparison.OrdinalIgnoreCase) >= 0)
                    accentScratch.Add(renderer);
            }
            var total = accentRenderers.Length + accentScratch.Count;
            var merged = new Renderer[total];
            accentRenderers.CopyTo(merged, 0);
            accentScratch.CopyTo(merged, accentRenderers.Length);
            accentScratch.Clear();
            accentRenderers = merged;
        }

        private void ApplyAccent(int colorIndex)
        {
            if (accentRenderers.Length == 0) return;
            var slots = RacerColors;
            var color = slots[((colorIndex % slots.Length) + slots.Length)
                % slots.Length];
            if (accentBlock == null)
                accentBlock = new MaterialPropertyBlock();
            foreach (var renderer in accentRenderers)
            {
                if (renderer == null) continue;
                renderer.GetPropertyBlock(accentBlock);
                accentBlock.SetColor("_BaseColor", color);
                accentBlock.SetColor("_Color", color);
                renderer.SetPropertyBlock(accentBlock);
            }
        }

        // Seated pose per vehicle class: legs swing forward toward the
        // car floor, astride the motorcycle's narrow body. Legs are
        // single-piece capsule pivots per the shared skeleton contract.
        private void PoseLegs(string vehicleClass)
        {
            var legL = FindDeep(rider, "LegL");
            var legR = FindDeep(rider, "LegR");
            if (legL == null || legR == null) return;
            if (vehicleClass == "motorcycle")
            {
                legL.localRotation = Quaternion.Euler(-45f, -18f, 0f);
                legR.localRotation = Quaternion.Euler(-45f, 18f, 0f);
            }
            else
            {
                legL.localRotation = Quaternion.Euler(-68f, -6f, 0f);
                legR.localRotation = Quaternion.Euler(-68f, 6f, 0f);
            }
        }

        // Cosmetic animation only — called after VehiclePresentation has
        // placed the root/lean pivot. The physical channel keeps wheel
        // pose ownership on VehiclePresentation (committed suspension +
        // spin) and this rig adds no chassis roll there: physical lean
        // must never be applied twice. Rider reactions (squash, head,
        // celebration) stay cosmetic in both modes.
        public void ApplyPose(MotorState motor, float steering, float alpha)
        {
            if (visualRoot == null || motor == null) return;
            var physical = motor.physics != null;
            if (!physical) DriveArcadeWheels(motor, steering);
            DriveRider(motor, steering, physical);
        }

        private void DriveArcadeWheels(MotorState motor, float steering)
        {
            var steerDegrees = boundVehicle != null
                ? boundVehicle.steerDegrees : 25f;
            var radius = boundVehicle != null
                ? Mathf.Max(0.05f, boundVehicle.wheelRadius) : 0.32f;
            var steer = Mathf.Clamp(steering, -1f, 1f) * steerDegrees;
            var roll = motor.speed * Time.deltaTime / radius
                * Mathf.Rad2Deg;
            for (var i = 0; i < wheelPivots.Length; i++)
            {
                var pivot = wheelPivots[i];
                if (pivot == null) continue;
                pivot.localRotation = i < wheelSteerable.Length
                    && wheelSteerable[i]
                    ? Quaternion.Euler(0f, steer, 0f)
                    : Quaternion.identity;
                var spin = i < wheelSpins.Length ? wheelSpins[i] : null;
                if (spin == null) continue;
                wheelSpinAngles[i] = (wheelSpinAngles[i] + roll) % 360f;
                // Axle laid onto the lateral axis first, then the
                // accumulated travel roll — mirrors the physical path's
                // pivot/spin split so both channels read identically.
                spin.localRotation = Quaternion.Euler(0f, 0f, 90f)
                    * Quaternion.Euler(0f, wheelSpinAngles[i], 0f);
            }
            // The car's dash wheel (and the motorcycle's bars, which live
            // on the steered front pivot) echo the steering demand.
            if (dashWheel != null)
            {
                dashSteer = Mathf.Lerp(dashSteer, steer * 2.2f,
                    1f - Mathf.Exp(-14f * Time.deltaTime));
                // Tilted column first, then spin about the wheel's own
                // axle (local Y) — Euler(0,steer,0) alone would yaw the
                // whole wheel face instead of turning it.
                dashWheel.localRotation = Quaternion.Euler(-24f, 0f, 0f)
                    * Quaternion.Euler(0f, dashSteer, 0f);
            }
        }

        private void DriveRider(MotorState motor, float steering,
            bool physical)
        {
            if (riderPivot == null) return;

            // Landing squash: track the deepest fall speed of the
            // airborne stretch, convert the landing to a short squash.
            if (motor.grounded)
            {
                if (hasPrevGrounded && !prevGrounded
                    && peakFallSpeed > 2f)
                    squash = Mathf.Clamp01(peakFallSpeed / 14f);
                peakFallSpeed = 0f;
            }
            else
            {
                peakFallSpeed = Mathf.Max(peakFallSpeed,
                    -motor.verticalSpeed);
            }
            prevGrounded = motor.grounded;
            hasPrevGrounded = true;
            squash = Mathf.MoveTowards(squash, 0f,
                SquashRecovery * Time.deltaTime);

            var celebrating = celebrateT > 0f;
            var bounce = 0f;
            if (celebrating)
            {
                celebrateT -= Time.deltaTime;
                bounce = Mathf.Abs(Mathf.Sin(
                    celebrateT * 10f)) * 0.06f
                    * Mathf.Clamp01(celebrateT / CelebrationSeconds);
            }
            riderPivot.localPosition = new Vector3(0f,
                bounce - squash * 0.1f, 0f);
            riderPivot.localScale = squash > 0f
                ? new Vector3(1f + squash * 0.35f, 1f - squash * 0.45f,
                    1f + squash * 0.35f)
                : Vector3.one;

            // Arcade rider lean adds a small extra roll relative to the
            // already-leaning chassis; in physical mode the committed
            // body attitude IS the lean, so the rider adds none.
            if (rider != null)
            {
                var targetLean = !physical && boundVehicle != null
                    ? -Mathf.Clamp(steering, -1f, 1f)
                        * boundVehicle.riderLeanDegrees
                    : 0f;
                riderLean = Mathf.Lerp(riderLean, targetLean,
                    1f - Mathf.Exp(
                        -RiderLeanSmoothing * Time.deltaTime));
                rider.localRotation =
                    Quaternion.Euler(0f, 0f, riderLean);
            }
            if (head != null)
            {
                var look = Mathf.Clamp(steering, -1f, 1f) * 18f;
                head.localRotation = Quaternion.Slerp(
                    head.localRotation,
                    Quaternion.Euler(0f, look, 0f),
                    1f - Mathf.Exp(-8f * Time.deltaTime));
            }
            if (celebrating)
            {
                RaiseArm(armL, -1f);
                RaiseArm(armR, 1f);
            }
            else
            {
                AimArm(armL, gripL);
                AimArm(armR, gripR);
            }
        }

        // Hands track the vehicle's grips every frame — the single
        // skeleton reaches a car dash wheel or motorcycle bars without
        // per-character animation data.
        private static void AimArm(Transform arm, Transform grip)
        {
            if (arm == null) return;
            var dir = grip != null
                ? grip.position - arm.position
                : (arm.parent != null ? arm.parent.forward : Vector3.forward)
                    * 0.45f + Vector3.down * 0.25f;
            if (dir.sqrMagnitude < 0.0001f) return;
            var target = Quaternion.FromToRotation(Vector3.down, dir);
            arm.rotation = Quaternion.Slerp(arm.rotation, target,
                1f - Mathf.Exp(-18f * Time.deltaTime));
        }

        private static void RaiseArm(Transform arm, float side)
        {
            if (arm == null) return;
            var up = (arm.parent != null ? arm.parent.up : Vector3.up)
                + (arm.parent != null ? arm.parent.right : Vector3.right)
                    * side * 0.25f;
            var target = Quaternion.FromToRotation(Vector3.down, up);
            arm.rotation = Quaternion.Slerp(arm.rotation, target,
                1f - Mathf.Exp(-16f * Time.deltaTime));
        }

        public void NotifyFinished()
        {
            celebrateT = CelebrationSeconds;
        }

        // Neutral pose: celebrations and squash end, arms re-aim at the
        // grips on the next ApplyPose. Visual transform identity stays
        // intact — this resets motion state, never the binding.
        public void ResetVisuals()
        {
            squash = 0f;
            celebrateT = 0f;
            riderLean = 0f;
            peakFallSpeed = 0f;
            hasPrevGrounded = false;
            prevGrounded = true;
            dashSteer = 0f;
            if (riderPivot != null)
            {
                riderPivot.localPosition = Vector3.zero;
                riderPivot.localScale = Vector3.one;
            }
            if (rider != null) rider.localRotation = Quaternion.identity;
            if (head != null) head.localRotation = Quaternion.identity;
        }

        // The unbound-rider placeholder: a generic seated figure under
        // the same node names so every rig behaviour still applies and
        // the fallback is visually honest (gray, featureless — never a
        // leftover animal).
        private static Transform BuildFallbackRider(Transform parent)
        {
            if (fallbackMaterial == null)
            {
                var shader = Shader.Find(
                    "Universal Render Pipeline/Simple Lit");
                if (shader == null)
                    shader = Shader.Find("Standard");
                fallbackMaterial = new Material(shader)
                {
                    color = new Color(0.55f, 0.55f, 0.58f)
                };
            }
            var root = new GameObject("Character-fallback");
            root.transform.SetParent(parent, false);
            var rig = new GameObject("Rig");
            rig.transform.SetParent(root.transform, false);
            var hips = new GameObject("Hips");
            hips.transform.SetParent(rig.transform, false);
            var spine = new GameObject("Spine");
            spine.transform.SetParent(hips.transform, false);
            spine.transform.localPosition = new Vector3(0f, 0.26f, 0f);
            Prim(spine.transform, PrimitiveType.Capsule, "Torso",
                fallbackMaterial, new Vector3(0f, 0.22f, 0f),
                new Vector3(0.5f, 0.32f, 0.4f));
            var headNode = new GameObject("Head");
            headNode.transform.SetParent(spine.transform, false);
            headNode.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            Prim(headNode.transform, PrimitiveType.Sphere, "HeadMesh",
                fallbackMaterial, new Vector3(0f, 0.12f, 0.02f),
                new Vector3(0.55f, 0.55f, 0.55f));
            for (var side = -1; side <= 1; side += 2)
            {
                var arm = new GameObject(side < 0 ? "ArmL" : "ArmR");
                arm.transform.SetParent(spine.transform, false);
                arm.transform.localPosition =
                    new Vector3(side * 0.3f, 0.42f, 0.04f);
                Prim(arm.transform, PrimitiveType.Capsule, "ArmMesh",
                    fallbackMaterial, new Vector3(0f, -0.16f, 0f),
                    new Vector3(0.14f, 0.22f, 0.14f));
            }
            return root.transform;
        }

        private static GameObject Prim(Transform parent,
            PrimitiveType type, string name, Material material,
            Vector3 position, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            var collider = go.GetComponent<Collider>();
            if (collider != null) DestroyImmediate(collider);
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.sharedMaterial = material;
            return go;
        }

        internal static Transform FindDeep(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;
            if (root.name == name) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static Transform FindChild(Transform parent, string name)
        {
            if (parent == null) return null;
            for (var i = 0; i < parent.childCount; i++)
                if (parent.GetChild(i).name == name)
                    return parent.GetChild(i);
            return null;
        }
    }
}
