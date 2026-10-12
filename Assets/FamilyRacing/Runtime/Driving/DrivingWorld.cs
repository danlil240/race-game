using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
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
        [SerializeField] private Vector3 cameraOffset = new Vector3(0f, 38f, -22f);
        [SerializeField] private float cameraPitchDegrees = 62f;

        private readonly Dictionary<string, VehicleDefinitionAsset> vehicleAssets =
            new Dictionary<string, VehicleDefinitionAsset>();
        private UnityMotorWorld motorWorld;
        private DrivingContentCatalog catalog;
        private GameObject courseInstance;
        private GameObject vehicleView;
        private VehiclePresentation presentation;
        private string shownVehicleId = "";
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
                    tunings, course == null ? null : new[] { course }, defs);
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
            if (courseInstance != null && courseInstance.activeSelf != active)
                courseInstance.SetActive(active);
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

        private void ApplyVehicleVisual(string vehicleId, MotorState motor,
            float steering, float visualAlpha)
        {
            if (vehicleView == null || presentation == null) return;
            if (vehicleId != shownVehicleId)
            {
                VehicleDefinitionAsset asset;
                if (vehicleAssets.TryGetValue(vehicleId ?? "", out asset) && asset != null)
                    presentation.SetVehicle(asset);
                shownVehicleId = vehicleId;
            }
            presentation.ApplyPose(motor, steering, visualAlpha);
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

        private void OnDisable()
        {
            if (overviewActive) RestoreCamera();
            if (courseInstance != null) courseInstance.SetActive(false);
            if (vehicleView != null) vehicleView.SetActive(false);
            ClearCameraFacts();
        }

        private void OnDestroy()
        {
            // VehiclePresentation owns its generated materials.
        }
    }
}
