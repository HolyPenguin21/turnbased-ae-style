#if UNITY_INCLUDE_TESTS
using System;
using System.IO;
using System.Linq;
using System.Text;
using Game.Core;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AtomicJsonFileTests
    {
        private string directory, path;
        [SetUp] public void Setup()
        {
            directory = Path.Combine(Path.GetTempPath(), "atomic-json-" + Guid.NewGuid().ToString("N"));
            path = Path.Combine(directory, "save.json");
        }
        [TearDown] public void Cleanup() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

        private static string Read(string file)
        {
            string text = File.ReadAllText(file);
            if (text == "future") throw new NotSupportedException("newer");
            if (!text.StartsWith("v")) throw new InvalidDataException("broken");
            return text;
        }

        [Test]
        public void MissingFileAndBackupLoadsNothing()
        {
            Assert.IsFalse(AtomicJsonFile.Exists(path));
            Assert.IsNull(AtomicJsonFile.Load(path, Read, "Recovered: ", out string notice));
            Assert.IsNull(notice);
        }

        [Test]
        public void SecondSaveKeepsThePreviousVersionAsBackup()
        {
            AtomicJsonFile.Save(path, Encoding.UTF8.GetBytes("v1"));
            AtomicJsonFile.Save(path, Encoding.UTF8.GetBytes("v2"));
            Assert.AreEqual("v2", AtomicJsonFile.Load(path, Read, "Recovered: ", out string notice));
            Assert.IsNull(notice);
            Assert.AreEqual("v1", File.ReadAllText(path + ".bak"));
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }

        [Test]
        public void CorruptFileRecoversFromBackupAndIsMovedAside()
        {
            AtomicJsonFile.Save(path, Encoding.UTF8.GetBytes("v1"));
            AtomicJsonFile.Save(path, Encoding.UTF8.GetBytes("v2"));
            File.WriteAllText(path, "garbage");
            Assert.AreEqual("v1", AtomicJsonFile.Load(path, Read, "Recovered: ", out string notice));
            Assert.AreEqual("Recovered: broken", notice);
            Assert.IsFalse(File.Exists(path));
            Assert.IsTrue(File.Exists(path + ".bak"), "the known-good backup survives recovery");
            Assert.AreEqual(1, Directory.GetFiles(directory, "save.json.corrupt-*").Length);
            Assert.IsTrue(AtomicJsonFile.Exists(path));
        }

        [Test]
        public void FutureSchemaIsNeverReplacedByBackup()
        {
            AtomicJsonFile.Save(path, Encoding.UTF8.GetBytes("v1"));
            AtomicJsonFile.Save(path, Encoding.UTF8.GetBytes("future"));
            Assert.Throws<NotSupportedException>(() => AtomicJsonFile.Load(path, Read, "Recovered: ", out _));
            Assert.AreEqual("future", File.ReadAllText(path));
            Assert.IsFalse(Directory.GetFiles(directory).Any(f => f.Contains(".corrupt-")));
        }
    }
}
#endif
