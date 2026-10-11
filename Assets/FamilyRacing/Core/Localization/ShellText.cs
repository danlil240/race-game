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
            ["driving.exit"] = new[] { "Exit", "יציאה" },
            ["hud.time"] = new[] { "Time", "זמן" },
            ["hud.speed"] = new[] { "Speed", "מהירות" },
            ["hud.finished"] = new[] { "Finished!", "הסתיים!" },
            ["action.back"] = new[] { "Back", "חזרה" },
            ["action.save"] = new[] { "Save", "שמירה" },
            ["status.saved"] = new[] { "Settings saved.", "ההגדרות נשמרו." },
            ["status.blocked"] = new[] { "This profile's data cannot be read, so it was left unchanged.", "לא ניתן לקרוא את נתוני הפרופיל, לכן לא בוצע שום שינוי." },
            ["status.recovered"] = new[] { "This profile was loaded from a backup; the original files were left unchanged. Saving stays off until it is checked.", "הפרופיל נטען מגיבוי; הקבצים המקוריים לא השתנו. השמירה כבויה עד שהוא ייבדק." },
            ["status.saveFailed"] = new[] { "The settings could not be saved. Your previous settings are unchanged.", "לא הצלחנו לשמור את ההגדרות. ההגדרות הקודמות לא השתנו." },
            ["warning.missingFont"] = new[] { "The Hebrew font is missing, so Hebrew text may not display correctly.", "גופן העברית חסר, ולכן ייתכן שטקסט בעברית לא יוצג כראוי." }
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
