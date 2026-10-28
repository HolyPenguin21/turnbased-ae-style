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
        [Serializable] public sealed class Node { public long id; public string name; public Node next; }
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
