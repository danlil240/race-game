using System;
using System.Collections.Generic;
using System.Globalization;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Racing;
using UnityEngine;

namespace FamilyRacing
{
    // Race effects presenter (M4 Task 5): pooled visual effects driven by
    // the published race state.
    //
    //   * Event bursts (landing puff, wall shunt, checkpoint sparkle,
    //     finish burst) fire off driving events deduplicated by event
    //     identity — the same cursor discipline the audio presenter uses:
    //     repeated snapshots, correction re-deliveries and reconnects
    //     never replay a consumed burst, and the first frame after a
    //     (re)bind is history baseline, not a replay.
    //   * Continuous emitters ride live motor state: dust puffs behind a
    //     drifting grounded racer, flare puffs behind a boosting one —
    //     throttled by authored emit intervals.
    //   * Pools are bounded by the catalog's authored poolSize and grow
    //     lazily; exhaustion is counted on the published pool facts and
    //     reduces decoration only — the landing/thump/readability cues
    //     the race needs are carried by audio and camera, so a starved
    //     pool never hides required feedback.
    //   * Prefab-less effect slots report on Facts.missingEffects and log
    //     once — never a silent skip.
    public sealed class RaceEffectsPresenter : MonoBehaviour
    {
        private const int MissingCapacity = 16;

        private sealed class Instance
        {
            internal Pool pool;
            internal GameObject go;
            internal Renderer[] renderers;
            internal MaterialPropertyBlock mpb;
            internal Color[] baseColors;
            internal float remaining;
            internal float life = 1f;
            internal float growTo = 1.6f;
            internal bool faceCamera;
            internal Vector3 baseScale = Vector3.one;
            internal bool active;
        }

        private sealed class Pool
        {
            internal RacePresentationCatalogAsset.Effect entry;
            internal readonly List<Instance> all = new List<Instance>();
            internal readonly Queue<Instance> free = new Queue<Instance>();
            internal int exhausted;
        }

        private RacePresentationCatalogAsset catalog;
        private readonly RaceEventCursor cursor = new RaceEventCursor();
        private bool contextActive;
        private Transform container;
        private readonly Dictionary<string, Pool> pools =
            new Dictionary<string, Pool>(StringComparer.Ordinal);
        private readonly List<Instance> activeInstances = new List<Instance>();
        // Continuous-emitter cooldowns keyed "<emit>@<racerKey>"; a false
        // condition resets the key so the next start emits immediately.
        private readonly Dictionary<string, float> emitCooldowns =
            new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly HashSet<string> missingSet =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> missingOrder = new List<string>();
        private int missingDropped;
        private readonly HashSet<string> missingLogged =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> exhaustedLogged =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly RaceEffectsState snapshot = new RaceEffectsState();

        public RaceEffectsState Facts { get { return snapshot.Copy(); } }
        // A bound catalog is the honest precondition for publishing the
        // effects slice: unbound means no effect can ever spawn, so the
        // schema-5 field stays null rather than reporting a phantom
        // catalog identity.
        public bool Bound { get { return catalog != null; } }

        public void Bind(RacePresentationCatalogAsset asset)
        {
            catalog = asset;
        }

        // (Re)bind to a race identity: active bursts retire (they belong
        // to the old race's history) and the next ApplyFrame baselines
        // the event stream — attach/rejoin never replays history. The
        // pool objects themselves persist; pools are resources, not
        // race state.
        public void Reset(string raceId)
        {
            DeactivateAll();
            cursor.Reset(raceId ?? "");
            ResetRaceCounters();
            contextActive = false;
            emitCooldowns.Clear();
            UpdateFacts();
        }

        public void ApplyFrame(FoundationState state, float deltaSeconds)
        {
            var raceId = RacePresentationUtil.RaceId(state);
            if (raceId == null)
            {
                if (contextActive || activeInstances.Count > 0)
                    DeactivateAll();
                contextActive = false;
                cursor.Reset("");
                emitCooldowns.Clear();
                ResetRaceCounters();
                UpdateFacts();
                return;
            }
            if (!contextActive || raceId != cursor.RaceId)
            {
                DeactivateAll();
                cursor.Reset(raceId);
                emitCooldowns.Clear();
                ResetRaceCounters();
            }
            contextActive = true;
            var paused = RacePresentationUtil.IsPaused(state);

            var fresh = cursor.Consume(state.drivingEvents);
            if (!paused)
            {
                foreach (var ev in fresh) FireTriggers(ev, state);
                EmitContinuous(state, Mathf.Max(0f, deltaSeconds));
            }
            TickInstances(Mathf.Max(0f, deltaSeconds));
            UpdateFacts();
        }

