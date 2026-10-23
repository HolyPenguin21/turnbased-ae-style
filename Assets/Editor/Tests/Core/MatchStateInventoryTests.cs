#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.Setup;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Every static field that can hold state between calls must be classified in
    // MatchStateInventory, so a future save/load knows whether to write it, rebuild it or
    // ignore it. A new static registry fails here until its owner decides.
    public sealed class MatchStateInventoryTests
    {
        private static readonly Type[] ImmutableElements =
            { typeof(string), typeof(Type), typeof(System.Text.RegularExpressions.Regex) };

        // A readonly field whose value cannot change after type initialization holds no state.
        private static bool IsConstantTable(FieldInfo f) => f.IsInitOnly && IsImmutable(f.FieldType, 0);

        // Conventions: arrays of immutable elements and IReadOnly* views are constant tables (the
        // project never writes into them); a delegate is a constant function. A class is immutable
        // only when every instance field is readonly and itself immutable; a struct reached through
        // a readonly field cannot be reassigned, but its reference members still can be mutated.
        private static bool IsImmutable(Type t, int depth)
        {
            if (t.IsPrimitive || t.IsEnum || ImmutableElements.Contains(t) || typeof(Delegate).IsAssignableFrom(t))
                return true;
            if (t.IsArray)
                return IsImmutable(t.GetElementType(), depth + 1);
            if (t.IsGenericType)
            {
                Type g = t.GetGenericTypeDefinition();
                if (g == typeof(IReadOnlyDictionary<,>) || g == typeof(IReadOnlyList<>)
                    || g == typeof(IReadOnlyCollection<>))
                    return true;
                if (g == typeof(Nullable<>))
                    return IsImmutable(t.GetGenericArguments()[0], depth + 1);
                if (g == typeof(ConditionalWeakTable<,>))
                    return false;
            }
            if (typeof(IEnumerable).IsAssignableFrom(t))
                return false;
            if (t.IsInterface || t.IsAbstract)
                return true; // comparers and strategies typed by their contract
            if (depth > 4)
                return false;
            return t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .All(x => (t.IsValueType || x.IsInitOnly) && IsImmutable(x.FieldType, depth + 1));
        }

        internal static List<(Type Owner, FieldInfo Field)> StatefulStatics()
        {
            Assembly game = typeof(MatchStateInventory).Assembly;
            var result = new List<(Type, FieldInfo)>();
            foreach (Type type in game.GetTypes())
            {
                if (type.Namespace == null || !type.Namespace.StartsWith("Game", StringComparison.Ordinal)
                    || type.Namespace.StartsWith("Game.EditorTests", StringComparison.Ordinal)) continue;
                if (type.IsDefined(typeof(CompilerGeneratedAttribute), false)) continue;
                foreach (FieldInfo f in type.GetFields(BindingFlags.Static | BindingFlags.Public
                             | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsLiteral) continue;
                    if (typeof(Delegate).IsAssignableFrom(f.FieldType)
                        && type.GetEvent(f.Name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null)
                        continue; // event subscriptions are wiring, re-established by their subscribers
                    if (IsConstantTable(f)) continue;
                    result.Add((type, f));
                }
            }
            return result;
        }

        private static string Name(Type t) => t.FullName.Replace('+', '.');

        [Test]
        public void EveryStatefulStaticTypeIsClassified()
        {
            var unclassified = StatefulStatics().Select(x => x.Owner).Distinct()
                .Where(t => !MatchStateInventory.ByType.ContainsKey(Name(t)))
                .Select(t => Name(t) + " [" + string.Join(", ", StatefulStatics()
                    .Where(x => x.Owner == t).Select(x => x.Field.Name)) + "]")
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.That(unclassified, Is.Empty,
                "classify these in MatchStateInventory:\n" + string.Join("\n", unclassified));
        }

        [Test]
        public void EveryClassifiedTypeStillHoldsStaticState()
        {
            var stateful = new HashSet<string>(StatefulStatics().Select(x => Name(x.Owner)));
            var stale = MatchStateInventory.ByType.Keys.Where(k => !stateful.Contains(k))
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.That(stale, Is.Empty, "remove or rename these inventory entries:\n" + string.Join("\n", stale));
        }

        [Test]
        public void EveryTypeIsListedOnce() =>
            Assert.That(MatchStateInventory.All.Select(e => e.TypeName), Is.Unique);

        [Test]
        public void NewGameBootstrapResetsOldWorldBeforeDefaultOrderMapVisuals()
        {
            var bootstrapType = typeof(CitadelSetupController);
            var order = (UnityEngine.DefaultExecutionOrder)Attribute.GetCustomAttribute(
                bootstrapType, typeof(UnityEngine.DefaultExecutionOrder));
            var generatorOrder = (UnityEngine.DefaultExecutionOrder)Attribute.GetCustomAttribute(
                typeof(Game.Map.HexMapGenerator), typeof(UnityEngine.DefaultExecutionOrder));
            Assert.That(order, Is.Not.Null);
            Assert.That(order.order, Is.LessThan(generatorOrder?.order ?? 0));
            var awake = bootstrapType.GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(awake, Is.Not.Null, "Start runs after the map has already drawn its fog.");

            var players = Game.Core.GameSession.Players;
            var mapSize = Game.Core.GameSession.SelectedMapSize;
            var biome = Game.Core.GameSession.SelectedBiome;
            var campaign = Game.Core.GameSession.CampaignContext;
            var matchId = Game.Core.GameSession.MatchId;
            var root = new UnityEngine.GameObject("new match bootstrap");
            root.SetActive(false);
            try
            {
                var bootstrap = root.AddComponent<CitadelSetupController>();
                var player = new Game.Players.PlayerSetupData { IsHuman = true };
                var hex = new Game.HexGrid.HexCoord(1, 1);
                Game.Map.VisionSystem.Configure(null);
                var building = new Game.Map.BuildingData { Owner = player, Hex = hex, IsBase = true };
                Game.Map.BuildingRegistry.Register(hex, building);
                Game.Map.VisionSystem.CurrentViewer = player;
                Game.Map.HumanVisualMemory.ObserverMatch = true;
                Assert.That(Game.Map.VisionSystem.IsVisible(player, hex), Is.True);
                var preparedPlayers = new List<Game.Players.PlayerSetupData> { player };
                Game.Core.GameSession.Players = preparedPlayers;

                awake.Invoke(bootstrap, null);

                Assert.That(Game.Map.VisionSystem.CurrentViewer, Is.Null);
                Assert.That(Game.Map.VisionSystem.IsVisible(player, hex), Is.False);
                Assert.That(Game.Map.VisionSystem.HasEverSeen(player, hex), Is.False);
                Assert.That(Game.Map.VisionSystem.IsVisibleToCurrentViewer(hex), Is.True,
                    "Citadel placement starts without a previous match's fog.");
                Assert.That(Game.Map.BuildingRegistry.FindAt(hex), Is.Null);
                Assert.That(Game.Map.HumanVisualMemory.ObserverMatch, Is.False);
                Assert.That(Game.Core.GameSession.Players, Is.SameAs(preparedPlayers));
                Assert.That(Game.Core.GameSession.SelectedMapSize, Is.EqualTo(mapSize));
                Assert.That(Game.Core.GameSession.SelectedBiome, Is.EqualTo(biome));
                Assert.That(Game.Core.GameSession.CampaignContext, Is.SameAs(campaign));
                Assert.That(Game.Core.GameSession.MatchId, Is.EqualTo(matchId));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                MatchStateInventory.ResetForNewMatch();
                Game.Core.GameSession.Players = players;
            }
        }

        // World, AI memory and turn-scoped holders must start every match empty: either the
        // inventory clears them or the entry names the reset that already does.
        [Test]
        public void PerMatchStateIsResetOrNamesItsReset()
        {
            var missing = MatchStateInventory.All
                .Where(e => e.RequiresMatchReset && e.Reset == null && e.ResetBy == null
                    && string.IsNullOrWhiteSpace(e.ResetNote))
                .Select(e => e.TypeName).ToList();
            Assert.That(missing, Is.Empty);
        }

        [Test]
        public void ResetByNamesAnEntryThatResets()
        {
            var broken = MatchStateInventory.All
                .Where(e => e.ResetBy != null && (!MatchStateInventory.ByType.TryGetValue(e.ResetBy, out var by)
                    || by.Reset == null))
                .Select(e => e.TypeName + " -> " + e.ResetBy).ToList();
            Assert.That(broken, Is.Empty);
        }

        // A chained reset is a claim about another class's Clear. Put one dummy row into every
        // static collection of the chained holder, run the named reset, and require them empty.
        [Test]
        public void ChainedResetsReallyEmptyTheirHolders()
        {
            Assembly game = typeof(MatchStateInventory).Assembly;
            var notEmptied = new List<string>();
            int checkedCollections = 0;
            foreach (MatchStateEntry entry in MatchStateInventory.All.Where(e => e.ResetBy != null
                         && e.Kind != MatchStateKind.IdentitySequence))
            {
                Type type = game.GetType(ToClrName(game, entry.TypeName), true);
                var filled = new List<FieldInfo>();
                foreach (FieldInfo f in type.GetFields(BindingFlags.Static | BindingFlags.Public
                             | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (TryAddDummy(f.GetValue(null)))
                        filled.Add(f);
                checkedCollections += filled.Count;
                MatchStateInventory.ByType[entry.ResetBy].Reset();
                foreach (FieldInfo f in filled)
                    if (((ICollection)f.GetValue(null)).Count != 0)
                        notEmptied.Add($"{entry.TypeName}.{f.Name} (reset by {entry.ResetBy})");
            }
            Assert.That(checkedCollections, Is.GreaterThanOrEqualTo(10), "the check must not pass vacuously");
            Assert.That(notEmptied, Is.Empty);
        }

        private static string ToClrName(Assembly game, string name)
        {
            // Nested types are listed with '.', the CLR spells them with '+'.
            while (game.GetType(name) == null && name.Contains('.'))
            {
                int i = name.LastIndexOf('.');
                string candidate = name.Substring(0, i) + "+" + name.Substring(i + 1);
                if (game.GetType(candidate) != null) return candidate;
                break;
            }
            return name;
        }

        private static bool TryAddDummy(object collection)
        {
            if (!(collection is ICollection)) return false;
            Type t = collection.GetType();
            Type[] args = t.IsGenericType ? t.GetGenericArguments() : Type.EmptyTypes;
            if (args.Any(a => typeof(IDisposable).IsAssignableFrom(a))) return false; // needs a real lifecycle
            if (collection is IDictionary dictionary && args.Length == 2)
            {
                dictionary[Dummy(args[0])] = Dummy(args[1]);
                return true;
            }
            MethodInfo add = t.GetMethod("Add", args);
            if (args.Length != 1 || add == null) return false;
            add.Invoke(collection, new[] { Dummy(args[0]) });
            return true;
        }

        private static object Dummy(Type t)
        {
            if (t == typeof(string)) return "dummy";
            if (t.IsValueType || t.GetConstructor(Type.EmptyTypes) != null) return Activator.CreateInstance(t);
            return System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t);
        }
    }
}
#endif
