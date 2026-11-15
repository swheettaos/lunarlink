using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.Loader;
using HarmonyLib;

namespace LunarLink
{
    // The /hot/* endpoints, plugins' own endpoints (HandleHttp) and their help (HttpHelp), and the list as JSON.
    static partial class Hot
    {
        // /help: each running plugin's HttpHelp() lines ("GET /path?x= | what it does")
        internal static List<(string owner, string[] lines)> Help()
        {
            var list = new List<(string, string[])>();
            foreach (var p in plugins.Values)
            {
                if (!p.Running || p.HelpM == null) continue;
                try { list.Add((p.Name, (string[])p.HelpM.Invoke(p.Obj, null) ?? new string[0])); }
                catch (Exception e) { list.Add((p.Name, new[] { "? | HttpHelp failed: " + (e.InnerException ?? e).Message })); }
            }
            return list;
        }

        // A request no LunarLink route took: offered to each running plugin with HandleHttp; null means "not mine".
        internal static string Http(string method, string path, NameValueCollection query, string body)
        {
            foreach (var p in plugins.Values)
            {
                if (!p.Running || p.HttpM == null) continue;
                string r;
                try { r = (string)p.HttpM.Invoke(p.Obj, new object[] { method, path, query, body }); }
                catch (Exception e) { var x = e.InnerException ?? e; return Api.Error(p.Name + ": " + x.GetType().Name + ": " + x.Message); }
                if (r != null) return r;
            }
            return null;
        }

        // GET /hot/list[?gc=1]; POST /hot/reload?name= | ?all=1, /hot/enable?name=, /hot/disable?name=, /hot/rescan
        // (registered in the route table). The manager window (the LunarLink.Window module) calls the same actions.
        internal static void Routes()
        {
            Api.Get("/hot/list", "?gc=1 : the hot plugins: state, build, newer build on disk, errors, leftovers, old copies " +
                "still in memory", r => List(r.Query["gc"] == "1"));
            Api.Post("/hot/reload",
                "?name= : reload a hot plugin from the build on disk now (and turn it on); a wrapped plugin that loads its own " +
                    "content waits for the main menu while a world is loaded; ?all=1 : every one with a newer build", r =>
                r.Query["all"] == "1" ? ReloadAll() : One(r, p => Enable(p, reload: true)));
            Api.Post("/hot/enable", "?name= : load and start a hot plugin, and remember it's on", r => One(r, p => Enable(p, reload: false)));
            Api.Post("/hot/disable", "?name= : stop and unload a hot plugin, and remember it's off", r => One(r, Disable));
            Api.Post("/hot/rescan", "Pick up DLLs added to or removed from BepInEx/hot/", r =>
            {
                var (added, removed) = RescanAll();
                var o = new JsonOut().Obj().Bool("ok", true).Arr("added");
                foreach (var a in added) o.Str(a);
                o.End().Arr("removed");
                foreach (var d in removed) o.Str(d);
                return o;
            });
        }

        // ?all=1: every plugin with a newer build, reloaded
        static string ReloadAll()
        {
            var ms = System.Diagnostics.Stopwatch.StartNew(); var done = ReloadChanged();
            var o = new JsonOut().Obj().Bool("ok", true).Num("ms", ms.ElapsedMilliseconds).Arr("reloaded");
            foreach (var p in done) Entry(o, p, false);
            return o;
        }

        static string One(Req r, Func<string, Plugin> act)
        {
            var name = r.Query["name"]; if (string.IsNullOrEmpty(name)) return Api.Error("name= is required");
            var ms = System.Diagnostics.Stopwatch.StartNew();
            var pl = act(name);
            if (pl == null) return Api.Error("no " + FileOf(name));
            var o = new JsonOut().Obj().Bool("ok", pl.Error == null).Num("ms", ms.ElapsedMilliseconds);
            Entry(o, pl, false, "plugin");
            return o;
        }

        // for the support file, read off the main thread: a copy of the list first
        internal static string ListJson() => List(false);
        internal static List<(string name, string error, int leftovers, bool newer)> Snapshot() =>
            plugins.Values.ToList().Select(p => (p.Name, p.Error, p.Leftovers.Count, p.Obj != null && NewerOnDisk(p))).ToList();

        static string List(bool gc)
        {
            if (gc) { for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }
            var o = new JsonOut().Obj().Str("dir", Dir).Raw("bridges", Bridges.Status()).Arr("plugins");
            foreach (var p in plugins.Values.ToList()) Entry(o, p, true);
            return o;
        }

        // one plugin as /hot/list shows it (full: with its old copies), as an array item or named
        static void Entry(JsonOut o, Plugin p, bool full, string name = null)
        {
            o.Obj(name).Str("name", p.Name).Str("state", StateOf(p)).Str("built", p.Obj != null
                ? p.FileTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : null)
             .Str("error", p.Error).Num("loads", p.Loads);
            if (p.HasBoot) o.Str("boot", p.Booted ? "done" : "restart to boot"); else o.Bool("boot", false);
            o.Bool("newerOnDisk", p.Obj != null && NewerOnDisk(p)).Arr("leftovers");
            foreach (var l in p.Leftovers) o.Str(l);
            o.End().Bool("reloadAtMenu", p.ReloadAtMenu).Bool("wrapped", p.Wrapped || (p.Obj == null && p.WasWrapped)).Arr("warnings");
            foreach (var w in p.Warnings) o.Str(w);
            o.End();
            if (full) o.Num("oldCopiesAlive", p.Old.Count(w => w.IsAlive)).Num("oldCopies", p.Old.Count);
            o.End();
        }
    }
}
