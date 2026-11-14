// A small JSON reader, so the screensaver needs no libraries beyond what Windows ships.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LaunchCountdownSaver
{
    /// <summary>A parsed JSON value. Missing members and wrong types read as empty rather than throwing.</summary>
    internal sealed class J
    {
        public static readonly J Null = new J(null);
        private readonly object v;
        private J(object value) { v = value; }

        public static J Parse(string text)
        {
            try { int i = 0; var r = Read(text, ref i); return new J(r); }
            catch { return Null; }
        }

        public bool IsNull { get { return v == null; } }
        public bool IsList { get { return v is List<object>; } }
        public string Str { get { return v as string ?? (v is double ? ((double)v).ToString(CultureInfo.InvariantCulture) : null); } }
        public double? Num
        {
            get
            {
                if (v is double) return (double)v;
                double d;
                if (v is string && double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
                return null;
            }
        }
        public double N(double fallback = 0) { return Num ?? fallback; }
        public bool Bool { get { return v is bool && (bool)v; } }
        public int Count { get { var l = v as List<object>; return l == null ? 0 : l.Count; } }

        public J this[string key]
        {
            get
            {
                var d = v as Dictionary<string, object>; object o;
                return d != null && d.TryGetValue(key, out o) ? new J(o) : Null;
            }
        }
        public J this[int index]
        {
            get { var l = v as List<object>; return l != null && index >= 0 && index < l.Count ? new J(l[index]) : Null; }
        }
        public IEnumerable<J> Items
        {
            get { var l = v as List<object>; if (l != null) foreach (var o in l) yield return new J(o); }
        }

        public DateTime? Time
        {
            get
            {
                DateTime t;
                return Str != null && DateTime.TryParse(Str, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t) ? t : (DateTime?)null;
            }
        }

        private static void Skip(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object Read(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException();
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>(); i++;
                Skip(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Skip(s, ref i);
                    var k = ReadString(s, ref i);
                    Skip(s, ref i);
                    if (s[i++] != ':') throw new FormatException();
                    d[k] = Read(s, ref i);
                    Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException();
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++;
                Skip(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Read(s, ref i));
                    Skip(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException();
                }
            }
            if (c == '"') return ReadString(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string ReadString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException();
            i++;
            var b = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') return b.ToString();
                if (c != '\\') { b.Append(c); continue; }
                c = s[i++];
                switch (c)
                {
                    case 'n': b.Append('\n'); break;
                    case 't': b.Append('\t'); break;
                    case 'r': b.Append('\r'); break;
                    case 'b': b.Append('\b'); break;
                    case 'f': b.Append('\f'); break;
                    case 'u': b.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber)); i += 4; break;
                    default: b.Append(c); break;
                }
            }
        }
    }
}
