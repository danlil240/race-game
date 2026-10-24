using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Racing;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    // A live rear view with a short, event-driven cutaway to a rival hit by
    // the local player's foam. Reads presentation/state; never changes motors.
    public sealed class RearViewMirror : MonoBehaviour
    {
        private const float ShotSeconds = 3f;
        private static readonly Color Mint = new Color(.35f, .84f, .72f);
        private static readonly Color Amber = new Color(1f, .68f, .32f);
        private readonly RaceEventCursor cursor = new RaceEventCursor();
        private readonly HashSet<string> ownedPatches = new HashSet<string>();
        private readonly Queue<string> patchOrder = new Queue<string>();
        private Camera mirrorCamera;
        private RenderTexture texture;
        private DrivingWorld world;
        private VisualElement frame, remaining;
        private Image view;
        private Label title, status;
        private string focusedId = "";
        private float shotRemaining;
        private bool blocked, deployed, hasPose;
        private Vector3 previousLocalPosition;

        public Camera RenderCamera => mirrorCamera;
        public string FocusedParticipantId => focusedId;

        public void ApplyFrame(FoundationState state, VisualElement root, float deltaSeconds)
        {
            var solo = state?.soloRace;
            var race = solo?.race ?? state?.networkRace;
            var localId = solo?.race != null ? solo.participantId : state?.network?.localParticipantId;
            var local = Find(race, localId);
            if (!enabled || root == null || local?.session?.motor == null
                || (state.screen != "driving" && state.screen != "lan-race")
                || (race.phase != "running" && race.phase != "countdown"))
            {
                Retire();
                return;
            }
            var raceId = solo?.race != null ? solo.raceId : race.raceId;
            var newRace = cursor.RaceId != raceId;
            if (newRace)
            {
                cursor.Reset(raceId);
                ownedPatches.Clear(); patchOrder.Clear();
                ClearShot(); hasPose = false;
            }
            EnsureCamera();
            if (mirrorCamera == null) return;
            EnsureHud(root);
            var paused = state.paused || race.paused || solo?.paused == true
                || state.network?.paused == true;
            var dt = paused ? 0f : Mathf.Max(0, deltaSeconds);
            shotRemaining = Mathf.Max(0, shotRemaining - dt);
            if (shotRemaining <= 0) ClearShot();

            if (race.gadgets?.patches != null)
                foreach (var patch in race.gadgets.patches)
                    if (patch != null && patch.ownerParticipantId == localId)
                        RememberPatch(patch.patchId);
            // Chronological events also preserve ownership if a short-lived
            // patch was dropped and expired between two rendered snapshots.
            foreach (var ev in cursor.Consume(state.drivingEvents))
            {
                if (ev.kind == GadgetRules.EventFoamDropped && ev.participantId == localId)
                {
                    RememberPatch(ev.detail);
                    if (!paused) { deployed = true; shotRemaining = ShotSeconds; }
                }
                if (paused || !ownedPatches.Contains(ev.detail ?? "")
                    || ev.participantId == localId || Find(race, ev.participantId) == null)
                    continue;
                if (ev.kind != GadgetRules.EventFoamHit && ev.kind != GadgetRules.EventShieldBlocked)
                    continue;
                focusedId = ev.participantId;
                blocked = ev.kind == GadgetRules.EventShieldBlocked;
                deployed = false;
                shotRemaining = ShotSeconds;
                hasPose = false; // cut immediately so a short hit is visible
            }

            var localMotor = local.session.motor;
            var localPosition = RacerPosition(local, localMotor);
            var forward = new Vector3(Mathf.Sin(localMotor.heading), 0, Mathf.Cos(localMotor.heading));
            var target = Find(race, focusedId);
            if (target?.session?.motor == null && !string.IsNullOrEmpty(focusedId)) ClearShot();
            Vector3 desired, lookAt;
            if (!string.IsNullOrEmpty(focusedId))
            {
                var motor = target.session.motor;
                var position = RacerPosition(target, motor);
                var targetForward = new Vector3(Mathf.Sin(motor.heading), 0, Mathf.Cos(motor.heading));
                desired = position + targetForward * 6f + Vector3.up * 2.8f;
                lookAt = position + Vector3.up * .8f;
            }
            else
            {
                desired = localPosition - forward * 2.8f + Vector3.up * 3.2f;
                lookAt = desired - forward * 24f - Vector3.up * 3.5f;
            }
            var snap = !hasPose || (localPosition - previousLocalPosition).sqrMagnitude > 18f * 18f;
            var blend = snap ? 1f : 1f - Mathf.Exp(-12f * dt);
            mirrorCamera.fieldOfView = string.IsNullOrEmpty(focusedId) ? 65f : 38f;
            mirrorCamera.transform.SetPositionAndRotation(
                Vector3.Lerp(mirrorCamera.transform.position, desired, blend),
                Quaternion.Slerp(mirrorCamera.transform.rotation,
                    Quaternion.LookRotation(lookAt - desired, Vector3.up), blend));
            previousLocalPosition = localPosition; hasPose = true;
            mirrorCamera.enabled = true;
            UpdateHud(state.language == "he");
        }

        private static NetworkRacerState Find(NetworkRaceState race, string id)
        {
            if (string.IsNullOrEmpty(id) || race?.racers == null) return null;
            foreach (var racer in race.racers)
                if (racer != null && racer.participantId == id) return racer;
            return null;
        }

        private Vector3 RacerPosition(NetworkRacerState racer, MotorState motor)
        {
            if (world == null) world = GetComponent<DrivingWorld>();
            var visual = world != null ? world.RacerPresentation(racer.participantId) : null;
            return visual != null && visual.gameObject.activeInHierarchy
                ? visual.transform.position : new Vector3(motor.x, motor.y, motor.z);
        }

        private void RememberPatch(string id)
        {
            if (string.IsNullOrEmpty(id) || !ownedPatches.Add(id)) return;
            patchOrder.Enqueue(id);
            while (patchOrder.Count > 64) ownedPatches.Remove(patchOrder.Dequeue());
        }

        private void EnsureCamera()
        {
            if (mirrorCamera != null) return;
            var source = Camera.main;
            if (source == null) return;
            var holder = new GameObject("Rear view camera");
            holder.transform.SetParent(transform, false);
            mirrorCamera = holder.AddComponent<Camera>();
            mirrorCamera.CopyFrom(source);
            mirrorCamera.enabled = false;
            mirrorCamera.tag = "Untagged";
            mirrorCamera.fieldOfView = 65f;
            mirrorCamera.nearClipPlane = .2f;
            mirrorCamera.farClipPlane = 180f;
            mirrorCamera.allowHDR = false;
            mirrorCamera.allowMSAA = false;
            mirrorCamera.depth = source.depth - 1;
            mirrorCamera.rect = new Rect(0, 0, 1, 1);
            mirrorCamera.aspect = 3f;
            var data = holder.AddComponent<UniversalAdditionalCameraData>();
            data.renderShadows = false;
            data.renderPostProcessing = false;
            data.requiresColorOption = CameraOverrideOption.Off;
            data.requiresDepthOption = CameraOverrideOption.Off;
            texture = new RenderTexture(600, 200, 16, RenderTextureFormat.ARGB32) {
                name = "Rear view mirror", antiAliasing = 1, filterMode = FilterMode.Bilinear };
            texture.Create();
            mirrorCamera.targetTexture = texture;
        }

        private void EnsureHud(VisualElement root)
        {
            if (frame != null && frame.parent == root) return;
            frame?.RemoveFromHierarchy();
            frame = new VisualElement { name = "hud.mirror", pickingMode = PickingMode.Ignore };
            frame.style.position = Position.Absolute;
            // Keep the driving line clear: stack under the left-side boost
            // meter rather than covering the vanishing point of the road.
            frame.style.top = 108;
            frame.style.left = 16;
            frame.style.width = 236;
            frame.style.backgroundColor = new Color(.055f, .10f, .15f, .98f);
            frame.style.paddingLeft = frame.style.paddingRight = 5;
            frame.style.paddingTop = 4; frame.style.paddingBottom = 5;
            frame.style.borderTopLeftRadius = frame.style.borderTopRightRadius = 16;
            frame.style.borderBottomLeftRadius = frame.style.borderBottomRightRadius = 20;
            frame.style.borderLeftWidth = frame.style.borderRightWidth = 2;
            frame.style.borderTopWidth = frame.style.borderBottomWidth = 2;
            frame.languageDirection = LanguageDirection.LTR;
            var header = new VisualElement { pickingMode = PickingMode.Ignore };
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.paddingLeft = header.style.paddingRight = 7;
            header.style.height = 21;
            title = Caption("hud.mirror.title", 10, new Color(.76f, .85f, .9f));
            header.Add(title);
            var live = Caption("hud.mirror.live", 9, Mint);
            live.text = "● LIVE"; header.Add(live);
            frame.Add(header);
            view = new Image { name = "hud.mirror.view", image = texture,
                pickingMode = PickingMode.Ignore, scaleMode = ScaleMode.StretchToFill,
                uv = new Rect(1, 0, -1, 1) };
            view.style.flexShrink = 0;
            // Image has a fixed 3:1 aspect at all supported HUD widths.
            view.RegisterCallback<GeometryChangedEvent>(e => {
                var height = e.newRect.width / 3f;
                if (Mathf.Abs(e.newRect.height - height) > .5f) view.style.height = height;
            });
            view.style.height = 96;
            view.style.overflow = Overflow.Hidden;
            view.style.borderTopLeftRadius = view.style.borderTopRightRadius = 8;
            view.style.borderBottomLeftRadius = view.style.borderBottomRightRadius = 12;
            frame.Add(view);
            status = Caption("hud.mirror.status", 11, Mint);
            status.style.height = 23;
            status.style.unityTextAlign = TextAnchor.MiddleCenter;
            frame.Add(status);
            remaining = new VisualElement { name = "hud.mirror.timer", pickingMode = PickingMode.Ignore };
            remaining.style.height = 2; remaining.style.backgroundColor = Mint;
            frame.Add(remaining);
            // Keep every modal above the mirror, including rebuilt pause HUDs.
            var index = root.childCount;
            foreach (var child in root.Children())
                if (child.name != null && child.name.StartsWith("overlay.")) { index = root.IndexOf(child); break; }
            root.Insert(index, frame);
        }

        private static Label Caption(string name, int size, Color color)
        {
            var label = new Label { name = name, pickingMode = PickingMode.Ignore };
            label.style.fontSize = size; label.style.color = color;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.marginLeft = label.style.marginRight = 0;
            return label;
        }

        private void UpdateHud(bool hebrew)
        {
            var focus = !string.IsNullOrEmpty(focusedId);
            var active = focus || deployed;
            var accent = blocked && focus ? Amber : Mint;
            title.text = hebrew ? "מבט לאחור" : "REAR VIEW";
            frame.Q<Label>("hud.mirror.live").text = hebrew ? "● חי" : "● LIVE";
            status.text = focus ? (blocked ? (hebrew ? "המגן חסם" : "SHIELD BLOCKED")
                : (hebrew ? "היריב הואט" : "TRAP HIT · RIVAL SLOWED"))
                : deployed ? (hebrew ? "המלכודת מוכנה" : "TRAP DEPLOYED · WATCH BEHIND")
                : (hebrew ? "שימו לב ליריבים" : "WATCH YOUR RIVALS");
            status.style.color = active ? accent : new Color(.65f, .76f, .82f);
            var border = active ? accent : new Color(.27f, .40f, .49f);
            frame.style.borderLeftColor = frame.style.borderRightColor = border;
            frame.style.borderTopColor = frame.style.borderBottomColor = border;
            remaining.style.backgroundColor = accent;
            remaining.style.width = Length.Percent(active ? shotRemaining / ShotSeconds * 100 : 0);
        }

        private void ClearShot()
        {
            if (!string.IsNullOrEmpty(focusedId)) hasPose = false;
            focusedId = ""; shotRemaining = 0; deployed = blocked = false;
        }

        private void Retire()
        {
            frame?.RemoveFromHierarchy(); frame = null;
            ClearShot(); hasPose = false;
            cursor.Reset(""); ownedPatches.Clear(); patchOrder.Clear();
            if (mirrorCamera != null)
            {
                mirrorCamera.enabled = false;
                mirrorCamera.targetTexture = null;
                Destroy(mirrorCamera.gameObject); mirrorCamera = null;
            }
            if (texture != null) { texture.Release(); Destroy(texture); texture = null; }
        }

        private void OnDisable() => Retire();
    }
}
