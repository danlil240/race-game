using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FamilyRacing.EditorTools
{
    // Licensed pack models and original geometry, batched in local road chunks.
    public static class LongRaceScenery
    {
        private static float Noise(int i, int salt) => Mathf.Repeat(Mathf.Sin(i*12.9898f+salt*78.233f)*43758.5453f,1f);
        private static GameObject Model(Transform parent,string pack,string name,Vector3 p,float height,float yaw)
        {
            var source=AssetDatabase.LoadAssetAtPath<GameObject>(AssetTrialAuthoring.Root+"/"+pack+"/"+name+".fbx");
            if(source==null)throw new System.InvalidOperationException("Missing tour scenery: "+pack+"/"+name);
            var go=Object.Instantiate(source);go.name=name;go.transform.rotation=Quaternion.Euler(0,yaw,0)*go.transform.rotation;
            var rs=go.GetComponentsInChildren<Renderer>();var bounds=rs[0].bounds;foreach(var r in rs.Skip(1))bounds.Encapsulate(r.bounds);
            go.transform.localScale*=height/Mathf.Max(.01f,bounds.size.y);
            rs=go.GetComponentsInChildren<Renderer>();bounds=rs[0].bounds;foreach(var r in rs.Skip(1))bounds.Encapsulate(r.bounds);
            go.transform.position+=p-new Vector3(bounds.center.x,bounds.min.y,bounds.center.z);go.transform.SetParent(parent,true);
            if(pack=="Quaternius")AssetTrialAuthoring.ConvertMaterials(go,false);
            else
            {
                var mat=LongRaceRoads.Mat("TourPalette"+pack,"FFFFFF");
                mat.SetTexture("_BaseMap",AssetDatabase.LoadAssetAtPath<Texture2D>(AssetTrialAuthoring.Root+"/"+pack+(pack=="KenneySuburban"?"/variation-c.png":"/colormap.png")));
                foreach(var r in rs)r.sharedMaterials=r.sharedMaterials.Select(_=>mat).ToArray();
            }
            foreach(var c in go.GetComponentsInChildren<Collider>())Object.DestroyImmediate(c);
            foreach(var t in go.GetComponentsInChildren<Transform>())t.gameObject.layer=2;
            return go;
        }
        // CC0 Poly Haven scan (decimated OBJ) with its own PBR material.
        private static GameObject Ph(Transform parent,string id,Vector3 p,float height,float yaw)
        {
            var source=LongRaceAuthoring.Required<GameObject>(LongRaceRoads.PolyHaven+"/"+id+"/"+id+".obj");
            if(!Cache.TryGetValue(id,out var mat))
                Cache[id]=mat=LongRaceRoads.Pbr("PH_"+id,id,"FFFFFF",id.Contains("rock")||id.Contains("boulder")||id.Contains("cliff")?.22f:.3f);
            var go=Object.Instantiate(source);go.name=id;go.transform.rotation=Quaternion.Euler(0,yaw,0);
            var rs=go.GetComponentsInChildren<Renderer>();var bounds=rs[0].bounds;foreach(var r in rs.Skip(1))bounds.Encapsulate(r.bounds);
            go.transform.localScale*=height/Mathf.Max(.01f,bounds.size.y);
            rs=go.GetComponentsInChildren<Renderer>();bounds=rs[0].bounds;foreach(var r in rs.Skip(1))bounds.Encapsulate(r.bounds);
            // Sink rocks a little so they sit in the ground instead of on it.
            go.transform.position+=p-new Vector3(bounds.center.x,bounds.min.y+height*(id.Contains("rock")||id.Contains("boulder")?.12f:.02f),bounds.center.z);
            go.transform.SetParent(parent,true);
            foreach(var r in rs)r.sharedMaterials=r.sharedMaterials.Select(_=>(Material)mat).ToArray();
            foreach(var t in go.GetComponentsInChildren<Transform>())t.gameObject.layer=2;
            return go;
        }
        // Generated low-poly mesh (pine, grass tuft) authored 1 m tall at the origin.
        private static void Proc(Transform parent,string name,Mesh mesh,Material[] mats,Vector3 p,float height,float yaw,float width=1)
        {
            var go=new GameObject(name);go.layer=2;go.transform.SetParent(parent,false);
            go.transform.SetPositionAndRotation(p,Quaternion.Euler(0,yaw,0));go.transform.localScale=new Vector3(height*width,height,height*width);
            go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterials=mats;
        }
        private static readonly System.Collections.Generic.Dictionary<string,Object> Cache=new System.Collections.Generic.Dictionary<string,Object>();
        public static void ResetCache()=>Cache.Clear();
        private static Mesh Cached(string name,System.Func<Mesh> make)
        {
            if(Cache.TryGetValue(name,out var mesh))return (Mesh)mesh;
            var built=make();built.name=name;built.RecalculateBounds();
            var saved=LongRaceRoads.Save(built,LongRaceRoads.Dir+"/"+name+".asset");Cache[name]=saved;return saved;
        }
        // Faceted pine: hexagonal trunk and four jittered cone tiers (~90 triangles).
        private static Mesh Pine()=>Cached("TourPine",()=>
        {
            var v=new System.Collections.Generic.List<Vector3>();var bark=new System.Collections.Generic.List<int>();var needles=new System.Collections.Generic.List<int>();
            void Tri(System.Collections.Generic.List<int> list,Vector3 a,Vector3 b,Vector3 c){list.Add(v.Count);v.Add(a);list.Add(v.Count);v.Add(b);list.Add(v.Count);v.Add(c);}
            for(int k=0;k<6;k++)
            {
                float a0=k*Mathf.PI/3,a1=(k+1)*Mathf.PI/3;
                Vector3 b0=new Vector3(Mathf.Cos(a0),0,Mathf.Sin(a0))*.035f,b1=new Vector3(Mathf.Cos(a1),0,Mathf.Sin(a1))*.035f;
                Vector3 t0=b0*.7f+Vector3.up*.32f,t1=b1*.7f+Vector3.up*.32f;
                Tri(bark,b0,t0,b1);Tri(bark,b1,t0,t1);
            }
            for(int tier=0;tier<4;tier++)
            {
                float bottom=.16f+tier*.19f,top=bottom+.42f-tier*.04f,radius=.27f-tier*.05f;const int sides=9;
                var apex=Vector3.up*top+new Vector3(Noise(tier,3)-.5f,0,Noise(tier,4)-.5f)*.02f;
                for(int k=0;k<sides;k++)
                {
                    float a0=k*Mathf.PI*2/sides,a1=(k+1)*Mathf.PI*2/sides;
                    float r0=radius*(.86f+.28f*Noise(k+tier*sides,7)),r1=radius*(.86f+.28f*Noise((k+1)%sides+tier*sides,7));
                    var p0=new Vector3(Mathf.Cos(a0)*r0,bottom+.03f*Noise(k,tier),Mathf.Sin(a0)*r0);
                    var p1=new Vector3(Mathf.Cos(a1)*r1,bottom+.03f*Noise((k+1)%sides,tier),Mathf.Sin(a1)*r1);
                    Tri(needles,p0,apex,p1);Tri(needles,p1,Vector3.up*(bottom+.06f),p0);
                }
            }
            var mesh=new Mesh();mesh.SetVertices(v);mesh.subMeshCount=2;mesh.SetTriangles(bark,0);mesh.SetTriangles(needles,1);mesh.RecalculateNormals();return mesh;
        });
        // Tuft of bent blades; normals point up so it shades like the ground it grows from.
        private static Mesh Tuft()=>Cached("TourGrassTuft",()=>
        {
            var v=new System.Collections.Generic.List<Vector3>();var tris=new System.Collections.Generic.List<int>();
            for(int b=0;b<11;b++)
            {
                float angle=b*2.4f,lean=.15f+.35f*Noise(b,11),h=.55f+.45f*Noise(b,12);
                var dir=new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle));var side=new Vector3(-dir.z,0,dir.x)*.035f;
                var root=dir*.12f*Noise(b,13);int n=v.Count;
                v.Add(root-side);v.Add(root+side);
                v.Add(root+dir*lean*.45f*h+Vector3.up*h*.55f-side*.6f);v.Add(root+dir*lean*.45f*h+Vector3.up*h*.55f+side*.6f);
                v.Add(root+dir*lean*h+Vector3.up*h);
                tris.AddRange(new[]{n,n+2,n+1,n+1,n+2,n+3,n+2,n+4,n+3});
            }
            var mesh=new Mesh();mesh.SetVertices(v);mesh.SetTriangles(tris,0);mesh.SetNormals(v.Select(_=>Vector3.up).ToList());return mesh;
        });
        private static Material TwoSided(string name,string hex)
        {
            var mat=LongRaceRoads.Mat(name,hex);mat.SetFloat("_Cull",0);return mat;
        }
        private static void Grass(Transform chunk,Vector3 p,int place,float n)
        {
            var mat=TwoSided("TourGrass"+place+(n>.5f?"b":"a"),place==0?(n>.5f?"9DA35F":"B6B074"):place==1?(n>.5f?"55773D":"6E8A47"):(n>.5f?"B59A5C":"C9AE6E"));
            Proc(chunk,"GrassTuft",Tuft(),new[]{mat},p-Vector3.up*.05f,.8f+n*.8f,n*360,1.6f);
        }
        private static void PineTree(Transform chunk,Vector3 p,float height,float n)
        {
            var bark=LongRaceRoads.Mat("PineBarkTour","5B4434");
            var needles=LongRaceRoads.Mat(n>.66f?"PineNeedlesLight":n>.33f?"PineNeedles":"PineNeedlesDark",n>.66f?"4E7A4C":n>.33f?"36613F":"284C36");
            Proc(chunk,"Pine",Pine(),new[]{bark,needles},p-Vector3.up*.3f,height,n*720,.9f+.3f*Noise((int)(n*997),5));
        }
        public static void DressChunk(Transform chunk,Vector3[] p,bool open,int start,int end,int place)
        {
            if(place!=2&&start%100==0)
            {
                var right=LongRaceRoads.Right(LongRaceRoads.Tangent(p,start,open));
                foreach(int side in new[]{-1,1})
                {
                    var center=p[start]+right*side*(place==0?330:370);
                    float clearance=float.MaxValue;
                    for(int j=0;j<p.Length;j+=8)clearance=Mathf.Min(clearance,Vector2.Distance(new Vector2(center.x,center.z),new Vector2(p[j].x,p[j].z)));
                    if(clearance<235)continue;
                    var hill=GameObject.CreatePrimitive(PrimitiveType.Sphere);hill.name="DistantWoodedHill";
                    hill.transform.SetParent(chunk,false);hill.transform.position=center-Vector3.up*13;
                    hill.transform.localScale=new Vector3(420,place==0?110:175,440);
                    hill.GetComponent<Renderer>().sharedMaterial=LongRaceRoads.Mat("DistantHill"+place,place==0?"8D9B69":"637F5B");
                    Object.DestroyImmediate(hill.GetComponent<Collider>());
                }
            }
            for(int i=start;i<end;i+=4)
            {
                var t=LongRaceRoads.Tangent(p,i,open);var right=LongRaceRoads.Right(t);
                foreach(int side in new[]{-1,1})
                {
                    float n=Noise(i,side+place*3+5),m=Noise(i,side+place*3+41),q=Noise(i,side+place*3+77);
                    Vector3 G(float d,int along=0)=>LongRaceRoads.Ground(p,open,open?Mathf.Min(i+along,p.Length-1):(i+along)%p.Length,side*d,place);
                    // Grass tufts fringe the verge on every place.
                    for(int k=0;k<6;k++)Grass(chunk,G(16.6f+Noise(i+k,side+60)*(k<4?5:14),k*2/3),place,Noise(i+k,side+61));
                    if(place==1)
                    {
                        // Close woodland, then a second canopy layer. Clearings every ~1.2 km.
                        bool clearing=(i/150)%4==1;
                        if(!clearing||i%12==0)
                        {
                            Model(chunk,"Quaternius",i%8==0?"CommonTree_1":"CommonTree_3",G(24+n*17),9+n*8,n*360);
                            Model(chunk,"Quaternius","CommonTree_1",G(63+n*33),12+n*11,n*190);
                            Model(chunk,"Quaternius","CommonTree_3",G(43+n*10),13+n*9,n*80);
                            Model(chunk,"Quaternius","CommonTree_1",G(117+n*25),16+n*12,n*280);
                            PineTree(chunk,G(20+m*9,1),9+m*7,m);
                            PineTree(chunk,G(34+q*14,2),13+q*8,q);
                            PineTree(chunk,G(52+m*22,3),15+n*9,n);
                            PineTree(chunk,G(88+q*45,1),18+m*10,(m+q)*.5f);
                        }
                        else PineTree(chunk,G(30+m*25,2),12+q*8,q);
                        Ph(chunk,"fern_02",G(17+m*4),.8f+q*.5f,m*360);
                        if(i%8==0)Ph(chunk,"fern_02",G(22+q*6,2),.9f+n*.5f,q*360);
                        if(i%8==4)Model(chunk,"Quaternius","Bush_Common_Flowers",G(18+n*5),1.4f+n,90*n);
                        if(i%20==0)Ph(chunk,"rock_moss_set_01",G(25+n*14),1.4f+m*1.6f,i*19);
                        else if(i%12==0)Ph(chunk,"rock_09",G(21+q*8),.9f+n*1.1f,i*31);
                    }
                    else if(place==2)
                    {
                        Ph(chunk,i%8==0?"namaqualand_boulder_04":"namaqualand_boulder_05",G(32+n*45),5+n*13,n*360);
                        if(i%8==4)Ph(chunk,i%16==4?"namaqualand_boulder_05":"namaqualand_boulder_04",G(19+m*6,2),.9f+q*1.6f,m*360);
                        if((i+(side>0?0:8))%20==0)Ph(chunk,"namaqualand_boulder_05",G(70+m*40,2),16+q*14,q*360);
                        Ph(chunk,"wild_rooibos_bush",G(17+m*10,1),.9f+q*.8f,m*360);
                        if(i%8==0)Ph(chunk,"wild_rooibos_bush",G(26+q*12,3),1.1f+n*.9f,q*360);
                        if(i%12==0)Cactus(chunk,G(21+n*6),2.5f+n*2);
                        if(i%28==0)Mesa(chunk,p[i]+right*side*(140+n*70),30+n*45,70+n*90,i);
                    }
                    else
                    {
                        if(side==1&&i%8==0)Model(chunk,"Quaternius",i%16==0?"CommonTree_1":"CommonTree_3",G(27+n*28),8+n*5,n*180);
                        if(side==-1&&i%16==8)Model(chunk,"Quaternius","CommonTree_3",G(30+q*14),7+q*4,q*180);
                        if(i%8==0)Ph(chunk,i%16==0?"rock_09":"rock_07",G(24+n*26),1.2f+n*3.2f,n*360);
                        if(side==-1&&i%16==4)Ph(chunk,"rock_07",G(36+m*30,2),3+m*4,m*360);
                        if(i%8==4)Ph(chunk,"shrub_sorrel_01",G(18+m*8,1),.7f+q*.6f,m*360);
                        if(i%12==0)Ph(chunk,"crystalline_iceplant",G(17+q*5,2),.35f+n*.25f,q*360);
                        if(i%12==0)Model(chunk,"Quaternius","Bush_Common_Flowers",G(19),1.2f+n,n*360);
                        if(i>30&&i<125&&i%8==0&&side==1)
                        {
                            Model(chunk,"KenneySuburban",i%16==0?"building-type-a":"building-type-d",G(42+n*18),7+n*3,Mathf.Atan2(t.x,t.z)*Mathf.Rad2Deg-90);
                            LongRaceRoads.Box(chunk,"VillagePath",p[i]+right*25-Vector3.up*.03f,new Vector3(13,.08f,4),LongRaceRoads.Mat("VillagePaving","D8C8AD"));
                        }
                        if(side==-1&&i%60==0)
                        {
                            var boatPos=p[i]-right*95;boatPos.y=-3.8f;
                            Model(chunk,"KenneyWatercraft",i%120==0?"boat-sail-a":"boat-fishing-small",boatPos,9+n*5,n*90);
                            var pier=LongRaceRoads.Box(chunk,"HarborPier",p[i]-right*42-Vector3.up*.4f,new Vector3(38,.5f,6),LongRaceRoads.Mat("TourPier","A38461"));
                            pier.transform.rotation=Quaternion.LookRotation(t);
                        }
                    }
                }
                if(i%40==0)TurnSign(chunk,p,i,open);
            }
            // Large recognizable features mark new sections rather than uniform scatter.
            if(place==1&&start%160==0&&start>0)
            {
                var point=p[start];var right=LongRaceRoads.Right(LongRaceRoads.Tangent(p,start,open));
                var lake=LongRaceRoads.Box(chunk,"ForestLake",point+right*123-Vector3.up*1.5f,new Vector3(125,.06f,230),
                    LongRaceRoads.Mat("LakeWater","FFFFFF","FamilyRacing/Trial Coastal Water"));
                lake.transform.rotation=Quaternion.LookRotation(LongRaceRoads.Tangent(p,start,open));
                Ph(chunk,"rock_moss_set_01",LongRaceRoads.Ground(p,open,start,76,place),5,35);
            }
            if(place==2&&start>p.Length-75)
            {
                var point=p[start];var right=LongRaceRoads.Right(LongRaceRoads.Tangent(p,start,open));
                for(int j=0;j<4;j++)Model(chunk,"Quaternius","CommonTree_3",LongRaceRoads.Ground(p,open,start,27+j*9,place)+Vector3.forward*j*7,10+j,30*j);
            }
        }
        private static void TurnSign(Transform chunk,Vector3[] points,int i,bool open)
        {
            var t=LongRaceRoads.Tangent(points,i,open);var right=LongRaceRoads.Right(t);
            var position=points[i]+right*17;
            LongRaceRoads.Box(chunk,"SignPost",position+Vector3.up*1.6f,new Vector3(.15f,3.2f,.15f),LongRaceRoads.Mat("TourPost","52666B"));
            var face=LongRaceRoads.Box(chunk,"ChevronBoard",position+Vector3.up*3,new Vector3(2.5f,1.5f,.15f),LongRaceRoads.Mat("TourSign","F3C85E"));
            face.transform.rotation=Quaternion.LookRotation(t);
            int next=open?Mathf.Min(i+10,points.Length-1):(i+10)%points.Length;
            float turn=Vector3.Cross(t,LongRaceRoads.Tangent(points,next,open)).y;
            foreach(int wing in new[]{-1,1})
            {
                var stripe=LongRaceRoads.Box(chunk,"Chevron",position+Vector3.up*(3+wing*.22f)-t*.09f,new Vector3(.75f,.2f,.04f),LongRaceRoads.Mat("TourNavy","254857"));
                stripe.transform.rotation=Quaternion.LookRotation(t)*Quaternion.Euler(0,0,wing*(turn>=0?-38:38));
            }
        }
        private static void Mesa(Transform chunk,Vector3 p,float height,float width,int seed)
        {
            // Layered CC0 cliff texture, ~20 m per tile, wraps the mesa sides.
            var mat=LongRaceRoads.Pbr("MesaDistant","cliff_side","E7B394",.18f);
            var vertices=new System.Collections.Generic.List<Vector3>();var tris=new System.Collections.Generic.List<int>();var uv=new System.Collections.Generic.List<Vector2>();
            const int sides=11;
            for(int ring=0;ring<5;ring++)for(int k=0;k<sides;k++)
            {
                float angle=k*Mathf.PI*2/sides;float radius=width*.5f*(1-ring*.105f)*( .85f+Noise(k,seed)*.3f);
                vertices.Add(chunk.InverseTransformPoint(p+new Vector3(Mathf.Cos(angle)*radius,ring*height*.25f,Mathf.Sin(angle)*radius*.75f)));
                uv.Add(new Vector2(k*width*2.6f/sides/20f,ring*height*.25f/20f));
                if(ring<4){int n=ring*sides+k;int next=ring*sides+(k+1)%sides;tris.AddRange(new[]{n,n+sides,next,next,n+sides,next+sides});}
            }
            int top=vertices.Count;vertices.Add(chunk.InverseTransformPoint(p+Vector3.up*height));uv.Add(new Vector2(0,height/20f));
            for(int k=0;k<sides;k++)tris.AddRange(new[]{top,4*sides+(k+1)%sides,4*sides+k});
            LongRaceRoads.MeshObject(chunk,chunk.parent.parent.name+"_Mesa"+seed,vertices,tris,uv,mat,false);
        }
        private static void Cactus(Transform chunk,Vector3 p,float height)
        {
            var mat=LongRaceRoads.Mat("TourCactus","598565");
            LongRaceRoads.Box(chunk,"CactusStem",p+Vector3.up*height*.5f,new Vector3(.45f,height,.45f),mat);
            LongRaceRoads.Box(chunk,"CactusArm",p+Vector3.up*height*.54f+Vector3.right*.6f,new Vector3(1.5f,.35f,.35f),mat);
            LongRaceRoads.Box(chunk,"CactusTip",p+Vector3.up*height*.7f+Vector3.right*1.2f,new Vector3(.35f,height*.42f,.35f),mat);
        }
    }
}
