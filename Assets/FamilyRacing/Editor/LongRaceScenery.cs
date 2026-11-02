using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FamilyRacing.EditorTools
{
    // CC0 scans and original geometry, batched in local road chunks.
    public static class LongRaceScenery
    {
        private static float Noise(int i, int salt) => Mathf.Repeat(Mathf.Sin(i*12.9898f+salt*78.233f)*43758.5453f,1f);
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
        // Multi-material CC0 scan: one decimated OBJ per material part, placed as one
        // object. `sink` buries that fraction of the height (a hull's draught).
        private static readonly string[] DutchShip={"dutch_ship_medium_hull","dutch_ship_medium_rigging","dutch_ship_medium_sails"};
        private static void PhGroup(Transform parent,string[] ids,Vector3 p,float height,float yaw,float sink=.02f)
        {
            var go=new GameObject(ids[0]);
            foreach(var id in ids)
            {
                if(!Cache.TryGetValue(id,out var mat))
                {
                    var made=LongRaceRoads.Pbr("PH_"+id,id,"FFFFFF",.3f);
                    // Sails and rigging are single sheets; draw both faces.
                    if(id.Contains("sails")||id.Contains("rigging"))made.SetFloat("_Cull",0);
                    Cache[id]=mat=made;
                }
                var part=Object.Instantiate(LongRaceAuthoring.Required<GameObject>(LongRaceRoads.PolyHaven+"/"+id+"/"+id+".obj"),go.transform);part.name=id;
                foreach(var r in part.GetComponentsInChildren<Renderer>())r.sharedMaterials=r.sharedMaterials.Select(_=>(Material)mat).ToArray();
            }
            go.transform.rotation=Quaternion.Euler(0,yaw,0);
            var rs=go.GetComponentsInChildren<Renderer>();var bounds=rs[0].bounds;foreach(var r in rs.Skip(1))bounds.Encapsulate(r.bounds);
            go.transform.localScale*=height/Mathf.Max(.01f,bounds.size.y);
            rs=go.GetComponentsInChildren<Renderer>();bounds=rs[0].bounds;foreach(var r in rs.Skip(1))bounds.Encapsulate(r.bounds);
            go.transform.position+=p-new Vector3(bounds.center.x,bounds.min.y+height*sink,bounds.center.z);
            go.transform.SetParent(parent,true);
            foreach(var t in go.GetComponentsInChildren<Transform>())t.gameObject.layer=2;
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
        // Card conifer authored 1 m tall: bark trunk, whorls of crossed cards carrying the
        // CC0 fir branch atlas, and a dark core that fills the crown between the cards.
        // The umbrella form is the coast's stone pine: long bare stem, flat spreading crown.
        private static Mesh Conifer(int variant,bool umbrella)=>Cached((umbrella?"TourStonePine":"TourFir")+variant,()=>
        {
            var v=new System.Collections.Generic.List<Vector3>();var nor=new System.Collections.Generic.List<Vector3>();var uv=new System.Collections.Generic.List<Vector2>();
            var bark=new System.Collections.Generic.List<int>();var needles=new System.Collections.Generic.List<int>();var core=new System.Collections.Generic.List<int>();
            float N(int k)=>Noise(k+variant*131,umbrella?17:9);
            var lean=umbrella?new Vector3(N(1)-.5f,0,N(2)-.5f)*.12f:Vector3.zero;
            Vector3 Axis(float y)=>lean*y*y+Vector3.up*y;
            // Seven-sided trunk, bark tiled about every 1/6 of the height.
            float trunkTop=umbrella?.86f:.94f,r0=umbrella?.026f:.021f;
            for(int ring=0;ring<=4;ring++)for(int k=0;k<=7;k++)
            {
                float y=trunkTop*ring/4f,a=k*Mathf.PI*2/7,r=Mathf.Lerp(r0,.004f,y/trunkTop)*(ring==0?1.35f:1);
                var o=new Vector3(Mathf.Cos(a),0,Mathf.Sin(a));v.Add(Axis(y)+o*r);nor.Add(o);uv.Add(new Vector2(k/7f*1.5f,y*6));
                if(ring<4&&k<7){int n=ring*8+k;bark.AddRange(new[]{n,n+8,n+1,n+1,n+8,n+9});}
            }
            void Quad(System.Collections.Generic.List<int> list,Vector3 a0,Vector3 a1,Vector3 b0,Vector3 b1,Vector2 u,Vector3 centre)
            {
                int n=v.Count;
                foreach(var q in new[]{a0,a1,b0,b1}){v.Add(q);var d=q-centre;d.y*=.6f;nor.Add((d.normalized+Vector3.up*.35f).normalized);}
                uv.Add(new Vector2(u.x,0));uv.Add(new Vector2(u.y,0));uv.Add(new Vector2(u.x,1));uv.Add(new Vector2(u.y,1));
                list.AddRange(new[]{n,n+2,n+1,n+1,n+2,n+3});
            }
            float bottom=umbrella?.56f:.1f;int whorls=umbrella?8:18,per=umbrella?7:6;
            var crownCentre=Axis(umbrella?.8f:.42f);
            for(int w=0;w<whorls;w++)
            {
                float t=w/(whorls-1f),y=Mathf.Lerp(bottom,.98f,t);
                float radius=umbrella?.06f+.36f*Mathf.Sin(Mathf.PI*Mathf.Lerp(.2f,1f,t))*(1-.45f*t):.02f+.27f*Mathf.Pow(1-t,.85f);
                for(int b=0;b<per;b++)
                {
                    float a=w*2.4f+b*Mathf.PI*2/per+(N(w*per+b)-.5f)*.7f;
                    var d=new Vector3(Mathf.Cos(a),0,Mathf.Sin(a));float len=radius*(.95f+.3f*N(w*per+b+300));
                    // Fir boughs droop toward the tips low down and lift near the leader.
                    float rise=umbrella?.18f:Mathf.Lerp(-.32f,.35f,t);
                    var root=Axis(y)+d*.01f;var tip=root+(d+Vector3.up*rise).normalized*len;
                    var along=(tip-root).normalized;var flat=Vector3.Cross(Vector3.up,d).normalized;
                    var u=(w+b)%2==0?new Vector2(0,.5f):new Vector2(.5f,1);
                    foreach(float tilt in new[]{12f,78f})
                    {
                        var side=Quaternion.AngleAxis(tilt*(b%2==0?1:-1),along)*flat*len*(umbrella?.27f:.32f);
                        Quad(needles,root-side*.55f,root+side*.55f,tip-side,tip+side,u,crownCentre);
                    }
                }
            }
            // Leader: two crossed upright cards.
            for(int k=0;k<2;k++)
            {
                var side=(k==0?Vector3.right:Vector3.forward)*.045f;var root=Axis(umbrella?.9f:.86f);var top=Axis(1.04f);
                Quad(needles,root-side,root+side,top-side*.6f,top+side*.6f,new Vector2(0,.5f),crownCentre);
            }
            // Core: dark, slightly smaller crown shell (octagonal), seen only through gaps.
            const int sides=8;int coreStart=v.Count;int rings=umbrella?4:6;
            for(int ring=0;ring<rings;ring++)for(int k=0;k<sides;k++)
            {
                float t=ring/(rings-1f),y=umbrella?Mathf.Lerp(.64f,.93f,t):Mathf.Lerp(.16f,.92f,t);
                float r=umbrella?.27f*Mathf.Sin(Mathf.PI*Mathf.Lerp(.15f,.95f,t)):.11f*Mathf.Pow(1-t,.9f)+.006f;
                float a=k*Mathf.PI*2/sides;var o=new Vector3(Mathf.Cos(a),0,Mathf.Sin(a))*r*(.9f+.2f*N(ring*sides+k+700));
                v.Add(Axis(y)+o);nor.Add((o.normalized+Vector3.up*.3f).normalized);uv.Add(Vector2.zero);
                if(ring<rings-1){int n=coreStart+ring*sides+k,m=coreStart+ring*sides+(k+1)%sides;core.AddRange(new[]{n,n+sides,m,m,n+sides,m+sides});}
            }
            int apex=v.Count;v.Add(Axis(umbrella?.95f:.97f));nor.Add(Vector3.up);uv.Add(Vector2.zero);
            for(int k=0;k<sides;k++)core.AddRange(new[]{coreStart+(rings-1)*sides+k,apex,coreStart+(rings-1)*sides+(k+1)%sides});
            var mesh=new Mesh();mesh.SetVertices(v);mesh.SetNormals(nor);mesh.SetUVs(0,uv);mesh.subMeshCount=3;
            mesh.SetTriangles(bark,0);mesh.SetTriangles(needles,1);mesh.SetTriangles(core,2);mesh.RecalculateTangents();return mesh;
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
        private static void Tree(Transform chunk,Vector3 p,float height,float n,bool umbrella=false)
        {
            var bark=LongRaceRoads.Pbr("TourFirBark","fir_bark","C9BBAA",.12f);
            // Three needle tints so a stand of firs does not read as one copy.
            var needles=LongRaceRoads.Pbr(umbrella?"TourStonePineNeedles":n>.66f?"TourFirNeedlesLight":n>.33f?"TourFirNeedles":"TourFirNeedlesDark","fir_branch",
                umbrella?"C4CC9A":n>.66f?"B9C79E":n>.33f?"98AA86":"7D9276",.12f);
            var core=LongRaceRoads.Mat(umbrella?"TourStonePineCore":"TourFirCore",umbrella?"2E3F25":"1E3022");
            int variant=Mathf.FloorToInt(Noise((int)(n*997),23)*3)%3;
            Proc(chunk,umbrella?"StonePine":"Fir",Conifer(variant,umbrella),new[]{bark,needles,core},p-Vector3.up*.25f,height,n*720,
                (umbrella?1.05f:.9f)+.25f*Noise((int)(n*997),5));
        }
        // Low rolling hill under the forest-floor PBR set, with a stand of trees on it.
        private static void Hill(Transform chunk,Vector3 center,int place,int seed,float height)
        {
            var v=new System.Collections.Generic.List<Vector3>();var tris=new System.Collections.Generic.List<int>();var uv=new System.Collections.Generic.List<Vector2>();
            const int rings=10,sectors=24;
            float Lift(float r)=>height*Mathf.Pow(Mathf.Max(0,Mathf.Cos(r*Mathf.PI*.5f)),1.4f);
            for(int ring=0;ring<=rings;ring++)for(int k=0;k<sectors;k++)
            {
                float r=ring/(float)rings,a=k*Mathf.PI*2/sectors;
                float wobble=1+.18f*(Noise(k+seed,31)-.5f)+.1f*Mathf.Sin(a*3+seed);
                float h=Lift(r)*(1+.12f*(Noise(ring*sectors+k+seed,37)-.5f));
                var world=center+new Vector3(Mathf.Cos(a)*210*r*wobble,h-2,Mathf.Sin(a)*220*r*wobble);
                v.Add(chunk.InverseTransformPoint(world));uv.Add(new Vector2(world.x,world.z)/9f);
                if(ring<rings){int n=ring*sectors+k,m=ring*sectors+(k+1)%sectors;tris.AddRange(new[]{n,m,n+sectors,m,m+sectors,n+sectors});}
            }
            var mat=LongRaceRoads.Pbr("DistantHillGround"+place,"forrest_ground_01",place==0?"C9C7A2":"A8AF92",.1f);
            LongRaceRoads.MeshObject(chunk,chunk.parent.parent.name+"_TerrainHill"+seed,v,tris,uv,mat,false);
            int count=place==1?70:22;
            for(int k=0;k<count;k++)
            {
                float r=Mathf.Sqrt(Noise(k+seed,41))*.8f,a=Noise(k+seed,43)*Mathf.PI*2;
                if(place==0&&r<.35f)continue;
                var p=center+new Vector3(Mathf.Cos(a)*210*r,Lift(r)-4.5f,Mathf.Sin(a)*220*r);
                Tree(chunk,p,place==1?16+Noise(k+seed,47)*12:9+Noise(k+seed,47)*5,Noise(k+seed,49),place==0);
            }
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
                    Hill(chunk,center,place,start*2+side,place==0?42:72);
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
                            // Dense conifer stand: a ragged edge row, then taller firs behind.
                            Tree(chunk,G(21+m*6,1),10+m*6,m);
                            Tree(chunk,G(28+q*9,3),14+q*7,q);
                            Tree(chunk,G(37+n*10),17+n*8,n);
                            Tree(chunk,G(49+m*14,2),19+q*8,(m+n)*.5f);
                            Tree(chunk,G(64+q*18,1),20+m*9,(q+n)*.5f);
                            Tree(chunk,G(84+n*22,3),22+q*8,(m+q)*.5f);
                            Tree(chunk,G(112+m*30,2),24+n*8,1-n);
                            if(i%8==0)Tree(chunk,G(18.5f+q*3,2),2.2f+m*2.5f,q);
                        }
                        else
                        {
                            Tree(chunk,G(30+m*25,2),12+q*8,q);
                            Tree(chunk,G(19+q*3,1),2.5f+m*2,m);
                        }
                        Ph(chunk,"fern_02",G(17+m*4),.8f+q*.5f,m*360);
                        if(i%8==0)Ph(chunk,"fern_02",G(22+q*6,2),.9f+n*.5f,q*360);
                        if(i%8==4)Ph(chunk,"shrub_sorrel_01",G(18+n*5),.9f+n*.6f,90*n);
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
                        // Quiver trees stand alone on the slopes, a few further back; low
                        // succulent mats hug the verge.
                        if(i%12==0)Ph(chunk,"quiver_tree_02",G(21+n*9),3.6f+n*2.8f,m*360);
                        if((i+(side>0?4:10))%24==0)Ph(chunk,"quiver_tree_02",G(48+q*40,2),4.5f+m*3,q*360);
                        if(i%8==4)Ph(chunk,"cheiridopsis_succulent",G(17.5f+q*5,1),.28f+m*.16f,q*360);
                        if(i%28==0)Mesa(chunk,p[i]+right*side*(140+n*70),30+n*45,70+n*90,i);
                    }
                    else
                    {
                        if(side==1&&i%8==0)Tree(chunk,G(27+n*28),9+n*5,n,true);
                        if(side==-1&&i%16==8)Tree(chunk,G(30+q*14),8+q*4,q,true);
                        if(i%8==0)Ph(chunk,i%16==0?"rock_09":"rock_07",G(24+n*26),1.2f+n*3.2f,n*360);
                        if(side==-1&&i%16==4)Ph(chunk,"rock_07",G(36+m*30,2),3+m*4,m*360);
                        if(i%8==4)Ph(chunk,"shrub_sorrel_01",G(18+m*8,1),.7f+q*.6f,m*360);
                        if(i%12==0)Ph(chunk,"crystalline_iceplant",G(17+q*5,2),.35f+n*.25f,q*360);
                        if(i%12==0)Ph(chunk,"wild_rooibos_bush",G(19),.9f+n*.6f,n*360);
                        if(i>30&&i<125&&i%8==0&&side==1)
                        {
                            House(chunk,G(42+n*18)-Vector3.up*.3f,-right,Mathf.FloorToInt(n*7)%3,(i/8)%3,.9f+m*.25f);
                            var path=LongRaceRoads.Box(chunk,"VillagePath",G(25)+Vector3.up*.03f,new Vector3(13,.08f,3),LongRaceRoads.Pbr("VillagePaving","gravel_road","E2D3BE",.2f));
                            path.transform.rotation=Quaternion.LookRotation(t);
                        }
                        if(side==-1&&i%60==0)
                        {
                            // A tall ship rides at anchor off every other harbour; channel
                            // markers and buoys mark the rest of the bay.
                            float shore=Shore(p,open,i);
                            var sea=p[i]-right*(i%120==0?Mathf.Max(170,shore+70):Mathf.Max(95,shore+30));sea.y=-3.8f;
                            if(i%120==0)PhGroup(chunk,DutchShip,sea,24+n*4,n*360,.11f);
                            else Ph(chunk,"lateral_sea_marker",sea-Vector3.up*1.5f,6.8f,n*360);
                            if(shore>0)Pier(chunk,p[i]-right*(shore+12),t);
                        }
                        if(side==-1&&i%60==30){var buoy=p[i]-right*(Mathf.Max(70,Shore(p,open,i)+20)+n*40);buoy.y=-3.8f-.7f;Ph(chunk,"ocean_buoy",buoy,2.6f,n*360);}
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
            if(place==0)foreach(int side in new[]{-1,1})SurfLine(chunk,p,open,start,end,side);
            if(place==2&&start>p.Length-75)
            {
                var point=p[start];var right=LongRaceRoads.Right(LongRaceRoads.Tangent(p,start,open));
                for(int j=0;j<4;j++)Tree(chunk,LongRaceRoads.Ground(p,open,start,27+j*9,place)+Vector3.forward*j*7,10+j,.25f*j,true);
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
        // Whitewashed coastal house: CC0 plaster walls (three tints), clay tile gable
        // roof, shuttered windows and a door on the road side. Authored to size so the
        // plaster and tiles keep their real scale; `variant` changes the footprint.
        private static Mesh HouseMesh(int variant)=>Cached("TourHouse"+variant,()=>
        {
            float w=new[]{8.5f,11f,7f}[variant],d=new[]{6.5f,7.5f,6f}[variant],h=new[]{5.6f,3.4f,6.2f}[variant],rise=new[]{2.2f,1.9f,2.6f}[variant];
            var v=new System.Collections.Generic.List<Vector3>();var uv=new System.Collections.Generic.List<Vector2>();
            var subs=new[]{new System.Collections.Generic.List<int>(),new System.Collections.Generic.List<int>(),new System.Collections.Generic.List<int>(),new System.Collections.Generic.List<int>()};
            void Quad(int sub,Vector3 a,Vector3 b,Vector3 c,Vector3 e,float tile)
            {   // a,b bottom edge; c,e top edge (c above a), front face counter-clockwise from outside.
                int n=v.Count;v.Add(a);v.Add(b);v.Add(c);v.Add(e);
                float wq=Vector3.Distance(a,b)/tile,hq=Vector3.Distance(a,c)/tile;
                uv.Add(new Vector2(0,0));uv.Add(new Vector2(wq,0));uv.Add(new Vector2(0,hq));uv.Add(new Vector2(wq,hq));
                subs[sub].AddRange(new[]{n,n+2,n+1,n+1,n+2,n+3});
            }
            float x=w/2,z=d/2,b=-1.2f;
            Quad(0,new Vector3(x,b,z),new Vector3(-x,b,z),new Vector3(x,h,z),new Vector3(-x,h,z),2.5f);      // front (+z)
            Quad(0,new Vector3(-x,b,-z),new Vector3(x,b,-z),new Vector3(-x,h,-z),new Vector3(x,h,-z),2.5f);  // back
            Quad(0,new Vector3(x,b,-z),new Vector3(x,b,z),new Vector3(x,h,-z),new Vector3(x,h,z),2.5f);      // ends
            Quad(0,new Vector3(-x,b,z),new Vector3(-x,b,-z),new Vector3(-x,h,z),new Vector3(-x,h,-z),2.5f);
            foreach(float sx in new[]{-1f,1f})
            {   // Gable triangles carry on in plaster.
                int n=v.Count;v.Add(new Vector3(sx*x,h,-sx*z));v.Add(new Vector3(sx*x,h,sx*z));v.Add(new Vector3(sx*x,h+rise,0));
                uv.Add(new Vector2(0,h/2.5f));uv.Add(new Vector2(d/2.5f,h/2.5f));uv.Add(new Vector2(d/5f,(h+rise)/2.5f));
                subs[0].AddRange(new[]{n,n+2,n+1});
            }
            // Roof with a 0.45 m overhang, tiles running down the slope; fascia boards at the eaves.
            float ox=x+.45f,oz=z+.45f,dropY=rise*.45f/z;
            Quad(1,new Vector3(ox,h-dropY,oz),new Vector3(-ox,h-dropY,oz),new Vector3(ox,h+rise,0),new Vector3(-ox,h+rise,0),2f);
            Quad(1,new Vector3(-ox,h-dropY,-oz),new Vector3(ox,h-dropY,-oz),new Vector3(-ox,h+rise,0),new Vector3(ox,h+rise,0),2f);
            Quad(1,new Vector3(ox,h-dropY-.12f,oz),new Vector3(-ox,h-dropY-.12f,oz),new Vector3(ox,h-dropY,oz),new Vector3(-ox,h-dropY,oz),2f);
            Quad(1,new Vector3(-ox,h-dropY-.12f,-oz),new Vector3(ox,h-dropY-.12f,-oz),new Vector3(-ox,h-dropY,-oz),new Vector3(ox,h-dropY,-oz),2f);
            // Windows: dark glass just proud of the wall, painted shutters either side.
            int floors=h>5?2:1;int across=Mathf.Max(2,Mathf.FloorToInt(w/2.6f));
            foreach(float face in new[]{1f,-1f})for(int f=0;f<floors;f++)for(int k=0;k<across;k++)
            {
                float cx=-x+w*(k+.5f)/across,y0=.9f+f*2.6f,y1=y0+1.3f,zf=face*(z+.03f);
                bool door=face>0&&f==0&&k==across/2;
                if(door){y0=-.05f;y1=2.15f;}
                float hw=door?.55f:.45f;
                // Mirror x on the back face so every quad still winds outward.
                Vector3 P(float px,float py,float off)=>new Vector3(face>0?-px:px,py,zf+face*off);
                Quad(door?3:2,P(cx-hw,y0,0),P(cx+hw,y0,0),P(cx-hw,y1,0),P(cx+hw,y1,0),1f);
                if(!door)foreach(float sd in new[]{-1f,1f})
                {
                    float s0=Mathf.Min(cx+sd*(hw+.02f),cx+sd*(hw+.5f)),s1=Mathf.Max(cx+sd*(hw+.02f),cx+sd*(hw+.5f));
                    Quad(3,P(s0,y0,.02f),P(s1,y0,.02f),P(s0,y1,.02f),P(s1,y1,.02f),1f);
                }
            }
            var mesh=new Mesh();mesh.SetVertices(v);mesh.SetUVs(0,uv);mesh.subMeshCount=4;
            for(int k=0;k<4;k++)mesh.SetTriangles(subs[k],k);mesh.RecalculateNormals();mesh.RecalculateTangents();return mesh;
        });
        private static void House(Transform chunk,Vector3 p,Vector3 facing,int variant,int tint,float scale)
        {
            var walls=LongRaceRoads.Pbr("TourHouseWall"+tint,"white_plaster_02",new[]{"F4F1EA","F1E2C4","E3E9EC"}[tint],.15f,.6f);
            var roof=LongRaceRoads.Pbr("TourHouseRoof","clay_roof_tiles_02","E8D2C2",.2f);
            var glass=LongRaceRoads.Mat("TourHouseGlass","26323A");
            var paint=LongRaceRoads.Mat("TourHouseShutter"+tint,new[]{"2F6F8F","4E7A55","8A5A3C"}[tint]);
            Proc(chunk,"House",HouseMesh(variant),new[]{walls,roof,glass,paint},p,scale,Mathf.Atan2(facing.x,facing.z)*Mathf.Rad2Deg);
        }
        // Timber jetty: weathered CC0 planks on a row of piles.
        private static void Pier(Transform chunk,Vector3 p,Vector3 along)
        {
            var planks=LongRaceRoads.Pbr("TourPierPlanks","weathered_planks","C9B8A2",.15f);planks.SetTextureScale("_BaseMap",new Vector2(3,12));
            p.y=-2.9f;var pier=LongRaceRoads.Box(chunk,"HarborPier",p,new Vector3(4,.3f,34),planks);
            pier.transform.rotation=Quaternion.LookRotation(Vector3.Cross(Vector3.up,along));
            var piles=LongRaceRoads.Pbr("TourPierPiles","weathered_planks","8E7B66",.1f);
            for(int k=0;k<8;k++)foreach(float sd in new[]{-.45f,.45f})
                LongRaceRoads.Box(chunk,"PierPile",pier.transform.TransformPoint(new Vector3(sd,0,-.5f+k/7f))-Vector3.up*.6f,new Vector3(.3f,1.6f,.3f),piles);
        }
        // Soft foam where the bay meets the beach: a strip along the shoreline on the
        // sea side, sunk into the sand on its land edge so it reads as a wet surf line.
        private const float Water=-3.77f;
        // Distance from the road centre to the coast's waterline on one side (-1 left, 1 right), or -1.
        private static float Shore(Vector3[] p,bool open,int i,int side=-1)
        {
            for(float d=20;d<=156;d+=2)
            {
                float y0=LongRaceRoads.Ground(p,open,i,side*d,0).y,y1=LongRaceRoads.Ground(p,open,i,side*(d+2),0).y;
                if(y0>=Water&&y1<Water)return d+2*(y0-Water)/Mathf.Max(.001f,y0-y1);
            }
            return -1;
        }
        public static void SurfLine(Transform chunk,Vector3[] p,bool open,int start,int end,int side)
        {
            var v=new System.Collections.Generic.List<Vector3>();var uv=new System.Collections.Generic.List<Vector2>();var tris=new System.Collections.Generic.List<int>();
            bool joined=false;float run=0;Vector3 prev=Vector3.zero;
            for(int i=start;i<=end;i+=2)
            {
                float shore=Shore(p,open,i,side);
                if(shore<0){joined=false;continue;}
                var right=LongRaceRoads.Right(LongRaceRoads.Tangent(p,i%p.Length,open))*-side;var c=p[i%p.Length]-right*shore;c.y=Water+.035f;
                if(joined)run+=Vector3.Distance(prev,c);prev=c;
                int n=v.Count;v.Add(chunk.InverseTransformPoint(c+right*3.5f-Vector3.up*.25f));v.Add(chunk.InverseTransformPoint(c-right*4.5f));
                uv.Add(new Vector2(run/14f,0));uv.Add(new Vector2(run/14f,1));
                if(joined)tris.AddRange(side<0?new[]{n-2,n-1,n,n-1,n+1,n}:new[]{n-2,n,n-1,n-1,n,n+1});
                joined=true;
            }
            if(tris.Count==0)return;
            LongRaceRoads.MeshObject(chunk,chunk.parent.parent.name+"_TerrainSurf"+start+(side<0?"L":"R"),v,tris,uv,SurfMaterial(),false);
        }
        private static Material SurfMaterial()
        {
            string path=LongRaceRoads.Dir+"/TourSurf.asset";
            var tex=AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if(tex==null)
            {   // Two foam bands across the strip, broken up along the shore; fades out on the sea edge.
                tex=new Texture2D(256,64,TextureFormat.RGBA32,true){name="TourSurf",wrapMode=TextureWrapMode.Repeat};
                for(int y=0;y<64;y++)for(int x=0;x<256;x++)
                {
                    float across=y/63f,along=x/256f*Mathf.PI*2;
                    float band=Mathf.Exp(-Mathf.Pow((across-.42f)/.13f,2))+.55f*Mathf.Exp(-Mathf.Pow((across-.7f)/.07f,2))*(.5f+.5f*Mathf.Sin(along*3+1.3f));
                    float breakup=.55f+.45f*Mathf.PerlinNoise(x*.06f,y*.18f);
                    tex.SetPixel(x,y,new Color(1,1,1,Mathf.Clamp01(band*breakup*.85f)*Mathf.SmoothStep(0,1,across*5)));
                }
                tex.Apply();AssetDatabase.CreateAsset(tex,path);
            }
            var mat=LongRaceRoads.Mat("TourSurf","F7FBFA","Universal Render Pipeline/Lit");
            mat.SetTexture("_BaseMap",tex);mat.SetFloat("_Surface",1);mat.SetFloat("_Blend",0);mat.SetFloat("_ZWrite",0);mat.SetFloat("_Smoothness",.4f);
            mat.SetFloat("_SrcBlend",(float)UnityEngine.Rendering.BlendMode.SrcAlpha);mat.SetFloat("_DstBlend",(float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");mat.SetOverrideTag("RenderType","Transparent");mat.renderQueue=3000;
            EditorUtility.SetDirty(mat);return mat;
        }
    }
}
