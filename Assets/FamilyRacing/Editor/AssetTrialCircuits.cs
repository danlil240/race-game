using System;
using System.Collections.Generic;
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
    // The user-requested replacement for the open benchmark map: three
    // closed circuits. One sampled centerline authors all road geometry,
    // barrier collision, checkpoints and the AI line.
    public static class AssetTrialCircuits
    {
        public const string Dir = AssetTrialAuthoring.Output + "/Circuits";
        public static readonly string[] Ids = { "coastal-village", "pine-forest", "desert-canyon" };
        public static readonly string[] Names = { "Coastal Village", "Pine Forest", "Desert Canyon" };
        public static readonly string[] HebrewNames = { "כפר החוף", "יער האורנים", "קניון המדבר" };
        public const string PrefabPath = Dir + "/RacePlaces.prefab";
        public static string TrackPath(int i) => Dir + "/" + Ids[i] + ".asset";
        public static string PreviewPath(string id) => Dir + "/preview-" + id + ".asset";
        private const float RoadHalf = 7f, BarrierOffset = 11f;
        private static readonly Vector2[][] Controls =
        {
            new[] { new Vector2(-70,-50), new Vector2(-70,30), new Vector2(-45,68), new Vector2(20,72), new Vector2(73,42), new Vector2(82,-15), new Vector2(45,-65), new Vector2(-18,-70) },
            new[] { new Vector2(-76,-50), new Vector2(-78,8), new Vector2(-42,60), new Vector2(15,50), new Vector2(64,70), new Vector2(96,20), new Vector2(65,-36), new Vector2(12,-34), new Vector2(-10,-75) },
            new[] { new Vector2(-85,-55), new Vector2(-90,38), new Vector2(-43,66), new Vector2(14,43), new Vector2(69,73), new Vector2(100,14), new Vector2(73,-61), new Vector2(12,-74), new Vector2(-30,-42) }
        };

        public static GameObject Build()
        {
            Directory.CreateDirectory(Dir); AssetDatabase.Refresh();
            var root = new GameObject("ThreeClosedRacePlaces");
            try
            {
                for (int i = 0; i < 3; i++) BuildPlace(root.transform, i);
                BuildPreviews(root.transform);
                return PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void BuildPreviews(Transform worldRoot)
        {
            var previousPipeline = QualitySettings.renderPipeline;
            var previousAsync = ShaderUtil.allowAsyncCompilation;
            var previousActive = RenderTexture.active;
            var previousFog = RenderSettings.fog;
            ShaderUtil.allowAsyncCompilation = false;
            RenderSettings.fog = false;
            var rt = new RenderTexture(384, 216, 24);
            var sun = new GameObject("CoursePreviewSun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.05f;
            sun.color = new Color(1f, 0.96f, 0.88f);
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(42f, -32f, 0f);
            var camera = new GameObject("CoursePreviewCamera").AddComponent<Camera>();
            camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.fieldOfView = 46f;
            camera.aspect = 384f / 216f;
            camera.farClipPlane = 420f;
            try
            {
                QualitySettings.renderPipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(VisualBenchmarkSetup.PcPipelinePath);
                for (var i = 0; i < 3; i++)
                {
                    foreach (Transform child in worldRoot)
                        child.gameObject.SetActive(child.name == Ids[i]);
                    var points = Sample(i);
                    var tangent = Tangent(points, 0);
                    var right = Right(tangent);
                    camera.backgroundColor = i == 0 ? new Color(0.45f, 0.73f, 0.88f)
                        : i == 1 ? new Color(0.38f, 0.62f, 0.74f)
                        : new Color(0.84f, 0.64f, 0.45f);
                    camera.transform.position = points[0] - tangent * 26f + right * 11f + Vector3.up * 12f;
                    camera.transform.LookAt(points[3] + Vector3.up * 1.8f);
                    RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = rt });
                    RenderTexture.active = rt;
                    var tex = new Texture2D(384, 216, TextureFormat.RGB24, false)
                    {
                        name = "preview-" + Ids[i],
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Bilinear
                    };
                    tex.ReadPixels(new Rect(0, 0, 384, 216), 0, 0);
                    tex.Apply(false, false);
                    Save(tex, PreviewPath(Ids[i]));
                }
                foreach (Transform child in worldRoot)
                    child.gameObject.SetActive(true);
            }
            finally
            {
                RenderSettings.fog = previousFog;
                RenderTexture.active = previousActive;
                QualitySettings.renderPipeline = previousPipeline;
                ShaderUtil.allowAsyncCompilation = previousAsync;
                Object.DestroyImmediate(camera.gameObject);
                Object.DestroyImmediate(sun.gameObject);
                Object.DestroyImmediate(rt);
            }
        }

        private static Vector3[] Sample(int place)
        {
            var controls = Controls[place]; var points = new List<Vector3>();
            for (int i = 0; i < controls.Length; i++)
            for (int j = 0; j < 8; j++)
            {
                var a = controls[(i + controls.Length - 1) % controls.Length]; var b = controls[i];
                var c = controls[(i + 1) % controls.Length]; var d = controls[(i + 2) % controls.Length];
                float t = j / 8f;
                var p = .5f * ((2 * b) + (-a+c)*t + (2*a-5*b+4*c-d)*t*t + (-a+3*b-3*c+d)*t*t*t);
                points.Add(new Vector3(p.x + place * 450, .04f, p.y));
            }
            return points.ToArray();
        }

        private static Vector3 Tangent(Vector3[] p, int i) => (p[(i+1)%p.Length] - p[(i+p.Length-1)%p.Length]).normalized;
        private static Vector3 Right(Vector3 tangent) => new Vector3(tangent.z, 0, -tangent.x);

        private static TrackDefinitionAsset.SpawnSlot RoadPose(Vector3[] points, int start, float behind, float lane = 0)
        {
            int end = start;
            for (int walked = 0; walked < points.Length; walked++)
            {
                int begin = (end + points.Length - 1) % points.Length;
                var delta = points[end] - points[begin];
                float length = delta.magnitude;
                if (behind <= length)
                {
                    var forward = delta / length;
                    return new TrackDefinitionAsset.SpawnSlot {
                        position = points[end] - forward * behind + Right(forward) * lane + Vector3.up * .025f,
                        headingDegrees = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg };
                }
                behind -= length;
                end = begin;
            }
            throw new InvalidOperationException("Grid exceeds circuit length");
        }

        private static void BuildPlace(Transform parent, int place)
        {
            var root = Node(parent, Ids[place]); var points = Sample(place);
            var sand = Mat("BeachSand", "CDBB91"); var grass = Mat("ForestGround", "5C8052"); var desert = Mat("DesertEarth", "BD875C");
            var ground = Box(root, "RoughGround", new Vector3(place * 450, -.20f, 0), new Vector3(330, .28f, 290), place == 0 ? sand : place == 1 ? grass : desert, true);
            ground.AddComponent<DrivingSurface>();
            var road = Mat("CircuitTarmac", "727D82");
            road.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(AssetTrialAuthoring.Output + "/Materials/Texture_road.asset"));
            var white = Mat("CircuitIvory", "FFF2D6"); var dark = Mat("CircuitNavy", "244756");
            var red = Mat("CircuitCoral", "CF5D45");
            var shoulder = place == 0 ? Mat("CoastalGravel", "ADAA93") : place == 1 ? Mat("ForestGravel", "8E947B") : Mat("CanyonGravel", "D2AB7B");
            Band(root, "PavedRoad", points, -RoadHalf, RoadHalf, .025f, new[] { road }, true);
            Band(root, "RunoffLeft", points, -BarrierOffset, -RoadHalf, 0, new[] { shoulder }, false);
            Band(root, "RunoffRight", points, RoadHalf, BarrierOffset, 0, new[] { shoulder }, false);
            Band(root, "CurbLeft", points, -RoadHalf-.45f, -RoadHalf+.15f, .04f, new[] { white, place == 1 ? dark : red }, false);
            Band(root, "CurbRight", points, RoadHalf-.15f, RoadHalf+.45f, .04f, new[] { white, place == 1 ? dark : red }, false);
            var barriers = Node(root, "ContinuousBarriers");
            for (int i = 0; i < points.Length; i++)
            foreach (int side in new[] { -1, 1 })
            {
                var a = points[i] + Right(Tangent(points, i)) * (side * BarrierOffset);
                int j = (i+1)%points.Length;
                var b = points[j] + Right(Tangent(points, j)) * (side * BarrierOffset);
                var box = Box(barriers, (side < 0 ? "InnerRail_" : "OuterRail_") + i,
                    (a+b)*.5f + Vector3.up*.72f, new Vector3(.55f, 1.5f, Vector3.Distance(a,b)+.28f), i%3 == 0 ? white : place == 2 ? red : dark, true);
                box.transform.rotation = Quaternion.LookRotation(b-a);
            }
            BatchVisuals(barriers, Ids[place] + "Rails");
            AuthorTrack(points, place);
            FinishArch(root, points, place);
            Dress(root, points, place);
        }

        private static void AuthorTrack(Vector3[] points, int place)
        {
            var track = ScriptableObject.CreateInstance<TrackDefinitionAsset>(); track.courseId = Ids[place];
            track.nameEn = Names[place]; track.nameHe = HebrewNames[place];
            var spawn = RoadPose(points, 0, 12);
            track.spawnPosition = spawn.position;
            track.spawnHeadingDegrees = spawn.headingDegrees;
            track.spawnSlots = Enumerable.Range(0,4).Select(i =>
                RoadPose(points, 0, 12 + i/2 * 6f, i%2 == 0 ? -2.3f : 2.3f)).ToArray();
            track.aiRoute = points.Select((p, i) => new TrackDefinitionAsset.RoutePoint { x = p.x, z = p.z, radius = 3f }).ToArray();
            track.gates = Enumerable.Range(0, points.Length/4).Select(n =>
            {
                int i = n*4; var t = Tangent(points, i); var recovery = RoadPose(points, i, 3);
                return new TrackDefinitionAsset.Gate { id = n == 0 ? "start" : "gate-"+n, center = points[i], halfWidth = 9.2f,
                    direction = new Vector2(t.x, t.z), recoverPosition = recovery.position, recoverHeadingDegrees = recovery.headingDegrees };
            }).ToArray();
            track.recoveryBounds = new[] { new TrackDefinitionAsset.BoundsVolume { min = new Vector3(place*450-165,-3,-145), max = new Vector3(place*450+165,15,145) } };
            track.shortcutLanes = Array.Empty<TrackDefinitionAsset.ShortcutLane>();
            track.pickupSites = new[] { 9, 25, 41 }.SelectMany((i,n) => new[] { -4.2f, 0, 4.2f }.Select((lane,k) => new TrackDefinitionAsset.PickupSite
                { id = Ids[place]+"-pickup-"+n+"-"+k, center = points[i%points.Length] + Right(Tangent(points,i%points.Length))*lane,
                    radiusMeters = 2.25f, verticalRangeMeters = 2f })).ToArray();
            track = Save(track, TrackPath(place));
            File.WriteAllText(Dir+"/"+Ids[place]+".json",JsonUtility.ToJson(track,true));
        }

        private static void FinishArch(Transform root, Vector3[] points, int place)
        {
            var t = Tangent(points,0); var r = Right(t); var ink = Mat("CircuitNavy", "244756"); var white = Mat("CircuitIvory", "FFF2D6");
            foreach (int side in new[] { -1,1 }) Box(root, "FinishPost", points[0]+r*side*9.9f+Vector3.up*3, new Vector3(.45f,6,.45f), ink, true);
            var top = Box(root,"FinishGantry",points[0]+Vector3.up*5.7f,new Vector3(20.2f,.9f,.55f), place==1 ? Mat("ForestBanner","D4B254") : Mat("CircuitCoral","CF5D45"),false);
            top.transform.rotation=Quaternion.LookRotation(t);
            for (int x=0; x<14; x++) for(int z=0;z<2;z++)
            {
                var tile = Box(root,"FinishChecker",points[0]+r*(x-6.5f)+t*(z-.5f)+Vector3.up*.065f,new Vector3(1,.015f,1),(x+z)%2==0?ink:white,false);
                tile.transform.rotation=Quaternion.LookRotation(t);
            }
        }

        private static void Dress(Transform root, Vector3[] points, int place)
        {
            float origin=place*450;
            if(place==0)
            {
                Box(root,"CoastalSea",new Vector3(origin,-.7f,0),new Vector3(405,.1f,360),Mat("CircuitWater","FFFFFF","FamilyRacing/Trial Coastal Water"),false);
                for(int i=0;i<5;i++) Model(root,"KenneySuburban",i%2==0?"building-type-a":"building-type-d",new Vector3(origin-20+i*17,0,3+(i%2)*17),6.5f,i*27);
                Model(root,"KenneyWatercraft","boat-sail-a",new Vector3(origin-115,-.3f,33),8,25);
                Model(root,"KenneyWatercraft","boat-fishing-small",new Vector3(origin+126,-.2f,-20),6,-15);
                Box(root,"HarborPier",new Vector3(origin-120,-.1f,27),new Vector3(30,.3f,6),Mat("PierWood","987450"),false);
                // Expose the water around a smaller island footprint.
                root.Find("RoughGround").localScale=new Vector3(235,.28f,205);
                for(int i=0;i<12;i++) Model(root,"Quaternius",i%2==0?"CommonTree_1":"CommonTree_3",new Vector3(origin+Mathf.Cos(i*2.4f)*34,0,Mathf.Sin(i*2.4f)*26),5+i%3,i*41);
            }
            var bark=Mat("PineBark","705244"); var pine=Mat("PineNeedles","285345"); var pineLight=Mat("PineTips","49735B"); var rock=Mat("CanyonRock","B66C44");
            for(int x=-140;x<=140;x+=18) for(int z=-120;z<=120;z+=18)
            {
                float noise=Mathf.Sin(x*7.17f+z*3.91f+place*12.4f);
                var p=new Vector3(origin+x+noise*4,0,z+Mathf.Cos(x+z)*4);
                if(Distance(points,p)<22 || (place==0 && (Mathf.Abs(x)>105 || Mathf.Abs(z)>90))) continue;
                if(place==1)
                {
                    var tree=Node(root,"Pine"); tree.position=p; float h=6+(noise+1)*2;
                    Box(tree,"Trunk",Vector3.up*h*.27f,new Vector3(.48f,h*.55f,.48f),bark,false);
                    for(int j=0;j<3;j++) Cone(tree,"PineCrown",new Vector3(0,h*(.38f+j*.17f),0),2.7f-j*.5f,h*.47f,j%2==0?pine:pineLight);
                    if((x+140)%36==0) Model(root,"Quaternius","Rock_Medium_1",p+new Vector3(5,0,3),1.8f,x+z);
                }
                else if(place==2)
                {
                    if(noise<-.15f) continue;
                    var model=Model(root,"Quaternius",(x+140)%36==0?"Rock_Medium_1":"Rock_Medium_2",p,6+(noise+1)*6,x+z);
                    foreach(var renderer in model.GetComponentsInChildren<Renderer>()) renderer.sharedMaterials=renderer.sharedMaterials.Select(_=>rock).ToArray();
                    if(noise>.65f) Cactus(root,p+new Vector3(6,0,-4));
                }
                else if(noise>.2f) Model(root,"Quaternius","Rock_Medium_2",p,2.5f,x+z);
            }
            // Turn chevrons make the racing direction clear from the chase camera.
            for(int i=8;i<points.Length;i+=12)
            {
                var t=Tangent(points,i); var p=points[i]+Right(t)*12.4f;
                Box(root,"ChevronPost",p+Vector3.up, new Vector3(.15f,2,.15f),bark,false);
                var sign=Box(root,"TurnSign",p+Vector3.up*2,new Vector3(1.8f,1.1f,.12f),Mat("SignYellow","F4D568"),false);
                sign.transform.rotation=Quaternion.LookRotation(-t);
                var stripe=Box(sign.transform,"DirectionMark",new Vector3(0,0,-.075f),new Vector3(.18f,.75f,.03f),Mat("CircuitNavy","244756"),false);
                stripe.transform.localRotation=Quaternion.Euler(0,0,-35);
            }
        }

        private static void Cactus(Transform root,Vector3 p)
        {
            var green=Mat("CactusGreen","567D53");
            var t=Node(root,"Cactus");t.position=p;
            Box(t,"CactusStem",Vector3.up*1.5f,new Vector3(.5f,3,.5f),green,false);
            Box(t,"CactusArm",new Vector3(.6f,1.5f,0),new Vector3(1.2f,.4f,.4f),green,false);
            Box(t,"CactusTip",new Vector3(1,2,0),new Vector3(.4f,1.4f,.4f),green,false);
        }

        private static GameObject Model(Transform parent,string pack,string name,Vector3 p,float height,float yaw)
        {
            var go=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(AssetTrialAuthoring.Root+"/"+pack+"/"+name+".fbx"));
            go.name=name; go.transform.rotation=Quaternion.Euler(0,yaw,0)*go.transform.rotation;
            var bounds=Bounds(go); go.transform.localScale*=height/Mathf.Max(.01f,bounds.size.y);
            bounds=Bounds(go);
            float footprint=Mathf.Max(bounds.size.x,bounds.size.z);
            if(footprint>14){go.transform.localScale*=14/footprint;bounds=Bounds(go);}
            go.transform.position+=p-new Vector3(bounds.center.x,bounds.min.y,bounds.center.z);go.transform.SetParent(parent,true);
            if(pack=="Quaternius") AssetTrialAuthoring.ConvertMaterials(go,false);
            else
            {
                var m=Mat(pack+"CircuitPalette","FFFFFF"); m.SetTexture("_BaseMap",AssetDatabase.LoadAssetAtPath<Texture2D>(AssetTrialAuthoring.Root+"/"+pack+(pack=="KenneySuburban"?"/variation-c.png":"/colormap.png")));
                foreach(var r in go.GetComponentsInChildren<Renderer>()) r.sharedMaterials=r.sharedMaterials.Select(_=>m).ToArray();
            }
            foreach(var c in go.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
            foreach(var t in go.GetComponentsInChildren<Transform>())t.gameObject.layer=2;
            return go;
        }

        private static Bounds Bounds(GameObject go){var rs=go.GetComponentsInChildren<Renderer>();var b=rs[0].bounds;foreach(var r in rs.Skip(1))b.Encapsulate(r.bounds);return b;}
        private static Transform Node(Transform p,string n){var t=new GameObject(n).transform;t.SetParent(p,false);return t;}
        private static GameObject Box(Transform p,string n,Vector3 pos,Vector3 size,Material mat,bool collision)
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=n;go.transform.SetParent(p,false);go.transform.localPosition=pos;go.transform.localScale=size;
            go.GetComponent<Renderer>().sharedMaterial=mat;if(!collision)Object.DestroyImmediate(go.GetComponent<Collider>());return go;
        }
        private static Material Mat(string n,string hex,string shader="Universal Render Pipeline/Simple Lit")
        {
            string path=Dir+"/"+n+".mat";var m=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(m==null){m=new Material(Shader.Find(shader)){name=n};AssetDatabase.CreateAsset(m,path);}
            ColorUtility.TryParseHtmlString("#"+hex,out var c);if(m.HasProperty("_BaseColor"))m.SetColor("_BaseColor",c);
            if(m.HasProperty("_Smoothness"))m.SetFloat("_Smoothness",.12f);m.enableInstancing=true;EditorUtility.SetDirty(m);return m;
        }
        private static T Save<T>(T value,string path)where T:Object
        {var old=AssetDatabase.LoadAssetAtPath<T>(path);if(old==null){AssetDatabase.CreateAsset(value,path);return value;}EditorUtility.CopySerialized(value,old);Object.DestroyImmediate(value);EditorUtility.SetDirty(old);return old;}

        private static void Band(Transform root,string name,Vector3[] p,float a,float b,float lift,Material[] mats,bool collision)
        {
            var v=new List<Vector3>();var uv=new List<Vector2>();var tris=mats.Select(_=>new List<int>()).ToArray();float distance=0;
            for(int i=0;i<=p.Length;i++)
            {
                int j=i%p.Length;var r=Right(Tangent(p,j));
                v.Add(p[j]+r*a+Vector3.up*lift);v.Add(p[j]+r*b+Vector3.up*lift);
                uv.Add(new Vector2(a/4,distance/4));uv.Add(new Vector2(b/4,distance/4));
                if(i<p.Length){int n=i*2;tris[i%mats.Length].AddRange(new[]{n,n+2,n+1,n+1,n+2,n+3});distance+=Vector3.Distance(p[j],p[(j+1)%p.Length]);}
            }
            var mesh=new Mesh{name=root.name+name,subMeshCount=mats.Length};mesh.SetVertices(v);mesh.SetUVs(0,uv);
            for(int i=0;i<mats.Length;i++)mesh.SetTriangles(tris[i],i);mesh.RecalculateNormals();mesh.RecalculateBounds();
            var go=Node(root,name).gameObject;go.AddComponent<MeshFilter>().sharedMesh=Save(mesh,Dir+"/"+root.name+name+".asset");
            go.AddComponent<MeshRenderer>().sharedMaterials=mats;if(collision)go.AddComponent<MeshCollider>().sharedMesh=go.GetComponent<MeshFilter>().sharedMesh;
        }
        private static void Cone(Transform root,string name,Vector3 p,float radius,float height,Material mat)
        {
            const int sides=9;var v=new List<Vector3>();var tris=new List<int>();
            for(int i=0;i<sides;i++){float a=i*Mathf.PI*2/sides,b=(i+1)*Mathf.PI*2/sides;int n=v.Count;
                v.Add(new Vector3(Mathf.Sin(a)*radius,0,Mathf.Cos(a)*radius));v.Add(Vector3.up*height);v.Add(new Vector3(Mathf.Sin(b)*radius,0,Mathf.Cos(b)*radius));tris.AddRange(new[]{n,n+1,n+2});}
            var mesh=new Mesh{name="PineCone"};mesh.SetVertices(v);mesh.SetTriangles(tris,0);mesh.RecalculateNormals();mesh.RecalculateBounds();
            // Only three canonical crown sizes are generated and shared across trees.
            string key="PineCone-"+radius.ToString("F1",System.Globalization.CultureInfo.InvariantCulture);
            var existing=AssetDatabase.LoadAssetAtPath<Mesh>(Dir+"/"+key+".asset");
            if(existing==null)existing=Save(mesh,Dir+"/"+key+".asset");else Object.DestroyImmediate(mesh);
            var go=Node(root,name);go.localPosition=p;go.gameObject.AddComponent<MeshFilter>().sharedMesh=existing;go.gameObject.AddComponent<MeshRenderer>().sharedMaterial=mat;
        }
        private static float Distance(Vector3[] p,Vector3 point)
        {float best=float.MaxValue;for(int i=0;i<p.Length;i++){var a=p[i];var d=p[(i+1)%p.Length]-a;float t=Mathf.Clamp01(Vector3.Dot(point-a,d)/d.sqrMagnitude);best=Mathf.Min(best,Vector3.Distance(point,a+d*t));}return best;}
        private static void BatchVisuals(Transform root,string name)
        {
            foreach(var g in root.GetComponentsInChildren<MeshRenderer>().GroupBy(r=>r.sharedMaterial).ToArray())
            {
                var mesh=new Mesh{name=name+g.Key.name};mesh.CombineMeshes(g.Select(r=>new CombineInstance{mesh=r.GetComponent<MeshFilter>().sharedMesh,transform=root.worldToLocalMatrix*r.transform.localToWorldMatrix}).ToArray());
                var go=Node(root,"BatchedRail");go.gameObject.AddComponent<MeshFilter>().sharedMesh=Save(mesh,Dir+"/"+mesh.name+".asset");go.gameObject.AddComponent<MeshRenderer>().sharedMaterial=g.Key;
                foreach(var r in g)Object.DestroyImmediate(r);
            }
        }

        public static void Bind(SerializedObject world)
        {
            var tracks=world.FindProperty("tracks");tracks.arraySize=3;
            for(int i=0;i<3;i++)tracks.GetArrayElementAtIndex(i).objectReferenceValue=AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(TrackPath(i));
            world.FindProperty("track").objectReferenceValue=tracks.GetArrayElementAtIndex(0).objectReferenceValue;
            // The old benchmark tutorial's landmark drills belonged to the
            // removed open map. Do not offer incompatible training here.
            world.FindProperty("tutorial").objectReferenceValue=null;
            // Existing practice identities keep working, now on the coastal circuit.
            var events=world.FindProperty("events");var track=(TrackDefinitionAsset)tracks.GetArrayElementAtIndex(0).objectReferenceValue;
            for(int i=0;i<events.arraySize;i++)
            {
                var source=events.GetArrayElementAtIndex(i).objectReferenceValue as EventDefinitionAsset;if(source==null)continue;
                var copy=Object.Instantiate(source);copy.track=track;
                events.GetArrayElementAtIndex(i).objectReferenceValue=Save(copy,Dir+"/"+source.eventId+".asset");
            }
        }

        public static void Validate()
        {
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            for(int i=0;i<3;i++)
            {
                var asset=AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(TrackPath(i));var p=Sample(i);
                if(asset==null || asset.ToCourse(out var error)==null)throw new InvalidOperationException("Invalid circuit "+Ids[i]);
                if(asset.shortcutLanes.Length!=0)throw new InvalidOperationException("Shortcut found on "+Ids[i]);
                var place=prefab.transform.Find(Ids[i]);
                if(place.GetComponentsInChildren<BoxCollider>().Count(c=>c.name.StartsWith("InnerRail_")||c.name.StartsWith("OuterRail_"))!=p.Length*2)
                    throw new InvalidOperationException("Broken barrier loop on "+Ids[i]);
                if(place.Find("PavedRoad").GetComponent<MeshCollider>()==null||place.Find("RoughGround").GetComponent<DrivingSurface>()==null)
                    throw new InvalidOperationException("Circuit surface collision missing");
                foreach(var gate in asset.gates)if(Distance(p,gate.center)>.1f)throw new InvalidOperationException("Gate off road");
                foreach(var slot in asset.spawnSlots)
                foreach(float x in new[]{-1f,1f}) foreach(float z in new[]{-1.7f,1.7f})
                    if(Distance(p,slot.position+Quaternion.Euler(0,slot.headingDegrees,0)*new Vector3(x,0,z))>RoadHalf-.3f)
                        throw new InvalidOperationException("Grid body outside paved road on "+Ids[i]);
                Debug.Log("CIRCUIT "+Ids[i]+": closed road, "+p.Length*2+" barrier sections, "+asset.gates.Length+" gates, no shortcuts.");
            }
        }

        public static void Preview()
        {
            var scene=EditorSceneManager.OpenScene(AssetTrialAuthoring.ScenePath);
            foreach(var go in scene.GetRootGameObjects())go.SetActive(false);
            var world=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            var sun=new GameObject("CircuitPreviewSun").AddComponent<Light>();sun.type=LightType.Directional;sun.intensity=1.05f;sun.shadows=LightShadows.Soft;
            sun.transform.rotation=Quaternion.Euler(48,-32,0);
            RenderSettings.ambientMode=AmbientMode.Trilight;RenderSettings.ambientSkyColor=new Color(.45f,.53f,.61f);
            RenderSettings.ambientEquatorColor=new Color(.4f,.41f,.38f);RenderSettings.ambientGroundColor=new Color(.22f,.25f,.22f);
            var camera=new GameObject("CircuitPreviewCamera").AddComponent<Camera>();camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            camera.clearFlags=CameraClearFlags.Skybox;camera.fieldOfView=47;camera.aspect=1600f/1000;camera.farClipPlane=650;
            bool fog=RenderSettings.fog;RenderSettings.fog=false;
            for(int i=0;i<3;i++)
            {
                foreach(Transform child in world.transform)child.gameObject.SetActive(child.name==Ids[i]);
                var target=new Vector3(i*450,0,0);camera.transform.position=target+new Vector3(-138,176,-170);camera.transform.LookAt(target);
                AssetTrialRacers.Capture(camera,Ids[i]+"-overview.png",1600,1000,"docs/qa/asset-trial-2026-10-23/closed-circuits");
            }
            RenderSettings.fog=fog;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        }
    }
}