        // --- event bursts -------------------------------------------------

        private void FireTriggers(DrivingEvent ev, FoundationState state)
        {
            if (catalog == null || ev == null) return;
            var triggers = catalog.TriggersFor(ev.kind);
            if (triggers.Count == 0) return;
            var position = RacePresentationUtil.PositionFor(state, ev);
            // The event detail carries a magnitude for some kinds
            // (landing fall speed, wall impact speed) — scale the burst
            // with it, bounded.
            var intensity = 1f;
            float parsed;
            if (float.TryParse(ev.detail ?? "", NumberStyles.Float,
                    CultureInfo.InvariantCulture, out parsed)
                && parsed > 0f)
                intensity = Mathf.Clamp(parsed / 8f, 0.5f, 2f);
            foreach (var trigger in triggers)
            {
                var count = Mathf.Clamp(trigger.count, 1, 4);
                for (var i = 0; i < count; i++)
                {
                    var offset = count > 1
                        ? new Vector3(
                            Mathf.Cos(i * 6.283185f / count) * 0.9f, 0f,
                            Mathf.Sin(i * 6.283185f / count) * 0.9f)
                        : Vector3.zero;
                    Play(trigger.effectId, position + offset, intensity);
                }
            }
        }

        // --- continuous emitters ---------------------------------------------

        private void EmitContinuous(FoundationState state, float dt)
        {
            if (catalog == null) return;
            var racers = RacePresentationUtil.Racers(state);
            foreach (var racer in racers)
            {
                var motor = racer.motor;
                if (motor == null) continue;
                var pos = new Vector3(motor.x, motor.y, motor.z);
                var forward = new Vector3(Mathf.Sin(motor.heading), 0f,
                    Mathf.Cos(motor.heading));
                var behind = pos - forward * 1.3f;

                if (motor.drifting && motor.grounded
                    && Mathf.Abs(motor.speed) > 1f)
                {
                    if (CoolingDown("drift@" + racer.key, dt,
                            catalog.driftEmitSeconds))
                    {
                        var dust = Mathf.Clamp01(
                            Mathf.Abs(motor.lateralSpeed) / 6f + 0.4f);
                        Play(catalog.driftEffectId, behind, dust);
                    }
                }
                else emitCooldowns["drift@" + racer.key] = 0f;

                if (motor.boostActive)
                {
                    if (CoolingDown("boost@" + racer.key, dt,
                            catalog.boostEmitSeconds))
                        Play(catalog.boostEffectId, behind, 1.1f);
                }
                else emitCooldowns["boost@" + racer.key] = 0f;
            }
        }

        // Returns true when the cooldown elapsed this frame (emit now)
        // and restarts the interval; false while still cooling.
        private bool CoolingDown(string key, float dt, float interval)
        {
            float left;
            emitCooldowns.TryGetValue(key, out left);
            left -= dt;
            if (left > 0f)
            {
                emitCooldowns[key] = left;
                return false;
            }
            emitCooldowns[key] = Mathf.Max(0.01f, interval);
            return true;
        }

        // --- pool plumbing --------------------------------------------------

