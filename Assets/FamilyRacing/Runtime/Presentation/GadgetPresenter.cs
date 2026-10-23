using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using UnityEngine;
using UnityEngine.Rendering;

namespace FamilyRacing
{
    // Gadget world presenter (M5 Task 5): pooled, render-only views of
    // the authoritative gadget state. It mirrors exactly what the host's
    // GadgetWorldState publishes — presentation can never grant, spend
    // or mutate gadget state.
    //
    //   * Pickups: one pooled spinner per authored site (bounded by
    //     NetworkSettings.GadgetPickupCapacity), visible only while the
    //     site is live; a consumed pickup dims while its respawn clock
    //     counts down.
    //   * Foam patches: pooled flat discs keyed by patchId, bounded by
    //     NetworkSettings.GadgetPatchCapacity — the rules' own bound —
    //     fading as their authoritative lifetime runs down. A patch that
    //     vanishes from state retires its view on the same frame.
    //   * Bubble shields: a translucent sphere per shielded racer row,
    //     pooled to the participant capacity.
    //
    // All motion (bob/spin/fade) is local wall-clock decoration; the
    // state itself is never touched. Paused frames freeze spin the same
    // way the authoritative clocks freeze.
    public sealed class GadgetPresenter : MonoBehaviour
    {
        private const float PickupSpinDegreesPerSecond = 120f;
        private const float PickupBobMeters = 0.25f;
        private const float PickupBobHz = 1.6f;
        private const float PickupSize = 1.6f;
        private const float InactivePickupAlpha = 0.18f;
        private const float PatchHeightMeters = 0.06f;
        private const float ShieldDiameterMeters = 3.8f;
        private const float ShieldHeightOffsetMeters = 0.9f;
        private const float TeleportSnapDistance = 4f;

        private sealed class PickupView
        {
            internal string siteId;
            internal GameObject go;
            internal Renderer renderer;
            internal MaterialPropertyBlock mpb;
            internal float baseY;
            internal bool active;
        }

        private sealed class PatchView
        {
            internal string patchId;
            internal GameObject go;
            internal Renderer renderer;
            internal MaterialPropertyBlock mpb;
            internal Color baseColor;
            internal bool active;
        }

        private sealed class ShieldView
        {
            internal string participantId;
            internal GameObject go;
            internal Renderer renderer;
            internal Vector3 prevPos;
            internal Vector3 curPos;
            internal long lastTick = -1;
            internal bool hasPose;
            internal bool active;
        }

        private Transform container;
        private DrivingWorld drivingWorld;
        private Material pickupMaterial;
        private Material inactivePickupMaterial;
        private Material patchMaterial;
        private Material shieldMaterial;
        private readonly List<PickupView> pickups = new List<PickupView>();
        private readonly List<PatchView> patches = new List<PatchView>();
        private readonly List<ShieldView> shields = new List<ShieldView>();
        private readonly Dictionary<string, int> pickupIndex =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Queue<PatchView> freePatches = new Queue<PatchView>();
        private readonly Queue<ShieldView> freeShields = new Queue<ShieldView>();
        private string boundRaceId = "";
        private float bobPhase;
        private bool exhaustedLogged;

        // --- public surface -------------------------------------------------

        // Observability matching the other presenters: live view counts,
        // never fabricated when unbound.
        public int ActivePickupCount
        {
            get
            {
                var n = 0;
                foreach (var view in pickups) if (view.active) n++;
                return n;
            }
        }

        public int ActivePatchCount
        {
            get
            {
                var n = 0;
                foreach (var view in patches) if (view.active) n++;
                return n;
            }
        }

        public int ActiveShieldCount
        {
            get
            {
                var n = 0;
                foreach (var view in shields) if (view.active) n++;
                return n;
            }
        }

        // One render tick driven by the app's Update: resolve the live
        // authoritative world from whichever slice publishes it and
        // mirror it. A missing world (no gadget content, or no race)
        // retires every view.
        public void ApplyFrame(FoundationState state, float deltaSeconds)
        {
            ApplyFrame(state, deltaSeconds, 1f);
        }

