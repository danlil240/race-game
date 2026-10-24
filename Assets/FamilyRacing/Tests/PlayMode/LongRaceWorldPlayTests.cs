using System.Linq;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Networking;
using FamilyRacing.EditorTools;
using NUnit.Framework;
using UnityEngine;

namespace FamilyRacing.Tests
{
    [UnityEngine.TestTools.UnityPlatform(RuntimePlatform.WindowsEditor)]
    public sealed class LongRaceWorldPlayTests
    {
        [TestCase(false)] [TestCase(true)]
        public void AuthoredTurboProvidesFiveSecondsOfUsefulSpeedWithoutSpendingExtraMeter(bool vehicleBoost)
        {
#if UNITY_EDITOR
            var catalog=LongRaceAuthoring.Required<GadgetCatalogAsset>("Assets/FamilyRacing/Generated/Driving/gadget-catalog-01.asset").ToCatalog(out var error);
            Assert.That(catalog,Is.Not.Null,error);
            var tuning=LongRaceAuthoring.Required<VehicleDefinitionAsset>(LongRaceAuthoring.Output+"/car-01.asset").ToTuning(out error);
            var gadgets=GadgetRules.Create(catalog,null,1,new[]{"driver"});
            gadgets.Racer("driver").heldItemId="turbo-01";
            gadgets=GadgetRules.Step(new GadgetStepInput { tick=1,racers=new System.Collections.Generic.List<GadgetRacerInput> {
                new GadgetRacerInput { participantId="driver",gadgetHeld=true } } },gadgets,catalog).state;
            var road=GameObject.CreatePrimitive(PrimitiveType.Cube);
            road.name="TurboTestRoad";road.transform.position=new Vector3(0,-.5f,500);road.transform.localScale=new Vector3(40,1,1200);
            try
            {
                Physics.SyncTransforms();
                var world=new UnityMotorWorld();
                var motor=new MotorState { grounded=true,speed=tuning.maxSpeed,boostCharge=1f };
                var baseline=motor.Copy();
                var input=new DriverInput(0,false,vehicleBoost,false,false,false,false);
                for(int tick=0;tick<150;tick++)
                {
                    motor=ArcadeMotor.Step(motor,input,tuning,world,1f/30f,null,GadgetRules.ModifierFor(gadgets,"driver")).state;
                    baseline=ArcadeMotor.Step(baseline,input,tuning,world,1f/30f).state;
                    Assert.That(motor.boostCharge,Is.EqualTo(baseline.boostCharge).Within(.0001f),"pickup must not drain extra rechargeable boost");
                    if(tick==45||tick==120||tick==149)
                        Assert.That(motor.speed,Is.GreaterThan(baseline.speed+8f),"pickup must deliver a substantial speed gain throughout its five seconds, including with vehicle turbo");
                    gadgets=GadgetRules.Step(new GadgetStepInput { tick=tick+2,racers=new System.Collections.Generic.List<GadgetRacerInput> {
                        new GadgetRacerInput { participantId="driver" } } },gadgets,catalog).state;
                }
                var expired=GadgetRules.ModifierFor(gadgets,"driver");
                Assert.That(expired.speedCapMultiplier,Is.EqualTo(1f),"pickup must expire after five seconds");
                Assert.That(expired.accelerationMultiplier,Is.EqualTo(1f));
                var after=ArcadeMotor.Step(motor,input,tuning,world,1f/30f,null,expired).state;
                Assert.That(after.speed,Is.LessThan(motor.speed),"normal motor drag resumes after pickup expiry");
            }
            finally { Object.DestroyImmediate(road); }
#endif
        }

