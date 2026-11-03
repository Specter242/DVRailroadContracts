using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Contracts.Core.Json
{
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object,
    }

    /// <summary>
    /// A small JSON document model with an ordered object, used for
    /// murphy-ops.json and the save state. The core carries its own so it
    /// needs no JSON library: the runtime would otherwise bind whichever
    /// Newtonsoft.Json the game ships, and the tests a different one.
    /// Objects keep their members in insertion order, so writing is
    /// deterministic and golden files compare as text.
    /// </summary>
    public sealed class JsonValue
    {
        public static readonly JsonValue Null = new JsonValue(JsonKind.Null);

        private readonly bool boolValue;
        private readonly double numberValue;
        private readonly string stringValue;
        private readonly List<JsonValue> items;
        private readonly List<KeyValuePair<string, JsonValue>> members;
        private readonly Dictionary<string, int> index;

        private JsonValue(JsonKind kind)
        {
            Kind = kind;
            if (kind == JsonKind.Array)
                items = new List<JsonValue>();
            else if (kind == JsonKind.Object)
            {
                members = new List<KeyValuePair<string, JsonValue>>();
                index = new Dictionary<string, int>(StringComparer.Ordinal);
            }
        }

        private JsonValue(bool value) : this(JsonKind.Bool) => boolValue = value;

        private JsonValue(double value) : this(JsonKind.Number)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value), "JSON numbers must be finite");
            numberValue = value;
        }

        private JsonValue(string value) : this(JsonKind.String) => stringValue = value ?? throw new ArgumentNullException(nameof(value));

        public JsonKind Kind { get; }

        public static JsonValue Bool(bool value) => new JsonValue(value);

        public static JsonValue Number(double value) => new JsonValue(value);

        public static JsonValue String(string value) => value == null ? Null : new JsonValue(value);

        /// <summary>A number, or null for a missing value.</summary>
        public static JsonValue NumberOrNull(double? value) => value.HasValue ? Number(value.Value) : Null;

        public static JsonValue NewObject() => new JsonValue(JsonKind.Object);

        public static JsonValue NewArray() => new JsonValue(JsonKind.Array);

        public bool IsNull => Kind == JsonKind.Null;

        public IReadOnlyList<JsonValue> Items => items ?? throw Mismatch("an array");

        public IEnumerable<KeyValuePair<string, JsonValue>> Members => members ?? throw Mismatch("an object");

        public int Count => Kind == JsonKind.Array ? items.Count : Kind == JsonKind.Object ? members.Count : throw Mismatch("an array or object");

        /// <summary>The member's value, or null (not <see cref="Null"/>) when absent.</summary>
        public JsonValue this[string key]
        {
            get
            {
                if (members == null)
                    throw Mismatch("an object");
                return index.TryGetValue(key, out var i) ? members[i].Value : null;
            }
        }

        public bool Has(string key) => this[key] != null;

        public bool AsBool() => Kind == JsonKind.Bool ? boolValue : throw Mismatch("a boolean");

        public double AsDouble() => Kind == JsonKind.Number ? numberValue : throw Mismatch("a number");

        public int AsInt()
        {
            var value = AsDouble();
            if (value != Math.Floor(value) || value < int.MinValue || value > int.MaxValue)
                throw new FormatException($"expected an integer, found {value.ToString("R", CultureInfo.InvariantCulture)}");
            return (int)value;
        }

        public string AsString() => Kind == JsonKind.String ? stringValue : throw Mismatch("a string");

        /// <summary>Sets or replaces a member, keeping the first position of a replaced key.</summary>
        public JsonValue Set(string key, JsonValue value)
        {
            if (members == null)
                throw Mismatch("an object");
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            value = value ?? Null;
            if (index.TryGetValue(key, out var i))
                members[i] = new KeyValuePair<string, JsonValue>(key, value);
            else
            {
                index[key] = members.Count;
                members.Add(new KeyValuePair<string, JsonValue>(key, value));
            }
            return this;
        }

        public JsonValue Set(string key, string value) => Set(key, String(value));

        public JsonValue Set(string key, double value) => Set(key, Number(value));

        public JsonValue Set(string key, bool value) => Set(key, Bool(value));

        public JsonValue Add(JsonValue value)
        {
            if (items == null)
                throw Mismatch("an array");
            items.Add(value ?? Null);
            return this;
        }

        private FormatException Mismatch(string expected) => new FormatException($"expected {expected}, found {Kind.ToString().ToLowerInvariant()}");

        // ------------------------------------------------------------ writing

        public override string ToString() => ToJson(indented: false);

        public string ToJson(bool indented)
        {
            var sb = new StringBuilder();
            Write(sb, indented, 0);
            return sb.ToString();
        }

        private void Write(StringBuilder sb, bool indented, int depth)
        {
            switch (Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;
                case JsonKind.Bool:
                    sb.Append(boolValue ? "true" : "false");
                    break;
                case JsonKind.Number:
                    sb.Append(FormatNumber(numberValue));
                    break;
                case JsonKind.String:
                    WriteString(sb, stringValue);
                    break;
                case JsonKind.Array:
                    if (items.Count == 0)
                    {
                        sb.Append("[]");
                        break;
                    }
                    sb.Append('[');
                    for (var i = 0; i < items.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(',');
                        NewLine(sb, indented, depth + 1);
                        items[i].Write(sb, indented, depth + 1);
                    }
                    NewLine(sb, indented, depth);
                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    if (members.Count == 0)
                    {
                        sb.Append("{}");
                        break;
                    }
                    sb.Append('{');
                    for (var i = 0; i < members.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(',');
                        NewLine(sb, indented, depth + 1);
                        WriteString(sb, members[i].Key);
                        sb.Append(indented ? ": " : ":");
                        members[i].Value.Write(sb, indented, depth + 1);
                    }
                    NewLine(sb, indented, depth);
                    sb.Append('}');
                    break;
            }
        }

        private static void NewLine(StringBuilder sb, bool indented, int depth)
        {
            if (!indented)
                return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        /// <summary>
        /// Whole numbers print without a fraction; others print the shortest
        /// form that parses back to the same double.
        /// </summary>
        internal static string FormatNumber(double value)
        {
            if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
                return ((long)value).ToString(CultureInfo.InvariantCulture);
            var text = value.ToString("R", CultureInfo.InvariantCulture);
            if (double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) != value)
                text = value.ToString("G17", CultureInfo.InvariantCulture);
            return text;
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------ parsing

        public static JsonValue Parse(string text)
        {
            if (text == null)
                throw new ArgumentNullException(nameof(text));
            var parser = new Parser(text);
            parser.SkipWhitespace();
            var value = parser.ReadValue(0);
            parser.SkipWhitespace();
            if (!parser.AtEnd)
                throw parser.Error("unexpected text after the JSON value");
            return value;
        }

        private sealed class Parser
        {
            private const int MaxDepth = 128;
            private readonly string text;
            private int pos;

            public Parser(string text) => this.text = text;

            public bool AtEnd => pos >= text.Length;

            public FormatException Error(string message)
            {
                var line = 1;
                var column = 1;
                for (var i = 0; i < pos && i < text.Length; i++)
                {
                    if (text[i] == '\n')
                    {
                        line++;
                        column = 1;
                    }
                    else
                        column++;
                }
                return new FormatException($"JSON line {line}, column {column}: {message}");
            }

            public void SkipWhitespace()
            {
                while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t' || text[pos] == '\n' || text[pos] == '\r'))
                    pos++;
            }

            public JsonValue ReadValue(int depth)
            {
                if (depth > MaxDepth)
                    throw Error("nesting too deep");
                if (AtEnd)
                    throw Error("unexpected end of text");
                var c = text[pos];
                switch (c)
                {
                    case '{':
                        return ReadObject(depth);
                    case '[':
                        return ReadArray(depth);
                    case '"':
                        return new JsonValue(ReadString());
                    case 't':
                        Expect("true");
                        return new JsonValue(true);
                    case 'f':
                        Expect("false");
                        return new JsonValue(false);
                    case 'n':
                        Expect("null");
                        return JsonValue.Null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                            return ReadNumber();
                        throw Error($"unexpected character '{c}'");
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(text, pos, word, 0, word.Length) != 0)
                    throw Error($"expected '{word}'");
                pos += word.Length;
            }

            private JsonValue ReadObject(int depth)
            {
                var value = NewObject();
                pos++;
                SkipWhitespace();
                if (!AtEnd && text[pos] == '}')
                {
                    pos++;
                    return value;
                }
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || text[pos] != '"')
                        throw Error("expected a member name");
                    var key = ReadString();
                    if (value.Has(key))
                        throw Error($"duplicate member '{key}'");
                    SkipWhitespace();
                    if (AtEnd || text[pos] != ':')
                        throw Error("expected ':'");
                    pos++;
                    SkipWhitespace();
                    value.Set(key, ReadValue(depth + 1));
                    SkipWhitespace();
                    if (AtEnd)
                        throw Error("unexpected end of text in an object");
                    if (text[pos] == ',')
                    {
                        pos++;
                        continue;
                    }
                    if (text[pos] == '}')
                    {
                        pos++;
                        return value;
                    }
                    throw Error("expected ',' or '}'");
                }
            }

            private JsonValue ReadArray(int depth)
            {
                var value = NewArray();
                pos++;
                SkipWhitespace();
                if (!AtEnd && text[pos] == ']')
                {
                    pos++;
                    return value;
                }
                while (true)
                {
                    SkipWhitespace();
                    value.Add(ReadValue(depth + 1));
                    SkipWhitespace();
                    if (AtEnd)
                        throw Error("unexpected end of text in an array");
                    if (text[pos] == ',')
                    {
                        pos++;
                        continue;
                    }
                    if (text[pos] == ']')
                    {
                        pos++;
                        return value;
                    }
                    throw Error("expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                pos++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                        throw Error("unterminated string");
                    var c = text[pos++];
                    if (c == '"')
                        return sb.ToString();
                    if (c < 0x20)
                        throw Error("control character in a string");
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (AtEnd)
                        throw Error("unterminated escape");
                    var e = text[pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (pos + 4 > text.Length || !int.TryParse(text.Substring(pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                                throw Error("bad \\u escape");
                            pos += 4;
                            sb.Append((char)code);
                            break;
                        default:
                            throw Error($"bad escape '\\{e}'");
                    }
                }
            }

            private JsonValue ReadNumber()
            {
                var start = pos;
                if (text[pos] == '-')
                    pos++;
                if (AtEnd || !char.IsDigit(text[pos]))
                    throw Error("bad number");
                if (text[pos] == '0')
                    pos++;
                else
                    while (!AtEnd && char.IsDigit(text[pos]))
                        pos++;
                if (!AtEnd && text[pos] == '.')
                {
                    pos++;
                    if (AtEnd || !char.IsDigit(text[pos]))
                        throw Error("bad number fraction");
                    while (!AtEnd && char.IsDigit(text[pos]))
                        pos++;
                }
                if (!AtEnd && (text[pos] == 'e' || text[pos] == 'E'))
                {
                    pos++;
                    if (!AtEnd && (text[pos] == '+' || text[pos] == '-'))
                        pos++;
                    if (AtEnd || !char.IsDigit(text[pos]))
                        throw Error("bad number exponent");
                    while (!AtEnd && char.IsDigit(text[pos]))
                        pos++;
                }
                var number = double.Parse(text.Substring(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
                if (double.IsInfinity(number))
                    throw Error("number out of range");
                return new JsonValue(number);
            }
        }
    }
}
