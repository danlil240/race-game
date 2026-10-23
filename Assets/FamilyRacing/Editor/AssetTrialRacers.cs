using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace FamilyRacing.EditorTools
{
    // Original, render-only character and kart detailing for the opt-in art study.
    public static class AssetTrialRacers
    {
        private const string Dir = AssetTrialAuthoring.Output + "/Racers";
        private static readonly Dictionary<string, Material> Mats = new Dictionary<string, Material>();
        public static string CharacterPath(string id) => Dir + "/character-" + id + ".asset";
        public static string PortraitPath(string id) => Dir + "/portrait-" + id + ".asset";

        public static void BuildCharacters()
        {
            Directory.CreateDirectory(Dir);
            AssetDatabase.Refresh();
            Mats.Clear();
            foreach (var id in RacerVisualAuthoring.CharacterIds)
            {
                var go = Character(id);
                try
                {
                    MergeRigidParts(go);
                    var prefab = PrefabUtility.SaveAsPrefabAsset(go, Dir + "/racer-" + id + ".prefab");
                    var def = ScriptableObject.CreateInstance<CharacterDefinitionAsset>();
                    def.characterId = id;
                    def.prefab = prefab;
                    Save(def, CharacterPath(id));
                }
                finally { Object.DestroyImmediate(go); }
            }
            BuildPortraits();
        }

        public static void BuildPortraits()
        {
            var previousPipeline = QualitySettings.renderPipeline;
            var previousAsync = ShaderUtil.allowAsyncCompilation;
            var previousActive = RenderTexture.active;
            ShaderUtil.allowAsyncCompilation = false;
            var sceneLights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            var enabledStates = sceneLights.Select(l => l.enabled).ToArray();
            var rt = new RenderTexture(256, 256, 24);
            var stage = new GameObject("PortraitStage");
            stage.transform.position = new Vector3(0f, 5000f, 0f);
            try
            {
                for (var i = 0; i < sceneLights.Length; i++) sceneLights[i].enabled = false;
                QualitySettings.renderPipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(VisualBenchmarkSetup.PcPipelinePath);
                var keyLight = new GameObject("KeyLight").AddComponent<Light>();
                keyLight.transform.SetParent(stage.transform, false);
                keyLight.type = LightType.Directional;
                keyLight.intensity = 1.25f;
                keyLight.color = new Color(1f, 0.96f, 0.90f);
                keyLight.transform.localRotation = Quaternion.Euler(28f, -152f, 0f);
                var fillLight = new GameObject("FillLight").AddComponent<Light>();
                fillLight.transform.SetParent(stage.transform, false);
                fillLight.type = LightType.Directional;
                fillLight.intensity = 0.55f;
                fillLight.color = new Color(0.62f, 0.82f, 0.95f);
                fillLight.transform.localRotation = Quaternion.Euler(-12f, 145f, 0f);
                var camera = new GameObject("PortraitCamera").AddComponent<Camera>();
                camera.transform.SetParent(stage.transform, false);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 12f;
                camera.fieldOfView = 34f;
                camera.aspect = 1f;
                camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
                for (var i = 0; i < RacerVisualAuthoring.CharacterIds.Length; i++)
                {
                    var id = RacerVisualAuthoring.CharacterIds[i];
                    var def = AssetDatabase.LoadAssetAtPath<CharacterDefinitionAsset>(CharacterPath(id));
                    var actor = Object.Instantiate(def.prefab, stage.transform, false);
                    actor.transform.localPosition = Vector3.zero;
                    actor.transform.localRotation = Quaternion.Euler(0f, 16f, 0f);
                    var block = new MaterialPropertyBlock();
                    block.SetColor("_BaseColor", RacerVisualRig.SlotColor(i));
                    foreach (var r in actor.GetComponentsInChildren<Renderer>())
                        if (r.name.Contains("Accent")) r.SetPropertyBlock(block);
                    camera.backgroundColor = Color.Lerp(new Color(0.09f, 0.16f, 0.24f), RacerVisualRig.SlotColor(i), 0.22f);
                    var focusY = id == "rabbit" ? 0.96f : 0.88f;
                    var dist = id == "rabbit" ? 2.45f : 2.15f;
                    camera.transform.localPosition = new Vector3(0.20f, focusY + 0.08f, dist);
                    camera.transform.LookAt(stage.transform.TransformPoint(new Vector3(0f, focusY, 0.08f)));
                    RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = rt });
                    RenderTexture.active = rt;
                    var tex = new Texture2D(256, 256, TextureFormat.RGB24, false)
                    {
                        name = "portrait-" + id,
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Bilinear
                    };
                    tex.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                    tex.Apply(false, false);
                    Save(tex, PortraitPath(id));
                    Object.DestroyImmediate(actor);
                }
            }
            finally
            {
                for (var i = 0; i < sceneLights.Length; i++)
                    if (sceneLights[i] != null) sceneLights[i].enabled = enabledStates[i];
                RenderTexture.active = previousActive;
                QualitySettings.renderPipeline = previousPipeline;
                ShaderUtil.allowAsyncCompilation = previousAsync;
                Object.DestroyImmediate(stage);
                Object.DestroyImmediate(rt);
            }
        }

        private static Material Mat(string name, string hex, float smooth = .15f)
        {
            if (Mats.TryGetValue(name, out var found) && found != null) return found;
            Directory.CreateDirectory(Dir);
            ColorUtility.TryParseHtmlString("#" + hex, out var color);
            var mat = new Material(Shader.Find("Universal Render Pipeline/Simple Lit")) { name = name };
            mat.SetColor("_BaseColor", color);
            mat.SetColor("_Color", color);
            mat.SetFloat("_Smoothness", smooth);
            mat.enableInstancing = true;
            return Mats[name] = Save(mat, Dir + "/" + name + ".mat");
        }

        private static T Save<T>(T value, string path) where T : Object
        {
            var old = AssetDatabase.LoadAssetAtPath<T>(path);
            if (old == null) { AssetDatabase.CreateAsset(value, path); return value; }
            EditorUtility.CopySerialized(value, old);
            Object.DestroyImmediate(value);
            EditorUtility.SetDirty(old);
            return old;
        }

        private static Transform Node(Transform parent, string name, Vector3 p)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = p;
            return t;
        }

        private static Transform Part(Transform parent, string name, Material m, Vector3 p, Vector3 scale,
            PrimitiveType type = PrimitiveType.Sphere, Vector3 rotation = default)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = p;
            go.transform.localScale = scale;
            go.transform.localRotation = Quaternion.Euler(rotation);
            go.GetComponent<Renderer>().sharedMaterial = m;
            return go.transform;
        }

        private static void Bar(Transform parent, string name, Material m, Vector3 a, Vector3 b, float width)
        {
            var t = Part(parent, name, m, (a + b) * .5f, new Vector3(width, Vector3.Distance(a, b) * .5f, width), PrimitiveType.Cylinder);
            t.localRotation = Quaternion.FromToRotation(Vector3.up, b - a);
        }

        private static GameObject Character(string id)
        {
            var cream = Mat("Ivory", "FFF0D3");
            var dark = Mat("Ink", "202F38");
            var suit = Mat("RacingSuit", "264C60");
            var accent = Mat("ProfileAccent", "FFFFFF", .3f);
            var white = Mat("EyeWhite", "FFFFFF", .3f);
            var eye = Mat("Eyes", "101C26", .65f);
            var pink = Mat("EarPink", "E7A1A3");
            var fur = id == "fox" ? Mat("FoxFur", "D96B2F") : id == "rabbit" ? Mat("RabbitFur", "C3BDD9")
                : id == "panda" ? cream : Mat("RaccoonFur", "8F9DA5");
            var root = new GameObject("TrialRacer-" + id);
            var rig = Node(root.transform, "Rig", Vector3.zero);
            var hips = Node(rig, "Hips", Vector3.zero);
            var spine = Node(hips, "Spine", new Vector3(0, .25f, 0));
            var head = Node(spine, "Head", new Vector3(0, .55f, .015f));
            Part(hips, "SuitHips", suit, new Vector3(0, .07f, 0), new Vector3(.48f, .29f, .40f));
            Part(spine, "SuitTorso", suit, new Vector3(0, .18f, 0), new Vector3(.49f, .53f, .38f));
            Part(spine, "ChestAccent", accent, new Vector3(0, .22f, .174f), new Vector3(.32f, .25f, .035f));
            Bar(spine, "Zip", cream, new Vector3(0, .07f, .197f), new Vector3(0, .36f, .17f), .018f);
            for (int side = -1; side <= 1; side += 2)
            {
                var leg = Node(hips, side < 0 ? "LegL" : "LegR", new Vector3(side * .145f, .03f, .08f));
                Part(leg, "Trouser", suit, new Vector3(0, -.14f, 0), new Vector3(.17f, .32f, .19f));
                Part(leg, "Boot", dark, new Vector3(0, -.31f, .04f), new Vector3(.19f, .18f, .29f));
                Part(leg, "BootAccent", accent, new Vector3(0, -.34f, .12f), new Vector3(.15f, .07f, .14f));
                var arm = Node(spine, side < 0 ? "ArmL" : "ArmR", new Vector3(side * .27f, .36f, .015f));
                Part(arm, "Sleeve", suit, new Vector3(0, -.20f, 0), new Vector3(.16f, .44f, .16f));
                Part(arm, "CuffAccent", accent, new Vector3(0, -.40f, 0), new Vector3(.17f, .08f, .17f));
                Part(arm, "Glove", id == "panda" ? dark : cream, new Vector3(0, -.49f, 0), new Vector3(.18f, .17f, .18f));
                Part(arm, "Thumb", id == "panda" ? dark : cream, new Vector3(side * -.07f, -.48f, .025f), new Vector3(.08f, .10f, .10f));
            }
            Part(head, "HeadFur", fur, new Vector3(0, .17f, 0), new Vector3(.73f, .66f, .62f));
            var eyes = new List<Transform>();
            var ears = new List<Transform>();
            for (int side = -1; side <= 1; side += 2)
            {
                if (id == "fox")
                {
                    Part(head, "Cheek", cream, new Vector3(side * .19f, .06f, .225f), new Vector3(.33f, .26f, .22f), rotation: new Vector3(0, 0, side * 20));
                    Part(head, "CheekTuft", fur, new Vector3(side * .31f, .12f, .04f), new Vector3(.25f, .20f, .30f), rotation: new Vector3(0, 0, side * 25));
                }
                if (id == "panda" || id == "raccoon")
                    Part(head, "EyeMask", dark, new Vector3(side * .172f, .225f, .26f), new Vector3(.27f, .27f, .11f), rotation: new Vector3(0, side * 10, side * -18));
                var e = Node(head, "BlinkEye" + side, new Vector3(side * .155f, .245f, .304f));
                Part(e, "Sclera", cream, Vector3.zero, new Vector3(.171f, .19f, .08f));
                Part(e, "Pupil", eye, new Vector3(-side * .013f, -.007f, .038f), new Vector3(.104f, .133f, .052f));
                Part(e, "Catchlight", white, new Vector3(-.024f, .027f, .063f), new Vector3(.036f, .042f, .018f));
                eyes.Add(e);
                Part(head, "Brow", id == "panda" ? dark : fur, new Vector3(side * .16f, .366f, .278f), new Vector3(.20f, .055f, .09f), rotation: new Vector3(0, 0, side * 8));
                var ear = Node(head, "EarMotion" + side, new Vector3(side * .242f, .405f, -.015f));
                ear.localRotation = Quaternion.Euler(-5, 0, side * -13);
                ears.Add(ear);
                if (id == "fox" || id == "raccoon")
                {
                    Ear(ear, "EarRim", id == "fox" ? dark : fur, .245f, .32f, .15f);
                    Ear(ear, "EarFur", fur, .196f, .265f, .16f, .025f);
                    Ear(ear, "EarInner", pink, .116f, .20f, .01f, .108f);
                }
                else if (id == "rabbit")
                {
                    Part(ear, "LongEar", fur, new Vector3(0, .23f, 0), new Vector3(.20f, .61f, .16f));
                    Part(ear, "EarInner", pink, new Vector3(0, .25f, .07f), new Vector3(.105f, .44f, .035f));
                }
                else
                {
                    Part(ear, "RoundEar", dark, new Vector3(0, .06f, 0), new Vector3(.27f, .27f, .16f));
                    Part(ear, "EarInset", Mat("PandaEarInset", "5A6166"), new Vector3(0, .06f, .075f), new Vector3(.14f, .14f, .035f));
                }
            }
            if (id == "raccoon")
                Part(head, "MaskBridge", dark, new Vector3(0, .24f, .29f), new Vector3(.19f, .11f, .06f));
            Part(head, "Muzzle", cream, new Vector3(0, .075f, .305f), new Vector3(id == "fox" ? .33f : .38f, .245f, id == "fox" ? .35f : .25f));
            Part(head, "Nose", id == "rabbit" ? pink : dark, new Vector3(0, .12f, id == "fox" ? .477f : .428f), new Vector3(.12f, .085f, .075f));
            float mouthZ = id == "fox" ? .456f : .423f;
            Bar(head, "Smile", dark, new Vector3(-.072f, .027f, mouthZ), new Vector3(0, .007f, mouthZ + .015f), .014f);
            Bar(head, "Smile", dark, new Vector3(0, .007f, mouthZ + .015f), new Vector3(.072f, .027f, mouthZ), .014f);
            if (id == "rabbit") Part(head, "Teeth", white, new Vector3(0, -.012f, .43f), new Vector3(.075f, .07f, .025f), PrimitiveType.Cube);
            // Aviator goggles sit on the brow, leaving the expressive eyes clear.
            Bar(head, "GoggleBridge", dark, new Vector3(-.09f, .46f, .18f), new Vector3(.09f, .46f, .18f), .04f);
            foreach (int side in new[] { -1, 1 })
            {
                Part(head, "GoggleFrame", dark, new Vector3(side * .12f, .45f, .17f), new Vector3(.25f, .16f, .13f));
                Part(head, "GoggleLens", Mat("GoggleGlass", "76CBD8", .65f), new Vector3(side * .12f, .46f, .227f), new Vector3(.18f, .095f, .045f));
                Bar(head, "GoggleStrap", dark, new Vector3(side * .23f, .44f, .13f), new Vector3(side * .30f, .36f, -.10f), .045f);
            }
            Part(spine, "CollarAccent", accent, new Vector3(0, .43f, 0), new Vector3(.37f, .12f, .35f));
            var scarf = Node(spine, "ScarfMotion", new Vector3(.13f, .43f, -.14f));
            Part(scarf, "ScarfAccent", accent, new Vector3(.07f, -.03f, -.18f), new Vector3(.15f, .06f, .45f), rotation: new Vector3(-12, -18, 0));
            var tail = Node(hips, "Tail", new Vector3(0, .13f, -.19f));
            if (id == "fox" || id == "raccoon")
            {
                var mesh = TailMesh(id);
                var go = Node(tail, "SculptedTail", Vector3.zero).gameObject;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = new[] { fur, id == "fox" ? cream : dark };
            }
            else Part(tail, "PuffTail", id == "rabbit" ? cream : dark, new Vector3(0, .06f, -.08f), Vector3.one * .24f);
            var life = root.AddComponent<AssetTrialRacerLife>();
            life.eyes = eyes.ToArray(); life.ears = ears.ToArray(); life.tail = tail; life.scarf = scarf;
            life.phase = Array.IndexOf(RacerVisualAuthoring.CharacterIds, id) * .63f;
            return root;
        }

        private static Mesh TailMesh(string id)
        {
            var v = new List<Vector3>(); var triangles = new[] { new List<int>(), new List<int>() };
            const int rings = 24, sides = 12;
            for (int i = 0; i <= rings; i++)
            {
                float t = i / (float)rings;
                var center = new Vector3(.17f * t, .14f * t + .1f * t * t, -.75f * t);
                var frame = Quaternion.FromToRotation(Vector3.forward, new Vector3(.17f, .14f + .2f * t, -.75f));
                // sin(pi) can be slightly negative at float precision; a
                // fractional power would serialize NaN bounds at the tip.
                float radius = .04f * (1 - t) + .135f * Mathf.Pow(Mathf.Max(0f, Mathf.Sin(t * Mathf.PI)), .8f) + .005f;
                for (int j = 0; j <= sides; j++)
                {
                    float a = j * Mathf.PI * 2 / sides;
                    v.Add(center + frame * new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0));
                    if (i == rings || j == sides) continue;
                    int n = i * (sides + 1) + j;
                    int color = id == "fox" ? (t > .65f ? 1 : 0) : ((i / 3) % 2);
                    triangles[color].AddRange(new[] { n, n+1, n+sides+1, n+1, n+sides+2, n+sides+1 });
                }
            }
            var mesh = new Mesh { name = id + "Tail", subMeshCount = 2 };
            mesh.SetVertices(v); mesh.SetTriangles(triangles[0], 0); mesh.SetTriangles(triangles[1], 1);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return Save(mesh, Dir + "/" + id + "Tail.asset");
        }

        // A solid tapered ear: broad base, two shoulders, a pointed tip.
        private static void Ear(Transform parent, string name, Material m, float w, float h, float depth, float z = 0)
        {
            var go = Node(parent, name, new Vector3(0, 0, z)).gameObject;
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { new Vector3(-w / 2, 0, -depth / 2), new Vector3(w / 2, 0, -depth / 2),
                new Vector3(0, h, 0), new Vector3(-w / 2, 0, depth / 2), new Vector3(w / 2, 0, depth / 2) };
            mesh.triangles = new[] { 0,2,1, 3,4,2, 0,3,2, 1,2,4, 0,1,4, 0,4,3 };
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = m;
        }

        public static void DetailCar(GameObject root, GameObject chassis)
        {
            var ink = Mat("Ink", "202F38");
            var rubber = Mat("TireRubber", "252D33", .04f);
            var tread = Mat("TireTread", "38434B", .06f);
            var metal = Mat("BrushedMetal", "9AADB7", .5f);
            var cream = Mat("Ivory", "FFF0D3", .32f);
            var accent = Mat("ProfileAccent", "FFFFFF", .3f);
            // Preserve palette detail in the CC0 mesh, replacing just the blue paint
            // triangles so profile colors also work on the imported chassis.
            var tex = new Texture2D(2, 2);
            ImageConversion.LoadImage(tex, File.ReadAllBytes(AssetTrialAuthoring.Root + "/Kenney/colormap.png"));
            foreach (var filter in chassis.GetComponentsInChildren<MeshFilter>())
            {
                if (!filter.GetComponent<Renderer>().enabled) continue;
                var mesh = Object.Instantiate(filter.sharedMesh);
                var paint = new List<int>(); var original = new List<int>();
                var uv = mesh.uv;
                var triangles = mesh.triangles;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    var p = (uv[triangles[i]] + uv[triangles[i+1]] + uv[triangles[i+2]]) / 3;
                    var c = tex.GetPixelBilinear(p.x, p.y);
                    var list = c.b > c.r * 1.12f && c.b > c.g * 1.06f ? paint : original;
                    list.AddRange(new[] { triangles[i], triangles[i+1], triangles[i+2] });
                }
                mesh.subMeshCount = 2;
                mesh.SetTriangles(original, 0); mesh.SetTriangles(paint, 1);
                mesh = Save(mesh, Dir + "/KartChassis.asset");
                filter.sharedMesh = mesh;
                var r = filter.GetComponent<MeshRenderer>();
                // Keep this renderer out of the whole-renderer Accent tint convention.
                // Split paint to its own child renderer below.
                var painted = Object.Instantiate(mesh);
                painted.subMeshCount = 1; painted.SetTriangles(paint, 0);
                var child = Node(filter.transform, "PaintAccent", Vector3.zero);
                child.gameObject.AddComponent<MeshFilter>().sharedMesh = Save(painted, Dir + "/KartPaint.asset");
                child.gameObject.AddComponent<MeshRenderer>().sharedMaterial = accent;
                mesh.subMeshCount = 1; mesh.SetTriangles(original, 0); EditorUtility.SetDirty(mesh);
                r.sharedMaterials = new[] { r.sharedMaterial };
            }
            Object.DestroyImmediate(tex);
            foreach (var name in new[] { "WheelFL", "WheelFR", "WheelRL", "WheelRR" })
            {
                var spin = root.GetComponentsInChildren<Transform>().Single(t => t.name == name).Find("Spin");
                foreach (Transform child in spin.Cast<Transform>().ToArray()) Object.DestroyImmediate(child.gameObject);
                spin.localRotation = Quaternion.Euler(0, 0, 90);
                var tire = Node(spin, "RoundedTire", Vector3.zero);
                tire.gameObject.AddComponent<MeshFilter>().sharedMesh = TireMesh();
                tire.gameObject.AddComponent<MeshRenderer>().sharedMaterial = rubber;
                Part(spin, "AlloyRim", cream, Vector3.zero, new Vector3(.40f, .153f, .40f), PrimitiveType.Cylinder);
                foreach (int side in new[] { -1, 1 })
                {
                    Part(spin, "BrakeDisc", ink, new Vector3(0, side * .157f, 0), new Vector3(.30f, .008f, .30f), PrimitiveType.Cylinder);
                    Part(spin, "AxleCap", metal, new Vector3(0, side * .18f, 0), new Vector3(.10f, .025f, .10f), PrimitiveType.Cylinder);
                    for (int i = 0; i < 5; i++)
                    {
                        float a = i * Mathf.PI * 2 / 5;
                        Bar(spin, "RimSpoke", cream, new Vector3(0, side * .17f, 0), new Vector3(Mathf.Sin(a) * .16f, side * .17f, Mathf.Cos(a) * .16f), .034f);
                    }
                    for (int i = 0; i < 20; i++)
                    {
                        float a = i * Mathf.PI * 2 / 20;
                        Part(spin, "TreadBlock", tread, new Vector3(Mathf.Sin(a) * .313f, side * .06f, Mathf.Cos(a) * .313f),
                            new Vector3(.06f, .12f, .027f), PrimitiveType.Cube, new Vector3(0, i * 18, side * 20));
                    }
                }
            }
            var detail = Node(root.transform, "RallyDetails", Vector3.zero);
            Part(detail, "BucketSeat", ink, new Vector3(0, .78f, -.59f), new Vector3(.68f, .40f, .39f));
            Part(detail, "SeatBack", ink, new Vector3(0, 1.0f, -.74f), new Vector3(.62f, .64f, .19f));
            foreach (int side in new[] { -1, 1 })
            {
                Bar(detail, "RollHoop", metal, new Vector3(side * .42f, .58f, -.80f), new Vector3(side * .42f, 1.34f, -.84f), .065f);
                Part(detail, "LampHousing", ink, new Vector3(side * .48f, .51f, 1.41f), new Vector3(.27f, .26f, .16f));
                Part(detail, "Headlamp", Mat("Headlamp", "FFF1B9", .65f), new Vector3(side * .48f, .51f, 1.49f), new Vector3(.19f, .18f, .05f));
                Part(detail, "RearLamp", Mat("TailLamp", "E54D43", .5f), new Vector3(side * .48f, .48f, -1.58f), new Vector3(.22f, .10f, .05f));
                Bar(detail, "Exhaust", metal, new Vector3(side * .59f, .35f, -.81f), new Vector3(side * .59f, .35f, -1.56f), .11f);
                Part(detail, "ExhaustInset", ink, new Vector3(side * .59f, .35f, -1.62f), new Vector3(.075f, .075f, .018f));
                Part(detail, "RacingStripe", cream, new Vector3(side * .12f, .584f, .92f), new Vector3(.08f, .018f, .64f), PrimitiveType.Cube);
            }
            Bar(detail, "RollHoop", metal, new Vector3(-.42f, 1.34f, -.84f), new Vector3(.42f, 1.34f, -.84f), .065f);
            Part(detail, "FrontSplitter", ink, new Vector3(0, .22f, 1.57f), new Vector3(1.44f, .08f, .16f), PrimitiveType.Cube);
            Part(detail, "RearDiffuser", ink, new Vector3(0, .22f, -1.62f), new Vector3(1.27f, .08f, .19f), PrimitiveType.Cube);
            Part(detail, "HoodRoundel", cream, new Vector3(0, .61f, .92f), new Vector3(.34f, .018f, .34f));
            Bar(detail, "RaceNumber7", ink, new Vector3(-.075f, .624f, 1.0f), new Vector3(.075f, .624f, 1.0f), .025f);
            Bar(detail, "RaceNumber7", ink, new Vector3(.075f, .624f, 1.0f), new Vector3(-.025f, .624f, .83f), .025f);
            var steering = root.GetComponentsInChildren<Transform>().Single(t => t.name == "DashWheel");
            steering.GetComponent<Renderer>().enabled = false;
            steering.localScale = Vector3.one;
            steering.localPosition = new Vector3(0, 1.14f, .20f);
            steering.Find("GripL").localPosition = new Vector3(-.19f, 0, 0);
            steering.Find("GripR").localPosition = new Vector3(.19f, 0, 0);
            for (int i = 0; i < 24; i++)
            {
                float a = i * Mathf.PI * 2 / 24, b = (i + 1) * Mathf.PI * 2 / 24;
                Bar(steering, "SteeringRim", ink, new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)) * .23f,
                    new Vector3(Mathf.Sin(b), 0, Mathf.Cos(b)) * .23f, .035f);
            }
            foreach (int side in new[] { -1, 1 })
                Bar(steering, "SteeringSpoke", metal, Vector3.zero, new Vector3(side * .21f, 0, 0), .035f);
            Part(steering, "SteeringHub", ink, Vector3.zero, new Vector3(.1f, .04f, .1f));
            var seat = root.GetComponentsInChildren<Transform>().Single(t => t.name == "SeatMount");
            seat.localPosition = new Vector3(0, .77f, -.38f);
            MergeRigidParts(detail.gameObject);
            MergeRigidParts(steering.gameObject);
            foreach (var spin in root.GetComponentsInChildren<Transform>().Where(t => t.name == "Spin").ToArray()) MergeRigidParts(spin.gameObject);
        }

        private static Mesh TireMesh()
        {
            var vertices = new List<Vector3>(); var indices = new List<int>();
            const int rings = 32, sides = 10;
            for (int i = 0; i <= rings; i++)
            for (int j = 0; j <= sides; j++)
            {
                float a = i * Mathf.PI * 2 / rings, b = j * Mathf.PI * 2 / sides;
                float r = .249f + .075f * Mathf.Cos(b);
                vertices.Add(new Vector3(Mathf.Sin(a) * r, Mathf.Sin(b) * .153f, Mathf.Cos(a) * r));
                if (i == rings || j == sides) continue;
                int n = i * (sides + 1) + j;
                indices.AddRange(new[] { n, n+sides+1, n+1, n+1, n+sides+1, n+sides+2 });
            }
            var mesh = new Mesh { name = "RoundedKartTire" };
            mesh.SetVertices(vertices); mesh.SetTriangles(indices, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return Save(mesh, Dir + "/RoundedKartTire.asset");
        }

        // One renderer per material per articulated node, rather than one per
        // eye glint, finger, tread block, etc. Accent groups stay independently tintable.
        private static void MergeRigidParts(GameObject root)
        {
            foreach (var parent in root.GetComponentsInChildren<Transform>(true).Reverse().ToArray())
            {
                var parts = parent.Cast<Transform>().Where(t => t.GetComponent<MeshFilter>() != null && t.childCount == 0
                    && t.GetComponent<Renderer>().sharedMaterials.Length == 1).ToArray();
                foreach (var group in parts.GroupBy(t => (t.GetComponent<Renderer>().sharedMaterial, accent: t.name.Contains("Accent"))))
                {
                    var mesh = new Mesh { name = root.name + "_" + parent.name + "_" + group.Key.sharedMaterial.name };
                    mesh.indexFormat = IndexFormat.UInt32;
                    mesh.CombineMeshes(group.Select(t => new CombineInstance { mesh = t.GetComponent<MeshFilter>().sharedMesh,
                        transform = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale) }).ToArray(), true, true);
                    var path = Dir + "/" + mesh.name + (group.Key.accent ? "Accent" : "") + ".asset";
                    var go = Node(parent, group.Key.accent ? "MergedAccent" : "Merged_" + group.Key.sharedMaterial.name, Vector3.zero).gameObject;
                    go.AddComponent<MeshFilter>().sharedMesh = Save(mesh, path);
                    go.AddComponent<MeshRenderer>().sharedMaterial = group.Key.sharedMaterial;
                    foreach (var t in group) Object.DestroyImmediate(t.gameObject);
                }
            }
        }

        public static void Preview()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(.55f, .65f, .75f);
            RenderSettings.ambientEquatorColor = new Color(.43f, .47f, .50f);
            RenderSettings.ambientGroundColor = new Color(.22f, .27f, .31f);
            var light = new GameObject("StudioKey").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.1f; light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(40, -35, 0);
            var floor = Part(null, "Floor", Mat("StudioFloor", "395568"), new Vector3(0, -.08f, 0), new Vector3(200, .10f, 200), PrimitiveType.Cube);
            var camera = new GameObject("StudioCamera").AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.16f, .24f, .31f);
            camera.fieldOfView = 33; camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            var actors = new List<GameObject>();
            for (int i = 0; i < 4; i++)
            {
                var id = RacerVisualAuthoring.CharacterIds[i];
                var def = AssetDatabase.LoadAssetAtPath<CharacterDefinitionAsset>(CharacterPath(id));
                var actor = Object.Instantiate(def.prefab); actors.Add(actor);
                actor.transform.position = new Vector3((i - 1.5f) * 1.38f, .48f, 0);
                actor.transform.rotation = Quaternion.Euler(0, -12, 0);
                var block = new MaterialPropertyBlock(); block.SetColor("_BaseColor", RacerVisualRig.SlotColor(i));
                foreach (var r in actor.GetComponentsInChildren<Renderer>()) if (r.name.Contains("Accent")) r.SetPropertyBlock(block);
                foreach (var t in actor.GetComponentsInChildren<Transform>())
                    if (t.name == "LegL" || t.name == "LegR") t.localRotation = Quaternion.Euler(-68, 0, 0);
                Part(null, "DisplaySeat", Mat("StudioPedestal", "233B4B"), new Vector3((i - 1.5f) * 1.38f, .20f, -.03f), new Vector3(.65f, .25f, .65f), PrimitiveType.Cylinder);
            }
            camera.orthographic = true; camera.orthographicSize = 1.23f;
            camera.aspect = 1600f / 650f;
            camera.transform.position = new Vector3(.45f, 2.7f, 10); camera.transform.LookAt(new Vector3(0, 1.18f, 0));
            Capture(camera, "racer-roster.png", 1600, 650);
            foreach (var actor in actors) Object.DestroyImmediate(actor);
            foreach (var go in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Where(t => t.name == "DisplaySeat").ToArray()) Object.DestroyImmediate(go.gameObject);
            var car = new GameObject("BeautyKart").AddComponent<VehiclePresentation>();
            car.SetRacer(AssetDatabase.LoadAssetAtPath<CharacterDefinitionAsset>(CharacterPath("fox")), 0);
            car.SetVehicle(AssetDatabase.LoadAssetAtPath<VehicleDefinitionAsset>(AssetTrialAuthoring.Output + "/car-01.asset"));
            car.ApplyPose(new Core.Driving.MotorState { x = 0, z = 0, heading = 0, tick = 1, grounded = true }, 0);
            // Set the still pose explicitly because an editor render has no delta time.
            foreach (var t in car.GetComponentsInChildren<Transform>())
                if (t.name == "ArmL" || t.name == "ArmR") t.localRotation = Quaternion.Euler(-65, 0, 0);
            camera.orthographic = false; camera.aspect = 1.6f;
            camera.transform.position = new Vector3(4.3f, 3f, 5.4f); camera.transform.LookAt(new Vector3(0, .85f, 0));
            Capture(camera, "kart-fox-beauty.png");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        internal static void Capture(Camera camera, string name, int width = 1600, int height = 1000, string directory = null)
        {
            var previous = QualitySettings.renderPipeline;
            var previousAsyncCompilation = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            var active = RenderTexture.active;
            var rt = new RenderTexture(width, height, 24);
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                QualitySettings.renderPipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(VisualBenchmarkSetup.PcPipelinePath);
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = rt });
                RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, width, height), 0, 0); tex.Apply();
                string output = directory ?? "docs/qa/asset-trial-2026-10-23/racer-pass";
                Directory.CreateDirectory(output); File.WriteAllBytes(output + "/" + name, tex.EncodeToPNG());
            }
            finally { ShaderUtil.allowAsyncCompilation = previousAsyncCompilation; QualitySettings.renderPipeline = previous; RenderTexture.active = active; Object.DestroyImmediate(rt); Object.DestroyImmediate(tex); }
        }
    }
}
