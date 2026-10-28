using System;
using System.Text.RegularExpressions;
using Noname.AvatarTools.Analysis;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.ObjectOptimization
{
    [InitializeOnLoad]
    internal static class ExternalParameterIntegration
    {
        static ExternalParameterIntegration() => ExternalParameterAccess.Register("Avatar Optimizer Asset Description", Collect);

        private static void Collect(ExternalParameterAccess result)
        {
            // Asset Description has no public query API. Read its serialized schema without invoking
            // internal optimizer code; an unknown schema disables pruning rather than losing OSC inputs.
            foreach (var guid in AssetDatabase.FindAssets("t:AssetDescription"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset == null || asset.GetType().FullName != "Anatawa12.AvatarOptimizer.AssetDescription") continue;
                using var serialized = new SerializedObject(asset);
                Read("parametersReadByExternalTools", result.Readers);
                Read("parametersChangedByExternalTools", result.Writers);
                void Read(string field, System.Collections.Generic.ICollection<Func<string, bool>> predicates)
                {
                    var list = serialized.FindProperty(field);
                    if (list == null || !list.isArray) throw new InvalidOperationException("Unsupported Asset Description schema: " + asset.name + "/" + field);
                    for (int i = 0; i < list.arraySize; i++)
                    {
                        var entry = list.GetArrayElementAtIndex(i);
                        var name = entry.FindPropertyRelative("name");
                        var mode = entry.FindPropertyRelative("matchMode");
                        if (name == null || mode == null) throw new InvalidOperationException("Unsupported parameter entry: " + asset.name);
                        string value = name.stringValue;
                        if (string.IsNullOrEmpty(value)) continue;
                        if (mode.intValue == 0) predicates.Add(n => n == value);
                        else if (mode.intValue == 1)
                        {
                            var regex = new Regex("^(?:" + value + ")$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                            predicates.Add(n => { try { return n != null && regex.IsMatch(n); } catch (RegexMatchTimeoutException) { return true; } });
                        }
                        else throw new InvalidOperationException("Unsupported parameter match mode: " + asset.name);
                    }
                }
            }
        }
    }
}
