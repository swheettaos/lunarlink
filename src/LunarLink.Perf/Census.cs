using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace LunarLink
{
    // /census: every component (MonoBehaviour) on an active object counted by its class, and which of Update / LateUpdate /
    // FixedUpdate the class (or a parent) has - Unity calls those on every one of them every frame. Read-only.
    // Uses only the IL2CPP runtime's raw class calls on the object pointers: the first version looked each type up through
    // Il2Cpp reflection objects and crashed the game (2026-11-06 19:37).
    static class Census
    {
        static readonly string[] Methods = { "Update", "LateUpdate", "FixedUpdate" };

        public static string Json(int top)
        {
            Pump.Trace("census: finding every MonoBehaviour");
            var all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            int total = all.Length;
            Pump.Trace("census: " + total + " found; reading their classes");
            var count = new Dictionary<IntPtr, int>();
            for (int i = 0; i < total; i++)
            {
                if (i % 200 == 0) Pump.Trace("census: object " + i + " of " + total);
                var b = all[i]; if (b == null) continue;
                IntPtr obj = b.Pointer; if (obj == IntPtr.Zero) continue;
                IntPtr k = IL2CPP.il2cpp_object_get_class(obj); if (k == IntPtr.Zero) continue;
                count[k] = (count.TryGetValue(k, out var c) ? c : 0) + 1;
            }
            Pump.Trace("census: " + count.Count + " classes; naming them");
            var keys = new List<IntPtr>(count.Keys); keys.Sort((x, y) => count[y].CompareTo(count[x]));
            var sb = new StringBuilder("{\"ok\":true,\"components\":").Append(total).Append(",\"classes\":").Append(count.Count).Append(",\"top\":[");
            for (int i = 0; i < keys.Count && i < top; i++)
            {
                IntPtr k = keys[i];
                string ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(k)) ?? "";
                string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(k)) ?? "?";
                Pump.Trace("census: class " + ns + "." + name);
                string has = "";
                foreach (var m in Methods)
                {
                    for (IntPtr p = k; p != IntPtr.Zero; p = IL2CPP.il2cpp_class_get_parent(p))
                    {
                        string pn = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(p)) ?? "";
                        if (pn == "MonoBehaviour" || pn == "Behaviour") break;
                        if (IL2CPP.il2cpp_class_get_method_from_name(p, m, 0) != IntPtr.Zero) { has += (has == "" ? "" : ",") + m; break; }
                    }
                }
                if (i > 0) sb.Append(',');
                sb.Append("{\"type\":").Append(J.S(ns == "" ? name : ns + "." + name)).Append(",\"n\":").Append(count[k]).Append(",\"calls\":").Append(J.S(has)).Append('}');
            }
            Pump.Trace("census: done");
            return sb.Append("]}").ToString();
        }
    }
}
