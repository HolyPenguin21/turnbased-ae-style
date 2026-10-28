using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Game.Saves
{
    // Field-only JSON transport for the concrete detached DTO schema. It has no Unity inline
    // depth/null semantics. Types come exclusively from the caller's known schema, never the
    // file; mission/entity model conversion remains in the explicit concrete codecs.
    internal static class MatchSaveJson
    {
        private sealed class Number { internal string Text; internal double Value; }
        internal static void Validate(string json, Type dtoType) => Check(new Parser(json).Read(), dtoType, "$", 0);
        internal static string Serialize<T>(T value)
        {
            var result = new StringBuilder();
            Write(value, typeof(T), result, new HashSet<object>(), 0);
            if (result.Length > MatchSaveStore.MaximumFileBytes) throw Invalid("Output exceeds the size limit.");
            string json = result.ToString();
            Validate(json, typeof(T));
            return json;
        }
        internal static T Deserialize<T>(string json)
        {
            var value = new Parser(json).Read();
            Check(value, typeof(T), "$", 0);
            return (T)Build(value, typeof(T));
        }
        private static FieldInfo[] Fields(Type type)
        {
            if (type == typeof(object) || type.IsInterface || type.IsAbstract || typeof(Delegate).IsAssignableFrom(type)
                || typeof(IEnumerable).IsAssignableFrom(type)) throw Invalid("Unsupported DTO type: " + type.FullName);
            return Array.FindAll(type.GetFields(BindingFlags.Instance | BindingFlags.Public),
                f => !f.IsDefined(typeof(NonSerializedAttribute), false));
        }
        private static Type ItemType(Type type) => type.IsArray ? type.GetElementType()
            : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0] : null;
        private static object Build(object value, Type type)
        {
            if (value == null) return null;
            if (type == typeof(string) || type == typeof(bool)) return value;
            if (type.IsEnum) return Enum.ToObject(type, Convert.ChangeType(((Number)value).Text, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture));
            if (type.IsPrimitive || type == typeof(decimal)) return Convert.ChangeType(((Number)value).Text, type, CultureInfo.InvariantCulture);
            Type item = ItemType(type);
            if (item != null)
            {
                var values = (List<object>)value;
                if (type.IsArray)
                {
                    var result = Array.CreateInstance(item, values.Count);
                    for (int i = 0; i < values.Count; i++) result.SetValue(Build(values[i], item), i);
                    return result;
                }
                var list = (IList)Activator.CreateInstance(type);
                foreach (object element in values) list.Add(Build(element, item));
                return list;
            }
            var instance = Activator.CreateInstance(type, true);
            var fields = (Dictionary<string, object>)value;
            foreach (var field in Fields(type)) field.SetValue(instance, Build(fields[field.Name], field.FieldType));
            return instance;
        }
        private static void Write(object value, Type type, StringBuilder output, HashSet<object> parents, int depth)
        {
            if (depth > 100 || output.Length > MatchSaveStore.MaximumFileBytes) throw Invalid("Output is too complex.");
            if (Nullable.GetUnderlyingType(type) != null) throw Invalid("Nullable is not a file DTO.");
            if (value == null) { output.Append("null"); return; }
            if (type == typeof(string)) { Quote((string)value, output); return; }
            if (type == typeof(bool)) { output.Append((bool)value ? "true" : "false"); return; }
            if (type.IsEnum) { output.Append(Convert.ToString(Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)); return; }
            if (type.IsPrimitive || type == typeof(decimal))
            {
                if (value is float single)
                {
                    if (float.IsNaN(single) || float.IsInfinity(single)) throw Invalid("Nonfinite float.");
                    output.Append(single.ToString("R", CultureInfo.InvariantCulture));
                }
                else if (value is double number)
                {
                    if (double.IsNaN(number) || double.IsInfinity(number)) throw Invalid("Nonfinite double.");
                    output.Append(number.ToString("R", CultureInfo.InvariantCulture));
                }
                else output.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            if (!type.IsValueType && !parents.Add(value)) throw Invalid("A DTO graph contains a cycle.");
            try
            {
                Type item = ItemType(type);
                bool first = true;
                if (item != null)
                {
                    output.Append('[');
                    foreach (object element in (IEnumerable)value)
                    { if (!first) output.Append(','); first = false; Write(element, item, output, parents, depth + 1); }
                    output.Append(']'); return;
                }
                output.Append('{');
                foreach (var field in Fields(type))
                {
                    if (field.IsInitOnly) throw Invalid("Readonly is not a file DTO.");
                    if (!first) output.Append(','); first = false;
                    Quote(field.Name, output); output.Append(':'); Write(field.GetValue(value), field.FieldType, output, parents, depth + 1);
                }
                output.Append('}');
            }
            finally { if (!type.IsValueType) parents.Remove(value); }
        }
        private static void Quote(string value, StringBuilder output)
        {
            output.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': output.Append("\\\""); break; case '\\': output.Append("\\\\"); break;
                    case '\b': output.Append("\\b"); break; case '\f': output.Append("\\f"); break;
                    case '\n': output.Append("\\n"); break; case '\r': output.Append("\\r"); break; case '\t': output.Append("\\t"); break;
                    default: if (c < 32) output.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture)); else output.Append(c); break;
                }
            }
            output.Append('"');
        }
        internal static int Version(string json)
        {
            var root = new Parser(json).Read() as Dictionary<string, object>;
            if (root == null || !root.TryGetValue("version", out var value)) throw Invalid("Missing file version.");
            Check(value, typeof(int), "version", 0);
            return int.Parse(((Number)value).Text, CultureInfo.InvariantCulture);
        }
        private static ArgumentException Invalid(string message) => new ArgumentException("Invalid saved match JSON: " + message);
        private static void Check(object value, Type type, string path, int depth)
        {
            if (depth > 100) throw Invalid("DTO nesting is too deep.");
            if (Nullable.GetUnderlyingType(type) != null) throw Invalid("Nullable is not a JSON DTO: " + path);
            if (value == null)
            {
                if (type.IsValueType) throw Invalid("Null value at " + path);
                return;
            }
            if (type == typeof(string)) { if (!(value is string)) throw Invalid("Expected text at " + path); return; }
            if (type == typeof(bool)) { if (!(value is bool)) throw Invalid("Expected boolean at " + path); return; }
            if (type.IsEnum)
            {
                Check(value, Enum.GetUnderlyingType(type), path, depth + 1);
                long raw = long.Parse(((Number)value).Text, CultureInfo.InvariantCulture);
                if (type.IsDefined(typeof(FlagsAttribute), false))
                {
                    long mask = 0; foreach (object flag in Enum.GetValues(type)) mask |= Convert.ToInt64(flag);
                    if ((raw & ~mask) != 0) throw Invalid("Unknown enum flags at " + path);
                }
                else if (!Enum.IsDefined(type, Enum.ToObject(type, raw))) throw Invalid("Unknown enum value at " + path);
                return;
            }
            if (type.IsPrimitive || type == typeof(decimal))
            {
                if (!(value is Number n)) throw Invalid("Expected number at " + path);
                if (type == typeof(float))
                {
                    // A shortest round-trip decimal for Single.MaxValue rounds slightly
                    // above its exact double value. Validate in the destination precision.
                    if (!float.TryParse(n.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float single)
                        || float.IsNaN(single) || float.IsInfinity(single)) throw Invalid("Nonfinite float at " + path);
                    return;
                }
                if (type == typeof(double)) return;
                if (n.Text.IndexOfAny(new[] {'.','e','E'}) >= 0) throw Invalid("Expected integer at " + path);
                try { Convert.ChangeType(n.Text, type, CultureInfo.InvariantCulture); }
                catch (Exception ex) when (ex is FormatException || ex is OverflowException || ex is InvalidCastException)
                { throw Invalid("Integer outside its range at " + path); }
                return;
            }
            if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
            {
                if (!(value is List<object> items)) throw Invalid("Expected array at " + path);
                Type item = type.IsArray ? type.GetElementType() : type.GetGenericArguments()[0];
                for (int i = 0; i < items.Count; i++) Check(items[i], item, path + "[" + i + "]", depth + 1);
                return;
            }
            if (!(value is Dictionary<string, object> obj)) throw Invalid("Expected object at " + path);
            var fields = Fields(type);
            int count = 0;
            foreach (var field in fields)
            {
                if (field.IsDefined(typeof(NonSerializedAttribute), false)) continue;
                if (field.IsInitOnly) throw Invalid("Readonly is not a JSON DTO: " + path + "." + field.Name);
                count++;
                if (!obj.TryGetValue(field.Name, out var child)) throw Invalid("Missing " + path + "." + field.Name);
                Check(child, field.FieldType, path + "." + field.Name, depth + 1);
            }
            if (obj.Count != count) throw Invalid("Unknown fields at " + path);
        }

        private sealed class Parser
        {
            private readonly string json;
            private int position, nodes;
            internal Parser(string json)
            {
                if (string.IsNullOrEmpty(json) || json.Length > MatchSaveStore.MaximumFileBytes) throw Invalid("Invalid text size.");
                this.json = json;
            }
            internal object Read()
            {
                object value = Value(0); White();
                if (position != json.Length) throw Invalid("Trailing data.");
                return value;
            }
            private void White() { while (position < json.Length && (json[position] == ' ' || json[position] == '\t' || json[position] == '\r' || json[position] == '\n')) position++; }
            private bool Take(char c) { White(); if (position < json.Length && json[position] == c) { position++; return true; } return false; }
            private void Need(char c) { if (!Take(c)) throw Invalid("Expected '" + c + "' at " + position); }
            private object Value(int depth)
            {
                if (depth > 100 || ++nodes > 2000000) throw Invalid("Input is too complex.");
                White(); if (position >= json.Length) throw Invalid("Unexpected end.");
                char c = json[position];
                if (c == '"') return String();
                if (c == '{')
                {
                    position++; var obj = new Dictionary<string, object>(StringComparer.Ordinal);
                    if (Take('}')) return obj;
                    do
                    {
                        White(); if (position >= json.Length || json[position] != '"') throw Invalid("Expected field name.");
                        string key = String(); Need(':');
                        if (obj.ContainsKey(key)) throw Invalid("Duplicate field: " + key);
                        obj.Add(key, Value(depth + 1));
                    } while (Take(','));
                    Need('}'); return obj;
                }
                if (c == '[')
                {
                    position++; var list = new List<object>();
                    if (Take(']')) return list;
                    do { list.Add(Value(depth + 1)); } while (Take(','));
                    Need(']'); return list;
                }
                if (c == 't') { Literal("true"); return true; }
                if (c == 'f') { Literal("false"); return false; }
                if (c == 'n') { Literal("null"); return null; }
                return Numeric();
            }
            private void Literal(string value)
            {
                if (position + value.Length > json.Length || string.CompareOrdinal(json, position, value, 0, value.Length) != 0)
                    throw Invalid("Invalid literal.");
                position += value.Length;
            }
            private string String()
            {
                Need('"'); var result = new StringBuilder();
                while (position < json.Length)
                {
                    char c = json[position++];
                    if (c == '"') return result.ToString();
                    if (c < 0x20) throw Invalid("Control character in string.");
                    if (c != '\\') { result.Append(c); continue; }
                    if (position >= json.Length) throw Invalid("Incomplete escape.");
                    c = json[position++];
                    switch (c)
                    {
                        case '"': case '\\': case '/': result.Append(c); break;
                        case 'b': result.Append('\b'); break; case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break; case 'r': result.Append('\r'); break; case 't': result.Append('\t'); break;
                        case 'u':
                            if (position + 4 > json.Length || !ushort.TryParse(json.Substring(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                throw Invalid("Invalid unicode escape.");
                            result.Append((char)code); position += 4; break;
                        default: throw Invalid("Unknown escape.");
                    }
                }
                throw Invalid("Unclosed string.");
            }
            private object Numeric()
            {
                int start = position;
                if (position < json.Length && json[position] == '-') position++;
                if (position >= json.Length) throw Invalid("Incomplete number.");
                if (json[position] == '0') position++;
                else
                {
                    if (json[position] < '1' || json[position] > '9') throw Invalid("Invalid number.");
                    Digits();
                }
                if (position < json.Length && json[position] == '.') { position++; RequiredDigits(); }
                if (position < json.Length && (json[position] == 'e' || json[position] == 'E'))
                {
                    position++; if (position < json.Length && (json[position] == '+' || json[position] == '-')) position++;
                    RequiredDigits();
                }
                string text = json.Substring(start, position - start);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || double.IsNaN(number) || double.IsInfinity(number))
                    throw Invalid("Number must be finite.");
                return new Number { Text = text, Value = number };
            }
            private void Digits() { while (position < json.Length && json[position] >= '0' && json[position] <= '9') position++; }
            private void RequiredDigits() { int start = position; Digits(); if (start == position) throw Invalid("Missing number digits."); }
        }
    }
}
