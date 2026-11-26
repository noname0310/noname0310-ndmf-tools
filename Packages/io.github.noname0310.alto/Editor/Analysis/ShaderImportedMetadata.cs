using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Analysis
{
    // Importers may generate a Shader without an editable ShaderLab asset. Unity's
    // imported metadata supplies dependency ownership and fixed-function readers;
    // the public ShaderData API supplies programs. Neither is editable source.
    internal sealed class ShaderImportedMetadata
    {
        internal sealed class Pass
        {
            internal string Name, Import, RasterIssue;
            internal bool Grab;
        }

        internal readonly List<List<Pass>> Subshaders = new();
        internal readonly List<(string Shader, string Pass)> Dependencies = new();
        internal readonly HashSet<string> StateIdentifiers = new(StringComparer.Ordinal);
        internal string DependencyIssue, StateIssue;

        // Validate the serialization schema, rather than interpreting unknown
        // fields as unused. Program inspection remains available on other schemas.
        private static readonly HashSet<string> StateFields = new(StringComparer.Ordinal)
        {
            "m_Name", "rtBlend0", "rtBlend1", "rtBlend2", "rtBlend3", "rtBlend4", "rtBlend5", "rtBlend6", "rtBlend7",
            "rtSeparateBlend", "zClip", "zTest", "zWrite", "culling", "conservative", "offsetFactor", "offsetUnits",
            "alphaToMask", "stencilOp", "stencilOpFront", "stencilOpBack", "stencilReadMask", "stencilWriteMask",
            "stencilRef", "fogStart", "fogEnd", "fogDensity", "fogColor", "fogMode", "gpuProgramID", "m_Tags", "m_LOD", "lighting"
        };

        internal static ShaderImportedMetadata Read(Shader shader, ShaderData data)
        {
            var result = new ShaderImportedMetadata();
            try
            {
                // The schema is checked in addition to the version. A new Unity
                // serializer needs explicit validation before authorizing absence.
                if (!Application.unityVersion.StartsWith("2022.3.", StringComparison.Ordinal))
                    throw new InvalidOperationException("unverified Unity imported ShaderLab schema");
                using var serialized = new SerializedObject(shader);
                var parsed = serialized.FindProperty("m_ParsedForm");
                if (parsed == null) throw new InvalidOperationException("imported ShaderLab metadata is unavailable");
                string fallback = Text(parsed, "m_FallbackName");
                if (!string.IsNullOrEmpty(fallback)) result.Dependencies.Add((fallback, null));
                var dependencies = Array(parsed, "m_Dependencies");
                for (int i = 0; i < dependencies.arraySize; i++)
                    result.Dependencies.Add((Text(dependencies.GetArrayElementAtIndex(i), "to"), null));
                var subs = Array(parsed, "m_SubShaders");
                if (subs.arraySize != data.SerializedSubshaderCount || subs.arraySize > data.SubshaderCount)
                    throw new InvalidOperationException("imported subshader inventory does not match native programs");
                for (int i = 0; i < subs.arraySize; i++)
                {
                    var sub = subs.GetArrayElementAtIndex(i);
                    result.ReadIdentifiers(sub.FindPropertyRelative("m_Tags"));
                    var passes = Array(sub, "m_Passes");
                    if (passes.arraySize != data.GetSerializedSubshader(i).PassCount)
                        throw new InvalidOperationException("imported pass inventory does not match native programs");
                    var entries = new List<Pass>(passes.arraySize); result.Subshaders.Add(entries);
                    for (int j = 0; j < passes.arraySize; j++)
                    {
                        var pass = passes.GetArrayElementAtIndex(j);
                        var type = Property(pass, "m_Type", SerializedPropertyType.Integer).intValue;
                        var state = Property(pass, "m_State", SerializedPropertyType.Generic);
                        var entry = new Pass { Name = Text(state, "m_Name"), Grab = type == 2 };
                        entries.Add(entry);
                        if (type == 1)
                        {
                            entry.Import = Text(pass, "m_UseName");
                            int split = entry.Import.LastIndexOf('/');
                            if (split <= 0 || split == entry.Import.Length - 1) throw new InvalidOperationException("invalid imported UsePass name");
                            entry.Name = entry.Import.Substring(split + 1);
                            result.Dependencies.Add((entry.Import.Substring(0, split), entry.Name));
                        }
                        else if (type != 0 && type != 2) throw new InvalidOperationException("unknown imported shader pass kind");
                        try
                        {
                            result.ReadState(state);
                            result.ReadIdentifiers(pass.FindPropertyRelative("m_Tags"));
                            var conservative = Property(state, "conservative", SerializedPropertyType.Generic);
                            string input = Text(conservative, "name");
                            float value = Property(conservative, "val", SerializedPropertyType.Float).floatValue;
                            if (input != "<noninit>" && input.Length != 0 || value != 0)
                                entry.RasterIssue = type == 1 ? "imported pass has an unverified inherited conservative raster state" :
                                    "a pass may use conservative rasterization";
                        }
                        catch (Exception exception)
                        {
                            result.StateIssue ??= exception.Message;
                            entry.RasterIssue = "unverified imported shader raster state: " + exception.Message;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                result.DependencyIssue = exception.Message;
                result.StateIssue ??= exception.Message;
            }
            return result;
        }

        internal string RasterizationIssue(ISet<string> selected)
        {
            if (DependencyIssue != null) return DependencyIssue;
            if (StateIssue != null) return StateIssue;
            var found = selected == null ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in Subshaders)
                foreach (var pass in sub)
                {
                    if (pass.Grab) return "a GrabPass has observable drawing side effects";
                    if (selected != null && !selected.Contains(pass.Name)) continue;
                    found?.Add(pass.Name);
                    if (pass.RasterIssue != null) return pass.RasterIssue;
                }
            if (selected != null && !found.IsSupersetOf(selected)) return "named pass raster state is unavailable";
            return null;
        }

        private void ReadState(SerializedProperty state)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var iterator = state.Copy(); var end = state.GetEndProperty(); bool enter = true;
            while (iterator.Next(enter) && !SerializedProperty.EqualContents(iterator, end))
            {
                enter = false;
                if (!StateFields.Contains(iterator.name)) throw new InvalidOperationException("unknown imported render state: " + iterator.name);
                seen.Add(iterator.name);
                ReadIdentifiers(iterator);
            }
            if (!seen.SetEquals(StateFields)) throw new InvalidOperationException("incomplete imported render-state schema");
        }

        private void ReadIdentifiers(SerializedProperty property)
        {
            if (property == null) throw new InvalidOperationException("missing imported render-state field");
            void Read(SerializedProperty field)
            {
                if (field.propertyType == SerializedPropertyType.String)
                {
                    string value = field.stringValue;
                    if (value == "<noninit>" || string.IsNullOrEmpty(value)) return;
                    // These are already decoded metadata strings, not HLSL.
                    // Quotes and comment markers in pass names or tags must not
                    // hide a possible identifier or invalidate the state proof.
                    for (int cursor = 0; cursor < value.Length;)
                    {
                        if (!PpLexer.IdentifierStart(value[cursor])) { cursor++; continue; }
                        int start = cursor++;
                        while (cursor < value.Length && PpLexer.IdentifierPart(value[cursor])) cursor++;
                        StateIdentifiers.Add(value.Substring(start, cursor - start));
                    }
                }
                else if (field.propertyType != SerializedPropertyType.Generic && field.propertyType != SerializedPropertyType.Integer &&
                    field.propertyType != SerializedPropertyType.Float && field.propertyType != SerializedPropertyType.Boolean &&
                    field.propertyType != SerializedPropertyType.ArraySize && field.propertyType != SerializedPropertyType.Color)
                    throw new InvalidOperationException("unverified imported render-state value: " + field.propertyType);
            }
            Read(property);
            if (property.propertyType != SerializedPropertyType.Generic) return;
            var iterator = property.Copy(); var end = property.GetEndProperty(); bool enter = true;
            while (iterator.Next(enter) && !SerializedProperty.EqualContents(iterator, end))
            {
                Read(iterator);
                // Strings expose their character array as children; it carries no
                // additional inputs and makes a metadata walk needlessly expensive.
                enter = iterator.propertyType == SerializedPropertyType.Generic;
            }
        }

        private static SerializedProperty Property(SerializedProperty parent, string name, SerializedPropertyType type)
        {
            var property = parent.FindPropertyRelative(name);
            if (property == null || property.propertyType != type) throw new InvalidOperationException("unverified imported field: " + name);
            return property;
        }

        private static SerializedProperty Array(SerializedProperty parent, string name)
        {
            var property = Property(parent, name, SerializedPropertyType.Generic);
            if (!property.isArray) throw new InvalidOperationException("unverified imported array: " + name);
            return property;
        }

        private static string Text(SerializedProperty parent, string name) => Property(parent, name, SerializedPropertyType.String).stringValue;
    }
}