        public void ApplyFrame(FoundationState state, float deltaSeconds,
            float visualAlpha)
        {
            var world = WorldFor(state);
            var raceId = RaceIdFor(state);
            if (world == null)
            {
                if (boundRaceId != "" || ActiveCount() > 0) DeactivateAll();
                boundRaceId = "";
                return;
            }
            if (raceId != boundRaceId)
            {
                // New race identity: views belong to the old world's
                // sites/patches — retire all, never carry across.
                DeactivateAll();
                boundRaceId = raceId ?? "";
            }

            var paused = RacePresentationUtil.IsPaused(state);
            if (!paused) bobPhase += deltaSeconds;
            SyncPickups(world);
            SyncPatches(world);
            SyncShields(state, world, visualAlpha);
        }

        // --- world resolution ------------------------------------------------

        // The authoritative gadget world for the presented race: the
        // session-owned world for solo practice/tutorial/time trial, the
        // local coordinator's shared world for campaign/quickRace, or the
        // host-published world on LAN. Null when the race authors no
        // gadget content — every view retires honestly.
        private static GadgetWorldState WorldFor(FoundationState state)
        {
            if (state == null) return null;
            if (state.driving != null && state.driving.gadgets != null)
                return state.driving.gadgets;
            var solo = state.soloRace;
            if (solo != null)
            {
                if (solo.race != null && solo.race.gadgets != null)
                    return solo.race.gadgets;
                if (solo.trial != null && solo.trial.gadgets != null)
                    return solo.trial.gadgets;
            }
            var race = state.networkRace;
            return race != null ? race.gadgets : null;
        }

        private static string RaceIdFor(FoundationState state)
        {
            if (state == null) return null;
            if (state.driving != null
                && !string.IsNullOrEmpty(state.driving.raceId))
                return state.driving.raceId;
            var solo = state.soloRace;
            if (solo != null && !string.IsNullOrEmpty(solo.raceId))
                return solo.raceId;
            var race = state.networkRace;
            return race != null && !string.IsNullOrEmpty(race.raceId)
                ? race.raceId : null;
        }

        // --- pickups ---------------------------------------------------------

        private void SyncPickups(GadgetWorldState world)
        {
            var live = world.pickups;
            var liveCount = live != null ? live.Count : 0;
            foreach (var view in pickups) view.active = false;
            for (var i = 0; i < liveCount; i++)
            {
                var site = live[i];
                if (site == null || string.IsNullOrEmpty(site.siteId))
                    continue;
                var view = PickupFor(site);
                if (view == null) continue; // capacity exhausted
                view.active = true;
                var t = view.go.transform;
                t.position = new Vector3(site.x,
                    view.baseY + Mathf.Sin(bobPhase * PickupBobHz
                        * Mathf.PI * 2f) * PickupBobMeters, site.z);
                // Spin is derived from the pause-aware accumulated phase
                // — a paused race freezes the decoration exactly like
                // the authoritative clocks freeze.
                t.rotation = Quaternion.Euler(0f,
                    45f + bobPhase * PickupSpinDegreesPerSecond, 0f);
                // A consumed pickup stays at a dim ghost while its
                // respawn clock runs — the site is honest about being
                // "not yet live" instead of popping back.
                if (view.renderer != null)
                    view.renderer.sharedMaterial = site.active
                        ? PickupMaterial() : InactivePickupMaterial();
                var alpha = site.active ? 1f : InactivePickupAlpha;
                SetAlpha(view.renderer, view.mpb, alpha);
            }
            foreach (var view in pickups)
                if (view.go != null)
                    view.go.SetActive(view.active);
        }

        private PickupView PickupFor(GadgetPickupState site)
        {
            int index;
            if (pickupIndex.TryGetValue(site.siteId, out index))
                return pickups[index];
            if (pickups.Count >= NetworkSettings.GadgetPickupCapacity)
            {
                if (!exhaustedLogged)
                {
                    exhaustedLogged = true;
                    Debug.LogWarning("GadgetPresenter: pickup view pool "
                        + "exhausted — site '" + site.siteId
                        + "' renders nothing.");
                }
                return null;
            }
            var view = new PickupView
            {
                siteId = site.siteId,
                go = BuildPickup(site),
                baseY = site.y + PickupSize * 0.5f + 0.3f
            };
            view.renderer = view.go.GetComponentInChildren<Renderer>();
            view.mpb = new MaterialPropertyBlock();
            pickups.Add(view);
            pickupIndex.Add(site.siteId, pickups.Count - 1);
            return view;
        }