        [Test] public void EveryLongCourseFitsTheExistingLanSnapshotBudget()
        {
#if UNITY_EDITOR
            var tuning=LongRaceAuthoring.Required<VehicleDefinitionAsset>(LongRaceAuthoring.Output+"/car-01.asset").ToTuning(out _);
            var gadgets=LongRaceAuthoring.Required<GadgetCatalogAsset>("Assets/FamilyRacing/Generated/Driving/gadget-catalog-01.asset").ToCatalog(out _);
            for(int place=0;place<3;place++)
            {
                var course=LongRaceAuthoring.Required<TrackDefinitionAsset>(LongRaceRoads.TrackPath(place)).ToCourse(out _);
                var evt=new EventDefinition{id="wire-test",courseId=course.id,laps=place==0?2:1,vehicleIds=new System.Collections.Generic.List<string>{"car-01"}};
                var content=DrivingContentCatalog.Create("tour",new[]{tuning},new[]{course},new[]{evt});
                var seats=Enumerable.Range(0,4).Select(i=>new NetworkRacerSpec{participantId="player-"+i,slot=i,vehicleId="car-01",controlSource="ai"}).ToArray();
                Assert.That(NetworkRace.TryCreate("session","race",content,evt.id,"car",seats,out var race,out var error,gadgets),Is.True,error);
                var snapshot=new NetworkSnapshot{sessionId="session",raceId="race",state=race.Snapshot()};
                Assert.That(NetworkWireCodec.TryEncodeSnapshot(snapshot,out var packet,out error),Is.True,course.id+" "+error);
                Assert.That(packet.Length,Is.LessThanOrEqualTo(NetworkSettings.SnapshotCeilingBytes));
                Debug.Log("TOUR_WIRE_SIZE "+course.id+" "+packet.Length);
            }
#endif
        }
        [Test] public void TimeTrialCameraFollowsTheTrialRider()
        {
            var cameraObject=new GameObject("TrialCamera");cameraObject.tag="MainCamera";cameraObject.AddComponent<Camera>();
            var controller=cameraObject.AddComponent<DrivingCamera>();
            try
            {
                var state=new FoundationState { soloRace=new SoloRaceState {mode="timeTrial",trial=new DrivingSessionState {
                    motor=new MotorState{x=15000,y=2,z=100,grounded=true} } } };
                controller.ApplyFrame(state);
                Assert.That(controller.Facts.mode,Is.EqualTo("chase"));
                Assert.That(controller.Facts.px,Is.GreaterThan(14900));
            }
            finally{Object.DestroyImmediate(cameraObject);}
        }
        [TestCase("quickRace")] [TestCase("campaign")] [TestCase("timeTrial")]
        public void LocalModesAreMeasuredAsRacingRatherThanMenu(string mode)
        {
            var state=new FoundationState { soloRace=new SoloRaceState { mode=mode,phase="running",participantId="local",
                trial=mode=="timeTrial"?new DrivingSessionState{vehicleId="car-01",phase="running"}:null,
                race=mode!="timeTrial"?new NetworkRaceState{racers=new System.Collections.Generic.List<NetworkRacerState>{
                    new NetworkRacerState{participantId="local",vehicleId="car-01"}}}:null } };
            var flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
            Assert.That(typeof(FoundationApp).GetMethod("PerfPhase",flags).Invoke(null,new object[]{state}),Is.EqualTo("racing"));
            Assert.That(typeof(FoundationApp).GetMethod("PerfVehicleClass",flags).Invoke(null,new object[]{state}),Is.EqualTo("car-01"));
        }
        [Test] public void BatchingNestedModelRenderersDoesNotAccessDestroyedChildren()
        {
#if UNITY_EDITOR
            var parent=new GameObject("BatchTest");
            try
            {
                var a=GameObject.CreatePrimitive(PrimitiveType.Cube);a.transform.SetParent(parent.transform);
                var b=GameObject.CreatePrimitive(PrimitiveType.Cube);b.transform.SetParent(a.transform);
                Assert.DoesNotThrow(()=>LongRaceRoads.Batch(parent.transform,"BatchRegression"));
                var groups=parent.GetComponent<TourSceneryInstances>().groups;
                Assert.That(groups.Length,Is.EqualTo(1));
                Assert.That(groups[0].localMatrices.Length,Is.EqualTo(2));
                Assert.That(parent.GetComponentsInChildren<MeshRenderer>(),Is.Empty);
            }
            finally {Object.DestroyImmediate(parent);UnityEditor.AssetDatabase.DeleteAsset(LongRaceRoads.Dir+"/BatchRegression_Default-Material.asset");}
#endif
        }
        [Test] public void CustomWaterKeepsItsNonInstancedRenderer()
        {
            var parent=new GameObject("WaterBatchTest");
            var mat=new Material(Shader.Find("FamilyRacing/Trial Coastal Water"));
            try
            {
                var water=GameObject.CreatePrimitive(PrimitiveType.Cube);water.transform.SetParent(parent.transform);
                water.GetComponent<Renderer>().sharedMaterial=mat;
                LongRaceRoads.Batch(parent.transform,"WaterBatchRegression");
                Assert.That(parent.GetComponentsInChildren<MeshRenderer>().Length,Is.EqualTo(1));
                Assert.That(parent.GetComponent<TourSceneryInstances>().groups,Is.Empty);
            }
            finally{Object.DestroyImmediate(parent);Object.DestroyImmediate(mat);}
        }
        [Test] public void LongRoutesHaveContinuousPavedGridAndRecoveryPoses()
        {
#if UNITY_EDITOR
            var geometry=Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(LongRaceRoads.PrefabPath));
            try
            {
                Physics.SyncTransforms();var world=new UnityMotorWorld();
                for(int i=0;i<3;i++)
                {
                    var track=UnityEditor.AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(LongRaceRoads.TrackPath(i));
                    foreach(var pose in track.spawnSlots)
                    {
                        var hit=world.ProbeGround(new MotorPose{x=pose.position.x,y=pose.position.y+.3f,z=pose.position.z},2f);
                        Assert.That(hit.hit&&!hit.offRoad,Is.True,track.courseId+" grid");
                    }
                    foreach(var gate in track.gates)
                    {
                        var pose=gate.recoverPosition;
                        var hit=world.ProbeGround(new MotorPose{x=pose.x,y=pose.y+.3f,z=pose.z},2f);
                        Assert.That(hit.hit&&!hit.offRoad,Is.True,track.courseId+" recovery "+gate.id);
                    }
                    foreach(var p in LongRaceRoads.Sample(i))
                        Assert.That(world.ProbeGround(new MotorPose{x=p.x,y=p.y+.3f,z=p.z},2f).hit,Is.True,track.courseId+" continuous road");
                }
            }
            finally{Object.DestroyImmediate(geometry);}
#endif
        }
        [Test] public void SelectingAWorldCullsOnlyOtherCourseRoots()
        {
            var root=new GameObject("World");
            try
            {
                foreach(var id in LongRaceRoads.Ids)new GameObject(id).transform.SetParent(root.transform);
                root.AddComponent<LongRaceVisibility>().SelectCourse(LongRaceRoads.Ids[1]);
                Assert.That(root.transform.Cast<Transform>().Count(t=>t.gameObject.activeSelf),Is.EqualTo(1));
                Assert.That(root.transform.Find(LongRaceRoads.Ids[1]).gameObject.activeSelf,Is.True);
            }
            finally{Object.DestroyImmediate(root);}
        }
    }
}
