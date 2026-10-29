#if UNITY_INCLUDE_TESTS
using System;
using System.IO;
using System.Text;
using Game.Saves;
using NUnit.Framework;

namespace Game.Tests.Core
{
    public sealed class MatchSaveFileTests
    {
        [Serializable] public sealed class Sample { public int version; public string value; public float number; }
        // Self-reference is the point: the tests exercise deep chains and cycle rejection in MatchSaveJson.
#pragma warning disable UAC1005
        [Serializable] public sealed class Node { public long id; public string name; public Node next; }
#pragma warning restore UAC1005
        private string directory;
        [SetUp] public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "match-save-" + Guid.NewGuid().ToString("N"));
        [TearDown] public void TearDown() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

        [TestCase("{\"version\":1,\"value\":\"a\",\"number\":2,\"version\":2}")]
        [TestCase("{\"version\":1,\"value\":\"a\",\"number\":NaN}")]
        [TestCase("{\"version\":1,\"value\":\"a\",\"number\":1e999}")]
        [TestCase("{\"version\":1,\"value\":\"a\",\"number\":1e50}")]
        [TestCase("{\"version\":1,\"value\":\"a\"}")]
        [TestCase("{\"version\":1,\"value\":\"a\",\"number\":2,\"unknown\":3}")]
        [TestCase("{\"version\":1.5,\"value\":\"a\",\"number\":2}")]
        [TestCase("{\"version\":1,\"value\":\"a\",\"number\":2} trailing")]
        public void StrictStructureRejectsMalformedOrIncompleteJson(string json) =>
            Assert.Throws<ArgumentException>(() => MatchSaveJson.Validate(json, typeof(Sample)));

        [Test] public void StrictStructureAcceptsEscapesAndFiniteValues() =>
            MatchSaveJson.Validate("{\"version\":1,\"value\":\"a\\n\\u0410\\\"\",\"number\":-2.5e2}", typeof(Sample));

        [Test] public void RealDtoTransportPreservesDeepNullGraphAndExactInt64()
        {
            var root = new Node { id = long.MaxValue, name = "\"Україна\\\n" };
            var tail = root;
            for (int i = 0; i < 25; i++) { tail.next = new Node { id = i }; tail = tail.next; }
            string json = MatchSaveJson.Serialize(root);
            var restored = MatchSaveJson.Deserialize<Node>(json);
            Assert.That(restored.id, Is.EqualTo(long.MaxValue)); Assert.That(restored.name, Is.EqualTo(root.name));
            tail = restored;
            for (int i = 0; i < 25; i++) { tail = tail.next; Assert.That(tail.id, Is.EqualTo(i)); Assert.That(tail.name, Is.Null); }
            Assert.That(tail.next, Is.Null);
            root.next.id = 999;
            Assert.That(restored.next.id, Is.EqualTo(0));
        }
        [Test] public void RealDtoTransportRejectsCyclesAndNonfiniteValues()
        {
            var cycle = new Node(); cycle.next = cycle;
            Assert.Throws<ArgumentException>(() => MatchSaveJson.Serialize(cycle));
            Assert.Throws<ArgumentException>(() => MatchSaveJson.Serialize(new Sample { number = float.NaN }));
        }
        [TestCase(float.MaxValue)]
        [TestCase(float.MinValue)]
        [TestCase(float.Epsilon)]
        public void FiniteFloatBoundaryRoundtrip(float value)
        {
            var json = MatchSaveJson.Serialize(new Sample { number = value });
            Assert.That(MatchSaveJson.Deserialize<Sample>(json).number, Is.EqualTo(value));
        }

