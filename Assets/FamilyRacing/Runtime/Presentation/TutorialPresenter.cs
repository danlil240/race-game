using FamilyRacing.Core.Foundation;
using UnityEngine;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    // Tutorial presentation: watches the observable cue identity/sequence
    // in driving.tutorial and announces each (re)issued cue through the
    // bundled licensed recordings assigned on TutorialDefinitionAsset.
    // There is no runtime speech service and no credential use: a cue
    // without a recorded clip is reported honestly (LastMissingCue +
    // one logged warning per cue/locale) while the shell's text/icon cue
    // keeps showing — the missing audio stays an open acceptance gap.
    // The presenter also animates the shell's cue icon as a small
    // demonstration: the same glyph the prompt names gets a looped,
    // lesson-appropriate motion.
    public sealed class TutorialPresenter : MonoBehaviour
    {
        private AudioSource source;
        private TutorialDefinitionAsset definition;
        private string lastCueKey = "";
        private readonly System.Collections.Generic.HashSet<string> missingLogged =
            new System.Collections.Generic.HashSet<string>();

        // Diagnostics for tests and review: the cue currently announced
        // and the most recent cue/locale that had no recording.
        public string PlayingCue { get; private set; }
        public string LastMissingCue { get; private set; }

        private void Awake()
        {
            EnsureSource();
        }

        public void Bind(TutorialDefinitionAsset asset)
        {
            definition = asset;
        }

        // Called once per frame after the shell refresh with the latest
        // snapshot and the shell's tutorial icon element (may be null in
        // fixtures without UI).
        public void ApplyFrame(FoundationState state, VisualElement icon)
        {
            var tutorial = state != null && state.driving != null
                ? state.driving.tutorial : null;
            if (tutorial == null)
            {
                StopCue();
                ResetIcon(icon);
                return;
            }
            var language = state != null ? state.language : "en";
            // The cue key changes on a new lesson, trial/graduation cue,
            // every tutorial.replay, and on locale switches mid-cue.
            var key = tutorial.cueId + "|" + tutorial.cueSequence + "|" + language;
            if (key != lastCueKey)
            {
                lastCueKey = key;
                PlayCue(tutorial.cueId, language);
            }
            EnsureSource();
            source.volume = state != null && state.draftProfile != null
                ? Mathf.Clamp01(state.draftProfile.voiceVolume) : 1f;
            AnimateIcon(icon, tutorial.cueId, Time.unscaledTime);
        }

        private void PlayCue(string cueId, string language)
        {
            PlayingCue = cueId;
            var clip = definition != null ? definition.ClipFor(cueId, language) : null;
            if (clip == null)
            {
                var miss = cueId + ":" + language;
                LastMissingCue = miss;
                if (missingLogged.Add(miss))
                    Debug.LogWarning("TutorialPresenter: no " + language +
                        " recording bundled for tutorial cue '" + cueId +
                        "' — licensed audio is pending; text/icon cue still shown.");
                if (source != null) source.Stop();
                return;
            }
            EnsureSource();
            source.clip = clip;
            source.Play();
        }

        private void StopCue()
        {
            if (!string.IsNullOrEmpty(lastCueKey) && source != null) source.Stop();
            lastCueKey = "";
            PlayingCue = null;
        }

        private void EnsureSource()
        {
            if (source != null) return;
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f; // 2D UI voiceover
        }

        // Small looped demonstration per lesson: steering slides, drift
        // rocks, boost pulses, jump bounces, recovery spins, trial/finish
        // glows. Pure presentation — simulation state is never touched.
        private void AnimateIcon(VisualElement icon, string cueId, float time)
        {
            if (icon == null) return;
            var tx = 0f;
            var ty = 0f;
            var rot = 0f;
            var scale = 1f;
            switch (cueId)
            {
                case "steering":
                    tx = Mathf.Sin(time * 2.4f) * 22f;
                    break;
                case "drift":
                    rot = Mathf.Sin(time * 3f) * 16f;
                    tx = Mathf.Sin(time * 3f) * 10f;
                    break;
                case "boost":
                    scale = 1f + 0.22f * Mathf.Abs(Mathf.Sin(time * 4f));
                    break;
                case "jump":
                    ty = -Mathf.Abs(Mathf.Sin(time * 3.2f)) * 26f;
                    break;
                case "recovery":
                    rot = (time * 140f) % 360f;
                    break;
                default: // trial / finished: celebratory pulse
                    scale = 1f + 0.15f * Mathf.Sin(time * 5f);
                    break;
            }
            icon.style.translate = new Translate(tx, ty);
            icon.style.rotate = new Rotate(Angle.Degrees(rot));
            icon.style.scale = new Scale(new Vector2(scale, scale));
        }

        private static void ResetIcon(VisualElement icon)
        {
            if (icon == null) return;
            icon.style.translate = new Translate(0f, 0f);
            icon.style.rotate = new Rotate(Angle.Degrees(0f));
            icon.style.scale = new Scale(new Vector2(1f, 1f));
        }
    }
}
