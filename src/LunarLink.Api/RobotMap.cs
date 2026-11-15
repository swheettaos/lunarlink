using System;
using System.Reflection;
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
            for (int i = 0; i < centres.Length; i++) { var c = centres[i]; float d = c.x * dir.x + c.y * dir.y + c.z * dir.z;
                if (d > bd) { bd = d; best = i; } }
            return best;
        }

        public static string Robots(MoonWorld w)
        {
            var mi = typeof(MoonWorld).GetMethod("MapRobots", BindingFlags.Public | BindingFlags.Instance);
            if (mi == null) return Api.Result(false, "no MapRobots");
            var listType = mi.GetParameters()[0].ParameterType;               // Il2CppSystem.Collections.Generic.List<T>
            var list = Activator.CreateInstance(listType);
            mi.Invoke(w, new[] { list });
            int count = (int)listType.GetProperty("Count").GetValue(list);
            var item = listType.GetProperty("Item");
            var elem = listType.GetGenericArguments()[0];
            var regions = w.Regions; var centres = regions?.Centers;
            var o = new JsonOut().Obj().Bool("ok", true).Str("type", elem.FullName).Num("count", count).Arr("robots");
            for (int i = 0; i < count; i++)
            {
                object e = item.GetValue(list, new object[] { i });
                o.Obj(); Vector3? pos = null;
                foreach (var p in elem.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0 || p.Name == "Pointer" || p.Name == "ObjectClass" || p.Name == "WasCollected") continue;
                    object v; try { v = p.GetValue(e); } catch { continue; }
                    switch (v)
                    {
                        case Vector3 v3: o.Vec(p.Name, v3); if (pos == null && v3.sqrMagnitude > 100f) pos = v3; break;
                        case Vector2 v2: o.Nums(p.Name, v2.x, v2.y); break;
                        case int iv: o.Num(p.Name, iv); break;
                        case long lv: o.Num(p.Name, lv); break;
                        case float fv: o.Num(p.Name, fv); break;
                        case double dv: o.Num(p.Name, dv); break;
                        case bool bv: o.Bool(p.Name, bv); break;
                        case null: o.Null(p.Name); break;
                        default: o.Str(p.Name, v.ToString()); break;
                    }
                }
                if (pos.HasValue && centres != null)
                {
                    int r = regions.Find(pos.Value.normalized); if (r < 0) r = Nearest(centres, pos.Value);
                    o.Num("region", r).Bool("regionComplete", w.IsRegionComplete(r));
                }
                o.End();
            }
            return o;
        }
    }
}
