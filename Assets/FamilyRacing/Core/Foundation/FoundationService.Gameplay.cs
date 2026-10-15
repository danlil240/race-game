using System;
using System.Collections.Generic;
using System.Globalization;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Foundation
{
    // Local gameplay-loop half of the foundation service (M5 Task 4):
    // Campaign, Quick Race and Time Trial run through SoloRaceSession —
    // the four-racer modes share the authoritative NetworkRace
    // coordinator, the trial reuses one DrivingSession — all under the
    // service's single input/pause boundary. Progression modes bind a
    // durable RaceRewardContext before the first simulated boundary and
    // retire it on abort; the finalized result feeds the same exactly-
    // once reward seam LAN uses. Practice keeps its legacy `driving`
    // path untouched.
    public sealed partial class FoundationService
    {
        private GameplayCatalog gameplayCatalog;
        // M5 Task 5: the authored gadget catalog for sessions that do
        // not gate on the gameplay catalog (practice, tutorial
        // free-drive). The gameplay catalog's gadget section wins when
        // bound — one authored source owns the numbers.
        private GadgetDefinitionCatalog gadgetCatalog;
        private SoloRaceSession soloRace;
        // The injected vehicle simulation the local race steps through:
        // separate ownership from the practice `drivingSimulation` even
        // though the two never coexist — one owner per session identity,
        // initialized with the session's own roster spawns.
        private IVehicleSimulation soloSimulation;
        private SoloRaceSession soloSimulationFor;
        // A finalized local result feeds the reward seam exactly once;
        // the flag is per-session because the immutable result object
        // stays observable on every later snapshot.
        private bool soloResultApplied;
        // The highest local-race ordinal this process has minted — seeded
        // from the profile-persisted floor (ProfileSave.localRaceOrdinal)
        // so a crashed run can never re-mint an identity a bound reward
        // context already owns.
        private int soloOrdinalHighWater;
        // M5 Task 7: a finalized result whose atomic progression
        // transaction FAILED to commit stays retained here with the exact
        // context it was bound under — the pair is what
        // RetryUnresolvedRewards replays and what blocks starting a new
        // progression race (or purchasing) until it resolves. Cleared on
        // any terminal verdict and on profile switches.
        private RaceResult unresolvedResult;
        private RaceRewardContext unresolvedContext;

        // True while a finalized result's progression transaction has a
        // retryable commit outstanding — the gate every progression-race
        // start and purchase must respect.
        public bool RewardUnresolved
        {
            get { return unresolvedResult != null; }
        }

        // Replays the retained immutable result against its ORIGINAL
        // bound context — the receipt dedup makes a double-commit
        // impossible and the durable context makes the fold identical.
        public RewardApplyResult RetryUnresolvedRewards()
        {
            if (unresolvedResult == null)
                return RewardApplyResult.Ineligible("noPendingResult");
            if (savedProfile == null)
                return RewardApplyResult.Ineligible("noProfile");
            var applied = RaceRewardService.Apply(unresolvedResult,
                savedProfile.id, savedProfile, store, gameplayCatalog,
                unresolvedContext);
            if (applied.status == RaceRewardService.StatusFailed)
                // Still unresolved — the identical inputs stay retained
                // for the next retry; nothing was guessed or dropped.
                return applied;
            unresolvedResult = null;
            unresolvedContext = null;
            if (applied.status == RaceRewardService.StatusApplied
                || applied.status == RaceRewardService.StatusAlreadyApplied)
                RefreshProgressionCaches();
            return applied;
        }

        // The runtime injects the validated gameplay content once —
        // solo.start reports contentUnavailable until a non-legacy
        // catalog arrives, exactly like race.start vs drivingContent.
        public void SetGameplayCatalog(GameplayCatalog catalog)
        {
            gameplayCatalog = catalog;
        }

        // The runtime injects the validated gadget content once —
        // practice/tutorial sessions and LAN races use it when no
        // gameplay catalog supplies a gadget section of its own.
        public void SetGadgetCatalog(
            GadgetDefinitionCatalog catalog)
        {
            gadgetCatalog = catalog;
        }

        // The local-mode catalog when one is bound (null otherwise) —
        // read-only seam for tests and the shell's mode pickers.
        public GameplayCatalog GameplayContent
        {
            get { return gameplayCatalog; }
        }

        // One simulated boundary through the injected vehicle
        // simulation for the active local race session — the same
        // prepare/shared-batch/complete seam practice uses, frozen while
        // paused. A missing simulation provider republishes unchanged
        // (the session is observable but cannot advance) rather than
        // manufacturing error events every tick.
        public void StepSolo()
        {
            if (soloRace == null || paused) return;
            var simulation = EnsureSoloSimulation();
            if (simulation == null) return;
            var state = soloRace.Step(input, simulation);
            AppendDrivingEvents(state.events);
            if (state.result != null) TryApplyLocalResult(state);
        }

        // Legacy call shape retained alongside StepDriving(IMotorWorld):
        // binds the motor world the runtime used to pass and steps
        // through the same seam via a lazily-wrapped arcade simulation.
        public void StepSolo(IMotorWorld world)
        {
            legacyMotorWorld = world;
            StepSolo();
        }

        // The simulation the active local session steps through: built
        // once per session from the injected factory (or the legacy
        // world adapter), initialized with the session's own roster
        // spawns, and disposed with the session.
        private IVehicleSimulation EnsureSoloSimulation()
        {
            if (soloRace == null) return null;
            if (soloSimulation != null && soloSimulationFor == soloRace)
                return soloSimulation;
            ReleaseSoloSimulation();
            if (gameplayCatalog == null || gameplayCatalog.Driving == null)
                return null;
            var spawns = soloRace.VehicleSpawns();
            // Last line of defense inside the tick path — the same
            // CatalogSeatsVehicle rule every session-creation boundary
            // enforces: a roster seat that cannot satisfy the race
            // backend never reaches the simulation.
            if (spawns != null)
                foreach (var spawn in spawns)
                    if (spawn != null && !CatalogSeatsVehicle(spawn.tuning))
                        return null;
            if (drivingSimulationFactory != null)
                soloSimulation = drivingSimulationFactory(
                    gameplayCatalog.Driving);
            else if (legacyMotorWorld != null)
                soloSimulation = new ArcadeVehicleSimulation(legacyMotorWorld);
            if (soloSimulation == null) return null;
            soloSimulation.Initialize(spawns);
            soloSimulationFor = soloRace;
            return soloSimulation;
        }

        private void ReleaseSoloSimulation()
        {
            if (soloSimulation != null)
                soloSimulation.Dispose();
            soloSimulation = null;
            soloSimulationFor = null;
        }

        // --- local mode commands ----------------------------------------

        // solo.start validates the whole selection through
        // SoloRaceSession.TryCreate — a rejected start leaves no
        // partially built state, exactly like NetworkRace.TryCreate.
        // Progression modes bind the durable reward context BEFORE the
        // session may step: a preparation save failure keeps the race
        // from starting and reports honestly (the same contract the LAN
        // preparation seam enforces).
        private CommandResult SoloStart(GameCommand command)
        {
            if (gameplayCatalog == null || gameplayCatalog.IsLegacy
                || gameplayCatalog.Driving == null)
                return Result(command, CommandResult.Rejected, "contentUnavailable");
            if (activeSlot < 0)
                return Result(command, CommandResult.Rejected, "unavailable");
            if (driving != null || soloRace != null)
                return Result(command, CommandResult.Rejected, "raceActive");
            var mode = command.Parameters["mode"];
            // Task 7: a progression race may not start while a finalized
            // result's transaction is still uncommitted — its fresh
            // context would interleave with the unresolved one.
            RaceMode requestedMode;
            if (unresolvedResult != null
                && RaceModeRules.TryParse(mode, out requestedMode)
                && RaceModeRules.IsProgression(requestedMode))
                return Result(command, CommandResult.Rejected,
                    "rewardUnresolved");
            var request = new SoloRaceRequest
            {
                mode = mode,
                eventId = OptionalParam(command, "eventId"),
                courseId = OptionalParam(command, "courseId"),
                classId = OptionalParam(command, "classId"),
                vehicleId = OptionalParam(command, "vehicleId"),
                difficultyId = OptionalParam(command, "difficulty"),
                participantId = savedProfile != null ? savedProfile.id : "",
                // Profile-salted, monotonically minted: the durable
                // ordinal floor keeps identities unique across process
                // restarts, so a crashed run's pending context can never
                // be silently adopted by a fresh "race-N".
                raceId = MintSoloRaceId(),
                commandId = command.CommandId
            };
            string error;
            SoloRaceSession created;
            if (!SoloRaceSession.TryCreate(request, savedProfile,
                    gameplayCatalog, out created, out error))
                return Result(command, CommandResult.Rejected, error);
            if (!BindRewardContextFor(request, created, command))
                return Result(command, CommandResult.Failed, "saveFailed");
            soloRace = created;
            soloResultApplied = false;
            screen = ScreenDriving;
            ClearInput();
            DrainSoloEvents();
            return Result(command, CommandResult.Applied, "");
        }

        // Mints a race identity that can never collide across process
        // restarts: the profile-id prefix namespaces the id to its owner
        // (also distinguishing it from the pre-salt "race-N" shape), the
        // ordinal rises monotonically above the profile-persisted floor,
        // and any ordinal already claimed by a pending context row is
        // skipped — a leftover row is superseded, never silently
        // adopted.
        private string MintSoloRaceId()
        {
            var owner = savedProfile != null
                && !string.IsNullOrEmpty(savedProfile.id)
                && savedProfile.id.Length >= 8
                ? savedProfile.id.Substring(0, 8) : "local";
            var floor = savedProfile != null
                ? savedProfile.localRaceOrdinal : 0;
            if (soloOrdinalHighWater < floor)
                soloOrdinalHighWater = floor;
            string id;
            do
            {
                id = "race-" + owner + "-" +
                    (++soloOrdinalHighWater).ToString(
                        CultureInfo.InvariantCulture);
            }
            while (HasPendingContext(id));
            return id;
        }

        private bool HasPendingContext(string raceId)
        {
            if (savedProfile == null
                || savedProfile.pendingRaceContexts == null)
                return false;
            foreach (var row in savedProfile.pendingRaceContexts)
                if (row != null && row.raceId == raceId) return true;
            return false;
        }

        // Binds the reward context for progression races. Returns true
        // when the session may start: non-progression modes bind nothing,
        // a bound context is a durable no-op, and any save failure
        // refuses the start (the session was never published).
        private bool BindRewardContextFor(SoloRaceRequest request,
            SoloRaceSession session, GameCommand command)
        {
            RaceMode mode;
            if (!RaceModeRules.TryParse(request.mode, out mode)
                || !RaceModeRules.IsProgression(mode))
                return true;
            var bindError = BindRewardContext(session.RaceId,
                RaceModeRules.ModeId(mode),
                gameplayCatalog.CompatibilityFingerprint);
            if (bindError == null) return true;
            // The race may not start unbound: release the never-stepped
            // session and report the preparation failure honestly.
            persistenceStatus = bindError;
            return false;
        }

        // The shared restart affordance: the local session's stored
        // request replays under a fresh race identity — the retired
        // session's context is dropped with it (restart abandons the
        // run), the new identity binds fresh.
        private CommandResult SoloRestart(GameCommand command)
        {
            var stored = soloRace.Request;
            if (stored == null)
                return Result(command, CommandResult.Rejected, "unavailable");
            stored.raceId = MintSoloRaceId();
            stored.commandId = command.CommandId;
            string error;
            SoloRaceSession created;
            if (!SoloRaceSession.TryCreate(stored, savedProfile,
                    gameplayCatalog, out created, out error))
            {
                EndSoloRace("restarted", command);
                return Result(command, CommandResult.Rejected, error);
            }
            // The old identity ends first — its lifecycle events and
            // context retirement publish on this boundary before the
            // replacement session's facts.
            EndSoloRace("restarted", command);
            if (!BindRewardContextFor(stored, created, command))
            {
                ReleaseSoloSimulation();
                return Result(command, CommandResult.Failed, "saveFailed");
            }
            soloRace = created;
            soloResultApplied = false;
            screen = ScreenDriving;
            ClearInput();
            DrainSoloEvents();
            return Result(command, CommandResult.Applied, "");
        }

        // Ends the active local session: latched actions cancel with
        // their reason, the abort path guarantees no rewardable result,
        // the unused reward context retires inside one save, held input
        // neutralizes and the owned simulation releases. The driving
        // screen falls back to practice.
        private void EndSoloRace(string reason, GameCommand command)
        {
            if (soloRace == null) return;
            var session = soloRace;
            session.CancelPending(reason);
            session.Abort(reason);
            AppendDrivingEvents(session.DrainStagedEvents());
            // Abandoned runs retire their unused context immediately;
            // a finalized race's context stays for Task 7's atomic
            // reward evaluation — it is not "unused".
            if (session.Result == null)
                RetireRewardContext(session.RaceId);
            AppendSoloEndEvent(session, reason,
                command != null ? command.CommandId : "");
            soloRace = null;
            ReleaseSoloSimulation();
            if (screen == ScreenDriving) screen = ScreenPractice;
            paused = false;
            ClearInput();
        }

        // The same lifecycle vocabulary the practice path emits: an
        // explicit exit publishes "race.exited", every other end
        // (restart, profile switch, lifecycle) publishes "race.ended" —
        // correlated to the command that produced it.
        private void AppendSoloEndEvent(SoloRaceSession session,
            string reason, string commandId)
        {
            var kind = reason == "raceExit" ? "race.exited" : "race.ended";
            AppendDrivingEvents(new[]
            {
                new DrivingEvent
                {
                    id = session.SessionId + "-end",
                    tick = tick,
                    kind = kind,
                    commandId = commandId ?? "",
                    detail = reason ?? "",
                    participantId = "",
                    attribution = ""
                }
            });
        }

        private void DrainSoloEvents()
        {
            if (soloRace == null) return;
            AppendDrivingEvents(soloRace.DrainStagedEvents());
        }

        // A finalized local result feeds the same exactly-once reward
        // seam LAN results use — progression modes against their durably
        // bound context, time trials against the record table (trials
        // bind no context; the result's own fingerprinted course/class
        // facts key its personal best). Aborted races never produce a
        // result at all.
        private void TryApplyLocalResult(SoloRaceState state)
        {
            if (soloResultApplied || state == null || state.result == null)
                return;
            soloResultApplied = true;
            RaceMode mode;
            if (!RaceModeRules.TryParse(state.mode, out mode)
                || (mode != RaceMode.TimeTrial
                    && !RaceModeRules.IsProgression(mode)))
                return;
            var applied = ApplyNetworkResult(state.result);
            if (applied != null && applied.status == RaceRewardService.StatusFailed)
                persistenceStatus = "saveFailed";
        }

        // The durable reward context bound for one race identity, or
        // null — the row the Task-7 transaction consumes atomically.
        private RaceRewardContext FindRewardContext(string raceId)
        {
            if (savedProfile == null
                || savedProfile.pendingRaceContexts == null
                || raceId == null)
                return null;
            foreach (var row in savedProfile.pendingRaceContexts)
                if (row != null && row.raceId == raceId) return row;
            return null;
        }

        // Slot cache + settings draft refresh after a committed reward —
        // the draft's progression slice tracks the live profile so a
        // pending settings save can never roll progression back.
        private void RefreshProgressionCaches()
        {
            if (activeSlot >= 0)
                slots[activeSlot].Profile = ProfileCopy.DeepCopy(savedProfile);
            if (draftProfile != null)
                ProfileCopy.CopyProgression(savedProfile, draftProfile);
        }

        // --- reward-context binding -------------------------------------
        // The durable binding between one race and the reward rules it
        // was started under: idempotent per race identity, written
        // through the normal atomic save path. Active bounty assignments
        // capture WITH their catalog definitions — the parallel lists
        // the schema requires; an assignment whose definition is absent
        // binds nothing rather than fabricating a definition. Peer
        // participants never see these definitions: the context lives in
        // the local profile file only.

        private string BindRewardContext(string raceId, string mode,
            string rulesFingerprint)
        {
            if (string.IsNullOrEmpty(raceId)) return null;
            if (savedProfile == null || activeSlot < 0) return "noProfile";
            if (!WritableSelected()) return "readOnlyProfile";
            // Task 7: a new bound context may not interleave with a
            // finalized result whose transaction is still uncommitted.
            if (unresolvedResult != null) return "rewardUnresolved";
            if (string.IsNullOrEmpty(rulesFingerprint))
                return "contentUnavailable";
            if (savedProfile.pendingRaceContexts != null)
                foreach (var row in savedProfile.pendingRaceContexts)
                    if (row != null && row.raceId == raceId) return null;
            var context = new RaceRewardContext
            {
                raceId = raceId,
                profileId = savedProfile.id,
                mode = mode ?? "",
                rulesFingerprint = rulesFingerprint
            };
            if (savedProfile.activeBounties != null
                && gameplayCatalog != null && gameplayCatalog.Bounties != null)
                foreach (var assignment in savedProfile.activeBounties)
                {
                    if (assignment == null
                        || string.IsNullOrEmpty(assignment.assignmentId))
                        continue;
                    var definition = FindBounty(assignment.bountyId);
                    if (definition == null) continue;
                    context.bountyAssignmentIds.Add(assignment.assignmentId);
                    context.bountyDefinitions.Add(definition.Copy());
                }
            var candidate = ProfileCopy.DeepCopy(savedProfile);
            candidate.pendingRaceContexts.Add(context);
            // The minted-ordinal high-water lands in the same atomic save
            // as the context row: a crashed process can never re-mint an
            // identity a bound context already owns.
            if (candidate.localRaceOrdinal < soloOrdinalHighWater)
                candidate.localRaceOrdinal = soloOrdinalHighWater;
            try
            {
                store.Save(candidate);
            }
            catch (Exception)
            {
                return "saveFailed";
            }
            savedProfile = ProfileCopy.DeepCopy(candidate);
            slots[activeSlot].Profile = ProfileCopy.DeepCopy(candidate);
            if (draftProfile != null)
                ProfileCopy.CopyProgression(savedProfile, draftProfile);
            persistenceStatus = "saved";
            return null;
        }

        // Drops the pending context for one race identity inside one
        // atomic save — used by abort paths. A failure reports through
        // persistenceStatus; the row simply stays pending for the next
        // retirement attempt.
        private void RetireRewardContext(string raceId)
        {
            if (savedProfile == null || activeSlot < 0
                || string.IsNullOrEmpty(raceId)) return;
            var found = false;
            if (savedProfile.pendingRaceContexts != null)
                foreach (var row in savedProfile.pendingRaceContexts)
                    if (row != null && row.raceId == raceId)
                    { found = true; break; }
            if (!found) return;
            var candidate = ProfileCopy.DeepCopy(savedProfile);
            candidate.pendingRaceContexts.RemoveAll(
                row => row != null && row.raceId == raceId);
            try
            {
                store.Save(candidate);
            }
            catch (Exception)
            {
                persistenceStatus = "saveFailed";
                return;
            }
            savedProfile = ProfileCopy.DeepCopy(candidate);
            slots[activeSlot].Profile = ProfileCopy.DeepCopy(candidate);
            if (draftProfile != null)
                ProfileCopy.CopyProgression(savedProfile, draftProfile);
            persistenceStatus = "saved";
        }

        private BountyDefinition FindBounty(string bountyId)
        {
            if (gameplayCatalog == null || gameplayCatalog.Bounties == null
                || bountyId == null) return null;
            foreach (var definition in gameplayCatalog.Bounties)
                if (definition != null && definition.id == bountyId)
                    return definition;
            return null;
        }

        // The LAN preparation seam (Task 4): the session service calls
        // this with the host-allocated race identity before the race
        // clock may start — the local reward context binds durably or
        // the returned error keeps this participant out of the race.
        // The rules fingerprint is the gameplay catalog's when bound
        // and the driving catalog's otherwise; neither case fabricates
        // a fingerprint.
        private string PrepareLanRewardContext(string allocatedRaceId)
        {
            var fingerprint = gameplayCatalog != null
                ? gameplayCatalog.CompatibilityFingerprint
                : drivingContent != null
                    ? drivingContent.CompatibilityFingerprint : "";
            var error = BindRewardContext(allocatedRaceId, "lan", fingerprint);
            // A missing profile/catalog is honest unavailability: the
            // participant cannot bind, so it cannot start.
            return error;
        }

        private static string OptionalParam(GameCommand command, string name)
        {
            string value;
            return command.Parameters.TryGetValue(name, out value)
                ? value ?? "" : "";
        }
    }
}
