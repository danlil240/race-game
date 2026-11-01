using System;
using System.Collections.Generic;
using UnityEngine;

namespace FamilyRacing
{
    // Course collision stays active throughout its race. Only distant decorative
    // chunks are culled, so visibility never changes authoritative driving.
    public sealed class LongRaceVisibility : MonoBehaviour
    {
        // Per-course sky, fog and sky-matched ambient, applied when a course is selected.
        [Serializable] public struct Atmosphere
        {
            public string courseId;
            public Color zenith, horizon, fog, ambientSky, ambientEquator, ambientGround;
            public float fogStart, fogEnd;
        }
        public Atmosphere[] atmospheres = Array.Empty<Atmosphere>();
        public float sceneryDistance = 720f;
        private Material sky;
        private readonly List<Transform> chunks = new List<Transform>();
        private string selected;
        private float nextUpdate;
        public void SelectCourse(string courseId)
        {
            if (selected == courseId) return;
            selected = courseId; chunks.Clear();
            foreach (Transform course in transform)
            {
                bool active = string.Equals(course.name,courseId,StringComparison.Ordinal);
                course.gameObject.SetActive(active);
                if (!active) continue;
                var scenery=course.Find("Scenery");if(scenery==null)continue;
                foreach(Transform chunk in scenery)chunks.Add(chunk);
            }
            nextUpdate=0;
            if (Application.isPlaying) ApplyAtmosphere(courseId);
        }
        private void ApplyAtmosphere(string courseId)
        {
            foreach (var a in atmospheres)
            {
                if (!string.Equals(a.courseId, courseId, StringComparison.Ordinal)) continue;
                RenderSettings.fogColor = a.fog; RenderSettings.fogStartDistance = a.fogStart; RenderSettings.fogEndDistance = a.fogEnd;
                RenderSettings.ambientSkyColor = a.ambientSky; RenderSettings.ambientEquatorColor = a.ambientEquator; RenderSettings.ambientGroundColor = a.ambientGround;
                if (RenderSettings.skybox != null)
                {
                    if (sky == null) RenderSettings.skybox = sky = new Material(RenderSettings.skybox);
                    sky.SetColor("_Zenith", a.zenith); sky.SetColor("_Horizon", a.horizon);
                }
                return;
            }
        }
        private void LateUpdate()
        {
            if(Time.unscaledTime<nextUpdate)return;nextUpdate=Time.unscaledTime+.25f;
            var cam=Camera.main;if(cam==null)return;var position=cam.transform.position;
            foreach(var chunk in chunks)
            {
                if(chunk==null)continue;bool visible=(chunk.position-position).sqrMagnitude<sceneryDistance*sceneryDistance;
                if(chunk.gameObject.activeSelf!=visible)chunk.gameObject.SetActive(visible);
            }
        }
    }
}
