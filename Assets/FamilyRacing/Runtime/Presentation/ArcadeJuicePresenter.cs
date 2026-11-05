using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Racing;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FamilyRacing
{
    // Arcade feedback layer: procedural, render-only effects that make the
    // gadgets and speed read at a glance. Everything here is built at
    // runtime from primitives and particle systems (no imported assets),
    // reads the same published state the other presenters read, and can
    // never change the race.
    //
    //   * Boost pads: glowing, scrolling chevron strips on the road at the
    //     course's derived pad positions (BoostPads.Derive).
    //   * Speed lines: streaks rushing past the camera while the local
    //     racer is on turbo, a pad kick or a held boost.
    //   * Gadget bursts: pickup sparkles and ring, turbo flames, missile
    //     smoke trails and explosions, foam bubbles, spin-out tyre smoke,
    //     shield pops.
    //   * A light post-processing pass on PC (bloom, punchier colour,
    //     soft vignette) so the glows and sky read less flat.
    public sealed class ArcadeJuicePresenter : MonoBehaviour
    {
        private const int PadPoolCapacity = 64;
        private const int RingPoolCapacity = 24;

        private sealed class Ring
        {
            internal LineRenderer line;
            internal float age, life, from, to;
            internal Color color;
        }

        private struct Racer
        {
            internal string id;
            internal MotorState motor;
            internal bool local;
        }

        private DrivingWorld drivingWorld;
        private DrivingCamera drivingCamera;
        private Transform root;
        private Material additive, smokeMaterial, padMaterial, padGlowMaterial;
        private Texture2D softDot, chevrons;
        private ParticleSystem sparks, smoke, speedLines;
        private Transform speedLinesHolder;
        private readonly List<GameObject> pads = new List<GameObject>();
        private readonly List<Ring> rings = new List<Ring>();
        private readonly RaceEventCursor cursor = new RaceEventCursor();
        private readonly List<Racer> racers = new List<Racer>();
        private readonly List<DrivingEvent> eventScratch = new List<DrivingEvent>();
        private string boundRaceId = "";
        private string padCourseId = "";
        private float fovKick, padFlash, emitClock, lineBudget;
        private Volume volume;
        private bool postChecked;

        public int ActivePadCount
        {
            get
            {
                var n = 0;
                foreach (var pad in pads) if (pad != null && pad.activeSelf) n++;
                return n;
            }
        }

        public void ApplyFrame(FoundationState state, float deltaSeconds)
        {
            var dt = Mathf.Clamp(deltaSeconds, 0f, 0.1f);
            EnsureBuilt();
            EnsurePostProcessing();
            var raceId = RacePresentationUtil.RaceId(state);
            if (raceId == null)
            {
                Unbind();
                TickRings(dt);
                return;
            }
            if (raceId != boundRaceId)
            {
                Unbind();
                boundRaceId = raceId;
                cursor.Reset(raceId);
            }
            SyncPads(CourseIdFor(state));
            CollectRacers(state);
            var world = GadgetPresenter.WorldFor(state);
            var paused = RacePresentationUtil.IsPaused(state);

            var fresh = cursor.Consume(state.drivingEvents);
            if (!paused)
            {
                foreach (var ev in fresh) OnEvent(ev, state, world);
                EmitContinuous(world, dt);
            }
            UpdateSpeedFeel(state, world, paused ? 0f : dt);
            AnimatePads(dt);
            TickRings(paused ? 0f : dt);
        }

        // --- context ---------------------------------------------------------

        private static string CourseIdFor(FoundationState state)
        {
            if (state == null) return "";
            if (state.driving != null && !string.IsNullOrEmpty(state.driving.courseId))
                return state.driving.courseId;
            var solo = state.soloRace;
            if (solo != null)
            {
                if (solo.trial != null && !string.IsNullOrEmpty(solo.trial.courseId))
                    return solo.trial.courseId;
                var fromRace = FirstCourse(solo.race);
                if (fromRace != "") return fromRace;
            }
            return FirstCourse(state.networkRace);
        }

        private static string FirstCourse(NetworkRaceState race)
        {
            if (race == null || race.racers == null) return "";
            foreach (var racer in race.racers)
                if (racer != null && racer.session != null
                    && !string.IsNullOrEmpty(racer.session.courseId))
                    return racer.session.courseId;
            return "";
        }

        private void CollectRacers(FoundationState state)
        {
            racers.Clear();
            foreach (var r in RacePresentationUtil.Racers(state))
                racers.Add(new Racer { id = r.key, motor = r.motor, local = r.local });
        }

        // Where a racer is drawn this frame: its interpolated vehicle view
        // when one is live, else its committed motor pose.
        private Vector3 PositionOf(Racer racer, out Vector3 forward)
        {
            if (drivingWorld == null) drivingWorld = GetComponent<DrivingWorld>();
            Transform view = null;
            if (drivingWorld != null)
            {
                var pres = drivingWorld.RacerPresentation(racer.id);
                if (pres != null && pres.gameObject.activeInHierarchy) view = pres.transform;
                else if (racers.Count == 1 && drivingWorld.SoloPresentation != null
                    && drivingWorld.SoloPresentation.gameObject.activeInHierarchy)
                    view = drivingWorld.SoloPresentation.transform;
            }
            if (view != null)
            {
                forward = view.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
                forward.Normalize();
                return view.position;
            }
            var m = racer.motor;
            forward = new Vector3(Mathf.Sin(m.heading), 0f, Mathf.Cos(m.heading));
            return new Vector3(m.x, m.y, m.z);
        }

        private bool TryRacer(string participantId, out Racer found)
        {
            foreach (var r in racers)
                if (string.IsNullOrEmpty(participantId) ? r.local : r.id == participantId)
                { found = r; return true; }
            found = default;
            return false;
        }

        private bool IsLocal(string participantId)
        {
            Racer r;
            return TryRacer(participantId, out r) && r.local;
        }

        // The gadget row behind a presented racer. A single-racer world
        // (practice, time trial) keys its one row by the session's
        // participant, which the presented key may not match.
        private static GadgetRacerState RowFor(GadgetWorldState world, Racer racer)
        {
            if (world == null || world.racers == null) return null;
            var row = world.Racer(racer.id);
            if (row == null && world.racers.Count == 1) row = world.racers[0];
            return row;
        }

        // --- events ----------------------------------------------------------

        private void OnEvent(DrivingEvent ev, FoundationState state, GadgetWorldState world)
        {
            if (ev == null || string.IsNullOrEmpty(ev.kind)) return;
            Racer racer;
            var has = TryRacer(ev.participantId, out racer);
            Vector3 forward = Vector3.forward;
            var at = has ? PositionOf(racer, out forward)
                : RacePresentationUtil.PositionFor(state, ev);
            var local = has && racer.local;
            switch (ev.kind)
            {
                case GadgetRules.EventPickup:
                    Burst(sparks, at + Vector3.up * 1.4f, 26, 7f, Gold, 0.5f, 0.35f);
                    Burst(sparks, at + Vector3.up * 1.4f, 10, 3f, Color.white, 0.6f, 0.5f);
                    SpawnRing(at + Vector3.up * 0.4f, 0.5f, 5f, 0.45f, Gold);
                    break;
                case GadgetRules.EventUsed:
                    var kind = KindOf(ev.detail);
                    if (kind == GadgetKind.Turbo)
                    {
                        Cone(sparks, at - forward * 1.8f + Vector3.up * 0.6f, -forward, 40, 16f, Flame, 0.4f);
                        SpawnRing(at + Vector3.up * 0.3f, 1f, 7f, 0.35f, Electric);
                        if (local) fovKick = 12f;
                    }
                    else if (kind == GadgetKind.BubbleShield)
                        SpawnRing(at + Vector3.up * 1f, 0.5f, 3.2f, 0.4f, Bubble);
                    break;
                case GadgetRules.EventBoostPad:
                    Cone(sparks, at - forward * 1.6f + Vector3.up * 0.5f, -forward, 22, 12f, Electric, 0.35f);
                    SpawnRing(at + Vector3.up * 0.25f, 1f, 5.5f, 0.3f, Electric);
                    if (local) { fovKick = Mathf.Max(fovKick, 9f); padFlash = 1f; }
                    break;
                case GadgetRules.EventFoamDropped:
                    Burst(smoke, at - forward * 2f + Vector3.up * 0.4f, 14, 2.5f, Bubble, 0.9f, 0.9f);
                    break;
                case GadgetRules.EventFoamHit:
                    Burst(smoke, at + Vector3.up * 0.8f, 30, 5f, Bubble, 1.1f, 1.2f);
                    Burst(sparks, at + Vector3.up * 1.2f, 18, 5f, Color.white, 0.7f, 0.25f);
                    SpawnRing(at + Vector3.up * 0.2f, 1f, 6f, 0.5f, Bubble);
                    break;
                case GadgetRules.EventMissileLaunched:
                    Burst(smoke, at - forward * 1.5f + Vector3.up * 0.8f, 10, 2f, SmokeGrey, 0.8f, 0.8f);
                    break;
                case GadgetRules.EventMissileHit:
                case GadgetRules.EventMineHit:
                    Explosion(at);
                    break;
                case GadgetRules.EventEmpHit:
                    Burst(sparks, at + Vector3.up * 1.2f, 30, 6f, Electric, 0.5f, 0.3f);
                    break;
                case GadgetRules.EventShieldBlocked:
                    Burst(sparks, at + Vector3.up * 1.2f, 30, 8f, Bubble, 0.5f, 0.3f);
                    SpawnRing(at + Vector3.up * 1f, 2f, 7f, 0.4f, Bubble);
                    break;
            }
        }

        private static string KindOf(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return "";
            if (itemId.StartsWith(GadgetKind.Turbo, StringComparison.Ordinal)) return GadgetKind.Turbo;
            if (itemId.StartsWith(GadgetKind.BubbleShield, StringComparison.Ordinal)) return GadgetKind.BubbleShield;
            return "";
        }

        private void Explosion(Vector3 at)
        {
            Burst(sparks, at + Vector3.up * 1.2f, 50, 14f, Flame, 0.6f, 0.45f);
            Burst(sparks, at + Vector3.up * 1.2f, 20, 7f, Color.white, 0.4f, 0.6f);
            Burst(smoke, at + Vector3.up * 1.2f, 22, 4f, SmokeDark, 1.3f, 1.6f);
            SpawnRing(at + Vector3.up * 0.3f, 1f, 9f, 0.45f, Flame);
        }

        // --- continuous emitters ---------------------------------------------

        private void EmitContinuous(GadgetWorldState world, float dt)
        {
            emitClock += dt;
            if (emitClock < 1f / 30f) return;
            emitClock = 0f;
            foreach (var racer in racers)
            {
                var row = RowFor(world, racer);
                Vector3 forward;
                var at = PositionOf(racer, out forward);
                var turbo = row != null && row.turboTicksRemaining > 0;
                if (turbo || (racer.motor != null && racer.motor.boostActive))
                    Cone(sparks, at - forward * 1.9f + Vector3.up * 0.55f, -forward, 3, 9f,
                        turbo ? Electric : Flame, 0.22f);
                if (row != null && row.spinTicksRemaining > 0)
                {
                    Burst(smoke, at + Vector3.up * 0.3f, 3, 1.5f, SmokeGrey, 0.9f, 0.9f);
                    if (UnityEngine.Random.value < 0.4f)
                        Burst(smoke, at + Vector3.up * 1f, 1, 1f, Bubble, 0.7f, 0.6f);
                }
            }
            if (world != null && world.projectiles != null)
                foreach (var p in world.projectiles)
                {
                    if (p == null) continue;
                    var tail = new Vector3(p.x - Mathf.Sin(p.heading) * 1.2f, p.y + 1f,
                        p.z - Mathf.Cos(p.heading) * 1.2f);
                    Burst(smoke, tail, 2, 0.6f, SmokeGrey, 0.6f, 0.7f);
                    Burst(sparks, tail, 2, 2f, Flame, 0.4f, 0.15f);
                }
        }

        // --- speed feel ------------------------------------------------------

        private void UpdateSpeedFeel(FoundationState state, GadgetWorldState world, float dt)
        {
            Racer local = default;
            var hasLocal = false;
            foreach (var r in racers) if (r.local) { local = r; hasLocal = true; break; }
            var intensity = 0f;
            if (hasLocal && local.motor != null)
            {
                var row = RowFor(world, local);
                if (row != null && row.turboTicksRemaining > 0) intensity = 1f;
                else if (local.motor.boostActive) intensity = 0.65f;
                if (row != null && row.spinTicksRemaining > 0) intensity = 0f;
                if (local.motor.speed < 8f) intensity *= Mathf.Clamp01(local.motor.speed / 8f);
            }
            var reduced = state != null && state.draftProfile != null && state.draftProfile.reducedCameraShake;
            fovKick = Mathf.MoveTowards(fovKick, intensity * 7f, 18f * dt);
            if (drivingCamera == null) drivingCamera = FindAnyObjectByType<DrivingCamera>();
            if (drivingCamera != null) drivingCamera.GadgetFovKick = reduced ? 0f : fovKick;
            var cam = Camera.main;
            if (speedLines == null || cam == null) return;
            if (speedLinesHolder.parent != cam.transform)
            {
                speedLinesHolder.SetParent(cam.transform, false);
                speedLinesHolder.localPosition = Vector3.zero;
                speedLinesHolder.localRotation = Quaternion.identity;
            }
            if (reduced || dt <= 0f) return;
            // Streaks spawn around the edge of the view 30 m ahead and
            // rush past the lens, so they fan outward from the centre.
            lineBudget += intensity * 420f * dt;
            var count = Mathf.FloorToInt(lineBudget);
            lineBudget -= count;
            for (var i = 0; i < count; i++)
            {
                var angle = UnityEngine.Random.value * Mathf.PI * 2f;
                var radius = UnityEngine.Random.Range(9f, 19f);
                var ep = new ParticleSystem.EmitParams
                {
                    position = new Vector3(Mathf.Cos(angle) * radius * 1.5f,
                        Mathf.Sin(angle) * radius, 30f),
                    velocity = new Vector3(0f, 0f, -100f),
                    startLifetime = 0.28f,
                    startSize = UnityEngine.Random.Range(0.05f, 0.11f),
                    startColor = new Color(1f, 1f, 1f, UnityEngine.Random.Range(0.25f, 0.6f))
                };
                speedLines.Emit(ep, 1);
            }
        }

        // --- boost pads --------------------------------------------------------

        private void SyncPads(string courseId)
        {
            if (courseId == padCourseId) return;
            padCourseId = courseId ?? "";
            foreach (var pad in pads) if (pad != null) pad.SetActive(false);
            if (drivingWorld == null) drivingWorld = GetComponent<DrivingWorld>();
            var catalog = drivingWorld != null ? drivingWorld.Catalog : null;
            CourseDefinition course;
            if (catalog == null || catalog.Courses == null || string.IsNullOrEmpty(padCourseId)
                || !catalog.Courses.TryGetValue(padCourseId, out course))
                return;
            var defs = BoostPads.Derive(course);
            for (var i = 0; i < defs.Count && i < PadPoolCapacity; i++)
            {
                if (i >= pads.Count) pads.Add(BuildPad());
                PlacePad(pads[i], defs[i]);
            }
        }

        private GameObject BuildPad()
        {
            var go = new GameObject("boost-pad");
            go.transform.SetParent(root, false);
            var strip = GameObject.CreatePrimitive(PrimitiveType.Quad);
            strip.name = "Chevrons";
            strip.transform.SetParent(go.transform, false);
            strip.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            strip.transform.localScale = new Vector3(4.6f, 8f, 1f);
            Strip(strip, padMaterial);
            var glow = GameObject.CreatePrimitive(PrimitiveType.Quad);
            glow.name = "Glow";
            glow.transform.SetParent(go.transform, false);
            glow.transform.localPosition = new Vector3(0f, -0.01f, 0f);
            glow.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            glow.transform.localScale = new Vector3(6f, 9.6f, 1f);
            Strip(glow, padGlowMaterial);
            return go;
        }

        private static void Strip(GameObject quad, Material material)
        {
            var collider = quad.GetComponent<Collider>();
            if (collider != null) { collider.enabled = false; Destroy(collider); }
            var renderer = quad.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        private static void PlacePad(GameObject go, GadgetBoostPad pad)
        {
            var position = new Vector3(pad.x, pad.y, pad.z);
            var up = Vector3.up;
            // Sit on the actual road surface; the gate height is only the
            // authored line, and roads are gently banked and sloped.
            RaycastHit hit;
            if (Physics.Raycast(position + Vector3.up * 4f, Vector3.down, out hit, 10f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                position.y = hit.point.y;
                up = hit.normal;
            }
            var forward = new Vector3(Mathf.Sin(pad.heading), 0f, Mathf.Cos(pad.heading));
            forward = Vector3.ProjectOnPlane(forward, up).normalized;
            go.transform.SetPositionAndRotation(position + up * 0.09f,
                Quaternion.LookRotation(forward, up));
            go.SetActive(true);
        }

        private void AnimatePads(float dt)
        {
            if (padMaterial == null) return;
            var offset = padMaterial.mainTextureOffset;
            offset.y = Mathf.Repeat(offset.y - dt * 1.6f, 1f);
            padMaterial.mainTextureOffset = offset;
            padFlash = Mathf.MoveTowards(padFlash, 0f, dt * 2.5f);
            var pulse = 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * 6f);
            padGlowMaterial.color = new Color(0.2f, 0.85f, 1f, 0.28f * pulse + 0.3f * padFlash);
        }

        // --- rings -------------------------------------------------------------

        private void SpawnRing(Vector3 at, float from, float to, float life, Color color)
        {
            Ring ring = null;
            foreach (var r in rings) if (!r.line.gameObject.activeSelf) { ring = r; break; }
            if (ring == null)
            {
                if (rings.Count >= RingPoolCapacity) return;
                var go = new GameObject("fx-ring");
                go.transform.SetParent(root, false);
                var line = go.AddComponent<LineRenderer>();
                line.sharedMaterial = additive;
                line.useWorldSpace = false;
                line.loop = true;
                line.positionCount = 48;
                line.shadowCastingMode = ShadowCastingMode.Off;
                line.receiveShadows = false;
                for (var i = 0; i < 48; i++)
                {
                    var a = i * Mathf.PI * 2f / 48f;
                    line.SetPosition(i, new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)));
                }
                ring = new Ring { line = line };
                rings.Add(ring);
            }
            ring.age = 0f; ring.life = life; ring.from = from; ring.to = to; ring.color = color;
            ring.line.transform.position = at;
            ring.line.transform.localScale = Vector3.one * from;
            ring.line.gameObject.SetActive(true);
        }

        private void TickRings(float dt)
        {
            foreach (var ring in rings)
            {
                if (!ring.line.gameObject.activeSelf) continue;
                ring.age += dt;
                var t = Mathf.Clamp01(ring.age / Mathf.Max(0.05f, ring.life));
                if (t >= 1f) { ring.line.gameObject.SetActive(false); continue; }
                var eased = 1f - (1f - t) * (1f - t);
                ring.line.transform.localScale = Vector3.one * Mathf.Lerp(ring.from, ring.to, eased);
                ring.line.widthMultiplier = Mathf.Lerp(0.5f, 0.05f, t);
                var c = ring.color; c.a *= 1f - t;
                ring.line.startColor = ring.line.endColor = c;
            }
        }

        // --- particles ---------------------------------------------------------

        private static void Burst(ParticleSystem ps, Vector3 at, int count, float speed,
            Color color, float life, float size)
        {
            if (ps == null) return;
            for (var i = 0; i < count; i++)
            {
                var dir = UnityEngine.Random.onUnitSphere;
                dir.y = Mathf.Abs(dir.y) * 0.8f + 0.2f;
                var ep = new ParticleSystem.EmitParams
                {
                    position = at + dir * 0.3f,
                    velocity = dir * speed * UnityEngine.Random.Range(0.4f, 1f),
                    startLifetime = life * UnityEngine.Random.Range(0.7f, 1.2f),
                    startSize = size * UnityEngine.Random.Range(0.6f, 1.3f),
                    startColor = color
                };
                ps.Emit(ep, 1);
            }
        }

        private static void Cone(ParticleSystem ps, Vector3 at, Vector3 direction, int count,
            float speed, Color color, float life)
        {
            if (ps == null) return;
            for (var i = 0; i < count; i++)
            {
                var dir = (direction + UnityEngine.Random.insideUnitSphere * 0.35f).normalized;
                var ep = new ParticleSystem.EmitParams
                {
                    position = at,
                    velocity = dir * speed * UnityEngine.Random.Range(0.5f, 1f),
                    startLifetime = life * UnityEngine.Random.Range(0.6f, 1.1f),
                    startSize = UnityEngine.Random.Range(0.25f, 0.5f),
                    startColor = color
                };
                ps.Emit(ep, 1);
            }
        }

        private static readonly Color Gold = new Color(1f, 0.82f, 0.25f, 1f);
        private static readonly Color Flame = new Color(1f, 0.55f, 0.15f, 1f);
        private static readonly Color Electric = new Color(0.35f, 0.85f, 1f, 1f);
        private static readonly Color Bubble = new Color(0.85f, 0.97f, 1f, 0.85f);
        private static readonly Color SmokeGrey = new Color(0.82f, 0.82f, 0.84f, 0.55f);
        private static readonly Color SmokeDark = new Color(0.25f, 0.22f, 0.22f, 0.7f);

        // --- construction --------------------------------------------------------

        private void EnsureBuilt()
        {
            if (root != null) return;
            root = new GameObject("ArcadeJuice").transform;
            root.SetParent(transform, false);
            var shader = Shader.Find("Sprites/Default");
            softDot = SoftDot();
            chevrons = Chevrons();
            additive = new Material(shader) { name = "juice-additive", mainTexture = softDot };
            smokeMaterial = new Material(shader) { name = "juice-smoke", mainTexture = softDot };
            padMaterial = new Material(shader) { name = "juice-pad", mainTexture = chevrons };
            padMaterial.color = new Color(1f, 1f, 1f, 0.95f);
            padMaterial.mainTextureScale = new Vector2(1f, 2f);
            padGlowMaterial = new Material(shader) { name = "juice-pad-glow", mainTexture = softDot };
            padGlowMaterial.color = new Color(0.2f, 0.85f, 1f, 0.3f);
            sparks = BuildParticles("fx-sparks", additive, 1.2f, 900, true);
            smoke = BuildParticles("fx-smoke", smokeMaterial, -0.05f, 700, false);
            speedLinesHolder = new GameObject("fx-speed-lines").transform;
            speedLinesHolder.SetParent(root, false);
            speedLines = BuildParticles("lines", additive, 0f, 400, false, speedLinesHolder);
            var main = speedLines.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            var lineRenderer = speedLines.GetComponent<ParticleSystemRenderer>();
            lineRenderer.renderMode = ParticleSystemRenderMode.Stretch;
            lineRenderer.velocityScale = 0.06f;
            lineRenderer.lengthScale = 1f;
            var fade = speedLines.colorOverLifetime;
            fade.enabled = true;
            fade.color = new ParticleSystem.MinMaxGradient(Fade(new Color(1f, 1f, 1f), 0.15f));
        }

        private ParticleSystem BuildParticles(string name, Material material, float gravity,
            int max, bool shrink, Transform parent = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent != null ? parent : root, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.maxParticles = max;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = gravity;
            main.startSpeed = 0f;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            var emission = ps.emission;
            emission.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;
            var fade = ps.colorOverLifetime;
            fade.enabled = true;
            fade.color = new ParticleSystem.MinMaxGradient(Fade(Color.white, 0.6f));
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, shrink
                ? AnimationCurve.Linear(0f, 1f, 1f, 0.1f)
                : AnimationCurve.Linear(0f, 0.6f, 1f, 2.2f));
            var drag = ps.limitVelocityOverLifetime;
            drag.enabled = true;
            drag.drag = shrink ? 1.2f : 2.5f;
            drag.multiplyDragByParticleSize = false;
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            ps.Play();
            return ps;
        }

        private static Gradient Fade(Color color, float holdUntil)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, holdUntil),
                    new GradientAlphaKey(0f, 1f) });
            return gradient;
        }

        private static Texture2D SoftDot()
        {
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
            { name = "juice-soft-dot", wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[n * n];
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    var dx = (x + 0.5f) / n * 2f - 1f;
                    var dy = (y + 0.5f) / n * 2f - 1f;
                    var d = Mathf.Sqrt(dx * dx + dy * dy);
                    var a = Mathf.Clamp01(1f - d);
                    a = a * a * (3f - 2f * a);
                    pixels[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }

        // Three bright chevrons pointing along +V on a translucent cyan
        // bed: the quad's V axis runs along the travel direction, so a
        // negative V scroll makes them rush forward under the car.
        private static Texture2D Chevrons()
        {
            const int w = 64, h = 128;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true)
            { name = "juice-chevrons", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            var pixels = new Color32[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var u = (x + 0.5f) / w;
                    var v = (y + 0.5f) / h;
                    var across = Mathf.Abs(u - 0.5f) * 2f;
                    // Chevron band: v offset by distance from the centre.
                    var phase = Mathf.Repeat(v * 2f - across * 0.55f, 1f);
                    var band = phase > 0.55f && phase < 0.85f ? 1f : 0f;
                    var edge = across > 0.9f ? 1f : 0f;
                    var bed = 0.35f;
                    var c = Color.Lerp(new Color(0.1f, 0.55f, 1f, bed),
                        new Color(0.85f, 1f, 1f, 1f), Mathf.Max(band, edge * 0.8f));
                    pixels[y * w + x] = c;
                }
            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return tex;
        }

        // A gentle URP post stack on PC: bloom so pads, flames and sparks
        // glow, a little extra contrast and saturation, and a soft
        // vignette. Tablets keep their lean pipeline untouched.
        private void EnsurePostProcessing()
        {
            if (postChecked) return;
            var cam = Camera.main;
            if (cam == null) return;
            postChecked = true;
            if (Application.isMobilePlatform) return;
            var data = cam.GetComponent<UniversalAdditionalCameraData>();
            if (data == null) data = cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "arcade-post";
            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(1f);
            bloom.intensity.Override(0.4f);
            bloom.scatter.Override(0.65f);
            var color = profile.Add<ColorAdjustments>(true);
            color.contrast.Override(10f);
            color.saturation.Override(14f);
            color.postExposure.Override(0.1f);
            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.2f);
            vignette.smoothness.Override(0.45f);
            var go = new GameObject("arcade-post-volume");
            go.transform.SetParent(transform, false);
            volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 5f;
            volume.sharedProfile = profile;
        }

        private void Unbind()
        {
            boundRaceId = "";
            padCourseId = "";
            foreach (var pad in pads) if (pad != null) pad.SetActive(false);
            if (sparks != null) sparks.Clear();
            if (smoke != null) smoke.Clear();
            if (speedLines != null) speedLines.Clear();
            fovKick = 0f;
            if (drivingCamera != null) drivingCamera.GadgetFovKick = 0f;
        }

        private void OnDestroy()
        {
            foreach (var m in new[] { additive, smokeMaterial, padMaterial, padGlowMaterial })
                if (m != null) Destroy(m);
            if (softDot != null) Destroy(softDot);
            if (chevrons != null) Destroy(chevrons);
            if (volume != null && volume.sharedProfile != null) Destroy(volume.sharedProfile);
        }
    }
}
