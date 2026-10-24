using System.Collections.Generic;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using UnityEngine;
using UnityEngine.Rendering;

namespace FamilyRacing
{
    public sealed partial class GadgetPresenter
    {
        private sealed class WeaponView
        {
            internal string id;
            internal GameObject go;
            internal Transform signal;
            internal LineRenderer ring;
            internal bool seen;
            internal Vector3 previous, current;
            internal long tick = -1;
        }

        private readonly List<WeaponView> missiles = new List<WeaponView>();
        private readonly List<WeaponView> mines = new List<WeaponView>();
        private readonly List<WeaponView> empRings = new List<WeaponView>();
        private Material weaponMetal, weaponRed, weaponGold, weaponGlow, pulseMaterial;

        public int ActiveWeaponCount
        {
            get { return CountWeapons(missiles) + CountWeapons(mines) + CountWeapons(empRings); }
        }

        private static int CountWeapons(List<WeaponView> pool)
        {
            var n = 0;
            foreach (var view in pool) if (view.go.activeSelf) n++;
            return n;
        }

        private void SyncWeapons(GadgetWorldState world, float alpha)
        {
            PrepareWeapons(missiles, world, "missile");
            PrepareWeapons(mines, world, "mine");
            PrepareWeapons(empRings, world, "emp");
            foreach (var missile in world.projectiles)
            {
                if (missile == null) continue;
                var view = WeaponFor(missiles, missile.projectileId, "missile", NetworkSettings.GadgetProjectileCapacity);
                if (view == null) continue;
                var position = new Vector3(missile.x, missile.y + 1f, missile.z);
                if (view.tick < 0) view.previous = view.current = position;
                else if (view.tick != world.tick) { view.previous = view.current; view.current = position; }
                view.tick = world.tick;
                view.go.transform.position = Vector3.Lerp(view.previous, view.current, Mathf.Clamp01(alpha));
                view.go.transform.rotation = Quaternion.Euler(0f, missile.heading * Mathf.Rad2Deg, 0f);
                view.signal.localScale = new Vector3(.24f, .24f, .65f + .16f * Mathf.Sin(bobPhase * 40f));
            }
            foreach (var mine in world.patches)
            {
                if (mine == null || mine.kind != GadgetKind.Mine) continue;
                var view = WeaponFor(mines, mine.patchId, "mine", NetworkSettings.GadgetPatchCapacity);
                if (view == null) continue;
                view.go.transform.position = new Vector3(mine.x, mine.y + .18f, mine.z);
                var armed = mine.armingTicksRemaining <= 0;
                view.signal.GetComponent<Renderer>().sharedMaterial = armed ? weaponRed : weaponGlow;
                view.signal.gameObject.SetActive(!armed || Mathf.Sin(bobPhase * 14f) > -.2f);
                view.signal.localScale = Vector3.one * (armed ? .45f : .22f);
                view.go.transform.localScale = Vector3.one * (armed ? 1f : .8f);
            }
            foreach (var pulse in world.pulses)
            {
                if (pulse == null) continue;
                var view = WeaponFor(empRings, pulse.pulseId, "emp", NetworkSettings.GadgetPulseCapacity);
                if (view == null) continue;
                view.go.transform.position = new Vector3(pulse.x, pulse.y + .35f, pulse.z);
                var age = 1f - (float)pulse.ticksRemaining / Mathf.Max(1, pulse.totalTicks);
                view.go.transform.localScale = Vector3.one * Mathf.Lerp(.1f, pulse.radiusMeters, age);
                var color = new Color(.35f, .8f, 1f, 1f - age);
                view.ring.startColor = view.ring.endColor = color;
            }
            RetireWeapons(missiles); RetireWeapons(mines); RetireWeapons(empRings);
        }

        // Retire missing identities before allocating replacements, including
        // a full-capacity snapshot where every effect changed this frame.
        private static void PrepareWeapons(List<WeaponView> pool, GadgetWorldState world, string kind)
        {
            foreach (var view in pool)
            {
                var live = false;
                if (kind == "missile")
                    foreach (var p in world.projectiles) { if (p != null && p.projectileId == view.id) { live = true; break; } }
                else if (kind == "mine")
                    foreach (var p in world.patches) { if (p != null && p.kind == GadgetKind.Mine && p.patchId == view.id) { live = true; break; } }
                else
                    foreach (var p in world.pulses) { if (p != null && p.pulseId == view.id) { live = true; break; } }
                view.seen = false;
                if (!live) { view.id = null; view.tick = -1; view.go.SetActive(false); }
            }
        }

        private static void RetireWeapons(List<WeaponView> pool)
        {
            foreach (var view in pool)
                if (!view.seen) { view.id = null; view.tick = -1; view.go.SetActive(false); }
        }

