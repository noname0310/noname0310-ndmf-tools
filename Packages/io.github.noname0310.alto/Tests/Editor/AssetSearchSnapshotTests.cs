using System;
using System.Linq;
using Noname.AvatarTools.Analysis;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed class AssetSearchSnapshotTests
    {
        [Test]
        public void SearchInvalidatesForAssetDatabaseChangesWithoutWaitingForAnEditorFrame()
        {
            string name = "ALTO_Search_" + Guid.NewGuid().ToString("N");
            string path = "Assets/" + name + ".asset", moved = "Assets/" + name + "_moved.asset";
            var search = new AssetSearchSnapshot(name + " t:VRCExpressionParameters");
            Assert.That(search.Guids, Is.Empty);
            var initial = search.Guids;
            Assert.That(search.Guids, Is.SameAs(initial));
            try
            {
                var asset = ScriptableObject.CreateInstance<VRCExpressionParameters>();
                AssetDatabase.CreateAsset(asset, path);
                string guid = AssetDatabase.AssetPathToGUID(path);
                Assert.That(search.Guids, Is.EquivalentTo(new[] { guid }));
                Assert.That(search.Guids, Is.EquivalentTo(AssetDatabase.FindAssets(name + " t:VRCExpressionParameters")));
                Assert.That(AssetDatabase.MoveAsset(path, moved), Is.Empty);
                Assert.That(search.Guids, Is.EquivalentTo(new[] { guid }));
                Assert.That(AssetDatabase.GUIDToAssetPath(search.Guids.Single()), Is.EqualTo(moved));
                AssetDatabase.ImportAsset(moved, ImportAssetOptions.ForceUpdate);
                Assert.That(search.Guids, Is.EquivalentTo(new[] { guid }));
                Assert.That(AssetDatabase.DeleteAsset(moved), Is.True);
                Assert.That(search.Guids, Is.Empty);
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
                AssetDatabase.DeleteAsset(moved);
            }
        }
    }
}
