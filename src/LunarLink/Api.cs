// The route table every LunarLink module adds its endpoints to, and the helpers they share. A module registers its
// routes in Start(); the core drops every route of a module's copy when that copy unloads, so a module never has to.
//
// The URL pattern: GET /thing lists it; POST /thing/check asks the game without changing anything; POST /thing/create,
// /thing/modify and /thing/remove change it. Every write is POST and needs the allow-writes file. Actions that don't
// fit (POST /message, /clean, /builder/aim...) and read-only reports (GET /production, /terrain...) sit beside them.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using MoonCleaners;

namespace LunarLink
{
    static class Api
    {
        internal sealed class Route
        {
            public string Method, Path, Help, Module, Area; public bool Write; public Func<Req, string> Run;
        }

        static readonly Dictionary<string, Route> routes = new Dictionary<string, Route>(StringComparer.Ordinal);

        // A read: GET, answered as is.
        internal static void Get(string path, string help, Func<Req, string> run) => Add("GET", path, help, false, run);
        // A change: POST, refused without allow-writes.
        internal static void Post(string path, string help, Func<Req, string> run) => Add("POST", path, help, true, run);
        // POST because it carries a body, but it changes nothing (the /check endpoints): no allow-writes needed.
        internal static void Ask(string path, string help, Func<Req, string> run) => Add("POST", path, help, false, run);

        static void Add(string method, string path, string help, bool write, Func<Req, string> run)
        {
            var module = run.Method.DeclaringType?.Assembly.GetName().Name ?? "?";
            // the area: the outermost class the handler sits in, without "Area"/"Module" (WorldArea -> World)
            var t = run.Method.DeclaringType; while (t?.DeclaringType != null) t = t.DeclaringType;
            var area = module == Core ? Core : t == null ? module : t.Name.EndsWith("Area") ? t.Name[..^4] : t.Name.EndsWith("Module") ? t.Name[..^6] : t.Name;
            if (routes.TryGetValue(path, out var had) && had.Module != module)
                Plugin.L.LogWarning("api: " + path + " moves from " + had.Module + " to " + module);
            routes[path] = new Route { Method = method, Path = path, Help = help, Module = module, Area = area, Write = write, Run = run };
        }

        // Every route whose code lives in this assembly (a hot copy being unloaded). Returns how many went.
        internal static int RemoveFrom(Assembly asm)
        {
            var gone = routes.Values.Where(r => r.Run.Method.DeclaringType?.Assembly == asm).Select(r => r.Path).ToList();
            foreach (var p in gone) routes.Remove(p);
            return gone.Count;
        }

        // The route for this request, run; null when no module has the path.
        internal static string Dispatch(Req req, string path)
        {
            if (!routes.TryGetValue(path, out var r)) return null;
            if (req.Method != r.Method) return "{\"error\":" + J.S("use " + r.Method + " for " + path) + "}";
            if (r.Write) { var no = Writes(); if (no != null) return no; }
            return r.Run(req);
        }

        internal static IEnumerable<Route> All => routes.Values.OrderBy(r => r.Path);

        // the core's own routes (registered by LunarLink.dll itself), which answer before a world is loaded
        internal static readonly string Core = typeof(Api).Assembly.GetName().Name;
        internal static bool IsCore(string path) => routes.TryGetValue(path, out var r) && r.Module == Core;

        internal static string Unknown(string path) => "{\"error\":" + J.S("unknown path " + path) + ",\"paths\":["
            + string.Join(",", All.Select(r => J.S(r.Method + " " + r.Path))) + "]}";

        // ---- shared by the modules ----

        internal static MoonWorld World => Pump.World();
        internal static MoonWalker Player => MoonRunOver.LocalWalker;
        // where LunarLink keeps its files (settings, requests.log, backups): BepInEx/plugins/LunarLink/
        internal static string DataDir => System.IO.Path.Combine(BepInEx.Paths.PluginPath, "LunarLink");

        // Writes stay off unless BepInEx/plugins/LunarLink/allow-writes exists; checked on every write.
        internal static string Writes() =>
            System.IO.File.Exists(System.IO.Path.Combine(DataDir, "allow-writes")) ? null : "{\"error\":\"writes disabled (no allow-writes file)\"}";

        internal static float Qf(Req req, string k, float dflt) =>
            float.TryParse(req.Query[k], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : dflt;
        internal static int Qi(Req req, string k, int dflt) =>
            int.TryParse(req.Query[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : dflt;
        internal static List<int> Ids(Req req, string k)
        {
            var ids = new List<int>();
            foreach (var t in (req.Query[k] ?? "").Split(',')) if (int.TryParse(t, out var i)) ids.Add(i);
            return ids;
        }

        internal static string Result(bool ok, string reason, string extra = "") =>
            "{\"ok\":" + (ok ? "true" : "false") + ",\"reason\":" + J.S(reason) + extra + "}";

        internal const string NoPlayer = "{\"error\":\"no local player\"}";
    }
}
