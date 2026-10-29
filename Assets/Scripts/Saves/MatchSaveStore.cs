using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Game.Core;

namespace Game.Saves
{
    // The file format has no fixed slot count. UI pagination is independent of storage.
    internal sealed class MatchSaveStore
    {
        internal const int MaximumFileBytes = 64 * 1024 * 1024;
        private readonly string directory;
        internal MatchSaveStore(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Missing save directory.");
            this.directory = directory;
        }
        private string PathFor(int slot)
        {
            if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot));
            return Path.Combine(directory, "slot-" + slot.ToString(CultureInfo.InvariantCulture) + ".json");
        }
        internal int[] Slots()
        {
            var result = new HashSet<int>();
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory, "slot-*.json*"))
                {
                    string name = Path.GetFileName(file);
                    string suffix = name.EndsWith(".json.bak", StringComparison.Ordinal) ? ".json.bak" : ".json";
                    if (!name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                    string number = name.Substring(5, name.Length - 5 - suffix.Length);
                    if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                        && id >= 0 && number == id.ToString(CultureInfo.InvariantCulture)) result.Add(id);
                }
            }
            catch (DirectoryNotFoundException) { return Array.Empty<int>(); }
            return result.OrderBy(x => x).ToArray();
        }
        internal bool Exists(int slot) => AtomicJsonFile.Exists(PathFor(slot));
        internal void Delete(int slot)
        {
            string path = PathFor(slot);
            // Directory.Exists also returns false on access errors. Perform the operation
            // and tolerate only an actually missing directory; all other failures reach UI.
            try
            {
                // Stop on failure before removing the primary. Leaving a backup behind
                // would make a deleted slot reappear through normal recovery.
                File.Delete(path + ".tmp");
                File.Delete(path + ".bak");
                File.Delete(path);
            }
            catch (DirectoryNotFoundException) { /* The slot's containing directory is already gone. */ }
        }
        internal void Save(int slot, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumFileBytes)
                throw new ArgumentException("The saved match file has an invalid size.");
            AtomicJsonFile.Save(PathFor(slot), bytes);
        }
        internal T Load<T>(int slot, Func<byte[], T> decode, out string notice, bool quarantineCorruptFile = true) where T : class
        {
            if (decode == null) throw new ArgumentNullException(nameof(decode));
            return AtomicJsonFile.Load(PathFor(slot), path => decode(ReadBounded(path)),
                "Recovered the previous save from backup. The latest file could not be loaded: ", out notice, quarantineCorruptFile);
        }
        private static byte[] ReadBounded(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > MaximumFileBytes)
                    throw new ArgumentException("The saved match file has an invalid size.");
                var bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw new EndOfStreamException("Saved match file was truncated.");
                    offset += count;
                }
                return bytes;
            }
        }
    }
}