        private void DeactivateWeapons()
        {
            foreach (var view in missiles) view.seen = false;
            foreach (var view in mines) view.seen = false;
            foreach (var view in empRings) view.seen = false;
            RetireWeapons(missiles); RetireWeapons(mines); RetireWeapons(empRings);
        }

        private WeaponView WeaponFor(List<WeaponView> pool, string id, string kind, int capacity)
        {
            WeaponView selected = null;
            foreach (var view in pool) if (view.id == id) { selected = view; break; }
            if (selected == null)
            {
                foreach (var view in pool) if (view.id == null) { selected = view; break; }
                if (selected == null)
                {
                    if (pool.Count >= capacity) return null;
                    selected = BuildWeapon(kind); pool.Add(selected);
                }
                selected.id = id; selected.tick = -1;
            }
            selected.seen = true; selected.go.SetActive(true);
            selected.go.name = "gadget-" + kind + "-" + id;
            return selected;
        }

        private WeaponView BuildWeapon(string kind)
        {
            if (weaponMetal == null)
            {
                weaponMetal = BuildMaterial(new Color(.8f, .9f, 1f));
                weaponRed = BuildMaterial(new Color(1f, .25f, .18f));
                weaponGold = BuildMaterial(new Color(1f, .65f, .12f));
                weaponGlow = BuildMaterial(new Color(1f, .9f, .35f));
                pulseMaterial = new Material(Shader.Find("Sprites/Default"));
            }
            var view = new WeaponView { go = new GameObject("gadget-" + kind) };
            view.go.transform.SetParent(Container(), false);
            if (kind == "missile")
            {
                var body = WeaponPart(view.go, PrimitiveType.Capsule, Vector3.zero, new Vector3(.35f, .8f, .35f), weaponMetal);
                body.localRotation = Quaternion.Euler(90f, 0f, 0f);
                WeaponPart(view.go, PrimitiveType.Sphere, new Vector3(0, 0, .65f), new Vector3(.4f, .4f, .65f), weaponRed);
                WeaponPart(view.go, PrimitiveType.Cube, new Vector3(0, 0, -.45f), new Vector3(1f, .1f, .5f), weaponRed);
                WeaponPart(view.go, PrimitiveType.Cube, new Vector3(0, 0, -.45f), new Vector3(.1f, .85f, .5f), weaponRed);
                view.signal = WeaponPart(view.go, PrimitiveType.Sphere, new Vector3(0, 0, -1f), new Vector3(.24f, .24f, .65f), weaponGlow);
            }
            else if (kind == "mine")
            {
                WeaponPart(view.go, PrimitiveType.Cylinder, Vector3.zero, new Vector3(1.6f, .15f, 1.6f), weaponGold);
                WeaponPart(view.go, PrimitiveType.Sphere, new Vector3(0, .15f, 0), new Vector3(1.1f, .5f, 1.1f), weaponMetal);
                for (var i = 0; i < 4; i++)
                {
                    var fin = WeaponPart(view.go, PrimitiveType.Cube, Vector3.zero, new Vector3(2f, .15f, .18f), weaponGold);
                    fin.localRotation = Quaternion.Euler(0, i * 45f, 0);
                }
                view.signal = WeaponPart(view.go, PrimitiveType.Sphere, new Vector3(0, .5f, 0), Vector3.one * .45f, weaponRed);
            }
            else
            {
                view.ring = view.go.AddComponent<LineRenderer>();
                view.ring.sharedMaterial = pulseMaterial; view.ring.useWorldSpace = false;
                view.ring.loop = true; view.ring.positionCount = 64; view.ring.widthMultiplier = .3f;
                view.ring.shadowCastingMode = ShadowCastingMode.Off;
                for (var i = 0; i < 64; i++)
                {
                    var angle = i * Mathf.PI * 2f / 64f;
                    view.ring.SetPosition(i, new Vector3(Mathf.Sin(angle), 0, Mathf.Cos(angle)));
                }
            }
            return view;
        }

        private static Transform WeaponPart(GameObject parent, PrimitiveType shape, Vector3 position, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(shape);
            part.transform.SetParent(parent.transform, false);
            part.transform.localPosition = position; part.transform.localScale = scale;
            var collider = part.GetComponent<Collider>();
            if (collider != null) { collider.enabled = false; Destroy(collider); }
            var renderer = part.GetComponent<Renderer>();
            renderer.sharedMaterial = material; renderer.shadowCastingMode = ShadowCastingMode.Off;
            return part.transform;
        }

        private void OnDestroy()
        {
            foreach (var material in new[] { weaponMetal, weaponRed, weaponGold, weaponGlow, pulseMaterial })
                if (material != null) Destroy(material);
        }
    }
}
