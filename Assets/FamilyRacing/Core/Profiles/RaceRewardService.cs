using System;
using System.Collections.Generic;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Profiles
{
    // Terminal outcome of attempting to apply one immutable finalized
    // result to the local profile. `awardedCoins` echoes the authored
    // reward the attempt recognized — including on alreadyApplied, where
    // no coins moved but the original award is reported.
    public sealed class RewardApplyResult
    {
        public string status = "";
        public string code = "";
        public int awardedCoins;
        public string fingerprint = "";

        public static RewardApplyResult Applied(int coins, string fingerprint)
        {
            return new RewardApplyResult
            {
                status = RaceRewardService.StatusApplied,
                awardedCoins = coins, fingerprint = fingerprint ?? ""
            };
        }

        public static RewardApplyResult AlreadyApplied(int coins, string fingerprint)
        {
            return new RewardApplyResult
            {
                status = RaceRewardService.StatusAlreadyApplied,
                awardedCoins = coins, fingerprint = fingerprint ?? ""
            };
        }

        public static RewardApplyResult Failed(string code, string fingerprint = "")
        {
            return new RewardApplyResult
            {
                status = RaceRewardService.StatusFailed,
                code = code ?? "", fingerprint = fingerprint ?? ""
            };
        }

        public static RewardApplyResult Ineligible(string code, string fingerprint = "")
        {
            return new RewardApplyResult
            {
                status = RaceRewardService.StatusIneligible,
                code = code ?? "", fingerprint = fingerprint ?? ""
            };
        }
    }

    // Exactly-once application of finalized LAN race rewards to the
    // selected profile. The M3 reward rule is deliberately minimal:
    // authored completion coins for finishing the prescribed laps in a
    // finalized race — no placement bonus, stars, or bounties.
    //
    // Coins and the applied-result receipt are written together inside
    // one atomic ProfileStore.Save, so persistence is all-or-nothing:
    //   - a failed save leaves memory and disk untouched
    //   - a replay of the identical result returns alreadyApplied
    //   - a replay of the same resultId with conflicting immutable facts
    //     is rejected by fingerprint comparison — durable across restarts
    //   - the local profile identity binds the award: a result meant for
    //     another participant can never be claimed here
    public static class RaceRewardService
    {
        public const string StatusApplied = "applied";
        public const string StatusAlreadyApplied = "alreadyApplied";
        public const string StatusFailed = "failed";
        public const string StatusIneligible = "ineligible";

        public static RewardApplyResult Apply(RaceResult result,
            string participantId, ProfileSave savedProfile, ProfileStore store)
        {
            var fingerprint = result != null ? result.Fingerprint() : "";
            if (result == null || !result.finalized)
                return RewardApplyResult.Ineligible("noResult", fingerprint);
            if (savedProfile == null || store == null)
                return RewardApplyResult.Ineligible("noProfile", fingerprint);
            // The reward is bound to the intended local profile: only the
            // result row that names this profile's identity can pay out.
            if (string.IsNullOrEmpty(participantId)
                || participantId != savedProfile.id)
                return RewardApplyResult.Ineligible("foreignProfile", fingerprint);
            RaceResultEntry entry = null;
            if (result.racers != null)
                foreach (var racer in result.racers)
                    if (racer != null && racer.participantId == participantId)
                        entry = racer;
            if (entry == null)
                return RewardApplyResult.Ineligible("notInResult", fingerprint);
            if (entry.outcome != "finished")
                return RewardApplyResult.Ineligible("dnf", fingerprint);

            var receipt = FindReceipt(savedProfile, result.resultId);
            if (receipt != null)
            {
                if (receipt.fingerprint == fingerprint)
                    return RewardApplyResult.AlreadyApplied(
                        receipt.awardedCoins, fingerprint);
                // Same result id, different immutable facts: the stored
                // fingerprint is the proof — never pay or overwrite.
                return RewardApplyResult.Failed("conflict", fingerprint);
            }

            // The authored completion reward comes solely from finalized
            // host facts — never from client-supplied claims.
            var awarded = result.completionCoins;
            if (awarded < 0)
                return RewardApplyResult.Ineligible("invalidResult", fingerprint);
            if ((long)savedProfile.coins + awarded > int.MaxValue)
                return RewardApplyResult.Failed("overflow", fingerprint);

            // Mutate a candidate copy: the live profile and the disk both
            // stay exactly as they were unless the atomic write commits.
            var candidate = Copy(savedProfile);
            candidate.coins += awarded;
            candidate.appliedResults.Add(new AppliedResultReceipt
            {
                resultId = result.resultId,
                participantId = participantId,
                awardedCoins = awarded,
                fingerprint = fingerprint
            });
            try
            {
                store.Save(candidate);
            }
            catch (Exception)
            {
                return RewardApplyResult.Failed("saveFailed", fingerprint);
            }
            // Commit to the live profile only after the write lands so a
            // retry sees the pre-award state the disk actually holds.
            savedProfile.coins = candidate.coins;
            savedProfile.appliedResults = candidate.appliedResults;
            return RewardApplyResult.Applied(awarded, fingerprint);
        }

        private static AppliedResultReceipt FindReceipt(ProfileSave profile,
            string resultId)
        {
            if (profile.appliedResults == null || resultId == null) return null;
            foreach (var receipt in profile.appliedResults)
                if (receipt != null && receipt.resultId == resultId)
                    return receipt;
            return null;
        }

        private static ProfileSave Copy(ProfileSave profile)
        {
            var copy = new ProfileSave
            {
                schemaVersion = profile.schemaVersion,
                id = profile.id,
                slot = profile.slot,
                avatar = profile.avatar,
                colorIndex = profile.colorIndex,
                language = profile.language,
                camera = profile.camera,
                reducedCameraShake = profile.reducedCameraShake,
                musicVolume = profile.musicVolume,
                effectsVolume = profile.effectsVolume,
                voiceVolume = profile.voiceVolume,
                coins = profile.coins,
                unlockedVehicles = profile.unlockedVehicles == null
                    ? null : new List<string>(profile.unlockedVehicles),
                appliedResults = new List<AppliedResultReceipt>()
            };
            if (profile.appliedResults != null)
                foreach (var receipt in profile.appliedResults)
                    copy.appliedResults.Add(
                        receipt != null ? receipt.Copy() : null);
            return copy;
        }
    }
}
