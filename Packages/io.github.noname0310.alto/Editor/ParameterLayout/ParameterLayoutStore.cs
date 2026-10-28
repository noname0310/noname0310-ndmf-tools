using System;
using System.IO;
using UnityEngine;

namespace Noname.AvatarTools.ParameterLayout
{
    internal sealed class ParameterLayoutStore
    {
        internal static readonly ParameterLayoutStore Project = new(Path.Combine("ProjectSettings", "ALTO", "ParameterLayouts"));
        private readonly string directory;
        internal ParameterLayoutStore(string directory) => this.directory = Path.GetFullPath(directory);

        internal static bool ValidId(string id) => id != null && id.StartsWith("avtr_", StringComparison.Ordinal) &&
            Guid.TryParseExact(id.Substring(5), "D", out _);

        internal string PathFor(string id)
        {
            if (!ValidId(id)) throw new InvalidOperationException("A valid avatar Blueprint ID is required to load a parameter layout.");
            return Path.Combine(directory, id + ".json");
        }

        internal ParameterLayoutRecord Read(string id)
            => Read(id, false);

        private ParameterLayoutRecord Read(string id, bool allowInvalidated)
        {
            string path = PathFor(id);
            if (!File.Exists(path)) return null;
            ParameterLayoutRecord record;
            try { record = JsonUtility.FromJson<ParameterLayoutRecord>(File.ReadAllText(path)); }
            catch (Exception exception) { throw new InvalidOperationException("Cannot read the reference parameter layout: " + path, exception); }
            ParameterLayoutModel.Validate(record, allowInvalidated);
            if (record.BlueprintId != id) throw new InvalidOperationException("The parameter layout belongs to a different Blueprint ID.");
            return record;
        }

        internal void Commit(ParameterLayoutRecord record)
        {
            ParameterLayoutModel.Validate(record);
            Write(record);
        }

        // A successful reference upload without our component replaces the server
        // avatar too. Keep the previous declaration list for diagnosis, but never
        // use it as a valid contract for that newer upload.
        internal bool InvalidateReference(string id, ParameterLayoutPlatform platform)
        {
            var record = Read(id, true);
            if (record == null || record.ReferencePlatform != platform || !string.IsNullOrEmpty(record.InvalidatedUtc)) return false;
            record.InvalidatedUtc = DateTime.UtcNow.ToString("O");
            Write(record);
            return true;
        }

        private void Write(ParameterLayoutRecord record)
        {
            string path = PathFor(record.BlueprintId);
            Directory.CreateDirectory(directory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonUtility.ToJson(record, true));
                // A failed write must leave the last confirmed upload intact.
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
