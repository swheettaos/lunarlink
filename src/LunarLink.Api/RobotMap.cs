using System;
using System.Reflection;
using System.Text;
using MoonCleaners;
using UnityEngine;

namespace LunarLink
{
    // The game's own robot map (MoonWorld.MapRobots, what the map screen draws): each entry's fields, plus the region
    // under it and whether that region is complete (2026-11-08: put robots on clean ground onto spots that aren't clean).
    static class RobotMap
    {
        // nearest region centre to a direction (brute force over all centres: one-off, about 160k dot products)
        static int Nearest(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3> centres, Vector3 dir)
        {
            dir.Normalize(); int best = -1; float bd = -2f;
            for (int i = 0; i < centres.Length; i++) { var c = centres[i]; float d = c.x * dir.x + c.y * dir.y + c.z * dir.z; if (d > bd) { bd = d; best = i; } }
            return best;
        }

        public static string Robots(MoonWorld w)
        {
            var mi = typeof(MoonWorld).GetMethod("MapRobots", BindingFlags.Public | BindingFlags.Instance);
            if (mi == null) return "{\"ok\":false,\"reason\":\"no MapRobots\"}";
            var listType = mi.GetParameters()[0].ParameterType;               // Il2CppSystem.Collections.Generic.List<T>
            var list = Activator.CreateInstance(listType);
            mi.Invoke(w, new[] { list });
            int count = (int)listType.GetProperty("Count").GetValue(list);
            var item = listType.GetProperty("Item");
            var elem = listType.GetGenericArguments()[0];
            var regions = w.Regions; var centres = regions?.Centers;
            var sb = new StringBuilder("{\"ok\":true,\"type\":").Append(J.S(elem.FullName)).Append(",\"count\":").Append(count).Append(",\"robots\":[");
            for (int i = 0; i < count; i++)
            {
                object e = item.GetValue(list, new object[] { i });
                if (i > 0) sb.Append(',');
                sb.Append('{'); bool first = true; Vector3? pos = null;
                foreach (var p in elem.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0 || p.Name == "Pointer" || p.Name == "ObjectClass" || p.Name == "WasCollected") continue;
                    object v; try { v = p.GetValue(e); } catch { continue; }
                    string js = v switch
                    {
                        Vector3 v3 => J.V(v3), Vector2 v2 => "[" + J.F(v2.x) + "," + J.F(v2.y) + "]",
                        int iv => iv.ToString(), long lv => lv.ToString(), float fv => J.F(fv), double dv => J.F(dv), bool bv => bv ? "true" : "false",
                        string sv => J.S(sv), null => "null", _ => J.S(v.ToString()),
                    };
                    if (v is Vector3 pv && pos == null && pv.sqrMagnitude > 100f) pos = pv;
                    if (!first) sb.Append(','); first = false;
                    sb.Append(J.S(p.Name)).Append(':').Append(js);
                }
                if (pos.HasValue && centres != null)
                {
                    int r = regions.Find(pos.Value.normalized); if (r < 0) r = Nearest(centres, pos.Value);
                    sb.Append(first ? "" : ",").Append("\"region\":").Append(r).Append(",\"regionComplete\":").Append(w.IsRegionComplete(r) ? "true" : "false");
                }
                sb.Append('}');
            }
            return sb.Append("]}").ToString();
        }
    }
}
