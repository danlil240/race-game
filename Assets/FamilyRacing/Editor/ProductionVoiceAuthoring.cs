using System.IO;
using UnityEditor;
using UnityEngine;

namespace FamilyRacing.EditorTools
{
    public static class ProductionVoiceAuthoring
    {
        public const string CatalogPath = FullContentSetup.ContentDir + "/voice-prompt-catalog.asset";
        public const string AudioDirectory = "Assets/FamilyRacing/Audio/Production";

        // Reauthor in place so references and GUID survive every run. Missing
        // genuine recordings stay null and therefore fail the production gate.
        public static VoicePromptCatalogAsset Create()
        {
            Directory.CreateDirectory(FullContentSetup.ContentDir);
            AssetDatabase.Refresh();
            var asset = AssetDatabase.LoadAssetAtPath<VoicePromptCatalogAsset>(CatalogPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<VoicePromptCatalogAsset>();
                AssetDatabase.CreateAsset(asset, CatalogPath);
            }
            var ids = FullContentValidation.RequiredCueIds();
            var rows = new VoicePromptCatalogAsset.CueRow[ids.Length];
            for (var i = 0; i < ids.Length; i++)
                rows[i] = new VoicePromptCatalogAsset.CueRow
                {
                    cueId = ids[i], english = Load(ids[i], "en"), hebrew = Load(ids[i], "he")
                };
            asset.cues = rows;
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return asset;
        }

        private static AudioClip Load(string id, string language)
        {
            var path = AudioDirectory + "/cue-" + id + "-" + language + ".wav";
            return File.Exists(path) ? AssetDatabase.LoadAssetAtPath<AudioClip>(path) : null;
        }
    }
}
