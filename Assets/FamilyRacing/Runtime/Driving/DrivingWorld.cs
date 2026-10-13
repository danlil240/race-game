using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using UnityEngine;

namespace FamilyRacing
{
    // Owns the graybox course instance, the visible vehicle and the
    // temporary fixed overview camera framing — a test-only fallback that
    // stays off once FoundationApp binds a real DrivingCamera (the Task-4
    // presentation camera then owns Camera.main alone). Builds the
    // validated Core content catalog from the generated definition assets
    // and applies committed motor state to visuals each frame —
    // simulation lives entirely in Core.
    public sealed class DrivingWorld : MonoBehaviour
    {
        [SerializeField] private VehicleDefinitionAsset[] vehicles;
        [SerializeField] private TrackDefinitionAsset track;
        [SerializeField] private EventDefinitionAsset[] events;
        [SerializeField] private TutorialDefinitionAsset tutorial;
        [SerializeField] private GameObject coursePrefab;
        [SerializeField] private string contentVersion = "driving-0.1";
        // Developer-facing race-level backend selection, bound to this
        // content identity: it lands on the catalog (and therefore on
        // the compatibility token), never on a player-facing toggle.
        [SerializeField] private VehicleSimulationBackend simulationBackend
            = VehicleSimulationBackend.Arcade;
        [SerializeField] private Vector3 cameraOffset = new Vector3(0f, 38f, -22f);
        [SerializeField] private float cameraPitchDegrees = 62f;

        private readonly Dictionary<string, VehicleDefinitionAsset> vehicleAssets =
            new Dictionary<string, VehicleDefinitionAsset>();
        private UnityMotorWorld motorWorld;
        private DrivingContentCatalog catalog;
        private GameObject courseInstance;
        // Solo and LAN frames share the one course instance: each path
        // records its own requirement and RefreshCourseInstance applies
        // the OR of both. Applying either path's "inactive" verdict
        // unconditionally let the later call in the frame hide an active
        // course — FoundationApp.Update calls ApplyFrame then
        // ApplyRaceFrame(null) every non-LAN frame, which switched the
        // ground colliders off during every solo race and let the motor
        // fall through the world into an offCourse recovery loop.
        private bool soloCourseActive;
        private bool raceCourseActive;
        private GameObject vehicleView;
        private VehiclePresentation presentation;
        private string shownVehicleId = "";

        // One participant-keyed vehicle view for a network race: same
        // construction as the solo visual (a VehiclePresentation on a
        // child object, no colliders), plus the vehicle id it currently
        // shows so a change of mount rebuilds the silhouette once.
        private sealed class RacerView
        {
            internal GameObject root;
            internal VehiclePresentation presentation;
            internal string vehicleId = "";
        }
        private readonly Dictionary<string, RacerView> racerViews =
            new Dictionary<string, RacerView>(System.StringComparer.Ordinal);
        private Camera mainCamera;
        private Vector3 savedCameraPosition;
        private Quaternion savedCameraRotation;
        private bool cameraSaved;
        private bool overviewActive;
        // Bound by FoundationApp when a real DrivingCamera component owns
        // Camera.main: the temporary overview fallback then stays off
        // (test-only path), so two systems never write the camera pose in
        // the same frame and no stale overview pose can leak if the
        // presentation camera disengages.
        private bool presentationCameraBound;
        private readonly DrivingCameraState cameraFacts = new DrivingCameraState();

        public DrivingContentCatalog Catalog { get { return catalog; } }
        public UnityMotorWorld MotorWorld { get { return motorWorld; } }

        // The runtime-owned simulation provider: maps the catalog's
        // race-level backend selection to a concrete world — the arcade
        // adapter over the shared IMotorWorld, or a dedicated local
        // physics scene populated with a collision-only clone of the
        // course prefab. Core never constructs these; the caller owns the
        // returned simulation's lifetime (solo session, LAN host race, or
        // client prediction — exactly one owner per screen).
        public IVehicleSimulation CreateSimulation(DrivingContentCatalog content)
        {
            var backend = content != null
                ? content.SimulationBackend : VehicleSimulationBackend.Arcade;
            if (backend == VehicleSimulationBackend.PhysicalAssisted)
            {
                var physicsWorld = coursePrefab != null
                    ? PhysicsCourseWorld.Create("DrivingPhysics", coursePrefab)
                    : PhysicsCourseWorld.Create("DrivingPhysics");
                return new PhysicsVehicleSimulation(physicsWorld);
            }
            return motorWorld != null
                ? (IVehicleSimulation)new ArcadeVehicleSimulation(motorWorld)
                : null;
        }

