// J: the small JSON helpers every endpoint builds its answer with (numbers, vectors, escaped strings).
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
}
