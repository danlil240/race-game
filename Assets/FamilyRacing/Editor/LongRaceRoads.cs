using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace FamilyRacing.EditorTools
{
    // A single sampled line supplies road, collision, AI, gates and safe poses.
    public static class LongRaceRoads
    {
        public const string Dir = LongRaceAuthoring.Output + "/Courses";
        public const string PrefabPath = Dir + "/GrandTourWorld.prefab";
        public static readonly string[] Ids = { "coastal-grand-tour-v1", "pine-lake-journey-v1", "canyon-expedition-v1" };
        public static readonly string[] Names = { "Coastal Grand Tour", "Pine Lake Journey", "Canyon Expedition" };
        public static readonly string[] HebrewNames = { "מסע החוף הגדול", "מסע אגם האורנים", "הרפתקת הקניון" };
        public static readonly float[] Lengths = { 7000f, 14000f, 15000f };
        public const float RoadHalf = 9f, RailOffset = 14f;
        public static string TrackPath(int place) => Dir + "/" + Ids[place] + ".asset";
        private static readonly Vector2[][] Controls = {
            new[] { new Vector2(0,0),new Vector2(0,320),new Vector2(140,520),new Vector2(430,540),new Vector2(700,660),new Vector2(970,480),new Vector2(920,230),new Vector2(1100,-60),new Vector2(820,-220),new Vector2(500,-160),new Vector2(260,-370),new Vector2(-80,-260) },
            new[] { new Vector2(0,0),new Vector2(0,250),new Vector2(160,620),new Vector2(-60,1000),new Vector2(280,1380),new Vector2(330,1820),new Vector2(10,2180),new Vector2(-260,2580),new Vector2(-100,3000),new Vector2(260,3430),new Vector2(540,3890),new Vector2(320,4340),new Vector2(0,4720),new Vector2(100,5160),new Vector2(460,5570),new Vector2(290,6030),new Vector2(320,6500) },
            new[] { new Vector2(0,0),new Vector2(0,350),new Vector2(-200,780),new Vector2(-120,1150),new Vector2(340,1600),new Vector2(600,1960),new Vector2(260,2370),new Vector2(-100,2780),new Vector2(-480,3200),new Vector2(-320,3570),new Vector2(160,4000),new Vector2(520,4450),new Vector2(420,4880),new Vector2(-40,5330),new Vector2(90,5790),new Vector2(480,6200),new Vector2(400,6700) }
        };

        public static Vector3[] Sample(int place)
        {
            bool open = place != 0; var controls = Controls[place]; var dense = new List<Vector3>();
            int segments = open ? controls.Length - 1 : controls.Length;
            for (int i = 0; i < segments; i++)
            {
                var b = controls[i]; var c = controls[(i + 1) % controls.Length];
                var a = open && i == 0 ? b * 2 - c : controls[(i - 1 + controls.Length) % controls.Length];
                var d = open && i == controls.Length - 2 ? c * 2 - b : controls[(i + 2) % controls.Length];
                for (int j = 0; j < 60; j++)
                {
                    float t = j / 60f;
                    var p = .5f * (2*b + (-a+c)*t + (2*a-5*b+4*c-d)*t*t + (-a+3*b-3*c+d)*t*t*t);
                    dense.Add(new Vector3(p.x, 0, p.y));
                }
            }
            dense.Add(open ? new Vector3(controls.Last().x, 0, controls.Last().y) : dense[0]);
            float raw = 0; for (int i = 1; i < dense.Count; i++) raw += Vector3.Distance(dense[i - 1], dense[i]);
            float scale = Lengths[place] / raw;
            for (int i = 0; i < dense.Count; i++) dense[i] *= scale;
            var sampled = new List<Vector3> { dense[0] }; float remainder = 8f;
            for (int i = 1; i < dense.Count; i++)
            {
                var from = dense[i - 1]; var to = dense[i]; float length = Vector3.Distance(from, to);
                while (length >= remainder)
                {
                    from = Vector3.MoveTowards(from, to, remainder); sampled.Add(from);
                    length = Vector3.Distance(from, to); remainder = 8f;
                }
                remainder -= length;
            }
            if (open) sampled.Add(dense.Last());
            else if (Vector3.Distance(sampled.Last(), sampled[0]) < 3f) sampled.RemoveAt(sampled.Count - 1);
            for (int i = 0; i < sampled.Count; i++)
            {
                var p = sampled[i];
                // Gentle gradients expose the horizon without introducing jump ramps.
                p.y = .12f + (place == 0 ? 2.5f * Mathf.Sin(i * Mathf.PI * 4f / sampled.Count) :
                    7f * Mathf.Sin(i * 8f / 730f) + 2.5f * Mathf.Sin(i * 8f / 260f));
                p.x += place * 15000f; sampled[i] = p;
            }
            return sampled.ToArray();
        }
        public static Vector3 Tangent(Vector3[] p, int i, bool open)
            => (p[open ? Mathf.Min(i + 1, p.Length - 1) : (i + 1) % p.Length]
                - p[open ? Mathf.Max(0, i - 1) : (i - 1 + p.Length) % p.Length]).normalized;
        public static Vector3 Right(Vector3 t) => new Vector3(t.z, 0, -t.x).normalized;
        public static Transform Node(Transform parent, string name)
        { var node = new GameObject(name).transform; node.SetParent(parent, false); return node; }
        public static T Save<T>(T value, string path) where T : Object
        {
            var old = AssetDatabase.LoadAssetAtPath<T>(path);
            if (old == null) { AssetDatabase.CreateAsset(value, path); return value; }
            EditorUtility.CopySerialized(value, old); Object.DestroyImmediate(value); EditorUtility.SetDirty(old); return old;
        }
        public static Material Mat(string name, string hex, string shader = "Universal Render Pipeline/Simple Lit")
        {
            string path = Dir + "/" + name + ".mat"; var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(Shader.Find(shader)) { name = name }; AssetDatabase.CreateAsset(mat, path); }
            ColorUtility.TryParseHtmlString("#" + hex, out var c);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", .16f);
            mat.enableInstancing = true; EditorUtility.SetDirty(mat); return mat;
        }
        public static GameObject Box(Transform parent, string name, Vector3 center, Vector3 scale, Material mat, bool solid = false)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name; go.transform.SetParent(parent, false);
            go.transform.position = center; go.transform.localScale = scale; go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!solid) Object.DestroyImmediate(go.GetComponent<Collider>()); return go;
        }
        public static void MeshObject(Transform parent, string name, List<Vector3> v, List<int> tris, List<Vector2> uv, Material mat, bool solid)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(v); mesh.SetTriangles(tris, 0); if (uv != null) mesh.SetUVs(0, uv);
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); mesh = Save(mesh, Dir + "/" + name + ".asset");
            var go = Node(parent, name).gameObject;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            if (solid) go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        public static GameObject Build()
        {
            Directory.CreateDirectory(Dir); AssetDatabase.Refresh();
            var root = new GameObject("GrandTourWorld"); root.AddComponent<LongRaceVisibility>();
            try
            {
                for (int place = 0; place < 3; place++) BuildPlace(root.transform, place);
                return PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally { Object.DestroyImmediate(root); }
        }
        private static void BuildPlace(Transform world, int place)
        {
            var root = Node(world, Ids[place]); var p = Sample(place); bool open = place > 0;
            var road = Mat("TourAsphalt", "A6ADB3");
            road.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(AssetTrialAuthoring.Output + "/Materials/Texture_road.asset"));
            var ivory = Mat("WarmRoadPaint", "FFF4D2"); var verge = Mat("Verge" + place, place == 0 ? "C9B995" : place == 1 ? "8A9580" : "C69B74");
            var soil = Mat("Landscape" + place, place == 0 ? "CDBD94" : place == 1 ? "688754" : "BD8058");
            var groundTex = GroundTexture(place); soil.SetTexture("_BaseMap", groundTex);
            if (place == 0)
                Box(root,"CoastalOcean",new Vector3(500,-3.8f,150),new Vector3(6500,.06f,6500),Mat("TourOcean","FFFFFF","FamilyRacing/Trial Coastal Water"));
            Band(root, p, open, "Road", -RoadHalf, RoadHalf, .04f, road, true);
            Band(root, p, open, "Rough", -RailOffset - 2, RailOffset + 2, -.08f, verge, true, true);
            Band(root, p, open, "EdgeLeft", -8.8f, -8.55f, .065f, ivory, false);
            Band(root, p, open, "EdgeRight", 8.55f, 8.8f, .065f, ivory, false);
            var decorations = Node(root, "Scenery");
            int segmentCount = open ? p.Length - 1 : p.Length;
            for (int start = 0; start < segmentCount; start += 20)
            {
                var chunk = Node(decorations, "Chunk_" + start); int end = Mathf.Min(start + 20, segmentCount);
                chunk.position = p[(start + (end - start) / 2) % p.Length];
                TerrainChunk(chunk, p, open, start, end, place, soil);
                for (int i = start; i < end; i++)
                {
                    int j = (i + 1) % p.Length; var t = Tangent(p, i, open); var r = Right(t);
                    if (i % 5 < 3)
                    {
                        var dash = Box(chunk, "CenterDash", p[i] + Vector3.up * .07f, new Vector3(.18f, .015f, 5.5f), ivory);
                        dash.transform.rotation = Quaternion.LookRotation(t);
                    }
                    foreach (int side in new[] { -1, 1 })
                    {
                        var a = p[i] + r * RailOffset * side;
                        var b = p[j] + Right(Tangent(p, j, open)) * RailOffset * side;
                        // Invisible continuous solid wall supports a slim, readable guardrail.
                        var wall = Box(root, "SafetyRail", (a+b)*.5f + Vector3.up*.62f,
                            new Vector3(.35f, 1.35f, Vector3.Distance(a,b)+.2f), ivory, true);
                        wall.transform.rotation = Quaternion.LookRotation(b-a);
                        Object.DestroyImmediate(wall.GetComponent<Renderer>());
                        var rail = Box(chunk, "SteelRail", (a+b)*.5f + Vector3.up*.85f,
                            new Vector3(.22f,.38f,Vector3.Distance(a,b)+.1f), Mat("RailSilver", "C8D4D3"));
                        rail.transform.rotation = Quaternion.LookRotation(b-a);
                        if (i % 2 == 0) Box(chunk, "RailPost", a + Vector3.up*.5f, new Vector3(.18f,1.1f,.18f), Mat("RailPosts", "56666B"));
                    }
                }
                LongRaceScenery.DressChunk(chunk, p, open, start, end, place);
                Batch(chunk, Ids[place] + "_" + start);
            }
            int startIndex = open ? 8 : 0, finishIndex = open ? p.Length - 9 : 0;
            Arch(root, p, startIndex, open, false); if (open) Arch(root, p, finishIndex, true, true);
            AuthorTrack(p, place, startIndex, finishIndex);
        }
        private static Texture2D GroundTexture(int place)
        {
            var path = Dir + "/GroundNoise" + place + ".asset";
            var tex = new Texture2D(128,128,TextureFormat.RGB24,true) { name = "GroundNoise" + place, wrapMode = TextureWrapMode.Repeat };
            for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
            { float n = .94f + .07f*Mathf.PerlinNoise(x*.14f,y*.14f) + .012f*Mathf.Sin(x*2.7f+y*1.6f); tex.SetPixel(x,y,new Color(n,n,n)); }
            tex.Apply(); return Save(tex, path);
        }
        private static void Band(Transform root, Vector3[] p, bool open, string name, float left, float right, float lift, Material mat, bool solid, bool rough = false)
        {
            int count = open ? p.Length - 1 : p.Length;
            for (int start = 0; start < count; start += 96)
            {
                int end = Mathf.Min(start + 96, count); var v = new List<Vector3>(); var uv = new List<Vector2>(); var tris = new List<int>();
                for (int i = start; i <= end; i++)
                {
                    int j = i % p.Length; var r = Right(Tangent(p,j,open));
                    v.Add(p[j]+r*left+Vector3.up*lift); v.Add(p[j]+r*right+Vector3.up*lift);
                    uv.Add(new Vector2(left/4f,i*2f)); uv.Add(new Vector2(right/4f,i*2f));
                    if (i < end) { int n = (i-start)*2; tris.AddRange(new[] {n,n+2,n+1,n+1,n+2,n+3}); }
                }
                string key = root.name + "_" + name + start;
                MeshObject(root,key,v,tris,uv,mat,solid);
                if (rough) root.Find(key).gameObject.AddComponent<DrivingSurface>();
            }
        }
        private static void TerrainChunk(Transform root, Vector3[] p, bool open, int start, int end, int place, Material mat)
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tris = new List<int>();
            var offsets = new[] {-160f,-115f,-70f,-32f,-15f,15f,32f,70f,115f,160f};
            for (int i=start;i<=end;i+=2)
            {
                int j=i%p.Length; var right=Right(Tangent(p,j,open));
                for(int k=0;k<offsets.Length;k++)
                {
                    float side=offsets[k]; float away=Mathf.Max(0,Mathf.Abs(side)-18f);
                    float noise=Mathf.PerlinNoise((p[j].x+side)*.006f,p[j].z*.006f);
                    float height=place==0 ? -away*.035f + noise*away*.025f : away*(place==1?.13f:.23f)*noise;
                    if(place==1&&side>65&&i>100&&(i%160<=30||i%160>=150))height=-3f;
                    var point=p[j]+right*side+Vector3.up*(height-.18f);
                    v.Add(root.InverseTransformPoint(point)); uv.Add(new Vector2(point.x/18,point.z/18));
                }
            }
            int rows=v.Count/offsets.Length;
            for(int i=0;i<rows-1;i++) for(int k=0;k<offsets.Length-1;k++)
            {int n=i*offsets.Length+k;tris.AddRange(new[]{n,n+offsets.Length,n+1,n+1,n+offsets.Length,n+offsets.Length+1});}
            MeshObject(root,root.parent.parent.name+"_Terrain"+start,v,tris,uv,mat,false);
        }
        public static void Batch(Transform parent, string key)
        {
            var renderers=parent.GetComponentsInChildren<MeshRenderer>().Where(r=>r.GetComponent<MeshFilter>()!=null).ToArray();
            var instancedRenderers=new HashSet<MeshRenderer>();
            var groups=new Dictionary<(Mesh mesh,int subMesh,Material material),List<Matrix4x4>>();
            foreach(var r in renderers)
            {
                var mesh=r.GetComponent<MeshFilter>().sharedMesh; if(mesh==null)continue;
                // Unique terrain and mesa meshes already have tight bounds.
                if(mesh.name.Contains("Terrain")||mesh.name.Contains("Mesa"))continue;
                // Original water shaders have no instancing variant/instance-ID
                // transform setup. Preserve their normal renderer and transform.
                if(r.sharedMaterials.Any(mat=>mat.shader.name.StartsWith("FamilyRacing/",StringComparison.Ordinal)))continue;
                instancedRenderers.Add(r);
                for(int m=0;m<mesh.subMeshCount;m++)
                {
                    var mat=r.sharedMaterials[Mathf.Min(m,r.sharedMaterials.Length-1)];
                    var groupKey=(mesh,m,mat);mat.enableInstancing=true;EditorUtility.SetDirty(mat);
                    if(!groups.TryGetValue(groupKey,out var list))groups[groupKey]=list=new List<Matrix4x4>();
                    list.Add(parent.worldToLocalMatrix*r.transform.localToWorldMatrix);
                }
            }
            var instances=parent.gameObject.AddComponent<TourSceneryInstances>();
            instances.groups=groups.SelectMany(group=>Enumerable.Range(0,(group.Value.Count+1022)/1023).Select(part=>new TourSceneryInstances.Group
            {
                mesh=group.Key.mesh,subMesh=group.Key.subMesh,material=group.Key.material,localMatrices=group.Value.Skip(part*1023).Take(1023).ToArray()
            })).ToArray();
            // Imported FBXs can carry renderers on both a parent and child.
            // Destroying the parent already removes the child's renderer.
            foreach(var r in instancedRenderers)if(r!=null)Object.DestroyImmediate(r.gameObject);
        }
        private static TrackDefinitionAsset.SpawnSlot Pose(Vector3[] p,int index,bool open,float behind,float lane=0)
        {
            var t=Tangent(p,index,open);return new TrackDefinitionAsset.SpawnSlot {
                position=p[index]-t*behind+Right(t)*lane+Vector3.up*.07f,
                headingDegrees=Mathf.Atan2(t.x,t.z)*Mathf.Rad2Deg};
        }
        private static void AuthorTrack(Vector3[] p,int place,int start,int finish)
        {
            bool open=place>0;var track=ScriptableObject.CreateInstance<TrackDefinitionAsset>();
            track.courseId=Ids[place];track.nameEn=Names[place];track.nameHe=HebrewNames[place];track.isOpenRoute=open;
            var spawn=Pose(p,start,open,15);track.spawnPosition=spawn.position;track.spawnHeadingDegrees=spawn.headingDegrees;
            track.spawnSlots=Enumerable.Range(0,4).Select(i=>Pose(p,start,open,15+i/2*7,i%2==0?-2.5f:2.5f)).ToArray();
            track.aiRoute=p.Select(point=>new TrackDefinitionAsset.RoutePoint{x=point.x,z=point.z,radius=4.5f}).ToArray();
            var indexes=new List<int>{start};
            // Roads have continuous barriers; 320 m gate intervals preserve
            // ordered progress while keeping four-racer snapshots under the
            // established 32 KB LAN packet ceiling, including pickup state.
            for(int i=start+40;i<(open?finish:p.Length);i+=40)indexes.Add(i);
            if(open&&indexes.Last()!=finish)indexes.Add(finish);
            track.gates=indexes.Select((i,n)=>{var t=Tangent(p,i,open);var safe=Pose(p,i,open,5);return new TrackDefinitionAsset.Gate {
                id=n==0?"start":open&&i==finish?"finish":"gate-"+n,center=p[i],halfWidth=12.5f,
                direction=new Vector2(t.x,t.z),recoverPosition=safe.position,recoverHeadingDegrees=safe.headingDegrees};}).ToArray();
            track.pickupSites=Enumerable.Range(0,10).SelectMany(n=>new[]{-5f,0f,5f}.Select((lane,k)=> {
                int i=25+Mathf.FloorToInt(n*(p.Length-55)/10f);return new TrackDefinitionAsset.PickupSite { id=Ids[place]+"-"+n+"-"+k,
                    center=p[i]+Right(Tangent(p,i,open))*lane,radiusMeters=2.5f,verticalRangeMeters=3f};})).ToArray();
            var bounds=new Bounds(p[0],Vector3.zero);foreach(var point in p)bounds.Encapsulate(point);bounds.Expand(new Vector3(80,40,80));
            track.recoveryBounds=new[]{new TrackDefinitionAsset.BoundsVolume{min=bounds.min,max=bounds.max}};
            track=Save(track,TrackPath(place));
            File.WriteAllText(Dir+"/"+Ids[place]+".json",JsonUtility.ToJson(track,true));
            Debug.Log("LONG_ROUTE "+Ids[place]+" points="+p.Length+" gates="+track.gates.Length+" targetMeters="+Lengths[place]);
        }
        private static void Arch(Transform root,Vector3[] p,int i,bool open,bool finish)
        {
            var t=Tangent(p,i,open);var r=Right(t);var ink=Mat("TourNavy","254857");var cream=Mat("TourCream","FFF1CF");
            foreach(int side in new[]{-1,1})Box(root,"GantryPost",p[i]+r*side*12+Vector3.up*3.5f,new Vector3(.6f,7,.6f),ink);
            var beam=Box(root,"Gantry",p[i]+Vector3.up*6.5f,new Vector3(25,.9f,1),Mat(finish?"FinishOrange":"StartTeal",finish?"E47A43":"4C9C91"));
            beam.transform.rotation=Quaternion.LookRotation(t);
            for(int x=0;x<18;x++)for(int z=0;z<3;z++)
            {var tile=Box(root,"Checker",p[i]+r*(x-8.5f)+t*(z-1)+Vector3.up*.075f,new Vector3(1,.02f,1),(x+z)%2==0?ink:cream);tile.transform.rotation=Quaternion.LookRotation(t);}
            var sign=Node(root,finish?"FinishTitle":"StartTitle");sign.position=p[i]+Vector3.up*6.5f-t*.55f;
            sign.rotation=Quaternion.LookRotation(t);var text=sign.gameObject.AddComponent<TextMesh>();
            text.text=finish?"FINISH":open?"GRAND JOURNEY":"COASTAL GRAND TOUR";text.anchor=TextAnchor.MiddleCenter;text.alignment=TextAlignment.Center;
            text.characterSize=.25f;text.fontSize=56;text.color=Color.white;
        }
    }
}
