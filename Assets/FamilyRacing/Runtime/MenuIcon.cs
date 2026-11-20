using UnityEngine;
using UnityEngine.UIElements;

namespace FamilyRacing
{
    // Vector menu icons drawn on a 48-unit grid with Painter2D, in the same
    // style as RaceControlIcon: no textures to upload, crisp at any size,
    // and repainted only when UI Toolkit dirties the element. Icons are the
    // primary label on the kid-facing menus, so each shape is bold and
    // instantly readable at thumbnail size.
    public sealed class MenuIcon : VisualElement
    {
        private string glyph;
        private Color tint;
        private bool mirror;

        public MenuIcon(string glyph, float size, Color tint, bool mirror = false)
        {
            this.glyph = glyph;
            this.tint = tint;
            this.mirror = mirror;
            name = "icon." + glyph;
            pickingMode = PickingMode.Ignore;
            style.width = size;
            style.height = size;
            style.flexShrink = 0;
            generateVisualContent += Draw;
        }

        public string Glyph => glyph;

        public void Set(string newGlyph, Color newTint)
        {
            if (newGlyph == glyph && newTint == tint) return;
            glyph = newGlyph;
            tint = newTint;
            MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (rect.width <= 0 || rect.height <= 0) return;
            var p = context.painter2D;
            var s = Mathf.Min(rect.width, rect.height) / 48f;
            var offset = new Vector2((rect.width - 48 * s) / 2, (rect.height - 48 * s) / 2);
            Vector2 V(float x, float y) => offset + new Vector2((mirror ? 48 - x : x) * s, y * s);
            p.strokeColor = tint;
            p.fillColor = tint;
            p.lineWidth = 4.2f * s;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;

            void Line(params Vector2[] points)
            {
                p.BeginPath(); p.MoveTo(points[0]);
                for (var i = 1; i < points.Length; i++) p.LineTo(points[i]);
                p.Stroke();
            }
            void Poly(params Vector2[] points)
            {
                p.BeginPath(); p.MoveTo(points[0]);
                for (var i = 1; i < points.Length; i++) p.LineTo(points[i]);
                p.ClosePath(); p.Fill();
            }
            void Disc(float x, float y, float r)
            {
                p.BeginPath(); p.Arc(V(x, y), r * s, 0f, 360f); p.ClosePath(); p.Fill();
            }
            void Ring(float x, float y, float r)
            {
                p.BeginPath(); p.Arc(V(x, y), r * s, 0f, 360f); p.ClosePath(); p.Stroke();
            }
            void Annulus(float x, float y, float outer, float inner)
            {
                p.BeginPath();
                p.Arc(V(x, y), outer * s, 0f, 360f); p.ClosePath();
                p.MoveTo(V(x + inner, y));
                p.Arc(V(x, y), inner * s, 0f, 360f); p.ClosePath();
                p.Fill(FillRule.OddEven);
            }
            void Star(float cx, float cy, float outer, float inner)
            {
                var pts = new Vector2[10];
                for (var i = 0; i < 10; i++)
                {
                    var a = Mathf.Deg2Rad * (-90f + i * 36f);
                    var r = i % 2 == 0 ? outer : inner;
                    pts[i] = V(cx + Mathf.Cos(a) * r * (mirror ? -1 : 1), cy + Mathf.Sin(a) * r);
                }
                Poly(pts);
            }

            switch (glyph)
            {
                case "play":
                    Poly(V(15, 8), V(40, 24), V(15, 40));
                    break;
                case "flag":
                    Line(V(11, 6), V(11, 44));
                    Poly(V(13, 7), V(39, 7), V(33, 16), V(39, 25), V(13, 25));
                    break;
                case "map":
                    Poly(V(5, 12), V(17, 7), V(17, 37), V(5, 42));
                    Poly(V(20, 7), V(29, 12), V(29, 42), V(20, 37));
                    Poly(V(32, 12), V(43, 7), V(43, 37), V(32, 42));
                    break;
                case "trophy":
                    Poly(V(13, 6), V(35, 6), V(34, 18), V(29, 26), V(19, 26), V(14, 18));
                    Line(V(13, 10), V(6, 10), V(7, 17), V(14, 21));
                    Line(V(35, 10), V(42, 10), V(41, 17), V(34, 21));
                    Poly(V(21, 25), V(27, 25), V(27, 34), V(21, 34));
                    Poly(V(14, 35), V(34, 35), V(36, 42), V(12, 42));
                    break;
                case "stopwatch":
                    Ring(24, 27, 15);
                    Poly(V(19, 4), V(29, 4), V(29, 9), V(19, 9));
                    Line(V(24, 27), V(24, 18));
                    Line(V(24, 27), V(31, 31));
                    Line(V(37, 11), V(40, 14));
                    break;
                case "car":
                    Poly(V(4, 32), V(4, 24), V(11, 22), V(17, 13), V(31, 13), V(38, 22), V(44, 24), V(44, 32));
                    Disc(13, 34, 6);
                    Disc(35, 34, 6);
                    break;
                case "motorcycle":
                    Ring(11, 33, 7);
                    Ring(37, 33, 7);
                    Line(V(11, 33), V(20, 22), V(31, 22), V(37, 33));
                    Line(V(27, 13), V(33, 13), V(37, 33));
                    Poly(V(17, 20), V(29, 20), V(26, 26), V(19, 26));
                    break;
                case "star":
                    Star(24, 25, 21, 9);
                    break;
                case "wheel":
                    Ring(24, 24, 18);
                    Disc(24, 24, 5);
                    Line(V(7, 22), V(41, 22));
                    Line(V(24, 24), V(24, 41));
                    break;
                case "friends":
                    Disc(16, 15, 7);
                    Disc(33, 17, 6);
                    p.BeginPath(); p.MoveTo(V(3, 42)); p.BezierCurveTo(V(3, 27), V(29, 27), V(29, 42)); p.ClosePath(); p.Fill();
                    p.BeginPath(); p.MoveTo(V(26, 32)); p.BezierCurveTo(V(28, 26), V(45, 25), V(45, 42)); p.LineTo(V(31, 42)); p.ClosePath(); p.Fill();
                    break;
                case "gear":
                {
                    p.BeginPath();
                    const int teeth = 8;
                    for (var i = 0; i < teeth * 4; i++)
                    {
                        var a = Mathf.Deg2Rad * (i * 360f / (teeth * 4) - 6f);
                        var r = (i % 4 == 0 || i % 4 == 1) ? 20f : 14.5f;
                        var pt = V(24 + Mathf.Cos(a) * r, 24 + Mathf.Sin(a) * r);
                        if (i == 0) p.MoveTo(pt); else p.LineTo(pt);
                    }
                    p.ClosePath();
                    p.MoveTo(V(30, 24));
                    p.Arc(V(24, 24), 6 * s, 0f, 360f);
                    p.ClosePath();
                    p.Fill(FillRule.OddEven);
                    break;
                }
                case "back":
                    Line(V(39, 24), V(11, 24));
                    Line(V(22, 12), V(10, 24), V(22, 36));
                    break;
                case "exit":
                    Line(V(21, 7), V(9, 7), V(9, 41), V(21, 41));
                    Line(V(20, 24), V(41, 24));
                    Line(V(33, 16), V(41, 24), V(33, 32));
                    break;
                case "swap":
                    Line(V(8, 16), V(38, 16));
                    Line(V(31, 9), V(38, 16), V(31, 23));
                    Line(V(40, 32), V(10, 32));
                    Line(V(17, 25), V(10, 32), V(17, 39));
                    break;
                case "coin":
                    Annulus(24, 24, 19, 14);
                    Poly(V(21, 14), V(27, 14), V(27, 34), V(21, 34));
                    break;
                case "retry":
                    Line(V(37, 17), V(30, 9), V(18, 9), V(9, 18), V(9, 31), V(18, 39), V(31, 39), V(38, 32));
                    Line(V(26, 18), V(38, 18), V(38, 6));
                    break;
                case "home":
                    Poly(V(24, 5), V(44, 23), V(38, 23), V(38, 42), V(28, 42), V(28, 31), V(20, 31), V(20, 42), V(10, 42), V(10, 23), V(4, 23));
                    break;
                case "lock":
                    Line(V(15, 21), V(15, 15), V(18, 9), V(24, 7), V(30, 9), V(33, 15), V(33, 21));
                    Poly(V(9, 21), V(39, 21), V(39, 43), V(9, 43));
                    break;
                case "check":
                    p.lineWidth = 6f * s;
                    Line(V(9, 25), V(19, 35), V(39, 13));
                    break;
                case "plus":
                    p.lineWidth = 6.5f * s;
                    Line(V(24, 9), V(24, 39));
                    Line(V(9, 24), V(39, 24));
                    break;
                case "globe":
                    Ring(24, 24, 18);
                    Line(V(7, 24), V(41, 24));
                    p.BeginPath(); p.MoveTo(V(24, 6)); p.BezierCurveTo(V(13, 15), V(13, 33), V(24, 42)); p.Stroke();
                    p.BeginPath(); p.MoveTo(V(24, 6)); p.BezierCurveTo(V(35, 15), V(35, 33), V(24, 42)); p.Stroke();
                    break;
                case "camera":
                    Line(V(6, 16), V(15, 16), V(19, 10), V(29, 10), V(33, 16), V(42, 16), V(42, 37), V(6, 37), V(6, 16));
                    Ring(24, 26, 7);
                    break;
                case "chase":
                    Poly(V(10, 40), V(10, 33), V(15, 31), V(19, 24), V(29, 24), V(33, 31), V(38, 33), V(38, 40));
                    Line(V(24, 4), V(24, 16));
                    Line(V(18, 11), V(24, 17), V(30, 11));
                    break;
                case "overhead":
                    Poly(V(17, 12), V(31, 12), V(33, 40), V(15, 40));
                    Line(V(6, 6), V(13, 13));
                    Line(V(42, 6), V(35, 13));
                    break;
                case "steady":
                    Ring(24, 24, 9);
                    Line(V(4, 24), V(11, 24));
                    Line(V(37, 24), V(44, 24));
                    Line(V(24, 4), V(24, 11));
                    Line(V(24, 37), V(24, 44));
                    break;
                case "music":
                    Line(V(19, 35), V(19, 9), V(39, 5), V(39, 31));
                    Disc(14, 36, 6);
                    Disc(34, 32, 6);
                    break;
                case "speaker":
                    Poly(V(6, 18), V(14, 18), V(25, 8), V(25, 40), V(14, 30), V(6, 30));
                    p.BeginPath(); p.MoveTo(V(31, 16)); p.BezierCurveTo(V(36, 20), V(36, 28), V(31, 32)); p.Stroke();
                    p.BeginPath(); p.MoveTo(V(36, 10)); p.BezierCurveTo(V(45, 18), V(45, 30), V(36, 38)); p.Stroke();
                    break;
                case "voice":
                    p.BeginPath(); p.MoveTo(V(8, 8)); p.LineTo(V(40, 8)); p.LineTo(V(40, 32)); p.LineTo(V(22, 32));
                    p.LineTo(V(12, 41)); p.LineTo(V(13, 32)); p.LineTo(V(8, 32)); p.ClosePath(); p.Fill();
                    break;
                case "save":
                    p.lineWidth = 6f * s;
                    Line(V(9, 25), V(19, 35), V(39, 13));
                    break;
                case "bolt":
                    Poly(V(27, 3), V(10, 27), V(22, 27), V(19, 45), V(39, 19), V(26, 19));
                    break;
                case "speed":
                    Poly(V(6, 12), V(22, 24), V(6, 36));
                    Poly(V(22, 12), V(40, 24), V(22, 36));
                    break;
                case "out":
                    p.lineWidth = 6f * s;
                    Line(V(12, 12), V(36, 36));
                    Line(V(36, 12), V(12, 36));
                    break;
                case "search":
                    Ring(20, 20, 12);
                    p.lineWidth = 6f * s;
                    Line(V(29, 29), V(41, 41));
                    break;
                case "stop":
                    Poly(V(12, 12), V(36, 12), V(36, 36), V(12, 36));
                    break;
                case "wifi":
                    Disc(24, 38, 4);
                    p.BeginPath(); p.MoveTo(V(14, 28)); p.BezierCurveTo(V(19, 22), V(29, 22), V(34, 28)); p.Stroke();
                    p.BeginPath(); p.MoveTo(V(7, 20)); p.BezierCurveTo(V(16, 10), V(32, 10), V(41, 20)); p.Stroke();
                    break;
                case "crown":
                    Poly(V(6, 36), V(6, 14), V(16, 24), V(24, 10), V(32, 24), V(42, 14), V(42, 36));
                    break;
                case "shirt":
                    Poly(V(16, 6), V(24, 11), V(32, 6), V(44, 14), V(39, 22), V(34, 19), V(34, 42), V(14, 42), V(14, 19), V(9, 22), V(4, 14));
                    break;
                case "pause":
                    Poly(V(12, 8), V(20, 8), V(20, 40), V(12, 40));
                    Poly(V(28, 8), V(36, 8), V(36, 40), V(28, 40));
                    break;
                case "next":
                    Poly(V(8, 10), V(26, 24), V(8, 38));
                    Poly(V(24, 10), V(42, 24), V(24, 38));
                    break;
            }
        }
    }
}
