using System;
using System.Linq;
using FamilyRacing.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FamilyRacing.Tests
{
    public sealed class ProductionSceneAuthoringTests
    {
        [OneTimeSetUp]
        public void Author() => ProductionSceneAuthoring.Create();

        [Test]
        public void SavedSceneBindsTheCompletePlayableCatalog()
        {
            ProductionSceneAuthoring.ValidateScene();
            var scene = EditorSceneManager.OpenScene(ProductionSceneAuthoring.ScenePath);
            var world = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<DrivingWorld>(true)).Single();
            var content = world.Load();
            Assert.That(content, Is.Not.Null);
            Assert.That(content.Courses.Count, Is.EqualTo(7), "Six campaign courses plus the original onboarding course");
            Assert.That(content.Events.Count, Is.EqualTo(80), "78 campaign events plus two tutorial helpers");
            Assert.That(content.Vehicles.Count, Is.EqualTo(8));
            Assert.That(world.Gameplay, Is.Not.Null);
            Assert.That(world.Tutorial, Is.Not.Null);
            Assert.That(world.Gameplay.Campaigns.Select(c => c.worldId), Is.EqualTo(FullContentValidation.WorldIds));
            Assert.That(world.GetComponent<ProductionVoicePresenter>(), Is.Not.Null);
        }

        [Test]
        public void AStaleSceneCannotPassShippingPreparation()
        {
            var scene = EditorSceneManager.OpenScene(ProductionSceneAuthoring.ScenePath);
            var world = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<DrivingWorld>(true)).Single();
            var serialized = new SerializedObject(world);
            serialized.FindProperty("tracks").arraySize = 1;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.SaveScene(scene);
            try
            {
                Assert.Throws<InvalidOperationException>(() => ProductionSceneAuthoring.ValidateScene());
            }
            finally { ProductionSceneAuthoring.Create(); }
        }

        [Test]
        public void MissingSpokenPromptsRemainAProductionBlocker()
        {
            var gaps = FullContentSetup.ValidateProduction();
            Assert.That(gaps.Where(gap => !gap.StartsWith("voice", StringComparison.Ordinal)), Is.Empty);
            if (gaps.Count > 0)
                Assert.Throws<System.IO.InvalidDataException>(() => ProductionSceneAuthoring.PrepareBuild());
            else
                Assert.That(ProductionSceneAuthoring.PrepareBuild(), Is.EqualTo(new[] { ProductionSceneAuthoring.ScenePath }));
        }
    }
}
