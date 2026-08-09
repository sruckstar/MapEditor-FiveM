using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MapEditor.Platform
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
    /// A small JSON reader/writer.
    ///
    /// The plan called for Newtonsoft.Json, which the FiveM runtime does ship — but the resource would be
    /// compiled against one version and run against whatever the runtime happens to carry, and a mismatch
    /// surfaces as a MissingMethodException at load time on somebody else's server. The documents this
    /// port reads and writes are its own (categories.json and the native map format), so a self-contained
    /// implementation removes the question entirely.
    ///
    /// Deliberately not general-purpose: no attribute-driven object mapping, no streaming. Callers walk
    /// the tree by hand, exactly as MapSerializer already does for XML.
    /// </summary>
    public sealed class Json
    {
        private readonly JsonKind _kind;
        private readonly bool _bool;
        private readonly double _number;
        private readonly string _string;
        private readonly List<Json> _array;
        // Insertion-ordered so that a document written back out keeps the shape it was written in;
        // _index only exists to keep member lookup off a linear scan.
        private readonly List<KeyValuePair<string, Json>> _members;
        private readonly Dictionary<string, int> _index;

        public static readonly Json Null = new Json(JsonKind.Null);

        private Json(JsonKind kind)
        {
            _kind = kind;
            if (kind == JsonKind.Array) _array = new List<Json>();
            if (kind == JsonKind.Object)
            {
                _members = new List<KeyValuePair<string, Json>>();
                _index = new Dictionary<string, int>(StringComparer.Ordinal);
            }
        }

        private Json(bool value) : this(JsonKind.Bool) { _bool = value; }
        private Json(double value) : this(JsonKind.Number) { _number = value; }
        private Json(string value) : this(JsonKind.String) { _string = value; }

        public JsonKind Kind { get { return _kind; } }
        public bool IsNull { get { return _kind == JsonKind.Null; } }

        // --- Construction ---------------------------------------------------------------------------

        public static Json Object() { return new Json(JsonKind.Object); }
        public static Json Array() { return new Json(JsonKind.Array); }
        public static Json Of(string value) { return value == null ? Null : new Json(value); }
        public static Json Of(bool value) { return new Json(value); }
        public static Json Of(double value) { return new Json(value); }
        public static Json Of(int value) { return new Json((double)value); }

        /// <summary>
        /// A float, written as short as it can be and still come back the same float.
        ///
        /// Widening to double first would be exact and useless: 0.1f is 0.10000000149011612 as a double,
        /// and "R" would write all of it. A map is tens of thousands of coordinates and it goes into a KVP
        /// entry, so the round trip goes through the float's own shortest form instead — which parses back
        /// to a double that narrows to exactly the float it came from.
        /// </summary>
        public static Json Of(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return new Json(0d);
            return new Json(double.Parse(value.ToString("R", CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        /// <summary>Appends to an array. Returns this, so calls chain.</summary>
        public Json Add(Json value)
        {
            if (_kind != JsonKind.Array) throw new InvalidOperationException("Add(value) needs an array.");
            _array.Add(value ?? Null);
            return this;
        }

        /// <summary>Sets a member on an object, replacing any previous value under the same name.</summary>
        public Json Set(string name, Json value)
        {
            if (_kind != JsonKind.Object) throw new InvalidOperationException("Set() needs an object.");
            var entry = new KeyValuePair<string, Json>(name, value ?? Null);
            int at;
            if (_index.TryGetValue(name, out at)) _members[at] = entry;
            else
            {
                _index[name] = _members.Count;
                _members.Add(entry);
            }
            return this;
        }

        public Json Set(string name, string value) { return Set(name, Of(value)); }
        public Json Set(string name, bool value) { return Set(name, Of(value)); }
        public Json Set(string name, int value) { return Set(name, Of(value)); }
        public Json Set(string name, float value) { return Set(name, Of(value)); }

        /// <summary>Skips the member entirely when <paramref name="condition"/> is false, the way the SP
        /// build's ShouldSerializeXxx methods kept type-irrelevant fields out of the XML.</summary>
        public Json SetIf(bool condition, string name, Json value)
        {
            return condition ? Set(name, value) : this;
        }

        // --- Reading --------------------------------------------------------------------------------

        /// <summary>Missing members read as <see cref="Null"/> rather than throwing, so a map written by
        /// an older version of the editor still loads.</summary>
        public Json this[string name]
        {
            get
            {
                int at;
                if (_kind == JsonKind.Object && _index.TryGetValue(name, out at)) return _members[at].Value;
                return Null;
            }
        }

        public Json this[int index]
        {
            get
            {
                if (_kind == JsonKind.Array && index >= 0 && index < _array.Count) return _array[index];
                return Null;
            }
        }

        public int Count
        {
            get
            {
                if (_kind == JsonKind.Array) return _array.Count;
                if (_kind == JsonKind.Object) return _members.Count;
                return 0;
            }
        }

        public IEnumerable<Json> Items
        {
            get { return _kind == JsonKind.Array ? (IEnumerable<Json>)_array : new Json[0]; }
        }

        public IEnumerable<KeyValuePair<string, Json>> Members
        {
            get
            {
                return _kind == JsonKind.Object
                    ? (IEnumerable<KeyValuePair<string, Json>>)_members
                    : new KeyValuePair<string, Json>[0];
            }
        }

        public bool Has(string name)
        {
            return _kind == JsonKind.Object && _index.ContainsKey(name);
        }

        public string AsString(string fallback)
        {
            return _kind == JsonKind.String ? _string : fallback;
        }

        public double AsDouble(double fallback)
        {
            return _kind == JsonKind.Number ? _number : fallback;
        }

        public float AsFloat(float fallback)
        {
            return _kind == JsonKind.Number ? (float)_number : fallback;
        }

        public int AsInt(int fallback)
        {
            return _kind == JsonKind.Number ? (int)Math.Round(_number) : fallback;
        }

        public bool AsBool(bool fallback)
        {
            return _kind == JsonKind.Bool ? _bool : fallback;
        }

        // --- Writing --------------------------------------------------------------------------------

        public string ToJson()
        {
            var sb = new StringBuilder();
            Write(sb);
            return sb.ToString();
        }

        private void Write(StringBuilder sb)
        {
            switch (_kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;
                case JsonKind.Bool:
                    sb.Append(_bool ? "true" : "false");
                    break;
                case JsonKind.Number:
                    if (double.IsNaN(_number) || double.IsInfinity(_number)) sb.Append('0');
                    // "R" round-trips: a prop nudged by 0.001 must reload in the same place.
                    else sb.Append(_number.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JsonKind.String:
                    WriteString(sb, _string);
                    break;
                case JsonKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < _array.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        _array[i].Write(sb);
                    }
                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    sb.Append('{');
                    for (int i = 0; i < _members.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteString(sb, _members[i].Key);
                        sb.Append(':');
                        _members[i].Value.Write(sb);
                    }
                    sb.Append('}');
                    break;
            }
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // --- Parsing --------------------------------------------------------------------------------

        /// <summary>Throws <see cref="FormatException"/> on malformed input.</summary>
        public static Json Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            int at = 0;
            SkipWhitespace(text, ref at);
            Json value = ParseValue(text, ref at);
            SkipWhitespace(text, ref at);
            if (at != text.Length) throw Malformed(text, at, "trailing content after the document");
            return value;
        }

        /// <summary>Returns null instead of throwing, for input that may not be JSON at all.</summary>
        public static Json TryParse(string text)
        {
            try
            {
                return text == null ? null : Parse(text);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static Json ParseValue(string s, ref int at)
        {
            if (at >= s.Length) throw Malformed(s, at, "a value");

            switch (s[at])
            {
                case '{': return ParseObject(s, ref at);
                case '[': return ParseArray(s, ref at);
                case '"': return new Json(ParseString(s, ref at));
                case 't': Expect(s, ref at, "true"); return Of(true);
                case 'f': Expect(s, ref at, "false"); return Of(false);
                case 'n': Expect(s, ref at, "null"); return Null;
                default: return new Json(ParseNumber(s, ref at));
            }
        }

        private static Json ParseObject(string s, ref int at)
        {
            var result = Object();
            at++; // '{'
            SkipWhitespace(s, ref at);
            if (at < s.Length && s[at] == '}') { at++; return result; }

            while (true)
            {
                SkipWhitespace(s, ref at);
                if (at >= s.Length || s[at] != '"') throw Malformed(s, at, "a member name");
                string name = ParseString(s, ref at);

                SkipWhitespace(s, ref at);
                if (at >= s.Length || s[at] != ':') throw Malformed(s, at, "':'");
                at++;

                SkipWhitespace(s, ref at);
                result.Set(name, ParseValue(s, ref at));

                SkipWhitespace(s, ref at);
                if (at >= s.Length) throw Malformed(s, at, "',' or '}'");
                if (s[at] == ',') { at++; continue; }
                if (s[at] == '}') { at++; return result; }
                throw Malformed(s, at, "',' or '}'");
            }
        }

        private static Json ParseArray(string s, ref int at)
        {
            var result = Array();
            at++; // '['
            SkipWhitespace(s, ref at);
            if (at < s.Length && s[at] == ']') { at++; return result; }

            while (true)
            {
                SkipWhitespace(s, ref at);
                result.Add(ParseValue(s, ref at));

                SkipWhitespace(s, ref at);
                if (at >= s.Length) throw Malformed(s, at, "',' or ']'");
                if (s[at] == ',') { at++; continue; }
                if (s[at] == ']') { at++; return result; }
                throw Malformed(s, at, "',' or ']'");
            }
        }

        private static string ParseString(string s, ref int at)
        {
            at++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (at >= s.Length) throw Malformed(s, at, "a closing quote");
                char c = s[at++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                if (at >= s.Length) throw Malformed(s, at, "an escape sequence");
                char escape = s[at++];
                switch (escape)
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
                        if (at + 4 > s.Length) throw Malformed(s, at, "four hex digits");
                        int code;
                        if (!int.TryParse(s.Substring(at, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out code))
                            throw Malformed(s, at, "four hex digits");
                        at += 4;
                        // Surrogate pairs come through as two \u escapes and are appended as-is: the two
                        // chars recombine into one code point in the resulting UTF-16 string.
                        sb.Append((char)code);
                        break;
                    default:
                        throw Malformed(s, at - 1, "a valid escape sequence");
                }
            }
        }

        private static double ParseNumber(string s, ref int at)
        {
            int start = at;
            if (at < s.Length && (s[at] == '-' || s[at] == '+')) at++;
            while (at < s.Length && (char.IsDigit(s[at]) || s[at] == '.' || s[at] == 'e' || s[at] == 'E' ||
                                     ((s[at] == '-' || s[at] == '+') && (s[at - 1] == 'e' || s[at - 1] == 'E'))))
                at++;

            double value;
            if (start == at || !double.TryParse(s.Substring(start, at - start), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value))
                throw Malformed(s, start, "a number");
            return value;
        }

        private static void Expect(string s, ref int at, string literal)
        {
            if (at + literal.Length > s.Length ||
                string.CompareOrdinal(s, at, literal, 0, literal.Length) != 0)
                throw Malformed(s, at, "'" + literal + "'");
            at += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int at)
        {
            while (at < s.Length && (s[at] == ' ' || s[at] == '\t' || s[at] == '\r' || s[at] == '\n')) at++;
        }

        private static FormatException Malformed(string s, int at, string expected)
        {
            int from = Math.Max(0, at - 20);
            int length = Math.Min(40, s.Length - from);
            return new FormatException(string.Format(CultureInfo.InvariantCulture,
                "Malformed JSON at offset {0}: expected {1}. Near: {2}",
                at, expected, s.Substring(from, length)));
        }
    }
}