        private GameObject BuildPickup(GadgetPickupState site)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "gadget-pickup-" + site.siteId;
            go.transform.SetParent(Container(), false);
            go.transform.position = new Vector3(site.x, site.y, site.z);
            go.transform.localScale = Vector3.one * PickupSize;
            go.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
            var collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            var renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = PickupMaterial();
            go.SetActive(false);
            return go;
        }

        // --- foam patches ------------------------------------------------------

        private void SyncPatches(GadgetWorldState world)
        {
            var live = world.patches;
            HashSet<string> seen = null;
            if (live != null && live.Count > 0)
            {
                seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var patch in live)
                    if (patch != null && !string.IsNullOrEmpty(patch.patchId))
                        seen.Add(patch.patchId);
            }
            // Retire views whose patch left the authoritative world.
            for (var i = patches.Count - 1; i >= 0; i--)
            {
                var view = patches[i];
                if (view.active
                    && (seen == null || !seen.Contains(view.patchId)))
                {
                    RetirePatch(view);
                }
            }
            if (live == null) return;
            foreach (var patch in live)
            {
                if (patch == null || string.IsNullOrEmpty(patch.patchId))
                    continue;
                var view = PatchFor(patch);
                if (view == null) continue;
                view.active = true;
                if (view.go != null && !view.go.activeSelf)
                    view.go.SetActive(true);
                var t = view.go.transform;
                t.position = new Vector3(patch.x,
                    patch.y + PatchHeightMeters, patch.z);
                t.localScale = new Vector3(patch.radiusMeters * 2f,
                    PatchHeightMeters, patch.radiusMeters * 2f);
                // Fade with the authoritative remaining lifetime: the
                // disc's alpha is the patch's own clock, so a client
                // reconnecting mid-effect renders the same decay.
                var fraction = Mathf.Clamp01(patch.ticksRemaining / 180f);
                var color = view.baseColor;
                color.a = Mathf.Lerp(0.35f, 0.85f, fraction);
                SetColor(view.renderer, view.mpb, color);
            }
        }

        private PatchView PatchFor(GadgetPatchState patch)
        {
            foreach (var existing in patches)
                if (existing.patchId == patch.patchId) return existing;
            PatchView view;
            if (freePatches.Count > 0)
            {
                view = freePatches.Dequeue();
            }
            else
            {
                if (patches.Count >= NetworkSettings.GadgetPatchCapacity)
                    return null; // rules cap the world; pools never exceed
                view = new PatchView
                {
                    go = BuildPatch(),
                    mpb = new MaterialPropertyBlock(),
                    baseColor = new Color(0.98f, 0.96f, 0.88f, 0.75f)
                };
                view.renderer = view.go.GetComponentInChildren<Renderer>();
                patches.Add(view);
            }
            view.patchId = patch.patchId;
            view.go.name = "gadget-patch-" + patch.patchId;
            return view;
        }

        private void RetirePatch(PatchView view)
        {
            view.active = false;
            view.patchId = null;
            if (view.go != null) view.go.SetActive(false);
            freePatches.Enqueue(view);
        }

        private GameObject BuildPatch()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "gadget-patch";
            go.transform.SetParent(Container(), false);
            go.transform.localScale = new Vector3(1f, PatchHeightMeters, 1f);
            var collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            var renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = PatchMaterial();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            go.SetActive(false);
            return go;
        }

        // --- shields ----------------------------------------------------------

        private void SyncShields(FoundationState state,
            GadgetWorldState world, float visualAlpha)
        {
            foreach (var view in shields) view.active = false;
            if (world.racers != null)
                foreach (var row in world.racers)
                {
                    if (row == null || row.shieldCharges <= 0) continue;
                    var view = ShieldFor(row.participantId);
                    if (view == null) continue;
                    var pose = ResolveShieldPose(state, row.participantId,
                        visualAlpha, view);
                    if (!pose.HasValue) continue;
                    view.active = true;
                    view.go.transform.position = pose.Value
                        + Vector3.up * ShieldHeightOffsetMeters;
                }
            foreach (var view in shields)
            {
                if (view.go != null) view.go.SetActive(view.active);
                if (!view.active)
                {
                    view.participantId = null;
                    view.hasPose = false;
                    view.lastTick = -1;
                }
            }
        }

        private ShieldView ShieldFor(string participantId)
        {
            foreach (var existing in shields)
                if (existing.participantId == participantId) return existing;
            ShieldView view;
            if (freeShields.Count > 0)
            {
                view = freeShields.Dequeue();
            }
            else
            {
                if (shields.Count >= NetworkSettings.ParticipantCapacity)
                    return null;
                view = new ShieldView { go = BuildShield() };
                view.renderer =
                    view.go.GetComponentInChildren<Renderer>();
                shields.Add(view);
            }
            view.participantId = participantId;
            view.hasPose = false;
            view.lastTick = -1;
            return view;
        }

        private GameObject BuildShield()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "gadget-shield";
            go.transform.SetParent(Container(), false);
            go.transform.localScale = Vector3.one * ShieldDiameterMeters;
            var collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            var renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = ShieldMaterial();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            go.SetActive(false);
            return go;
        }

        // Anchors the shield bubble on the racer's rendered vehicle
        // transform when DrivingWorld presents it (which already carries
        // sub-tick interpolation and LAN prediction/interpolation), and
        // falls back to sub-tick interpolation of the committed motor
        // state when no vehicle view is active.
        private Vector3? ResolveShieldPose(FoundationState state,
            string participantId, float visualAlpha, ShieldView view)
        {
            if (drivingWorld == null)
                drivingWorld = GetComponent<DrivingWorld>();
            if (drivingWorld != null)
            {
                var racerPres = drivingWorld.RacerPresentation(participantId);
                if (racerPres != null && racerPres.gameObject.activeInHierarchy)
                    return racerPres.transform.position;
                var soloPres = drivingWorld.SoloPresentation;
                if (soloPres != null && soloPres.gameObject.activeInHierarchy
                    && ((state != null && state.driving != null
                        && (state.driving.gadgets == null
                            || state.driving.gadgets.racers == null
                            || state.driving.gadgets.racers.Count == 1
                            || RowIsLocal(state.driving, participantId)))
                        || (state != null && state.soloRace != null
                            && state.soloRace.trial != null)))
                {
                    return soloPres.transform.position;
                }
            }
            var motor = MotorFor(state, participantId);
            if (motor == null) return null;
            var pos = new Vector3(motor.x, motor.y, motor.z);
            if (!view.hasPose || motor.tick != view.lastTick)
            {
                view.prevPos = view.hasPose ? view.curPos : pos;
                view.curPos = pos;
                view.lastTick = motor.tick;
                if (!view.hasPose
                    || (view.curPos - view.prevPos).sqrMagnitude
                        > TeleportSnapDistance * TeleportSnapDistance)
                {
                    view.prevPos = view.curPos;
                }
                view.hasPose = true;
            }
            return Vector3.LerpUnclamped(view.prevPos, view.curPos,
                Mathf.Clamp01(visualAlpha));
        }

        private static MotorState MotorFor(FoundationState state,
            string participantId)
        {
            if (state == null) return null;
            if (state.driving != null && state.driving.motor != null
                && (state.driving.gadgets == null
                    || state.driving.gadgets.racers == null
                    || state.driving.gadgets.racers.Count == 1
                    || RowIsLocal(state.driving, participantId)))
            {
                return state.driving.motor;
            }
            var solo = state.soloRace;
            if (solo != null)
            {
                if (solo.race != null && solo.race.racers != null)
                    foreach (var racer in solo.race.racers)
                        if (racer != null
                            && racer.participantId == participantId
                            && racer.session != null
                            && racer.session.motor != null)
                            return racer.session.motor;
                if (solo.trial != null && solo.trial.motor != null)
                    return solo.trial.motor;
            }
            var race = state.networkRace;
            if (race != null && race.racers != null)
                foreach (var racer in race.racers)
                    if (racer != null
                        && racer.participantId == participantId
                        && racer.session != null
                        && racer.session.motor != null)
                        return racer.session.motor;
            return null;
        }

        private static bool RowIsLocal(DrivingSessionState session,
            string participantId)
        {
            var world = session.gadgets;
            return world != null && world.racers != null
                && world.racers.Count > 0
                && world.racers[0] != null
                && world.racers[0].participantId == participantId;
        }

        // --- teardown / materials -------------------------------------------

        private int ActiveCount()
        {
            return ActivePickupCount + ActivePatchCount + ActiveShieldCount;
        }

        private void DeactivateAll()
        {
            foreach (var view in pickups)
            {
                view.active = false;
                if (view.go != null) view.go.SetActive(false);
            }
            for (var i = patches.Count - 1; i >= 0; i--)
                RetirePatch(patches[i]);
            foreach (var view in shields)
            {
                view.active = false;
                view.participantId = null;
                view.hasPose = false;
                view.lastTick = -1;
                if (view.go != null) view.go.SetActive(false);
            }
            freeShields.Clear();
            foreach (var view in shields) freeShields.Enqueue(view);
        }

        private Transform Container()
        {
            if (container == null)
            {
                var holder = new GameObject("GadgetWorld");
                holder.transform.SetParent(transform, false);
                container = holder.transform;
            }
            return container;
        }

        private Material PickupMaterial()
        {
            if (pickupMaterial == null)
                pickupMaterial = BuildMaterial(
                    new Color(0.35f, 0.85f, 0.95f, 1f));
            return pickupMaterial;
        }

        private Material InactivePickupMaterial()
        {
            if (inactivePickupMaterial == null)
            {
                inactivePickupMaterial = BuildMaterial(
                    new Color(0.35f, 0.85f, 0.95f, InactivePickupAlpha));
                ConfigureTransparent(inactivePickupMaterial);
            }
            return inactivePickupMaterial;
        }

        private Material PatchMaterial()
        {
            if (patchMaterial == null)
            {
                patchMaterial = BuildMaterial(
                    new Color(0.98f, 0.96f, 0.88f, 0.75f));
                ConfigureTransparent(patchMaterial);
            }
            return patchMaterial;
        }

        private Material ShieldMaterial()
        {
            if (shieldMaterial == null)
            {
                shieldMaterial = BuildMaterial(
                    new Color(0.45f, 0.82f, 1f, 0.28f));
                ConfigureTransparent(shieldMaterial);
            }
            return shieldMaterial;
        }

        // URP Lit is the house shader; fall back through the built-in
        // chain so a stripped render pipeline still renders honestly.
        private static Material BuildMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var material = new Material(shader) { color = color };
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            return material;
        }

        // Runtime URP materials do not run the editor ShaderGUI blend
        // setup when _Surface is set alone: explicit SrcAlpha/OneMinusSrcAlpha
        // blend factors, ZWrite Off, the Transparent render queue and the
        // transparent surface keyword are required so shields and patches
        // actually render translucent instead of solid opaque.
        private static void ConfigureTransparent(Material material)
        {
            if (material == null) return;
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_Mode", 3f);
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_SrcBlendAlpha", (int)BlendMode.One);
            material.SetInt("_DstBlendAlpha", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.renderQueue = (int)RenderQueue.Transparent;
        }

        private static void SetAlpha(Renderer renderer,
            MaterialPropertyBlock mpb, float alpha)
        {
            if (renderer == null) return;
            var prop = AlphaProp(renderer);
            var color = renderer.sharedMaterial != null
                && renderer.sharedMaterial.HasProperty(prop)
                ? renderer.sharedMaterial.GetColor(prop) : Color.white;
            color.a = Mathf.Clamp01(alpha);
            mpb.SetColor(prop, color);
            renderer.SetPropertyBlock(mpb);
        }

        private static void SetColor(Renderer renderer,
            MaterialPropertyBlock mpb, Color color)
        {
            if (renderer == null) return;
            mpb.SetColor(AlphaProp(renderer), color);
            renderer.SetPropertyBlock(mpb);
        }

        private static int AlphaProp(Renderer renderer)
        {
            return renderer.sharedMaterial != null
                && renderer.sharedMaterial.HasProperty("_BaseColor")
                ? Shader.PropertyToID("_BaseColor")
                : Shader.PropertyToID("_Color");
        }

        private void OnDisable()
        {
            DeactivateAll();
            boundRaceId = "";
        }
    }
}