        // The client prediction world's provider (Task 10): same backend
        // mapping as CreateSimulation, but the physical path returns a
        // PhysicsPredictionSimulation — a separate collision-only scene
        // that structurally holds exactly one local body. Remote display
        // vehicles are never collision here; host racer contacts stay
        // authoritative and arrive as corrections.
        public IVehicleSimulation CreatePredictionSimulation(
            DrivingContentCatalog content)
        {
            var backend = content != null
                ? content.SimulationBackend : VehicleSimulationBackend.Arcade;
            if (backend == VehicleSimulationBackend.PhysicalAssisted)
            {
                var physicsWorld = coursePrefab != null
                    ? PhysicsCourseWorld.Create("PredictionPhysics", coursePrefab)
                    : PhysicsCourseWorld.Create("PredictionPhysics");
                return new Networking.PhysicsPredictionSimulation(physicsWorld);
            }
            return motorWorld != null
                ? (IVehicleSimulation)new ArcadeVehicleSimulation(motorWorld)
                : null;
        }
        // The validated tutorial definition for FoundationService and the
        // authored asset for the presenter's clip slots. Tutorial stays
        // optional content: without it the races still load and
        // tutorial.start simply reports contentUnavailable.
        public Core.Tutorial.TutorialDefinition Tutorial { get; private set; }
        public TutorialDefinitionAsset TutorialAsset { get { return tutorial; } }
        public DrivingCameraState CameraFacts { get { return cameraFacts; } }

        // FoundationApp calls this once a DrivingCamera is bound/active;
        // the world's own ApplyFrame stops driving Camera.main from then on.
        public void BindPresentationCamera()
        {
            presentationCameraBound = true;
        }

        // Build + validate the catalog once. Returns null (fail-closed:
        // race.start reports contentUnavailable) when any definition fails
        // validation.
        public DrivingContentCatalog Load()
        {
            if (catalog != null) return catalog;
            motorWorld = new UnityMotorWorld();
            vehicleAssets.Clear();
            var tunings = new List<VehicleTuning>();
            if (vehicles != null)
                foreach (var asset in vehicles)
                {
                    if (asset == null) continue;
                    string error;
                    var tuning = asset.ToTuning(out error);
                    if (tuning == null)
                    {
                        Debug.LogError("DrivingWorld: " + error);
                        return null;
                    }
                    tunings.Add(tuning);
                    vehicleAssets[tuning.vehicleId] = asset;
                }
            CourseDefinition course = null;
            if (track != null)
            {
                string error;
                course = track.ToCourse(out error);
                if (course == null)
                {
                    Debug.LogError("DrivingWorld: " + error);
                    return null;
                }
            }
            var defs = new List<EventDefinition>();
            if (events != null)
                foreach (var asset in events)
                {
                    if (asset == null) continue;
                    string error;
                    var evt = asset.ToEvent(out error);
                    if (evt == null)
                    {
                        Debug.LogError("DrivingWorld: " + error);
                        return null;
                    }
                    defs.Add(evt);
                }
            Tutorial = null;
            if (tutorial != null)
            {
                string tutorialError;
                Tutorial = tutorial.ToDefinition(out tutorialError);
                if (Tutorial == null)
                {
                    Debug.LogError("DrivingWorld: " + tutorialError);
                    return null;
                }
            }
            else
            {
                Debug.LogWarning("DrivingWorld: no tutorial definition assigned; " +
                    "tutorial.start will report contentUnavailable.");
            }
            try
            {
                catalog = DrivingContentCatalog.Create(contentVersion,
                    tunings, course == null ? null : new[] { course }, defs,
                    simulationBackend);
            }
            catch (System.ArgumentException error)
            {
                Debug.LogError("DrivingWorld: " + error.Message);
                return null;
            }
            if (coursePrefab != null && courseInstance == null)
            {
                // Parented under this component so scene/test teardown of
                // the host never leaks course or vehicle objects.
                courseInstance = Instantiate(coursePrefab, transform);
                courseInstance.name = "GrayboxCourse";
                courseInstance.SetActive(false);
            }
            if (vehicleView == null)
            {
                vehicleView = new GameObject("DrivingVehicle");
                vehicleView.transform.SetParent(transform, false);
                // Visual only: the swept motor handles collision through
                // IMotorWorld, so this hierarchy carries no colliders.
                presentation = vehicleView.AddComponent<VehiclePresentation>();
                vehicleView.SetActive(false);
            }
            if (mainCamera == null) mainCamera = Camera.main;
            return catalog;
        }

        // Called once per frame with the latest snapshot: shows the course
        // and vehicle only while a race session exists, applies committed
        // motor state to the visual, and drives the temporary overview
        // camera. Camera facts are published back for observation.
        // `visualAlpha` is the render-time fraction of the current sim
        // tick already elapsed — the visual interpolates between the last
        // two committed poses instead of stepping at 30 Hz.
        public void ApplyFrame(FoundationState state)
        {
            ApplyFrame(state, 1f);
        }

