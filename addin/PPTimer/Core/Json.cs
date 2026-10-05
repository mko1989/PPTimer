using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PPTimer.Core
{
    /// <summary>
    /// Minimal JSON reader/writer so the core has no dependencies and runs on both
    /// .NET Framework (the add-in) and modern .NET (the Mac dev server).
    /// Objects parse to Dictionary&lt;string, object&gt;, arrays to List&lt;object&gt;, numbers to double.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var parser = new Parser(text ?? "");
            parser.SkipWhitespace();
            var value = parser.ReadValue();
            parser.SkipWhitespace();
            if (!parser.AtEnd) throw new FormatException($"Unexpected trailing characters at {parser.Where(parser.Position)}");
            return value;
        }

        public static string Serialize(object value, bool indent = false)
        {
            var sb = new StringBuilder();
            Write(sb, value, indent, 0);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object value, bool indent, int depth)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case double d:
                    sb.Append(FormatDouble(d));
                    break;
                case float f:
                    sb.Append(FormatDouble(f));
                    break;
                case int _:
                case long _:
                case short _:
                case byte _:
                case uint _:
                case ulong _:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case IDictionary<string, object> dict:
                    WriteObject(sb, dict, indent, depth);
                    break;
                case IEnumerable list:
                    WriteArray(sb, list, indent, depth);
                    break;
                default:
                    WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
            }
        }

        static void WriteObject(StringBuilder sb, IDictionary<string, object> dict, bool indent, int depth)
        {
            if (dict.Count == 0) { sb.Append("{}"); return; }
            sb.Append('{');
            var first = true;
            foreach (var kv in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                NewLine(sb, indent, depth + 1);
                WriteString(sb, kv.Key);
                sb.Append(indent ? ": " : ":");
                Write(sb, kv.Value, indent, depth + 1);
            }
            NewLine(sb, indent, depth);
            sb.Append('}');
        }

        static void WriteArray(StringBuilder sb, IEnumerable list, bool indent, int depth)
        {
            sb.Append('[');
            var first = true;
            foreach (var item in list)
            {
                if (!first) sb.Append(indent ? ", " : ",");
                first = false;
                Write(sb, item, false, depth + 1);
            }
            sb.Append(']');
        }

        static void NewLine(StringBuilder sb, bool indent, int depth)
        {
            if (!indent) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        static string FormatDouble(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            if (Math.Abs(d) < 1e15 && d == Math.Floor(d)) return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
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
                        if (c < 0x20 || c == '<' || c == '>') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        sealed class Parser
        {
            readonly string s;
            int i;

            public Parser(string text) { s = text; }

            public bool AtEnd => i >= s.Length;

            public int Position => i;

            /// <summary>"line 3, column 28" (1-based), so a hand-edited config.json error points at the mistake.</summary>
            public string Where(int pos)
            {
                int line = 1, col = 1;
                for (var k = 0; k < pos && k < s.Length; k++)
                {
                    if (s[k] == '\n') { line++; col = 1; }
                    else col++;
                }
                return $"line {line}, column {col}";
            }

            public void SkipWhitespace()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            public object ReadValue()
            {
                if (AtEnd) throw new FormatException("Unexpected end of JSON");
                var c = s[i];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw new FormatException($"Unexpected character '{c}' at {Where(i)}");
                }
            }

            Dictionary<string, object> ReadObject()
            {
                var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                i++; // {
                SkipWhitespace();
                if (Peek() == '}') { i++; return result; }
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"') throw new FormatException($"Expected property name (in double quotes) at {Where(i)}");
                    var key = ReadString();
                    SkipWhitespace();
                    if (Peek() != ':') throw new FormatException($"Expected ':' at {Where(i)}");
                    i++;
                    SkipWhitespace();
                    result[key] = ReadValue();
                    SkipWhitespace();
                    var c = Peek();
                    i++;
                    if (c == '}') return result;
                    if (c != ',') throw new FormatException($"Expected ',' or '}}' at {Where(i - 1)}");
                }
            }

            List<object> ReadArray()
            {
                var result = new List<object>();
                i++; // [
                SkipWhitespace();
                if (Peek() == ']') { i++; return result; }
                while (true)
                {
                    SkipWhitespace();
                    result.Add(ReadValue());
                    SkipWhitespace();
                    var c = Peek();
                    i++;
                    if (c == ']') return result;
                    if (c != ',') throw new FormatException($"Expected ',' or ']' at {Where(i - 1)}");
                }
            }

            string ReadString()
            {
                var sb = new StringBuilder();
                i++; // opening quote
                while (true)
                {
                    if (AtEnd) throw new FormatException("Unterminated string");
                    var c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (AtEnd) throw new FormatException("Unterminated escape");
                    var e = s[i++];
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
                            if (i + 4 > s.Length) throw new FormatException("Bad unicode escape");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: throw new FormatException($"Bad escape '\\{e}'");
                    }
                }
            }

            double ReadNumber()
            {
                var start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException($"Expected '{word}' at {Where(i)}");
                i += word.Length;
            }

            char Peek() => AtEnd ? '\0' : s[i];
        }
    }
}
