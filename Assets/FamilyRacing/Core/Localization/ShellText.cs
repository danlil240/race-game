using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Localization
{
    // Static bilingual shell catalog. Hebrew strings are stored in normal
    // logical order; right-to-left layout is the caller's responsibility.
    public static class ShellText
    {
        private const int English = 0;
        private const int Hebrew = 1;

        private static readonly Dictionary<string, string[]> Catalog = new Dictionary<string, string[]>
        {
            ["shell.title"] = new[] { "Family Racing", "מירוץ משפחתי" },
            ["shell.disclaimer"] = new[] { "Foundation preview: profiles, settings, and controls only — no racing yet.", "גרסת יסודות בלבד: פרופילים, הגדרות ופקדים — עדיין אין מירוץ." },
            ["profile.select"] = new[] { "Choose your driver", "בחרו נהג" },
            ["profile.animal.fox"] = new[] { "Fox", "שועל" },
            ["profile.animal.rabbit"] = new[] { "Rabbit", "ארנב" },
            ["profile.animal.panda"] = new[] { "Panda", "פנדה" },
            ["profile.animal.raccoon"] = new[] { "Raccoon", "דביבון" },
            ["settings.language"] = new[] { "Language", "שפה" },
            ["settings.camera"] = new[] { "Camera", "מצלמה" },
            ["settings.camera.chase"] = new[] { "Chase camera", "מצלמת מעקב" },
            ["settings.camera.overhead"] = new[] { "Overhead camera", "מצלמה מלמעלה" },
            ["settings.reducedShake"] = new[] { "Reduce camera shake", "הפחתת רעידת מצלמה" },
            ["settings.volume.music"] = new[] { "Music", "מוזיקה" },
            ["settings.volume.effects"] = new[] { "Sound effects", "אפקטים" },
            ["settings.volume.voice"] = new[] { "Voice", "דיבור" },
            ["sandbox.title"] = new[] { "Input sandbox", "בדיקת פקדים" },
            ["input.steering"] = new[] { "Steering", "היגוי" },
            ["input.drift"] = new[] { "Drift", "דריפט" },
            ["input.boost"] = new[] { "Boost", "האצה" },
            ["input.gadget"] = new[] { "Gadget", "גאדג'ט" },
            ["input.recovery"] = new[] { "Recovery", "חזרה למסלול" },
            ["input.pause"] = new[] { "Pause", "השהיה" },
            ["practice.open"] = new[] { "Go driving", "לנהיגה" },
            ["practice.title"] = new[] { "Driving practice", "אימון נהיגה" },
            ["practice.hint"] = new[] { "Pick a vehicle to start a practice run.", "בחרו רכב כדי להתחיל אימון." },
            ["practice.car"] = new[] { "Rally car", "מכונית ראלי" },
            ["practice.motorcycle"] = new[] { "Motorcycle", "אופנוע" },
            ["practice.tutorial"] = new[] { "Learn to drive", "לומדים לנהוג" },
            ["vehicle.car-01"] = new[] { "Rally car", "מכונית ראלי" },
            ["vehicle.motorcycle-01"] = new[] { "Motorcycle", "אופנוע" },
            ["tutorial.lesson"] = new[] { "Lesson", "שיעור" },
            ["tutorial.replay"] = new[] { "Hear it again", "שמעו שוב" },
            ["tutorial.exit"] = new[] { "Exit lesson", "יציאה מהשיעור" },
            ["tutorial.cue.steering"] = new[] { "Hold ◀ or ▶ to steer!", "החזיקו ◀ או ▶ כדי לפנות!" },
            ["tutorial.cue.drift"] = new[] { "Hold Drift while turning to slide!", "החזיקו דריפט בזמן הפניה כדי להחליק!" },
            ["tutorial.cue.boost"] = new[] { "Press Boost to zoom super fast!", "לחצו על האצה כדי לנסוע ממש מהר!" },
            ["tutorial.cue.jump"] = new[] { "Drive up the ramp and jump into the air!", "נסעו על הרמפה וקפצו באוויר!" },
            ["tutorial.cue.recovery"] = new[] { "Stuck? Press Recovery to get back on track!", "נתקעתם? לחצו על חזרה למסלול!" },
            ["tutorial.cue.trial"] = new[] { "Great job! Now drive one whole lap to finish!", "כל הכבוד! עכשיו השלימו הקפה שלמה כדי לסיים!" },
            ["tutorial.done"] = new[] { "You did it! You are ready to race!", "עשיתם את זה! אתם מוכנים למירוץ!" },
            ["driving.exit"] = new[] { "Exit", "יציאה" },
            ["hud.time"] = new[] { "Time", "זמן" },
            ["hud.speed"] = new[] { "Speed", "מהירות" },
            ["hud.lap"] = new[] { "Lap", "הקפה" },
            ["hud.gate"] = new[] { "Gate", "שער" },
            ["hud.paused"] = new[] { "Paused", "מושהה" },
            ["hud.resume"] = new[] { "Resume", "המשך" },
            ["hud.retry"] = new[] { "Try again", "נסו שוב" },
            ["hud.finished"] = new[] { "Finished!", "הסתיים!" },
            ["hud.gadget.soon"] = new[] { "Gadget (soon)", "גאדג'ט (בקרוב)" },
            ["hud.gadget.unavailable"] = new[] { "Gadgets are not in this practice yet.", "גאדג'טים עוד לא באימון הזה." },
            // Keyboard legend: key names stay Latin in both locales — they
            // name the physical key caps, not words to translate.
            ["hud.keys"] = new[] {
                "Keys: ◀▶ steer · Space drift · Shift boost · R recover · C camera · Esc pause",
                "מקשים: ◀▶ היגוי · Space דריפט · Shift האצה · R חזרה · C מצלמה · Esc השהיה" },
            ["action.back"] = new[] { "Back", "חזרה" },
            ["action.save"] = new[] { "Save", "שמירה" },
            ["status.saved"] = new[] { "Settings saved.", "ההגדרות נשמרו." },
            ["status.blocked"] = new[] { "This profile's data cannot be read, so it was left unchanged.", "לא ניתן לקרוא את נתוני הפרופיל, לכן לא בוצע שום שינוי." },
            ["status.recovered"] = new[] { "This profile was loaded from a backup; the original files were left unchanged. Saving stays off until it is checked.", "הפרופיל נטען מגיבוי; הקבצים המקוריים לא השתנו. השמירה כבויה עד שהוא ייבדק." },
            ["status.saveFailed"] = new[] { "The settings could not be saved. Your previous settings are unchanged.", "לא הצלחנו לשמור את ההגדרות. ההגדרות הקודמות לא השתנו." },
            ["warning.missingFont"] = new[] { "The Hebrew font is missing, so Hebrew text may not display correctly.", "גופן העברית חסר, ולכן ייתכן שטקסט בעברית לא יוצג כראוי." },
            // --- LAN screens (Task 9) -------------------------------------
            // All Hebrew strings are logical order; callers set the element
            // direction. Addresses, ports, participant ids and timers stay
            // LTR regardless of locale.
            ["lan.title"] = new[] { "Race together", "מירוץ ברשת הביתית" },
            ["lan.hint"] = new[] { "One device hosts; the others join on the same network.", "מכשיר אחד מארח; השאר מצטרפים באותה רשת." },
            ["lan.host"] = new[] { "Host a race", "אירוח מירוץ" },
            ["lan.discover"] = new[] { "Find races", "חיפוש מירוצים" },
            ["lan.discovering"] = new[] { "Searching for races…", "מחפשים מירוצים…" },
            ["lan.stopSearch"] = new[] { "Stop searching", "עצירת חיפוש" },
            ["lan.connecting"] = new[] { "Connecting…", "מתחברים…" },
            ["lan.address"] = new[] { "Host address", "כתובת המארח" },
            ["lan.port"] = new[] { "Port", "פורט" },
            ["lan.join"] = new[] { "Join", "הצטרפות" },
            ["lan.found"] = new[] { "Races found", "מירוצים שנמצאו" },
            ["lan.none"] = new[] { "No races found yet.", "עדיין לא נמצאו מירוצים." },
            ["lan.lobby"] = new[] { "Race lobby", "חדר המירוץ" },
            ["lan.players"] = new[] { "Players", "שחקנים" },
            ["lan.course"] = new[] { "Course", "מסלול" },
            ["lan.class"] = new[] { "Vehicle class", "סוג רכב" },
            ["lan.class.car"] = new[] { "Cars", "מכוניות" },
            ["lan.class.motorcycle"] = new[] { "Motorcycles", "אופנועים" },
            ["lan.vehicle"] = new[] { "Vehicle", "רכב" },
            ["lan.ready"] = new[] { "Ready!", "מוכנים!" },
            ["lan.notReady"] = new[] { "Not ready", "עוד לא מוכנים" },
            ["lan.start"] = new[] { "Start race", "התחלת מירוץ" },
            ["lan.leave"] = new[] { "Leave", "עזיבה" },
            ["lan.you"] = new[] { "You", "אתם" },
            ["lan.empty"] = new[] { "Open slot", "מקום פנוי" },
            ["lan.ai"] = new[] { "Computer", "מחשב" },
            ["lan.offline"] = new[] { "Away", "לא מחובר" },
            ["lan.hostTag"] = new[] { "Host", "מארח" },
            ["lan.waitHost"] = new[] { "Waiting for the host…", "מחכים למארח…" },
            ["lan.pauseAsk"] = new[] { "Ask to pause", "בקשת השהיה" },
            ["lan.pausedBy"] = new[] { "wants a pause", "מבקשים הפסקה" },
            ["lan.resumeAll"] = new[] { "Resume everyone", "ממשיכים את כולם" },
            ["lan.sharedPaused"] = new[] { "Race paused for everyone", "המירוץ מושהה לכולם" },
            ["lan.standings"] = new[] { "Standings", "דירוג" },
            ["lan.recovery.title"] = new[] { "Connection lost", "החיבור נותק" },
            ["lan.reconnect"] = new[] { "Reconnect", "חיבור מחדש" },
            ["lan.reconnecting"] = new[] { "Reconnecting…", "מתחברים מחדש…" },
            ["lan.results.title"] = new[] { "Race results", "תוצאות המירוץ" },
            ["lan.rematch"] = new[] { "Race again!", "עוד מירוץ!" },
            ["lan.retrySave"] = new[] { "Try saving again", "נסיון שמירה נוסף" },
            ["lan.saved"] = new[] { "Saved", "נשמר" },
            ["lan.saving"] = new[] { "Saving…", "שומרים…" },
            ["lan.saveFailed"] = new[] { "Save failed", "השמירה נכשלה" },
            ["lan.err.title"] = new[] { "Network problem", "תקלת רשת" },
            // Disabled-reason / error-code texts for the LAN surface; the
            // raw code stays the fallback for anything unlisted.
            ["lan.reason.inSession"] = new[] { "You are already in a race session.", "אתם כבר בתוך מירוץ." },
            ["lan.reason.requiresSelectedProfile"] = new[] { "Pick a driver first.", "קודם בוחרים נהג." },
            ["lan.reason.hostOnly"] = new[] { "Only the host can do that.", "רק המארח יכול לעשות את זה." },
            ["lan.reason.notReady"] = new[] { "Everyone must be ready first.", "כולם צריכים להיות מוכנים קודם." },
            ["lan.reason.noSession"] = new[] { "No race session right now.", "אין מירוץ ברשת כרגע." },
            ["lan.reason.invalidPhase"] = new[] { "Not right now.", "לא עכשיו." },
            ["lan.reason.notConfigured"] = new[] { "Pick a course and class first.", "קודם בוחרים מסלול וסוג רכב." },
            ["lan.reason.joinTimeout"] = new[] { "Could not reach that host.", "לא הצלחנו להגיע למארח." },
            ["lan.reason.connectFailed"] = new[] { "Could not connect.", "החיבור נכשל." },
            ["lan.reason.joinPending"] = new[] { "A join is already in progress.", "הצטרפות כבר בעבודה." },
            ["lan.reason.hostClosed"] = new[] { "The host ended the race.", "המארח סיים את המירוץ." },
            ["lan.reason.hostLost"] = new[] { "The host went away.", "המארח נותק." },
            ["lan.reason.invalidVehicle"] = new[] { "That vehicle does not fit this race.", "הרכב הזה לא מתאים למירוץ." },
            ["lan.reason.invalidClass"] = new[] { "That class is not in this race.", "סוג הרכב הזה לא במירוץ." },
            ["lan.reason.alreadyDiscovering"] = new[] { "Already searching.", "כבר מחפשים." },
            ["lan.reason.notDiscovering"] = new[] { "Not searching right now.", "לא מחפשים כרגע." },
            ["lan.reason.pendingFull"] = new[] { "Too many requests; wait a moment.", "יותר מדי בקשות; מחכים רגע." },
            ["lan.reason.requestTimeout"] = new[] { "No answer; try again.", "אין מענה; מנסים שוב." },
            ["lan.reason.sessionFull"] = new[] { "This session is full of saved results.", "החדר מלא בתוצאות שמורות." },
            ["lan.reason.unavailable"] = new[] { "Not available right now.", "לא זמין כרגע." },
            ["lan.reason.networkActive"] = new[] { "Leave the race session first.", "קודם עוזבים את המירוץ." },
            ["lan.reason.raceActive"] = new[] { "Finish or leave the race first.", "קודם מסיימים את המירוץ." },
            ["lan.reason.lost"] = new[] { "Connection lost.", "החיבור אבד." },
            ["lan.reason.rejected"] = new[] { "The request was refused.", "הבקשה נדחתה." },
            ["lan.reason.leaveCancelled"] = new[] { "Cancelled by leaving.", "בוטל בעזיבה." },
            ["lan.reason.lobbyFull"] = new[] { "The race is full.", "המירוץ מלא." },
            ["lan.reason.incompatible"] = new[] { "Different game version.", "גרסת המשחק שונה." },
            ["lan.reason.duplicatePending"] = new[] { "Already sent.", "כבר נשלח." },
            ["lan.reason.unknownEvent"] = new[] { "Unknown course.", "מסלול לא ידוע." },
            ["lan.reason.missingParameter"] = new[] { "Missing details.", "חסרים פרטים." },
            ["lan.reason.invalidParameter"] = new[] { "Bad details.", "פרטים שגויים." }
        };

        public static IReadOnlyCollection<string> Keys
        {
            get { return Catalog.Keys; }
        }

        public static string Get(string key, string language)
        {
            if (key == null) throw new ArgumentNullException("key");
            RequireLanguage(language);
            string[] pair;
            if (!Catalog.TryGetValue(key, out pair))
                throw new KeyNotFoundException("Unknown shell text key.");
            return pair[language == "he" ? Hebrew : English];
        }

        public static bool IsRightToLeft(string language)
        {
            RequireLanguage(language);
            return language == "he";
        }

        private static void RequireLanguage(string language)
        {
            if (language != "en" && language != "he")
                throw new ArgumentException("Supported languages are \"en\" and \"he\".", "language");
        }
    }
}
