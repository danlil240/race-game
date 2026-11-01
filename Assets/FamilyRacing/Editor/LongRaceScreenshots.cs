using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace FamilyRacing.EditorTools
{
    // Renders chase-camera views of the generated Grand Tour for visual review:
    // -executeMethod FamilyRacing.EditorTools.LongRaceScreenshots.Capture -screenshotDir <dir>
    public static class LongRaceScreenshots
    {
        private static readonly int[][] Shots = { new[] { 65, 210, 380 }, new[] { 100, 260, 420 }, new[] { 100, 300, 470 } };
        [MenuItem("Family Racing/Grand Tour/Capture Screenshots")]
        public static void Capture()
        {
            var args = Environment.GetCommandLineArgs(); int k = Array.IndexOf(args, "-screenshotDir");
            Capture(k >= 0 && k + 1 < args.Length ? args[k + 1] : "Builds/screenshots");
        }
        public static void CreateAndCapture() { LongRaceAuthoring.Create(); Capture(); }
        public static void Capture(string dir)
        {
            Directory.CreateDirectory(dir);
            EditorSceneManager.OpenScene(LongRaceAuthoring.ScenePath);
            var root = Object.Instantiate(LongRaceAuthoring.Required<GameObject>(LongRaceRoads.PrefabPath));
            var atmospheres = root.GetComponent<LongRaceVisibility>().atmospheres;
            var camera = new GameObject("ScreenshotCamera").AddComponent<Camera>(); camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            camera.fieldOfView = 66; camera.nearClipPlane = .3f; camera.farClipPlane = 1250; camera.clearFlags = CameraClearFlags.Skybox; camera.aspect = 16f / 9; camera.allowMSAA = true; camera.allowHDR = true;
            var rt = new RenderTexture(1600, 900, 24) { antiAliasing = 4 }; var active = RenderTexture.active; var async = ShaderUtil.allowAsyncCompilation;
            var priorPipeline = QualitySettings.renderPipeline; var sky = RenderSettings.skybox != null ? new Material(RenderSettings.skybox) : null; var priorSky = RenderSettings.skybox;
            var pcPath = LongRaceAuthoring.Output + "/GrandTourPC.asset";
            if (AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pcPath) != null) QualitySettings.renderPipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pcPath);
            ShaderUtil.allowAsyncCompilation = false; RenderSettings.skybox = sky;
            try
            {
                for (int place = 0; place < 3; place++)
                {
                    foreach (Transform child in root.transform) child.gameObject.SetActive(child.name == LongRaceRoads.Ids[place]);
                    if (atmospheres.Length > place) LongRaceRoads.ApplyAtmosphere(place, sky);
                    var p = LongRaceRoads.Sample(place); bool open = place > 0;
                    int ramp = (open ? 8 : 0) + 80 + 3;
                    foreach (int index in Shots[place].Append(ramp))
                    {
                        var t = LongRaceRoads.Tangent(p, index, open); var r = LongRaceRoads.Right(t);
                        string name;
                        if (index == ramp)
                        {   // From the verge, looking across at the kicker's outer skirt and flags.
                            camera.transform.position = p[index] - t * 14 + r * 13 + Vector3.up * 3.2f;
                            camera.transform.LookAt(p[index + 2] + r * 4.6f + Vector3.up * 1.2f); name = "ramp";
                        }
                        else
                        {   // Chase-camera height and distance used in game.
                            camera.transform.position = p[index] - t * 10.5f + r * 2.5f + Vector3.up * 4f;
                            camera.transform.LookAt(p[Mathf.Min(index + 4, p.Length - 1)] + r * 1.2f + Vector3.up * 1.8f); name = "road" + index;
                        }
                        foreach (var chunk in root.GetComponentsInChildren<TourSceneryInstances>(true))
                            chunk.gameObject.SetActive((chunk.transform.position - camera.transform.position).sqrMagnitude < 720f * 720f);
                        foreach (var instance in root.GetComponentsInChildren<TourSceneryInstances>()) instance.Draw(camera);
                        RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = rt });
                        RenderTexture.active = rt; var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
                        var file = Path.Combine(dir, LongRaceRoads.Ids[place] + "-" + name + ".png");
                        File.WriteAllBytes(file, tex.EncodeToPNG()); Object.DestroyImmediate(tex);
                        Debug.Log("GRAND_TOUR_SCREENSHOT " + file);
                    }
                }
            }
            finally
            {
                QualitySettings.renderPipeline = priorPipeline; RenderTexture.active = active; ShaderUtil.allowAsyncCompilation = async; RenderSettings.skybox = priorSky;
                Object.DestroyImmediate(root); Object.DestroyImmediate(camera.gameObject); Object.DestroyImmediate(rt); if (sky != null) Object.DestroyImmediate(sky);
            }
        }
    }
}
