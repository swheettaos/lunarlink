// J: the JSON helpers every endpoint builds its answer with: escaped strings, numbers (4 decimals, null when not finite)
// and vectors as text, and JsonOut, a writer that puts the commas, brackets and quotes in itself.
using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace LunarLink
{
    static class J
    {
        public static string F(double v) => double.IsFinite(v) ? Math.Round(v, 4).ToString(CultureInfo.InvariantCulture) : "null";
        public static string V(Vector3 v) => "[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]";
        public static string S(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (var ch in s)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }
    }

    // Writes JSON as it goes, with the commas, brackets and quotes put in for you:
    //   var o = new JsonOut().Obj().Num("id", 3).Str("name", n).Arr("items");
    //   foreach (...) o.Obj().Num("kg", kg).End();
    //   return o.End().End().ToString();
    // Obj/Arr open an object or array (named inside an object, unnamed in an array or at the top); End closes the last
    // one opened. Inside an array the values take no name: Str(null, x), or the ones without a name below.
    sealed class JsonOut
    {
        readonly StringBuilder sb = new StringBuilder();
        // each open object or array: its closing bracket, and whether anything is in it yet
        readonly System.Collections.Generic.List<(char close, bool any)> open = new System.Collections.Generic.List<(char, bool)>();

        JsonOut Key(string name)
        {
            if (open.Count > 0)
            {
                var top = open[open.Count - 1];
                if (top.any) sb.Append(',');
                open[open.Count - 1] = (top.close, true);
            }
            if (name != null) sb.Append(J.S(name)).Append(':');
            return this;
        }

        public JsonOut Obj(string name = null) { Key(name).sb.Append('{'); open.Add(('}', false)); return this; }
        public JsonOut Arr(string name = null) { Key(name).sb.Append('['); open.Add((']', false)); return this; }
        public JsonOut End() { sb.Append(open[open.Count - 1].close); open.RemoveAt(open.Count - 1); return this; }

        public JsonOut Str(string name, string value) { Key(name).sb.Append(J.S(value)); return this; }
        public JsonOut Num(string name, double value) { Key(name).sb.Append(J.F(value)); return this; }
        public JsonOut Num(string name, long value) { Key(name).sb.Append(value); return this; }
        public JsonOut Bool(string name, bool value) { Key(name).sb.Append(value ? "true" : "false"); return this; }
        public JsonOut Vec(string name, Vector3 value) { Key(name).sb.Append(J.V(value)); return this; }
        // a short list of numbers on one line: [x, y, z], a quaternion
        public JsonOut Nums(string name,
            params double[] values) { Key(name).sb.Append('[').Append(string.Join(",",
            System.Array.ConvertAll(values, J.F))).Append(']'); return this; }
        public JsonOut Null(string name) { Key(name).sb.Append("null"); return this; }
        // already-made JSON (another endpoint's answer, or a helper's)
        public JsonOut Raw(string name, string json) { Key(name).sb.Append(json); return this; }

        // array items
        public JsonOut Str(string value) => Str(null, value);
        public JsonOut Num(double value) => Num(null, value);
        public JsonOut Num(long value) => Num(null, value);
        public JsonOut Vec(Vector3 value) => Vec(null, value);
        public JsonOut Nums(params double[] values) => Nums(null, values);

        // everything still open is closed; an endpoint can return the writer itself
        public override string ToString() { while (open.Count > 0) End(); return sb.ToString(); }
        public static implicit operator string(JsonOut o) => o.ToString();
    }
}