        public void ApplyFrame(FoundationState state, float visualAlpha)
        {
            var session = state != null ? state.driving : null;
            var active = session != null && session.motor != null;
            soloCourseActive = active;
            RefreshCourseInstance();
            if (vehicleView != null && vehicleView.activeSelf != active)
                vehicleView.SetActive(active);
            if (active)
            {
                var motor = session.motor;
                ApplyVehicleVisual(session.vehicleId, motor,
                    session.input != null ? session.input.steering : 0f,
                    visualAlpha);
                if (presentationCameraBound)
                    // The bound presentation camera publishes its own
                    // facts; the fallback reports nothing rather than a
                    // phantom pose.
                    ClearCameraFacts();
                else
                    ApplyOverviewCamera(motor);
            }
            else if (overviewActive)
            {
                RestoreCamera();
                ClearCameraFacts();
            }
        }

        // Called once per frame with the latest host race state (LAN
        // play): shows the course and one vehicle view per participant,
        // keyed by participantId and capped at the shared participant
        // capacity. Ids absent from the frame lose their view
        // deterministically — stale views are destroyed immediately in
        // ordinal order, so a shrinking or cleared roster never leaves
        // a phantom car on course. The solo ApplyFrame path is
        // untouched and still owns the single DrivingVehicle visual.
        public void ApplyRaceFrame(NetworkRaceState race)
        {
            ApplyRaceFrame(race, 1f);
        }

        public void ApplyRaceFrame(NetworkRaceState race, float visualAlpha)
        {
            ApplyRaceFrame(race, visualAlpha, null);
        }

        // `poseOverrides` is the Task-6 presentation seam: when supplied,
        // a participant's vehicle renders the given motor pose (locally
        // predicted or remotely interpolated) instead of the raw
        // authoritative snapshot pose. Roster/view lifecycle still keys
        // off the authoritative racers list — overrides only ever
        // replace the *pose*, never identity or authority facts.
        public void ApplyRaceFrame(NetworkRaceState race, float visualAlpha,
            IReadOnlyDictionary<string, MotorState> poseOverrides)
        {
            var racers = race != null ? race.racers : null;
            var active = racers != null && racers.Count > 0;
            raceCourseActive = active;
            RefreshCourseInstance();
            if (!active)
            {
                ClearRacerViews();
                return;
            }
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            for (var i = 0; i < racers.Count; i++)
            {
                var racer = racers[i];
                if (racer == null || racer.session == null
                    || racer.session.motor == null
                    || string.IsNullOrEmpty(racer.participantId))
                    continue;
                // Hard capacity: extra participants never manufacture
                // views beyond the shared four-slot bound.
                if (!racerViews.ContainsKey(racer.participantId)
                    && racerViews.Count >= NetworkSettings.ParticipantCapacity)
                    continue;
                seen.Add(racer.participantId);
                var view = EnsureRacerView(racer.participantId);
                MotorState overrideMotor = null;
                if (poseOverrides != null)
                    poseOverrides.TryGetValue(racer.participantId,
                        out overrideMotor);
                ApplyRacerVisual(view, racer, visualAlpha, overrideMotor);
            }
            // Deterministic cleanup in ordinal key order.
            var stale = new List<string>();
            foreach (var key in racerViews.Keys)
                if (!seen.Contains(key)) stale.Add(key);
            stale.Sort(System.StringComparer.Ordinal);
            foreach (var key in stale) DestroyRacerView(key);
        }

        private RacerView EnsureRacerView(string participantId)
        {
            RacerView view;
            if (racerViews.TryGetValue(participantId, out view)
                && view != null && view.root != null)
            {
                // OnDisable hides every root; re-show on the next frame.
                if (!view.root.activeSelf) view.root.SetActive(true);
                return view;
            }
            var root = new GameObject("RacerVehicle-" + participantId);
            root.transform.SetParent(transform, false);
            // Visual only: collision stays in the Core swept motor, so
            // this hierarchy carries no colliders.
            view = new RacerView
            {
                root = root,
                presentation = root.AddComponent<VehiclePresentation>()
            };
            racerViews[participantId] = view;
            return view;
        }

        private void ApplyRacerVisual(RacerView view, NetworkRacerState racer,
            float visualAlpha, MotorState poseOverride)
        {
            if (view == null || view.presentation == null) return;
            if (racer.vehicleId != view.vehicleId)
            {
                VehicleDefinitionAsset asset;
                if (!vehicleAssets.TryGetValue(racer.vehicleId ?? "", out asset)
                    || asset == null)
                    asset = FallbackVehicle(racer.vehicleId);
                view.presentation.SetVehicle(asset);
                view.vehicleId = racer.vehicleId;
            }
            var steering = racer.session.input != null
                ? racer.session.input.steering : 0f;
            view.presentation.ApplyPose(
                poseOverride ?? racer.session.motor, steering, visualAlpha);
        }

