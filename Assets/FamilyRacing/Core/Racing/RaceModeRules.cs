using System;

namespace FamilyRacing.Core.Racing
{
    // The race-mode vocabulary shared by local modes, reward contexts
    // and results. `Practice` is the legacy free-drive lane; `Campaign`
    // and `QuickRace` seat one human against three same-class AI through
    // the shared four-racer coordinator; `TimeTrial` is a lone
    // normalized-starter run against the clock; `Lan` is the networked
    // session mode (never creatable through SoloRaceSession).
    public enum RaceMode
    {
        Practice = 0,
        Campaign = 1,
        QuickRace = 2,
        TimeTrial = 3,
        Lan = 4
    }

    // String identity + progression policy for the mode vocabulary.
    // Progression modes bind a durable RaceRewardContext at race start;
    // practice and time-trial never earn rewards and bind nothing.
    public static class RaceModeRules
    {
        public const string DefaultDifficultyId = "easy";

        public static string ModeId(RaceMode mode)
        {
            switch (mode)
            {
                case RaceMode.Practice: return "practice";
                case RaceMode.Campaign: return "campaign";
                case RaceMode.QuickRace: return "quickRace";
                case RaceMode.TimeTrial: return "timeTrial";
                case RaceMode.Lan: return "lan";
                default: return "";
            }
        }

        public static bool TryParse(string value, out RaceMode mode)
        {
            switch (value)
            {
                case "practice": mode = RaceMode.Practice; return true;
                case "campaign": mode = RaceMode.Campaign; return true;
                case "quickRace": mode = RaceMode.QuickRace; return true;
                case "timeTrial": mode = RaceMode.TimeTrial; return true;
                case "lan": mode = RaceMode.Lan; return true;
                default: mode = RaceMode.Practice; return false;
            }
        }

        // Modes whose finished human results may award progression:
        // coins, challenge-bounty progress and reward receipts.
        public static bool IsProgression(RaceMode mode)
        {
            return mode == RaceMode.Campaign || mode == RaceMode.QuickRace
                || mode == RaceMode.Lan;
        }
    }
}
