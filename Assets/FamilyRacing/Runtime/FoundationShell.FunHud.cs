using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Racing;
using UnityEngine;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    // Fun-pass race callouts: a big pop-up line in the upper middle of the
    // screen for overtakes, slipstreams, mini-turbos, landed tricks,
    // knockouts, the final stretch and photo finishes, plus the
    // Elimination countdown and a camera-flash overlay. Everything reads
    // the published race state and events; nothing here changes the race.
    public sealed partial class FoundationShell
    {
        private const float BannerSeconds = 1.4f;
        private const float PlaceSettleSeconds = 0.45f;
        private const long PhotoFinishTicks = 15; // half a second

        private static readonly Color BannerGold = new Color(1f, 0.82f, 0.25f, 1f);
        private static readonly Color BannerWhite = new Color(0.98f, 0.97f, 0.92f, 1f);
        private static readonly Color BannerRed = new Color(1f, 0.42f, 0.35f, 1f);
        private static readonly Color BannerBlue = new Color(0.55f, 0.85f, 1f, 1f);
        private static readonly Color BannerPurple = new Color(0.85f, 0.55f, 1f, 1f);

        private Label funBanner;
        private Label eliminationLabel;
        private VisualElement photoFlash;
        private readonly RaceEventCursor funCursor = new RaceEventCursor();
        private string funRaceId = "";
        private float bannerTimer;
        private float flashAlpha;
        private int shownPlace;
        private int pendingPlace;
        private float pendingPlaceTimer;
        private bool finalStretchShown;
        private bool photoChecked;

        private void BuildFunHud(FoundationState state)
        {
            funBanner = new Label("");
            funBanner.name = "hud.fun.banner";
            funBanner.pickingMode = PickingMode.Ignore;
            funBanner.style.position = Position.Absolute;
            funBanner.style.left = 0;
            funBanner.style.right = 0;
            funBanner.style.top = new Length(20f, LengthUnit.Percent);
            funBanner.style.fontSize = 46;
            funBanner.style.unityFontStyleAndWeight = FontStyle.Bold;
            funBanner.style.unityTextAlign = TextAnchor.MiddleCenter;
            funBanner.style.color = BannerGold;
            funBanner.style.unityTextOutlineColor = new Color(0.05f, 0.08f, 0.12f, 0.9f);
            funBanner.style.unityTextOutlineWidth = 1.4f;
            funBanner.style.display = DisplayStyle.None;
            root.Add(funBanner);

            eliminationLabel = new Label("");
            eliminationLabel.name = "hud.fun.elimination";
            eliminationLabel.pickingMode = PickingMode.Ignore;
            eliminationLabel.style.position = Position.Absolute;
            eliminationLabel.style.left = 0;
            eliminationLabel.style.right = 0;
            eliminationLabel.style.top = new Length(12f, LengthUnit.Percent);
            eliminationLabel.style.fontSize = 26;
            eliminationLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            eliminationLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            eliminationLabel.style.color = BannerWhite;
            eliminationLabel.style.unityTextOutlineColor = new Color(0.05f, 0.08f, 0.12f, 0.9f);
            eliminationLabel.style.unityTextOutlineWidth = 1f;
            eliminationLabel.style.display = DisplayStyle.None;
            root.Add(eliminationLabel);

            photoFlash = new VisualElement();
            photoFlash.name = "hud.fun.flash";
            photoFlash.pickingMode = PickingMode.Ignore;
            photoFlash.style.position = Position.Absolute;
            photoFlash.style.left = photoFlash.style.right = 0;
            photoFlash.style.top = photoFlash.style.bottom = 0;
            photoFlash.style.backgroundColor = Color.white;
            photoFlash.style.opacity = 0f;
            photoFlash.style.display = DisplayStyle.None;
            root.Add(photoFlash);
        }

        private void ShowBanner(string text, Color color)
        {
            if (funBanner == null) return;
            funBanner.text = text;
            funBanner.style.color = color;
            funBanner.style.display = DisplayStyle.Flex;
            bannerTimer = BannerSeconds;
        }

        private void UpdateFunHud(FoundationState state)
        {
            if (funBanner == null) return;
            var dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f);
            var raceId = RacePresentationUtil.RaceId(state) ?? "";
            if (raceId != funRaceId)
            {
                funRaceId = raceId;
                funCursor.Reset(raceId);
                bannerTimer = 0f;
                flashAlpha = 0f;
                shownPlace = pendingPlace = 0;
                pendingPlaceTimer = 0f;
                finalStretchShown = false;
                photoChecked = false;
            }
            var paused = RacePresentationUtil.IsPaused(state);
            var race = RacePresentationUtil.PresentedRace(state);
            var me = RacePresentationUtil.LocalRacer(state);

            foreach (var ev in funCursor.Consume(state.drivingEvents))
                OnFunEvent(ev, state, race, me);

            if (!paused && race != null && me != null && race.phase == NetworkRace.PhaseRunning)
            {
                TrackPlace(state, me, dt);
                if (!finalStretchShown && RacePresentationUtil.FinalStretch(me.session))
                {
                    finalStretchShown = true;
                    ShowBanner(T("hud.finalStretch", state), BannerWhite);
                }
            }
            if (race != null && me != null) CheckPhotoFinish(state, race, me);

            // Elimination countdown.
            if (eliminationLabel != null)
            {
                var show = race != null && race.eliminationIntervalTicks > 0
                    && race.phase == NetworkRace.PhaseRunning
                    && me != null && !me.eliminated && !me.finished;
                eliminationLabel.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                if (show)
                {
                    var left = Mathf.Max(0, Mathf.CeilToInt(
                        (race.nextEliminationElapsedTicks - race.elapsedTicks) / 30f));
                    eliminationLabel.text = T("hud.elimination.next", state) + " " + Ltr(left.ToString());
                    eliminationLabel.style.color = left <= 5 ? BannerRed : BannerWhite;
                }
            }

            if (!paused && bannerTimer > 0f)
            {
                bannerTimer -= dt;
                var t = Mathf.Clamp01(bannerTimer / BannerSeconds);
                // Pop in big, settle, then fade.
                var pop = 1f + Mathf.Max(0f, t - 0.8f) * 2.5f;
                funBanner.style.scale = new Scale(new Vector3(pop, pop, 1f));
                funBanner.style.opacity = Mathf.Clamp01(t * 3f);
                if (bannerTimer <= 0f) funBanner.style.display = DisplayStyle.None;
            }
            if (photoFlash != null)
            {
                flashAlpha = Mathf.MoveTowards(flashAlpha, 0f, dt * 1.6f);
                photoFlash.style.opacity = flashAlpha;
                photoFlash.style.display = flashAlpha > 0.001f ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        // Overtake callouts wait for the place to hold for a moment so two
        // cars side by side do not flicker the banner.
        private void TrackPlace(FoundationState state, NetworkRacerState me, float dt)
        {
            if (me.position <= 0) return;
            if (shownPlace == 0) { shownPlace = pendingPlace = me.position; return; }
            if (me.position != pendingPlace)
            {
                pendingPlace = me.position;
                pendingPlaceTimer = 0f;
                return;
            }
            if (pendingPlace == shownPlace) return;
            pendingPlaceTimer += dt;
            if (pendingPlaceTimer < PlaceSettleSeconds) return;
            var gained = pendingPlace < shownPlace;
            shownPlace = pendingPlace;
            ShowBanner((gained ? T("hud.overtake.up", state) : T("hud.overtake.down", state))
                + "  " + T("results.place", state) + " " + Ltr(shownPlace.ToString()),
                gained ? BannerGold : BannerRed);
        }

        private void OnFunEvent(DrivingEvent ev, FoundationState state,
            NetworkRaceState race, NetworkRacerState me)
        {
            if (ev == null || string.IsNullOrEmpty(ev.kind)) return;
            var mine = me != null ? ev.participantId == me.participantId
                : RacePresentationUtil.IsLocalEvent(state, ev);
            switch (ev.kind)
            {
                case "race.slipstream":
                    if (mine) ShowBanner(T("hud.slipstream", state), BannerBlue);
                    break;
                case "motor.miniTurbo":
                    if (mine)
                    {
                        int tier;
                        int.TryParse(ev.detail, out tier);
                        if (tier >= 2)
                            ShowBanner(T("hud.miniTurbo", state),
                                tier >= 3 ? BannerPurple : BannerGold);
                    }
                    break;
                case "motor.trick":
                    if (mine && bannerTimer <= 0.2f) ShowBanner(T("hud.trick", state), BannerWhite);
                    break;
                case "motor.trickLanded":
                    if (mine) ShowBanner(T("hud.trickLanded", state), BannerGold);
                    break;
                case "race.eliminated":
                    if (mine) ShowBanner(T("hud.elimination.you", state), BannerRed);
                    else ShowBanner(RacerName(race, ev.participantId, state) + " "
                        + T("hud.elimination.other", state), BannerWhite);
                    break;
                case "race.lastStanding":
                    if (mine) ShowBanner(T("hud.elimination.win", state), BannerGold);
                    break;
            }
        }

        private string RacerName(NetworkRaceState race, string participantId, FoundationState state)
        {
            if (race != null && race.racers != null)
                foreach (var racer in race.racers)
                    if (racer != null && racer.participantId == participantId)
                        return VehicleName(racer.vehicleId, state);
            return participantId ?? "";
        }

        // A photo finish: someone crosses the line within half a second
        // of the local racer, before or after.
        private void CheckPhotoFinish(FoundationState state, NetworkRaceState race,
            NetworkRacerState me)
        {
            if (photoChecked || !me.finished || me.finishTicks <= 0) return;
            var close = false;
            var settled = race.elapsedTicks > me.finishTicks + PhotoFinishTicks
                || race.phase == NetworkRace.PhaseFinished;
            foreach (var racer in race.racers)
            {
                if (racer == null || racer == me || !racer.finished
                    || racer.eliminated || racer.finishTicks <= 0) continue;
                if (System.Math.Abs(racer.finishTicks - me.finishTicks) <= PhotoFinishTicks)
                    close = true;
            }
            if (!close && !settled) return;
            photoChecked = true;
            if (!close) return;
            flashAlpha = 0.85f;
            ShowBanner(T("hud.photoFinish", state), BannerWhite);
        }
    }
}
