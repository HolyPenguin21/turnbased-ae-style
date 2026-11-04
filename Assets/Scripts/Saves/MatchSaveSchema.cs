using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Game.Saves
{
    // Identity of the save FORMAT, not of the game content (project owner, 2026-11-04): balance
    // values and art may change between builds without invalidating a save, while any change of
    // the file structure must. The hash covers every DTO type reachable from the file roots
    // through the same field rule MatchSaveJson serializes with: field names and types, plus
    // every enum member name and number (enums are written as numbers, so renumbering or
    // removing a member changes the meaning of old files). Nothing here is maintained by hand.
    internal static class MatchSaveSchema
    {
        private static string hash;
        internal static string Hash => hash ??= Compute(typeof(MatchSaveEnvelope), typeof(MatchSaveData));

        internal static string Compute(params Type[] roots)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(Describe(roots)));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        // One line per reachable DTO or enum type, ordered by type name.
        internal static string Describe(params Type[] roots)
        {
            var described = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var pending = new Queue<Type>(roots);
            while (pending.Count > 0)
            {
                Type type = pending.Dequeue();
                if (type == typeof(string) || type == typeof(bool) || type.IsPrimitive || type == typeof(decimal)) continue;
                Type item = MatchSaveJson.ItemType(type);
                if (item != null) { pending.Enqueue(item); continue; }
                string key = TypeName(type);
                if (described.ContainsKey(key)) continue;
                if (type.IsEnum) { described.Add(key, DescribeEnum(type)); continue; }
                var fields = MatchSaveJson.Fields(type);
                Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
                var text = new StringBuilder("type ").Append(key).Append('{');
                foreach (var field in fields)
                {
                    text.Append(field.Name).Append(':').Append(TypeName(field.FieldType)).Append(';');
                    pending.Enqueue(field.FieldType);
                }
                described.Add(key, text.Append('}').ToString());
            }
            return string.Join("\n", described.Values);
        }

        private static string DescribeEnum(Type type)
        {
            var text = new StringBuilder("enum ").Append(type.FullName).Append(':')
                .Append(Enum.GetUnderlyingType(type).Name);
            if (type.IsDefined(typeof(FlagsAttribute), false)) text.Append(":flags");
            var names = Enum.GetNames(type);
            Array.Sort(names, StringComparer.Ordinal);
            foreach (string name in names)
                text.Append(';').Append(name).Append('=')
                    .Append(Convert.ToInt64(Enum.Parse(type, name), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            return text.ToString();
        }

        // Generic FullName embeds assembly versions, which differ between editor and player.
        private static string TypeName(Type type)
        {
            Type item = MatchSaveJson.ItemType(type);
            if (item != null) return "list<" + TypeName(item) + ">";
            if (!type.IsGenericType) return type.FullName;
            var arguments = Array.ConvertAll(type.GetGenericArguments(), TypeName);
            return type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", arguments) + ">";
        }
    }
}
