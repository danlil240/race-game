using System;
using System.Collections.Generic;
using FamilyRacing.Core.Foundation;
using UnityEngine;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    public sealed partial class FoundationShell
    {
        private bool TouchRaceLayout => !playerPresentation || Application.isMobilePlatform
            || RenderProfileController.ResolveTablet(Application.platform, Environment.GetCommandLineArgs());

        // One deck for solo, tutorial and LAN. Only the buttons pick input;
        // the open space between the thumb clusters stays transparent.
        private void BuildRaceTouchControls(FoundationState state, bool network)
        {
            var prefix = network ? "lan.race." : "driving.";
            var controlsName = network ? "lan.controls" : "hud.controls";
            var deck = new VisualElement { name = controlsName, pickingMode = PickingMode.Ignore };
            deck.AddToClassList("race-deck");
            deck.languageDirection = LanguageDirection.LTR;
            deck.styleSheets.Add(Resources.Load<StyleSheet>("RaceTouchControls"));
            root.Add(deck);

            var keys = AddLabel(root, T("hud.keys", state), 15, Deep);
            keys.name = network ? "lan.hud.keys" : "hud.keys";
            keys.style.opacity = 0.75f;
            HudStrip(keys);
            if (Application.isMobilePlatform || playerPresentation)
                keys.style.display = DisplayStyle.None;

            var steering = new VisualElement { name = controlsName + ".steering", pickingMode = PickingMode.Ignore };
            steering.AddToClassList("race-steering");
            steering.languageDirection = LanguageDirection.LTR;
            deck.Add(steering);
            AddTouchHold(steering, "", "left", "cyan", held => app.Input.TouchLeft(held), prefix + "steerLeft");
            AddTouchHold(steering, "", "right", "cyan", held => app.Input.TouchRight(held), prefix + "steerRight");

            var actions = new VisualElement { name = controlsName + ".actions", pickingMode = PickingMode.Ignore };
            actions.AddToClassList("race-actions");
            deck.Add(actions);
            AddTouchHold(actions, T("input.boost", state), "boost", "gold", held => app.Input.TouchBoost(held), prefix + "boost");
            AddTouchHold(actions, T("input.drift", state), "drift", "purple", held => app.Input.TouchDrift(held), prefix + "drift");
            AddTouchHold(actions, T("input.brake", state), "brake", "red", held => app.Input.TouchBrake(held), prefix + "brake");
            AddTouchAction(actions, T("input.gadget", state), "gadget", "mint", () => app.Input.TouchGadget(), prefix + "gadget", "input.set");

            var utilities = new VisualElement { name = network ? "lan.utilities" : "hud.utilities", pickingMode = PickingMode.Ignore };
            utilities.AddToClassList("race-utilities");
            utilities.languageDirection = LanguageDirection.LTR;
            deck.Add(utilities);
            AddTouchAction(utilities, T("input.recovery", state), "recover", "utility", () => app.Input.TouchRecover(), prefix + "recover", "input.set");
            AddTouchAction(utilities, T("settings.camera", state), "camera", "utility", () => app.Dispatch("camera.toggle", emptyParams), prefix + "camera", "camera.toggle");
            AddTouchAction(utilities, T("input.pause", state), "pause", "utility", () => app.Dispatch("pause.toggle", emptyParams), prefix + "pause", "pause.toggle");
            AddTouchAction(utilities, T(network ? "lan.leave" : "driving.exit", state), "exit", "utility",
                () => app.Dispatch(network ? "lan.leave" : "race.exit", emptyParams), prefix + (network ? "leave" : "exit"), network ? "lan.leave" : "race.exit");

            // The panel scales to the tablet, while safe-area insets account
            // for Android gesture bars and cutouts in either orientation.
            deck.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                var safe = UnityEngine.Screen.safeArea;
                var sx = deck.resolvedStyle.width / Mathf.Max(1, UnityEngine.Screen.width);
                var sy = deck.resolvedStyle.height / Mathf.Max(1, UnityEngine.Screen.height);
                var left = Mathf.Max(20, safe.xMin * sx + 12);
                var right = Mathf.Max(20, (UnityEngine.Screen.width - safe.xMax) * sx + 12);
                var bottom = Mathf.Max(22, safe.yMin * sy + 12);
                steering.style.left = left;
                steering.style.bottom = bottom;
                actions.style.right = right;
                actions.style.bottom = bottom;
                utilities.style.right = right;
                utilities.style.top = Mathf.Max(16, (UnityEngine.Screen.height - safe.yMax) * sy + 8);
                var compact = deck.resolvedStyle.width < 1000;
                deck.EnableInClassList("race-deck--compact", compact);
            });
        }

        private void AddTouchHold(VisualElement parent, string caption, string glyph, string color, Action<bool> onHold, string name)
        {
            AddHoldButton(parent, caption, Color.clear, onHold, name);
            var button = parent.Q<Button>(name);
            DecorateTouchControl(button, caption, glyph, color, true);
            ApplyAvailability(button, "input.set");
        }

        private void AddTouchAction(VisualElement parent, string caption, string glyph, string color, Action action, string name, string availability)
        {
            var button = new Button(action) { text = caption, name = name };
            parent.Add(button);
            DecorateTouchControl(button, caption, glyph, color);
            ApplyAvailability(button, availability);
        }

        private static void DecorateTouchControl(Button button, string caption, string glyph, string color, bool held = false)
        {
            // Clear the generic menu-button inline styles so the deck's USS
            // owns geometry, color and animated press states.
            button.style.minHeight = StyleKeyword.Null;
            button.style.minWidth = StyleKeyword.Null;
            button.style.height = StyleKeyword.Null;
            button.style.fontSize = StyleKeyword.Null;
            button.style.backgroundColor = StyleKeyword.Null;
            button.style.marginLeft = button.style.marginRight = StyleKeyword.Null;
            button.style.marginTop = button.style.marginBottom = StyleKeyword.Null;
            button.style.paddingLeft = button.style.paddingRight = StyleKeyword.Null;
            button.style.paddingTop = button.style.paddingBottom = StyleKeyword.Null;
            button.style.borderTopWidth = button.style.borderBottomWidth = StyleKeyword.Null;
            button.style.borderLeftWidth = button.style.borderRightWidth = StyleKeyword.Null;
            button.style.borderTopColor = button.style.borderBottomColor = StyleKeyword.Null;
            button.style.borderLeftColor = button.style.borderRightColor = StyleKeyword.Null;
            button.style.borderTopLeftRadius = button.style.borderTopRightRadius = StyleKeyword.Null;
            button.style.borderBottomLeftRadius = button.style.borderBottomRightRadius = StyleKeyword.Null;
            button.AddToClassList("race-control");
            button.AddToClassList("race-control--" + color);
            button.AddToClassList("race-control--" + glyph);
            button.tooltip = caption;
            button.Add(new RaceControlIcon(glyph) { name = "control.icon", pickingMode = PickingMode.Ignore });

            var pointers = new HashSet<int>();
            Action<int> release = id =>
            {
                if (!pointers.Remove(id)) return;
                if (button.HasPointerCapture(id)) button.ReleasePointer(id);
                button.EnableInClassList("race-control--pressed", pointers.Count > 0);
            };
            button.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (!button.enabledInHierarchy) return;
                // Clickable owns one active pointer for one-shot actions.
                // A second capture could cancel that click when it ends.
                if (!held && pointers.Count > 0) return;
                pointers.Add(evt.pointerId);
                button.CapturePointer(evt.pointerId);
                button.AddToClassList("race-control--pressed");
            }, TrickleDown.TrickleDown);
            const CallbackOptions releaseOptions = CallbackOptions.TrickleDown | CallbackOptions.IncludeDisabled;
            button.RegisterCallback<PointerUpEvent>(evt => release(evt.pointerId), releaseOptions);
            button.RegisterCallback<PointerCancelEvent>(evt => release(evt.pointerId), releaseOptions);
            button.RegisterCallback<PointerCaptureOutEvent>(evt => release(evt.pointerId), releaseOptions);
            button.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                pointers.Clear();
                button.RemoveFromClassList("race-control--pressed");
            });
        }
    }

    // Resolution-independent, font-independent icons: no emoji fallback or
    // texture uploads, and geometry is repainted only when UI Toolkit asks.
    internal sealed class RaceControlIcon : VisualElement
    {
        private readonly string glyph;
        public RaceControlIcon(string glyph)
        {
            this.glyph = glyph;
            AddToClassList("race-control-icon");
            generateVisualContent += Draw;
        }

        private void Draw(MeshGenerationContext context)
        {
            var p = context.painter2D;
            var s = Mathf.Min(contentRect.width, contentRect.height) / 48f;
            var offset = new Vector2((contentRect.width - 48 * s) / 2, (contentRect.height - 48 * s) / 2);
            Vector2 V(float x, float y) => offset + new Vector2(x, y) * s;
            p.strokeColor = new Color(1, 1, 1, 0.96f);
            p.fillColor = p.strokeColor;
            p.lineWidth = 3.6f * s;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;
            void Path(bool fill, params Vector2[] points)
            {
                p.BeginPath(); p.MoveTo(points[0]);
                for (var i = 1; i < points.Length; i++) p.LineTo(points[i]);
                if (fill) { p.ClosePath(); p.Fill(); } else p.Stroke();
            }
            switch (glyph)
            {
                case "left": Path(false, V(29, 10), V(15, 24), V(29, 38)); break;
                case "right": Path(false, V(19, 10), V(33, 24), V(19, 38)); break;
                case "boost":
                    Path(true, V(27, 3), V(10, 27), V(22, 27), V(19, 45), V(39, 19), V(26, 19));
                    break;
                case "drift":
                    Path(false, V(10, 38), V(18, 30), V(12, 23));
                    Path(false, V(21, 42), V(29, 34), V(23, 27));
                    Path(true, V(19, 15), V(30, 7), V(41, 21), V(30, 29));
                    Path(false, V(12, 12), V(17, 8));
                    break;
                case "brake":
                    Path(false, V(11, 12), V(6, 20), V(6, 29), V(11, 37));
                    Path(false, V(37, 12), V(42, 20), V(42, 29), V(37, 37));
                    Path(true, V(17, 13), V(22, 13), V(22, 35), V(17, 35));
                    Path(true, V(27, 13), V(32, 13), V(32, 35), V(27, 35));
                    break;
                case "gadget":
                    Path(true, V(24, 5), V(29, 17), V(42, 20), V(32, 29), V(34, 42), V(24, 35), V(13, 42), V(15, 29), V(5, 20), V(19, 17));
                    break;
                case "camera":
                    Path(false, V(6, 16), V(15, 16), V(19, 10), V(29, 10), V(33, 16), V(42, 16), V(42, 37), V(6, 37), V(6, 16));
                    p.BeginPath(); p.Arc(V(24, 26), 7 * s, 0, 360); p.Stroke();
                    break;
                case "recover":
                    Path(false, V(37, 17), V(30, 9), V(18, 9), V(9, 18), V(9, 31), V(18, 39), V(31, 39), V(38, 32));
                    Path(false, V(26, 18), V(38, 18), V(38, 6));
                    break;
                case "pause":
                    Path(true, V(12, 8), V(20, 8), V(20, 40), V(12, 40));
                    Path(true, V(28, 8), V(36, 8), V(36, 40), V(28, 40));
                    break;
                case "exit":
                    Path(false, V(21, 7), V(9, 7), V(9, 41), V(21, 41));
                    Path(false, V(22, 24), V(41, 24));
                    Path(false, V(33, 16), V(41, 24), V(33, 32));
                    break;
            }
        }
    }
}
