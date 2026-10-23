using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FamilyRacing.Core.Racing;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace FamilyRacing.EditorTools
{
    public static class LongRaceAuthoring
    {
        public const string Output = "Assets/FamilyRacing/Generated/LongRaces";
        public const string ScenePath = Output + "/GrandTour.unity";
        public const string Evidence = "docs/qa/long-races-2026-10-24";
        public static T Required<T>(string path) where T : Object
            => AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new InvalidOperationException("Missing long-race source: " + path);

        [MenuItem("Family Racing/Grand Tour/Create and Build Both")]
        public static void CreateAndBuildBoth() { Create(); LongRaceQualification.Run(); Build(); BuildOrdinary(); }
        [MenuItem("Family Racing/Grand Tour/Create")]
        public static void Create()
        {
            Directory.CreateDirectory(Output);Directory.CreateDirectory(Output+"/Campaign");AssetDatabase.Refresh();
            LongRaceRoads.Build();
            var scene=EditorSceneManager.OpenScene(AssetTrialAuthoring.ScenePath);
            if(!EditorSceneManager.SaveScene(scene,ScenePath,false))throw new IOException("Cannot save Grand Tour scene");
            var world=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<DrivingWorld>(true)).Single();
            var so=new SerializedObject(world);
            so.FindProperty("coursePrefab").objectReferenceValue=Required<GameObject>(LongRaceRoads.PrefabPath);
            so.FindProperty("contentVersion").stringValue="grand-tour-1";
            var tracks=so.FindProperty("tracks");tracks.arraySize=3;
            for(int i=0;i<3;i++)tracks.GetArrayElementAtIndex(i).objectReferenceValue=Required<TrackDefinitionAsset>(LongRaceRoads.TrackPath(i));
            so.FindProperty("track").objectReferenceValue=tracks.GetArrayElementAtIndex(0).objectReferenceValue;
            so.FindProperty("tutorial").objectReferenceValue=null;
            var vehicles=so.FindProperty("vehicles");
            for(int i=0;i<vehicles.arraySize;i++)
            {
                var source=vehicles.GetArrayElementAtIndex(i).objectReferenceValue as VehicleDefinitionAsset;if(source==null)continue;
                var vehicle=Object.Instantiate(source);bool bike=vehicle.vehicleId.StartsWith("motorcycle",StringComparison.Ordinal);
                vehicle.maxSpeed=bike?40.3f:37.5f;vehicle.boostMaxSpeed=bike?54.2f:51.4f;
                vehicle.acceleration=bike?12.5f:11.5f;vehicle.boostAcceleration=20f;
                vehicle.steeringRate=bike?1.9f:1.65f;vehicle.grip=bike?6.5f:5.5f;
                vehicle.boostDurationSeconds=3.4f;vehicle.boostDrainPerSecond=.29f;
                vehicle=LongRaceRoads.Save(vehicle,Output+"/"+vehicle.vehicleId+".asset");
                vehicles.GetArrayElementAtIndex(i).objectReferenceValue=vehicle;
            }
            BindCampaign(so);
            so.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(world);
            var hud=world.GetComponent<AssetTrialHud>();hud.courseIds=LongRaceRoads.Ids.ToArray();
            foreach(var light in scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Light>(true)))
            {
                if(light.type!=LightType.Directional)continue;
                light.color=new Color(1,.94f,.83f);light.intensity=1.12f;light.shadows=LightShadows.Soft;
                light.shadowStrength=.72f;light.shadowBias=.035f;light.transform.rotation=Quaternion.Euler(38,-38,0);
            }
            RenderSettings.ambientMode=AmbientMode.Trilight;RenderSettings.ambientSkyColor=new Color(.46f,.58f,.67f);
            RenderSettings.ambientEquatorColor=new Color(.42f,.45f,.4f);RenderSettings.ambientGroundColor=new Color(.24f,.27f,.23f);
            RenderSettings.fog=true;RenderSettings.fogMode=FogMode.Linear;
            RenderSettings.fogColor=new Color(.66f,.77f,.79f);RenderSettings.fogStartDistance=290;RenderSettings.fogEndDistance=1000;
            RenderSettings.skybox=LongRaceRoads.Mat("GrandTourSky","FFFFFF","FamilyRacing/Grand Tour Sky");
            var pc=Object.Instantiate(Required<UniversalRenderPipelineAsset>(VisualBenchmarkSetup.PcPipelinePath));
            pc.name="GrandTourPC";pc.msaaSampleCount=4;pc.shadowDistance=140;
            var pcSettings=new SerializedObject(pc);pcSettings.FindProperty("m_SoftShadowsSupported").boolValue=true;
            pcSettings.ApplyModifiedPropertiesWithoutUndo();
            pc=LongRaceRoads.Save(pc,Output+"/GrandTourPC.asset");
            foreach(var profile in scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<RenderProfileController>(true)))
            {var settings=new SerializedObject(profile);settings.FindProperty("pcPipeline").objectReferenceValue=pc;settings.ApplyModifiedPropertiesWithoutUndo();}
            foreach(var camera in scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Camera>(true)))
            {camera.farClipPlane=1250;camera.fieldOfView=66;camera.allowHDR=true;camera.allowMSAA=true;}
            foreach(var camera in scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<DrivingCamera>(true)))
            {
                var settings=new SerializedObject(camera);settings.FindProperty("chaseBack").floatValue=10.5f;
                settings.FindProperty("chaseUp").floatValue=4.0f;settings.FindProperty("targetUp").floatValue=1.8f;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            hud.coursePreviews=Previews();EditorUtility.SetDirty(hud);
            EditorSceneManager.SaveScene(scene);AssetDatabase.SaveAssets();PruneUnusedGeneratedAssets();Validate();
            Debug.Log("GRAND_TOUR_CREATED "+ScenePath);
        }
        private static void BindCampaign(SerializedObject world)
        {
            var eventSlots=world.FindProperty("events");
            for(int i=0;i<eventSlots.arraySize;i++)
            {
                var source=eventSlots.GetArrayElementAtIndex(i).objectReferenceValue as EventDefinitionAsset;if(source==null)continue;
                var evt=Object.Instantiate(source);int place=Array.IndexOf(AssetTrialCircuits.Ids,source.track.courseId);if(place<0)place=0;
                evt.track=Required<TrackDefinitionAsset>(LongRaceRoads.TrackPath(place));evt.laps=place==0?2:1;
                evt.minCourseY=-40;evt.finishGraceSeconds=240;evt.parTicks=evt.eventType==GameplayEventType.Time?12600:0;
                if(evt.objectives!=null)foreach(var objective in evt.objectives)
                    foreach(var condition in objective.alternatives)
                        if(condition.metric==FamilyRacing.Core.Progression.ObjectiveMetric.FinishWithinTicks)
                            condition.threshold=objective.kind=="mastery"?12600:18000;
                evt=LongRaceRoads.Save(evt,Output+"/Campaign/"+evt.eventId+".asset");eventSlots.GetArrayElementAtIndex(i).objectReferenceValue=evt;
            }
            var sourceCatalog=world.FindProperty("gameplayCatalog").objectReferenceValue as GameplayCatalogAsset;
            if(sourceCatalog==null)throw new InvalidOperationException("Grand Tour needs the existing trial campaign");
            var catalog=Object.Instantiate(sourceCatalog);
            var sourceCampaigns=catalog.campaigns.Length>0?catalog.campaigns:new[]{catalog.campaign};
            catalog.campaigns=sourceCampaigns.Select(source=> {
                var campaign=Object.Instantiate(source);campaign.courseIds=LongRaceRoads.Ids.ToArray();
                campaign.nameEn="Race Planet · Grand Tour";campaign.nameHe="כוכב המרוצים · המסע הגדול";
                return LongRaceRoads.Save(campaign,Output+"/Campaign/"+campaign.worldId+".asset");}).ToArray();
            catalog.campaign=null;
            catalog=LongRaceRoads.Save(catalog,Output+"/Campaign/grand-tour-catalog.asset");
            world.FindProperty("gameplayCatalog").objectReferenceValue=catalog;
        }
        private static Texture2D[] Previews()
        {
            Directory.CreateDirectory(Evidence);var textures=new Texture2D[3];
            var root=Object.Instantiate(Required<GameObject>(LongRaceRoads.PrefabPath));
            var camera=new GameObject("TourPreviewCamera").AddComponent<Camera>();camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            camera.fieldOfView=60;camera.farClipPlane=1250;camera.clearFlags=CameraClearFlags.Skybox;camera.aspect=16f/9;
            var rt=new RenderTexture(640,360,24);var active=RenderTexture.active;var async=ShaderUtil.allowAsyncCompilation;
            var priorPipeline=QualitySettings.renderPipeline;
            QualitySettings.renderPipeline=Required<UniversalRenderPipelineAsset>(Output+"/GrandTourPC.asset");
            ShaderUtil.allowAsyncCompilation=false;
            try
            {
                for(int i=0;i<3;i++)
                {
                    foreach(Transform child in root.transform)child.gameObject.SetActive(child.name==LongRaceRoads.Ids[i]);
                    var p=LongRaceRoads.Sample(i);int index=i==0?65:100;var tangent=LongRaceRoads.Tangent(p,index,i>0);
                    camera.transform.position=p[index]-tangent*48+LongRaceRoads.Right(tangent)*28+Vector3.up*25;
                    camera.transform.LookAt(p[index+5]+Vector3.up*1.5f);
                    foreach(var instance in root.GetComponentsInChildren<TourSceneryInstances>())instance.Draw(camera);
                    RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest{destination=rt});
                    RenderTexture.active=rt;var tex=new Texture2D(640,360,TextureFormat.RGB24,false){name="preview-"+LongRaceRoads.Ids[i],wrapMode=TextureWrapMode.Clamp};
                    tex.ReadPixels(new Rect(0,0,640,360),0,0);tex.Apply();
                    File.WriteAllBytes(Evidence+"/preview-"+LongRaceRoads.Ids[i]+".png",tex.EncodeToPNG());
                    textures[i]=LongRaceRoads.Save(tex,Output+"/preview-"+LongRaceRoads.Ids[i]+".asset");
                }
            }
            finally {QualitySettings.renderPipeline=priorPipeline;RenderTexture.active=active;ShaderUtil.allowAsyncCompilation=async;Object.DestroyImmediate(root);Object.DestroyImmediate(camera.gameObject);Object.DestroyImmediate(rt);}
            return textures;
        }
        public static void Validate()
        {
            var scene=EditorSceneManager.OpenScene(ScenePath);var world=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<DrivingWorld>(true)).Single();
            var prefab=Required<GameObject>(LongRaceRoads.PrefabPath);
            for(int i=0;i<3;i++)
            {
                var track=Required<TrackDefinitionAsset>(LongRaceRoads.TrackPath(i));if(track.ToCourse(out var error)==null)throw new InvalidOperationException(error);
                var points=LongRaceRoads.Sample(i);float length=0;int segments=i==0?points.Length:points.Length-1;
                for(int j=0;j<segments;j++)length+=Vector3.Distance(points[j],points[(j+1)%points.Length]);
                if(Mathf.Abs(length-LongRaceRoads.Lengths[i])>20)throw new InvalidOperationException("Wrong route length: "+length);
                if(track.isOpenRoute!=(i>0))throw new InvalidOperationException("Wrong topology");
                if(prefab.transform.Find(track.courseId)==null)throw new InvalidOperationException("Missing course hierarchy");
                Debug.Log("LONG_ROUTE_VALID "+track.courseId+" meters="+length.ToString("F1")+" open="+track.isOpenRoute);
            }
            AssetTrialCampaign.Validate(new SerializedObject(world));
        }
        public static void Build()=>BuildPlayer("Builds/Windows-LongRaces/FamilyRacing.exe",BuildOptions.Development);
        public static void BuildOrdinary()=>BuildPlayer("Builds/Windows-LongRaces-Ordinary/FamilyRacing.exe",BuildOptions.None);
        public static void BuildBoth(){Validate();Build();BuildOrdinary();}
        public static void PruneUnusedGeneratedAssets()
        {
            // This folder was created solely for this upgrade. Remove obsolete
            // generated meshes from the earlier, vertex-duplicating art pass.
            var used=new HashSet<string>(AssetDatabase.GetDependencies(ScenePath,true));
            foreach(var guid in AssetDatabase.FindAssets("",new[]{Output}))
            {
                var path=AssetDatabase.GUIDToAssetPath(guid);
                if(path.StartsWith(Output+"/",StringComparison.Ordinal)&&path.EndsWith(".asset",StringComparison.Ordinal)&&!used.Contains(path))
                    AssetDatabase.DeleteAsset(path);
            }
        }
        private static void BuildPlayer(string path,BuildOptions options)
        {
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes=new[]{ScenePath},locationPathName=path,target=BuildTarget.StandaloneWindows64,options=options });
            Debug.Log("GRAND_TOUR_BUILD "+path+" "+report.summary.result+" errors="+report.summary.totalErrors);
            if(report.summary.result!=BuildResult.Succeeded)throw new InvalidOperationException("Grand Tour build failed: "+path);
        }
    }
}