        private void Play(string effectId, Vector3 position, float intensity)
        {
            if (catalog == null || string.IsNullOrEmpty(effectId)) return;
            var entry = catalog.EffectFor(effectId);
            if (entry == null)
            {
                RecordMissing(effectId);
                return;
            }
            if (entry.prefab == null)
            {
                RecordMissing(effectId);
                return;
            }
            var pool = PoolFor(effectId, entry);
            if (pool.free.Count == 0 && pool.all.Count < entry.poolSize)
                GrowPool(pool);
            if (pool.free.Count == 0)
            {
                pool.exhausted++;
                if (exhaustedLogged.Add(effectId))
                    Debug.LogWarning("RaceEffectsPresenter: effect pool '"
                        + effectId + "' exhausted (" + entry.poolSize
                        + ") — decoration reduced, gameplay feedback "
                        + "unaffected.");
                return;
            }
            var inst = pool.free.Dequeue();
            inst.remaining = inst.life;
            inst.active = true;
            inst.go.transform.position = position;
            inst.baseScale = Vector3.one * Mathf.Max(0.05f, intensity);
            inst.go.transform.localScale = inst.baseScale;
            SetAlpha(inst, 1f);
            inst.go.SetActive(true);
            if (!activeInstances.Contains(inst)) activeInstances.Add(inst);
        }

        private Pool PoolFor(string effectId,
            RacePresentationCatalogAsset.Effect entry)
        {
            Pool pool;
            if (!pools.TryGetValue(effectId, out pool))
            {
                pool = new Pool { entry = entry };
                pools[effectId] = pool;
            }
            else if (pool.entry != entry) pool.entry = entry;
            return pool;
        }

        private void GrowPool(Pool pool)
        {
            if (container == null)
            {
                var holder = new GameObject("RaceEffects");
                holder.transform.SetParent(transform, false);
                container = holder.transform;
            }
            var go = Instantiate(pool.entry.prefab, container);
            go.name = pool.entry.effectId + "-" + pool.all.Count;
            var inst = new Instance
            {
                pool = pool,
                go = go,
                renderers = go.GetComponentsInChildren<Renderer>(true),
                mpb = new MaterialPropertyBlock(),
                life = Mathf.Max(0.05f, pool.entry.lifetimeSeconds),
                growTo = pool.entry.growTo,
                faceCamera = pool.entry.faceCamera
            };
            if (inst.renderers != null)
            {
                inst.baseColors = new Color[inst.renderers.Length];
                for (var i = 0; i < inst.renderers.Length; i++)
                    inst.baseColors[i] = inst.renderers[i] != null
                        && inst.renderers[i].sharedMaterial != null
                        && inst.renderers[i].sharedMaterial.HasProperty(
                            BaseColorProp(inst.renderers[i]))
                        ? inst.renderers[i].sharedMaterial.GetColor(
                            BaseColorProp(inst.renderers[i]))
                        : Color.white;
            }
            go.SetActive(false);
            pool.all.Add(inst);
            pool.free.Enqueue(inst);
        }

        private static int BaseColorProp(Renderer renderer)
        {
            return renderer.sharedMaterial != null
                && renderer.sharedMaterial.HasProperty("_BaseColor")
                ? Shader.PropertyToID("_BaseColor")
                : Shader.PropertyToID("_Color");
        }

        private void TickInstances(float dt)
        {
            if (activeInstances.Count == 0) return;
            var cam = Camera.main;
            for (var i = activeInstances.Count - 1; i >= 0; i--)
            {
                var inst = activeInstances[i];
                if (inst == null || inst.go == null)
                {
                    activeInstances.RemoveAt(i);
                    continue;
                }
                inst.remaining -= dt;
                if (inst.remaining <= 0f)
                {
                    Retire(inst);
                    continue;
                }
                var t = 1f - inst.remaining / inst.life;
                inst.go.transform.localScale =
                    inst.baseScale * Mathf.Lerp(1f, inst.growTo, t);
                SetAlpha(inst, 1f - t);
                if (inst.faceCamera && cam != null)
                {
                    var toCam = cam.transform.position
                        - inst.go.transform.position;
                    toCam.y = 0f;
                    if (toCam.sqrMagnitude > 0.01f)
                        inst.go.transform.rotation =
                            Quaternion.LookRotation(toCam.normalized);
                }
            }
        }

        private void SetAlpha(Instance inst, float alpha)
        {
            if (inst.renderers == null || inst.mpb == null) return;
            for (var i = 0; i < inst.renderers.Length; i++)
            {
                var renderer = inst.renderers[i];
                if (renderer == null) continue;
                var prop = BaseColorProp(renderer);
                var color = inst.baseColors != null && i < inst.baseColors.Length
                    ? inst.baseColors[i] : Color.white;
                color.a = Mathf.Clamp01(alpha);
                inst.mpb.SetColor(prop, color);
                renderer.SetPropertyBlock(inst.mpb);
            }
        }

