using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FamilyRacing
{
    // Owns the dedicated local physics scene the vehicle backend
    // simulates: created with LocalPhysicsMode.Physics3D so it only
    // advances when Simulate() is called — never from FixedUpdate, render
    // Update, or Physics.autoSimulation. Course geometry lives here as
    // collision-only instances (renderers stripped); render meshes stay
    // in the render scene and read committed poses.
    //
    // Vehicles sit on VehicleLayer so the wheel suspension rays can
    // exclude them — a raycast tire must never stand on another chassis —
    // while ordinary chassis-vs-chassis collision still applies.
    public sealed class PhysicsCourseWorld : IDisposable
    {
        // Unnamed project layer reserved for physical-vehicle chassis
        // colliders. TagManager is intentionally untouched: the wheel
        // mask is computed from the layer index, not a name.
        public const int VehicleLayer = 8;
        public const int VehicleLayerMask = 1 << VehicleLayer;

        private Scene scene;
        private PhysicsScene physics;
        private bool disposed;

        public Scene Scene { get { return scene; } }
        public PhysicsScene Physics { get { return physics; } }

        // Suspension rays see every default-queryable collider except
        // other vehicle chassis (and triggers, via QueryTriggerInteraction
        // at the call site). Wall rejection is by normal, not layer, so a
        // curb face still reads as a valid drivable edge when it is flat.
        public static int WheelRaycastMask
        {
            // Fully qualified: the `Physics` property below would
            // otherwise shadow the UnityEngine.Physics type.
            get { return UnityEngine.Physics.DefaultRaycastLayers
                & ~VehicleLayerMask; }
        }

        // Empty world: bodies free-fall with nothing to contact. Used by
        // the mass/torque oracle tests that must be isolated from ground.
        public static PhysicsCourseWorld Create(string name)
        {
            return new PhysicsCourseWorld(name, null);
        }

        // World populated with a collision-only clone of the given course
        // template (prefab or scene instance): every renderer is stripped
        // and rigidbodies removed so the clone is pure static collision.
        public static PhysicsCourseWorld Create(string name,
            GameObject collisionTemplate)
        {
            return new PhysicsCourseWorld(name, collisionTemplate);
        }

        private static int worldCounter;

        private PhysicsCourseWorld(string name, GameObject collisionTemplate)
        {
            // A leaked or still-unloading world with the same base name
            // must never collide with the next one: every physical world
            // gets a process-unique suffix. Names are diagnostic only —
            // nothing keys on them.
            scene = SceneManager.CreateScene(
                (string.IsNullOrEmpty(name) ? "PhysicsCourseWorld" : name)
                    + "-" + (++worldCounter),
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            physics = scene.GetPhysicsScene();
            if (collisionTemplate != null)
                AddCollisionTemplate(collisionTemplate);
        }

        // Moves an already-created hierarchy into this world and strips
        // it down to collision: renderers, rigidbodies and trigger
        // volumes are removed from the clone. Returns the moved root.
        public GameObject AddCollisionTemplate(GameObject template)
        {
            if (template == null) return null;
            var instance = UnityEngine.Object.Instantiate(template);
            instance.name = template.name + "-collision";
            StripToCollision(instance.transform);
            SceneManager.MoveGameObjectToScene(instance, scene);
            instance.SetActive(true);
            return instance;
        }

        // Registers a pre-built hierarchy (tests build fixtures directly):
        // moved into the scene unchanged — renderers, if any, are removed
        // so everything here stays collision-only.
        public GameObject AddCollisionObject(GameObject instance)
        {
            if (instance == null) return null;
            StripRenderers(instance.transform);
            SceneManager.MoveGameObjectToScene(instance, scene);
            return instance;
        }

        private static void StripToCollision(Transform root)
        {
            // Gather first: destroying children while enumerating the
            // hierarchy is unsafe.
            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var renderer in renderers)
                UnityEngine.Object.DestroyImmediate(renderer);
            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            foreach (var filter in filters)
                UnityEngine.Object.DestroyImmediate(filter);
            var bodies = root.GetComponentsInChildren<Rigidbody>(true);
            foreach (var body in bodies)
                UnityEngine.Object.DestroyImmediate(body);
            var colliders = root.GetComponentsInChildren<Collider>(true);
            foreach (var collider in colliders)
            {
                // Trigger volumes (checkpoint gates, zones) are invisible
                // to physics queries anyway; removing them keeps the
                // collision scene minimal and unambiguous.
                if (collider.isTrigger) UnityEngine.Object.DestroyImmediate(collider);
            }
        }

        private static void StripRenderers(Transform root)
        {
            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var renderer in renderers)
                UnityEngine.Object.DestroyImmediate(renderer);
        }

        public bool Raycast(Vector3 origin, Vector3 direction,
            out RaycastHit hit, float maxDistance, int layerMask)
        {
            return physics.Raycast(origin, direction, out hit, maxDistance,
                layerMask, QueryTriggerInteraction.Ignore);
        }

        // The only legal advancement path: called exactly once per 1/60 s
        // substep by PhysicsVehicleSimulation.Step — twice per logical
        // tick. Never invoked from Update/FixedUpdate.
        public void Simulate(float dt)
        {
            physics.Simulate(dt);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (scene.IsValid() && scene.isLoaded)
            {
                // Synchronous unload: destroys every GameObject in the
                // scene (bodies and course collision alike) so a disposed
                // world can never leak colliders into later queries.
                // UnloadSceneAsync would defer destruction to the end of
                // the frame, which the leak-free Dispose contract forbids.
#pragma warning disable CS0618
                SceneManager.UnloadScene(scene);
#pragma warning restore CS0618
            }
        }
    }
}
