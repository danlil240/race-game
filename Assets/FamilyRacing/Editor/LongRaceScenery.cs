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
                    float n=Noise(i,side+place*3+5);
                    if(place==1)
                    {
                        // Close woodland, then a second canopy layer. Clearings every ~1.2 km.
                        bool clearing=(i/150)%4==1;
                        if(!clearing||i%12==0)
                        {
                            Model(chunk,"Quaternius",i%8==0?"CommonTree_1":"CommonTree_3",
                                p[i]+right*side*(24+n*17)+Vector3.up*1.5f,9+n*8,n*360);
                            Model(chunk,"Quaternius","CommonTree_1",p[i]+right*side*(63+n*33)+Vector3.up*(5+n*4),12+n*11,n*190);
                            Model(chunk,"Quaternius","CommonTree_3",p[i]+right*side*(43+n*10)+Vector3.up*2,13+n*9,n*80);
                            Model(chunk,"Quaternius","CommonTree_1",p[i]+right*side*(117+n*25)+Vector3.up*(9+n*5),16+n*12,n*280);
                        }
                        if(i%8==0)Model(chunk,"Quaternius","Bush_Common_Flowers",p[i]+right*side*(18+n*5),1.4f+n,90*n);
                        if(i%20==0)Model(chunk,"Quaternius","Rock_Medium_1",p[i]+right*side*32,3+n*3,i*19);
                    }
                    else if(place==2)
                    {
                        var rock=Model(chunk,"Quaternius",i%8==0?"Rock_Medium_1":"Rock_Medium_2",
                            p[i]+right*side*(35+n*42)+Vector3.up*2,13+n*24,n*360);
                        var sandstone=LongRaceRoads.Mat(i%12==0?"CanyonOchre":"CanyonSandstone",i%12==0?"B1734D":"D49D6B");
                        foreach(var r in rock.GetComponentsInChildren<Renderer>())r.sharedMaterials=r.sharedMaterials.Select(_=>sandstone).ToArray();
                        if(i%12==0)Cactus(chunk,p[i]+right*side*(21+n*6),2.5f+n*2);
                        if(i%28==0)Mesa(chunk,p[i]+right*side*(140+n*70),30+n*45,70+n*90,i);
                    }
                    else
                    {
                        if(side==1&&i%8==0)Model(chunk,"Quaternius",i%16==0?"CommonTree_1":"CommonTree_3",p[i]+right*(27+n*28),8+n*5,n*180);
                        if(i%16==0)Model(chunk,"Quaternius","Rock_Medium_2",p[i]+right*side*(29+n*20),2+n*5,n*360);
                        if(i%12==0)Model(chunk,"Quaternius","Bush_Common_Flowers",p[i]+right*side*19,1.2f+n,n*360);
                        if(i>30&&i<125&&i%8==0&&side==1)
                        {
                            var house=Model(chunk,"KenneySuburban",i%16==0?"building-type-a":"building-type-d",p[i]+right*(42+n*18),7+n*3,Mathf.Atan2(t.x,t.z)*Mathf.Rad2Deg-90);
                            var roof=house.transform.position;
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
                Model(chunk,"Quaternius","Rock_Medium_1",point+right*76,7,35);
            }
            if(place==2&&start>p.Length-75)
            {
                var point=p[start];var right=LongRaceRoads.Right(LongRaceRoads.Tangent(p,start,open));
                for(int j=0;j<4;j++)Model(chunk,"Quaternius","CommonTree_3",point+right*(27+j*9)+Vector3.forward*j*7,10+j,30*j);
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
            var mat=LongRaceRoads.Mat("MesaDistant","AF785C");
            var vertices=new System.Collections.Generic.List<Vector3>();var tris=new System.Collections.Generic.List<int>();
            const int sides=11;
            for(int ring=0;ring<5;ring++)for(int k=0;k<sides;k++)
            {
                float angle=k*Mathf.PI*2/sides;float radius=width*.5f*(1-ring*.105f)*( .85f+Noise(k,seed)*.3f);
                vertices.Add(chunk.InverseTransformPoint(p+new Vector3(Mathf.Cos(angle)*radius,ring*height*.25f,Mathf.Sin(angle)*radius*.75f)));
                if(ring<4){int n=ring*sides+k;int next=ring*sides+(k+1)%sides;tris.AddRange(new[]{n,n+sides,next,next,n+sides,next+sides});}
            }
            int top=vertices.Count;vertices.Add(chunk.InverseTransformPoint(p+Vector3.up*height));
            for(int k=0;k<sides;k++)tris.AddRange(new[]{top,4*sides+(k+1)%sides,4*sides+k});
            LongRaceRoads.MeshObject(chunk,chunk.parent.parent.name+"_Mesa"+seed,vertices,tris,null,mat,false);
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
