using System.Collections;
using System.Collections.Generic;
using FamilyRacing.Core.Foundation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    // M4 Task 2: the tablet/PC render profiles must actually switch the
    // render pipeline and frame-rate target while leaving every
    // collision/gameplay surface untouched. Profiles own presentation
    // settings only — identical collision, checkpoints and route visibility
    // is a hard milestone constraint.
    // Editor-only fixture: pipeline assets are built in-memory, and
    // QualitySettings/Time capture is restored in teardown.
    [UnityPlatform(RuntimePlatform.WindowsEditor, RuntimePlatform.OSXEditor,
        RuntimePlatform.LinuxEditor)]
    public sealed class RenderProfilePlayTests
    {
        private GameObject host;
        private RenderProfileController controller;
        private UniversalRenderPipelineAsset tabletPipeline;
        private UniversalRenderPipelineAsset pcPipeline;
        private UniversalRendererData tabletRenderer;
        private UniversalRendererData pcRenderer;
        private RenderPipelineAsset savedPipeline;
        private int savedTargetFrameRate;
        private int savedVSync;

        private static UniversalRenderPipelineAsset MakePipeline(
            out UniversalRendererData renderer, float renderScale,
            int shadowCascades, float shadowDistance,
            bool hdr, int msaa)
        {
            renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            var pipeline = UniversalRenderPipelineAsset.Create(renderer);
            pipeline.renderScale = renderScale;
            pipeline.shadowCascadeCount = shadowCascades;
            pipeline.shadowDistance = shadowDistance;
            pipeline.supportsHDR = hdr;
            pipeline.msaaSampleCount = msaa;
            return pipeline;
        }

        [SetUp]
        public void SetUp()
        {
            savedPipeline = QualitySettings.renderPipeline;
            savedTargetFrameRate = Application.targetFrameRate;
            savedVSync = QualitySettings.vSyncCount;
            tabletPipeline = MakePipeline(out tabletRenderer,
                renderScale: RenderProfileController.TabletRenderScale,
                shadowCascades: 1, shadowDistance: 30f,
                hdr: false, msaa: 1);
            pcPipeline = MakePipeline(out pcRenderer,
                renderScale: RenderProfileController.PcRenderScale,
                shadowCascades: 2, shadowDistance: 80f,
                hdr: true, msaa: 2);
            host = new GameObject("render-profile-test");
            controller = host.AddComponent<RenderProfileController>();
            var serialized = controller.GetType()
                .GetField("tabletPipeline",
                    System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance);
            serialized.SetValue(controller, tabletPipeline);
            controller.GetType()
                .GetField("pcPipeline",
                    System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance)
                .SetValue(controller, pcPipeline);
        }

        [TearDown]
        public void TearDown()
        {
            QualitySettings.renderPipeline = savedPipeline;
            QualitySettings.vSyncCount = savedVSync;
            Application.targetFrameRate = savedTargetFrameRate;
            if (host != null) Object.Destroy(host);
            foreach (var asset in new Object[]
                     { tabletPipeline, pcPipeline, tabletRenderer, pcRenderer })
                if (asset != null) Object.DestroyImmediate(asset);
        }

        // Every collision-relevant global the profiles are forbidden to
        // touch: the layer matrix, gravity and solver budgets. Checkpoints
        // and route data live in the track asset the controller never
        // references — it has no gameplay fields by construction.
        private static string CollisionFingerprint()
        {
            var text = new System.Text.StringBuilder();
            text.Append(Physics.gravity.ToString("R"));
            text.Append('|').Append(Physics.defaultContactOffset);
            text.Append('|').Append(Physics.defaultSolverIterations);
            text.Append('|').Append(Physics.defaultSolverVelocityIterations);
            for (var a = 0; a < 32; a++)
                for (var b = 0; b < 32; b++)
                    text.Append(Physics.GetIgnoreLayerCollision(a, b) ? '0' : '1');
            return text.ToString();
        }

        [Test]
        public void TabletAndPcProfilesKeepCollisionEqual()
        {
            var before = CollisionFingerprint();

            controller.Apply(true);
            Assert.That(controller.ActiveProfile,
                Is.EqualTo(RenderProfileController.ProfileTablet));
            Assert.That(QualitySettings.renderPipeline,
                Is.SameAs(tabletPipeline));
            Assert.That(Application.targetFrameRate, Is.EqualTo(30),
                "the tablet profile caps at 30 fps");
            Assert.That(controller.ActiveRenderScale,
                Is.EqualTo(0.75f).Within(0.0001f));
            var tabletFingerprint = CollisionFingerprint();
            Assert.That(tabletFingerprint, Is.EqualTo(before),
                "the tablet profile must not alter collision layers, "
                + "gravity or solver budgets");

            controller.Apply(false);
            Assert.That(controller.ActiveProfile,
                Is.EqualTo(RenderProfileController.ProfilePc));
            Assert.That(QualitySettings.renderPipeline,
                Is.SameAs(pcPipeline));
            Assert.That(Application.targetFrameRate, Is.EqualTo(60),
                "the PC profile targets 60 fps");
            Assert.That(controller.ActiveRenderScale,
                Is.EqualTo(1.0f).Within(0.0001f));
            var pcFingerprint = CollisionFingerprint();
            Assert.That(pcFingerprint, Is.EqualTo(before),
                "the PC profile must not alter collision layers, "
                + "gravity or solver budgets");
        }

        [Test]
        public void MissingPipelineRefusesHonestly()
        {
            controller.GetType()
                .GetField("tabletPipeline",
                    System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance)
                .SetValue(controller, null);
            LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex(
                    "tablet pipeline asset is not wired"));
            controller.Apply(true);
            Assert.That(controller.ActiveProfile,
                Is.EqualTo(RenderProfileController.ProfileMissing));
        }

        [Test]
        public void ResolveTabletFollowsPlatformAndOverride()
        {
            Assert.That(RenderProfileController.ResolveTablet(
                RuntimePlatform.Android, null), Is.True);
            Assert.That(RenderProfileController.ResolveTablet(
                RuntimePlatform.WindowsPlayer, null), Is.False);
            Assert.That(RenderProfileController.ResolveTablet(
                RuntimePlatform.WindowsPlayer,
                new[] { "--render-profile", "tablet" }), Is.True);
            Assert.That(RenderProfileController.ResolveTablet(
                RuntimePlatform.Android,
                new[] { "--render-profile", "pc" }), Is.False);
        }
    }
}
