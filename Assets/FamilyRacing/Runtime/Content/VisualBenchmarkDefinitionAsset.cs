using UnityEngine;

namespace FamilyRacing
{
    // Visual-benchmark content contract (M4): the scene, its content
    // identity and the authored references the benchmark is measured
    // against. The definition is the single source the generated
    // VisualBenchmark scene is wired from — same course/gates/route/
    // recovery as the shared graybox track, under a distinct content
    // version so a benchmark player can never silently join a graybox
    // LAN race. Tasks 4–5 add typed vehicle/character/effect catalog
    // references here when those types exist.
    [CreateAssetMenu(menuName = "FamilyRacing/Visual Benchmark Definition",
        fileName = "visual-benchmark")]
    public sealed class VisualBenchmarkDefinitionAsset : ScriptableObject
    {
        // Benchmark content identity, deliberately distinct from the
        // driving slice's "driving-0.1" and the physics slice's
        // "physics-1": session admission requires matching versions.
        public string presentationVersion = "visual-1";
        // The visible course hierarchy the benchmark scene instantiates.
        // Until Task 3 authors the Sunny Island dressing this is the
        // shared graybox prefab — the same colliders the motor probes,
        // so gameplay geometry can never silently diverge.
        public GameObject courseVisualPrefab;
        // The shared graybox track the benchmark is measured on: same
        // gates, route, spawn grid and recovery rules as the driving
        // slice. Referenced, never copied.
        public TrackDefinitionAsset sourceTrack;
        // Task-4 typed catalog: the four authored animals in slot order
        // (fox, rabbit, panda, raccoon — the same order ProfileValidation
        // assigns avatars, so AI/slot cosmetics land correctly). Vehicle
        // visuals bind per-vehicle on VehicleDefinitionAsset.visual.
        public CharacterDefinitionAsset[] characters;
        // Task-5 typed catalog: the authored cue/effect bindings the
        // scene's race effects/audio presenters consume — cue clips,
        // effect prefab pools, voice bounds and volume categories.
        public RacePresentationCatalogAsset presentationCatalog;

        public bool Validate(out string error)
        {
            if (string.IsNullOrEmpty(presentationVersion))
            {
                error = name + ": presentationVersion is empty";
                return false;
            }
            if (presentationVersion == "driving-0.1"
                || presentationVersion == "physics-1")
            {
                error = name + ": presentationVersion collides with the "
                    + presentationVersion + " content identity";
                return false;
            }
            if (courseVisualPrefab == null)
            {
                error = name + ": missing courseVisualPrefab reference";
                return false;
            }
            if (sourceTrack == null)
            {
                error = name + ": missing sourceTrack reference";
                return false;
            }
            if (presentationCatalog == null)
            {
                error = name + ": missing presentationCatalog reference";
                return false;
            }
            error = null;
            return true;
        }
    }
}