        // DestroyImmediate: a deferred Destroy would leave the stale view
        // alive in the scene for a frame — visible to same-frame queries
        // and captures. Teardown order is the caller's sorted key list,
        // so cleanup is deterministic.
        private void DestroyRacerView(string participantId)
        {
            RacerView view;
            if (racerViews.TryGetValue(participantId, out view))
            {
                racerViews.Remove(participantId);
                if (view != null && view.root != null)
                    DestroyImmediate(view.root);
            }
        }

        private void ClearRacerViews()
        {
            var keys = new List<string>(racerViews.Keys);
            keys.Sort(System.StringComparer.Ordinal);
            foreach (var key in keys) DestroyRacerView(key);
        }

        private void ApplyVehicleVisual(string vehicleId, MotorState motor,
            float steering, float visualAlpha)
        {
            if (vehicleView == null || presentation == null) return;
            if (vehicleId != shownVehicleId)
            {
                VehicleDefinitionAsset asset;
                if (!vehicleAssets.TryGetValue(vehicleId ?? "", out asset)
                    || asset == null)
                {
                    // An unresolvable id must not leave the previous
                    // vehicle's silhouette on screen — fall back to the
                    // generic body and say so once per id.
                    asset = FallbackVehicle(vehicleId);
                }
                presentation.SetVehicle(asset);
                shownVehicleId = vehicleId;
            }
            presentation.ApplyPose(motor, steering, visualAlpha);
        }

        private readonly Dictionary<string, VehicleDefinitionAsset> fallbackAssets =
            new Dictionary<string, VehicleDefinitionAsset>();

        private VehicleDefinitionAsset FallbackVehicle(string vehicleId)
        {
            var key = vehicleId ?? "";
            VehicleDefinitionAsset fallback;
            if (!fallbackAssets.TryGetValue(key, out fallback) || fallback == null)
            {
                Debug.LogWarning("DrivingWorld: no definition asset for vehicle '"
                    + key + "' — showing the generic fallback silhouette.");
                fallback = ScriptableObject.CreateInstance<VehicleDefinitionAsset>();
                fallback.vehicleId = key;
                fallback.silhouette = ""; // the generic-cube rebuild path
                fallbackAssets[key] = fallback;
            }
            return fallback;
        }

        private void ApplyOverviewCamera(MotorState motor)
        {
            if (mainCamera == null) mainCamera = Camera.main;
            if (mainCamera == null) return;
            if (!cameraSaved)
            {
                savedCameraPosition = mainCamera.transform.position;
                savedCameraRotation = mainCamera.transform.rotation;
                cameraSaved = true;
            }
            overviewActive = true;
            var target = new Vector3(motor.x, motor.y + 0.5f, motor.z);
            mainCamera.transform.SetPositionAndRotation(
                target + cameraOffset, Quaternion.Euler(cameraPitchDegrees, 0f, 0f));
            cameraFacts.mode = "overview";
            cameraFacts.px = mainCamera.transform.position.x;
            cameraFacts.py = mainCamera.transform.position.y;
            cameraFacts.pz = mainCamera.transform.position.z;
            cameraFacts.tx = target.x;
            cameraFacts.ty = target.y;
            cameraFacts.tz = target.z;
        }

        private void RestoreCamera()
        {
            overviewActive = false;
            if (mainCamera != null && cameraSaved)
                mainCamera.transform.SetPositionAndRotation(savedCameraPosition, savedCameraRotation);
        }

        // mode:"none" must carry no pose: consumers keying on coordinates
        // would otherwise read a phantom framing from the last live mode.
        private void ClearCameraFacts()
        {
            cameraFacts.mode = "none";
            cameraFacts.px = cameraFacts.py = cameraFacts.pz = 0f;
            cameraFacts.tx = cameraFacts.ty = cameraFacts.tz = 0f;
        }

        private void RefreshCourseInstance()
        {
            if (courseInstance == null) return;
            var active = soloCourseActive || raceCourseActive;
            if (courseInstance.activeSelf != active)
                courseInstance.SetActive(active);
        }

        private void OnDisable()
        {
            if (overviewActive) RestoreCamera();
            soloCourseActive = false;
            raceCourseActive = false;
            if (courseInstance != null) courseInstance.SetActive(false);
            if (vehicleView != null) vehicleView.SetActive(false);
            foreach (var view in racerViews.Values)
                if (view != null && view.root != null)
                    view.root.SetActive(false);
            ClearCameraFacts();
        }

        private void OnDestroy()
        {
            // VehiclePresentation owns its generated materials.
            foreach (var fallback in fallbackAssets.Values)
                if (fallback != null) Destroy(fallback);
            fallbackAssets.Clear();
        }
    }
}
