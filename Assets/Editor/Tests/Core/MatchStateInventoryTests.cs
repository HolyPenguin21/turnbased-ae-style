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
        private static bool IsConstantTable(FieldInfo f)
        {
            if (!f.IsInitOnly) return false;
            Type t = f.FieldType;
            if (t.IsPrimitive || t.IsEnum || ImmutableElements.Contains(t)) return true;
            if (t.IsValueType) return true; // readonly struct copy: callers cannot mutate the field
            if (t.IsArray)
            {
                Type e = t.GetElementType();
                return e.IsPrimitive || e.IsEnum || ImmutableElements.Contains(e) || e.IsValueType
                    || (e.IsArray && (e.GetElementType() == typeof(string)));
            }
            if (t.IsGenericType)
            {
                Type g = t.GetGenericTypeDefinition();
                if (g == typeof(IReadOnlyDictionary<,>) || g == typeof(IReadOnlyList<>)
                    || g == typeof(IReadOnlyCollection<>))
                    return true;
            }
            // Immutable reference values (comparers, sentinel instances) declare no mutable fields.
            return !typeof(IEnumerable).IsAssignableFrom(t)
                && !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ConditionalWeakTable<,>))
                && t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .All(x => x.IsInitOnly);
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
                if (type.IsGenericTypeDefinition) continue;
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

        // World, AI memory and turn-scoped holders must start every match empty: either the
        // inventory clears them or the entry names the reset that already does.
        [Test]
        public void PerMatchStateIsResetOrNamesItsReset()
        {
            var missing = MatchStateInventory.All
                .Where(e => e.RequiresMatchReset && e.Reset == null && string.IsNullOrWhiteSpace(e.ResetNote))
                .Select(e => e.TypeName).ToList();
            Assert.That(missing, Is.Empty);
        }
    }
}
#endif
