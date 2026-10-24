using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FamilyRacing.EditorTools
{
    public static class LongRaceQualification
    {
        [Serializable] private sealed class Result
        { public string course,vehicle,difficulty; public bool finished; public float seconds,maxSpeedKmh; public int gate,recoveries,boostTicks,longestBoostTicks; }
        [Serializable] private sealed class Report { public List<Result> runs=new List<Result>(); }
        public static void Run()
        {
            RunPresets(new AiDifficultyDefinition[] { null }, LongRaceAuthoring.Evidence+"/simulated-route-runs.json");
        }

        public static void RunBalance()
        {
            var catalog=LongRaceAuthoring.Required<GameplayCatalogAsset>(LongRaceAuthoring.Output+"/Campaign/grand-tour-catalog.asset");
            var presets=catalog.difficulties.Select(row=>new AiDifficultyDefinition {
                id=row.difficultyId,brakeMarginScale=row.brakeMarginScale,boostUseMinCharge=row.boostUseMinCharge,
                gadgetUseWillingness=row.gadgetUseWillingness,shortcutWillingness=row.shortcutWillingness }).ToArray();
            var label=Environment.GetEnvironmentVariable("FAMILY_RACING_BALANCE_LABEL")=="before"?"before":"after";
            RunPresets(presets,"docs/qa/turbo-ai-balance-2026-10-24/"+label+".json");
        }

        private static void RunPresets(AiDifficultyDefinition[] presets,string reportPath)
        {
            var root=Object.Instantiate(LongRaceAuthoring.Required<GameObject>(LongRaceRoads.PrefabPath));
            var report=new Report();
            try
            {
                var world=new UnityMotorWorld();
                for(int place=0;place<3;place++)
                {
                    root.GetComponent<LongRaceVisibility>().SelectCourse(LongRaceRoads.Ids[place]);Physics.SyncTransforms();
                    var track=LongRaceAuthoring.Required<TrackDefinitionAsset>(LongRaceRoads.TrackPath(place));
                    var course=track.ToCourse(out var error);if(course==null)throw new InvalidOperationException(error);
                    foreach(var id in new[]{"car-01","motorcycle-01"})
                    foreach(var preset in presets)
                    {
                        var tuning=LongRaceAuthoring.Required<VehicleDefinitionAsset>(LongRaceAuthoring.Output+"/"+id+".asset").ToTuning(out error);
                        var evt=new EventDefinition{id="qualification",courseId=course.id,laps=place==0?2:1,countdownTicks=0,
                            vehicleIds=new List<string>{id},recovery=new RecoverySettings{minCourseY=-40}};
                        var session=new DrivingSession("qualify",tuning,course,evt,"grand-tour-1","start");var ai=new AiDriver(0f,preset);
                        var state=session.Snapshot();float speed=0;int recoveries=0,boostTicks=0,burst=0,longestBurst=0;
                        for(int tick=0;tick<27000&&!state.finished;tick++)
                        {
                            var input=ai.Sample(state,course);
                            if(input.Recover)session.QueuePending("recover","recover-"+tick,out _);
                            var step=session.Step(input,world);state=step.state;
                            speed=Mathf.Max(speed,state.motor.speed*3.6f);
                            burst=state.motor.boostActive?burst+1:0;
                            if(state.motor.boostActive)boostTicks++;
                            longestBurst=Math.Max(longestBurst,burst);
                            foreach(var e in step.events)if(e.kind=="race.recovered")recoveries++;
                            if(tick%3000==0)Debug.Log("TOUR_SIM_PROGRESS "+course.id+" "+id+" tick="+tick+" gate="+state.nextCheckpoint+" x="+state.motor.x+" z="+state.motor.z);
                        }
                        report.runs.Add(new Result{course=course.id,vehicle=id,difficulty=preset?.id??"default",finished=state.finished,seconds=state.elapsedTicks/30f,maxSpeedKmh=speed,gate=state.nextCheckpoint,recoveries=recoveries,boostTicks=boostTicks,longestBoostTicks=longestBurst});
                        Debug.Log("TOUR_SIM_RESULT "+course.id+" "+id+" "+(preset?.id??"default")+" finished="+state.finished+" seconds="+state.elapsedTicks/30f+" maxKmh="+speed+" gate="+state.nextCheckpoint+" recoveries="+recoveries+" boostTicks="+boostTicks+" longestBurst="+longestBurst);
                    }
                }
            }
            finally
            {
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
                File.WriteAllText(reportPath,JsonUtility.ToJson(report,true));Object.DestroyImmediate(root);
            }
            foreach(var run in report.runs)if(!run.finished)throw new InvalidOperationException("AI did not finish "+run.course+" "+run.vehicle);
        }
    }
}