        private void Retire(Instance inst)
        {
            inst.remaining = 0f;
            inst.active = false;
            if (inst.go != null) inst.go.SetActive(false);
            activeInstances.Remove(inst);
            if (inst.pool != null) inst.pool.free.Enqueue(inst);
        }

        private void DeactivateAll()
        {
            for (var i = activeInstances.Count - 1; i >= 0; i--)
            {
                var inst = activeInstances[i];
                if (inst != null)
                {
                    inst.remaining = 0f;
                    inst.active = false;
                    if (inst.go != null) inst.go.SetActive(false);
                }
            }
            // Every instance returns to its pool's free queue.
            activeInstances.Clear();
            foreach (var pool in pools.Values)
            {
                pool.free.Clear();
                foreach (var inst in pool.all) pool.free.Enqueue(inst);
            }
        }

        // Per-race counters live and die with the race binding: every
        // re-baseline (explicit Reset, a new race identity on the
        // stream, or teardown to no race) zeroes them so a rematch can
        // never publish the previous race's pressure. Pool objects are
        // resources and persist across races; their `exhausted`
        // counters do not. The log-once guards (`missingLogged`,
        // `exhaustedLogged`) stay app-lifetime — they bound warning
        // spam, they are not facts.
        private void ResetRaceCounters()
        {
            foreach (var pool in pools.Values) pool.exhausted = 0;
            missingSet.Clear();
            missingOrder.Clear();
            missingDropped = 0;
        }

        private void RecordMissing(string effectId)
        {
            if (missingSet.Add(effectId))
            {
                missingOrder.Add(effectId);
                while (missingOrder.Count > MissingCapacity)
                {
                    missingSet.Remove(missingOrder[0]);
                    missingOrder.RemoveAt(0);
                    missingDropped++;
                }
            }
            if (missingLogged.Add(effectId))
                Debug.LogWarning("RaceEffectsPresenter: effect '" + effectId
                    + "' has no prefab bound in the catalog — reporting "
                    + "missing instead of spawning.");
        }

        private void UpdateFacts()
        {
            snapshot.catalogId = catalog != null ? catalog.catalogId : "none";
            snapshot.activeEffects = activeInstances.Count;
            snapshot.pools.Clear();
            var ids = new List<string>(pools.Keys);
            ids.Sort(StringComparer.Ordinal);
            // Publish every authored effect's pool (created or not) so
            // capacity truth is visible before first use.
            if (catalog != null && catalog.effects != null)
                foreach (var entry in catalog.effects)
                    if (entry != null && !pools.ContainsKey(entry.effectId))
                        ids.Add(entry.effectId);
            ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                Pool pool;
                var entry = catalog != null ? catalog.EffectFor(id) : null;
                if (!pools.TryGetValue(id, out pool))
                {
                    snapshot.pools.Add(new EffectPoolFacts
                    {
                        effectId = id,
                        capacity = entry != null ? entry.poolSize : 0,
                        active = 0,
                        exhausted = 0
                    });
                    continue;
                }
                var active = 0;
                foreach (var inst in pool.all) if (inst.active) active++;
                snapshot.pools.Add(new EffectPoolFacts
                {
                    effectId = id,
                    capacity = entry != null ? entry.poolSize : pool.all.Count,
                    active = active,
                    exhausted = pool.exhausted
                });
            }
            snapshot.missingEffects.Clear();
            snapshot.missingEffects.AddRange(missingOrder);
            snapshot.missingDropped = missingDropped;
        }

        private void OnDisable()
        {
            DeactivateAll();
            contextActive = false;
            cursor.Reset("");
            emitCooldowns.Clear();
            ResetRaceCounters();
            UpdateFacts();
        }

        internal int ActiveCount(string effectId)
        {
            Pool pool;
            if (!pools.TryGetValue(effectId ?? "", out pool)) return 0;
            var count = 0;
            foreach (var inst in pool.all) if (inst.active) count++;
            return count;
        }
    }
}
