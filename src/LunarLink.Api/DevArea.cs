using System;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace LunarLink
{
    // Reading the game's code from inside the running game: find methods, cross-references, disassembly, fields; and
    // calling a parameterless game method on purpose.
    static class DevArea
    {
        internal static void Start(string state)
        {
            Api.Get("/dev/find", "?name= : every game method by that name: class, static, parameters, returns, live objects", r => DevFind(r));
            Api.Get("/dev/xref", "?find=text[&type=MoonCleaners.X] : game methods whose code uses a string containing text", r => DevCode.Xref(r.Query["find"] ?? "", r.Query["type"]));
            Api.Get("/dev/dis", "?type=MoonCleaners.X&method=Name[&n=0][&bytes=8192] : disassembly with its strings and called methods named", r => DevCode.Dis(r.Query["type"] ?? "", r.Query["method"] ?? "", Api.Qi(r, "n", 0), Api.Qi(r, "bytes", 8192)));
            Api.Get("/dev/fields", "?type=MoonCleaners.X : a class's fields with their memory offsets", r => DevCode.Fields(r.Query["type"] ?? ""));
            Api.Get("/dev/name", "?addr=hex : which known method contains a code address", r => DevCode.NameAt(r.Query["addr"] ?? ""));
            Api.Post("/dev/call", "?type=&method= : call a parameterless game method (static, or on every live object of the class)", r => DevCall(r));
        }


        // /dev/find?name=DevResetDust : every game method by that name - its class, static or not, its parameters, and
        // how many live objects of that class there are. POST /dev/call?type=<full class name>&method=<name> runs one that
        // takes no parameters (static, or on every live object of the class) and reports what it returned.
        // Added for the game's own developer tools (the dust reset, 2026-11-03); nothing runs unless asked.
        static System.Collections.Generic.IEnumerable<Type> GameTypes()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Assembly-CSharp") continue;
                Type[] ts; try { ts = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException ex) { ts = ex.Types; }
                foreach (var t in ts) if (t != null) yield return t;
            }
        }
        static int LiveCount(Type t)
        {
            try { if (!typeof(UnityEngine.Object).IsAssignableFrom(t)) return -1; return UnityEngine.Object.FindObjectsOfType(Il2CppType.From(t)).Length; } catch { return -2; }
        }
        static string DevFind(Req req)
        {
            var name = req.Query["name"] ?? ""; var sb = new StringBuilder("["); int n = 0;
            foreach (var t in GameTypes())
            {
                System.Reflection.MethodInfo[] ms;
                try { ms = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly); } catch { continue; }
                foreach (var m in ms)
                {
                    if (m.Name != name) continue;
                    var ps = string.Join(",", Array.ConvertAll(m.GetParameters(), q => q.ParameterType.Name));
                    if (n++ > 0) sb.Append(",");
                    sb.Append("{\"type\":" + J.S(t.FullName) + ",\"static\":" + (m.IsStatic ? "true" : "false") + ",\"public\":" + (m.IsPublic ? "true" : "false")
                        + ",\"params\":" + J.S(ps) + ",\"returns\":" + J.S(m.ReturnType.Name) + ",\"live\":" + (m.IsStatic ? -1 : LiveCount(t)) + "}");
                }
            }
            return sb.Append("]").ToString();
        }
        static string DevCall(Req req)
        {
            var tn = req.Query["type"] ?? ""; var mn = req.Query["method"] ?? "";
            Type t = null; foreach (var x in GameTypes()) if (x.FullName == tn) { t = x; break; }
            if (t == null) return "{\"ok\":false,\"reason\":\"no such class\"}";
            var m = t.GetMethod(mn, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (m == null) return "{\"ok\":false,\"reason\":\"no such method without parameters\"}";
            try
            {
                if (m.IsStatic) { var r = m.Invoke(null, null); return "{\"ok\":true,\"static\":true,\"returned\":" + J.S(r == null ? null : r.ToString()) + "}"; }
                var objs = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(t)); var sb = new StringBuilder("["); int n = 0;
                for (int i = 0; i < objs.Length; i++)
                {
                    var inst = Activator.CreateInstance(t, new object[] { objs[i].Pointer });
                    var r = m.Invoke(inst, null); if (n++ > 0) sb.Append(","); sb.Append(J.S(r == null ? "null" : r.ToString()));
                }
                return "{\"ok\":true,\"objects\":" + objs.Length + ",\"returned\":" + sb.Append("]") + "}";
            }
            catch (Exception ex) { var e = ex.InnerException ?? ex; return "{\"ok\":false,\"reason\":" + J.S(e.GetType().Name + ": " + e.Message) + "}"; }
        }

    }
}
