using UnityEngine;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    // Opt-in marker and art carrier for the player-facing circuit presentation.
    [RequireComponent(typeof(UIDocument))]
    public sealed class AssetTrialHud : MonoBehaviour
    {
        private static readonly string[] PortraitIds = { "fox", "rabbit", "panda", "raccoon" };
        private static readonly string[] CourseIds = { "coastal-village", "pine-forest", "desert-canyon" };

        public Texture2D[] portraits;
        public Texture2D[] coursePreviews;
        public string[] courseIds;

        public Texture2D Portrait(int slot)
        {
            var index = ((slot % PortraitIds.Length) + PortraitIds.Length) % PortraitIds.Length;
            if (portraits != null && index < portraits.Length && portraits[index] != null)
                return portraits[index];
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Assets/FamilyRacing/Generated/AssetTrial/Racers/portrait-" + PortraitIds[index] + ".asset");
#else
            return null;
#endif
        }

        public Texture2D CoursePreview(string courseId)
        {
            if (string.IsNullOrEmpty(courseId)) return null;
            var ids = courseIds != null && courseIds.Length > 0 ? courseIds : CourseIds;
            for (var i = 0; i < ids.Length; i++)
            {
                if (ids[i] != courseId) continue;
                if (coursePreviews != null && i < coursePreviews.Length && coursePreviews[i] != null)
                    return coursePreviews[i];
#if UNITY_EDITOR
                return UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(
                    "Assets/FamilyRacing/Generated/AssetTrial/Circuits/preview-" + courseId + ".asset");
#endif
            }
            return null;
        }
    }
}
