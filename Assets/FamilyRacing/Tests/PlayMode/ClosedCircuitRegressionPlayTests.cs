using System.Linq;
using FamilyRacing.Core.Driving;
using FamilyRacing.EditorTools;
using NUnit.Framework;
using UnityEngine;

namespace FamilyRacing.Tests
{
    [UnityEngine.TestTools.UnityPlatform(RuntimePlatform.WindowsEditor)]
    public sealed class ClosedCircuitRegressionPlayTests
    {
        private GameObject geometry;
        [SetUp] public void SetUp()
        {
#if UNITY_EDITOR
            geometry = Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(AssetTrialCircuits.PrefabPath));
#endif
            Physics.SyncTransforms();
        }
        [TearDown] public void TearDown() { Object.DestroyImmediate(geometry); }

        [Test] public void EveryGridSeatIsPavedAndClearOfFences()
        {
#if UNITY_EDITOR
            var world = new UnityMotorWorld();
            for (int course = 0; course < 3; course++)
            {
                var track = UnityEditor.AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(AssetTrialCircuits.TrackPath(course));
                foreach (var slot in track.spawnSlots)
                {
                    var p = slot.position;
                    var pose = new MotorPose { x=p.x, y=p.y, z=p.z, heading=slot.headingDegrees*Mathf.Deg2Rad };
                    Assert.That(world.ProbeGround(pose,.8f).offRoad, Is.False, track.courseId + " grid seat is off the road");
                    var blockers = Physics.OverlapBox(p+Vector3.up*.6f,new Vector3(.95f,.48f,1.65f),Quaternion.Euler(0,slot.headingDegrees,0));
                    Assert.That(blockers.Any(c=>c.name.StartsWith("InnerRail_")||c.name.StartsWith("OuterRail_")||c.name=="FinishPost"), Is.False,
                        track.courseId + " grid seat overlaps a fence");
                }
            }
#endif
        }

        [Test] public void ReturningFromLowerShoulderFindsTarmacAtEveryRoutePoint()
        {
#if UNITY_EDITOR
            var world = new UnityMotorWorld();
            for (int course=0;course<3;course++)
            {
                var track=UnityEditor.AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(AssetTrialCircuits.TrackPath(course));
                foreach(var point in track.aiRoute)
                {
                    // A car's feet are still at the rough-ground height on the
                    // first step back onto the higher road ribbon.
                    var ground=world.ProbeGround(new MotorPose{x=point.x,y=-.06f,z=point.z},.8f);
                    Assert.That(ground.hit && !ground.offRoad,Is.True,track.courseId+" rejoin keeps reading rough ground");
                    Assert.That(ground.y,Is.EqualTo(.065f).Within(.003f));
                }
            }
#endif
        }
    }
}