        private static string Read(byte[] bytes)
        {
            string value = Encoding.UTF8.GetString(bytes);
            if (value == "future") throw new NotSupportedException("Future version");
            if (!value.StartsWith("valid-", StringComparison.Ordinal)) throw new ArgumentException("Corrupt");
            return value;
        }
        [Test] public void SlotFilesRoundtripAndRecoverLastGoodBackup()
        {
            var store = new MatchSaveStore(directory);
            store.Save(7, Encoding.UTF8.GetBytes("valid-first"));
            store.Save(7, Encoding.UTF8.GetBytes("valid-second"));
            Assert.That(store.Load(7, Read, out var notice), Is.EqualTo("valid-second"));
            Assert.That(notice, Is.Null);
            File.WriteAllText(Path.Combine(directory, "slot-7.json"), "broken");
            Assert.That(store.Load(7, Read, out notice), Is.EqualTo("valid-first"));
            Assert.That(notice, Is.Not.Null.And.Not.Empty);
            CollectionAssert.Contains(store.Slots(), 7);
        }
        [Test] public void FutureVersionDoesNotFallBackOrChangeFiles()
        {
            var store = new MatchSaveStore(directory);
            store.Save(0, Encoding.UTF8.GetBytes("valid-first"));
            store.Save(0, Encoding.UTF8.GetBytes("future"));
            Assert.Throws<NotSupportedException>(() => store.Load(0, Read, out _));
            Assert.That(File.ReadAllText(Path.Combine(directory,"slot-0.json")), Is.EqualTo("future"));
        }
        [Test] public void FailedWriteKeepsPreviouslyUsableSlot()
        {
            var store = new MatchSaveStore(directory);
            store.Save(1, Encoding.UTF8.GetBytes("valid-first"));
            Directory.CreateDirectory(Path.Combine(directory, "slot-1.json.tmp"));
            Assert.Throws<UnauthorizedAccessException>(() => store.Save(1, Encoding.UTF8.GetBytes("valid-next")));
            Assert.That(store.Load(1, Read, out _), Is.EqualTo("valid-first"));
        }
        private static void Delete(MatchSaveStore store, int slot)
        {
            var method = typeof(MatchSaveStore).GetMethod("Delete", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "MatchSaveStore must support deleting a slot, including its backup.");
            try { method.Invoke(store, new object[] { slot }); }
            catch (System.Reflection.TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
        }
        [Test] public void InspectBackupDoesNotRenamePrimaryBeforeDeleteConfirmation()
        {
            var store = new MatchSaveStore(directory);
            store.Save(1, Encoding.UTF8.GetBytes("valid-backup"));
            store.Save(1, Encoding.UTF8.GetBytes("broken"));
            var method = Array.Find(typeof(MatchSaveStore).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                m => m.Name == "Load" && m.GetParameters().Length == 4);
            Assert.That(method, Is.Not.Null, "Listing slots must support a read without quarantining the original file.");
            var arguments = new object[] { 1, (Func<byte[], string>)Read, null, false };
            Assert.That(method.MakeGenericMethod(typeof(string)).Invoke(store, arguments), Is.EqualTo("valid-backup"));
            Assert.That(arguments[2], Is.Not.Null);
            Assert.That(File.ReadAllText(Path.Combine(directory, "slot-1.json")), Is.EqualTo("broken"));
            Assert.That(Directory.GetFiles(directory, "*.corrupt-*").Length, Is.Zero);
        }
        [Test] public void DeleteRemovesPrimaryBackupAndTemporaryWithoutTouchingOtherSlots()
        {
            var store = new MatchSaveStore(directory);
            store.Save(7, Encoding.UTF8.GetBytes("valid-first"));
            store.Save(7, Encoding.UTF8.GetBytes("future"));
            store.Save(8, Encoding.UTF8.GetBytes("valid-other"));
            File.WriteAllText(Path.Combine(directory, "slot-7.json.tmp"), "unfinished");
            Delete(store, 7);
            Assert.That(store.Exists(7), Is.False);
            Assert.That(File.Exists(Path.Combine(directory, "slot-7.json.tmp")), Is.False);
            CollectionAssert.DoesNotContain(store.Slots(), 7);
            Assert.That(store.Load(7, Read, out _), Is.Null);
            Assert.That(store.Load(8, Read, out _), Is.EqualTo("valid-other"));
        }
        [Test] public void DeleteBackupOnlySlotAndRecreateDoesNotRecoverDeletedProgress()
        {
            var store = new MatchSaveStore(directory);
            store.Save(2, Encoding.UTF8.GetBytes("valid-old"));
            store.Save(2, Encoding.UTF8.GetBytes("valid-new"));
            File.Delete(Path.Combine(directory, "slot-2.json"));
            Delete(store, 2);
            Assert.That(store.Exists(2), Is.False);
            store.Save(2, Encoding.UTF8.GetBytes("broken"));
            Assert.Throws<FileNotFoundException>(() => store.Load(2, Read, out _));
        }
        [Test] public void DeleteMissingSlotIsIdempotentAndRejectsNegativeNumber()
        {
            var store = new MatchSaveStore(directory);
            Delete(store, 4);
            Assert.That(Directory.Exists(directory), Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => Delete(store, -1));
        }
        [Test] public void DeleteFailureKeepsPrimaryWhenBackupCannotBeRemoved()
        {
            var store = new MatchSaveStore(directory);
            store.Save(3, Encoding.UTF8.GetBytes("valid-current"));
            Directory.CreateDirectory(Path.Combine(directory, "slot-3.json.bak"));
            Assert.Throws<UnauthorizedAccessException>(() => Delete(store, 3));
            Assert.That(store.Load(3, Read, out _), Is.EqualTo("valid-current"));
        }
        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, int mode);
        [TestCase(false)]
        [TestCase(true)]
        public void DeniedDirectoryIsNotReportedAsEmptyOrSuccessfullyDeleted(bool deleting)
        {
            if (Environment.OSVersion.Platform != PlatformID.Unix) Assert.Ignore("Unix permission fixture.");
            string parent = Path.Combine(directory, "access");
            string child = Path.Combine(parent, "matches");
            var store = new MatchSaveStore(child);
            store.Save(1, Encoding.UTF8.GetBytes("valid-protected"));
            Assert.That(chmod(parent, 0), Is.Zero);
            try
            {
                bool denied = false;
                try { Directory.GetFiles(child); } catch (UnauthorizedAccessException) { denied = true; }
                if (!denied) Assert.Ignore("This process bypasses Unix permission checks.");
                if (deleting) Assert.Throws<UnauthorizedAccessException>(() => Delete(store, 1));
                else Assert.Throws<UnauthorizedAccessException>(() => store.Slots());
            }
            finally { chmod(parent, 448); } // 0700: restore access even when an assertion fails.
            Assert.That(store.Load(1, Read, out _), Is.EqualTo("valid-protected"));
        }
        [Test] public void SlotNumbersAreNotLimitedToTheInitialUiPage()
        {
            var store = new MatchSaveStore(directory);
            store.Save(1234, Encoding.UTF8.GetBytes("valid-large-slot"));
            Assert.That(store.Load(1234, Read, out _), Is.EqualTo("valid-large-slot"));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(-1, new byte[1]));
        }
    }
}
#endif
