using System;
using System.IO;

namespace Game.Core
{
    // Shared disk policy of the local save stores: atomic replace with a .bak of the previous
    // version, and recovery from that backup when the main file cannot be read. Each store
    // keeps its own format, validation and size limits in `read`.
    public static class AtomicJsonFile
    {
        public static bool Exists(string path) => File.Exists(path) || File.Exists(path + ".bak");

        // Null when neither the file nor its backup exists. A future schema (NotSupportedException)
        // is never replaced by an older client's backup.
        public static T Load<T>(string path, Func<string, T> read, string recoveredNotice, out string notice,
            bool quarantineCorruptFile = true) where T : class
        {
            notice = null;
            if (!Exists(path)) return null;
            try { return read(path); }
            catch (NotSupportedException) { throw; }
            catch (Exception ex)
            {
                var restored = read(path + ".bak");
                notice = recoveredNotice + ex.Message;
                if (quarantineCorruptFile && File.Exists(path)) File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                // Keep the known-good backup during recovery's next atomic write.
                return restored;
            }
        }

        public static void Save(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
